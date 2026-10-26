using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace ScoreLite.App;

// UI-1: appen registreras som mottagare för .mscz (”Öppna med”). MuseScore använder application/x-musescore för .mscz
// och application/x-musescore+xml för .mscx. Många filhanterare skickar application/octet-stream, så den matchas
// bara tillsammans med filändelsen.
[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataMimeTypes = new[] { "application/x-musescore", "application/x-musescore+xml" },
    DataSchemes = new[] { "content", "file" })]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataMimeType = "application/octet-stream", DataPathPattern = ".*\\\\.mscz",
    DataSchemes = new[] { "content", "file" })]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        HandleIntent(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        HandleIntent(intent);
    }

    private void HandleIntent(Intent? intent)
    {
        if (intent?.Action != Intent.ActionView || intent.Data == null) return;
        var uri = intent.Data;
        string name = uri.LastPathSegment ?? "partitur.mscz";
        if (name.Contains('/')) name = name[(name.LastIndexOf('/') + 1)..];
        // DR-27: URI:n från ”Öppna med” läses en gång till minnet i StartViewModel.OpenAsync.
        Services.IncomingFiles.Raise(new Services.PickedFile(name, () =>
        {
            var s = ContentResolver?.OpenInputStream(uri) ?? throw new IOException("kunde inte öppna filen");
            return Task.FromResult<Stream>(s);
        }));
    }
}
