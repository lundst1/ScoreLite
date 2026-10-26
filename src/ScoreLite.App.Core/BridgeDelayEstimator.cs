using System.Diagnostics;

namespace ScoreLite.App;

/// <summary>
/// AL-12 regel 5. Mäter bryggfördröjningen som halva rundturstiden för ping/pong. Medianen av de senaste fem
/// mätningarna används så att en enstaka långsam rundtur inte flyttar pekaren.
/// </summary>
public sealed class BridgeDelayEstimator
{
    private readonly Func<long> _timestamp;
    private readonly double _ticksPerMs;
    private readonly Dictionary<int, long> _outstanding = new();
    private readonly Queue<double> _samples = new();
    private int _nextId;

    public BridgeDelayEstimator(Func<long>? timestamp = null, double? ticksPerMs = null)
    {
        _timestamp = timestamp ?? Stopwatch.GetTimestamp;
        _ticksPerMs = ticksPerMs ?? Stopwatch.Frequency / 1000.0;
    }

    /// <summary>Fördröjning i ms. 0 innan första mätningen.</summary>
    public double DelayMs { get; private set; }

    /// <summary>Returnerar id för nästa ping.</summary>
    public int NextPing()
    {
        int id = ++_nextId;
        _outstanding[id] = _timestamp();
        if (_outstanding.Count > 16) _outstanding.Remove(_outstanding.Keys.Min());
        return id;
    }

    public void OnPong(int id)
    {
        if (!_outstanding.Remove(id, out long sent)) return;
        double rtt = (_timestamp() - sent) / _ticksPerMs;
        _samples.Enqueue(Math.Max(0, rtt / 2));
        while (_samples.Count > 5) _samples.Dequeue();
        var sorted = _samples.OrderBy(x => x).ToArray();
        DelayMs = sorted[sorted.Length / 2];
    }
}
