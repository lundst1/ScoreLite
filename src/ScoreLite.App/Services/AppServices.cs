using MeltySynth;
using ScoreLite.App.Services;
using ScoreLite.Audio;
using ScoreLite.Core;

namespace ScoreLite.App.Services;

/// <summary>”Öppna med” från andra appar (UI-1). Filer som kommer innan startsidan lyssnar sparas tills den gör det.</summary>
public static class IncomingFiles
{
    private static PickedFile? _pending;
    public static event Action<PickedFile>? Received;

    public static void Raise(PickedFile file)
    {
        var handler = Received;
        if (handler == null) _pending = file;
        else MainThread.BeginInvokeOnMainThread(() => handler(file));
    }

    public static PickedFile? TakePending()
    {
        var f = _pending;
        _pending = null;
        return f;
    }
}

/// <summary>DR-25: inställningar lokalt i plattformens Preferences.</summary>
public sealed class PreferencesSettings : ISettings
{
    public bool MetronomeEnabled { get => Preferences.Default.Get(nameof(MetronomeEnabled), false); set => Preferences.Default.Set(nameof(MetronomeEnabled), value); }
    public bool CountInEnabled { get => Preferences.Default.Get(nameof(CountInEnabled), false); set => Preferences.Default.Set(nameof(CountInEnabled), value); }
    public int ZoomLevel { get => Preferences.Default.Get(nameof(ZoomLevel), 2); set => Preferences.Default.Set(nameof(ZoomLevel), value); }
    public bool FollowEnabled { get => Preferences.Default.Get(nameof(FollowEnabled), true); set => Preferences.Default.Set(nameof(FollowEnabled), value); }
}

/// <summary>DR-22: skärmen hålls tänd under uppspelning.</summary>
public sealed class DisplayKeepAwake : IKeepAwake
{
    public void SetKeepScreenOn(bool on) => MainThread.BeginInvokeOnMainThread(() => DeviceDisplay.Current.KeepScreenOn = on);
}

public sealed class MauiFilePicker : IScoreFilePicker
{
    public async Task<PickedFile?> PickAsync()
    {
        var types = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
        {
            // Android och iOS har ingen egen typ för .mscz. Alla filer visas och innehållet avgör (ErrorMessages NOT_A_SCORE).
            { DevicePlatform.Android, new[] { "*/*" } },
            { DevicePlatform.iOS, new[] { "public.data" } },
        });
        var result = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Välj ett MuseScore-partitur (.mscz)", FileTypes = types });
        return result == null ? null : new PickedFile(result.FileName, () => result.OpenReadAsync());
    }
}

public sealed class MauiDialogService : IDialogService
{
    public Task ShowErrorAsync(string title, string message) =>
        MainThread.InvokeOnMainThreadAsync(() => Shell.Current.DisplayAlertAsync(title, message, "OK"));
}

/// <summary>Delar partituret och motorn mellan startsidan och partitursidan.</summary>
public sealed class ScoreSession
{
    public byte[]? Data { get; private set; }
    public string FileName { get; private set; } = "";
    public IPlaybackEngine Engine { get; private set; } = new NullPlaybackEngine();
    public IAudioInterruptions? Interruptions { get; private set; }

    /// <summary>P1: partituret hålls inte kvar i minnet efter laddningen.</summary>
    public void ClearData() => Data = null;

    public void Set(byte[] data, string name, IPlaybackEngine engine, IAudioInterruptions? interruptions)
    {
        Data = data; FileName = name; Engine = engine; Interruptions = interruptions;
    }
}

/// <summary>Läser den paketerade ljudfonten en gång i bakgrunden (K14). Delas av alla motorer (V9).</summary>
public static class SoundFontProvider
{
    private static Task<SoundFont>? _task;

    public static Task<SoundFont> GetAsync() => _task ??= Task.Run(async () =>
    {
        using var asset = await FileSystem.OpenAppPackageFileAsync("soundfont.sf2");
        // MeltySynth läser med Position, så strömmen måste vara sökbar.
        var ms = new MemoryStream();
        await asset.CopyToAsync(ms);
        ms.Position = 0;
        return new SoundFont(ms);
    });

    /// <summary>Startar laddningen vid appstart så att den är klar när användaren valt en fil.</summary>
    public static void Preload() => _ = GetAsync().ContinueWith(_ => { }, TaskScheduler.Default);
}

/// <summary>SynchronizationContext som levererar snapshots på UI-tråden (§7.4).</summary>
public sealed class MainThreadContext : SynchronizationContext
{
    public override void Post(SendOrPostCallback d, object? state) => MainThread.BeginInvokeOnMainThread(() => d(state));
}

public static class PlaybackEngineFactory
{
    /// <summary>Skapar motor och avbrottskälla. Vid fel i ljud eller ljudfont returneras <see cref="NullPlaybackEngine"/> (§12.3).</summary>
    public static async Task<(IPlaybackEngine engine, IAudioInterruptions? interruptions)> CreateAsync()
    {
        try
        {
            var font = await SoundFontProvider.GetAsync();
            var output = PlatformAudio.CreateOutput();
            var engine = new PlaybackEngine(output, new MeltySynthHost(font, output.SampleRate),
                new EngineOptions { Context = new MainThreadContext() });
            return (engine, PlatformAudio.CreateInterruptions());
        }
        catch (Exception)
        {
            return (new NullPlaybackEngine(), null);
        }
    }
}

public sealed class ShellNavigation : IAppNavigation
{
    private readonly ScoreSession _session;
    public ShellNavigation(ScoreSession session) => _session = session;

    public async Task ShowScoreAsync(byte[] data, string fileName)
    {
        var (engine, interruptions) = await PlaybackEngineFactory.CreateAsync();
        _session.Set(data, fileName, engine, interruptions);
        await Shell.Current.GoToAsync("score");
    }

    public Task ShowStartAsync() => Shell.Current.GoToAsync("..");
}
