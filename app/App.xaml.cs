using Microsoft.Extensions.DependencyInjection;
using System.Runtime.Versioning;

namespace app;

[SupportedOSPlatform("windows")]

public partial class App : Application
{
	private static volatile bool _windowActive = true;

	public static bool IsWindowActive => _windowActive;

	public App()
	{
		UserAppTheme = AppTheme.Light;
		Services.AppSettings.Initialize();
		Services.UploadCommandListener.Start();
		_ = Task.Run(async () =>
		{
			var id = await Services.ApiService.ResolveStationIdAsync(Environment.MachineName);
			Services.AppSettings.CompleteStationResolution(id);
			Services.Logger.Log($"App: station resolved → id={id}");
			if (id is not null)
			{
				Services.StationWsClient.Start(id.Value);
				await Services.VideoWorkflowManager.RecoverAsync(id);
			}
		});
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var window = new Window(new AppShell()) { Title = "Warehouse" };
		window.Activated += (_, _) => _windowActive = true;
		window.Deactivated += (_, _) => _windowActive = false;
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