using System;

namespace app.Services;

/// <summary>
/// Pure decision logic for the Shipping (dispatch) station. Zero MAUI dependencies so it
/// can be unit-tested from a plain net10.0 test project. Given a tracking lookup result,
/// decides whether a scan ships the parcel (terminal 'Shipped') and which verdict to flash.
/// The gate is all_items_cleared (the QC-done signal); the packing_status string does not gate,
/// except QC Hold (blocked) and Shipped (idempotent no-op).
/// </summary>
public enum PackOutcome { Ship, NotFound, Cancelled, AlreadyShipped, Blocked, SaveFailed }

public readonly record struct PackVerdictResult(
    PackOutcome Outcome,
    bool ShouldWrite,
    string Word,
    string Sub,
    string Glyph,
    string Color);

public static class PackVerdict
{
    public const string ColorGreen = "#16a34a";
    public const string ColorRed   = "#dc2626";
    public const string ColorAmber = "#d97706";
    public const string ColorGrey  = "#6b7280";

    private static bool Is(string? status, string expected) =>
        string.Equals(status?.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Evaluate a tracking scan. <paramref name="found"/> = a row matched;
    /// <paramref name="cancelled"/> = PackingList.IsCancelledOrder;
    /// <paramref name="packingStatus"/> = raw backend status (may be null);
    /// <paramref name="allItemsCleared"/> = PackingList.AllItemsCleared (the QC-done gate).
    /// </summary>
    public static PackVerdictResult Evaluate(bool found, bool cancelled, string? packingStatus, bool allItemsCleared)
    {
        if (!found)
            return new(PackOutcome.NotFound, false, "NOT FOUND", "No matching order", "?", ColorRed);

        // Cancelled wins over everything — a cancelled parcel must never ship.
        if (cancelled)
            return new(PackOutcome.Cancelled, false, "CANCELLED", "Order cancelled", "✕", ColorRed);

        if (Is(packingStatus, "Shipped"))
            return new(PackOutcome.AlreadyShipped, false, "ALREADY SHIPPED", "Already shipped", "↻", ColorGrey);

        if (Is(packingStatus, "QC Hold"))
            return new(PackOutcome.Blocked, false, "QC HOLD", "On QC hold", "!", ColorAmber);

        // Lifecycle: To be packed → QC → Packing → Packed → Shipped. Gate = QC done
        // (allItemsCleared) AND a post-QC, shippable status. Ideal = Packed; no-video
        // stations ship from QC Passed / Packing.
        var shippable = Is(packingStatus, "Packed") || Is(packingStatus, "QC Passed") || Is(packingStatus, "Packing");
        if (allItemsCleared && shippable)
            return new(PackOutcome.Ship, true, "SHIPPED", "QC verified — dispatched", "✓", ColorGreen);

        // Not cleared, or not yet packable.
        return new(PackOutcome.Blocked, false, "AWAITING QC", "Not yet QC'd", "!", ColorAmber);
    }

    /// <summary>Verdict shown when the REST write itself fails (network / server error).</summary>
    public static PackVerdictResult SaveFailed() =>
        new(PackOutcome.SaveFailed, false, "SAVE FAILED", "Could not reach server", "!", ColorRed);
}
