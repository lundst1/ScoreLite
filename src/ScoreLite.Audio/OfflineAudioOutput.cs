namespace ScoreLite.Audio;

/// <summary>
/// Ljudutgång utan ljudkort för tester och offline-rendering. Anroparen drar block i egen takt med <see cref="Pull"/>,
/// precis som en driver-callback. Samma indata ger bit-identiskt resultat (P7).
/// </summary>
public sealed class OfflineAudioOutput : IAudioOutput
{
    private RingBuffer? _source;
    private long _consumed;

    public OfflineAudioOutput(int sampleRate = 44100, int latencyFrames = 0)
    {
        SampleRate = sampleRate;
        OutputLatencyFrames = latencyFrames;
    }

    public int SampleRate { get; }
    public int OutputLatencyFrames { get; set; }
    public long FramesConsumed => Interlocked.Read(ref _consumed);
    public bool IsStarted => _source != null;
    public int LastEpoch { get; private set; }

    public void Start(RingBuffer source) => _source = source;
    public void Stop() => _source = null;

    /// <summary>Läser <c>dest.Length / 2</c> ramar (stereo). Tystnad om utgången inte är startad.</summary>
    public int Pull(Span<float> dest)
    {
        int frames = dest.Length / 2;
        var src = _source;
        if (src == null) dest.Clear();
        else
        {
            src.Read(dest, out int epoch);
            LastEpoch = epoch;
        }
        Interlocked.Add(ref _consumed, frames);
        return frames;
    }

    public void Dispose() => Stop();
}
