using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
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

/// <summary>Matches the backend jsonb payload shape: {"items": [...]}.</summary>
public record ProductListPayload([property: JsonPropertyName("items")] List<ProductItem> Items);

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
            ? Color.FromArgb("#dcfce7")
        : string.Equals(_orderQcContext, "Packed", StringComparison.OrdinalIgnoreCase)
            ? Color.FromArgb("#ffedd5")
        : string.IsNullOrEmpty(_orderQcContext)
            ? Colors.White
            : Color.FromArgb("#fef9c3");

    private bool _isBeingPicked;
    [JsonIgnore]
    public bool IsBeingPicked
    {
        get => _isBeingPicked;
        set
        {
            _isBeingPicked = value;
            OnPropertyChanged();
        }
    }

    private string _pickQtyText = "";
    /// <summary>Set this BEFORE setting IsBeingPicked = true (no auto-default).</summary>
    [JsonIgnore]
    public string PickQtyText
    {
        get => _pickQtyText;
        set { _pickQtyText = value; OnPropertyChanged(); }
    }

    private string _orderQcContext = "";
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
    public ProductListPayload? ProductLists { get; set; }
    public ProductListPayload? UpdatedProductLists { get; set; }
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

    // ── Display helpers ───────────────────────────────────────────────────────

    public bool IsQcHold    => string.Equals(_packingStatus, "QC Hold", StringComparison.OrdinalIgnoreCase);
    public bool IsNotQcHold => !IsQcHold;
    public double ResetOpacity => IsQcHold ? 1.0 : 0.0;
    public bool IsPacked  => string.Equals(_packingStatus, "Packed",   StringComparison.OrdinalIgnoreCase);
    public bool IsPackedComplete =>
        IsPacked && !string.IsNullOrWhiteSpace(_checkedBy) && AllUpdatedItemsZero();

    private bool AllUpdatedItemsZero() =>
        UpdatedProductLists?.Items is { } items && items.All(p => p.Quantity <= 0);

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

    public bool HasProducts => ProductLists?.Items is { Count: > 0 };

    private ObservableCollection<ProductItem>? _parsedProducts;

    /// <summary>
    /// Builds the product list once and caches it so quantity changes propagate
    /// back to the UI via INotifyPropertyChanged on each ProductItem.
    /// Items are copied from the payload so mutations don't affect the source.
    /// </summary>
    public ObservableCollection<ProductItem> ParsedProducts =>
        _parsedProducts ??= ParseProductsCore();

    private bool IsQcStatus =>
        string.Equals(PackingStatus, "QC Passed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(PackingStatus, "QC Hold",   StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resets ParsedProducts quantities back to the original required values.
    /// Uses RequiredQuantity set at parse time — no re-parsing needed.
    /// </summary>
    public void ResetToOriginalQuantities()
    {
        if (_parsedProducts == null || ProductLists?.Items is not { Count: > 0 }) return;
        foreach (var item in _parsedProducts)
        {
            item.Quantity         = item.RequiredQuantity;
            item.OriginalQuantity = item.RequiredQuantity;
            item.OrderQcContext   = "";
            item.IsBeingPicked    = false;
        }
    }

    private ObservableCollection<ProductItem> ParseProductsCore()
    {
        var useUpdated = (IsQcHold || (IsPacked && !IsPackedComplete))
                         && UpdatedProductLists?.Items is { Count: > 0 };
        var sourceItems = (useUpdated ? UpdatedProductLists : ProductLists)?.Items;
        if (sourceItems is null or { Count: 0 }) return [];

        // Build required-quantity map from the original list when showing updated quantities.
        Dictionary<string, int>? requiredMap = null;
        if (useUpdated && ProductLists?.Items is { } origItems)
            requiredMap = origItems.ToDictionary(p => p.SellerSku, p => p.Quantity);

        string ctx;
        if (IsPacked)
            ctx = IsPackedComplete ? "Packed Complete" : "Packed";
        else if (IsQcStatus)
            ctx = _packingStatus!;
        else
            ctx = "";

        // Copy items so mutations (Quantity, OrderQcContext, …) don't affect the payload source.
        var list = sourceItems.Select(p => new ProductItem
        {
            Name      = p.Name,
            Variation = p.Variation,
            SellerSku = p.SellerSku,
            Quantity  = p.Quantity,
        }).ToList();

        foreach (var item in list)
        {
            item.OriginalQuantity = item.Quantity;
            item.RequiredQuantity = requiredMap != null && requiredMap.TryGetValue(item.SellerSku, out var req)
                ? req
                : item.Quantity;
            item.OrderQcContext = ctx;
        }
        return new ObservableCollection<ProductItem>(list);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
