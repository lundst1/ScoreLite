namespace ScoreLite.Core;

/// <summary>Ett element ur <c>layoutReady.items</c> (IF-5): not (<c>n..</c>) eller takt (<c>m..</c>) med geometri.</summary>
public readonly record struct LayoutItem(string Id, int Measure, double X, int Sys);

/// <summary>En post i pekarens tidslinje: <c>[playMs, x, sys]</c> i <c>setTimeline.entries</c>.</summary>
public readonly record struct CursorEntry(double PlayMs, double X, int Sys)
{
    public double[] ToArray() => new[] { PlayMs, X, Sys };
}

/// <summary>AL-11. Bygger pekarens tidslinje från notbildens geometri, spelordningen och tempokartan.</summary>
public static class CursorTimelineBuilder
{
    public static IReadOnlyList<CursorEntry> Build(Score score, IReadOnlyList<LayoutItem> items, int sampleRate, List<Diagnostic>? diagnostics = null)
    {
        var geometry = new Dictionary<string, LayoutItem>(items.Count, StringComparer.Ordinal);
        foreach (var it in items) geometry[it.Id] = it;

        // Alla stämmors noter, oberoende av stämmaval (regel 3), per takt och tickposition i takten.
        int[] starts = score.Measures.Select(m => m.StartScoreTick).ToArray();
        var byMeasure = new SortedDictionary<int, List<Note>>[score.Measures.Count];
        foreach (var n in score.Notes)
        {
            int mi = Array.BinarySearch(starts, n.ScoreTick);
            if (mi < 0) mi = ~mi - 1;
            if (mi < 0 || mi >= starts.Length) continue;
            (byMeasure[mi] ??= new SortedDictionary<int, List<Note>>())
                .TryAdd(n.ScoreTick - starts[mi], new List<Note>());
            byMeasure[mi][n.ScoreTick - starts[mi]].Add(n);
        }

        var entries = new List<CursorEntry>();
        int missing = 0;
        CursorEntry? last = null;

        void Add(long playTick, LayoutItem g)
        {
            double ms = score.Tempo.PlayTickToFrame(playTick, sampleRate) * 1000.0 / sampleRate;
            var e = new CursorEntry(ms, g.X, g.Sys);
            entries.Add(e);
            last = e;
        }

        foreach (var seg in score.Order.Segments)
        {
            var ticks = byMeasure[seg.MeasureIndex];
            bool haveMeasureGeo = geometry.TryGetValue($"m{seg.MeasureIndex}", out var mg);
            bool startCovered = false;

            if (ticks != null)
                foreach (var (tickInMeasure, notes) in ticks)
                {
                    // Minsta x bland noterna vid tickpositionen (regel 2).
                    LayoutItem? best = null;
                    foreach (var n in notes)
                    {
                        if (geometry.TryGetValue(n.Id, out var g)) { if (best == null || g.X < best.Value.X) best = g; }
                        else missing++;
                    }
                    if (best == null) continue;
                    if (tickInMeasure == 0) startCovered = true;
                    Add(seg.PlayStartTick + tickInMeasure, best.Value);
                }

            if (!startCovered)
            {
                // Takten börjar med paus eller saknar geometri för notstarten: pekaren står på taktens plats.
                if (haveMeasureGeo) { InsertAtStart(entries, seg, mg, score, sampleRate); last = entries.Count > 0 ? entries[^1] : null; }
                else if (last != null)
                {
                    missing++;
                    InsertAtStart(entries, seg, new LayoutItem("", seg.MeasureIndex, last.Value.X, last.Value.Sys), score, sampleRate);
                }
                else missing++;
            }
        }

        if (missing > 0)
            diagnostics?.Add(new Diagnostic(DiagSeverity.Info, "CURSOR_MISSING_GEOMETRY", $"{missing} poster saknade geometri i notbilden."));

        // Sortera på playMs (stabilt): upprepade segment ger upprepade poster med senare playMs.
        return entries.Select((e, i) => (e, i)).OrderBy(x => x.e.PlayMs).ThenBy(x => x.i).Select(x => x.e).ToList();
    }

    // Lägger in segmentstarten sist i listan; slutsorteringen ordnar den på rätt plats.
    private static void InsertAtStart(List<CursorEntry> entries, PlaySegment seg, LayoutItem g, Score score, int sampleRate)
    {
        double ms = score.Tempo.PlayTickToFrame(seg.PlayStartTick, sampleRate) * 1000.0 / sampleRate;
        entries.Add(new CursorEntry(ms, g.X, g.Sys));
    }
}
