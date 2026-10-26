namespace ScoreLite.Core;

public sealed record PlaySegment(int MeasureIndex, long PlayStartTick, int LengthTicks);

/// <summary>Spelordning med repriser, voltor och hopp utvecklade (AL-03).</summary>
public sealed class PlayOrder
{
    public IReadOnlyList<PlaySegment> Segments { get; }
    public long TotalTicks { get; }

    private readonly Dictionary<int, List<int>> _byMeasure = new();

    public PlayOrder(IReadOnlyList<PlaySegment> segments)
    {
        Segments = segments;
        TotalTicks = segments.Count == 0 ? 0 : segments[^1].PlayStartTick + segments[^1].LengthTicks;
        for (int i = 0; i < segments.Count; i++)
        {
            if (!_byMeasure.TryGetValue(segments[i].MeasureIndex, out var l))
                _byMeasure[segments[i].MeasureIndex] = l = new List<int>();
            l.Add(i);
        }
    }

    public static PlayOrder Straight(IReadOnlyList<Measure> measures)
    {
        var segs = new List<PlaySegment>(measures.Count);
        long t = 0;
        foreach (var m in measures) { segs.Add(new PlaySegment(m.Index, t, m.ActualLengthTicks)); t += m.ActualLengthTicks; }
        return new PlayOrder(segs);
    }

    /// <summary>Binärsökning på segmentets start.</summary>
    public ScorePosition ToScorePosition(long playTick)
    {
        int lo = 0, hi = Segments.Count - 1;
        if (hi < 0) return new ScorePosition(0, 0);
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >>> 1;
            if (Segments[mid].PlayStartTick <= playTick) lo = mid; else hi = mid - 1;
        }
        var s = Segments[lo];
        int inMeasure = (int)Math.Clamp(playTick - s.PlayStartTick, 0, Math.Max(0, s.LengthTicks - 1));
        return new ScorePosition(s.MeasureIndex, inMeasure);
    }

    /// <summary>Första förekomsten (DR-10). Utanför partituret: 0.</summary>
    public long FirstPlayTick(ScorePosition position) =>
        _byMeasure.TryGetValue(position.MeasureIndex, out var l)
            ? Segments[l[0]].PlayStartTick + position.TickInMeasure
            : 0;

    public IReadOnlyList<long> AllPlayTicks(ScorePosition position) =>
        _byMeasure.TryGetValue(position.MeasureIndex, out var l)
            ? l.Select(i => Segments[i].PlayStartTick + position.TickInMeasure).ToList()
            : Array.Empty<long>();
}

/// <summary>
/// AL-03. Enklare än MuseScores RepeatList (A14): hanterar start-/slutrepris, enkla voltor, hopp med
/// etiketter (jumpTo/playUntil/continueAt) samt skyddsgräns och fallback. Överlappande och öppna
/// voltor ger PLAYORDER_FALLBACK. Tolkningen av playUntil/continueAt ska bekräftas av T2.
/// </summary>
public static class PlayOrderBuilder
{
    public static PlayOrder Build(IReadOnlyList<Measure> measures, IReadOnlyList<MeasureMarks> marks, List<Diagnostic> diagnostics)
    {
        int n = measures.Count;
        if (n == 0) return new PlayOrder(Array.Empty<PlaySegment>());
        try
        {
            var segs = TryBuild(measures, marks, diagnostics);
            if (segs != null) return new PlayOrder(segs);
        }
        catch (InvalidOperationException e)
        {
            diagnostics.Add(new Diagnostic(DiagSeverity.Warning, "PLAYORDER_FALLBACK", "Spelordningen kunde inte tolkas: " + e.Message));
            return PlayOrder.Straight(measures);
        }
        diagnostics.Add(new Diagnostic(DiagSeverity.Warning, "PLAYORDER_LIMIT", "Spelordningen överskred säkerhetsgränsen och spelas rakt igenom."));
        return PlayOrder.Straight(measures);
    }

    private static List<PlaySegment>? TryBuild(IReadOnlyList<Measure> measures, IReadOnlyList<MeasureMarks> marks, List<Diagnostic> diagnostics)
    {
        int n = measures.Count;
        int limit = 20 * n + 1000;

        // Voltor: mät index -> volta (täcker start..start+längd-1)
        var voltaAt = new Dictionary<int, (VoltaInfo v, int start)>();
        for (int i = 0; i < n; i++)
        {
            var v = marks[i].Volta;
            if (v == null) continue;
            int len = Math.Max(1, v.LengthMeasures);
            for (int k = i; k < Math.Min(n, i + len); k++)
            {
                if (voltaAt.ContainsKey(k))
                {
                    diagnostics.Add(new Diagnostic(DiagSeverity.Warning, "PLAYORDER_FALLBACK", "Överlappande voltor stöds inte i v1.", $"takt {k + 1}"));
                    return PlayOrder.Straight(measures).Segments.ToList();
                }
                voltaAt[k] = (v, i);
            }
        }

        // Markörer: etikett -> första takt
        var markerAt = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < n; i++)
            foreach (var label in marks[i].Markers)
                markerAt.TryAdd(label, i);

        int ResolveLabel(string label)
        {
            if (label == "start") return 0;
            if (label == "end") return n;
            if (markerAt.TryGetValue(label, out int idx)) return idx;
            throw new InvalidOperationException($"markören '{label}' saknas");
        }

        var segs = new List<PlaySegment>();
        long playTick = 0;
        int i2 = 0, repeatStart = 0, pass = 1;
        bool loopedBack = false, jumpActive = false, resetPassAfterVoltas = false;
        string until = "end", continueAt = "";
        var usedJumps = new HashSet<(int, int)>();

        while (i2 < n)
        {
            if (segs.Count > limit) return null;
            var mk = marks[i2];

            if (mk.StartRepeat && !loopedBack && !jumpActive) { repeatStart = i2; pass = 1; }
            loopedBack = false;

            // Genomspelningsräknaren nollställs först när voltagruppen är slut.
            if (resetPassAfterVoltas && !voltaAt.ContainsKey(i2)) { pass = 1; resetPassAfterVoltas = false; }

            // Volta som inte gäller i den här genomspelningen hoppas över.
            if (voltaAt.TryGetValue(i2, out var vt))
            {
                int effectivePass = jumpActive ? 1 : pass;
                if (!vt.v.Endings.Contains(effectivePass))
                {
                    int endOfVolta = vt.start + Math.Max(1, vt.v.LengthMeasures);
                    endOfVolta = Math.Max(i2 + 1, endOfVolta);
                    // En överhoppad volta som bär slutreprisen avslutar repriset: nästa avsnitt utan egen
                    // startrepris börjar efter den (MuseScores beteende, hittat i goldberg.mscz).
                    if (!jumpActive)
                        for (int k = i2; k < Math.Min(n, endOfVolta); k++)
                            if (marks[k].EndRepeatCount > 0) { repeatStart = endOfVolta; resetPassAfterVoltas = true; break; }
                    i2 = endOfVolta;
                    continue;
                }
            }

            segs.Add(new PlaySegment(i2, playTick, measures[i2].ActualLengthTicks));
            playTick += measures[i2].ActualLengthTicks;

            if (jumpActive)
            {
                if (until == "fine" && mk.Markers.Contains("fine")) break;
                if (until == "coda" && mk.Markers.Contains("coda"))
                {
                    int target = ResolveLabel(continueAt);
                    until = "end";
                    if (target >= n) break;
                    i2 = target;
                    continue;
                }
            }

            // Hopp utlöses efter att dess takt spelats; varje hopp används högst en gång.
            bool jumped = false;
            for (int j = 0; j < mk.Jumps.Count; j++)
            {
                if (!usedJumps.Add((i2, j))) continue;
                var jump = mk.Jumps[j];
                int target = ResolveLabel(jump.JumpTo);
                if (target >= n) return segs;
                jumpActive = !jump.PlayRepeats;
                until = string.IsNullOrEmpty(jump.PlayUntil) ? "end" : jump.PlayUntil;
                continueAt = jump.ContinueAt;
                i2 = target;
                jumped = true;
                break;
            }
            if (jumped) continue;

            if (!jumpActive && mk.EndRepeatCount > 0)
            {
                if (pass < mk.EndRepeatCount) { pass++; i2 = repeatStart; loopedBack = true; continue; }
                pass = 1;
                repeatStart = i2 + 1;   // avsnitt utan startrepris börjar efter föregående slutrepris
            }
            i2++;
        }
        return segs;
    }
}
