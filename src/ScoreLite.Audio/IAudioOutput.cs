namespace ScoreLite.Audio;

/// <summary>IF-4. Plattformsutgång (Android/iOS) eller <see cref="OfflineAudioOutput"/>.</summary>
public interface IAudioOutput : IDisposable
{
    int SampleRate { get; }               // 44100 (DR-23)
    int OutputLatencyFrames { get; }      // rapporterad eller uppmätt
    long FramesConsumed { get; }          // monoton. Uppdateras av driver-callbacken
    void Start(RingBuffer source);
    void Stop();
}
