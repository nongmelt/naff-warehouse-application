using System.Collections.Concurrent;
using System.Runtime.Versioning;

namespace app.Services;

/// <summary>
/// Creates and tracks VideoWorkflowRunner instances (one per videoId).
/// RecoverAsync restarts runners for in-flight uploads that did not complete in a
/// prior session. Failed videos are retried only via HandleRetry (dashboard command).
/// </summary>
[SupportedOSPlatform("windows")]
public static class VideoWorkflowManager
{
    private static readonly ConcurrentDictionary<int, Task> _active = new();

    /// <summary>
    /// Creates and starts a VideoWorkflowRunner in the background.
    /// No-ops if a runner for this videoId is already active.
    /// </summary>
    public static void Start(
        int     videoId,
        string  localFilePath,
        string  trackingNumber,
        string? @operator,
        int?    stationId)
    {
        _active.AddOrUpdate(
            videoId,
            addValueFactory: id =>
            {
                var runner = new VideoWorkflowRunner(id, localFilePath, trackingNumber, @operator, stationId);
                return Task.Run(() => runner.RunAsync()).ContinueWith(_ => Prune(id));
            },
            updateValueFactory: (id, existing) =>
            {
                if (!existing.IsCompleted)
                {
                    Logger.Log($"[VideoWorkflowManager] video {id} already has an active runner — skipping");
                    return existing;
                }
                var runner = new VideoWorkflowRunner(id, localFilePath, trackingNumber, @operator, stationId);
                return Task.Run(() => runner.RunAsync()).ContinueWith(_ => Prune(id));
            });
    }

    /// <summary>
    /// Dashboard-triggered retry for a video that reached the failed state.
    /// Called by UploadCommandListener instead of MinioUploadService.
    /// </summary>
    public static void HandleRetry(int videoId, string localFilePath, string trackingNumber, int? stationId)
    {
        _active.AddOrUpdate(
            videoId,
            addValueFactory: id =>
            {
                var runner = new VideoWorkflowRunner(id, localFilePath, trackingNumber, null, stationId);
                return Task.Run(() => runner.HandleRetryAsync()).ContinueWith(_ => Prune(id));
            },
            updateValueFactory: (id, existing) =>
            {
                if (!existing.IsCompleted)
                {
                    Logger.Log($"[VideoWorkflowManager] retry for video {id} skipped — runner already active");
                    return existing;
                }
                var runner = new VideoWorkflowRunner(id, localFilePath, trackingNumber, null, stationId);
                return Task.Run(() => runner.HandleRetryAsync()).ContinueWith(_ => Prune(id));
            });
    }

    /// <summary>
    /// Queries the backend for videos with status Recorded/Uploading/Uploaded (crashed
    /// mid-flight) and starts a runner for each whose local file still exists on disk.
    /// Failed videos are excluded — those require an explicit dashboard retry command.
    /// Safe to call on every StationView load; skips already-active runners.
    /// </summary>
    public static async Task RecoverAsync(int? stationId)
    {
        if (stationId is null) return;

        var pending = await ApiService.GetPendingVideosForStationAsync(stationId.Value);
        foreach (var v in pending)
        {
            if (_active.TryGetValue(v.Id, out var t) && !t.IsCompleted) continue;
            if (string.IsNullOrEmpty(v.FilePath) || !File.Exists(v.FilePath))
            {
                Logger.Log($"[VideoWorkflowManager] recovery: video {v.Id} — file not found at {v.FilePath}");
                continue;
            }
            Logger.Log($"[VideoWorkflowManager] recovery: restarting video {v.Id} ({v.FilePath})");
            Start(v.Id, v.FilePath, v.TrackingNumber ?? "", v.Operator, stationId);
        }
    }

    private static void Prune(int videoId)
    {
        if (_active.TryGetValue(videoId, out var t) && t.IsCompleted)
            _active.TryRemove(videoId, out _);
    }
}
