namespace app.Models;

/// <summary>
/// Duplicate-card decision policy (spec: docs/mockups/2026-08-23-dup-overlay-redesign.html).
/// Normal case: the sibling was already processed, so the just-scanned parcel is
/// the duplicate. Neither-processed case: the just-scanned parcel is the one in
/// hand and ships — the OTHER (sibling) parcel gets marked.
/// </summary>
public static class DuplicateMarkPolicy
{
    public static bool MarksSibling(string? siblingStatus, string? scannedStatus) =>
        string.Equals(siblingStatus, "To be packed", StringComparison.OrdinalIgnoreCase)
        && string.Equals(scannedStatus, "To be packed", StringComparison.OrdinalIgnoreCase);

    public static string BuildMarkTooltip(string markTracking, string shipTracking) =>
        $"ทำเครื่องหมาย {markTracking} ว่าเป็นพัสดุซ้ำ และจัดส่งเฉพาะ {shipTracking}";

    public const string DismissTooltip = "เก็บพัสดุทั้งสองไว้ และจัดส่งทั้งสองพัสดุ";
}
