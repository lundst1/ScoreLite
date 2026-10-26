using System.Collections.Concurrent;
using System.Diagnostics;
using ScoreLite.Core;

namespace ScoreLite.Audio;

public enum PlaybackState { Unloaded, Stopped, CountingIn, Playing, Paused }

/// <summary>IF-3. <c>PlayMs</c> är absolut tid i spelordningen, redan korrigerad för utgångslatens.</summary>
public sealed record PlaybackSnapshot(PlaybackState State, int Epoch, double PlayMs, ScorePosition Marker);

/// <summary>IF-3. Alla metoder anropas från UI-tråden och returnerar direkt. Arbetet sker på audio worker (§7.4).</summary>
public interface IPlaybackEngine : IAsyncDisposable
{
    PlaybackState State { get; }
    void Load(Score score);
    void Play();
    void Pause();
    void Stop();
    void SeekTo(ScorePosition position);
    void SetSelectedParts(IReadOnlySet<string> partIds);
    bool MetronomeEnabled { get; set; }
    bool CountInEnabled { get; set; }
    event Action<PlaybackSnapshot> SnapshotChanged;
}

public sealed class EngineOptions
{
    /// <summary>False: ingen egen tråd. Anroparen driver motorn med <see cref="PlaybackEngine.Pump"/> (tester, offline).</summary>
    public bool UseWorkerThread { get; init; } = true;
    public int RingMilliseconds { get; init; } = 200;   // DR-06
    public int IdleCloseSeconds { get; init; } = 30;    // DR-26
    /// <summary>Snapshot levereras här. Standard: <see cref="SynchronizationContext.Current"/> vid konstruktion (UI-tråden).</summary>
    public SynchronizationContext? Context { get; init; }
}

/// <summary>
/// AL-06 till AL-08. Äger audio worker, kommandokön och epokprotokollet. Tid räknas i ljudramar (P3, DR-06):
/// position = <c>epochStartFrame + max(0, FramesInCurrentEpoch - OutputLatencyFrames)</c>.
/// </summary>
public sealed class PlaybackEngine : IPlaybackEngine
{
    private const int Channels = 2;

    private readonly IAudioOutput _output;
    private readonly ISynthHost _synth;
    private readonly EngineOptions _options;
    private readonly SynchronizationContext? _context;
    private readonly RingBuffer _ring;
    private readonly int _sampleRate;
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly ManualResetEventSlim _signal = new(false);
    private readonly Thread? _thread;
    private volatile bool _disposed;

    private int _state = (int)PlaybackState.Unloaded;
    private volatile bool _metronome, _countIn;

    // Tillstånd som bara audio worker rör.
    private Score? _score;
    private Sequencer? _seq;
    private ScorePosition _marker;
    private HashSet<string> _selected = new();
    private int _epoch;
    private long _epochStartFrame, _framesWritten, _lastSnapshotFrame, _tailFrames;
    private long _pausedTick, _startTick;
    private int _silentBlocks;
    private bool _ended, _pendingSnapshot, _outputRunning;
    private readonly float[] _block = new float[Sequencer.BlockFrames * Channels];
    private readonly Stopwatch _idle = Stopwatch.StartNew();

    public PlaybackEngine(IAudioOutput output, ISynthHost synth, EngineOptions? options = null)
    {
        _output = output;
        _synth = synth;
        _options = options ?? new EngineOptions();
        _sampleRate = output.SampleRate;
        _context = _options.Context ?? SynchronizationContext.Current;
        _ring = new RingBuffer(_sampleRate * _options.RingMilliseconds / 1000, Channels);

        if (_options.UseWorkerThread)
        {
            _thread = new Thread(WorkerLoop) { Name = "ScoreLite.Audio", IsBackground = true, Priority = ThreadPriority.Highest };
            _thread.Start();
        }
    }

    public event Action<PlaybackSnapshot>? SnapshotChanged;

    public PlaybackState State => (PlaybackState)Volatile.Read(ref _state);
    public RingBuffer Ring => _ring;
    public long Underruns => _ring.Underruns;

    public bool MetronomeEnabled { get => _metronome; set => _metronome = value; }
    public bool CountInEnabled { get => _countIn; set => _countIn = value; }

    // ---------------------------------------------------------------- Kommandon (UI-tråden)

    public void Load(Score score) => Post(() => DoLoad(score));
    public void Play() => Post(DoPlay);
    public void Pause() => Post(DoPause);
    public void Stop() => Post(DoStop);
    public void SeekTo(ScorePosition position) => Post(() => DoSeek(position));

    public void SetSelectedParts(IReadOnlySet<string> partIds)
    {
        if (partIds.Count == 0) throw new ArgumentException("Minst en stämma måste vara vald (DR-08).", nameof(partIds));
        var copy = new HashSet<string>(partIds, StringComparer.Ordinal);
        Post(() => DoSetSelection(copy));
    }

    /// <summary>R4: OS-avbrott, utdragna hörlurar eller bakgrund. Beter sig som Pause.</summary>
    public void OnAudioInterrupted() => Post(DoPause);

    private void Post(Action a)
    {
        if (_disposed) return;
        _commands.Enqueue(a);
        _signal.Set();
    }

    // ---------------------------------------------------------------- Worker

    private void WorkerLoop()
    {
        while (!_disposed)
        {
            try { Pump(); }
            catch (Exception) { /* R1: inget undantag får döda ljudtråden */ }
            _signal.Wait(2);
            _signal.Reset();
        }
    }

    /// <summary>En varv av AL-06: kommandon, rendering till ringbufferten, tillståndsövergångar och snapshots.</summary>
    public void Pump()
    {
        while (_commands.TryDequeue(out var c)) c();
        if (_seq != null) _seq.MetronomeEnabled = _metronome;

        var st = State;
        if (st is PlaybackState.Playing or PlaybackState.CountingIn)
        {
            RenderBlocks();
            Transitions();
            EmitSnapshots();
        }
        else if (_outputRunning && _idle.Elapsed.TotalSeconds >= _options.IdleCloseSeconds)
        {
            _output.Stop();   // DR-26
            _outputRunning = false;
        }
    }

    private void RenderBlocks()
    {
        var seq = _seq!;
        while (!_ended && _ring.WritableFrames >= Sequencer.BlockFrames)
        {
            if (!seq.PastEnd) seq.RenderBlock(_block);
            else
            {
                // Slutfas: utsvängning tills utsignalen är tyst eller högst 2 sekunder.
                seq.RenderTail(_block);
                _tailFrames += Sequencer.BlockFrames;
                float peak = 0;
                foreach (float s in _block) peak = Math.Max(peak, Math.Abs(s));
                _silentBlocks = peak < 1e-4f ? _silentBlocks + 1 : 0;
                if (_silentBlocks >= 4 || _tailFrames >= 2L * _sampleRate) _ended = true;
            }
            if (!_ring.TryWrite(_block, _epoch)) break;
            _framesWritten += Sequencer.BlockFrames;
        }
        if (_ended) _ring.CompleteEpoch();
    }

    private long CurrentFrame()
    {
        if (_ring.CurrentEpoch != _epoch) return _epochStartFrame;
        long played = Math.Max(0, _ring.FramesInCurrentEpoch - _output.OutputLatencyFrames);
        return _epochStartFrame + played;
    }

    private void Transitions()
    {
        var seq = _seq!;
        if (_ring.CurrentEpoch != _epoch) return;

        if (State == PlaybackState.CountingIn && CurrentFrame() >= seq.StartFrame)
        {
            Volatile.Write(ref _state, (int)PlaybackState.Playing);
            Emit(PositionMs(seq.StartFrame));
            _lastSnapshotFrame = CurrentFrame();
        }

        // `_ended` väntar på uppspelningen: allt skrivet ska ha lästs av drivrutinen.
        if (_ended && _ring.FramesInCurrentEpoch >= _framesWritten)
        {
            NewEpoch();
            _synth.Reset();
            Volatile.Write(ref _state, (int)PlaybackState.Stopped);
            _idle.Restart();
            Emit(MarkerMs());
        }
    }

    private void EmitSnapshots()
    {
        var st = State;
        if (st is not (PlaybackState.Playing or PlaybackState.CountingIn)) return;
        if (_ring.CurrentEpoch != _epoch) return;   // AL-07 steg 7: tidigast när drivrutinen läser nya epoken

        long cur = CurrentFrame();
        if (_pendingSnapshot)
        {
            _pendingSnapshot = false;
            _lastSnapshotFrame = cur;
            Emit(st == PlaybackState.CountingIn ? PositionMs(_seq!.StartFrame) : PositionMs(cur));
        }
        else if (st == PlaybackState.Playing && cur - _lastSnapshotFrame >= _sampleRate / 2)
        {
            _lastSnapshotFrame = cur;
            Emit(PositionMs(cur));
        }
    }

    // ---------------------------------------------------------------- Kommandologik (AL-07, AL-08)

    private void NewEpoch()
    {
        _epoch++;
        _ring.Flush(_epoch);
        _ended = false;
        _framesWritten = 0;
        _tailFrames = 0;
        _silentBlocks = 0;
    }

    private void DoLoad(Score score)
    {
        _score = score;
        _seq = new Sequencer(score, _synth, _sampleRate);
        _marker = new ScorePosition(0, 0);
        _selected = new HashSet<string>(score.Parts.Select(p => p.Id), StringComparer.Ordinal);
        _pausedTick = 0;
        NewEpoch();
        Volatile.Write(ref _state, (int)PlaybackState.Stopped);
        _idle.Restart();
        Emit(0);
    }

    /// <summary>AL-07.</summary>
    private void StartEpoch(long startTick, bool countIn)
    {
        var seq = _seq!;
        NewEpoch();                                       // steg 2
        seq.SetSelection(_selected);                      // steg 4
        _epochStartFrame = seq.Start(startTick, countIn); // steg 3, 5, 6
        _startTick = startTick;
        Volatile.Write(ref _state, countIn ? (int)PlaybackState.CountingIn : (int)PlaybackState.Playing);
        _pendingSnapshot = true;                          // steg 7: skickas när ring.CurrentEpoch == epoch
        EnsureOutput();
    }

    private void EnsureOutput()
    {
        if (_outputRunning) return;
        _output.Start(_ring);
        _outputRunning = true;
    }

    private long MarkerTick() => _score!.Order.FirstPlayTick(_marker);
    private double PositionMs(long frame) => frame * 1000.0 / _sampleRate;
    private double MarkerMs() => PositionMs(_score!.Tempo.PlayTickToFrame(MarkerTick(), _sampleRate));
    private long CurrentTick() => Math.Max(_startTick, _score!.Tempo.FrameToPlayTick(CurrentFrame(), _sampleRate));

    private void DoPlay()
    {
        if (_score == null) return;
        switch (State)
        {
            case PlaybackState.Stopped: StartEpoch(MarkerTick(), _countIn); break;
            case PlaybackState.Paused: StartEpoch(_pausedTick, _countIn); break;
        }
    }

    private void DoPause()
    {
        switch (State)
        {
            case PlaybackState.Playing:
                _pausedTick = CurrentTick();
                EnterIdleState(PlaybackState.Paused, PositionMs(_score!.Tempo.PlayTickToFrame(_pausedTick, _sampleRate)));
                break;
            case PlaybackState.CountingIn:
                _pausedTick = MarkerTick();   // inräkningen kastas, positionen blir Marker (AL-08)
                EnterIdleState(PlaybackState.Paused, MarkerMs());
                break;
        }
    }

    private void DoStop()
    {
        if (State is PlaybackState.Playing or PlaybackState.CountingIn or PlaybackState.Paused)
            EnterIdleState(PlaybackState.Stopped, MarkerMs());
    }

    private void EnterIdleState(PlaybackState next, double ms)
    {
        NewEpoch();            // kastar buffrat ljud. Läsaren tonar ut (declick)
        _synth.Reset();
        Volatile.Write(ref _state, (int)next);
        _idle.Restart();
        Emit(ms);
    }

    private void DoSeek(ScorePosition p)
    {
        if (_score == null) return;
        p = new ScorePosition(Math.Clamp(p.MeasureIndex, 0, _score.Measures.Count - 1), Math.Max(0, p.TickInMeasure));
        _marker = p;
        switch (State)
        {
            case PlaybackState.Stopped:
                Emit(MarkerMs());
                break;
            case PlaybackState.Paused:
                _pausedTick = MarkerTick();
                Emit(MarkerMs());
                break;
            case PlaybackState.Playing:
                StartEpoch(MarkerTick(), countIn: false);   // DR-11: hopp utan inräkning
                break;
            case PlaybackState.CountingIn:
                StartEpoch(MarkerTick(), countIn: true);
                break;
        }
    }

    private void DoSetSelection(HashSet<string> ids)
    {
        if (_score == null) return;
        var valid = new HashSet<string>(ids.Where(id => _score.Parts.Any(p => p.Id == id)), StringComparer.Ordinal);
        if (valid.Count == 0) return;
        _selected = valid;
        switch (State)
        {
            case PlaybackState.Playing: StartEpoch(CurrentTick(), countIn: false); break;   // DR-09
            case PlaybackState.CountingIn: StartEpoch(_startTick, countIn: true); break;
        }
    }

    private void Emit(double playMs)
    {
        var snap = new PlaybackSnapshot(State, _epoch, playMs, _marker);
        var handler = SnapshotChanged;
        if (handler == null) return;
        if (_context != null) _context.Post(_ => handler(snap), null);
        else handler(snap);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _signal.Set();
        _thread?.Join(1000);
        _output.Stop();
        _outputRunning = false;
        Volatile.Write(ref _state, (int)PlaybackState.Unloaded);
        return ValueTask.CompletedTask;
    }
}
