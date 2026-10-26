using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScoreLite.App.Services;

namespace ScoreLite.App.ViewModels;

/// <summary>§11.3. Filval och start av laddning (UI-1).</summary>
public sealed partial class StartViewModel : ObservableObject
{
    /// <summary>S2/DR-27: filen läses en gång till minnet, högst 50 MB.</summary>
    public const long MaxFileBytes = 50L * 1024 * 1024;

    private readonly IScoreFilePicker _picker;
    private readonly IAppNavigation _navigation;
    private readonly IDialogService _dialogs;

    public StartViewModel(IScoreFilePicker picker, IAppNavigation navigation, IDialogService dialogs)
    {
        _picker = picker;
        _navigation = navigation;
        _dialogs = dialogs;
    }

    [ObservableProperty] private bool _isBusy;
    public bool IsIdle => !IsBusy;
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsIdle));

    [RelayCommand]
    private async Task OpenFileAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var file = await _picker.PickAsync();
            if (file == null) return;
            await OpenAsync(file);
        }
        catch (Exception)
        {
            await _dialogs.ShowErrorAsync("Kunde inte öppna filen", ErrorMessages.For("NOT_A_SCORE"));   // R1
        }
        finally { IsBusy = false; }
    }

    /// <summary>Också ingången för ”Öppna med” från andra appar.</summary>
    public async Task OpenAsync(PickedFile file)
    {
        byte[] data;
        try
        {
            await using var s = await file.OpenAsync();
            data = await ReadBoundedAsync(s);
        }
        catch (InvalidDataException)
        {
            await _dialogs.ShowErrorAsync("Kunde inte öppna filen", ErrorMessages.For("LIMIT_EXCEEDED"));
            return;
        }
        // Filhandtaget är släppt här (DR-27).
        await _navigation.ShowScoreAsync(data, file.Name);
    }

    internal static async Task<byte[]> ReadBoundedAsync(Stream s)
    {
        if (s.CanSeek && s.Length > MaxFileBytes) throw new InvalidDataException("för stor");
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = await s.ReadAsync(buf)) > 0)
        {
            if (ms.Length + n > MaxFileBytes) throw new InvalidDataException("för stor");
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }
}
