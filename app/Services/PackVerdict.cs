using System;

namespace app.Services;

/// <summary>
/// Pure decision logic for the no-video packing station. Zero MAUI dependencies so it
/// can be unit-tested from a plain net10.0 test project. Given the result of a tracking
/// lookup, decides whether a scan seals the parcel as "Packed" and which verdict to flash.
/// </summary>
public enum PackOutcome { Pack, NotFound, Cancelled, AlreadyPacked, Blocked, SaveFailed }

public readonly record struct PackVerdictResult(
    PackOutcome Outcome,
    bool ShouldWrite,
    string Word,
    string Sub,
    string Glyph,
    string Color);

public static class PackVerdict
{
    // Full-screen verdict-flash background colours.
    public const string ColorGreen = "#16a34a";
    public const string ColorRed   = "#dc2626";
    public const string ColorAmber = "#d97706";
    public const string ColorGrey  = "#6b7280";

    private static bool Is(string? status, string expected) =>
        string.Equals(status?.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Evaluate a tracking scan. <paramref name="found"/> = a row matched the tracking number;
    /// <paramref name="cancelled"/> = PackingList.IsCancelledOrder;
    /// <paramref name="packingStatus"/> = PackingList.PackingStatus (raw backend string, may be null).
    /// </summary>
    public static PackVerdictResult Evaluate(bool found, bool cancelled, string? packingStatus)
    {
        if (!found)
            return new(PackOutcome.NotFound, false, "NOT FOUND", "No matching order", "?", ColorRed);

        // Cancelled wins over packing_status — a cancelled parcel must never seal.
        if (cancelled)
            return new(PackOutcome.Cancelled, false, "CANCELLED", "Order cancelled", "✕", ColorRed);

        if (Is(packingStatus, "Packed"))
            return new(PackOutcome.AlreadyPacked, false, "ALREADY PACKED", "No action taken", "✓", ColorGrey);

        if (Is(packingStatus, "QC Passed") || Is(packingStatus, "Packing"))
            return new(PackOutcome.Pack, true, "PACKED", "QC verified — sealed", "✓", ColorGreen);

        if (Is(packingStatus, "To be packed"))
            return new(PackOutcome.Blocked, false, "NOT QC'D", "Awaiting QC", "!", ColorAmber);

        if (Is(packingStatus, "QC Hold"))
            return new(PackOutcome.Blocked, false, "QC HOLD", "On QC hold", "!", ColorAmber);

        // Unknown / null status — block rather than seal.
        return new(PackOutcome.Blocked, false, "BLOCKED",
            string.IsNullOrWhiteSpace(packingStatus) ? "Unknown status" : packingStatus, "!", ColorAmber);
    }

    /// <summary>Verdict shown when the REST write itself fails (network / server error).</summary>
    public static PackVerdictResult SaveFailed() =>
        new(PackOutcome.SaveFailed, false, "SAVE FAILED", "Could not reach server", "!", ColorRed);
}
