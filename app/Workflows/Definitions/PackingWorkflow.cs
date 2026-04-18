using System.Runtime.Versioning;

namespace app.Workflows.Definitions;

/// <summary>
/// Packing (merged with Video upload): Idle → Packing → Uploading → Uploaded → Completed,
/// with Failed as a terminal branch. Video upload is an inline phase of the pack
/// cycle because on the station floor the two flows are inseparable — the file
/// only exists because someone just finished packing.
///
/// Triggers:
///   <list type="bullet">
///     <item><c>barcode_scan</c> — any USB-barcode-gun input.</item>
///     <item><c>upload_response</c> — MinIO PUT resolved (ctx.UploadResponseStatus + ctx.FailureReason).</item>
///     <item><c>verify_remote</c> — HEAD verify completed (ctx.UploadResponseStatus = "200" | "404").</item>
///     <item><c>retry_command</c> — dashboard asked the station to re-upload a Failed video.</item>
///   </list>
///
/// Retry: up to 3 attempts inside <c>uploading</c>, exponential backoff (2s / 8s / 30s)
/// jittered, done as a step inside the retry transition so the event stream
/// records each attempt.
///
/// Failure reason codes end up in <c>workflow_events.payload.reason</c> and
/// <c>packing_videos.failure_reason</c>:
/// <c>network_timeout</c>, <c>connection_refused</c>, <c>auth_failure</c>,
/// <c>disk_full</c>, <c>remote_missing</c>, <c>checksum_mismatch</c>,
/// <c>local_file_missing</c>, <c>unknown</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public static class PackingWorkflow
{
    public const string Name = "Packing";

    public static Workflow Build() =>
        new WorkflowBuilder(Name)
            .Initial("idle")

            // ── idle ─────────────────────────────────────────────────────────
            .State("idle", s => s
                .On("barcode_scan")
                    .When("any barcode when nothing is recording",
                          c => !string.IsNullOrWhiteSpace(c.Barcode)
                               && string.IsNullOrWhiteSpace(c.ActiveBarcode))
                    .Do("tracking_scanned", "start camera recording",
                        StationActions.StartRecording)
                    .Do("tracking_scanned", "push status Packing",
                        StationActions.PushStatusByScan("Packing"))
                    .GoTo("recording"))

            // ── recording ────────────────────────────────────────────────────
            .State("recording", s => s
                .On("barcode_scan")
                    .When("same barcode rescanned — stop and start upload",
                          c => !string.IsNullOrWhiteSpace(c.ActiveBarcode)
                               && c.Barcode == c.ActiveBarcode)
                    .Do("packing_stopped", "stop recording",
                        StationActions.StopAndUpload)
                    .Do("packing_stopped", "push status Packed",
                        StationActions.PushStatusByScan("Packed"))
                    .Do("upload_started", "queue MinIO upload",
                        StationActions.UploadToMinio)
                    .GoTo("uploading")

                .On("barcode_scan")
                    .When("Reset literal scanned — abort recording",
                          c => c.Barcode == "Reset")
                    .Do("packing_reset", "abort and discard recording",
                        StationActions.StopAndUpload)
                    .GoTo("idle")

                .On("barcode_scan")
                    .When("different barcode during recording — mismatch",
                          c => !string.IsNullOrWhiteSpace(c.ActiveBarcode)
                               && !string.IsNullOrWhiteSpace(c.Barcode)
                               && c.Barcode != c.ActiveBarcode
                               && c.Barcode != "Reset")
                    .Do("barcode_mismatch", "log mismatch, keep recording",
                        StationActions.LogMismatch)
                    .GoTo("recording"))

            // ── uploading ────────────────────────────────────────────────────
            .State("uploading", s => s
                .On("upload_response")
                    .When("HTTP 2xx",
                          c => c.FailureReason is null)
                    .Do("upload_succeeded", "record success",
                        StationActions.Noop)
                    .GoTo("uploaded")

                .On("upload_response")
                    .When("non-2xx or timeout, attempts remaining",
                          c => c.FailureReason is not null && c.UploadAttempt < 3)
                    .Do("upload_retry", "backoff before retry",
                        StationActions.BackoffBeforeRetry)
                    .Do("upload_retry", "POST file to MinIO again",
                        StationActions.UploadToMinio)
                    .GoTo("uploading")

                .On("upload_response")
                    .When("non-2xx or timeout, attempts exhausted",
                          c => c.FailureReason is not null && c.UploadAttempt >= 3)
                    .Do("upload_failed", "mark video Failed with reason",
                        StationActions.Noop)
                    .GoTo("failed")

                .On("barcode_scan")
                    .When("new tracking scanned while upload in flight",
                          c => !string.IsNullOrWhiteSpace(c.Barcode)
                               && c.Barcode != c.ActiveBarcode
                               && c.Barcode != "Reset")
                    .Do("tracking_scanned", "start camera recording",
                        StationActions.StartRecording)
                    .Do("tracking_scanned", "push status Packing",
                        StationActions.PushStatusByScan("Packing"))
                    .GoTo("recording"))

            // ── uploaded ─────────────────────────────────────────────────────
            .State("uploaded", s => s
                .On("verify_remote")
                    .When("HEAD returns 200",
                          c => c.UploadResponseStatus is "200")
                    .Do("verified", "mark video Completed",
                        StationActions.VerifyRemote)
                    .GoTo("completed")

                .On("verify_remote")
                    .When("HEAD returns 404",
                          c => c.UploadResponseStatus is "404")
                    .Do("verify_missing", "mark video Failed (remote_missing)",
                        StationActions.Noop)
                    .GoTo("failed"))

            // ── completed (terminal for this cycle; next scan starts a new one) ──
            .State("completed", s => s
                .On("barcode_scan")
                    .When("new tracking scanned — start next cycle",
                          c => !string.IsNullOrWhiteSpace(c.Barcode))
                    .Do("tracking_scanned", "start camera recording",
                        StationActions.StartRecording)
                    .Do("tracking_scanned", "push status Packing",
                        StationActions.PushStatusByScan("Packing"))
                    .GoTo("recording"))

            // ── failed ───────────────────────────────────────────────────────
            .State("failed", s => s
                .On("retry_command")
                    .When("local file still exists",
                          c => !string.IsNullOrWhiteSpace(c.LocalFilePath)
                               && File.Exists(c.LocalFilePath))
                    .Do("retry_requested", "reset attempt counter",
                        ctx => { ctx.UploadAttempt = 0; ctx.FailureReason = null; return Task.CompletedTask; })
                    .Do("retry_requested", "POST file to MinIO again",
                        StationActions.UploadToMinio)
                    .GoTo("uploading")

                .On("retry_command")
                    .When("local file gone — cannot retry",
                          c => string.IsNullOrWhiteSpace(c.LocalFilePath)
                               || !File.Exists(c.LocalFilePath))
                    .Do("retry_rejected", "remain Failed, reason = local_file_missing",
                        ctx => { ctx.FailureReason = "local_file_missing"; return Task.CompletedTask; })
                    .GoTo("failed")

                .On("barcode_scan")
                    .When("new tracking scanned — start next cycle",
                          c => !string.IsNullOrWhiteSpace(c.Barcode)
                               && c.Barcode != "Reset")
                    .Do("tracking_scanned", "start camera recording",
                        StationActions.StartRecording)
                    .Do("tracking_scanned", "push status Packing",
                        StationActions.PushStatusByScan("Packing"))
                    .GoTo("recording"))

            .Build();
}
