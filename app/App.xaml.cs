using Microsoft.Extensions.DependencyInjection;
using System.Runtime.Versioning;

namespace app;

[SupportedOSPlatform("windows")]

public partial class App : Application
{
	public App()
	{
		Services.AppSettings.Initialize();
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var window = new Window(new AppShell()) { Title = "Warehouse" };
		window.Destroying += OnWindowDestroying;
		return window;
	}

	private static void OnWindowDestroying(object? sender, EventArgs e)
	{
		// Dispose all stations (closes serial ports, stops camera previews) on true app exit
		if (Shell.Current?.CurrentPage is MainPage mainPage)
			mainPage.DisposeStations();
	}
}