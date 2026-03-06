using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace app.Models;

public class ProductItem
{
    [JsonPropertyName("product_name")]    public string Name      { get; set; } = "";
    [JsonPropertyName("product_variation")] public string Variation { get; set; } = "";
    [JsonPropertyName("quantity")]          public int    Quantity  { get; set; }

    public bool HasVariation => !string.IsNullOrWhiteSpace(Variation);
}

[SupportedOSPlatform("windows")]
public class PackingList
{
    public int PackingId { get; set; }
    public string TrackingNumber { get; set; } = "";
    public string OrderNumber { get; set; } = "";
    public int? TotalItems { get; set; }
    public string? PackingStatus { get; set; }
    public DateTime? CreatedAt { get; set; }
    public string? PackingStation { get; set; }
    public string? ProductLists { get; set; }  // raw JSON string
    public string? Platform { get; set; }

    // ── Display helpers ──────────────────────────────────────────────────────

    public string StatusDisplay => PackingStatus ?? "unknown";

    public Color StatusBgColor => (PackingStatus ?? "").ToLower() switch
    {
        "completed" or "done"      => Color.FromArgb("#dcfce7"),
        "in_progress" or "packing" => Color.FromArgb("#fef9c3"),
        _                          => Color.FromArgb("#f3f4f6"),
    };

    public Color StatusFgColor => (PackingStatus ?? "").ToLower() switch
    {
        "completed" or "done"      => Color.FromArgb("#166534"),
        "in_progress" or "packing" => Color.FromArgb("#713f12"),
        _                          => Color.FromArgb("#374151"),
    };

    public string CreatedAtDisplay =>
        CreatedAt.HasValue ? CreatedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "—";

    public string TotalItemsDisplay =>
        TotalItems.HasValue ? TotalItems.Value.ToString() : "—";

    public string PackingStationDisplay =>
        string.IsNullOrWhiteSpace(PackingStation) ? "—" : PackingStation;

    public string? PlatformIcon => (Platform ?? "").ToLower() switch
    {
        "shopee"  => "shopee.png",
        "lazada"  => "lazada.png",
        "tiktok"  => "tiktok.png",
        _         => null,
    };

    public bool HasPlatformIcon => PlatformIcon != null;

    // ── Product line items ────────────────────────────────────────────────────

    public bool HasProducts => !string.IsNullOrWhiteSpace(ProductLists);

    public IReadOnlyList<ProductItem> Products
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ProductLists)) return [];
            try
            {
                return JsonSerializer.Deserialize<List<ProductItem>>(
                    ProductLists,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            }
            catch { return []; }
        }
    }
}
