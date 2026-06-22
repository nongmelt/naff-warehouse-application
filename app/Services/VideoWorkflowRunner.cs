using app.Workflows;
using app.Workflows.Definitions;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;
using System.Diagnostics;
using System.Net;
using System.Runtime.Versioning;

namespace app.Services;

/// <summary>
/// Self-driving background task for one video's upload → verify → delete lifecycle.
/// Wraps WorkflowEngine (VideoWorkflow) for event recording; performs real I/O
/// between FireAsync calls so the engine semaphore is never held during a long upload.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VideoWorkflowRunner
{
    private static readonly Lazy<SemaphoreSlim> _uploadGate = new(() =>
    {
        var max = AppSettings.MaxConcurrentUploads;
        Logger.Log($"[VideoWorkflowRunner] upload gate initialized: max {max} concurrent uploads");
        return new SemaphoreSlim(max, max);
    });
    private static IMinioClient? _sharedMinio;
    private static readonly object _minioLock = new();

    private readonly WorkflowEngine _engine;
    private readonly WorkflowContext _ctx;
    private readonly bool _isRecovery;

    private string? _bucket;
    private string? _objectName;

    public VideoWorkflowRunner(
        int videoId,
        string localFilePath,
        string trackingNumber,
        string? @operator,
        int? stationId,
        bool isRecovery = false)
    {
        _isRecovery = isRecovery;
        _engine = new WorkflowEngine(VideoWorkflow.Build());
        _ctx = new WorkflowContext
        {
            VideoId = videoId,
            LocalFilePath = localFilePath,
            ActiveBarcode = trackingNumber,
            Operator = @operator?.Replace(' ', '-'),
            StationId = stationId,
        };
    }

    public string CurrentState => _engine.CurrentState;

    /// <summary>
    /// Runs the full upload → verify → (delete | notify-failed) lifecycle.
    /// Returns when the engine reaches "completed" or "failed".
    /// </summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        var fileName = Path.GetFileName(_ctx.LocalFilePath) ?? "unknown";

        // pending → uploading
        VideoWorkflowManager.ReportProgress(_ctx.VideoId!.Value, fileName, "Uploading", 0);
        await _engine.FireAsync(_isRecovery ? "recover" : "start", _ctx);

        // Upload loop — engine guards determine retry vs success vs exhausted
        while (_engine.CurrentState == "uploading" && !ct.IsCancellationRequested)
        {
            if (_ctx.UploadAttempt > 0)
                await BackoffAsync(_ctx.UploadAttempt, ct);

            VideoWorkflowManager.ReportProgress(_ctx.VideoId!.Value, fileName, "Uploading", _ctx.UploadAttempt + 1);

            var sw = Stopwatch.StartNew();
            try
            {
                await ApiService.UpdateVideoStatusAsync(
                    _ctx.VideoId!.Value, "Uploading", uploadAttempts: _ctx.UploadAttempt + 1);

                var (_, sizeBytes) = await DoMinioUploadAsync(ct);
                sw.Stop();

                _ctx.FailureReason = null;
                _ctx.UploadResponseStatus = "200";
                _ctx.UploadDurationMs = sw.ElapsedMilliseconds;
                _ctx.VideoFileSizeBytes = sizeBytes;

                await ApiService.UpdateVideoStatusAsync(
                    _ctx.VideoId.Value, "Uploaded", uploadAttempts: _ctx.UploadAttempt + 1);
            }
            catch (Exception ex)
            {
                sw.Stop();
                _ctx.FailureReason = ClassifyFailure(ex);
                _ctx.UploadResponseStatus = null;
                _ctx.UploadDurationMs = sw.ElapsedMilliseconds;
                VideoWorkflowManager.ReportProgress(_ctx.VideoId!.Value, fileName, $"Retry ({_ctx.UploadAttempt + 1}/3)", _ctx.UploadAttempt + 1);

                var detail = ex.InnerException is not null
                    ? $"{ex.Message} → {ex.InnerException.Message}"
                    : ex.Message;
                var extra = $" elapsed={sw.ElapsedMilliseconds}ms";
                if (ex is MinioException mex)
                    extra += $" minio_error={mex.ServerMessage ?? mex.Message}";
                if (ex is TaskCanceledException && sw.ElapsedMilliseconds < 115_000)
                    extra += " (likely HTTP client timeout, not per-attempt CTS)";
                if (ex.InnerException is HttpRequestException httpEx)
                    extra += $" http_status={httpEx.StatusCode}";

                // Walk full exception chain for hidden details
                var inner = ex.InnerException;
                while (inner is not null)
                {
                    if (inner is MinioException innerMex)
                        extra += $" inner_minio={innerMex.ServerMessage}";
                    if (inner is HttpRequestException innerHttp)
                        extra += $" inner_http={innerHttp.StatusCode}";
                    inner = inner.InnerException;
                }

                Logger.Log($"[VideoWorkflowRunner:{_ctx.VideoId}] upload attempt {_ctx.UploadAttempt + 1} failed — {detail} [{ex.GetType().Name}]{extra}");
            }

            _ctx.UploadAttempt++;
            await _engine.FireAsync("upload_response", _ctx);
        }

        if (ct.IsCancellationRequested)
        {
            Logger.Log($"[VideoWorkflowRunner:{_ctx.VideoId}] runner canceled — state={_engine.CurrentState}, attempt={_ctx.UploadAttempt}, reason={_ctx.FailureReason}");
            return;
        }

        // Verify phase
        if (_engine.CurrentState == "verifying")
        {
            VideoWorkflowManager.ReportProgress(_ctx.VideoId!.Value, fileName, "Verifying", _ctx.UploadAttempt);
            var exists = await DoMinioVerifyAsync();
            _ctx.UploadResponseStatus = exists ? "200" : "404";

            if (exists)
            {
                var remoteFilePath = $"{_bucket}/{_objectName}";
                await ApiService.UpdateVideoStatusAsync(
                    _ctx.VideoId!.Value, "Completed", remoteFilePath: remoteFilePath);
            }
            else
            {
                await ApiService.UpdateVideoStatusAsync(
                    _ctx.VideoId!.Value, "Failed", failureReason: "remote_missing");
            }

            await _engine.FireAsync("verify_remote", _ctx);
        }

        // Post-state actions
        if (_engine.CurrentState == "completed")
        {
            VideoWorkflowManager.ReportProgress(_ctx.VideoId!.Value, fileName, "Completed", _ctx.UploadAttempt);
            // Hotfix 1.4.4.1: a recovered upload that verifies Completed advances its packing
            // list to "Packed" (when still pre-Packed and present in packing_lists).
            if (_isRecovery)
                await VideoWorkflowManager.MarkPackedIfEligibleAsync(
                    _ctx.ActiveBarcode, _ctx.LocalFilePath, _ctx.StationId);
            if (AppSettings.AutoDeleteCompletedVideos)
                TryDeleteLocalFile();
            VideoWorkflowManager.RemoveProgress(_ctx.VideoId!.Value);
        }
        else if (_engine.CurrentState == "failed")
        {
            VideoWorkflowManager.ReportProgress(_ctx.VideoId!.Value, fileName, "Failed", _ctx.UploadAttempt);
            await ApiService.UpdateVideoStatusAsync(
                _ctx.VideoId!.Value, "Failed",
                failureReason: _ctx.FailureReason,
                uploadAttempts: _ctx.UploadAttempt);
            await ApiService.NotifyManualUploadNeededAsync(_ctx.VideoId!.Value);
            VideoWorkflowManager.RemoveProgress(_ctx.VideoId!.Value);
        }
    }

    /// <summary>
    /// Re-enters a failed video via retry_command trigger, then runs the upload loop.
    /// Called by VideoWorkflowManager.HandleRetry (dashboard-triggered retry).
    /// </summary>
    public async Task HandleRetryAsync(CancellationToken ct = default)
    {
        await _engine.FireAsync("retry_command", _ctx);
        if (_engine.CurrentState == "uploading")
            await RunAsync(ct);
    }

    // ── MinIO helpers ────────────────────────────────────────────────────────

    internal static IMinioClient GetOrCreateMinioClient()
    {
        if (_sharedMinio is not null) return _sharedMinio;
        lock (_minioLock)
        {
            if (_sharedMinio is not null) return _sharedMinio;

            var endpoint = AppSettings.MinioEndpoint?.Trim();
            var accessKey = AppSettings.MinioAccessKey?.Trim();
            var secretKey = AppSettings.MinioSecretKey?.Trim();

            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(accessKey) ||
                string.IsNullOrWhiteSpace(secretKey))
                throw new InvalidOperationException("MinIO not configured");

            var uri = endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                      endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    ? new Uri(endpoint) : new Uri("http://" + endpoint);
            Logger.Log($"[MinIO] connecting → endpoint={uri.Authority}, scheme={uri.Scheme}, bucket={AppSettings.MinioBucket}");
            var builder = new MinioClient()
                .WithEndpoint(uri.Authority)
                .WithCredentials(accessKey, secretKey);
            if (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
                builder = builder.WithSSL();

            _sharedMinio = builder.Build();
            return _sharedMinio;
        }
    }

    /// <summary>
    /// Drop the cached MinIO client so the next upload rebuilds it with fresh creds
    /// (called after enrollment saves new credentials). Null under the lock — do NOT
    /// dispose: enrollment runs at first launch with no upload in flight, and disposing
    /// a client another task might hold would break that upload.
    /// </summary>
    internal static void ResetMinioClient()
    {
        lock (_minioLock) { _sharedMinio = null; }
    }

    private async Task<(string objectName, long sizeBytes)> DoMinioUploadAsync(CancellationToken ct)
    {
        var bucket = AppSettings.MinioBucket?.Trim();
        if (string.IsNullOrWhiteSpace(bucket))
            throw new InvalidOperationException("MinIO bucket not configured");

        var minio = GetOrCreateMinioClient();
        var filePath = _ctx.LocalFilePath!;
        var dateDir = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(filePath)))
                         ?? DateTime.Now.ToString("yyyy-MM-dd");
        var objectName = $"{dateDir}/{Path.GetFileName(filePath)}";

        _bucket = bucket;
        _objectName = objectName;

        var gate = _uploadGate.Value;
        await gate.WaitAsync(ct);
        try
        {
            using var stream = File.OpenRead(filePath);
            var size = stream.Length;
            var max = AppSettings.MaxConcurrentUploads;
            Logger.Log($"[VideoWorkflowRunner:{_ctx.VideoId}] uploading {objectName} ({size / 1_048_576.0:F1} MB) → {bucket} [gate: {max - gate.CurrentCount}/{max} active]");
            var putResponse = await minio.PutObjectAsync(new PutObjectArgs()
                .WithBucket(bucket).WithObject(objectName)
                .WithStreamData(stream).WithObjectSize(size)
                .WithContentType("video/mp4"), ct);

            Logger.Log($"[VideoWorkflowRunner:{_ctx.VideoId}] uploaded {objectName} ({size / 1_048_576.0:F1} MB)");
            return (objectName, size);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<bool> DoMinioVerifyAsync()
    {
        if (_sharedMinio is null || _bucket is null || _objectName is null) return false;
        try
        {
            await _sharedMinio.StatObjectAsync(new StatObjectArgs()
                .WithBucket(_bucket).WithObject(_objectName));
            return true;
        }
        catch { return false; }
    }

    private void TryDeleteLocalFile()
    {
        var path = _ctx.LocalFilePath;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
            Logger.Log($"[VideoWorkflowRunner:{_ctx.VideoId}] deleted local file {path}");
        }
        catch (Exception ex)
        {
            Logger.Log($"[VideoWorkflowRunner:{_ctx.VideoId}] delete failed: {ex.Message}");
        }
    }

    private static async Task BackoffAsync(int attempt, CancellationToken ct)
    {
        var baseMs = attempt switch { 1 => 2_000, 2 => 8_000, _ => 30_000 };
        var jitter = Random.Shared.Next(-baseMs / 4, baseMs / 4);
        await Task.Delay(Math.Max(500, baseMs + jitter), ct);
    }

    private static string ClassifyFailure(Exception ex)
    {
        var msg = ex.Message.ToLowerInvariant();
        if (ex is TaskCanceledException || ex is TimeoutException || msg.Contains("timeout"))
            return "network_timeout";
        if (msg.Contains("refused") || msg.Contains("connection"))
            return "connection_refused";
        if (msg.Contains("403") || msg.Contains("401") || msg.Contains("signature") || msg.Contains("access denied"))
            return "auth_failure";
        if (msg.Contains("disk") || msg.Contains("space"))
            return "disk_full";
        return "unknown";
    }
}
