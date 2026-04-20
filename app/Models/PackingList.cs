using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace app.Models;

/// <summary>A coloured badge descriptor used by StatusBadges on ProductItem.</summary>
[SupportedOSPlatform("windows")]
public class BadgeInfo
{
    public string Text        { get; init; } = "";
    public Color  BgColor     { get; init; } = Colors.Transparent;
    public Color  FgColor     { get; init; } = Colors.Black;
    public Color  BorderColor { get; init; } = Colors.Transparent;
}

[SupportedOSPlatform("windows")]
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

    /// <summary>Original required quantity from the order (ProductLists), regardless of picking state.</summary>
    [JsonIgnore] public int RequiredQuantity { get; set; }

    // ── Product name helpers ──────────────────────────────────────────────────

    /// <summary>Product name without a trailing (important info) parenthetical.</summary>
    [JsonIgnore] public string BaseName => ExtractBaseName(Name);

    /// <summary>Content inside the trailing (...) at the end of the product name, if present.</summary>
    [JsonIgnore] public string ImportantInfo => ExtractImportantInfo(Name);

    [JsonIgnore] public bool HasImportantInfo => !string.IsNullOrWhiteSpace(ImportantInfo);

    private static string ExtractBaseName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name;
        var trimmed = name.TrimEnd();
        var lastClose = trimmed.LastIndexOf(')');
        var lastOpen  = trimmed.LastIndexOf('(');
        if (lastOpen >= 0 && lastClose == trimmed.Length - 1 && lastClose > lastOpen)
            return trimmed[..lastOpen].TrimEnd();
        return name;
    }

    private static string ExtractImportantInfo(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var trimmed = name.TrimEnd();
        var lastClose = trimmed.LastIndexOf(')');
        var lastOpen  = trimmed.LastIndexOf('(');
        if (lastOpen >= 0 && lastClose == trimmed.Length - 1 && lastClose > lastOpen)
            return trimmed[(lastOpen + 1)..lastClose];
        return "";
    }

    [JsonIgnore] public bool HasVariation   => !string.IsNullOrWhiteSpace(Variation);
    [JsonIgnore] public bool HasNoVariation => !HasVariation;

    // ── Status badges ─────────────────────────────────────────────────────────

    [JsonIgnore] public IReadOnlyList<BadgeInfo> StatusBadges => BuildStatusBadges(_orderQcContext);

    private static IReadOnlyList<BadgeInfo> BuildStatusBadges(string ctx) => ctx.ToLower() switch
    {
        "qc passed" or "packed complete" =>
            [new BadgeInfo { Text = ctx,        BgColor = Color.FromArgb("#dcfce7"), FgColor = Color.FromArgb("#166534"), BorderColor = Color.FromArgb("#86efac") }],
        "qc hold" =>
            [new BadgeInfo { Text = "QC Hold",  BgColor = Color.FromArgb("#fef9c3"), FgColor = Color.FromArgb("#713f12"), BorderColor = Color.FromArgb("#fde68a") }],
        "packed" =>
            [new BadgeInfo { Text = "Packed",   BgColor = Color.FromArgb("#ffedd5"), FgColor = Color.FromArgb("#9a3412"), BorderColor = Color.FromArgb("#fdba74") }],
        var s when string.IsNullOrWhiteSpace(s) => [],
        _ => [new BadgeInfo { Text = ctx,       BgColor = Color.FromArgb("#f3f4f6"), FgColor = Color.FromArgb("#374151"), BorderColor = Color.FromArgb("#e5e7eb") }],
    };
    [JsonIgnore] public bool IsFullyPicked => Quantity <= 0;
    [JsonIgnore] public Color CardBgColor =>
        IsFullyPicked ||
        string.Equals(_orderQcContext, "QC Passed",        StringComparison.OrdinalIgnoreCase) ||
        string.Equals(_orderQcContext, "Packed Complete",  StringComparison.OrdinalIgnoreCase)
            ? Color.FromArgb("#dcfce7")  // green — picked / QC Passed / Packed Complete
        : string.Equals(_orderQcContext, "Packed", StringComparison.OrdinalIgnoreCase)
            ? Color.FromArgb("#ffedd5")  // orange — Packed (in progress)
        : string.IsNullOrEmpty(_orderQcContext)
            ? Colors.White
            : Color.FromArgb("#fef9c3"); // yellow — QC Hold

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
        set { _orderQcContext = value; OnPropertyChanged(nameof(CardBgColor)); OnPropertyChanged(nameof(StatusBadges)); }
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
            OnPropertyChanged(nameof(IsNotQcHold));
            OnPropertyChanged(nameof(ResetOpacity));
            OnPropertyChanged(nameof(IsPacked));
            OnPropertyChanged(nameof(IsPackedComplete));
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
        set { _checkedBy = value; OnPropertyChanged(); OnPropertyChanged(nameof(CheckedByDisplay)); OnPropertyChanged(nameof(IsPackedComplete)); }
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

    public bool IsQcHold    => string.Equals(_packingStatus, "QC Hold", StringComparison.OrdinalIgnoreCase);
    public bool IsNotQcHold => !IsQcHold;
    public double ResetOpacity => IsQcHold ? 1.0 : 0.0;
    public bool IsPacked  => string.Equals(_packingStatus, "Packed",   StringComparison.OrdinalIgnoreCase);
    public bool IsPackedComplete =>
        IsPacked && !string.IsNullOrWhiteSpace(_checkedBy) && AllUpdatedItemsZero();

    private bool AllUpdatedItemsZero()
    {
        if (string.IsNullOrWhiteSpace(UpdatedProductLists)) return false;
        try
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var list = JsonSerializer.Deserialize<List<ProductItem>>(UpdatedProductLists, opts);
            return list?.All(p => p.Quantity <= 0) ?? false;
        }
        catch { return false; }
    }

    public string StatusDisplay => _packingStatus ?? "unknown";

    public Color StatusBgColor => (_packingStatus ?? "").ToLower() switch
    {
        "completed" or "done" or "qc passed" => Color.FromArgb("#dcfce7"),
        "in_progress" or "packing" or "qc hold" => Color.FromArgb("#fef9c3"),
        "packed"                              => Color.FromArgb("#ffedd5"),
        _                                     => Color.FromArgb("#f3f4f6"),
    };

    public Color StatusFgColor => (_packingStatus ?? "").ToLower() switch
    {
        "completed" or "done" or "qc passed" => Color.FromArgb("#166534"),
        "in_progress" or "packing" or "qc hold" => Color.FromArgb("#713f12"),
        "packed"                              => Color.FromArgb("#9a3412"),
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
        var p when p.Contains("shopee")  => "shopee.png",
        var p when p.Contains("lazada")  => "lazada.png",
        var p when p.Contains("tiktok")  => "tiktok.png",
        _                                => null,
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
                item.RequiredQuantity = item.Quantity;
                item.OrderQcContext   = "";
                item.IsBeingPicked    = false;
            }
        }
        catch { }
    }

    private ObservableCollection<ProductItem> ParseProductsCore()
    {
        // QC Hold / Packed (in-progress) → show updated quantities so picker sees what's left.
        // Packed Complete / QC Passed / everything else → show original required quantities.
        var useUpdated = (IsQcHold || (IsPacked && !IsPackedComplete)) && !string.IsNullOrWhiteSpace(UpdatedProductLists);
        var json = useUpdated ? UpdatedProductLists : ProductLists;

        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var list = JsonSerializer.Deserialize<List<ProductItem>>(json, opts) ?? [];

            // Build a map of original required quantities from ProductLists
            Dictionary<string, int>? requiredMap = null;
            if (useUpdated && !string.IsNullOrWhiteSpace(ProductLists))
            {
                try
                {
                    var origList = JsonSerializer.Deserialize<List<ProductItem>>(ProductLists, opts) ?? [];
                    requiredMap = origList.ToDictionary(p => p.SellerSku, p => p.Quantity);
                }
                catch { }
            }

            string ctx;
            if (IsPacked)
                ctx = IsPackedComplete ? "Packed Complete" : "Packed";
            else if (IsQcStatus)
                ctx = _packingStatus!;
            else
                ctx = "";

            foreach (var item in list)
            {
                item.OriginalQuantity = item.Quantity;
                item.RequiredQuantity = requiredMap != null && requiredMap.TryGetValue(item.SellerSku, out var req)
                    ? req
                    : item.Quantity;
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
