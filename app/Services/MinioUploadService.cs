using app.Workflows;
using Minio;
using Minio.DataModel.Args;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace app.Services;

[SupportedOSPlatform("windows")]
public static class MinioUploadService
{
    private const int MaxRetries = 3;

    /// <summary>
    /// Fire-and-forget: uploads the recorded video to MinIO, updating the
    /// video record status at each stage via ApiService.
    /// </summary>
    public static void UploadAsync(int videoId, string filePath, string trackingNumber, string? @operator = null)
    {
        Task.Run(() => RunAsync(videoId, filePath, trackingNumber, @operator));
    }

    private static async Task RunAsync(int videoId, string filePath, string trackingNumber, string? @operator = null)
    {
        var op = @operator?.Replace(' ', '-');
        var endpoint = AppSettings.MinioEndpoint;
        var accessKey = AppSettings.MinioAccessKey;
        var secretKey = AppSettings.MinioSecretKey;
        var bucket = AppSettings.MinioBucket;

        if (string.IsNullOrWhiteSpace(endpoint) ||
            string.IsNullOrWhiteSpace(accessKey) ||
            string.IsNullOrWhiteSpace(secretKey) ||
            string.IsNullOrWhiteSpace(bucket))
        {
            Logger.Log($"MinioUploadService: MinIO not configured, skipping upload for video {videoId}");
            return;
        }

        if (!File.Exists(filePath))
        {
            Logger.Log($"MinioUploadService: file not found at {filePath}");
            await ApiService.UpdateVideoStatusAsync(videoId, "Failed");
            return;
        }

        var objectName = $"{DateTime.Now.ToString("yyyy-MM-dd")}/{Path.GetFileName(filePath)}";

        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                await ApiService.UpdateVideoStatusAsync(videoId, "Uploading", uploadAttempts: attempt);

                // MinIO SDK requires host:port only — strip scheme if present
                var uri = endpoint.StartsWith("http://") || endpoint.StartsWith("https://")
                    ? new Uri(endpoint)
                    : new Uri("http://" + endpoint);
                var host = uri.Authority; // "host:port" without scheme
                var useSSL = uri.Scheme == "https";

                var builder = new MinioClient()
                    .WithEndpoint(host)
                    .WithCredentials(accessKey, secretKey);
                if (useSSL)
                    builder = builder.WithSSL();
                var minio = builder.Build();

                using var stream = File.OpenRead(filePath);
                var size = stream.Length;

                await minio.PutObjectAsync(new PutObjectArgs()
                    .WithBucket(bucket)
                    .WithObject(objectName)
                    .WithStreamData(stream)
                    .WithObjectSize(size)
                    .WithContentType("video/mp4"));

                await ApiService.UpdateVideoStatusAsync(videoId, "Uploaded", uploadAttempts: attempt);
                sw.Stop();
                Logger.Log($"MinioUploadService: uploaded {objectName} ({size / 1_048_576.0:F1} MB)");

                StationEvents.Emit(
                    workflowName: "Packing",
                    stepId:       "upload_succeeded",
                    trigger:      "upload_response",
                    trackingNumber: trackingNumber,
                    fromState:    "uploading",
                    toState:      "uploaded",
                    stationId:    AppSettings.ResolvedStationId,
                    @operator:    op,
                    payload: new Dictionary<string, object?>
                    {
                        ["videoId"]            = videoId,
                        ["attempt"]            = attempt,
                        ["durationMs"]         = sw.ElapsedMilliseconds,
                        ["responseStatus"]     = "200",
                        ["videoFileSizeBytes"] = size,
                    });

                // Validate the file exists on MinIO (HEAD-style verify)
                bool exists = await ObjectExistsAsync(minio, bucket, objectName);
                await ApiService.UpdateVideoStatusAsync(videoId, exists ? "Completed" : "Failed",
                    failureReason: exists ? null : "remote_missing");

                if (exists)
                {
                    // Successful upload — if this video had previously failed and
                    // was pending re-upload, drop it from the queue.
                    ReuploadQueue.Complete(videoId);

                    var remoteFilePath = $"{bucket}/{objectName}";
                    await ApiService.UpdateVideoRemotePathAsync(videoId, remoteFilePath);
                    Logger.Log($"MinioUploadService: remote_file_path saved as {remoteFilePath}");

                    StationEvents.Emit(
                        workflowName: "Packing",
                        stepId:       "verified",
                        trigger:      "verify_remote",
                        trackingNumber: trackingNumber,
                        fromState:    "uploaded",
                        toState:      "completed",
                        stationId:    AppSettings.ResolvedStationId,
                            @operator:    op,
                        payload: new Dictionary<string, object?>
                        {
                            ["videoId"]        = videoId,
                            ["responseStatus"] = "200",
                        });
                }
                else
                {
                    Logger.Log($"MinioUploadService: post-upload validation failed for {objectName}");
                    StationEvents.Emit(
                        workflowName: "Packing",
                        stepId:       "verify_missing",
                        trigger:      "verify_remote",
                        trackingNumber: trackingNumber,
                        fromState:    "uploaded",
                        toState:      "failed",
                        stationId:    AppSettings.ResolvedStationId,
                            @operator:    op,
                        payload: new Dictionary<string, object?>
                        {
                            ["videoId"]        = videoId,
                            ["reason"]         = "remote_missing",
                            ["responseStatus"] = "404",
                        });
                    await ApiService.NotifyManualUploadNeededAsync(videoId);
                }

                return; // success
            }
            catch (Exception ex)
            {
                sw.Stop();
                Logger.Log($"MinioUploadService: attempt {attempt}/{MaxRetries} failed — {ex.Message}");

                var reason = ClassifyFailure(ex);
                var isLast = attempt >= MaxRetries;

                StationEvents.Emit(
                    workflowName: "Packing",
                    stepId:       isLast ? "upload_failed" : "upload_retry",
                    trigger:      "upload_response",
                    trackingNumber: trackingNumber,
                    fromState:    "uploading",
                    toState:      isLast ? "failed" : "uploading",
                    stationId:    AppSettings.ResolvedStationId,
                    @operator:    op,
                    payload: new Dictionary<string, object?>
                    {
                        ["videoId"]    = videoId,
                        ["attempt"]    = attempt,
                        ["durationMs"] = sw.ElapsedMilliseconds,
                        ["reason"]     = reason,
                        ["detail"]     = ex.Message,
                    });

                if (!isLast)
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 2));
            }
        }

        // All retries exhausted — persist the local path so a dashboard-initiated
        // retry (via UploadCommandListener) can find the file to re-upload.
        Logger.Log($"MinioUploadService: giving up after {MaxRetries} attempts for video {videoId}");
        await ApiService.UpdateVideoStatusAsync(videoId, "Failed",
            failureReason: "unknown", uploadAttempts: MaxRetries);
        ReuploadQueue.Enqueue(videoId, filePath, trackingNumber, "unknown");
        await ApiService.NotifyManualUploadNeededAsync(videoId);
    }

    /// <summary>
    /// Maps exceptions to stable reason codes that land in
    /// <c>workflow_events.payload.reason</c> and <c>packing_videos.failure_reason</c>.
    /// The dashboard groups retry metrics by these codes, so the mapping is the contract —
    /// keep it in sync with the list in PackingWorkflow's xmldoc.
    /// </summary>
    private static string ClassifyFailure(Exception ex)
    {
        var msg = ex.Message.ToLowerInvariant();
        if (ex is TaskCanceledException || ex is TimeoutException || msg.Contains("timeout")) return "network_timeout";
        if (msg.Contains("refused") || msg.Contains("connection"))                            return "connection_refused";
        if (msg.Contains("403") || msg.Contains("401") || msg.Contains("signature") || msg.Contains("access denied"))
                                                                                              return "auth_failure";
        if (msg.Contains("disk") || msg.Contains("space"))                                    return "disk_full";
        return "unknown";
    }

    private static async Task<bool> ObjectExistsAsync(IMinioClient minio, string bucket, string objectName)
    {
        try
        {
            await minio.StatObjectAsync(new StatObjectArgs()
                .WithBucket(bucket)
                .WithObject(objectName));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
