namespace ScoreLite.App.Services;

/// <summary>En vald fil. Innehållet läses en gång till minnet (DR-27).</summary>
public sealed record PickedFile(string Name, Func<Task<Stream>> OpenAsync);

public interface IScoreFilePicker
{
    /// <summary>Null om användaren avbryter.</summary>
    Task<PickedFile?> PickAsync();
}

public interface IKeepAwake { void SetKeepScreenOn(bool on); }   // DR-22

/// <summary>DR-25: bara inställningar sparas, aldrig partitur.</summary>
public interface ISettings
{
    bool MetronomeEnabled { get; set; }
    bool CountInEnabled { get; set; }
    int ZoomLevel { get; set; }
    bool FollowEnabled { get; set; }
}

public interface IDialogService
{
    Task ShowErrorAsync(string title, string message);
}

/// <summary>Navigering mellan startskärm och partiturskärm (§11.1).</summary>
public interface IAppNavigation
{
    Task ShowScoreAsync(byte[] data, string fileName);
    Task ShowStartAsync();
}

/// <summary>R4: ljudfokus förlorat, hörlurar utdragna eller appen i bakgrunden.</summary>
public interface IAudioInterruptions { event Action Interrupted; }

public sealed class InMemorySettings : ISettings
{
    public bool MetronomeEnabled { get; set; }
    public bool CountInEnabled { get; set; }
    public int ZoomLevel { get; set; } = 2;
    public bool FollowEnabled { get; set; } = true;
}
