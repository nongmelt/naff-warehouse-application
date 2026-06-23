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
				await RunRecoveryWithRetryAsync(id.Value);
			}
		});
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		// First-launch gate: with no MinIO credentials the station isn't enrolled
		// yet, so block on the enrollment page until it connects (creds arrive from
		// POST /enroll). Once present, boot straight into the shell.
		Page root;
		try
		{
			bool enrolled = !string.IsNullOrWhiteSpace(Services.AppSettings.MinioAccessKey)
						 && !string.IsNullOrWhiteSpace(Services.AppSettings.MinioSecretKey)
						 && !string.IsNullOrWhiteSpace(Services.AppSettings.MinioEndpoint);
			root = enrolled ? new AppShell() : new Views.EnrollmentPage();
		}
		catch (Exception ex)
		{
			Services.Logger.Log($"App.CreateWindow: enrollment gate failed, defaulting to shell — {ex.Message}");
			root = new AppShell();
		}

		var window = new Window(root) { Title = "Warehouse", MinimumWidth = 800 };
		window.Activated += (_, _) => _windowActive = true;
		window.Deactivated += (_, _) => _windowActive = false;
		window.Destroying += OnWindowDestroying;
		return window;
	}

	private static async Task RunRecoveryWithRetryAsync(int stationId, int maxAttempts = 3)
	{
		for (var attempt = 1; attempt <= maxAttempts; attempt++)
		{
			try
			{
				await Services.VideoWorkflowManager.RecoverAsync(stationId);
				return;
			}
			catch (Exception ex)
			{
				Services.Logger.LogError($"App: RecoverAsync attempt {attempt}/{maxAttempts} failed: {ex.Message}");
				if (attempt < maxAttempts)
					await Task.Delay(TimeSpan.FromSeconds(attempt * 10));
			}
		}
		Services.Logger.LogError($"App: RecoverAsync gave up after {maxAttempts} attempts");
	}

	private static void OnWindowDestroying(object? sender, EventArgs e)
	{
		// Dispose all stations (closes serial ports, stops camera previews) on true app exit
		if (Shell.Current?.CurrentPage is MainPage mainPage)
			mainPage.DisposeStations();
	}
}