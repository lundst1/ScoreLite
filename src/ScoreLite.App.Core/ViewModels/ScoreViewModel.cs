using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScoreLite.App.Bridge;
using ScoreLite.App.Services;
using ScoreLite.Audio;
using ScoreLite.Core;

namespace ScoreLite.App.ViewModels;

/// <summary>En rad i stämmalistan (UI-7).</summary>
public sealed partial class PartItemViewModel : ObservableObject
{
    public PartItemViewModel(string id, string name) { Id = id; Name = name; }
    public string Id { get; }
    public string Name { get; }

    [ObservableProperty] private bool _isSelected = true;
    /// <summary>Sista valda stämman kan inte avmarkeras: kryssrutan är inaktiv (UI-7).</summary>
    [ObservableProperty] private bool _canToggle = true;
    partial void OnIsSelectedChanged(bool value) => OnPropertyChanged(nameof(AccessibilityLabel));
    partial void OnCanToggleChanged(bool value) => OnPropertyChanged(nameof(AccessibilityLabel));
    public string AccessibilityLabel => IsSelected
        ? (CanToggle ? $"{Name}, vald" : $"{Name}, vald. Kan inte avmarkeras eftersom minst en stämma måste vara vald")
        : $"{Name}, inte vald";
}

/// <summary>
/// §11.3. Äger <see cref="IPlaybackEngine"/>, <see cref="IScoreBridge"/>, valda stämmor, växlarna och
/// <see cref="PlaybackSnapshot"/>. Översätter <c>tap</c> till <c>SeekTo</c> (AL-13) och bygger tidslinjen (AL-11).
/// </summary>
public sealed partial class ScoreViewModel : ObservableObject, IDisposable
{
    private readonly IPlaybackEngine _engine;
    private readonly IScoreBridge _bridge;
    private readonly IScoreLoader _loader;
    private readonly ISettings _settings;
    private readonly IKeepAwake _keepAwake;
    private readonly BridgeDelayEstimator _delay;
    private readonly IAudioInterruptions? _interruptions;

    private Score? _score;
    private string? _pendingMei;
    private bool _bridgeReady;
    private int _requestId;
    private int _lastLayoutRequest = -1;
    private List<LayoutItem> _layoutItems = new();
    private int _timelineVersion;
    private bool _disposed;

    public ScoreViewModel(IPlaybackEngine engine, IScoreBridge bridge, IScoreLoader loader, ISettings settings,
        IKeepAwake keepAwake, IAudioInterruptions? interruptions = null, BridgeDelayEstimator? delay = null)
    {
        _engine = engine;
        _bridge = bridge;
        _loader = loader;
        _settings = settings;
        _keepAwake = keepAwake;
        _delay = delay ?? new BridgeDelayEstimator();
        _interruptions = interruptions;

        // DR-25: växlarna återställs vid start.
        _metronomeEnabled = settings.MetronomeEnabled;
        _countInEnabled = settings.CountInEnabled;
        _zoomLevel = Math.Clamp(settings.ZoomLevel, 0, 4);
        _followEnabled = settings.FollowEnabled;
        engine.MetronomeEnabled = _metronomeEnabled;
        engine.CountInEnabled = _countInEnabled;

        _engine.SnapshotChanged += OnSnapshot;
        _bridge.Ready += OnReady;
        _bridge.LayoutReady += OnLayoutReady;
        _bridge.Tap += OnTap;
        _bridge.FollowChanged += OnFollowChanged;
        _bridge.Pong += m => _delay.OnPong(m.Id);
        _bridge.RenderError += OnRenderError;
        if (_interruptions != null) _interruptions.Interrupted += OnInterrupted;
    }

    // ---------------------------------------------------------------- Tillstånd för vyn

    public ObservableCollection<PartItemViewModel> Parts { get; } = new();
    public ObservableCollection<string> Warnings { get; } = new();

    /// <summary>Sätts av vyn: webbvyns bredd i CSS-pixlar och tema (light/dark).</summary>
    public int ViewportWidthPx { get; set; } = 400;
    public string Theme { get; set; } = "light";

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private PlaybackState _state = PlaybackState.Unloaded;
    [ObservableProperty] private bool _metronomeEnabled;
    [ObservableProperty] private bool _countInEnabled;
    [ObservableProperty] private bool _followEnabled = true;
    [ObservableProperty] private int _zoomLevel = 2;
    /// <summary>RENDER_FAILED: notbilden saknas men uppspelning och stämmaval fungerar (§12.3).</summary>
    [ObservableProperty] private bool _renderDegraded;
    /// <summary>AUDIO_INIT_FAILED: notbilden visas men uppspelningsknapparna är inaktiva.</summary>
    [ObservableProperty] private bool _audioFailed;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _showWarnings;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));
    public bool HasWarnings => Warnings.Count > 0;
    public bool CanPlay => _score != null && !AudioFailed;
    public bool IsPlaying => State is PlaybackState.Playing or PlaybackState.CountingIn;
    public bool CanStop => State is PlaybackState.Playing or PlaybackState.CountingIn or PlaybackState.Paused;
    public string PlayPauseLabel => IsPlaying ? "Pausa" : "Spela";
    // U+FE0E: text- i stället för emojipresentation (annars ritas pausknappen som en färgad emoji).
    public string PlayPauseGlyph => char.ConvertFromUtf32(IsPlaying ? 0x23F8 : 0x25B6) + char.ConvertFromUtf32(0xFE0E);
    public string MetronomeLabel => MetronomeEnabled ? "Metronom på" : "Metronom av";
    public string CountInLabel => CountInEnabled ? "Inräkning på" : "Inräkning av";
    public string FollowLabel => FollowEnabled ? "Följ uppspelning på" : "Följ uppspelning av";
    public string Marker { get; private set; } = "";

    partial void OnStateChanged(PlaybackState value)
    {
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(PlayPauseGlyph));
        OnPropertyChanged(nameof(PlayPauseLabel));
        PlayPauseCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }
    partial void OnMetronomeEnabledChanged(bool value) => OnPropertyChanged(nameof(MetronomeLabel));
    partial void OnCountInEnabledChanged(bool value) => OnPropertyChanged(nameof(CountInLabel));
    partial void OnFollowEnabledChanged(bool value) => OnPropertyChanged(nameof(FollowLabel));
    partial void OnAudioFailedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanPlay));
        PlayPauseCommand.NotifyCanExecuteChanged();
    }

    // ---------------------------------------------------------------- Laddning (§7.5 steg 1)

    /// <summary>Laddar partituret. Returnerar false vid fatalt fel, och <see cref="ErrorMessage"/> är då satt.</summary>
    public async Task<bool> LoadAsync(byte[] data, string fileName, CancellationToken ct = default)
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var result = await _loader.LoadAsync(new MemoryStream(data, writable: false), fileName, ct).ConfigureAwait(true);
            if (result.Score == null)
            {
                var err = result.Diagnostics.FirstOrDefault(d => d.Severity == DiagSeverity.Error);
                ErrorMessage = ErrorMessages.For(err?.Code ?? "NOT_A_SCORE");
                return false;
            }

            _score = result.Score;
            Title = string.IsNullOrWhiteSpace(_score.Title) ? Path.GetFileNameWithoutExtension(fileName) : _score.Title;

            Warnings.Clear();
            foreach (var d in result.Diagnostics.Where(ErrorMessages.IsUserVisible)) Warnings.Add(ErrorMessages.For(d.Code));
            OnPropertyChanged(nameof(HasWarnings));

            Parts.Clear();
            foreach (var p in _score.Parts) Parts.Add(new PartItemViewModel(p.Id, p.Name));
            UpdatePartToggles();

            try
            {
                _engine.Load(_score);
                AudioFailed = false;
            }
            catch (Exception)
            {
                AudioFailed = true;   // AUDIO_INIT_FAILED: notbilden visas ändå
                ErrorMessage = ErrorMessages.For("AUDIO_INIT_FAILED");
            }
            OnPropertyChanged(nameof(CanPlay));

            string mei = await Task.Run(() => MeiExporter.Export(_score), ct).ConfigureAwait(true);
            _pendingMei = mei;
            SendPendingScore();
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            ErrorMessage = ErrorMessages.For("NOT_A_SCORE");   // R1: ingen krasch
            return false;
        }
        finally { IsLoading = false; }
    }

    private void OnReady(ReadyMessage m)
    {
        _bridgeReady = true;
        SendPendingScore();
        PingBridge();
    }

    private void SendPendingScore()
    {
        if (!_bridgeReady || _pendingMei == null) return;
        _requestId++;
        _bridge.LoadScore(_requestId, _pendingMei, ViewportWidthPx, ZoomLevel, Theme);
        _pendingMei = null;
    }

    /// <summary>Anropas av vyn efter rotation eller storleksändring: ny layout (§7.5 steg 6). Uppspelningen påverkas inte.</summary>
    public void OnViewportChanged(int widthPx)
    {
        if (widthPx <= 0 || widthPx == ViewportWidthPx) return;
        ViewportWidthPx = widthPx;
        if (_score != null && _bridgeReady) _bridge.SetZoom(ZoomLevel);   // JS renderar om med ny bredd
    }

    /// <summary>Anropas av vyn var 10:e sekund (AL-12 regel 5).</summary>
    public void PingBridge() => _bridge.Ping(_delay.NextPing());

    // ---------------------------------------------------------------- Layout och pekare (AL-11)

    private async void OnLayoutReady(LayoutReadyMessage m)
    {
        if (_score == null) return;
        var score = _score;
        _layoutItems = m.Items.ToList();
        _lastLayoutRequest = m.RequestId;
        int version = ++_timelineVersion;
        int layoutGen = m.LayoutGen;
        var items = _layoutItems;
        try
        {
            var entries = await Task.Run(() => CursorTimelineBuilder.Build(score, items, 44100)).ConfigureAwait(true);
            if (_disposed || version != _timelineVersion) return;   // en nyare layout har tagit över
            _bridge.SetTimeline(layoutGen, entries);
        }
        catch (Exception) { /* pekaren saknas men uppspelningen fungerar (R1) */ }
    }

    private void OnRenderError(RenderErrorMessage m)
    {
        RenderDegraded = true;
        ErrorMessage = ErrorMessages.For("RENDER_FAILED");
    }

    private void OnFollowChanged(FollowChangedMessage m)
    {
        if (FollowEnabled == m.Enabled) return;
        FollowEnabled = m.Enabled;   // JS har redan ändrat läget: skicka inte tillbaka
        _settings.FollowEnabled = m.Enabled;
    }

    // ---------------------------------------------------------------- Uppspelningsstatus

    private void OnSnapshot(PlaybackSnapshot s)
    {
        State = s.State;
        _keepAwake.SetKeepScreenOn(s.State is PlaybackState.Playing or PlaybackState.CountingIn);   // DR-22
        _bridge.SetClock(s.Epoch, ClockState(s.State), s.PlayMs, _delay.DelayMs);
    }

    internal static string ClockState(PlaybackState s) => s switch
    {
        PlaybackState.Playing => "playing",
        PlaybackState.CountingIn => "countingIn",
        PlaybackState.Paused => "paused",
        _ => "stopped",
    };

    private void OnInterrupted()
    {
        if (IsPlaying) _engine.Pause();   // R4
    }

    /// <summary>R4: appen går till bakgrunden. Uppspelningen pausas och positionen behålls.</summary>
    public void OnBackgrounded() => OnInterrupted();

    // ---------------------------------------------------------------- Kommandon

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private void PlayPause()
    {
        if (IsPlaying) { _engine.Pause(); return; }
        SetFollowInternal(true);   // AL-12: följ-läget slås på igen vid Play
        _engine.Play();
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _engine.Stop();

    [RelayCommand]
    private void ToggleMetronome()
    {
        MetronomeEnabled = !MetronomeEnabled;
        _engine.MetronomeEnabled = MetronomeEnabled;
        _settings.MetronomeEnabled = MetronomeEnabled;
    }

    [RelayCommand]
    private void ToggleCountIn()
    {
        CountInEnabled = !CountInEnabled;
        _engine.CountInEnabled = CountInEnabled;
        _settings.CountInEnabled = CountInEnabled;
    }

    [RelayCommand]
    private void ToggleFollow() => SetFollowInternal(!FollowEnabled);

    private void SetFollowInternal(bool on)
    {
        FollowEnabled = on;
        _settings.FollowEnabled = on;
        _bridge.SetFollow(on);
    }

    [RelayCommand]
    private void ZoomIn() => SetZoom(ZoomLevel + 1);

    [RelayCommand]
    private void ZoomOut() => SetZoom(ZoomLevel - 1);

    public void SetZoom(int level)
    {
        level = Math.Clamp(level, 0, 4);
        if (level == ZoomLevel) return;
        ZoomLevel = level;
        _settings.ZoomLevel = level;
        _bridge.SetZoom(level);   // omlayout: ny layoutReady -> ny tidslinje
    }

    [RelayCommand]
    private void ToggleWarnings() => ShowWarnings = !ShowWarnings;

    // ---------------------------------------------------------------- Stämmor (UI-7, DR-08)

    [RelayCommand]
    private void TogglePart(PartItemViewModel? part)
    {
        if (part == null || !part.CanToggle) return;
        part.IsSelected = !part.IsSelected;
        ApplySelection();
    }

    [RelayCommand]
    private void SelectAllParts()
    {
        foreach (var p in Parts) p.IsSelected = true;
        ApplySelection();
    }

    private void ApplySelection()
    {
        UpdatePartToggles();
        var ids = Parts.Where(p => p.IsSelected).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        if (ids.Count > 0) _engine.SetSelectedParts(ids);
    }

    private void UpdatePartToggles()
    {
        int selected = Parts.Count(p => p.IsSelected);
        foreach (var p in Parts)
        {
            p.CanToggle = !(p.IsSelected && selected == 1);
        }
    }

    // ---------------------------------------------------------------- Tryck (AL-13)

    private void OnTap(TapMessage tap)
    {
        if (_score == null) return;
        if (!TryResolveTap(tap, out var pos, out string anchorId)) return;
        _engine.SeekTo(pos);
        _bridge.SetMarker(anchorId, pos.MeasureIndex, pos.TickInMeasure);
        Marker = $"{pos.MeasureIndex + 1}";
        OnPropertyChanged(nameof(Marker));
    }

    /// <summary>
    /// AL-13 steg 3. En not ger notens takt och tick. Annars väljs närmaste notstart i takten på x (DR-10).
    /// Finns ingen not med geometri i takten används taktens början.
    /// </summary>
    internal bool TryResolveTap(TapMessage tap, out ScorePosition position, out string anchorId)
    {
        position = default;
        anchorId = "";
        var score = _score!;
        int lastMeasure = score.Measures.Count - 1;

        if (tap.Id is { Length: > 1 } id && id[0] == 'n' &&
            int.TryParse(id.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int noteIdx) &&
            noteIdx >= 0 && noteIdx < score.Notes.Count)
        {
            var n = score.Notes[noteIdx];
            int mi = MeasureOf(score, n.ScoreTick);
            position = new ScorePosition(mi, n.ScoreTick - score.Measures[mi].StartScoreTick);
            anchorId = id;
            return true;
        }

        int measure = Math.Clamp(tap.Measure, 0, lastMeasure);
        if (tap.Id is { Length: > 1 } mid && mid[0] == 'm' &&
            int.TryParse(mid.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int mIdx) && mIdx >= 0 && mIdx <= lastMeasure)
            measure = mIdx;

        // Närmaste not i takten på x, ur den senaste layoutens geometri.
        LayoutItem? best = null;
        foreach (var it in _layoutItems)
        {
            if (it.Measure != measure || it.Id.Length < 2 || it.Id[0] != 'n') continue;
            if (best == null || Math.Abs(it.X - tap.X) < Math.Abs(best.Value.X - tap.X)) best = it;
        }
        if (best != null &&
            int.TryParse(best.Value.Id.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int bi) && bi < score.Notes.Count)
        {
            var n = score.Notes[bi];
            position = new ScorePosition(measure, Math.Max(0, n.ScoreTick - score.Measures[measure].StartScoreTick));
            anchorId = best.Value.Id;
            return true;
        }

        position = new ScorePosition(measure, 0);
        anchorId = "m" + measure.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    private static int MeasureOf(Score score, int tick)
    {
        int lo = 0, hi = score.Measures.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >>> 1;
            if (score.Measures[mid].StartScoreTick <= tick) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.SnapshotChanged -= OnSnapshot;
        if (_interruptions != null) _interruptions.Interrupted -= OnInterrupted;
        _keepAwake.SetKeepScreenOn(false);
    }
}
