using System.Runtime.Versioning;

namespace app.Workflows.Definitions;

/// <summary>
/// Runs independently from PackingWorkflow. Started by StationActions.StartVideoWorkflow
/// after the local file passes verification.
///
/// States: pending → uploading → verifying → completed (local file deleted) | failed
///
/// Triggers (all fired by VideoWorkflowRunner):
///   start          — fired immediately on runner creation (normal flow)
///   recover        — fired by VideoWorkflowManager.RecoverAsync (disk recovery)
///   upload_response — after each MinIO PUT; ctx.FailureReason=null on success
///   verify_remote  — after HEAD check; ctx.UploadResponseStatus="200"|"404"
///   retry_command  — fired by VideoWorkflowManager.HandleRetry (dashboard retry)
///
/// Steps are Noops. WorkflowEngine records a WorkflowEventSink entry per step.
/// </summary>
[SupportedOSPlatform("windows")]
public static class VideoWorkflow
{
    public const string Name = "Video";

    public static Workflow Build() =>
        new WorkflowBuilder(Name)
            .Initial("pending")

            // ── pending ──────────────────────────────────────────────────────────
            .State("pending", s => s
                .On("start")
                    .Do("upload_started", "begin MinIO upload",
                        StationActions.Noop)
                    .GoTo("uploading")

                .On("recover")
                    .Do("upload_recovered", "resume upload from disk recovery",
                        StationActions.Noop)
                    .GoTo("uploading"))

            // ── uploading ────────────────────────────────────────────────────────
            .State("uploading", s => s
                .On("upload_response")
                    .When("HTTP 2xx — proceed to verify",
                          c => c.FailureReason is null)
                    .Do("upload_succeeded", "record upload success",
                        StationActions.Noop)
                    .GoTo("verifying")

                .On("upload_response")
                    .When("non-2xx, attempts remaining",
                          c => c.FailureReason is not null && c.UploadAttempt < 3)
                    .Do("upload_retry", "record retry",
                        StationActions.Noop)
                    .GoTo("uploading")

                .On("upload_response")
                    .When("non-2xx, attempts exhausted",
                          c => c.FailureReason is not null && c.UploadAttempt >= 3)
                    .Do("upload_failed", "record failure",
                        StationActions.Noop)
                    .GoTo("failed"))

            // ── verifying ────────────────────────────────────────────────────────
            .State("verifying", s => s
                .On("verify_remote")
                    .When("HEAD returns 200",
                          c => c.UploadResponseStatus is "200")
                    .Do("verified", "record completion",
                        StationActions.Noop)
                    .GoTo("completed")

                .On("verify_remote")
                    .When("HEAD returns 404 — remote missing",
                          c => c.UploadResponseStatus is "404")
                    .Do("verify_missing", "record verify failure",
                        StationActions.Noop)
                    .GoTo("failed"))

            // ── completed (terminal — runner deletes local file) ─────────────────
            .State("completed", _ => { })

            // ── failed (terminal — dashboard retry via retry_command) ────────────
            .State("failed", s => s
                .On("retry_command")
                    .When("local file still exists",
                          c => !string.IsNullOrWhiteSpace(c.LocalFilePath)
                               && File.Exists(c.LocalFilePath))
                    .Do("retry_requested", "reset attempt counter",
                        ctx => { ctx.UploadAttempt = 0; ctx.FailureReason = null; return Task.CompletedTask; })
                    .GoTo("uploading")

                .On("retry_command")
                    .When("local file gone — cannot retry",
                          c => string.IsNullOrWhiteSpace(c.LocalFilePath)
                               || !File.Exists(c.LocalFilePath))
                    .Do("retry_rejected", "remain failed, local_file_missing",
                        ctx => { ctx.FailureReason = "local_file_missing"; return Task.CompletedTask; })
                    .GoTo("failed"))

            .Build();
}
