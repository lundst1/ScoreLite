using ScoreLite.Core;

namespace ScoreLite.Audio;

/// <summary>
/// AL-06 (renderloopens innehåll) och AL-07 steg 3–6 (start/chase). Ren, synkron och deterministisk: ett anrop till
/// <see cref="RenderBlock"/> ger ett block om 256 ramar. Trådning och epoker hör till <see cref="PlaybackEngine"/>.
/// Alla positioner är absoluta ramar i spelordningen och kan vara negativa under inräkning.
/// </summary>
public sealed class Sequencer
{
    public const int BlockFrames = 256;   // DR-24
    private const int MaxActiveClicks = 16;

    private sealed class EventSet
    {
        public required PlaybackEvent[] Events;
        public required long[] Frames;
    }

    private readonly Score _score;
    private readonly ISynthHost _synth;
    private readonly int _sampleRate;
    private readonly IReadOnlyDictionary<string, ChannelAssignment> _channels;
    private readonly Dictionary<string, EventSet> _cache = new(StringComparer.Ordinal);

    private readonly long[] _gridFrames;
    private readonly bool[] _gridAccent;
    private readonly float[] _clickNormal, _clickAccent;
    private readonly float[]?[] _activeClick = new float[]?[MaxActiveClicks];
    private readonly int[] _activePos = new int[MaxActiveClicks];

    private EventSet _set;
    private int _eventIdx, _gridIdx, _ciIdx;
    private (long Frame, bool Accent)[] _countIn = Array.Empty<(long, bool)>();
    private volatile bool _metronome;
    private readonly long _endFrame;

    public Sequencer(Score score, ISynthHost synth, int sampleRate = 44100)
    {
        _score = score;
        _synth = synth;
        _sampleRate = sampleRate;
        _channels = ChannelAllocator.Allocate(score.Parts);
        synth.Configure(ChannelAllocator.SynthCount(_channels), BlockFrames);

        var grid = Metronome.BuildGrid(score.Order, score.Measures);
        _gridFrames = grid.Select(c => score.Tempo.PlayTickToFrame(c.PlayTick, sampleRate)).ToArray();
        _gridAccent = grid.Select(c => c.Accent).ToArray();
        _clickNormal = Metronome.GenerateClick(sampleRate, accent: false);
        _clickAccent = Metronome.GenerateClick(sampleRate, accent: true);
        _endFrame = score.Tempo.PlayTickToFrame(score.Order.TotalTicks, sampleRate);

        _set = null!;
        SetSelection(new HashSet<string>(score.Parts.Select(p => p.Id)));
    }

    public bool MetronomeEnabled { get => _metronome; set => _metronome = value; }
    public long NextFrame { get; private set; }
    /// <summary>Ramen där första musiken börjar (efter inräkningen).</summary>
    public long StartFrame { get; private set; }
    public long CountInFrames { get; private set; }

    /// <summary>Slutfasen börjar när alla händelser och hela spelordningen är renderade.</summary>
    public bool PastEnd => NextFrame >= Math.Max(_endFrame, _set.Frames.Length == 0 ? 0 : _set.Frames[^1]);

    public IReadOnlyDictionary<string, ChannelAssignment> Channels => _channels;

    /// <summary>AL-07 steg 4: händelser för valda stämmor, cachade per val.</summary>
    public void SetSelection(IReadOnlySet<string> partIds)
    {
        string key = string.Join("|", partIds.OrderBy(x => x, StringComparer.Ordinal));
        if (!_cache.TryGetValue(key, out var set))
        {
            var ev = EventGenerator.Generate(_score, partIds).ToArray();
            var frames = new long[ev.Length];
            for (int i = 0; i < ev.Length; i++) frames[i] = _score.Tempo.PlayTickToFrame(ev[i].PlayTick, _sampleRate);
            _cache[key] = set = new EventSet { Events = ev, Frames = frames };
        }
        _set = set;
    }

    /// <summary>
    /// AL-07 steg 3, 5 och 6: stäng toner, chase, placera läsaren. Returnerar ramen där rendering börjar
    /// (<c>PlayTickToFrame(startTick) - countInFrames</c>).
    /// </summary>
    public long Start(long startTick, bool countIn)
    {
        _synth.Reset();
        Array.Clear(_activeClick);
        var ev = _set.Events;

        // Chase: senaste ProgramChange och senaste värde per controller före startTick (DR-19: inga noter).
        int first = LowerBound(ev, startTick);
        var lastByKey = new Dictionary<(string, int, int), int>();
        for (int i = 0; i < first; i++)
        {
            var e = ev[i];
            if (e.Kind == PlaybackEventKind.ProgramChange) lastByKey[(e.PartId, 0, 0)] = i;
            else if (e.Kind == PlaybackEventKind.Controller) lastByKey[(e.PartId, 1, e.Data1)] = i;
        }
        foreach (int i in lastByKey.Values.OrderBy(x => x)) Apply(ev[i]);
        _eventIdx = first;

        StartFrame = _score.Tempo.PlayTickToFrame(startTick, _sampleRate);
        if (countIn)
        {
            _countIn = Metronome.BuildCountIn(_score, startTick, _sampleRate, out long ciFrames).ToArray();
            CountInFrames = ciFrames;
        }
        else { _countIn = Array.Empty<(long, bool)>(); CountInFrames = 0; }
        _ciIdx = 0;

        _gridIdx = Array.BinarySearch(_gridFrames, StartFrame);
        if (_gridIdx < 0) _gridIdx = ~_gridIdx;
        else while (_gridIdx > 0 && _gridFrames[_gridIdx - 1] == StartFrame) _gridIdx--;

        NextFrame = StartFrame - CountInFrames;
        return NextFrame;
    }

    private static int LowerBound(PlaybackEvent[] ev, long tick)
    {
        int lo = 0, hi = ev.Length;
        while (lo < hi) { int mid = (lo + hi) >>> 1; if (ev[mid].PlayTick < tick) lo = mid + 1; else hi = mid; }
        return lo;
    }

    private void Apply(PlaybackEvent e)
    {
        if (!_channels.TryGetValue(e.PartId, out var a)) return;
        switch (e.Kind)
        {
            case PlaybackEventKind.ProgramChange:
                if (e.Data2 != 128)   // slagverk ligger på kanal 9, som MeltySynth redan behandlar som trumkanal
                {
                    _synth.ProcessMidi(a.Synth, a.Channel, MeltySynthHost.Controller, 0, 0);    // bank 0 (DR-30)
                    _synth.ProcessMidi(a.Synth, a.Channel, MeltySynthHost.Controller, 32, 0);
                }
                _synth.ProcessMidi(a.Synth, a.Channel, MeltySynthHost.ProgramChange, Math.Clamp(e.Data1, 0, 127), 0);
                break;
            case PlaybackEventKind.Controller:
                _synth.ProcessMidi(a.Synth, a.Channel, MeltySynthHost.Controller, e.Data1, e.Data2);
                break;
            case PlaybackEventKind.NoteOff:
                _synth.ProcessMidi(a.Synth, a.Channel, MeltySynthHost.NoteOff, Math.Clamp(e.Data1, 0, 127), 0);
                break;
            case PlaybackEventKind.NoteOn:
                _synth.ProcessMidi(a.Synth, a.Channel, MeltySynthHost.NoteOn, Math.Clamp(e.Data1, 0, 127), Math.Clamp(e.Data2, 1, 127));
                break;
        }
    }

    /// <summary>AL-06: renderar ett block (<see cref="BlockFrames"/> ramar, stereo) och flyttar fram läsaren.</summary>
    public void RenderBlock(Span<float> dest)
    {
        if (dest.Length != BlockFrames * 2) throw new ArgumentException("Blocket måste vara 256 ramar stereo.", nameof(dest));
        long blockStart = NextFrame, blockEnd = blockStart + BlockFrames;
        var ev = _set.Events; var fr = _set.Frames;

        int off = 0;
        while (_eventIdx < ev.Length && fr[_eventIdx] < blockEnd)
        {
            int rel = (int)Math.Max(off, fr[_eventIdx] - blockStart);
            if (rel > off) { _synth.Render(dest.Slice(off * 2, (rel - off) * 2)); off = rel; }
            Apply(ev[_eventIdx]);
            _eventIdx++;
        }
        if (off < BlockFrames) _synth.Render(dest.Slice(off * 2));

        MixClicks(dest, blockStart, blockEnd);
        SoftLimit.Process(dest);
        NextFrame = blockEnd;
    }

    /// <summary>Slutfasen: bara synten (utsvängning), inga nya händelser eller klick.</summary>
    public void RenderTail(Span<float> dest)
    {
        _synth.Render(dest);
        SoftLimit.Process(dest);
        NextFrame += dest.Length / 2;
    }

    private void MixClicks(Span<float> dest, long blockStart, long blockEnd)
    {
        ContinueActive(dest);   // först klick som började i ett tidigare block

        // Inräkningen använder alltid klickljuden. Rasterklick bara när metronomen är på (AL-09, AL-10).
        while (_ciIdx < _countIn.Length && _countIn[_ciIdx].Frame < blockEnd)
        {
            Trigger(_countIn[_ciIdx].Accent, (int)Math.Max(0, _countIn[_ciIdx].Frame - blockStart), dest);
            _ciIdx++;
        }
        bool on = _metronome;
        while (_gridIdx < _gridFrames.Length && _gridFrames[_gridIdx] < blockEnd)
        {
            if (on && _gridFrames[_gridIdx] >= blockStart)
                Trigger(_gridAccent[_gridIdx], (int)(_gridFrames[_gridIdx] - blockStart), dest);
            _gridIdx++;
        }
    }

    // Nytt klick mixas direkt från sin förskjutning. Det som inte ryms i blocket sparas för nästa block.
    private void Trigger(bool accent, int offset, Span<float> dest)
    {
        var buf = accent ? _clickAccent : _clickNormal;
        int n = Math.Min(buf.Length, BlockFrames - offset);
        for (int i = 0; i < n; i++)
        {
            dest[(offset + i) * 2] += buf[i];
            dest[(offset + i) * 2 + 1] += buf[i];
        }
        if (n < buf.Length)
            for (int s = 0; s < MaxActiveClicks; s++)
                if (_activeClick[s] == null) { _activeClick[s] = buf; _activePos[s] = n; break; }
    }

    private void ContinueActive(Span<float> dest)
    {
        for (int s = 0; s < MaxActiveClicks; s++)
        {
            var buf = _activeClick[s];
            if (buf == null) continue;
            int pos = _activePos[s];
            int n = Math.Min(buf.Length - pos, BlockFrames);
            for (int i = 0; i < n; i++)
            {
                dest[i * 2] += buf[pos + i];
                dest[i * 2 + 1] += buf[pos + i];
            }
            if (pos + n >= buf.Length) _activeClick[s] = null; else _activePos[s] = pos + n;
        }
    }
}
