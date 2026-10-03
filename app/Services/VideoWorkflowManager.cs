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
    private const int ResolveBatchSize = 200;
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
        bool    isRecovery = false,
        bool    isOrphan   = false)
    {
        _active.AddOrUpdate(
            videoId,
            addValueFactory: id =>
            {
                var runner = new VideoWorkflowRunner(id, localFilePath, trackingNumber, @operator, stationId, isRecovery, isOrphan);
                return Task.Run(() => runner.RunAsync()).ContinueWith(_ => Prune(id));
            },
            updateValueFactory: (id, existing) =>
            {
                if (!existing.IsCompleted)
                {
                    Logger.Log($"[VideoWorkflowManager] video {id} already has an active runner — skipping");
                    return existing;
                }
                var runner = new VideoWorkflowRunner(id, localFilePath, trackingNumber, @operator, stationId, isRecovery, isOrphan);
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

        Logger.Log($"[VideoWorkflowManager] recovery: starting for station {stationId}");

        var folders = AppSettings.VideoFolders;
        var localFiles = new List<string>();
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder)) continue;
            localFiles.AddRange(Directory.EnumerateFiles(folder, "*.mp4", SearchOption.AllDirectories));
        }

        if (localFiles.Count == 0)
        {
            Logger.Log("[VideoWorkflowManager] recovery: no local .mp4 files found");
            return;
        }

        var fileNames = localFiles.Select(Path.GetFileName).Where(n => n is not null).Cast<string>().ToList();
        var resolved = new List<ApiService.VideoDetail>();
        foreach (var batch in fileNames.Chunk(ResolveBatchSize))
        {
            var batchList = batch.ToList();
            var partial = await ApiService.ResolveVideosByFileNamesAsync(stationId.Value, batchList);
            resolved.AddRange(partial);
        }

        var byFileName = new Dictionary<string, ApiService.VideoDetail>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in resolved)
        {
            var fn = Path.GetFileName(v.FilePath);
            if (!string.IsNullOrEmpty(fn))
                byFileName.TryAdd(fn, v);
        }

        int recovered = 0, skipped = 0, completed = 0, failed = 0;

        foreach (var filePath in localFiles)
        {
            try
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
                            recovered++;
                            continue;
                        }

                        var remoteExists = await VerifyRemoteExistsAsync(record.RemoteFilePath);
                        if (!remoteExists)
                        {
                            Logger.Log($"[VideoWorkflowManager] recovery: video {record.Id} marked Completed but remote missing ({record.RemoteFilePath}) — restarting upload");
                            Start(record.Id, filePath, record.TrackingNumber ?? "", record.Operator, stationId, isRecovery: true);
                            recovered++;
                            continue;
                        }

                        // Hotfix 1.4.4.1: video already Completed + remote verified — sync a
                        // pre-Packed list to "Packed" (no re-upload; the video is already done).
                        await MarkPackedIfEligibleAsync(record.TrackingNumber, filePath, stationId);

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
                        completed++;
                        continue;
                    }

                    if (_active.TryGetValue(record.Id, out var t) && !t.IsCompleted)
                    {
                        skipped++;
                        continue;
                    }

                    Logger.Log($"[VideoWorkflowManager] recovery: video {record.Id} status={record.Status} — restarting: {filePath}");
                    Start(record.Id, filePath, record.TrackingNumber ?? "", record.Operator, stationId, isRecovery: true);
                    recovered++;
                }
                else
                {
                    var stem = Path.GetFileNameWithoutExtension(filePath);
                    var trackingNumber = ParseTrackingFromFileName(stem);
                    var operatorLabel = ParseStationLabelFromFileName(stem);

                    // SearchAsync is a fuzzy q-search; require an exact case-insensitive
                    // TrackingNumber match to treat the file as recoverable against an
                    // existing packing_lists row. SearchResultAsync distinguishes a
                    // genuine empty response from an errored call so we never orphan on
                    // an API outage (decision #3).
                    var search = await ApiService.SearchResultAsync(trackingNumber);
                    if (!search.Succeeded)
                    {
                        Logger.Log($"[VideoWorkflowManager] recovery: search errored for '{trackingNumber}' — leaving on disk: {filePath}");
                        skipped++;
                        continue;
                    }

                    var packingListExists = search.Results.Any(p =>
                        string.Equals(p.TrackingNumber, trackingNumber, StringComparison.OrdinalIgnoreCase));

                    if (packingListExists)
                    {
                        Logger.Log($"[VideoWorkflowManager] recovery: no backend record but packing list found — creating for: {filePath}");
                        var videoId = await ApiService.CreateVideoRecordAsync(
                            trackingNumber, filePath, stationId, operatorLabel);

                        if (videoId > 0)
                        {
                            // packing_lists status is synced to Packed on completion (VideoWorkflowRunner).
                            Start(videoId, filePath, trackingNumber, operatorLabel, stationId, isRecovery: true);
                            recovered++;
                        }
                        else
                        {
                            Logger.Log($"[VideoWorkflowManager] recovery: failed to create record for {filePath}");
                            failed++;
                        }
                    }
                    else
                    {
                        // Succeeded AND empty (definitive: no packing_lists row) → orphan path.
                        // Register the orphan row, then run the shared workflow against
                        // warehouse-raw (isOrphan: true). The register is idempotent on
                        // object_key and the MinIO PUT overwrites, so re-running recovery
                        // is safe.
                        DateTime recordedAtUtc;
                        try { recordedAtUtc = File.GetLastWriteTimeUtc(filePath); }
                        catch { recordedAtUtc = DateTime.UtcNow; }

                        var rawBucket = AppSettings.MinioRawBucket?.Trim();
                        if (string.IsNullOrWhiteSpace(rawBucket))
                        {
                            Logger.Log($"[VideoWorkflowManager] recovery: raw bucket not configured — leaving on disk: {filePath}");
                            skipped++;
                        }
                        else
                        {
                            var objectKey = OrphanCapture.BuildRawObjectKey(filePath, DateTime.Now);
                            long fileSize = 0;
                            try { fileSize = new FileInfo(filePath).Length; } catch { /* best-effort */ }

                            var orphanId = await ApiService.CreateOrphanVideoAsync(
                                objectKey, rawBucket, trackingNumber, stationId, operatorLabel,
                                filePath, Path.GetFileName(filePath), recordedAtUtc, fileSize);

                            if (orphanId > 0)
                            {
                                Start(orphanId, filePath, trackingNumber, operatorLabel, stationId,
                                      isRecovery: true, isOrphan: true);
                                Logger.Log($"[VideoWorkflowManager] recovery: '{trackingNumber}' not in packing_lists — orphan workflow started: {filePath}");
                                recovered++;
                            }
                            else
                            {
                                Logger.Log($"[VideoWorkflowManager] recovery: orphan register failed for '{trackingNumber}' — leaving on disk: {filePath}");
                                skipped++;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"[VideoWorkflowManager] recovery: unhandled error processing {filePath}: {ex.Message}");
                failed++;
            }
        }

        Logger.Log($"[VideoWorkflowManager] recovery: done — scanned={localFiles.Count}, recovered={recovered}, completed={completed}, skipped={skipped}, failed={failed}");
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

    /// <summary>
    /// Extracts the station label from filename format:
    /// {yyyyMMdd}_{HHmmss}_{Machine}_{Station}_{Tracking}. Index 3 is the station label
    /// (e.g. "Station-1"), already sanitized at record time. Used as the operator / packed_by
    /// for recovered videos. Falls back to "recovery" if the pattern doesn't match.
    /// </summary>
    private static string ParseStationLabelFromFileName(string fileNameWithoutExt)
    {
        var parts = fileNameWithoutExt.Split('_');
        // Expected: date_time_machine_station_tracking (5+ parts, station label is index 3)
        return parts.Length >= 5 ? parts[3] : "recovery";
    }

    /// <summary>
    /// If the video's tracking number exists in packing_lists and the list is still pre-Packed
    /// (To be packed / QC Hold / QC Passed / Packing), advance it to "Packed". Called once a
    /// recovered video is confirmed Completed (remote verified). No-op for unknown tracking or a
    /// list already past packing. packed_by / station come from the recovery file's station label.
    /// </summary>
    public static async Task MarkPackedIfEligibleAsync(string? trackingNumber, string? localFilePath, int? stationId)
    {
        if (string.IsNullOrWhiteSpace(trackingNumber)) return;

        var matches = await ApiService.SearchAsync(trackingNumber);
        var match = matches.FirstOrDefault(p =>
            string.Equals(p.TrackingNumber, trackingNumber, StringComparison.OrdinalIgnoreCase));
        if (match is null) return;

        var status = match.PackingStatus?.Trim();
        var eligible =
            string.Equals(status, "To be packed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "QC Hold",      StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "QC Passed",    StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "Packing",      StringComparison.OrdinalIgnoreCase);
        if (!eligible)
        {
            Logger.Log($"[VideoWorkflowManager] recovery: packing list '{trackingNumber}' status '{status}' not eligible for Packed — leaving as-is");
            return;
        }

        var operatorLabel = ParseStationLabelFromFileName(Path.GetFileNameWithoutExtension(localFilePath ?? ""));
        var ok = await ApiService.UpdatePackingStatusByScanAsync(
            trackingNumber, "Packed", operatorLabel, packingStationId: stationId);
        Logger.Log($"[VideoWorkflowManager] recovery: packing list '{trackingNumber}' {status} → Packed ({(ok ? "ok" : "failed")})");
    }

    private static void Prune(int videoId)
    {
        if (_active.TryGetValue(videoId, out var t) && t.IsCompleted)
            _active.TryRemove(videoId, out _);
    }
}
