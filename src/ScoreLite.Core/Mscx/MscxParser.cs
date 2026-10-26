using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace ScoreLite.Core;

/// <summary>Resultat från parsern, före bygget av spelordning och tempokarta.</summary>
public sealed class ParsedScore
{
    public string Title { get; init; } = "";
    public string FormatVersion { get; init; } = "";
    public required IReadOnlyList<Part> Parts { get; init; }
    public required IReadOnlyList<Measure> Measures { get; init; }
    public required IReadOnlyList<MeasureMarks> Marks { get; init; }
    public required IReadOnlyList<Note> Notes { get; init; }
    public required IReadOnlyList<NotatedItem> Items { get; init; }
    public required IReadOnlyList<DynamicEvent> Dynamics { get; init; }
    public required IReadOnlyList<TempoEvent> TempoEvents { get; init; }
}

/// <summary>
/// AL-01 steg 3–8. Läser <c>.mscx</c> strömmande på XmlReader-nivå och bygger en takt i taget (XElement)
/// så att minnet hålls lågt. Tolerant: okända element hoppas över. Stöd: 3.01 och senare fullt ut,
/// 2.06 på bästa sätt (DR-28). Kända luckor: instrumentbyte i stycket, trumsetdefinitioner (V5),
/// taktupprepning (V14), partiella bindebågar (V14).
/// </summary>
public sealed class MscxParser
{
    private readonly LoadLimits _limits;
    private readonly List<Diagnostic> _diag;

    // Del-info
    private sealed class PartInfo
    {
        public int Index;
        public string Name = "", ShortName = "", InstrumentId = "";
        public int Program, Transpose;
        public bool IsPercussion;
        public List<(int staffId, StaffKind kind, ClefKind clef)> Staves = new();
    }

    private readonly List<PartInfo> _parts = new();
    private readonly Dictionary<int, (int part, int staffInPart)> _staffMap = new();

    // Taktdata byggs av första notsystemsblocket
    private readonly List<Measure> _measures = new();
    private readonly List<MeasureMarks> _marks = new();
    private readonly List<NoteBuilder> _notes = new();
    private readonly List<DynamicEvent> _dynamics = new();
    private readonly Dictionary<int, double> _tempo = new();
    private TimeSig _currentSig = TimeSig.Common;
    private int _currentKey;
    private int _unknownCount;
    private int _roundingCount;
    private int _voiceMismatch;
    private readonly List<int> _mismatchMeasures = new();
    private int _voice1Total;
    private bool _firstStaffBlockDone;

    private sealed class NoteBuilder
    {
        public int PartIndex, StaffIndex, Voice, Tick, Dur, Pitch, Tpc = Note.NoTpc, Tpc2 = Note.NoTpc;
        public bool TieStart;
        public string Id = "";
    }

    private sealed class ItemBuilder
    {
        public ItemKind Kind;
        public int PartIndex, StaffIndex, Voice, Measure, Tick, Dur, Dots;
        public string DurationType = "quarter";
        public List<TupletInst> Tuplets = new();
        public List<NoteBuilder> Notes = new();
    }

    private readonly List<ItemBuilder> _items = new();
    private readonly Dictionary<string, TupletInst> _legacyInst = new();
    private int _tupletCounter;
    /// <summary>Lokal taktartsstretch i aktuellt notsystem (längd i notsystemet * stretch = global längd). 1 = ingen.</summary>
    private Frac _stretch = Frac.Of(1, 1);

    public MscxParser(LoadLimits? limits, List<Diagnostic> diagnostics)
    {
        _limits = limits ?? LoadLimits.Default;
        _diag = diagnostics;
    }

    public static ParsedScore Parse(Stream mscx, List<Diagnostic> diagnostics, LoadLimits? limits = null) =>
        new MscxParser(limits, diagnostics).Run(mscx);

    private ParsedScore Run(Stream mscx)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,   // S3
            XmlResolver = null,
            IgnoreWhitespace = true,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };
        string version = "";
        string title = "";
        try
        {
            using var reader = XmlReader.Create(mscx, settings);
            reader.MoveToContent();
            if (reader.NodeType != XmlNodeType.Element || reader.Name != "museScore")
                throw new ScoreLoadException("NOT_A_SCORE", "Filen verkar inte vara ett MuseScore-partitur.");
            version = reader.GetAttribute("version") ?? "";
            CheckVersion(version);

            // Gå ner till <Score>
            if (!reader.IsEmptyElement) reader.Read();
            while (!reader.EOF && !(reader.NodeType == XmlNodeType.Element && reader.Name == "Score"))
            {
                if (reader.NodeType == XmlNodeType.Element) reader.Skip(); else reader.Read();
            }
            if (reader.EOF) throw new ScoreLoadException("EMPTY_SCORE", "Partituret innehåller inga noter.");

            int scoreDepth = reader.Depth;
            if (reader.IsEmptyElement) throw new ScoreLoadException("EMPTY_SCORE", "Partituret innehåller inga noter.");
            reader.Read();
            while (!reader.EOF && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == scoreDepth))
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Depth == scoreDepth + 1)
                {
                    switch (reader.Name)
                    {
                        case "Part":
                            if (_parts.Count >= _limits.MaxParts)
                                throw new ScoreLoadException("LIMIT_EXCEEDED", "Partituret är för stort för att öppnas i appen.");
                            ReadPart((XElement)XNode.ReadFrom(reader));
                            break;
                        case "Staff":
                            ReadStaffBlock(reader);
                            break;
                        case "metaTag":
                            var meta = (XElement)XNode.ReadFrom(reader);
                            if ((string?)meta.Attribute("name") == "workTitle" && title.Length == 0) title = meta.Value.Trim();
                            break;
                        default:
                            _unknownCount++;
                            reader.Skip();
                            break;
                    }
                }
                else reader.Read();
            }
        }
        catch (XmlException e)
        {
            if (_measures.Count == 0)
                throw new ScoreLoadException("NOT_A_SCORE", "Filen verkar inte vara ett MuseScore-partitur.", e);
            _diag.Add(new Diagnostic(DiagSeverity.Warning, "TRUNCATED_XML", "Filen slutar oväntat. Det som hann läsas används.", e.Message));
        }

        if (_parts.Count == 0 || _measures.Count == 0)
            throw new ScoreLoadException("EMPTY_SCORE", "Partituret innehåller inga noter.");

        if (_unknownCount > 0)
            _diag.Add(new Diagnostic(DiagSeverity.Info, "UNKNOWN_ELEMENTS", $"{_unknownCount} okända element hoppades över."));
        if (_voiceMismatch > 0)
            _diag.Add(new Diagnostic(DiagSeverity.Info, "VOICE_LENGTH_MISMATCH",
                $"Röst 1 i första stämman fyller inte takten i {_voiceMismatch} av {_voice1Total} takter (takt {string.Join(", ", _mismatchMeasures)})."));
        if (_roundingCount > 0)
            _diag.Add(new Diagnostic(DiagSeverity.Warning, "TICK_ROUNDING", $"{_roundingCount} noter fick avrundad tickposition (tuplets)."));

        return Finish(title, version);
    }

    private void CheckVersion(string version)
    {
        if (!double.TryParse(version, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
        {
            _diag.Add(new Diagnostic(DiagSeverity.Warning, "UNSUPPORTED_VERSION", "Filversionen kunde inte läsas. Vissa delar kan saknas.", version));
            return;
        }
        if (v < 3.0)
            _diag.Add(new Diagnostic(DiagSeverity.Warning, "UNSUPPORTED_VERSION", "Filformat före 3.0 läses på bästa sätt. Vissa delar kan saknas.", version));
        else if (v > 5.0)
            _diag.Add(new Diagnostic(DiagSeverity.Warning, "UNSUPPORTED_VERSION", "Filversionen är nyare än 5.00. Vissa delar kan saknas.", version));
    }

    // ---------------- Part ----------------

    private void ReadPart(XElement part)
    {
        var info = new PartInfo { Index = _parts.Count };
        var inst = part.Element("Instrument");

        string longName = inst?.Element("longName")?.Value.Trim() ?? "";
        string partTrack = part.Element("trackName")?.Value.Trim() ?? "";
        string instTrack = inst?.Element("trackName")?.Value.Trim() ?? "";
        info.Name = longName.Length > 0 ? longName : partTrack.Length > 0 ? partTrack : instTrack.Length > 0 ? instTrack : $"Stämma {info.Index + 1}";
        info.ShortName = inst?.Element("shortName")?.Value.Trim() ?? "";

        // Äldre filer saknar ofta Instrument id (A11): instrumentId, sedan trackName.
        info.InstrumentId = (string?)inst?.Attribute("id")
                            ?? inst?.Element("instrumentId")?.Value.Trim()
                            ?? (instTrack.Length > 0 ? instTrack : partTrack);

        // Första <Channel> gäller (DR-30).
        var channel = inst?.Elements("Channel").FirstOrDefault();
        info.Program = (int?)channel?.Element("program")?.Attribute("value") ?? 0;
        info.Transpose = (int?)inst?.Element("transposeChromatic") ?? 0;
        bool useDrumset = (string?)inst?.Element("useDrumset") is "1" or "true";

        var clefs = inst?.Elements("clef").ToList() ?? new List<XElement>();
        int order = 0;
        foreach (var st in part.Elements("Staff"))
        {
            int id = (int?)st.Attribute("id") ?? (_staffMap.Count + 1);
            string group = (string?)st.Element("StaffType")?.Attribute("group") ?? "pitched";
            var kind = group switch { "percussion" => StaffKind.Percussion, "tablature" => StaffKind.Tab, _ => StaffKind.Standard };
            if (kind == StaffKind.Percussion) useDrumset = true;
            var clef = kind switch
            {
                StaffKind.Percussion => ClefKind.Percussion,
                StaffKind.Tab => ClefKind.Tab,
                _ => ClefFromText(clefs.FirstOrDefault(c => ((int?)c.Attribute("staff") ?? 1) == order + 1)?.Value ?? clefs.FirstOrDefault(c => c.Attribute("staff") == null)?.Value),
            };
            info.Staves.Add((id, kind, clef));
            _staffMap[id] = (info.Index, order);
            order++;
        }
        if (info.Staves.Count == 0)
        {
            // Defensiv reserv: en del tomma Part-element har inga Staff-definitioner.
            int id = _staffMap.Count + 1;
            info.Staves.Add((id, StaffKind.Standard, ClefKind.Treble));
            _staffMap[id] = (info.Index, 0);
        }
        info.IsPercussion = useDrumset;
        _parts.Add(info);
    }

    private static ClefKind ClefFromText(string? t) => (t ?? "G").Trim() switch
    {
        "F" or "F8vb" or "F15mb" or "F3" or "F_8VA" => ClefKind.Bass,
        "C3" => ClefKind.Alto,
        "C4" => ClefKind.Tenor,
        "PERC" or "PERC2" => ClefKind.Percussion,
        "TAB" or "TAB2" => ClefKind.Tab,
        "G" or "G8vb" or "G8va" or "G15mb" or "G15ma" or "G1" => ClefKind.Treble,
        _ => ClefKind.Treble,
    };

    // ---------------- Notsystemsblock ----------------

    private void ReadStaffBlock(XmlReader reader)
    {
        int staffId = int.TryParse(reader.GetAttribute("id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) ? id : 1;
        bool isFirst = !_firstStaffBlockDone;
        int depth = reader.Depth;
        if (reader.IsEmptyElement) { reader.Skip(); _firstStaffBlockDone = true; return; }
        reader.Read();

        _stretch = Frac.Of(1, 1);
        _legacyInst.Clear();
        int measureIdx = 0;
        var tupletDefs = new Dictionary<string, (long normal, long actual, string? parent)>();
        var voltaStarts = new Dictionary<string, (int start, IReadOnlyList<int> endings)>();

        while (!reader.EOF && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Depth == depth + 1 && reader.Name == "Measure")
            {
                if (isFirst && _measures.Count >= _limits.MaxMeasures)
                    throw new ScoreLoadException("LIMIT_EXCEEDED", "Partituret är för stort för att öppnas i appen.");
                var me = (XElement)XNode.ReadFrom(reader);
                ReadMeasure(me, measureIdx, staffId, isFirst, tupletDefs, voltaStarts);
                measureIdx++;
            }
            else if (reader.NodeType == XmlNodeType.Element) { reader.Skip(); }
            else reader.Read();
        }
        if (reader.NodeType == XmlNodeType.EndElement) reader.Read();
        _firstStaffBlockDone = true;
    }

    private static Frac DurationOfType(string? type) => (type ?? "quarter").Trim() switch
    {
        "long" => Frac.Of(4, 1),
        "breve" => Frac.Of(2, 1),
        "whole" => Frac.Of(1, 1),
        "half" => Frac.Of(1, 2),
        "quarter" => Frac.Of(1, 4),
        "eighth" => Frac.Of(1, 8),
        "16th" => Frac.Of(1, 16),
        "32nd" => Frac.Of(1, 32),
        "64th" => Frac.Of(1, 64),
        "128th" => Frac.Of(1, 128),
        "256th" => Frac.Of(1, 256),
        "512th" => Frac.Of(1, 512),
        "1024th" => Frac.Of(1, 1024),
        _ => Frac.Of(1, 4),
    };

    private static Frac WithDots(Frac baseDur, int dots)
    {
        // d * (2 - 2^-dots) = d * (2^(dots+1) - 1) / 2^dots
        dots = Math.Clamp(dots, 0, 6);
        long den = 1L << dots, num = (1L << (dots + 1)) - 1;
        return baseDur * Frac.Of(num, den);
    }

    private static bool IsGrace(XElement chord) =>
        chord.Elements().Any(e => e.Name.LocalName is "acciaccatura" or "appoggiatura" || e.Name.LocalName.StartsWith("grace", StringComparison.Ordinal));

    private static Frac ParseDurationElement(XElement? d, Frac fallback)
    {
        if (d == null) return fallback;
        if (Frac.TryParse(d.Value, out var f) && f.Num > 0) return f;
        // 2.x: <duration z="4" n="4"/>
        if (long.TryParse((string?)d.Attribute("z"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long z) &&
            long.TryParse((string?)d.Attribute("n"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long nn) && nn != 0)
            return Frac.Of(z, nn);
        return fallback;
    }

    private void ReadMeasure(XElement me, int mIdx, int staffId, bool isFirst,
        Dictionary<string, (long normal, long actual, string? parent)> tupletDefs,
        Dictionary<string, (int start, IReadOnlyList<int> endings)> voltaStarts)
    {
        if (!_staffMap.TryGetValue(staffId, out var map))
        {
            // Notsystem utan Part-definition: koppla till sista stämman.
            int pi = Math.Max(0, _parts.Count - 1);
            map = (pi, _staffMap.Count(kv => kv.Value.part == pi));
            _staffMap[staffId] = map;
        }

        // 3.x och senare har <voice>-element. 2.x har innehållet direkt i <Measure> (en röst).
        var voices = me.Elements("voice").ToList();
        bool legacy = voices.Count == 0;
        var marksVoices = legacy ? new List<XElement> { me } : voices;
        if (legacy) voices = SplitLegacyMeasure(me);

        // Taktart före längd: TimeSig i någon röst gäller från början av den här takten.
        // Lokal taktart med stretch (stretchN/stretchD, t.ex. 18/16 med 3/2): längderna i notsystemet skalas
        // med stretchD/stretchN så att takten blir lika lång som den globala.
        foreach (var v in marksVoices)
        {
            var ts = v.Element("TimeSig");
            if (ts == null) continue;
            if (!int.TryParse(ts.Element("sigN")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int sn) ||
                !int.TryParse(ts.Element("sigD")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int sd) || sn <= 0 || sd <= 0)
                continue;
            long stN = (long?)ts.Element("stretchN") ?? 1, stD = (long?)ts.Element("stretchD") ?? 1;
            if (stN <= 0 || stD <= 0) { stN = 1; stD = 1; }
            _stretch = Frac.Of(stD, stN);
            if (isFirst)
            {
                var global = Frac.Of(sn, sd) * _stretch;
                _currentSig = _stretch == Frac.Of(1, 1) || global.Num is <= 0 or > 64 || global.Den is <= 0 or > 64
                    ? new TimeSig(sn, sd)
                    : new TimeSig((int)global.Num, (int)global.Den);
            }
        }

        // Tonart (klingande): <concertKey> (4.x) före <accidental> (2.x/3.x). [VERIFIERA mot 4.1+ filer, V14]
        if (isFirst)
            foreach (var v in marksVoices)
            {
                var ks = v.Element("KeySig");
                if (ks == null) continue;
                var txt = ks.Element("concertKey")?.Value ?? ks.Element("accidental")?.Value;
                if (int.TryParse(txt, NumberStyles.Integer, CultureInfo.InvariantCulture, out int fifths) && fifths is >= -7 and <= 7)
                    _currentKey = fifths;
            }

        Frac actualLen = _currentSig.Length;
        var lenAttr = (string?)me.Attribute("len");
        if (lenAttr != null && Frac.TryParse(lenAttr, out var lf) && lf.Num > 0) actualLen = lf;

        int measureStart;
        if (isFirst)
        {
            measureStart = _measures.Count == 0 ? 0 : _measures[^1].StartScoreTick + _measures[^1].ActualLengthTicks;
            _measures.Add(new Measure(mIdx, measureStart, actualLen.ToTicks(), _currentSig, new KeySig(_currentKey)));
            _marks.Add(ReadMarks(me, marksVoices, mIdx, voltaStarts));
        }
        else
        {
            if (mIdx >= _measures.Count) return;   // fler takter än i första notsystemet ignoreras
            measureStart = _measures[mIdx].StartScoreTick;
        }

        // Röster
        int voiceNo = 0;
        foreach (var voice in voices)
        {
            voiceNo++;
            ReadVoice(voice, voiceNo, mIdx, measureStart, actualLen, map.part, map.staffInPart, isFirst, tupletDefs);
        }
    }

    /// <summary>
    /// 2.x har inga &lt;voice&gt;-element: innehållet ligger direkt i &lt;Measure&gt;, rösten anges med &lt;track&gt; (notsystem*4 + röst)
    /// och &lt;tick&gt; är en absolut tickposition för nästa not. Övriga element hängs på nästa Chord/Rest.
    /// </summary>
    private static List<XElement> SplitLegacyMeasure(XElement me)
    {
        var byVoice = new SortedDictionary<int, XElement>();
        XElement Voice(int key) => byVoice.TryGetValue(key, out var v) ? v : byVoice[key] = new XElement("voice");
        var pending = new List<XElement>();
        foreach (var e in me.Elements())
        {
            string name = e.Name.LocalName;
            if (name is "Chord" or "Rest")
            {
                int key = Math.Abs((int?)e.Element("track") ?? 0) % 4;
                var v = Voice(key);
                foreach (var pe in pending) v.Add(new XElement(pe));
                pending.Clear();
                v.Add(new XElement(e));
            }
            else if (name is "tick" or "Tuplet" or "Dynamic" or "Tempo" or "TimeSig" or "KeySig" or "Clef")
                pending.Add(e);
        }
        if (pending.Count > 0) { var v0 = Voice(0); foreach (var pe in pending) v0.Add(new XElement(pe)); }
        if (byVoice.Count == 0) byVoice[0] = new XElement("voice");
        return byVoice.Values.ToList();
    }

    private MeasureMarks ReadMarks(XElement me, List<XElement> voices, int mIdx,
        Dictionary<string, (int start, IReadOnlyList<int> endings)> voltaStarts)
    {
        bool startRepeat = me.Element("startRepeat") != null;
        int endRepeat = 0;
        var er = me.Element("endRepeat");
        if (er != null) endRepeat = int.TryParse(er.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int c) && c > 0 ? c : 2;

        VoltaInfo? volta = null;
        var jumps = new List<JumpInfo>();
        var markers = new List<string>();

        foreach (var v in voices)
        {
            foreach (var e in v.Elements())
            {
                switch (e.Name.LocalName)
                {
                    case "Jump":
                        jumps.Add(new JumpInfo(
                            e.Element("jumpTo")?.Value.Trim() ?? "start",
                            e.Element("playUntil")?.Value.Trim() ?? "end",
                            e.Element("continueAt")?.Value.Trim() ?? "",
                            e.Element("playRepeats")?.Value.Trim() is "1" or "true"));
                        break;
                    case "Marker":
                        string label = e.Element("label")?.Value.Trim() ?? "";
                        if (label.Length == 0) label = e.Element("subtype")?.Value.Trim() ?? "";
                        if (label.Length > 0) markers.Add(label);
                        break;
                    case "Spanner" when (string?)e.Attribute("type") == "Volta":
                        var vo = e.Element("Volta");
                        var endings = ParseEndings(vo?.Element("endings")?.Value);
                        int len = (int?)e.Element("next")?.Element("location")?.Element("measures") ?? 1;
                        if (endings.Count > 0 && volta == null) volta = new VoltaInfo(endings, Math.Max(1, len));
                        break;
                    case "Volta":
                        // 2.x: <Volta id> med <endings>; slut markeras av <endSpanner id> (samma eller senare takt).
                        string id = (string?)e.Attribute("id") ?? "";
                        voltaStarts[id] = (mIdx, ParseEndings(e.Element("endings")?.Value));
                        break;
                }
            }
        }

        // 2.x: avsluta voltor vars endSpanner ligger i den här takten.
        foreach (var v in voices)
            foreach (var es in v.Elements("endSpanner"))
            {
                string id = (string?)es.Attribute("id") ?? "";
                if (voltaStarts.TryGetValue(id, out var vs) && vs.endings.Count > 0)
                {
                    if (vs.start == mIdx) volta ??= new VoltaInfo(vs.endings, 1);
                    else if (vs.start < _marks.Count)
                    {
                        var old = _marks[vs.start];
                        _marks[vs.start] = old with { Volta = new VoltaInfo(vs.endings, mIdx - vs.start + 1) };
                    }
                    voltaStarts.Remove(id);
                }
            }

        return new MeasureMarks(startRepeat, endRepeat, volta, jumps, markers);
    }

    private static List<int> ParseEndings(string? text)
    {
        var list = new List<int>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        foreach (var part in text.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(part.Trim().TrimEnd('.'), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) list.Add(n);
        return list;
    }

    // ---------------- Röst ----------------

    private void ReadVoice(XElement voice, int voiceNo, int mIdx, int measureStart, Frac measureLen,
        int partIdx, int staffInPart, bool isFirst,
        Dictionary<string, (long normal, long actual, string? parent)> tupletDefs)
    {
        Frac pos = Frac.Zero;
        Frac contentEnd = Frac.Zero;   // längst nådda slut för Chord/Rest (location kan flytta markören bakåt)
        var tupletStack = new List<TupletInst>();
        int measureTicks = measureLen.ToTicks();

        foreach (var e in voice.Elements())
        {
            switch (e.Name.LocalName)
            {
                case "location":
                    if (Frac.TryParse(e.Element("fractions")?.Value, out var move)) pos += move;
                    break;

                case "tick":   // 2.x: absolut tickposition (A: hittat i adeste.mscx)
                    if (int.TryParse(e.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int absTick))
                        pos = Frac.FromTicks(absTick - measureStart);
                    break;

                case "Tuplet":
                {
                    var tid = (string?)e.Attribute("id");
                    long normal = (long?)e.Element("normalNotes") ?? 1, actual = (long?)e.Element("actualNotes") ?? 1;
                    if (actual <= 0) actual = 1;
                    if (tid != null)
                    {
                        // 2.x: definition med id. Överordnad tuplet anges som <Tuplet>id</Tuplet>.
                        tupletDefs[tid] = (normal, actual, e.Element("Tuplet")?.Value.Trim());
                        _legacyInst[tid] = new TupletInst(++_tupletCounter, (int)Math.Min(normal, 1000), (int)Math.Min(actual, 1000));
                    }
                    else tupletStack.Add(new TupletInst(++_tupletCounter, (int)Math.Min(normal, 1000), (int)Math.Min(actual, 1000)));   // 3.x: start, slut med <endTuplet/>
                    break;
                }
                case "endTuplet":
                    if (tupletStack.Count > 0) tupletStack.RemoveAt(tupletStack.Count - 1);
                    break;

                case "Chord":
                case "Rest":
                {
                    bool isRest = e.Name.LocalName == "Rest";
                    bool grace = !isRest && IsGrace(e);
                    string? dtype = e.Element("durationType")?.Value;
                    int dots = (int?)e.Element("dots") ?? 0;

                    Frac dur;
                    if (dtype == "measure") dur = ParseDurationElement(e.Element("duration"), measureLen);
                    else dur = WithDots(DurationOfType(dtype), dots);

                    // Tuplet-skalning: 3.x-stack och 2.x-referens (<Tuplet>id</Tuplet> i Chord/Rest).
                    Frac ratio = Frac.Of(1, 1);
                    var tuplets = new List<TupletInst>(tupletStack);
                    foreach (var t in tupletStack) ratio = ratio * Frac.Of(t.Normal, t.Actual);
                    var refId = e.Element("Tuplet")?.Value.Trim();
                    int guard = 0;
                    var legacyChain = new List<TupletInst>();
                    while (!string.IsNullOrEmpty(refId) && tupletDefs.TryGetValue(refId, out var def) && guard++ < 8)
                    {
                        ratio = ratio * Frac.Of(def.normal, def.actual);
                        if (_legacyInst.TryGetValue(refId, out var li)) legacyChain.Insert(0, li);   // yttersta först
                        refId = def.parent;
                    }
                    tuplets.AddRange(legacyChain);
                    if (dtype != "measure") dur = dur * ratio * _stretch;

                    if (!grace)
                    {
                        int startTick = (pos).ToTicks();
                        int endTick = (pos + dur).ToTicks();
                        if (!pos.IsExactTicks || !(pos + dur).IsExactTicks) _roundingCount++;
                        var item = new ItemBuilder
                        {
                            Kind = !isRest ? ItemKind.Chord : dtype == "measure" ? ItemKind.MeasureRest : ItemKind.Rest,
                            PartIndex = partIdx, StaffIndex = staffInPart, Voice = voiceNo, Measure = mIdx,
                            Tick = measureStart + startTick, Dur = Math.Max(1, endTick - startTick),
                            DurationType = (dtype ?? "quarter").Trim(), Dots = Math.Clamp(dots, 0, 6), Tuplets = tuplets,
                        };
                        if (!isRest) EmitChordNotes(e, item);
                        if (_items.Count < _limits.MaxNotes) _items.Add(item);
                    }
                    if (!grace) { pos += dur; if (pos > contentEnd) contentEnd = pos; }
                    break;
                }

                case "Dynamic":
                {
                    int? vel = (int?)e.Element("velocity");
                    if (vel == null) vel = DynamicVelocity.FromName(e.Element("subtype")?.Value.Trim());
                    if (vel != null)
                        _dynamics.Add(new DynamicEvent($"p{partIdx}", staffInPart, measureStart + pos.ToTicks(), Math.Clamp(vel.Value, 1, 127)));
                    break;
                }

                case "Tempo":
                {
                    if (double.TryParse(e.Element("tempo")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double qps) && qps > 0)
                        _tempo.TryAdd(measureStart + pos.ToTicks(), qps);
                    break;
                }

                // Hanteras av ReadMarks eller är utan betydelse för ljud och tid:
                case "TimeSig": case "KeySig": case "Clef": case "Jump": case "Marker": case "Spanner":
                case "Volta": case "endSpanner": case "StaffText": case "Beam": case "BarLine":
                case "LayoutBreak": case "RehearsalMark": case "Harmony": case "Fermata": case "Text":
                    break;

                default:
                    // Mycket i en takt är layout. Räkna bara, hoppa över (P5).
                    _unknownCount++;
                    break;
            }
        }

        // Kvalitetskontroll av tickräkningen: röst 1 ska fylla takten exakt (MuseScore fyller med pauser).
        if (isFirst && voiceNo == 1 && staffInPart == 0 && partIdx == 0)
        {
            _voice1Total++;
            if (contentEnd != measureLen) { _voiceMismatch++; if (_mismatchMeasures.Count < 8) _mismatchMeasures.Add(mIdx + 1); }
        }
    }

    private void EmitChordNotes(XElement chord, ItemBuilder item)
    {
        int partIdx = item.PartIndex, staffInPart = item.StaffIndex, voiceNo = item.Voice, tick = item.Tick, dur = item.Dur;
        foreach (var n in chord.Elements("Note"))
        {
            if (!int.TryParse(n.Element("pitch")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pitch)) continue;
            if (_notes.Count >= _limits.MaxNotes)
                throw new ScoreLoadException("LIMIT_EXCEEDED", "Partituret är för stort för att öppnas i appen.");

            // Bindebåge framåt: 3.x <Spanner type="Tie"> med <next>, 2.x <Tie id>.
            bool tieStart = n.Elements("Spanner").Any(s => (string?)s.Attribute("type") == "Tie" && s.Element("next") != null)
                            || n.Elements("Tie").Any();

            var nb = new NoteBuilder
            {
                PartIndex = partIdx, StaffIndex = staffInPart, Voice = voiceNo,
                Tick = tick, Dur = dur, Pitch = Math.Clamp(pitch, 0, 127), TieStart = tieStart,
                Tpc = (int?)n.Element("tpc") ?? Note.NoTpc, Tpc2 = (int?)n.Element("tpc2") ?? Note.NoTpc,
            };
            _notes.Add(nb);
            item.Notes.Add(nb);
        }
    }

    // ---------------- Slutbygge ----------------

    private ParsedScore Finish(string title, string version)
    {
        var parts = _parts.Select(p => new Part(
            Id: $"p{p.Index}",
            Name: p.Name,
            ShortName: p.ShortName,
            Staves: p.Staves.Select((s, i) => new Staff(i, s.clef, s.kind)).ToList(),
            Instruments: new[]
            {
                new InstrumentSpan(0, p.InstrumentId, p.IsPercussion ? 0 : p.Program, p.IsPercussion ? 128 : 0,
                    p.IsPercussion, p.Transpose, null)
            })).ToList();

        // Sortera deterministiskt (P7) och ge stabila id:n (IF-1).
        var sorted = _notes
            .OrderBy(n => n.Tick).ThenBy(n => n.PartIndex).ThenBy(n => n.StaffIndex).ThenBy(n => n.Voice).ThenBy(n => n.Pitch)
            .ToList();
        var notes = new List<Note>(sorted.Count);
        for (int i = 0; i < sorted.Count; i++)
        {
            var b = sorted[i];
            b.Id = $"n{i}";
            notes.Add(new Note(b.Id, $"p{b.PartIndex}", b.StaffIndex, b.Voice, b.Tick, b.Dur, b.Pitch, b.TieStart, b.Tpc, b.Tpc2));
        }

        // Notbildsmodell sorterad på takt, stämma, notsystem, röst, tick. TieEnd: en bunden not med samma höjd slutar här.
        var tiedEnds = new HashSet<(int, int, int, int, int)>();
        foreach (var b in _notes.Where(n => n.TieStart)) tiedEnds.Add((b.PartIndex, b.StaffIndex, b.Voice, b.Pitch, b.Tick + b.Dur));
        var items = _items
            .OrderBy(i => i.Measure).ThenBy(i => i.PartIndex).ThenBy(i => i.StaffIndex).ThenBy(i => i.Voice).ThenBy(i => i.Tick)
            .Select(i => new NotatedItem(i.Kind, i.PartIndex, i.StaffIndex, i.Voice, i.Measure, i.Tick, i.Dur, i.DurationType, i.Dots,
                i.Tuplets,
                i.Notes.OrderBy(n => n.Pitch).Select(n => new NotatedNote(n.Id, n.Pitch, n.Tpc, n.Tpc2, n.TieStart,
                    tiedEnds.Contains((n.PartIndex, n.StaffIndex, n.Voice, n.Pitch, n.Tick)))).ToList()))
            .ToList();

        return new ParsedScore
        {
            Title = title,
            FormatVersion = version,
            Parts = parts,
            Measures = _measures,
            Marks = _marks,
            Notes = notes,
            Items = items,
            Dynamics = _dynamics.OrderBy(d => d.ScoreTick).ToList(),
            TempoEvents = _tempo.OrderBy(kv => kv.Key).Select(kv => new TempoEvent(kv.Key, kv.Value)).ToList(),
        };
    }
}
