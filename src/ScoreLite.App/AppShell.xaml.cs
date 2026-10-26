using ScoreLite.App.Views;

namespace ScoreLite.App;

public partial class AppShell : Shell
{
	public AppShell()
	{
		InitializeComponent();
		Routing.RegisterRoute("score", typeof(ScorePage));
		Routing.RegisterRoute("licenses", typeof(LicensesPage));
	}
}
