using app.Workflows;
using app.Workflows.Definitions;
using Minio;
using Minio.DataModel.Args;
using System.Diagnostics;
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
    private readonly WorkflowEngine  _engine;
    private readonly WorkflowContext _ctx;

    // MinIO state set during upload, reused for verify
    private IMinioClient? _minio;
    private string?       _bucket;
    private string?       _objectName;

    public VideoWorkflowRunner(
        int     videoId,
        string  localFilePath,
        string  trackingNumber,
        string? @operator,
        int?    stationId)
    {
        _engine = new WorkflowEngine(VideoWorkflow.Build());
        _ctx    = new WorkflowContext
        {
            VideoId       = videoId,
            LocalFilePath = localFilePath,
            ActiveBarcode = trackingNumber,
            Operator      = @operator?.Replace(' ', '-'),
            StationId     = stationId,
        };
    }

    public string CurrentState => _engine.CurrentState;

    /// <summary>
    /// Runs the full upload → verify → (delete | notify-failed) lifecycle.
    /// Returns when the engine reaches "completed" or "failed".
    /// </summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        // pending → uploading
        await _engine.FireAsync("start", _ctx);

        // Upload loop — engine guards determine retry vs success vs exhausted
        while (_engine.CurrentState == "uploading" && !ct.IsCancellationRequested)
        {
            if (_ctx.UploadAttempt > 0)
                await BackoffAsync(_ctx.UploadAttempt, ct);

            var sw = Stopwatch.StartNew();
            try
            {
                await ApiService.UpdateVideoStatusAsync(
                    _ctx.VideoId!.Value, "Uploading", uploadAttempts: _ctx.UploadAttempt + 1);

                var (_, sizeBytes) = await DoMinioUploadAsync(ct);
                sw.Stop();

                _ctx.FailureReason        = null;
                _ctx.UploadResponseStatus = "200";
                _ctx.UploadDurationMs     = sw.ElapsedMilliseconds;
                _ctx.VideoFileSizeBytes   = sizeBytes;

                await ApiService.UpdateVideoStatusAsync(
                    _ctx.VideoId.Value, "Uploaded", uploadAttempts: _ctx.UploadAttempt + 1);
            }
            catch (Exception ex)
            {
                sw.Stop();
                _ctx.FailureReason        = ClassifyFailure(ex);
                _ctx.UploadResponseStatus = null;
                _ctx.UploadDurationMs     = sw.ElapsedMilliseconds;
                Logger.Log($"[VideoWorkflowRunner:{_ctx.VideoId}] upload attempt {_ctx.UploadAttempt + 1} failed — {ex.Message}");
            }

            _ctx.UploadAttempt++;
            await _engine.FireAsync("upload_response", _ctx);
        }

        if (ct.IsCancellationRequested) return;

        // Verify phase
        if (_engine.CurrentState == "verifying")
        {
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
            TryDeleteLocalFile();
        }
        else if (_engine.CurrentState == "failed")
        {
            await ApiService.UpdateVideoStatusAsync(
                _ctx.VideoId!.Value, "Failed",
                failureReason: _ctx.FailureReason,
                uploadAttempts: _ctx.UploadAttempt);
            await ApiService.NotifyManualUploadNeededAsync(_ctx.VideoId!.Value);
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

    private async Task<(string objectName, long sizeBytes)> DoMinioUploadAsync(CancellationToken ct)
    {
        var endpoint  = AppSettings.MinioEndpoint?.Trim();
        var accessKey = AppSettings.MinioAccessKey?.Trim();
        var secretKey = AppSettings.MinioSecretKey?.Trim();
        var bucket    = AppSettings.MinioBucket?.Trim();

        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(accessKey) ||
            string.IsNullOrWhiteSpace(secretKey) || string.IsNullOrWhiteSpace(bucket))
            throw new InvalidOperationException("MinIO not configured");

        var filePath   = _ctx.LocalFilePath!;
        var objectName = $"{DateTime.Now:yyyy-MM-dd}/{Path.GetFileName(filePath)}";

        var uri    = endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                   ? new Uri(endpoint) : new Uri("http://" + endpoint);
        var builder = new MinioClient()
            .WithEndpoint(uri.Authority)
            .WithCredentials(accessKey, secretKey);
        if (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            builder = builder.WithSSL();

        _minio      = builder.Build();
        _bucket     = bucket;
        _objectName = objectName;

        using var stream = File.OpenRead(filePath);
        var size = stream.Length;
        await _minio.PutObjectAsync(new PutObjectArgs()
            .WithBucket(bucket).WithObject(objectName)
            .WithStreamData(stream).WithObjectSize(size)
            .WithContentType("video/mp4"), ct);

        Logger.Log($"[VideoWorkflowRunner:{_ctx.VideoId}] uploaded {objectName} ({size / 1_048_576.0:F1} MB)");
        return (objectName, size);
    }

    private async Task<bool> DoMinioVerifyAsync()
    {
        if (_minio is null || _bucket is null || _objectName is null) return false;
        try
        {
            await _minio.StatObjectAsync(new StatObjectArgs()
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
