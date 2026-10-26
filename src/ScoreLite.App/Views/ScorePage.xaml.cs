using ScoreLite.App.Services;
using ScoreLite.App.ViewModels;
using ScoreLite.Core;

namespace ScoreLite.App.Views;

public partial class ScorePage : ContentPage
{
    private readonly ScoreSession _session;
    private readonly ScoreViewModel _vm;
    private readonly HybridWebViewScoreBridge _bridge;
    private IDispatcherTimer? _pingTimer;
    private Window? _window;
    private bool _started;

    public ScorePage(ScoreSession session, ISettings settings, IKeepAwake keepAwake)
    {
        InitializeComponent();
        _session = session;
        _bridge = new HybridWebViewScoreBridge(Web);
        _vm = new ScoreViewModel(session.Engine, _bridge, new ScoreLoader(), settings, keepAwake, session.Interruptions)
        {
            Theme = Application.Current?.RequestedTheme == AppTheme.Dark ? "dark" : "light",
        };
        BindingContext = _vm;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_started) return;
        _started = true;

        // AL-12 regel 5: mät bryggfördröjningen var 10:e sekund.
        _pingTimer = Dispatcher.CreateTimer();
        _pingTimer.Interval = TimeSpan.FromSeconds(10);
        _pingTimer.Tick += (_, _) => _vm.PingBridge();
        _pingTimer.Start();

        // R4: appen i bakgrunden pausar uppspelningen.
        _window = Window;
        if (_window != null) _window.Stopped += OnWindowStopped;

        var data = _session.Data;
        if (data == null) { await Shell.Current.GoToAsync(".."); return; }
        bool ok = await _vm.LoadAsync(data, _session.FileName);
        _session.ClearData();   // P1: partituret hålls inte kvar
        if (!ok)
        {
            await DisplayAlertAsync("Kunde inte öppna filen", _vm.ErrorMessage ?? ErrorMessages.For("NOT_A_SCORE"), "OK");
            await Shell.Current.GoToAsync("..");
        }
    }

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();
        if (_window != null) _window.Stopped -= OnWindowStopped;
        _pingTimer?.Stop();
        _vm.Dispose();
        _bridge.Dispose();
        _session.Interruptions?.Let(i => (i as IDisposable)?.Dispose());
        await _session.Engine.DisposeAsync();
    }

    private void OnWindowStopped(object? sender, EventArgs e) => _vm.OnBackgrounded();

    private void OnWebSizeChanged(object? sender, EventArgs e)
    {
        if (Web.Width > 0) _vm.OnViewportChanged((int)Web.Width);
    }

    private async void OnBackClicked(object? sender, EventArgs e) => await Shell.Current.GoToAsync("..");

    private void OnPartsClicked(object? sender, EventArgs e) => PartsSheet.IsVisible = !PartsSheet.IsVisible;

    private void OnPartCheckedChanged(object? sender, CheckedChangedEventArgs e)
    {
        // Bindningen är enkelriktad. Ett användartryck ändrar CheckBox först; VM:n avgör om ändringen är tillåten (DR-08).
        if (sender is CheckBox { BindingContext: PartItemViewModel part } && e.Value != part.IsSelected)
            _vm.TogglePartCommand.Execute(part);
    }
}

internal static class ObjectExtensions
{
    public static void Let<T>(this T value, Action<T> action) where T : class => action(value);
}
