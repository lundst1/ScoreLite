namespace ScoreLite.Core;

public readonly record struct TempoPoint(long PlayTick, double QuarterNotesPerSecond);

/// <summary>
/// Tempokarta uttryckt i PlayTick (AL-02). Enhet: kvartsnoter per sekund (V3). Förvalt 2.0 = 120 BPM.
/// Omvandlingen är monoton och avrundar en gång per tempoavsnitt.
/// </summary>
public sealed class TempoMap
{
    public const double DefaultQuarterNotesPerSecond = 2.0;

    private readonly TempoPoint[] _points;
    private readonly double[] _secondsAtPoint;

    public IReadOnlyList<TempoPoint> Points => _points;

    public TempoMap(IEnumerable<TempoPoint> points)
    {
        var list = points.OrderBy(p => p.PlayTick).ToList();
        // Sista tempot vid samma tick vinner.
        var dedup = new List<TempoPoint>();
        foreach (var p in list)
        {
            if (p.QuarterNotesPerSecond <= 0 || double.IsNaN(p.QuarterNotesPerSecond)) continue;
            if (dedup.Count > 0 && dedup[^1].PlayTick == p.PlayTick) dedup[^1] = p;
            else dedup.Add(p);
        }
        if (dedup.Count == 0 || dedup[0].PlayTick > 0)
            dedup.Insert(0, new TempoPoint(0, DefaultQuarterNotesPerSecond));
        _points = dedup.ToArray();
        _secondsAtPoint = new double[_points.Length];
        for (int i = 1; i < _points.Length; i++)
        {
            long dt = _points[i].PlayTick - _points[i - 1].PlayTick;
            _secondsAtPoint[i] = _secondsAtPoint[i - 1] + dt / (double)Time.TicksPerQuarter / _points[i - 1].QuarterNotesPerSecond;
        }
    }

    public static TempoMap Constant(double qps = DefaultQuarterNotesPerSecond) =>
        new(new[] { new TempoPoint(0, qps) });

    private int IndexAt(long playTick)
    {
        int lo = 0, hi = _points.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >>> 1;
            if (_points[mid].PlayTick <= playTick) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    public double QuarterNotesPerSecondAt(long playTick) => _points[IndexAt(playTick)].QuarterNotesPerSecond;

    public double PlayTickToSeconds(long playTick)
    {
        int i = IndexAt(playTick);
        return _secondsAtPoint[i] + (playTick - _points[i].PlayTick) / (double)Time.TicksPerQuarter / _points[i].QuarterNotesPerSecond;
    }

    public long PlayTickToFrame(long playTick, int sampleRate)
    {
        int i = IndexAt(playTick);
        long atStart = (long)Math.Round(_secondsAtPoint[i] * sampleRate, MidpointRounding.AwayFromZero);
        long within = (long)Math.Round((playTick - _points[i].PlayTick) * (double)sampleRate
                                       / (Time.TicksPerQuarter * _points[i].QuarterNotesPerSecond), MidpointRounding.AwayFromZero);
        return atStart + within;
    }

    public long FrameToPlayTick(long frame, int sampleRate)
    {
        double seconds = frame / (double)sampleRate;
        int lo = 0, hi = _points.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >>> 1;
            if (_secondsAtPoint[mid] <= seconds) lo = mid; else hi = mid - 1;
        }
        double dt = (seconds - _secondsAtPoint[lo]) * _points[lo].QuarterNotesPerSecond * Time.TicksPerQuarter;
        return _points[lo].PlayTick + (long)Math.Round(dt, MidpointRounding.AwayFromZero);
    }

    /// <summary>Bygger tempokartan i PlayTick från tempohändelser i ScoreTick och spelordningen.</summary>
    public static TempoMap Build(PlayOrder order, IReadOnlyList<Measure> measures, IReadOnlyList<TempoEvent> events)
    {
        var points = new List<TempoPoint>();
        if (events.Count == 0) return Constant();
        var sorted = events.OrderBy(e => e.ScoreTick).ToList();
        foreach (var seg in order.Segments)
        {
            var m = measures[seg.MeasureIndex];
            int start = m.StartScoreTick, end = start + m.ActualLengthTicks;
            foreach (var ev in sorted)
            {
                if (ev.ScoreTick < start || ev.ScoreTick >= end) continue;
                points.Add(new TempoPoint(seg.PlayStartTick + (ev.ScoreTick - start), ev.QuarterNotesPerSecond));
            }
        }
        // Tempo före första förekomsten ska gälla från början om inget tempo står på tick 0:
        // ett tempo i takt 0 efter tick 0 påverkar inte starten (förvalt tempo gäller dit).
        return new TempoMap(points);
    }
}
