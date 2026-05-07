using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Minio.DataModel.Args;

namespace app.Services;

public sealed record UploadProgress(int VideoId, string FileName, string Status, int Attempt);

/// <summary>
/// Creates and tracks VideoWorkflowRunner instances (one per videoId).
/// RecoverAsync restarts runners for in-flight uploads that did not complete in a
/// prior session. Failed videos are retried only via HandleRetry (dashboard command).
/// </summary>
[SupportedOSPlatform("windows")]
public static class VideoWorkflowManager
{
    private static readonly ConcurrentDictionary<int, Task> _active = new();
    private static readonly ConcurrentDictionary<int, UploadProgress> _progress = new();

    public static event Action? ProgressChanged;

    public static IReadOnlyList<UploadProgress> GetSnapshot() =>
        _progress.Values.ToList();

    public static void ReportProgress(int videoId, string fileName, string status, int attempt)
    {
        _progress[videoId] = new UploadProgress(videoId, fileName, status, attempt);
        ProgressChanged?.Invoke();
    }

    public static void RemoveProgress(int videoId)
    {
        _progress.TryRemove(videoId, out _);
        ProgressChanged?.Invoke();
    }

    /// <summary>
    /// Creates and starts a VideoWorkflowRunner in the background.
    /// No-ops if a runner for this videoId is already active.
    /// </summary>
    public static void Start(
        int     videoId,
        string  localFilePath,
        string  trackingNumber,
        string? @operator,
        int?    stationId,
        bool    isRecovery = false)
    {
        _active.AddOrUpdate(
            videoId,
            addValueFactory: id =>
            {
                var runner = new VideoWorkflowRunner(id, localFilePath, trackingNumber, @operator, stationId, isRecovery);
                return Task.Run(() => runner.RunAsync()).ContinueWith(_ => Prune(id));
            },
            updateValueFactory: (id, existing) =>
            {
                if (!existing.IsCompleted)
                {
                    Logger.Log($"[VideoWorkflowManager] video {id} already has an active runner — skipping");
                    return existing;
                }
                var runner = new VideoWorkflowRunner(id, localFilePath, trackingNumber, @operator, stationId, isRecovery);
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
    /// Scans all configured video folders for .mp4 files on disk, batch-resolves
    /// them against backend by filename + stationId, then decides per file:
    ///   - Backend status "Completed" → log as removable (manual delete for cross-check)
    ///   - Backend status "Failed" → restart video workflow
    ///   - No backend record → create record and start video workflow
    ///   - Backend status in-flight (Recorded/Uploading/Uploaded) → restart workflow
    /// Safe to call on every StationView load; skips already-active runners.
    /// </summary>
    public static async Task RecoverAsync(int? stationId)
    {
        if (stationId is null) return;

        var folders = AppSettings.VideoFolders;
        var localFiles = new List<string>();
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder)) continue;
            localFiles.AddRange(Directory.EnumerateFiles(folder, "*.mp4", SearchOption.AllDirectories));
        }

        if (localFiles.Count == 0) return;

        var fileNames = localFiles.Select(Path.GetFileName).Where(n => n is not null).ToList()!;
        var resolved = await ApiService.ResolveVideosByFileNamesAsync(stationId.Value, fileNames!);

        var byFileName = new Dictionary<string, ApiService.VideoDetail>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in resolved)
        {
            var fn = Path.GetFileName(v.FilePath);
            if (!string.IsNullOrEmpty(fn))
                byFileName.TryAdd(fn, v);
        }

        foreach (var filePath in localFiles)
        {
            var fileName = Path.GetFileName(filePath);
            if (string.IsNullOrEmpty(fileName)) continue;

            if (byFileName.TryGetValue(fileName, out var record))
            {
                if (record.Status == "Completed")
                {
                    if (string.IsNullOrWhiteSpace(record.RemoteFilePath))
                    {
                        Logger.Log($"[VideoWorkflowManager] recovery: video {record.Id} marked Completed but remote_file_path is null — restarting upload");
                        Start(record.Id, filePath, record.TrackingNumber ?? "", record.Operator, stationId, isRecovery: true);
                        continue;
                    }

                    var remoteExists = await VerifyRemoteExistsAsync(record.RemoteFilePath);
                    if (!remoteExists)
                    {
                        Logger.Log($"[VideoWorkflowManager] recovery: video {record.Id} marked Completed but remote missing ({record.RemoteFilePath}) — restarting upload");
                        Start(record.Id, filePath, record.TrackingNumber ?? "", record.Operator, stationId, isRecovery: true);
                        continue;
                    }

                    if (AppSettings.AutoDeleteCompletedVideos)
                    {
                        try
                        {
                            File.Delete(filePath);
                            Logger.Log($"[VideoWorkflowManager] recovery: video {record.Id} completed + verified — deleted: {filePath}");
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"[VideoWorkflowManager] recovery: video {record.Id} completed — delete failed: {ex.Message}");
                        }
                    }
                    continue;
                }

                if (_active.TryGetValue(record.Id, out var t) && !t.IsCompleted) continue;

                Logger.Log($"[VideoWorkflowManager] recovery: video {record.Id} status={record.Status} — restarting: {filePath}");
                Start(record.Id, filePath, record.TrackingNumber ?? "", record.Operator, stationId, isRecovery: true);
            }
            else
            {
                var trackingNumber = ParseTrackingFromFileName(Path.GetFileNameWithoutExtension(filePath));
                Logger.Log($"[VideoWorkflowManager] recovery: no backend record — creating for: {filePath}");

                var videoId = await ApiService.CreateVideoRecordAsync(
                    trackingNumber, filePath, stationId, "recovery");

                if (videoId > 0)
                    Start(videoId, filePath, trackingNumber, null, stationId, isRecovery: true);
                else
                    Logger.Log($"[VideoWorkflowManager] recovery: failed to create record for {filePath}");
            }
        }
    }

    private static async Task<bool> VerifyRemoteExistsAsync(string remoteFilePath)
    {
        try
        {
            var parts = remoteFilePath.Split('/', 2);
            if (parts.Length < 2)
            {
                Logger.Log($"[VideoWorkflowManager] VerifyRemoteExistsAsync: invalid remote path format: {remoteFilePath}");
                return false;
            }

            var bucket = parts[0];
            var objectName = parts[1];

            var minio = VideoWorkflowRunner.GetOrCreateMinioClient();

            await minio.StatObjectAsync(new StatObjectArgs()
                .WithBucket(bucket).WithObject(objectName));
            return true;
        }
        catch (Minio.Exceptions.ObjectNotFoundException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Logger.Log($"[VideoWorkflowManager] VerifyRemoteExistsAsync error for {remoteFilePath}: {ex.Message}");
            return true;
        }
    }

    /// <summary>
    /// Extracts tracking number from filename format: {yyyyMMdd_HHmmss}_{Machine}_{Station}_{Tracking}
    /// Falls back to full filename if pattern doesn't match.
    /// </summary>
    private static string ParseTrackingFromFileName(string fileNameWithoutExt)
    {
        var parts = fileNameWithoutExt.Split('_');
        // Expected: date_time_machine_station_tracking (5+ parts, tracking is last)
        return parts.Length >= 5 ? parts[^1] : fileNameWithoutExt;
    }

    private static void Prune(int videoId)
    {
        if (_active.TryGetValue(videoId, out var t) && t.IsCompleted)
            _active.TryRemove(videoId, out _);
    }
}
