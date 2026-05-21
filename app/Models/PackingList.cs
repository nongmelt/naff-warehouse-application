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

[SupportedOSPlatform("windows")]
public class BundleComponentItem : INotifyPropertyChanged
{
    public int ComponentProductId { get; set; }
    public string Name { get; set; } = "";
    public string? Variation { get; set; }
    public string? QcNotes { get; set; }
    public bool HasQcNotes => !string.IsNullOrWhiteSpace(QcNotes);
    public bool HasNoQcNotes => !HasQcNotes;
    public string SellerSku { get; set; } = "";

    public string SubRowNumber { get; set; } = "";

    private int _requiredQuantity;
    public int RequiredQuantity
    {
        get => _requiredQuantity;
        set { _requiredQuantity = value; OnPropertyChanged(); OnPropertyChanged(nameof(VerifiedQuantity)); OnPropertyChanged(nameof(IsFullyVerified)); OnPropertyChanged(nameof(IsPartiallyVerified)); OnPropertyChanged(nameof(RemainingQuantity)); OnPropertyChanged(nameof(ProgressText)); OnPropertyChanged(nameof(StripColor)); OnPropertyChanged(nameof(ComponentCardBg)); OnPropertyChanged(nameof(ComponentBorderColor)); OnPropertyChanged(nameof(ComponentBorderWidth)); OnPropertyChanged(nameof(QtyBadgeBg)); OnPropertyChanged(nameof(QtyTextColor)); }
    }

    private int _verifiedQuantity;
    public int VerifiedQuantity
    {
        get => _verifiedQuantity;
        set { _verifiedQuantity = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsFullyVerified)); OnPropertyChanged(nameof(IsPartiallyVerified)); OnPropertyChanged(nameof(RemainingQuantity)); OnPropertyChanged(nameof(ProgressText)); OnPropertyChanged(nameof(StripColor)); OnPropertyChanged(nameof(ComponentCardBg)); OnPropertyChanged(nameof(ComponentBorderColor)); OnPropertyChanged(nameof(ComponentBorderWidth)); OnPropertyChanged(nameof(QtyBadgeBg)); OnPropertyChanged(nameof(QtyTextColor)); OnPropertyChanged(nameof(SkuPillBg)); OnPropertyChanged(nameof(SkuPillBorder)); OnPropertyChanged(nameof(SkuPillText)); OnPropertyChanged(nameof(VariationBadgeBg)); OnPropertyChanged(nameof(VariationBadgeTextColor)); }
    }

    public int RemainingQuantity => RequiredQuantity - VerifiedQuantity;
    public bool IsFullyVerified => VerifiedQuantity >= RequiredQuantity;
    public string ProgressText => $"{VerifiedQuantity}/{RequiredQuantity}";

    public List<string> AllSkus { get; set; } = [];
    public bool HasMultipleSkus => AllSkus.Count > 1;
    public IEnumerable<string> AltSkus => AllSkus.Where(s => !string.Equals(s, SellerSku, StringComparison.OrdinalIgnoreCase));
    public string AltSkusDisplay => HasMultipleSkus ? string.Join("\n", AltSkus.Take(3)) : "";

    public bool IsPartiallyVerified => !IsFullyVerified && VerifiedQuantity > 0;
    public Color StripColor => IsFullyVerified ? Color.FromArgb("#86efac")
        : IsPartiallyVerified ? Color.FromArgb("#fdba74") : Color.FromArgb("#e5e7eb");
    public Color ComponentCardBg => _isActiveComponent ? Color.FromArgb("#F5F3FF")
        : IsFullyVerified ? Color.FromArgb("#ECFDF5")
        : IsPartiallyVerified ? Color.FromArgb("#FFF7ED") : Colors.White;
    public Color ComponentBorderColor => _isActiveComponent ? Color.FromArgb("#a78bfa")
        : IsFullyVerified ? Color.FromArgb("#86efac")
        : IsPartiallyVerified ? Color.FromArgb("#fdba74") : Colors.Transparent;
    public int ComponentBorderWidth => _isActiveComponent ? 2
        : (IsFullyVerified || IsPartiallyVerified) ? 1 : 0;
    public Color QtyBadgeBg => IsFullyVerified ? Color.FromArgb("#dcfce7")
        : IsPartiallyVerified ? Color.FromArgb("#ffedd5") : Color.FromArgb("#f3f4f6");
    public Color QtyTextColor => IsFullyVerified ? Color.FromArgb("#166534")
        : IsPartiallyVerified ? Color.FromArgb("#c2410c") : Color.FromArgb("#374151");
    public Color SkuPillBg => IsFullyVerified ? Color.FromArgb("#dcfce7")
        : VerifiedQuantity > 0 ? Color.FromArgb("#ffedd5") : Color.FromArgb("#ede9fe");
    public Color SkuPillBorder => IsFullyVerified ? Color.FromArgb("#86efac")
        : VerifiedQuantity > 0 ? Color.FromArgb("#fdba74") : Color.FromArgb("#c4b5fd");
    public Color SkuPillText => IsFullyVerified ? Color.FromArgb("#166534")
        : VerifiedQuantity > 0 ? Color.FromArgb("#c2410c") : Color.FromArgb("#7c3aed");
    public List<string> SkuPillSkus => AllSkus.Count > 0 ? AllSkus : string.IsNullOrEmpty(SellerSku) ? [] : [SellerSku];
    public bool HasSkuPills => SkuPillSkus.Count > 0;
    public bool HasNoSkuPills => !HasSkuPills;

    public bool HasVariation => !string.IsNullOrWhiteSpace(Variation);
    public Color VariationBadgeBg => IsFullyVerified ? Color.FromArgb("#f0fdf4")
        : IsPartiallyVerified ? Color.FromArgb("#fefce8")
        : Color.FromArgb("#f5f3ff");
    public Color VariationBadgeTextColor => IsFullyVerified ? Color.FromArgb("#166534")
        : IsPartiallyVerified ? Color.FromArgb("#92400e")
        : Color.FromArgb("#5b21b6");
    public Color VariationBorderColor => IsFullyVerified ? Color.FromArgb("#22c55e")
        : IsPartiallyVerified ? Color.FromArgb("#f59e0b") : Color.FromArgb("#a78bfa");
    public Color? SwatchColor { get; set; }
    public bool HasSwatch => SwatchColor != null;
    public Color? SwatchColor2 { get; set; }
    public bool HasSwatch2 => SwatchColor2 != null;
    public bool HasNoImage => !HasImage;

    private ImageSource? _imageSource;
    public ImageSource? ImageSource
    {
        get => _imageSource;
        set { _imageSource = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasImage)); OnPropertyChanged(nameof(HasNoImage)); }
    }
    public bool HasImage => _imageSource != null;

    private bool _isActiveComponent;
    public bool IsActiveComponent
    {
        get => _isActiveComponent;
        set
        {
            _isActiveComponent = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ComponentCardBg));
            OnPropertyChanged(nameof(ComponentBorderColor));
            OnPropertyChanged(nameof(ComponentBorderWidth));
            OnPropertyChanged(nameof(VariationBadgeBg));
            OnPropertyChanged(nameof(VariationBadgeTextColor));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Matches the backend jsonb payload shape: {"items": [...]}.</summary>
public record ProductListPayload([property: JsonPropertyName("items")] List<ProductItem> Items);

public record BundleComponentState(
    [property: JsonPropertyName("seller_sku")] string SellerSku,
    [property: JsonPropertyName("product_name")] string ProductName,
    [property: JsonPropertyName("verified_quantity")] int VerifiedQuantity,
    [property: JsonPropertyName("required_quantity")] int RequiredQuantity);

[SupportedOSPlatform("windows")]
public class ProductItem : INotifyPropertyChanged
{
    [JsonPropertyName("product_name")]      public string Name      { get; set; } = "";
    [JsonPropertyName("product_variation")] public string Variation { get; set; } = "";
    [JsonPropertyName("seller_sku")]        public string SellerSku { get; set; } = "";

    [JsonPropertyName("bundle_components")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<BundleComponentState>? BundleComponentStates { get; set; }

    private int _quantity;
    [JsonPropertyName("quantity")]
    public int Quantity
    {
        get => _quantity;
        set
        {
            _quantity = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(VerifiedQuantity));
            OnPropertyChanged(nameof(IsFullyPicked));
            OnPropertyChanged(nameof(IsCompleted));
            OnPropertyChanged(nameof(IsPartiallyVerified));
            OnPropertyChanged(nameof(CardBgColor));
            OnPropertyChanged(nameof(CardTextColor));
            OnPropertyChanged(nameof(CardBorderColor));
            OnPropertyChanged(nameof(CardBorderWidth));
            OnPropertyChanged(nameof(ButtonBgColor));
            OnPropertyChanged(nameof(ButtonTextColor));
            OnPropertyChanged(nameof(VariationBadgeBg));
            OnPropertyChanged(nameof(VariationBadgeTextColor));
            OnPropertyChanged(nameof(StripColor));
            OnPropertyChanged(nameof(ShowCompletedCheck));
            OnPropertyChanged(nameof(ShowRowNumber));
            OnPropertyChanged(nameof(CompletedQtyText));
            OnPropertyChanged(nameof(SkuPillBg));
            OnPropertyChanged(nameof(SkuPillBorder));
            OnPropertyChanged(nameof(SkuPillText));
        }
    }

    /// <summary>Quantity at load time — used to detect whether any picking occurred.</summary>
    [JsonIgnore] public int OriginalQuantity { get; set; }

    /// <summary>Original required quantity from the order (ProductLists), regardless of picking state.</summary>
    [JsonIgnore] public int RequiredQuantity { get; set; }

    /// <summary>Number of items verified so far (RequiredQuantity − remaining Quantity).</summary>
    [JsonIgnore] public int VerifiedQuantity => RequiredQuantity - Quantity;

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

    [JsonIgnore] public bool IsPartiallyVerified => !IsFullyPicked && VerifiedQuantity > 0;

    [JsonIgnore] public bool IsCompleted => IsFullyPicked || (IsBundle && IsBundleFullyVerified);

    [JsonIgnore] public Color CardBgColor
    {
        get
        {
            if (_isActive)
                return Color.FromArgb("#F5F3FF");

            if (IsCompleted ||
                string.Equals(_orderQcContext, "QC Passed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_orderQcContext, "Packed Complete", StringComparison.OrdinalIgnoreCase))
                return Color.FromArgb("#ECFDF5");

            if (IsPartiallyVerified)
                return Color.FromArgb("#FFF7ED");

            if (string.Equals(_orderQcContext, "QC Hold", StringComparison.OrdinalIgnoreCase))
                return Color.FromArgb("#FFF7ED");

            if (string.Equals(_orderQcContext, "Packed", StringComparison.OrdinalIgnoreCase))
                return Color.FromArgb("#ffedd5");

            return IsBundle ? Color.FromArgb("#F8F7FF") : Colors.White;
        }
    }

    [JsonIgnore] public Color CardTextColor
    {
        get
        {
            if (IsCompleted ||
                string.Equals(_orderQcContext, "QC Passed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_orderQcContext, "Packed Complete", StringComparison.OrdinalIgnoreCase))
                return Color.FromArgb("#166534");
            return Color.FromArgb("#111827");
        }
    }

    [JsonIgnore] public Color CardBorderColor
    {
        get
        {
            if (_isActive) return Color.FromArgb("#a78bfa");
            if (IsBundle && !IsCompleted) return Color.FromArgb("#e0daf7");
            return Colors.Transparent;
        }
    }

    [JsonIgnore] public int CardBorderWidth => _isActive ? 2 : (IsBundle && !IsCompleted) ? 1 : 0;

    [JsonIgnore] public Color ButtonBgColor
    {
        get
        {
            if (IsCompleted) return Color.FromArgb("#dcfce7");
            if (IsPartiallyVerified) return Color.FromArgb("#ffedd5");
            if (_isActive) return Color.FromArgb("#ede9fe");
            return Color.FromArgb("#f3f4f6");
        }
    }

    [JsonIgnore] public Color ButtonTextColor
    {
        get
        {
            if (IsCompleted) return Color.FromArgb("#166534");
            if (IsPartiallyVerified) return Color.FromArgb("#c2410c");
            if (_isActive) return Color.FromArgb("#7c3aed");
            return Color.FromArgb("#374151");
        }
    }

    [JsonIgnore] public Color VariationBadgeBg
    {
        get
        {
            if (IsBundle) return Color.FromArgb("#ede9fe");
            if (IsCompleted) return Color.FromArgb("#f0fdf4");
            if (IsPartiallyVerified) return Color.FromArgb("#fefce8");
            return Color.FromArgb("#f5f3ff");
        }
    }

    [JsonIgnore] public Color VariationBadgeTextColor
    {
        get
        {
            if (IsBundle) return Color.FromArgb("#5b21b6");
            if (IsCompleted) return Color.FromArgb("#166534");
            if (IsPartiallyVerified) return Color.FromArgb("#92400e");
            return Color.FromArgb("#5b21b6");
        }
    }

    [JsonIgnore] public Color VariationBorderColor
    {
        get
        {
            if (IsBundle) return Color.FromArgb("#a78bfa");
            if (IsCompleted) return Color.FromArgb("#22c55e");
            if (IsPartiallyVerified) return Color.FromArgb("#f59e0b");
            return Color.FromArgb("#a78bfa");
        }
    }

    [JsonIgnore] public Color StripColor
    {
        get
        {
            if (IsCompleted ||
                string.Equals(_orderQcContext, "QC Passed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_orderQcContext, "Packed Complete", StringComparison.OrdinalIgnoreCase))
                return Color.FromArgb("#86efac");

            if (_isActive)
                return Color.FromArgb("#a78bfa");

            if (HasQcNotes)
                return Color.FromArgb("#fdba74");

            return CategoryBadgeBg.Alpha > 0 ? CategoryBadgeBg.WithAlpha(0.35f) : Color.FromArgb("#e5e7eb");
        }
    }

    [JsonIgnore] public bool ShowCompletedCheck =>
        IsCompleted ||
        string.Equals(_orderQcContext, "QC Passed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(_orderQcContext, "Packed Complete", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore] public bool ShowRowNumber => !ShowCompletedCheck;
    [JsonIgnore] public string CompletedQtyText => $"{VerifiedQuantity}/{RequiredQuantity}";

    private bool _isBeingPicked;
    [JsonIgnore]
    public bool IsBeingPicked
    {
        get => _isBeingPicked;
        set
        {
            _isBeingPicked = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsNotBeingPicked));
        }
    }

    [JsonIgnore] public bool IsNotBeingPicked => !_isBeingPicked;

    private bool _isActive;
    [JsonIgnore]
    public bool IsActive
    {
        get => _isActive;
        set
        {
            _isActive = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CardBgColor));
            OnPropertyChanged(nameof(CardTextColor));
            OnPropertyChanged(nameof(CardBorderColor));
            OnPropertyChanged(nameof(CardBorderWidth));
            OnPropertyChanged(nameof(ButtonBgColor));
            OnPropertyChanged(nameof(ButtonTextColor));
            OnPropertyChanged(nameof(VariationBadgeBg));
            OnPropertyChanged(nameof(VariationBadgeTextColor));
            OnPropertyChanged(nameof(StripColor));
            OnPropertyChanged(nameof(ShowCompletedCheck));
            OnPropertyChanged(nameof(ShowRowNumber));
            OnPropertyChanged(nameof(CompletedQtyText));
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
        set { _orderQcContext = value; OnPropertyChanged(nameof(CardBgColor)); OnPropertyChanged(nameof(CardTextColor)); OnPropertyChanged(nameof(StripColor)); OnPropertyChanged(nameof(ShowCompletedCheck));
            OnPropertyChanged(nameof(ShowRowNumber));
            OnPropertyChanged(nameof(CompletedQtyText)); OnPropertyChanged(nameof(StatusBadges)); }
    }

    // ── Product catalog enrichment ───────────────────────────────────────────
    [JsonIgnore] public int ProductId { get; set; }
    [JsonIgnore] public string ProductType { get; set; } = "single";
    [JsonIgnore] public bool IsBundle => string.Equals(ProductType, "bundle", StringComparison.OrdinalIgnoreCase);

    private ImageSource? _imageSource;
    [JsonIgnore]
    public ImageSource? ImageSource
    {
        get => _imageSource;
        set { _imageSource = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasImage)); OnPropertyChanged(nameof(HasNoImage)); }
    }
    [JsonIgnore] public bool HasImage => _imageSource != null;
    [JsonIgnore] public bool HasNoImage => !HasImage;

    private bool _isExpanded;
    [JsonIgnore]
    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; OnPropertyChanged(); }
    }

    private ObservableCollection<BundleComponentItem>? _bundleComponents;
    [JsonIgnore]
    public ObservableCollection<BundleComponentItem>? BundleComponents
    {
        get => _bundleComponents;
        set { _bundleComponents = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasBundleComponents)); NotifyBundleProgressChanged(); }
    }
    [JsonIgnore] public bool HasBundleComponents => _bundleComponents is { Count: > 0 };

    [JsonIgnore] public int BundleVerifiedCount => BundleComponents?.Count(c => c.IsFullyVerified) ?? 0;
    [JsonIgnore] public int BundleTotalCount => BundleComponents?.Count ?? 0;
    [JsonIgnore] public string BundleProgressText => $"{BundleVerifiedCount}/{BundleTotalCount} components";
    [JsonIgnore] public double BundleProgressFraction => BundleTotalCount > 0 ? (double)BundleVerifiedCount / BundleTotalCount : 0;
    [JsonIgnore] public double BundleProgressBarWidth => BundleProgressFraction * 80;
    [JsonIgnore] public bool IsBundleFullyVerified => BundleComponents?.All(c => c.IsFullyVerified) ?? false;
    [JsonIgnore] public Color BundleQtyBadgeBg => IsBundleFullyVerified ? Color.FromArgb("#dcfce7") : Color.FromArgb("#f3f4f6");
    [JsonIgnore] public Color BundleQtyTextColor => IsBundleFullyVerified ? Color.FromArgb("#166534") : Color.FromArgb("#374151");
    [JsonIgnore] public IReadOnlyList<Color> BundleComponentDotColors =>
        BundleComponents?.Select(c => c.IsFullyVerified ? Color.FromArgb("#22c55e")
            : c.IsPartiallyVerified ? Color.FromArgb("#f59e0b")
            : Color.FromArgb("#3b82f6")).ToList()
        ?? [];

    public void NotifyBundleProgressChanged()
    {
        OnPropertyChanged(nameof(BundleVerifiedCount));
        OnPropertyChanged(nameof(BundleTotalCount));
        OnPropertyChanged(nameof(BundleProgressText));
        OnPropertyChanged(nameof(BundleProgressFraction));
        OnPropertyChanged(nameof(BundleProgressBarWidth));
        OnPropertyChanged(nameof(IsBundleFullyVerified));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(BundleQtyBadgeBg));
        OnPropertyChanged(nameof(BundleQtyTextColor));
        OnPropertyChanged(nameof(CardBgColor));
        OnPropertyChanged(nameof(CardTextColor));
        OnPropertyChanged(nameof(CardBorderColor));
        OnPropertyChanged(nameof(CardBorderWidth));
        OnPropertyChanged(nameof(StripColor));
        OnPropertyChanged(nameof(ShowCompletedCheck));
            OnPropertyChanged(nameof(ShowRowNumber));
            OnPropertyChanged(nameof(CompletedQtyText));
        OnPropertyChanged(nameof(ButtonBgColor));
        OnPropertyChanged(nameof(ButtonTextColor));
        OnPropertyChanged(nameof(SkuPillBg));
        OnPropertyChanged(nameof(SkuPillBorder));
        OnPropertyChanged(nameof(SkuPillText));
        OnPropertyChanged(nameof(VariationBadgeBg));
        OnPropertyChanged(nameof(VariationBadgeTextColor));
        OnPropertyChanged(nameof(VariationBorderColor));
        OnPropertyChanged(nameof(BundleComponentDotColors));
    }

    public void PopulateBundleComponentStates()
    {
        if (!IsBundle || BundleComponents is not { Count: > 0 })
        {
            BundleComponentStates = null;
            return;
        }
        BundleComponentStates = BundleComponents.Select(c => new BundleComponentState(
            c.SellerSku, c.Name, c.VerifiedQuantity, c.RequiredQuantity)).ToList();
    }

    private bool _isLoadingComponents;
    [JsonIgnore]
    public bool IsLoadingComponents
    {
        get => _isLoadingComponents;
        set { _isLoadingComponents = value; OnPropertyChanged(); }
    }

    private bool _isBundleExpanded = true;
    [JsonIgnore]
    public bool IsBundleExpanded
    {
        get => _isBundleExpanded;
        set { _isBundleExpanded = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsBundleCollapsed)); OnPropertyChanged(nameof(BundleChevron)); }
    }
    [JsonIgnore] public bool IsBundleCollapsed => !_isBundleExpanded;
    [JsonIgnore] public string BundleChevron => _isBundleExpanded ? "▾" : "▸";

    // ── Enrichment (populated after search via EnrichProductsAsync) ──────────

    [JsonIgnore] public string? CategoryName { get; set; }
    [JsonIgnore] public int? CategoryId { get; set; }
    [JsonIgnore] public string? ImagePath { get; set; }
    private string? _qcNotes;
    [JsonIgnore] public string? QcNotes
    {
        get => _qcNotes;
        set { _qcNotes = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasQcNotes)); OnPropertyChanged(nameof(HasNoQcNotes)); }
    }
    [JsonIgnore] public string? Brand { get; set; }

    private List<string> _allSkus = [];
    [JsonIgnore] public List<string> AllSkus
    {
        get => _allSkus;
        set { _allSkus = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasMultipleSkus)); OnPropertyChanged(nameof(SkuPillSkus)); OnPropertyChanged(nameof(HasSkuPills)); OnPropertyChanged(nameof(HasNoSkuPills)); }
    }
    [JsonIgnore] public bool HasMultipleSkus => AllSkus.Count > 1;
    [JsonIgnore] public IEnumerable<string> AltSkus => AllSkus.Where(s => !string.Equals(s, SellerSku, StringComparison.OrdinalIgnoreCase));
    [JsonIgnore] public string AltSkusDisplay => HasMultipleSkus ? string.Join("\n", AltSkus.Take(3)) : "";
    [JsonIgnore] public Color SkuPillBg => IsCompleted ? Color.FromArgb("#dcfce7")
        : IsPartiallyVerified ? Color.FromArgb("#ffedd5") : Color.FromArgb("#ede9fe");
    [JsonIgnore] public Color SkuPillBorder => IsCompleted ? Color.FromArgb("#86efac")
        : IsPartiallyVerified ? Color.FromArgb("#fdba74") : Color.FromArgb("#c4b5fd");
    [JsonIgnore] public Color SkuPillText => IsCompleted ? Color.FromArgb("#166534")
        : IsPartiallyVerified ? Color.FromArgb("#c2410c") : Color.FromArgb("#7c3aed");
    [JsonIgnore] public List<string> SkuPillSkus => AllSkus.Count > 0 ? AllSkus : string.IsNullOrEmpty(SellerSku) ? [] : [SellerSku];
    [JsonIgnore] public bool HasSkuPills => SkuPillSkus.Count > 0;
    [JsonIgnore] public bool HasNoSkuPills => !HasSkuPills;
    [JsonIgnore] public bool IsNotBundle => !IsBundle;

    [JsonIgnore] public bool HasQcNotes => !string.IsNullOrWhiteSpace(QcNotes);
    [JsonIgnore] public bool HasNoQcNotes => string.IsNullOrWhiteSpace(QcNotes);
    [JsonIgnore] public bool HasImagePath => !string.IsNullOrWhiteSpace(ImagePath);

    /// <summary>Category badge text like "TEE-01".</summary>
    [JsonIgnore] public string CategoryBadge { get; set; } = "";

    /// <summary>Category badge background color.</summary>
    [JsonIgnore] public Color CategoryBadgeBg { get; set; } = Colors.Transparent;

    /// <summary>Category badge text color.</summary>
    [JsonIgnore] public Color CategoryBadgeFg { get; set; } = Colors.White;

    private Color? _swatchColor;
    [JsonIgnore] public Color? SwatchColor
    {
        get => _swatchColor;
        set { _swatchColor = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasSwatch)); }
    }

    [JsonIgnore] public bool HasSwatch => SwatchColor != null;

    private Color? _swatchColor2;
    [JsonIgnore] public Color? SwatchColor2
    {
        get => _swatchColor2;
        set { _swatchColor2 = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasSwatch2)); }
    }

    [JsonIgnore] public bool HasSwatch2 => SwatchColor2 != null;

    private string? _localImagePath;
    [JsonIgnore]
    public string? LocalImagePath
    {
        get => _localImagePath;
        set { _localImagePath = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasLocalImage)); }
    }

    [JsonIgnore] public bool HasLocalImage => !string.IsNullOrWhiteSpace(_localImagePath);

    [JsonIgnore] public int RowNumber { get; set; }

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
            if (item.IsBundle && item.BundleComponents != null)
            {
                foreach (var comp in item.BundleComponents)
                    comp.VerifiedQuantity = 0;
                item.NotifyBundleProgressChanged();
            }
        }
    }

    private ObservableCollection<ProductItem> ParseProductsCore()
    {
        var isQcPassed = string.Equals(_packingStatus, "QC Passed", StringComparison.OrdinalIgnoreCase);
        var useUpdated = (IsQcHold || isQcPassed || (IsPacked && !IsPackedComplete))
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
            BundleComponentStates = p.BundleComponentStates,
        }).ToList();

        foreach (var item in list)
        {
            item.OriginalQuantity = item.Quantity;
            item.RequiredQuantity = requiredMap != null && requiredMap.TryGetValue(item.SellerSku, out var req)
                ? req
                : item.Quantity;
            item.OrderQcContext = ctx;
        }

        for (int i = 0; i < list.Count; i++)
            list[i].RowNumber = i + 1;

        return new ObservableCollection<ProductItem>(list);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
