using System.Globalization;
using System.Text;
using System.Xml;

namespace ScoreLite.Core;

/// <summary>
/// DR-04 / IF-1. Exporterar <see cref="Score"/> till MEI för Verovio. <c>xml:id</c> för noter är <see cref="Note.Id"/>
/// och för takter <c>m</c> + MeasureIndex, så att tryck och pekare kan kopplas till modellen utan annat tillstånd (P4).
/// Notbilden visar skriven höjd: klingande höjd minus instrumentets transponering. Stavning ges av tpc
/// (klingande) eller tpc2 (skriven). tpc2 saknas vid oktavtransponering, då används tpc.
/// Kända luckor: klavbyten, tabulatur (ritas som G-notsystem), förslagsnoter, artikulationer.
/// </summary>
public static class MeiExporter
{
    private const string MeiNs = "http://www.music-encoding.org/ns/mei";
    private const string XmlNs = "http://www.w3.org/XML/1998/namespace";
    private const int GapTolerance = 15;   // 1/128-not. Mindre luckor beror på tuplet-avrundning

    public static string Export(Score score)
    {
        var sb = new StringBuilder(1 << 16);
        var settings = new XmlWriterSettings { OmitXmlDeclaration = false, Indent = false, Encoding = new UTF8Encoding(false), CheckCharacters = false };
        using (var w = XmlWriter.Create(sb, settings))
            Write(w, score);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ Stavning

    private static readonly char[] LetterByTpcMod = { 'f', 'c', 'g', 'd', 'a', 'e', 'b' };

    /// <summary>tpc 14 = C. Returnerar tonnamn (a–g) och förtecken i halvtoner (−2..+2).</summary>
    public static (char letter, int accid) SpellTpc(int tpc)
    {
        int idx = ((tpc + 1) % 7 + 7) % 7;
        int accid = (int)Math.Floor((tpc + 1) / 7.0) - 2;
        return (LetterByTpcMod[idx], accid);
    }

    private static readonly int[] LetterPitchClass = { 9, 11, 0, 2, 4, 5, 7 };   // a b c d e f g

    private static int NaturalPitchClass(char letter) => LetterPitchClass[Math.Clamp(letter - 'a', 0, 6)];

    /// <summary>Reservstavning från MIDI-höjd när tpc saknas: kors i kors-tonarter, annars b.</summary>
    public static (char letter, int accid) SpellFromMidi(int midi, int keyFifths)
    {
        int pc = ((midi % 12) + 12) % 12;
        string[] sharps = { "c", "c#", "d", "d#", "e", "f", "f#", "g", "g#", "a", "a#", "b" };
        string[] flats = { "c", "db", "d", "eb", "e", "f", "gb", "g", "ab", "a", "bb", "b" };
        string s = keyFifths < 0 ? flats[pc] : sharps[pc];
        return (s[0], s.Length == 1 ? 0 : s[1] == '#' ? 1 : -1);
    }

    private static string AccidName(int accid) => accid switch
    {
        0 => "n", 1 => "s", 2 => "x", -1 => "f", -2 => "ff", _ => "n",
    };

    // Förtecken som ingår i tonarten per tonnamn (a–g): +1 kors, −1 b.
    private static readonly char[] SharpOrder = { 'f', 'c', 'g', 'd', 'a', 'e', 'b' };
    private static int KeyAccid(char letter, int fifths)
    {
        int idx = Array.IndexOf(SharpOrder, letter);
        if (fifths > 0) return idx < fifths ? 1 : 0;
        if (fifths < 0) return idx >= 7 + fifths ? -1 : 0;
        return 0;
    }

    private static string KeySigAttr(int fifths) => fifths == 0 ? "0" : fifths > 0 ? fifths + "s" : -fifths + "f";

    /// <summary>Skriven tonart för ett transponerande instrument. Tonarten i modellen är klingande.</summary>
    private static int WrittenKey(int concertFifths, int transposeSemitones)
    {
        int shift = (((-transposeSemitones * 7) % 12) + 12) % 12;   // kvintsteg för att gå från klingande till skriven
        if (shift > 6) shift -= 12;
        int k = concertFifths + shift;
        if (k > 7) k -= 12;
        if (k < -7) k += 12;
        return k;
    }

    // ------------------------------------------------------------------ Notvärden

    private static string? DurAttr(string type) => type switch
    {
        "long" => "long", "breve" => "breve", "whole" => "1", "half" => "2", "quarter" => "4", "eighth" => "8",
        "16th" => "16", "32nd" => "32", "64th" => "64", "128th" => "128", "256th" => "256", "512th" => "512", "1024th" => "1024",
        _ => null,
    };

    private static readonly (int ticks, string dur, int dots)[] SpaceValues =
    {
        (7680, "long", 0), (3840, "breve", 0), (1920 + 960 + 480, "1", 2), (1920 + 960, "1", 1), (1920, "1", 0),
        (960 + 480, "2", 1), (960, "2", 0), (480 + 240, "4", 1), (480, "4", 0), (240 + 120, "8", 1), (240, "8", 0),
        (120 + 60, "16", 1), (120, "16", 0), (60 + 30, "32", 1), (60, "32", 0), (30 + 15, "64", 1), (30, "64", 0), (15, "128", 0),
    };

    private sealed record StaffRef(int PartIndex, int StaffIndex, int Number, Part Part, Staff Staff, int Transpose);

    private static void Write(XmlWriter w, Score score)
    {
        // MEI-notsystem numreras löpande över stämmorna.
        var staves = new List<StaffRef>();
        for (int pi = 0; pi < score.Parts.Count; pi++)
        {
            var p = score.Parts[pi];
            int transpose = p.Instruments.Count > 0 ? p.Instruments[0].TransposeSemitones : 0;
            foreach (var st in p.Staves)
                staves.Add(new StaffRef(pi, st.Index, staves.Count + 1, p, st, transpose));
        }
        var staffLookup = staves.ToDictionary(s => (s.PartIndex, s.StaffIndex));

        // Objekt per (takt, notsystem, röst) i tidsordning.
        var itemsByMeasure = score.Items
            .GroupBy(i => i.MeasureIndex)
            .ToDictionary(g => g.Key, g => g.ToList());

        w.WriteStartDocument();
        w.WriteStartElement("mei", MeiNs);
        w.WriteAttributeString("meiversion", "5.0");

        w.WriteStartElement("meiHead");
        w.WriteStartElement("fileDesc");
        w.WriteStartElement("titleStmt");
        w.WriteElementString("title", string.IsNullOrWhiteSpace(score.Title) ? "Untitled" : score.Title);
        w.WriteEndElement();
        w.WriteStartElement("pubStmt"); w.WriteEndElement();
        w.WriteEndElement();
        w.WriteEndElement();

        w.WriteStartElement("music");
        w.WriteStartElement("body");
        w.WriteStartElement("mdiv");
        w.WriteStartElement("score");

        TimeSig? prevSig = null; int? prevKey = null;
        WriteScoreDef(w, score, staves, score.Measures.Count > 0 ? score.Measures[0] : null, full: true);
        if (score.Measures.Count > 0) { prevSig = score.Measures[0].NominalSig; prevKey = score.Measures[0].Key.Fifths; }

        w.WriteStartElement("section");
        for (int mi = 0; mi < score.Measures.Count; mi++)
        {
            var m = score.Measures[mi];
            if (mi > 0 && (m.NominalSig != prevSig || m.Key.Fifths != prevKey))
            {
                WriteScoreDef(w, score, staves, m, full: false, sigChanged: m.NominalSig != prevSig, keyChanged: m.Key.Fifths != prevKey);
                prevSig = m.NominalSig; prevKey = m.Key.Fifths;
            }

            w.WriteStartElement("measure");
            w.WriteAttributeString("id", XmlNs, $"m{m.Index}");
            w.WriteAttributeString("n", (mi + 1).ToString(CultureInfo.InvariantCulture));
            if (m.ActualLengthTicks != m.NominalSig.LengthTicks) w.WriteAttributeString("metcon", "false");

            itemsByMeasure.TryGetValue(mi, out var mItems);
            foreach (var sr in staves)
            {
                w.WriteStartElement("staff");
                w.WriteAttributeString("n", sr.Number.ToString(CultureInfo.InvariantCulture));
                var staffItems = mItems?.Where(i => i.PartIndex == sr.PartIndex && i.StaffIndex == sr.StaffIndex).ToList()
                                 ?? new List<NotatedItem>();
                var voices = staffItems.Select(i => i.Voice).Distinct().OrderBy(v => v).ToList();
                if (voices.Count == 0) voices.Add(1);
                int writtenKey = WrittenKey(m.Key.Fifths, sr.Transpose);
                foreach (int voice in voices)
                {
                    w.WriteStartElement("layer");
                    w.WriteAttributeString("n", voice.ToString(CultureInfo.InvariantCulture));
                    WriteLayer(w, m, sr, staffItems.Where(i => i.Voice == voice).ToList(), writtenKey);
                    w.WriteEndElement();
                }
                w.WriteEndElement();
            }
            w.WriteEndElement();   // measure
        }
        w.WriteEndElement();   // section
        w.WriteEndElement(); w.WriteEndElement(); w.WriteEndElement(); w.WriteEndElement(); w.WriteEndElement();
        w.WriteEndDocument();
    }

    private static void WriteScoreDef(XmlWriter w, Score score, List<StaffRef> staves, Measure? m, bool full,
        bool sigChanged = true, bool keyChanged = true)
    {
        w.WriteStartElement("scoreDef");
        if (m != null)
        {
            if (full || sigChanged)
            {
                w.WriteAttributeString("meter.count", m.NominalSig.Numerator.ToString(CultureInfo.InvariantCulture));
                w.WriteAttributeString("meter.unit", m.NominalSig.Denominator.ToString(CultureInfo.InvariantCulture));
            }
            if (full || keyChanged) w.WriteAttributeString("key.sig", KeySigAttr(m.Key.Fifths));
        }
        if (full)
        {
            w.WriteStartElement("staffGrp");
            w.WriteAttributeString("symbol", "bracket");
            w.WriteAttributeString("bar.thru", "true");
            foreach (var part in score.Parts.Select((p, i) => (p, i)))
            {
                var mine = staves.Where(s => s.PartIndex == part.i).ToList();
                bool brace = mine.Count > 1;
                w.WriteStartElement("staffGrp");
                if (brace) { w.WriteAttributeString("symbol", "brace"); w.WriteAttributeString("bar.thru", "true"); }
                w.WriteAttributeString("id", XmlNs, $"g{part.i}");
                if (!brace) { w.WriteStartElement("label"); w.WriteString(part.p.Name); w.WriteEndElement(); w.WriteStartElement("labelAbbr"); w.WriteString(part.p.ShortName); w.WriteEndElement(); }
                foreach (var sr in mine)
                {
                    w.WriteStartElement("staffDef");
                    w.WriteAttributeString("n", sr.Number.ToString(CultureInfo.InvariantCulture));
                    w.WriteAttributeString("lines", "5");
                    var (shape, line) = sr.Staff.Clef switch
                    {
                        ClefKind.Bass => ("F", "4"), ClefKind.Alto => ("C", "3"), ClefKind.Tenor => ("C", "4"),
                        ClefKind.Percussion => ("perc", "3"), _ => ("G", "2"),
                    };
                    w.WriteAttributeString("clef.shape", shape);
                    w.WriteAttributeString("clef.line", line);
                    if (m != null && sr.Transpose != 0)
                        w.WriteAttributeString("key.sig", KeySigAttr(WrittenKey(m.Key.Fifths, sr.Transpose)));
                    w.WriteEndElement();
                }
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }
        else if (keyChanged && m != null && staves.Any(s => s.Transpose != 0))
        {
            // Transponerande notsystem har egen skriven tonart.
            w.WriteStartElement("staffGrp");
            foreach (var sr in staves.Where(s => s.Transpose != 0))
            {
                w.WriteStartElement("staffDef");
                w.WriteAttributeString("n", sr.Number.ToString(CultureInfo.InvariantCulture));
                w.WriteAttributeString("key.sig", KeySigAttr(WrittenKey(m.Key.Fifths, sr.Transpose)));
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }

    // ------------------------------------------------------------------ Lager

    private sealed class Ctx
    {
        public required XmlWriter W;
        public required StaffRef Staff;
        public required int WrittenKey;
        public required int Fifths;   // klingande tonart, för reservstavning
        public Dictionary<(char, int), int> MeasureAccids = new();
    }

    private static void WriteLayer(XmlWriter w, Measure m, StaffRef sr, List<NotatedItem> items, int writtenKey)
    {
        var ctx = new Ctx { W = w, Staff = sr, WrittenKey = writtenKey, Fifths = m.Key.Fifths };
        int pos = m.StartScoreTick;
        int measureEnd = m.StartScoreTick + m.ActualLengthTicks;

        if (items.Count == 0)
        {
            w.WriteStartElement("mSpace"); w.WriteEndElement();
            return;
        }

        var sequence = new List<NotatedItem>();
        foreach (var it in items)
        {
            if (it.ScoreTick < pos - GapTolerance) continue;   // överlapp (negativ location) kan inte uttryckas i ett lager
            if (it.ScoreTick - pos > GapTolerance)
            {
                // Luckor (location-flytt framåt) blir <space>. Tuplets grupperas inom varje sammanhängande följd.
                EmitSequence(ctx, sequence, 0);
                sequence.Clear();
                WriteSpaces(ctx, it.ScoreTick - pos);
            }
            sequence.Add(it);
            pos = it.ScoreTick + it.DurationTicks;
        }
        EmitSequence(ctx, sequence, 0);
        if (measureEnd - pos > GapTolerance) WriteSpaces(ctx, measureEnd - pos);
    }

    private static void WriteSpaces(Ctx ctx, int ticks)
    {
        foreach (var (t, dur, dots) in SpaceValues)
            while (ticks >= t)
            {
                ctx.W.WriteStartElement("space");
                ctx.W.WriteAttributeString("dur", dur);
                if (dots > 0) ctx.W.WriteAttributeString("dots", dots.ToString(CultureInfo.InvariantCulture));
                ctx.W.WriteEndElement();
                ticks -= t;
            }
    }

    private static void EmitSequence(Ctx ctx, List<NotatedItem> items, int depth)
    {
        for (int i = 0; i < items.Count;)
        {
            var it = items[i];
            if (it.Tuplets.Count > depth)
            {
                int id = it.Tuplets[depth].Id;
                int j = i;
                while (j < items.Count && items[j].Tuplets.Count > depth && items[j].Tuplets[depth].Id == id) j++;
                ctx.W.WriteStartElement("tuplet");
                ctx.W.WriteAttributeString("num", it.Tuplets[depth].Actual.ToString(CultureInfo.InvariantCulture));
                ctx.W.WriteAttributeString("numbase", it.Tuplets[depth].Normal.ToString(CultureInfo.InvariantCulture));
                EmitSequence(ctx, items.GetRange(i, j - i), depth + 1);
                ctx.W.WriteEndElement();
                i = j;
            }
            else
            {
                WriteItem(ctx, it);
                i++;
            }
        }
    }

    private static void WriteItem(Ctx ctx, NotatedItem it)
    {
        var w = ctx.W;
        string? dur = DurAttr(it.DurationType);
        switch (it.Kind)
        {
            case ItemKind.MeasureRest:
                w.WriteStartElement("mRest"); w.WriteEndElement();
                return;
            case ItemKind.Rest:
                w.WriteStartElement("rest");
                w.WriteAttributeString("dur", dur ?? "4");
                if (it.Dots > 0) w.WriteAttributeString("dots", it.Dots.ToString(CultureInfo.InvariantCulture));
                w.WriteEndElement();
                return;
        }

        if (it.Notes.Count == 0) return;
        bool chord = it.Notes.Count > 1;
        if (chord)
        {
            w.WriteStartElement("chord");
            w.WriteAttributeString("dur", dur ?? "4");
            if (it.Dots > 0) w.WriteAttributeString("dots", it.Dots.ToString(CultureInfo.InvariantCulture));
        }
        foreach (var n in it.Notes)
        {
            var (letter, accid, oct) = Written(ctx, n);
            w.WriteStartElement("note");
            w.WriteAttributeString("id", XmlNs, n.NoteId);
            if (!chord)
            {
                w.WriteAttributeString("dur", dur ?? "4");
                if (it.Dots > 0) w.WriteAttributeString("dots", it.Dots.ToString(CultureInfo.InvariantCulture));
            }
            w.WriteAttributeString("pname", letter.ToString());
            w.WriteAttributeString("oct", oct.ToString(CultureInfo.InvariantCulture));

            // Förtecken: skrivs bara när tonarten eller tidigare förtecken i takten inte redan ger höjden.
            int implied = ctx.MeasureAccids.TryGetValue((letter, oct), out int cur) ? cur : KeyAccid(letter, ctx.WrittenKey);
            if (accid != implied)
            {
                w.WriteAttributeString("accid", AccidName(accid));
                ctx.MeasureAccids[(letter, oct)] = accid;
            }
            if (accid != 0) w.WriteAttributeString("accid.ges", AccidName(accid));   // gestural
            if (n.TieStart && n.TieEnd) w.WriteAttributeString("tie", "m");
            else if (n.TieStart) w.WriteAttributeString("tie", "i");
            else if (n.TieEnd) w.WriteAttributeString("tie", "t");
            w.WriteEndElement();
        }
        if (chord) w.WriteEndElement();
    }

    /// <summary>Skriven tonhöjd för en not: (tonnamn, förtecken, oktav).</summary>
    private static (char letter, int accid, int oct) Written(Ctx ctx, NotatedNote n)
    {
        int transpose = ctx.Staff.Transpose;
        int writtenMidi = n.MidiPitch - transpose;
        int tpc = transpose % 12 == 0 ? n.Tpc : n.Tpc2;   // oktavtransponering ändrar inte stavningen
        char letter; int accid;
        if (tpc != Note.NoTpc && tpc is >= -20 and <= 50)
            (letter, accid) = SpellTpc(tpc);
        else
            (letter, accid) = SpellFromMidi(writtenMidi, ctx.WrittenKey);

        // Oktav från den naturliga tonens höjd (Cb4 klingar som B3 men skrivs med oktav 4).
        int natural = writtenMidi - accid;
        if ((((natural - NaturalPitchClass(letter)) % 12) + 12) % 12 != 0)
        {
            // Stavningen stämmer inte med höjden (t.ex. saknad/gammal tpc2): reservstavning.
            (letter, accid) = SpellFromMidi(writtenMidi, ctx.WrittenKey);
            natural = writtenMidi - accid;
        }
        int oct = (int)Math.Floor(natural / 12.0) - 1;
        return (letter, accid, oct);
    }
}
