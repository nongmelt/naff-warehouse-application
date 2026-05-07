using Microsoft.UI.Xaml;
using System.Runtime.Versioning;
// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace app.WinUI;

[SupportedOSPlatform("windows")]

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : MauiWinUIApplication
{
	/// <summary>
	/// Initializes the singleton application object.  This is the first line of authored code
	/// executed, and as such is the logical equivalent of main() or WinMain().
	/// </summary>
	public App()
	{
		this.InitializeComponent();
		// Catch any unhandled WinUI3 exception so the process does not silently exit.
		// Log it and mark as handled to keep the app alive.
		UnhandledException += (_, e) =>
		{
			app.Services.Logger.Log($"UNHANDLED WINUI EXCEPTION: {e.Exception}");
			e.Handled = true;
		};
	}

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}

