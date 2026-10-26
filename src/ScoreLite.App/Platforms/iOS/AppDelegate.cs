// Kompilerar i CI (macOS, decisions #34). Beteendet på iPhone är OVERIFIERAT: se docs/device-checklist.md.
using Foundation;
using ScoreLite.App.Services;
using UIKit;

namespace ScoreLite.App;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

	// UI-1: ”Öppna med” för .mscz. DR-27: åtkomsten via security-scoped URL hålls bara under läsningen.
	public override bool OpenUrl(UIApplication application, NSUrl url, NSDictionary options)
	{
		if (url == null || !url.IsFileUrl) return false;
		string name = url.LastPathComponent ?? "partitur.mscz";
		IncomingFiles.Raise(new PickedFile(name, () =>
		{
			bool scoped = url.StartAccessingSecurityScopedResource();
			try
			{
				var data = NSData.FromUrl(url) ?? throw new IOException("kunde inte läsa filen");
				return Task.FromResult<Stream>(new MemoryStream(data.ToArray(), writable: false));
			}
			finally { if (scoped) url.StopAccessingSecurityScopedResource(); }
		}));
		return true;
	}
}
