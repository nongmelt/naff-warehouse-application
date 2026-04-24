using System.Runtime.Versioning;

namespace app.Services;

/// <summary>
/// Sends POST /stations/{id}/heartbeat every second so the frontend dashboard
/// can show live online/offline status per station.
/// Waits for station ID resolution before starting — safe to call from App constructor.
/// </summary>
[SupportedOSPlatform("windows")]
public static class HeartbeatService
{
    private static System.Timers.Timer? _timer;

    public static void Start()
    {
        _ = Task.Run(async () =>
        {
            var id = await AppSettings.StationIdReady;
            if (id is null)
            {
                Logger.Log("HeartbeatService: station ID not resolved, heartbeat disabled");
                return;
            }

            _timer = new System.Timers.Timer(1000) { AutoReset = true };
            _timer.Elapsed += async (_, _) =>
            {
                await ApiService.SendHeartbeatAsync(id.Value);
            };
            _timer.Start();
            Logger.Log($"HeartbeatService: started for station {id}");
        });
    }

    public static void Stop()
    {
        _timer?.Stop();
        _timer?.Dispose();
        _timer = null;
    }
}
