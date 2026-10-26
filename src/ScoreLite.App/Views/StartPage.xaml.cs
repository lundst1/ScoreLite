using ScoreLite.App.Services;
using ScoreLite.App.ViewModels;

namespace ScoreLite.App.Views;

public partial class StartPage : ContentPage
{
    private readonly StartViewModel _vm;

    public StartPage(StartViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        IncomingFiles.Received += OnIncoming;
        var pending = IncomingFiles.TakePending();   // fil som öppnades med ”Öppna med” innan sidan fanns
        if (pending != null) await OpenSafe(pending);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        IncomingFiles.Received -= OnIncoming;
    }

    private async void OnIncoming(PickedFile file) => await OpenSafe(file);

    private async Task OpenSafe(PickedFile file)
    {
        try { await _vm.OpenAsync(file); }
        catch (Exception e)   // R1
        {
            System.Diagnostics.Debug.WriteLine("OpenSafe: " + e);   // bara i Debug-bygge; innehåller aldrig filinnehåll (§12.2)
            await DisplayAlertAsync("Kunde inte öppna filen", ErrorMessages.For("NOT_A_SCORE"), "OK");
        }
    }

    private async void OnLicensesTapped(object? sender, TappedEventArgs e) => await Shell.Current.GoToAsync("licenses");
}
