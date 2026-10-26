// OVERIFIERAD: den här filen kan inte kompileras i utvecklingsmiljön (kräver macOS/Xcode). Skriven mot AVFoundation-API:erna i
// .NET för iOS och ska byggas och provas på en iPhone i M0(b). Se docs/device-checklist.md.
using AudioToolbox;
using AVFoundation;
using Foundation;
using ScoreLite.App.Services;
using ScoreLite.Audio;

namespace ScoreLite.App.Platforms.IosAudio;

/// <summary>
/// IF-4 på iOS: <see cref="AVAudioEngine"/> med en <see cref="AVAudioSourceNode"/> som läser <see cref="RingBuffer"/> i
/// renderanropet (driver-callback, §7.4: bara kopiera ramar och räkna). Interleaved float, stereo, 44100 Hz.
/// </summary>
public sealed unsafe class IosAudioOutput : IAudioOutput
{
    private const int Rate = 44100;
    private AVAudioEngine? _engine;
    private AVAudioSourceNode? _node;
    private RingBuffer? _ring;
    private long _consumed;

    public int SampleRate => Rate;
    public long FramesConsumed => Interlocked.Read(ref _consumed);
    public int OutputLatencyFrames => (int)(AVAudioSession.SharedInstance().OutputLatency * Rate);

    public void Start(RingBuffer source)
    {
        Stop();
        _ring = source;

        var session = AVAudioSession.SharedInstance();
        session.SetCategory(AVAudioSessionCategory.Playback, out NSError? catError);
        if (catError != null) throw new InvalidOperationException(catError.LocalizedDescription);
        session.SetPreferredIOBufferDuration(0.01, out _);
        session.SetActive(true, out NSError? activeError);
        if (activeError != null) throw new InvalidOperationException(activeError.LocalizedDescription);

        var format = new AVAudioFormat(AVAudioCommonFormat.PCMFloat32, Rate, 2, true);
        _node = new AVAudioSourceNode(format, Render);
        _engine = new AVAudioEngine();
        _engine.AttachNode(_node);
        _engine.Connect(_node, _engine.MainMixerNode, format);
        _engine.Prepare();
        if (!_engine.StartAndReturnError(out NSError? startError))
            throw new InvalidOperationException(startError?.LocalizedDescription ?? "AVAudioEngine startade inte");
    }

    private int Render(bool isSilence, AudioToolbox.AudioTimeStamp timestamp, uint frameCount, ref AudioBuffers outputData)
    {
        var ring = _ring;
        var buf = outputData[0];
        int frames = (int)frameCount;
        var dest = new Span<float>((void*)buf.Data, frames * 2);
        if (ring == null) dest.Clear();
        else ring.Read(dest, out _);
        Interlocked.Add(ref _consumed, frames);
        return 0;
    }

    public void Stop()
    {
        _ring = null;
        if (_engine != null)
        {
            _engine.Stop();
            if (_node != null) { _engine.DetachNode(_node); _node.Dispose(); _node = null; }
            _engine.Dispose();
            _engine = null;
        }
        AVAudioSession.SharedInstance().SetActive(false, AVAudioSessionSetActiveOptions.NotifyOthersOnDeactivation, out _);
    }

    public void Dispose() => Stop();
}

/// <summary>R4 på iOS: avbrott (samtal, Siri) och utdragna hörlurar ger <see cref="Interrupted"/>.</summary>
public sealed class IosAudioSession : IAudioInterruptions, IDisposable
{
    private readonly NSObject _interruption, _route;

    public IosAudioSession()
    {
        var center = NSNotificationCenter.DefaultCenter;
        _interruption = center.AddObserver(AVAudioSession.InterruptionNotification, n =>
        {
            var type = (AVAudioSessionInterruptionType)(n.UserInfo?[AVAudioSession.InterruptionTypeKey] as NSNumber)!.UInt32Value;
            if (type == AVAudioSessionInterruptionType.Began) Interrupted?.Invoke();
        });
        _route = center.AddObserver(AVAudioSession.RouteChangeNotification, n =>
        {
            var reason = (AVAudioSessionRouteChangeReason)(n.UserInfo?[AVAudioSession.RouteChangeReasonKey] as NSNumber)!.UInt32Value;
            if (reason == AVAudioSessionRouteChangeReason.OldDeviceUnavailable) Interrupted?.Invoke();   // hörlurar utdragna
        });
    }

    public event Action? Interrupted;

    public void Dispose()
    {
        NSNotificationCenter.DefaultCenter.RemoveObserver(_interruption);
        NSNotificationCenter.DefaultCenter.RemoveObserver(_route);
    }
}
