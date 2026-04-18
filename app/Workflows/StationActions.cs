using System.Runtime.Versioning;
using app.Services;

namespace app.Workflows;

/// <summary>
/// Named <see cref="Func{WorkflowContext, Task}"/> factories that wrap the
/// existing <see cref="ApiService"/> calls and station-side helpers. Workflow
/// definitions reference these so the machine reads as prose. Each helper
/// returns a <c>Func&lt;WorkflowContext, Task&gt;</c> so the definition can
/// bind parameters at build time (e.g. the status string) and delay
/// execution until <see cref="WorkflowEngine.FireAsync"/> fires.
/// </summary>
[SupportedOSPlatform("windows")]
public static class StationActions
{
    // ── No-op / utility ──────────────────────────────────────────────────────

    /// <summary>
    /// A step that does nothing at runtime. Useful when the "action" is just
    /// the act of emitting the event (e.g. a `barcode_mismatch` observation
    /// that is informational, not side-effecting).
    /// </summary>
    public static Func<WorkflowContext, Task> Noop =>
        _ => Task.CompletedTask;

    // ── Shared ────────────────────────────────────────────────────────────────

    /// <summary>
    /// PATCH packing status by scan input. The <see cref="WorkflowContext.Barcode"/>
    /// is used as the scan key; callers should populate it before firing.
    /// </summary>
    public static Func<WorkflowContext, Task> PushStatusByScan(string status) =>
        async ctx =>
        {
            if (string.IsNullOrWhiteSpace(ctx.Barcode)) return;
            await ApiService.UpdatePackingStatusByScanAsync(ctx.Barcode, status, ctx.PackedBy);
        };

    // ── Packing / Video ──────────────────────────────────────────────────────

    /// <summary>
    /// Caller wires the actual start-recording + create-video-record work
    /// (both live on <c>StationView</c> and need access to the capture
    /// element). The workflow only needs a hook-point; StationView will
    /// inject its own delegate via <see cref="SetStationRecorder"/>.
    /// </summary>
    public static Func<WorkflowContext, Task> StartRecording =>
        ctx => _recorder?.StartAsync(ctx) ?? Task.CompletedTask;

    public static Func<WorkflowContext, Task> StopAndUpload =>
        ctx => _recorder?.StopAndUploadAsync(ctx) ?? Task.CompletedTask;

    public static Func<WorkflowContext, Task> LogMismatch =>
        ctx =>
        {
            Logger.Log($"[Packing] barcode mismatch: active={ctx.ActiveBarcode} scanned={ctx.Barcode}");
            return Task.CompletedTask;
        };

    /// <summary>
    /// Hook for the host page (StationView) to inject its recorder.
    /// Keeps camera/capture lifetime out of the engine and off the main
    /// <see cref="StationActions"/> static so Workflow unit tests stay trivial.
    /// </summary>
    public static void SetStationRecorder(IStationRecorder? recorder) => _recorder = recorder;
    private static IStationRecorder? _recorder;

    public interface IStationRecorder
    {
        Task StartAsync(WorkflowContext ctx);
        Task StopAndUploadAsync(WorkflowContext ctx);
    }

    // ── QC ────────────────────────────────────────────────────────────────────

    public static Func<WorkflowContext, Task> LoadOrdersForTracking =>
        ctx => _qcHost?.LoadOrdersAsync(ctx) ?? Task.CompletedTask;

    public static Func<WorkflowContext, Task> ApplySkuDeduction =>
        ctx => _qcHost?.ApplySkuDeductionAsync(ctx) ?? Task.CompletedTask;

    public static Func<WorkflowContext, Task> ApplyManualQty =>
        ctx => _qcHost?.ApplyManualQtyAsync(ctx) ?? Task.CompletedTask;

    public static Func<WorkflowContext, Task> ApplyCardTap =>
        ctx => _qcHost?.ApplyCardTapAsync(ctx) ?? Task.CompletedTask;

    public static Func<WorkflowContext, Task> SaveQcPassed =>
        ctx => _qcHost?.SaveQcPassedAsync(ctx) ?? Task.CompletedTask;

    public static Func<WorkflowContext, Task> SaveQcHold =>
        ctx => _qcHost?.SaveQcHoldAsync(ctx) ?? Task.CompletedTask;

    public static Func<WorkflowContext, Task> ResetOrder =>
        ctx => _qcHost?.ResetOrderAsync(ctx) ?? Task.CompletedTask;

    /// <summary>
    /// Snapshots the current session's tracking number into
    /// <see cref="WorkflowContext.PreviousTrackingNumber"/> before a new
    /// <c>tracking_scan</c> overwrites the session. The subsequent
    /// <c>tracking_scanned</c> event will carry both values in its payload,
    /// making the handoff between orders fully auditable.
    /// </summary>
    public static Func<WorkflowContext, Task> CapturePreviousTracking =>
        ctx =>
        {
            ctx.PreviousTrackingNumber = ctx.OrderTrackingNumber ?? ctx.ActiveBarcode;
            return Task.CompletedTask;
        };

    public static Func<WorkflowContext, Task> LogScanRejected =>
        ctx =>
        {
            Logger.Log($"[QC] scan rejected: sku={ctx.Sku} reason={ctx.RejectReason}");
            return Task.CompletedTask;
        };

    public static void SetQcHost(IQcHost? host) => _qcHost = host;
    private static IQcHost? _qcHost;

    public interface IQcHost
    {
        Task LoadOrdersAsync(WorkflowContext ctx);
        Task ApplySkuDeductionAsync(WorkflowContext ctx);
        Task ApplyManualQtyAsync(WorkflowContext ctx);
        Task ApplyCardTapAsync(WorkflowContext ctx);
        Task SaveQcPassedAsync(WorkflowContext ctx);
        Task SaveQcHoldAsync(WorkflowContext ctx);
        Task ResetOrderAsync(WorkflowContext ctx);
    }

    // ── Video (uploads) ───────────────────────────────────────────────────────

    /// <summary>
    /// Calls POST /videos/{id}/manual-upload-needed so the backend broadcasts
    /// a manual_upload_needed WebSocket event to connected frontends.
    /// Fires after all upload retries are exhausted or remote verification fails.
    /// </summary>
    public static Func<WorkflowContext, Task> NotifyManualUploadNeeded =>
        async ctx =>
        {
            if (ctx.VideoId is { } videoId)
                await ApiService.NotifyManualUploadNeededAsync(videoId);
        };

    public static Func<WorkflowContext, Task> UploadToMinio =>
        ctx => _videoUploader?.UploadAsync(ctx) ?? Task.CompletedTask;

    public static Func<WorkflowContext, Task> VerifyRemote =>
        ctx => _videoUploader?.VerifyRemoteAsync(ctx) ?? Task.CompletedTask;

    public static Func<WorkflowContext, Task> BackoffBeforeRetry =>
        async ctx =>
        {
            // 2s → 8s → 30s, jittered ±25%.
            var baseMs = ctx.UploadAttempt switch
            {
                <= 1 => 2_000,
                2    => 8_000,
                _    => 30_000,
            };
            var jitter = Random.Shared.Next(-baseMs / 4, baseMs / 4);
            await Task.Delay(Math.Max(500, baseMs + jitter));
        };

    public static void SetVideoUploader(IVideoUploader? uploader) => _videoUploader = uploader;
    private static IVideoUploader? _videoUploader;

    public interface IVideoUploader
    {
        Task UploadAsync(WorkflowContext ctx);
        Task VerifyRemoteAsync(WorkflowContext ctx);
    }
}
