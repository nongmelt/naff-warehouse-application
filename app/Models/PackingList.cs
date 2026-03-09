using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace app.Models;

public class ProductItem : INotifyPropertyChanged
{
    [JsonPropertyName("product_name")]      public string Name      { get; set; } = "";
    [JsonPropertyName("product_variation")] public string Variation { get; set; } = "";
    [JsonPropertyName("seller_sku")]        public string SellerSku { get; set; } = "";

    private int _quantity;
    [JsonPropertyName("quantity")]
    public int Quantity
    {
        get => _quantity;
        set
        {
            _quantity = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsFullyPicked));
            OnPropertyChanged(nameof(CardBgColor));
        }
    }

    /// <summary>Quantity at load time — used to detect whether any picking occurred.</summary>
    [JsonIgnore] public int OriginalQuantity { get; set; }

    [JsonIgnore] public bool HasVariation  => !string.IsNullOrWhiteSpace(Variation);
    [JsonIgnore] public bool IsFullyPicked => Quantity <= 0;
    [JsonIgnore] public Color CardBgColor =>
        IsFullyPicked || string.Equals(_orderQcContext, "QC Passed", StringComparison.OrdinalIgnoreCase)
            ? Color.FromArgb("#dcfce7")  // green — picked or QC Passed
            : string.IsNullOrEmpty(_orderQcContext) ? Colors.White
            : Color.FromArgb("#fef9c3"); // yellow — remaining in QC Hold context

    private bool _isBeingPicked;
    [JsonIgnore]
    public bool IsBeingPicked
    {
        get => _isBeingPicked;
        set
        {
            _isBeingPicked = value;
            if (value) { _pickQtyText = "1"; OnPropertyChanged(nameof(PickQtyText)); }
            OnPropertyChanged();
        }
    }

    // Resets to "1" each time the entry is shown; read at apply time from Entry.Text directly.
    private string _pickQtyText = "1";
    [JsonIgnore] public string PickQtyText => _pickQtyText;

    private string _orderQcContext = "";
    /// <summary>Set to "QC Passed" or "QC Hold" after in-session QC transitions to drive card colors.</summary>
    [JsonIgnore]
    public string OrderQcContext
    {
        get => _orderQcContext;
        set { _orderQcContext = value; OnPropertyChanged(nameof(CardBgColor)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

[SupportedOSPlatform("windows")]
public class PackingList : INotifyPropertyChanged
{
    public int PackingId { get; set; }
    public string TrackingNumber { get; set; } = "";
    public string OrderNumber { get; set; } = "";
    public int? TotalItems { get; set; }
    public DateTime? CreatedAt { get; set; }
    public string? ProductLists { get; set; }         // raw JSON — original quantities
    public string? UpdatedProductLists { get; set; }  // raw JSON — saved after QC action
    public string? Platform { get; set; }

    private string? _packingStatus;
    public string? PackingStatus
    {
        get => _packingStatus;
        set
        {
            _packingStatus = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsQcHold));
            OnPropertyChanged(nameof(StatusDisplay));
            OnPropertyChanged(nameof(StatusBgColor));
            OnPropertyChanged(nameof(StatusFgColor));
        }
    }

    private string? _packedBy;
    public string? PackedBy
    {
        get => _packedBy;
        set { _packedBy = value; OnPropertyChanged(); OnPropertyChanged(nameof(PackedByDisplay)); }
    }

    private DateTime? _updatedAt;
    public DateTime? UpdatedAt
    {
        get => _updatedAt;
        set
        {
            _updatedAt = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(UpdatedAtDisplay));
            OnPropertyChanged(nameof(HasBeenUpdated));
        }
    }

    private string? _checkedBy;
    public string? CheckedBy
    {
        get => _checkedBy;
        set { _checkedBy = value; OnPropertyChanged(); OnPropertyChanged(nameof(CheckedByDisplay)); }
    }

    private DateTime? _checkedAt;
    public DateTime? CheckedAt
    {
        get => _checkedAt;
        set
        {
            _checkedAt = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CheckedAtDisplay));
            OnPropertyChanged(nameof(HasBeenChecked));
        }
    }

    // ── Display helpers ──────────────────────────────────────────────────────

    public bool IsQcHold => string.Equals(PackingStatus, "QC Hold", StringComparison.OrdinalIgnoreCase);

    public string StatusDisplay => PackingStatus ?? "unknown";

    public Color StatusBgColor => (PackingStatus ?? "").ToLower() switch
    {
        "completed" or "done" or "qc passed" => Color.FromArgb("#dcfce7"),
        "in_progress" or "packing" or "qc hold" => Color.FromArgb("#fef9c3"),
        _                                     => Color.FromArgb("#f3f4f6"),
    };

    public Color StatusFgColor => (PackingStatus ?? "").ToLower() switch
    {
        "completed" or "done" or "qc passed" => Color.FromArgb("#166534"),
        "in_progress" or "packing" or "qc hold" => Color.FromArgb("#713f12"),
        _                                     => Color.FromArgb("#374151"),
    };

    public string CreatedAtDisplay =>
        CreatedAt.HasValue ? CreatedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "—";

    public string UpdatedAtDisplay =>
        UpdatedAt.HasValue ? UpdatedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "—";

    public bool HasBeenUpdated => UpdatedAt.HasValue;

    public string CheckedAtDisplay =>
        CheckedAt.HasValue ? CheckedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "—";

    public bool HasBeenChecked => CheckedAt.HasValue;

    public string TotalItemsDisplay =>
        TotalItems.HasValue ? TotalItems.Value.ToString() : "—";

    public string PackedByDisplay =>
        string.IsNullOrWhiteSpace(PackedBy) ? "—" : PackedBy;

    public string CheckedByDisplay =>
        string.IsNullOrWhiteSpace(CheckedBy) ? "—" : CheckedBy;

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

    private ObservableCollection<ProductItem>? _parsedProducts;

    /// <summary>
    /// Parses ProductLists JSON once and caches the result so quantity changes
    /// propagate back to the UI via INotifyPropertyChanged on each ProductItem.
    /// </summary>
    public ObservableCollection<ProductItem> ParsedProducts =>
        _parsedProducts ??= ParseProductsCore();

    private bool IsQcStatus =>
        string.Equals(PackingStatus, "QC Passed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(PackingStatus, "QC Hold",   StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resets ParsedProducts quantities back to the original values from ProductLists JSON.
    /// Call this after a DB reset to restore the UI without re-parsing from scratch.
    /// </summary>
    public void ResetToOriginalQuantities()
    {
        if (_parsedProducts == null || string.IsNullOrWhiteSpace(ProductLists)) return;
        try
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var originals = JsonSerializer.Deserialize<List<ProductItem>>(ProductLists, opts) ?? [];
            var origMap = originals.ToDictionary(p => p.SellerSku, p => p.Quantity);
            foreach (var item in _parsedProducts)
            {
                if (origMap.TryGetValue(item.SellerSku, out var origQty))
                    item.Quantity = origQty;
                item.OriginalQuantity = item.Quantity;
                item.OrderQcContext   = "";
                item.IsBeingPicked    = false;
            }
        }
        catch { }
    }

    private ObservableCollection<ProductItem> ParseProductsCore()
    {
        // QC Hold  → show remaining (updated) quantities so the picker knows what's left.
        // QC Passed → show the original needed quantities for reference.
        // Everything else → always use original.
        var isQcHold = string.Equals(PackingStatus, "QC Hold", StringComparison.OrdinalIgnoreCase);
        var json = isQcHold && !string.IsNullOrWhiteSpace(UpdatedProductLists)
            ? UpdatedProductLists
            : ProductLists;

        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var list = JsonSerializer.Deserialize<List<ProductItem>>(json, opts) ?? [];
            var ctx  = IsQcStatus ? PackingStatus! : "";
            foreach (var item in list)
            {
                item.OriginalQuantity = item.Quantity;
                item.OrderQcContext   = ctx;
            }
            return new ObservableCollection<ProductItem>(list);
        }
        catch { return []; }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
