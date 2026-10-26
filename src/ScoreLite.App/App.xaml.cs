using ScoreLite.App.Services;

namespace ScoreLite.App;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
		SoundFontProvider.Preload();   // klar när användaren hunnit välja en fil (K14)
	}

	protected override Window CreateWindow(IActivationState? activationState) => new Window(new AppShell());
}
