using Android.Content;
using Android.Media;
using Android.OS;
using ScoreLite.Audio;

namespace ScoreLite.App.Platforms.AndroidAudio;

/// <summary>
/// IF-4 på Android: en <see cref="AudioTrack"/> i strömläge (float, stereo, 44100 Hz, låg latens) som matas från
/// <see cref="RingBuffer"/> av en egen tråd. Tråden gör bara det som driver-callbacken får göra (§7.4): läsa ur ringbufferten
/// och skriva till AudioTrack. <see cref="OutputLatencyFrames"/> mäts med <c>AudioTrack.GetTimestamp</c>.
/// </summary>
public sealed class AndroidAudioOutput : IAudioOutput
{
    private const int Rate = 44100;
    private const int ChunkFrames = 256;

    private AudioTrack? _track;
    private Thread? _thread;
    private volatile bool _running;
    private long _consumed;
    private int _latencyFrames;

    public int SampleRate => Rate;
    public int OutputLatencyFrames => Volatile.Read(ref _latencyFrames);
    public long FramesConsumed => Interlocked.Read(ref _consumed);

    public void Start(RingBuffer source)
    {
        Stop();
        int minBytes = AudioTrack.GetMinBufferSize(Rate, ChannelOut.Stereo, Encoding.PcmFloat);
        if (minBytes <= 0) throw new InvalidOperationException("AudioTrack stöder inte float/stereo/44100.");
        // Två gånger minsta buffert: tålig mot kortvariga schemaläggningsstopp utan att latensen blir stor.
        int bufferBytes = Math.Max(minBytes * 2, ChunkFrames * 2 * sizeof(float) * 4);

        var attrs = new AudioAttributes.Builder()!
            .SetUsage(AudioUsageKind.Media)!
            .SetContentType(AudioContentType.Music)!
            .Build()!;
        var format = new AudioFormat.Builder()!
            .SetSampleRate(Rate)!
            .SetEncoding(Encoding.PcmFloat)!
            .SetChannelMask(ChannelOut.Stereo)!
            .Build()!;
        _track = new AudioTrack.Builder()!
            .SetAudioAttributes(attrs)!
            .SetAudioFormat(format)!
            .SetBufferSizeInBytes(bufferBytes)!
            .SetTransferMode(AudioTrackMode.Stream)!
            .SetPerformanceMode(AudioTrackPerformanceMode.LowLatency)!
            .Build()!;
        if (_track.State != AudioTrackState.Initialized) throw new InvalidOperationException("AudioTrack kunde inte startas.");

        Volatile.Write(ref _latencyFrames, _track.BufferSizeInFrames);   // grov start; ersätts av tidsstämpelmätningen
        _track.Play();

        _running = true;
        var track = _track;
        _thread = new Thread(() => Pump(track, source)) { Name = "ScoreLite.AudioTrack", IsBackground = true };
        _thread.Start();
    }

    private void Pump(AudioTrack track, RingBuffer ring)
    {
        Process.SetThreadPriority(Android.OS.ThreadPriority.UrgentAudio);
        var buf = new float[ChunkFrames * 2];
        var ts = new AudioTimestamp();
        long written = 0;
        int loops = 0;
        while (_running)
        {
            ring.Read(buf, out _);
            int n = track.Write(buf, 0, buf.Length, WriteMode.Blocking);   // blockerar tills AudioTrack har plats
            if (n <= 0) break;
            written += n / 2;
            Interlocked.Add(ref _consumed, n / 2);

            if (++loops % 16 == 0 && track.GetTimestamp(ts))
            {
                // Ramar skrivna men ännu inte spelade = latensen mellan ringbufferten och högtalaren.
                long pending = written - ts.FramePosition;
                if (pending >= 0 && pending < Rate) Volatile.Write(ref _latencyFrames, (int)pending);
            }
        }
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(500);
        _thread = null;
        if (_track != null)
        {
            try { _track.Stop(); } catch (Java.Lang.IllegalStateException) { }
            _track.Release();
            _track.Dispose();
            _track = null;
        }
    }

    public void Dispose() => Stop();
}
