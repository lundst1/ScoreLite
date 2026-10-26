namespace ScoreLite.App.Views;

public partial class LicensesPage : ContentPage
{
    public LicensesPage()
    {
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        FontLicense.Text = await ReadAsset("GeneralUser-GS-LICENSE.txt");
        SynthLicense.Text = await ReadAsset("MeltySynth-LICENSE.txt");
        VerovioLicense.Text = await ReadAsset("wwwroot/verovio/LICENSE.txt");
    }

    private static async Task<string> ReadAsset(string name)
    {
        try
        {
            using var s = await FileSystem.OpenAppPackageFileAsync(name);
            using var r = new StreamReader(s);
            return await r.ReadToEndAsync();
        }
        catch (Exception) { return "(licenstexten kunde inte läsas)"; }
    }
}
