namespace app.Models;

/// <summary>Copy + ink for one leg's hover stamp. Hex strings, not Colors, so
/// the policy stays unit-testable without a MAUI context.</summary>
public readonly record struct ShipStampStyle(string Text, string Ink);

/// <summary>
/// Ship-outcome stamp policy for the duplicate card (spec:
/// docs/mockups/2026-08-23-will-ship-variations.html, variation E).
/// Hovering Dismiss keeps both parcels, so every leg ships. Hovering Mark
/// voids exactly one leg — the mark target chosen by <see cref="DuplicateMarkPolicy"/>.
/// </summary>
public static class ShipStampPolicy
{
    public const string ShipText = "จัดส่ง";
    public const string DuplicateText = "พัสดุซ้ำ";
    public const string ShipInk = "#15803d";
    public const string DuplicateInk = "#be123c";

    /// <summary>Opacity applied to the voided leg's content while Mark is hovered.</summary>
    public const double DimmedOpacity = 0.45;

    public static ShipStampStyle For(bool ships) =>
        ships ? new ShipStampStyle(ShipText, ShipInk)
              : new ShipStampStyle(DuplicateText, DuplicateInk);

    public static bool LegShips(bool markHover, bool isMarkTarget) => !markHover || !isMarkTarget;

    /// <summary>Both legs' ship outcomes from one call, so the leg→verdict mapping
    /// is pinned by tests rather than by the order of two call sites in the view.</summary>
    public static (bool SiblingShips, bool ScannedShips) LegOutcomes(bool markHover, bool siblingIsTarget) =>
        (LegShips(markHover, siblingIsTarget), LegShips(markHover, !siblingIsTarget));

    /// <summary>Content opacity for a leg — the dim is the ship outcome, not a second decision.</summary>
    public static double OpacityFor(bool ships) => ships ? 1 : DimmedOpacity;
}
