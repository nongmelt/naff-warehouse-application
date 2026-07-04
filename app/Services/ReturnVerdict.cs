using System;

namespace app.Services;

/// <summary>
/// Pure decision logic for the Returns side of the Shipping station. Zero MAUI
/// dependencies (unit-tested from app.Tests). A return scan is valid ONLY for a
/// parcel that physically left — packing_status 'Shipped'. Cancelled order_status
/// does not block (returns ARE cancelled orders by definition).
/// </summary>
public enum ReturnOutcome { Return, NotFound, AlreadyReturned, NotShipped, SaveFailed }

public readonly record struct ReturnVerdictResult(
    ReturnOutcome Outcome,
    bool ShouldWrite,
    string Word,
    string Sub,
    string Glyph,
    string Color);

public static class ReturnVerdict
{
    public const string ColorGreen = "#16a34a";
    public const string ColorRed   = "#dc2626";
    public const string ColorAmber = "#d97706";
    public const string ColorGrey  = "#6b7280";

    private static bool Is(string? status, string expected) =>
        string.Equals(status?.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    public static ReturnVerdictResult Evaluate(bool found, string? packingStatus)
    {
        if (!found)
            return new(ReturnOutcome.NotFound, false, "NOT FOUND", "No matching order", "?", ColorRed);

        if (Is(packingStatus, "Returned"))
            return new(ReturnOutcome.AlreadyReturned, false, "ALREADY RETURNED", "Already returned", "↻", ColorGrey);

        if (Is(packingStatus, "Shipped"))
            return new(ReturnOutcome.Return, true, "RETURN", "Confirm reason to return", "⮌", ColorGreen);

        return new(ReturnOutcome.NotShipped, false, "NOT SHIPPED", "Only shipped parcels return", "!", ColorAmber);
    }

    /// <summary>Verdict when the REST write itself fails (network / server error).</summary>
    public static ReturnVerdictResult SaveFailed() =>
        new(ReturnOutcome.SaveFailed, false, "SAVE FAILED", "Could not reach server", "!", ColorRed);
}
