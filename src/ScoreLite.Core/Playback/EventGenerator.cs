namespace ScoreLite.Core;

/// <summary>V8 (bekräftad): MuseScores standardvelocity per dynamikmärke. Filens egen <c>&lt;velocity&gt;</c> har företräde (DR-32).</summary>
public static class DynamicVelocity
{
    public const int Default = 80;   // mf (DR-32)

    private static readonly Dictionary<string, int> Table = new(StringComparer.Ordinal)
    {
        ["pppp"] = 10, ["ppp"] = 16, ["pp"] = 33, ["p"] = 49, ["mp"] = 64, ["mf"] = 80,
        ["f"] = 96, ["ff"] = 112, ["fff"] = 126, ["ffff"] = 127,
        ["fp"] = 96, ["sf"] = 112, ["sfz"] = 112, ["sff"] = 112, ["sffz"] = 112, ["sfp"] = 112, ["sfpp"] = 112,
        ["rfz"] = 112, ["rf"] = 112, ["fz"] = 112,
    };

    public static int? FromName(string? name) => name != null && Table.TryGetValue(name, out int v) ? v : null;
}

public enum PlaybackEventKind : byte { ProgramChange = 0, Controller = 1, NoteOff = 2, NoteOn = 3 }

/// <summary>ProgramChange: Data1=program, Data2=bank. Controller: Data1=nummer, Data2=värde. Note*: Data1=MIDI-höjd, Data2=velocity.</summary>
public readonly record struct PlaybackEvent(long PlayTick, PlaybackEventKind Kind, string PartId, int Data1, int Data2);

/// <summary>AL-04. Nivå 1: höjd, längd, bindebågar, statisk dynamik, spelordning. Bank är alltid 0 eller 128 (DR-30).</summary>
public static class EventGenerator
{
    public static IReadOnlyList<PlaybackEvent> Generate(Score score, IReadOnlySet<string> selectedPartIds)
    {
        var events = new List<PlaybackEvent>();
        var selected = score.Parts.Where(p => selectedPartIds.Contains(p.Id)).ToList();

        // 6. Programbyte och startvärden vid början av spelordningen.
        foreach (var p in selected)
        {
            var inst = p.Instruments[0];
            events.Add(new PlaybackEvent(0, PlaybackEventKind.ProgramChange, p.Id, inst.Program, inst.IsPercussion ? 128 : 0));
            events.Add(new PlaybackEvent(0, PlaybackEventKind.Controller, p.Id, 7, 100));
            events.Add(new PlaybackEvent(0, PlaybackEventKind.Controller, p.Id, 10, 64));
        }

        int[] measureStarts = score.Measures.Select(m => m.StartScoreTick).ToArray();
        int MeasureOf(int tick)
        {
            int lo = 0, hi = measureStarts.Length - 1;
            while (lo < hi) { int mid = (lo + hi + 1) >>> 1; if (measureStarts[mid] <= tick) lo = mid; else hi = mid - 1; }
            return lo;
        }

        // Förberäkna per takt: noter och dynamik (bara valda stämmor).
        var notesByMeasure = new Dictionary<int, List<Note>>();
        foreach (var n in score.Notes)
        {
            if (!selectedPartIds.Contains(n.PartId)) continue;
            int mi = MeasureOf(n.ScoreTick);
            if (!notesByMeasure.TryGetValue(mi, out var l)) notesByMeasure[mi] = l = new List<Note>();
            l.Add(n);
        }
        var dynByMeasure = new Dictionary<int, List<DynamicEvent>>();
        foreach (var d in score.Dynamics)
        {
            if (!selectedPartIds.Contains(d.PartId)) continue;
            int mi = MeasureOf(d.ScoreTick);
            if (!dynByMeasure.TryGetValue(mi, out var l)) dynByMeasure[mi] = l = new List<DynamicEvent>();
            l.Add(d);
        }

        // Bindebågar: nyckel (part, notsystem, röst, höjd, starttick) -> not
        var byStart = new Dictionary<(string, int, int, int, int), Note>();
        foreach (var n in score.Notes)
            byStart[(n.PartId, n.StaffIndex, n.Voice, n.MidiPitch, n.ScoreTick)] = n;
        Note? NextInTie(Note n) =>
            n.TiedToNext && byStart.TryGetValue((n.PartId, n.StaffIndex, n.Voice, n.MidiPitch, n.ScoreTick + n.DurationTicks), out var nx) ? nx : null;

        var velocity = new Dictionary<(string, int), int>();
        var segs = score.Order.Segments;
        var consumed = new HashSet<(int seg, string noteId)>();

        for (int si = 0; si < segs.Count; si++)
        {
            var seg = segs[si];
            int mi = seg.MeasureIndex;
            int mStart = measureStarts[mi];

            var items = new List<(int tick, int kind, Note? note, DynamicEvent? dyn)>();
            if (dynByMeasure.TryGetValue(mi, out var dl)) foreach (var d in dl) items.Add((d.ScoreTick, 0, null, d));
            if (notesByMeasure.TryGetValue(mi, out var nl)) foreach (var n in nl) items.Add((n.ScoreTick, 1, n, null));
            items.Sort((a, b) => a.tick != b.tick ? a.tick.CompareTo(b.tick) : a.kind.CompareTo(b.kind));

            foreach (var it in items)
            {
                if (it.dyn is { } dv) { velocity[(dv.PartId, dv.StaffIndex)] = dv.Velocity; continue; }
                var note = it.note!;
                if (consumed.Contains((si, note.Id))) continue;

                long on = seg.PlayStartTick + (note.ScoreTick - mStart);
                long end = on + note.DurationTicks;

                // 3. Slå ihop bindebågar bara inom segmentet eller in i omedelbart följande segment.
                Note cur = note; int curSeg = si;
                while (NextInTie(cur) is { } next)
                {
                    int nextMeasure = MeasureOf(next.ScoreTick);
                    int nextSeg;
                    if (nextMeasure == MeasureOf(cur.ScoreTick)) nextSeg = curSeg;
                    else if (curSeg + 1 < segs.Count && segs[curSeg + 1].MeasureIndex == nextMeasure) nextSeg = curSeg + 1;
                    else break;
                    consumed.Add((nextSeg, next.Id));
                    end = segs[nextSeg].PlayStartTick + (next.ScoreTick - measureStarts[nextMeasure]) + next.DurationTicks;
                    cur = next; curSeg = nextSeg;
                }

                int vel = velocity.TryGetValue((note.PartId, note.StaffIndex), out int v) ? v : DynamicVelocity.Default;
                events.Add(new PlaybackEvent(on, PlaybackEventKind.NoteOn, note.PartId, note.MidiPitch, vel));
                events.Add(new PlaybackEvent(end, PlaybackEventKind.NoteOff, note.PartId, note.MidiPitch, 0));
            }
        }

        // 7. Stabil sortering: tick, art, stämma, höjd.
        return events
            .OrderBy(e => e.PlayTick).ThenBy(e => (int)e.Kind).ThenBy(e => e.PartId, StringComparer.Ordinal).ThenBy(e => e.Data1)
            .ToList();
    }
}
