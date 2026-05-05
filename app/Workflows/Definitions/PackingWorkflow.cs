using System.Runtime.Versioning;

namespace app.Workflows.Definitions;

/// <summary>
/// Packing: idle → recording → verify → idle.
///
/// The verify state checks the local file before creating a backend record.
/// If the file is valid, StationActions.StartVideoWorkflow hands it off to
/// VideoWorkflowManager which runs upload/verify/delete independently.
///
/// Triggers:
///   barcode_scan  — USB barcode gun input
///   file_checked  — fired by StationView after ctx.LocalFileIsValid is set
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
                    .When("same barcode rescanned — stop recording",
                          c => !string.IsNullOrWhiteSpace(c.ActiveBarcode)
                               && c.Barcode == c.ActiveBarcode)
                    .Do("packing_stopped", "stop recording",
                        StationActions.StopAndUpload)
                    .Do("packing_stopped", "push status Packed",
                        StationActions.PushStatusByScan("Packed"))
                    .GoTo("verify")

                .On("barcode_scan")
                    .When("Reset literal — abort recording",
                          c => c.Barcode == "Reset")
                    .Do("packing_reset", "abort recording",
                        StationActions.StopAndUpload)
                    .GoTo("verify")

                .On("barcode_scan")
                    .When("different barcode during recording — mismatch",
                          c => !string.IsNullOrWhiteSpace(c.ActiveBarcode)
                               && !string.IsNullOrWhiteSpace(c.Barcode)
                               && c.Barcode != c.ActiveBarcode
                               && c.Barcode != "Reset")
                    .Do("barcode_mismatch", "log mismatch, keep recording",
                        StationActions.LogMismatch)
                    .GoTo("recording"))

            // ── verify ───────────────────────────────────────────────────────
            // StationView fires file_checked after setting ctx.LocalFileIsValid
            // and ctx.LocalFilePath. This happens synchronously in HandleBarcode
            // before any async I/O so the engine gate is held only briefly.
            .State("verify", s => s
                .On("file_checked")
                    .When("file exists, ≥1 KB, valid mp4 container",
                          c => c.LocalFileIsValid)
                    .Do("file_verified", "create backend record and start video workflow",
                        StationActions.StartVideoWorkflow)
                    .GoTo("idle")

                .On("file_checked")
                    .When("file missing, empty, or corrupt",
                          c => !c.LocalFileIsValid)
                    .Do("file_invalid", "log and discard — no upload",
                        StationActions.LogInvalidFile)
                    .GoTo("idle"))

            .Build();
}
