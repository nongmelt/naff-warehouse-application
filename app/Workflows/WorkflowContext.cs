using System.Collections.Generic;

namespace app.Workflows;

/// <summary>
/// The mutable data bag passed to every guard and step. Fields are nullable so
/// individual workflows only set what they care about — Packing never touches
/// <see cref="Sku"/>, QC never touches <see cref="UploadAttempt"/>.
///
/// Anything here can end up in <c>workflow_events.payload</c> via
/// <see cref="WorkflowEngine"/>; keep it cheap to JSON-serialise.
/// </summary>
public sealed class WorkflowContext
{
    // ── Shared ────────────────────────────────────────────────────────────────
    public string? Barcode { get; set; }
    public string? ActiveBarcode { get; set; }
    public string? Operator { get; set; }
    public int?    StationId { get; set; }

    // ── QC ────────────────────────────────────────────────────────────────────
    public string? Sku { get; set; }
    public string? OrderTrackingNumber { get; set; }
    public string? PreviousTrackingNumber { get; set; }
    public int     SequenceInSession { get; set; }
    public int?    QtyBefore { get; set; }
    public int?    QtyAfter { get; set; }
    public int?    QtyRemaining { get; set; }
    public int?    QtyEntered { get; set; }
    public int?    QtyDeducted { get; set; }
    public int?    ItemsPicked { get; set; }
    public int?    ItemsRemaining { get; set; }
    public int?    OrdersFound { get; set; }
    public string? CheckedBy { get; set; }
    public string? RejectReason { get; set; }
    public string? PreviousStatus { get; set; }

    // ── Packing ───────────────────────────────────────────────────────────────
    public double? DurationSeconds { get; set; }
    public long?   VideoFileSizeBytes { get; set; }
    public string? StationLabel { get; set; }
    public string? PackedBy { get; set; }

    // ── Video ─────────────────────────────────────────────────────────────────
    public int     UploadAttempt { get; set; }
    public int?    VideoId { get; set; }
    public string? LocalFilePath { get; set; }
    public string? RemoteFilePath { get; set; }
    public string? FailureReason { get; set; }
    public long?   UploadDurationMs { get; set; }
    public string? UploadResponseStatus { get; set; }
    public bool    LocalFileIsValid { get; set; }

    // ── Bag for step-specific extras that don't deserve a typed field ─────────
    public Dictionary<string, object?> Extra { get; } = new();

    /// <summary>
    /// Returns a shallow-cloned context with <see cref="Barcode"/> overridden.
    /// Host pages use this when firing a trigger without mutating the long-lived
    /// session context (e.g. successive scans in the same tracking session).
    /// </summary>
    public WorkflowContext With(string? barcode = null, string? sku = null)
    {
        var copy = (WorkflowContext)MemberwiseClone();
        if (barcode is not null) copy.Barcode = barcode;
        if (sku is not null)     copy.Sku     = sku;
        return copy;
    }
}
