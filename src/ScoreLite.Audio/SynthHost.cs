using MeltySynth;

namespace ScoreLite.Audio;

/// <summary>Synt bakom ett gränssnitt så att sequencern kan testas utan ljudfont (AL-05).</summary>
public interface ISynthHost : IDisposable
{
    int SampleRate { get; }
    /// <summary>Antal synt-instanser (AL-05). Anropas före första rendering. Allokerar buffertar.</summary>
    void Configure(int synthCount, int maxBlockFrames);
    void ProcessMidi(int synth, int channel, int command, int data1, int data2);
    /// <summary>Renderar och summerar alla instanser till <paramref name="interleavedStereo"/> (skrivs över).</summary>
    void Render(Span<float> interleavedStereo);
    /// <summary>Stäng alla toner och återställ controllers i alla instanser (AL-07 steg 3).</summary>
    void Reset();
}

/// <summary>AL-05 regel 4. En MeltySynth-<see cref="Synthesizer"/> per instans, alla med samma delade <see cref="SoundFont"/>.</summary>
public sealed class MeltySynthHost : ISynthHost
{
    public const int NoteOff = 0x80, NoteOn = 0x90, Controller = 0xB0, ProgramChange = 0xC0;

    private readonly SoundFont _font;
    private readonly bool _ownsFont;
    private Synthesizer[] _synths = Array.Empty<Synthesizer>();
    private float[] _left = Array.Empty<float>(), _right = Array.Empty<float>();
    private float[] _sumL = Array.Empty<float>(), _sumR = Array.Empty<float>();

    public MeltySynthHost(SoundFont font, int sampleRate = 44100, bool ownsFont = false)
    {
        _font = font;
        _ownsFont = ownsFont;
        SampleRate = sampleRate;
    }

    public static MeltySynthHost FromStream(Stream soundFont, int sampleRate = 44100) =>
        new(new SoundFont(soundFont), sampleRate, ownsFont: true);

    public int SampleRate { get; }

    public void Configure(int synthCount, int maxBlockFrames)
    {
        // Reverb och chorus är av: spar CPU och minne på mobil (N2).
        var settings = new SynthesizerSettings(SampleRate) { EnableReverbAndChorus = false };
        _synths = Enumerable.Range(0, Math.Max(1, synthCount)).Select(_ => new Synthesizer(_font, settings)).ToArray();
        _left = new float[maxBlockFrames]; _right = new float[maxBlockFrames];
        _sumL = new float[maxBlockFrames]; _sumR = new float[maxBlockFrames];
    }

    public void ProcessMidi(int synth, int channel, int command, int data1, int data2)
    {
        if ((uint)synth >= (uint)_synths.Length) return;
        _synths[synth].ProcessMidiMessage(channel, command, data1, data2);
    }

    public void Render(Span<float> interleavedStereo)
    {
        int frames = interleavedStereo.Length / 2;
        var sl = _sumL.AsSpan(0, frames); var sr = _sumR.AsSpan(0, frames);
        sl.Clear(); sr.Clear();
        foreach (var s in _synths)
        {
            var l = _left.AsSpan(0, frames); var r = _right.AsSpan(0, frames);
            s.Render(l, r);
            for (int i = 0; i < frames; i++) { sl[i] += l[i]; sr[i] += r[i]; }
        }
        for (int i = 0; i < frames; i++)
        {
            interleavedStereo[2 * i] = sl[i];
            interleavedStereo[2 * i + 1] = sr[i];
        }
    }

    public void Reset()
    {
        // Synthesizer.Reset() stänger tonerna direkt, nollställer kanaler och kastar även resten av det redan renderade
        // 64-rams-blocket, så att inget gammalt ljud följer med in i nästa epok.
        foreach (var s in _synths) s.Reset();
    }

    public void Dispose() { /* SoundFont har inga ohanterade resurser */ }
}

/// <summary>Mjuk begränsare så att summan av många stämmor inte klipper (AL-05 regel 4). Linjär upp till knäet, därefter tanh.</summary>
public static class SoftLimit
{
    public const float Knee = 0.6f;

    public static float Process(float x)
    {
        float a = Math.Abs(x);
        if (a <= Knee) return x;
        float y = Knee + (1f - Knee) * MathF.Tanh((a - Knee) / (1f - Knee));
        return x < 0 ? -y : y;
    }

    public static void Process(Span<float> samples)
    {
        for (int i = 0; i < samples.Length; i++) samples[i] = Process(samples[i]);
    }
}
