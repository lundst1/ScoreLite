using Microsoft.Extensions.Logging;
using ScoreLite.App.Services;
using ScoreLite.App.ViewModels;
using ScoreLite.App.Views;

namespace ScoreLite.App;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

		// §11.3: plattformstjänster bakom gränssnitt.
		builder.Services.AddSingleton<ISettings, PreferencesSettings>();
		builder.Services.AddSingleton<IKeepAwake, DisplayKeepAwake>();
		builder.Services.AddSingleton<IScoreFilePicker, MauiFilePicker>();
		builder.Services.AddSingleton<IDialogService, MauiDialogService>();
		builder.Services.AddSingleton<ScoreSession>();
		builder.Services.AddSingleton<IAppNavigation, ShellNavigation>();

		builder.Services.AddTransient<StartViewModel>();
		builder.Services.AddTransient<StartPage>();
		builder.Services.AddTransient<ScorePage>();
		builder.Services.AddTransient<LicensesPage>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
