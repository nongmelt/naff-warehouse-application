using app.Helpers;
using app.Models;
using app.Services;
using app.Workflows;
using CommunityToolkit.Maui.Alerts;
using Microsoft.Maui.Controls.Shapes;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Ports;
using System.Net;
using System.Runtime.Versioning;

namespace app.Views;

[SupportedOSPlatform("windows")]
public partial class OrderSearchPage : ContentPage
{
    private enum AppMode { QC, Returns }

    private record ComPortEntry(string PortName, string DisplayName);

    private SerialPort? _serialPort;
    private string? _selectedPortName;
    private List<ComPortEntry> _comPorts = [];
    private bool _isSearching;
    private AppMode _currentMode = AppMode.QC;
    private IDispatcherTimer? _comHeartbeatTimer;
    private long _lastSerialDataTicks;
    private int _historyNavIndex = -1;

    // Returns mode state
    private string _returnsActionType = "return";
    private string? _selectedReturnReason;
    private int? _currentOperatorId;
    private int _returnsReturnedCount;
    private int _returnsPendingCount;
    private readonly Dictionary<int, string> _sessionReturnType = new();
    private readonly string[] _returnReasons = ["Customer request", "Damaged package", "Duplicate order", "Wrong product", "Other"];
    private readonly string[] _pickupReasons = ["Full capacity", "Carrier no-show", "Incomplete paperwork", "Other"];

    // Search-session navigation (back / forward through previous scans)
    private record SearchSession(string Query, List<PackingList> Data);
    private readonly List<SearchSession> _sessions = [];
    private int _sessionIndex = -1;
    private readonly Queue<string> _pendingScanQueue = new();
    private readonly Dictionary<string, List<ProductItem>> _altSkuMap = new(StringComparer.OrdinalIgnoreCase);
    private bool _carouselDirty;

    // Station identifier — computer name, resolved once
    private static readonly string StationName = Environment.MachineName;

    // Full barcode of the logged-in operator — null when no operator active
    private string? _currentOperator;

    // Resolved first name from API — null until lookup completes or no operator active
    private string? _currentOperatorFirstName;

    // Prevents OverlayComPortPicker ↔ ComPortPicker sync from firing recursively
    private bool _syncingPickers;

    // Inactivity auto-logout timer
    private IDispatcherTimer? _inactivityTimer;

    // Falls back to MachineName when no operator is logged in
    private string EffectiveOperator => _currentOperator ?? StationName;

    public string StationNameDisplay => $"Station: {StationName}";

    private PackingList? _currentOrder;
    public PackingList? CurrentOrder
    {
        get => _currentOrder;
        set
        {
            _currentOrder = value;
            OnPropertyChanged(nameof(CurrentOrder));
            OnPropertyChanged(nameof(HasCurrentOrder));
        }
    }
    public bool HasCurrentOrder => _currentOrder != null;

    // SKU picking state — set after an order loads; cleared on new search
    private bool _orderLoaded;
    private bool _isFirstItemScan;
    private ProductItem? _pendingSkuProduct;
    private BundleComponentItem? _activeComponent;
    private readonly HashSet<int> _completedPackingIds = [];

    // Orders that were "To be packed" when first scanned this session — the only ones counted in SessionCard
    private readonly HashSet<int> _qualifiedPackingIds = [];

    // Increments on every QC mutation within a scan session — shipped on workflow_events
    // so analytics can answer "which SKUs get scanned first on average?"
    private int _sequenceInSession;

    // Scan indicator state
    private string? _lastScanBarcode;
    private bool _lastScanFound;
    private DateTime? _lastScanTime;
    private IDispatcherTimer? _lastScanTimer;

    private int NextSequence() => ++_sequenceInSession;

    private string ConsumePickingFromState()
    {
        var s = _isFirstItemScan ? "order-loaded" : "picking";
        _isFirstItemScan = false;
        return s;
    }

    private void EmitQcEvent(
        string stepId,
        string trigger,
        string? trackingNumber,
        string? fromState,
        string? toState,
        Dictionary<string, object?>? payload = null,
        bool bumpSequence = true)
    {
        StationEvents.Emit(
            workflowName: "QC",
            stepId: stepId,
            trigger: trigger,
            trackingNumber: trackingNumber,
            fromState: fromState,
            toState: toState,
            stationId: AppSettings.ResolvedStationId,
            @operator: EffectiveOperator,
            sequenceInSession: bumpSequence ? NextSequence() : _sequenceInSession,
            payload: payload);
    }

    private string? CurrentTrackingNumber =>
        _sessionIndex >= 0 && _sessionIndex < _sessions.Count
            ? _sessions[_sessionIndex].Query
            : null;

    public ObservableCollection<PackingList> Results { get; } = new();
    public ObservableCollection<PackingList> ActiveResults { get; } = new();

    public OrderSearchPage()
    {
        InitializeComponent();
        BindingContext = this;
        ApplyMode(_currentMode);
        RefreshHistoryItems();
        UpdateHistoryHeader();

#if WINDOWS
        HeaderSearchEntry.HandlerChanged += (_, _) =>
        {
            if (HeaderSearchEntry.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.TextBox tb)
            {
                tb.VerticalContentAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center;
                tb.Padding = new Microsoft.UI.Xaml.Thickness(2, 0, 0, 0);
                tb.BorderThickness = new Microsoft.UI.Xaml.Thickness(0);
            }
        };
#endif
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    private bool _warmedUp;

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadComPortsAsync();
        if (!_warmedUp)
        {
            _warmedUp = true;
            _ = ApiService.TestConnectionAsync(); // establish TCP + DB pool in background
        }
#if WINDOWS
        RegisterKeyboardHandler();
#endif
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        CloseSerialPort();
        StopInactivityTimer();
#if WINDOWS
        UnregisterKeyboardHandler();
#endif
    }

    // ── Navigation ───────────────────────────────────────────────────────────

    private async void OnGoHome(object sender, EventArgs e)
        => await Shell.Current.GoToAsync("//home");

    private void OnOpenLogs(object sender, EventArgs e)
        => Process.Start("explorer.exe", FileSystem.AppDataDirectory);

    private async void OnOpenSettings(object sender, EventArgs e)
        => await Navigation.PushModalAsync(new SettingsPage());

    // ── SKU picking ───────────────────────────────────────────────────────────

    private void HandleSkuScan(string barcode)
    {
        bool overlayOpen = ProductImageOverlay.IsVisible;

        // Repeated scan of same SKU while already editing → increment verified target
        if (_pendingSkuProduct != null
            && _pendingSkuProduct.IsBeingPicked
            && (string.Equals(_pendingSkuProduct.SellerSku, barcode, StringComparison.OrdinalIgnoreCase)
                || (_altSkuMap.TryGetValue(barcode, out var pendingParents) && pendingParents.Contains(_pendingSkuProduct))))
        {
            if (int.TryParse(_pendingSkuProduct.PickQtyText, out var cur) && cur < _pendingSkuProduct.RequiredQuantity)
                _pendingSkuProduct.PickQtyText = (cur + 1).ToString();
            if (overlayOpen && int.TryParse(OverlayPickEntry.Text, out var oCur) && oCur < _pendingSkuProduct.RequiredQuantity)
                OverlayPickEntry.Text = (oCur + 1).ToString();
            UpdateScanIndicator(barcode, found: true);
            Logger.Log($"OrderSearch: repeated scan '{barcode}', verified target incremented to {_pendingSkuProduct.PickQtyText}");

            // Auto-apply when target reaches required
            if (int.TryParse(_pendingSkuProduct.PickQtyText, out var newQty) && newQty >= _pendingSkuProduct.RequiredQuantity)
            {
                var item = _pendingSkuProduct;
                ApplyVerifiedOverride(item, item.PickQtyText, "manual_qty_entered", "qty_entered");
                if (overlayOpen) { HideOverlayPickEntry(); SyncOverlayAfterDeduction(item); }
            }

            return;
        }

        // Auto-deduct accumulated qty from previous product — operator switched focus
        FlushPendingDeduction();

        ProductItem? found = null;
        PackingList? foundOrder = null;
        bool blockedByQcPassed = false;

        foreach (var order in Results)
        {
            var match = order.ParsedProducts.FirstOrDefault(p =>
                p.Quantity > 0 &&
                (string.Equals(p.SellerSku, barcode, StringComparison.OrdinalIgnoreCase) ||
                 p.AllSkus.Any(s => string.Equals(s, barcode, StringComparison.OrdinalIgnoreCase))));

            if (match == null) continue;

            if (IsOrderQcPassed(order))
            {
                blockedByQcPassed = true;
                continue;
            }

            found = match;
            foundOrder = order;
            break;
        }

        // Fallback: check alias SKUs (skip bundles — component scan handles those)
        if (found == null && !blockedByQcPassed && _altSkuMap.TryGetValue(barcode, out var mappedProducts))
        {
            foreach (var candidate in mappedProducts)
            {
                if (candidate.IsBundle) continue;
                if (candidate.Quantity <= 0) continue;
                var mappedOrder = FindOrderForItem(candidate);
                if (mappedOrder == null || IsOrderQcPassed(mappedOrder)) continue;
                found = candidate;
                foundOrder = mappedOrder;
                break;
            }
        }

        // Component-level scan: find matching component within bundle products
        BundleComponentItem? foundComponent = null;
        ProductItem? foundBundleParent = null;
        PackingList? foundComponentOrder = null;

        if (found == null && !blockedByQcPassed)
        {
            foreach (var order in Results)
            {
                foreach (var product in order.ParsedProducts)
                {
                    if (!product.IsBundle || product.BundleComponents == null) continue;
                    var comp = product.BundleComponents.FirstOrDefault(c =>
                        !c.IsFullyVerified &&
                        (string.Equals(c.SellerSku, barcode, StringComparison.OrdinalIgnoreCase) ||
                         c.AllSkus.Any(s => string.Equals(s, barcode, StringComparison.OrdinalIgnoreCase))));
                    if (comp == null) continue;
                    if (IsOrderQcPassed(order)) { blockedByQcPassed = true; continue; }
                    foundComponent = comp;
                    foundBundleParent = product;
                    foundComponentOrder = order;
                    break;
                }
                if (foundComponent != null) break;
            }
        }

        if (foundComponent != null)
        {
            HandleComponentScan(foundComponent, foundBundleParent!, foundComponentOrder!, barcode);
            return;
        }

        if (found == null)
        {
            if (!blockedByQcPassed)
            {
                EmitQcEvent(
                    stepId: "item_scan_rejected",
                    trigger: "sku_scan",
                    trackingNumber: CurrentTrackingNumber,
                    fromState: "picking",
                    toState: "picking",
                    payload: new Dictionary<string, object?>
                    {
                        ["sku"] = barcode,
                        ["reason"] = "not-in-order",
                    });
            }
            UpdateScanIndicator(barcode, found: false);
            UpdateSearchStatus(blockedByQcPassed
                ? $"SKU '{barcode}' belongs to a QC Passed order — no changes allowed"
                : $"SKU '{barcode}' not found in this order");
            return;
        }

        UpdateScanIndicator(barcode, found: true);
        SetActiveProduct(found);

        EmitQcEvent(
            stepId: "item_scanned_await_qty",
            trigger: "sku_scan",
            trackingNumber: foundOrder?.TrackingNumber,
            fromState: ConsumePickingFromState(),
            toState: "picking",
            payload: new Dictionary<string, object?>
            {
                ["sku"] = barcode,
                ["qtyRemaining"] = found.Quantity,
            });

        var targetVerified = (found.VerifiedQuantity + 1).ToString();
        found.PickQtyText = targetVerified;
        found.IsBeingPicked = true;

        var label = found.Name + (found.HasVariation ? $" · {found.Variation}" : "");

        // Auto-apply when scan already meets required qty (e.g. qty=1 products)
        if (int.TryParse(targetVerified, out var tv) && tv >= found.RequiredQuantity)
        {
            ShowProductImageOverlay(found, "sku_scan");
            _ = ApplyVerificationThenDismiss(found, targetVerified);
            ScrollToProduct(found);
            UpdateSearchStatus($"✓ {found.SellerSku} fully verified");
            Logger.Log($"OrderSearch: SKU '{barcode}' auto-verified (qty met on first scan)");
            return;
        }

        ShowProductImageOverlay(found, "sku_scan");
        ShowOverlayPickEntry(targetVerified);

        ScrollToProduct(found);
        UpdateSearchStatus($"Matched: {label} — enter qty and confirm");
        Logger.Log($"OrderSearch: SKU matched '{barcode}', qty remaining: {found.Quantity}");
    }

    /// <summary>
    /// Auto-deducts accumulated qty from the pending product when switching away.
    /// Called before setting up a new active product.
    /// </summary>
    private void FlushPendingDeduction()
    {
        if (_pendingSkuProduct == null) return;
        var item = _pendingSkuProduct;
        if (item.IsBeingPicked && int.TryParse(item.PickQtyText, out var qty) && qty > 0)
        {
            ApplyVerifiedOverride(item, item.PickQtyText, "auto_deducted", "focus_changed");
            if (ProductImageOverlay.IsVisible) { HideOverlayPickEntry(); SyncOverlayAfterDeduction(item); }
        }
        else
        {
            item.IsBeingPicked = false;
            SetActiveProduct(null);
        }
    }

    private PackingList? FindOrderForItem(ProductItem item) =>
        Results.FirstOrDefault(o => o.ParsedProducts.Contains(item));

    private bool IsOrderQcPassed(PackingList order) =>
        _completedPackingIds.Contains(order.PackingId)
        || string.Equals(order.PackingStatus, "QC Passed", StringComparison.OrdinalIgnoreCase);

    private enum DeductionSource
    {
        AutoPrior,     // pending item auto-deducted because user moved on to the next scan/tap
        ScanAuto,      // scan matched a qty=1 row — immediate deduct; event already emitted in HandleSkuScan
        ManualQty,     // user typed a number in the qty entry
        CardTap,       // user tapped the qty area on a card
        KeyboardPlus,  // user pressed + key on keyboard
    }

    private void ApplySkuDeduction(ProductItem item, string? qtyText, DeductionSource source)
    {
        if (!int.TryParse(qtyText?.Trim(), out var qty)) qty = 1;
        qty = Math.Max(0, Math.Min(qty, item.Quantity));

        if (qty == 0)
        {
            item.IsBeingPicked = false;
            if (item == _pendingSkuProduct) SetActiveProduct(null);
            UpdateSearchStatus($"{item.SellerSku} — no deduction (0 entered)");
            return;
        }

        var owner = FindOrderForItem(item);

        // ScanAuto already reported in HandleSkuScan; skip here.
        if (source is DeductionSource.AutoPrior or DeductionSource.ManualQty or DeductionSource.CardTap or DeductionSource.KeyboardPlus)
        {
            var stepId = source switch
            {
                DeductionSource.AutoPrior => "auto_deducted",
                DeductionSource.ManualQty => "manual_qty_entered",
                DeductionSource.KeyboardPlus => "manual_card_clicked",
                _ => "manual_card_clicked",
            };
            var trigger = source switch
            {
                DeductionSource.AutoPrior => "focus_changed",
                DeductionSource.ManualQty => "qty_entered",
                DeductionSource.KeyboardPlus => "keyboard_plus",
                _ => "card_tap_plus",
            };
            var payload = new Dictionary<string, object?>
            {
                ["sku"] = item.SellerSku,
                ["qtyBefore"] = item.Quantity,
                ["qtyAfter"] = item.Quantity - qty,
                ["qtyDeducted"] = qty,
            };
            if (source == DeductionSource.ManualQty) payload["qtyEntered"] = qty;
            EmitQcEvent(
                stepId: stepId,
                trigger: trigger,
                trackingNumber: owner?.TrackingNumber,
                fromState: ConsumePickingFromState(),
                toState: "picking",
                payload: payload);
        }

        item.Quantity -= qty;
        item.IsBeingPicked = false;
        item.OrderQcContext = item.VerifiedQuantity > 0 ? "QC Hold" : "";

        // Cascade to bundle components when parent is fully verified
        if (item.IsBundle && item.IsFullyPicked && item.BundleComponents != null)
        {
            foreach (var comp in item.BundleComponents)
                comp.VerifiedQuantity = comp.RequiredQuantity;
            item.NotifyBundleProgressChanged();
        }

        if (owner != null && string.IsNullOrWhiteSpace(owner.CheckedBy))
        {
            owner.CheckedBy = EffectiveOperator;
            UpdateHeaderOrderInfo();
        }

        if (item == _pendingSkuProduct && item.IsFullyPicked) SetActiveProduct(null);

        // Animate fully-picked item sliding to bottom
        if (item.IsFullyPicked)
            _ = AnimateAndMoveItemToBottomAsync(item);

        UpdateSearchStatus(item.IsFullyPicked
            ? $"✓ {item.SellerSku} fully verified"
            : $"{item.SellerSku} — {item.VerifiedQuantity}/{item.RequiredQuantity} verified");
        Logger.Log($"OrderSearch: verified {qty} for '{item.SellerSku}', now {item.VerifiedQuantity}/{item.RequiredQuantity}");

        _ = CheckAndSaveQcStatusAsync();
    }

    private void ApplyVerifiedOverride(ProductItem item, string? text,
        string stepId = "manual_qty_entered", string trigger = "qty_entered")
    {
        if (item.IsFullyPicked) { item.IsBeingPicked = false; return; }
        if (!int.TryParse(text?.Trim(), out var verified)) { item.IsBeingPicked = false; return; }
        verified = Math.Clamp(verified, 0, item.RequiredQuantity);

        var newRemaining = item.RequiredQuantity - verified;
        if (newRemaining == item.Quantity) { item.IsBeingPicked = false; return; }

        var owner = FindOrderForItem(item);
        var deducted = item.Quantity - newRemaining;

        EmitQcEvent(
            stepId: stepId,
            trigger: trigger,
            trackingNumber: owner?.TrackingNumber,
            fromState: ConsumePickingFromState(),
            toState: "picking",
            payload: new Dictionary<string, object?>
            {
                ["sku"] = item.SellerSku,
                ["qtyBefore"] = item.Quantity,
                ["qtyAfter"] = newRemaining,
                ["qtyEntered"] = verified,
                ["qtyDeducted"] = deducted,
            });

        item.Quantity = newRemaining;
        item.IsBeingPicked = false;
        item.OrderQcContext = item.VerifiedQuantity > 0 ? "QC Hold" : "";

        // Cascade to bundle components when parent is fully verified
        if (item.IsBundle && item.IsFullyPicked && item.BundleComponents != null)
        {
            foreach (var comp in item.BundleComponents)
                comp.VerifiedQuantity = comp.RequiredQuantity;
            item.NotifyBundleProgressChanged();
        }

        if (owner != null && string.IsNullOrWhiteSpace(owner.CheckedBy))
        {
            owner.CheckedBy = EffectiveOperator;
            UpdateHeaderOrderInfo();
        }

        if (item.IsFullyPicked)
        {
            SetActiveProduct(null);
            _ = AnimateAndMoveItemToBottomAsync(item);
        }

        UpdateSearchStatus(item.IsFullyPicked
            ? $"✓ {item.SellerSku} fully verified"
            : $"{item.SellerSku} — {item.VerifiedQuantity}/{item.RequiredQuantity} verified");
        Logger.Log($"OrderSearch: override verified={verified} for '{item.SellerSku}', now {item.VerifiedQuantity}/{item.RequiredQuantity}");

        _ = CheckAndSaveQcStatusAsync();
    }

    private void ApplyComponentVerifiedOverride(ProductItem parent, BundleComponentItem comp, string? text)
    {
        if (comp.IsFullyVerified) return;
        if (!int.TryParse(text?.Trim(), out var verified)) return;
        verified = Math.Clamp(verified, 0, comp.RequiredQuantity);
        if (verified == comp.VerifiedQuantity) return;

        var order = FindOrderForItem(parent);
        if (order == null || IsOrderQcPassed(order)) return;

        var qtyBefore = comp.VerifiedQuantity;
        var fromState = ConsumePickingFromState();
        comp.VerifiedQuantity = verified;
        parent.NotifyBundleProgressChanged();

        EmitQcEvent(
            stepId: "component_qty_entered",
            trigger: "manual_qty_entered",
            trackingNumber: order.TrackingNumber,
            fromState: fromState,
            toState: "picking",
            payload: new Dictionary<string, object?>
            {
                ["sku"] = comp.SellerSku,
                ["componentName"] = comp.Name,
                ["qtyBefore"] = qtyBefore,
                ["qtyAfter"] = verified,
                ["bundleSku"] = parent.SellerSku,
                ["bundleComplete"] = parent.IsBundleFullyVerified,
            });

        if (parent.IsBundleFullyVerified && parent.Quantity > 0)
            parent.Quantity = 0;
        else if (!parent.IsBundleFullyVerified && parent.Quantity <= 0)
            parent.Quantity = parent.RequiredQuantity;

        UpdateSearchStatus(comp.IsFullyVerified
            ? $"✓ {comp.Name} fully verified ({comp.VerifiedQuantity}/{comp.RequiredQuantity})"
            : $"{comp.Name} — {comp.VerifiedQuantity}/{comp.RequiredQuantity} verified");
        Logger.Log($"OrderSearch: component override verified={verified} for '{comp.SellerSku}', now {comp.VerifiedQuantity}/{comp.RequiredQuantity}");

        _ = CheckAndSaveQcStatusAsync();

        if (ProductImageOverlay.IsVisible && _overlayItem == parent)
        {
            if (comp.IsFullyVerified)
                _ = AnimateOverlayComponentCompletion(parent, comp);
            else
                ShowBundleOverlay(parent, comp);
        }
    }

    private async Task CheckAndSaveQcStatusAsync()
    {
        await CheckCompletedOrdersAsync();
        await SaveQcHoldImmediateAsync();
        FlushCarouselIfDirty();
    }

    // ── Component scan verification ─────────────────────────────────────────

    private void HandleComponentScan(BundleComponentItem component, ProductItem bundleParent, PackingList order, string scannedBarcode)
    {
        if (component.IsFullyVerified)
        {
            UpdateSearchStatus($"{component.Name} already verified ({component.VerifiedQuantity}/{component.RequiredQuantity})");
            ShowBundleOverlay(bundleParent, component);
            return;
        }

        var targetVerified = component.VerifiedQuantity + 1;

        // Multi-qty component with remaining > 1: open pick entry for operator to type target qty
        if (component.RemainingQuantity > 1)
        {
            component.VerifiedQuantity++;
            bundleParent.NotifyBundleProgressChanged();

            UpdateScanIndicator(scannedBarcode, found: true);
            ShowBundleOverlay(bundleParent, component);
            ShowOverlayPickEntry(targetVerified.ToString());

            UpdateSearchStatus($"{component.Name} — {component.VerifiedQuantity}/{component.RequiredQuantity}, enter qty and confirm");
            Logger.Log($"OrderSearch: component scan '{scannedBarcode}', awaiting qty input ({component.VerifiedQuantity}/{component.RequiredQuantity})");

            EmitQcEvent(
                stepId: "component_scanned",
                trigger: "sku_scan",
                trackingNumber: order.TrackingNumber,
                fromState: ConsumePickingFromState(),
                toState: "picking",
                payload: new Dictionary<string, object?>
                {
                    ["sku"] = component.SellerSku,
                    ["componentName"] = component.Name,
                    ["verified"] = component.VerifiedQuantity,
                    ["required"] = component.RequiredQuantity,
                    ["bundleSku"] = bundleParent.SellerSku,
                    ["bundleComplete"] = bundleParent.IsBundleFullyVerified,
                    ["awaitingQty"] = true,
                });
            _ = CheckAndSaveQcStatusAsync();
            return;
        }

        // Single remaining: show overlay with current qty, then apply after delay
        UpdateScanIndicator(scannedBarcode, found: true);
        ShowBundleOverlay(bundleParent, component);

        _ = ApplyComponentVerificationThenDismiss(
            component, bundleParent, order, targetVerified);
    }

    private void OnComponentRowTapped(object sender, TappedEventArgs e)
    {
        if (sender is not VisualElement ve || ve.BindingContext is not BundleComponentItem comp) return;
        var parent = FindBundleParentForComponent(comp);
        if (parent != null)
            ShowBundleOverlay(parent, comp);
    }

    private ProductItem? FindBundleParentForComponent(BundleComponentItem comp)
    {
        return Results
            .SelectMany(o => o.ParsedProducts)
            .FirstOrDefault(p => p.IsBundle && p.BundleComponents?.Contains(comp) == true);
    }

    private void ShowBundleOverlay(ProductItem bundleParent, BundleComponentItem? highlightComponent = null)
    {
        _completionDismissCts?.Cancel();
        _overlayItem = bundleParent;

        if (highlightComponent != null && bundleParent.BundleComponents != null)
            _activeComponentIndex = bundleParent.BundleComponents.IndexOf(highlightComponent);
        else
            _activeComponentIndex = -1;

        UpdateOverlayImageForActiveComponent(bundleParent);

        OverlayBundleBar.IsVisible = true;
        OverlayBundleBarLine.IsVisible = true;
        OverlayBundleBarName.Text = bundleParent.BaseName;
        RebuildBundleStepDots(bundleParent);

        OverlayStandardPanel.IsVisible = true;
        OverlayNavHint.IsVisible = true;
        OverlayMinusBtn.IsVisible = true;
        OverlayPlusBtn.IsVisible = true;
        PopulateStandardPanelForBundle(bundleParent);
        HideOverlayPickEntry();

        var order = FindOrderForItem(bundleParent);
        if (order != null)
        {
            var idx = order.ParsedProducts.IndexOf(bundleParent) + 1;
            OverlayItemPosition.Text = $"ITEM {idx:D2} of {order.ParsedProducts.Count}";
        }

        if (!ProductImageOverlay.IsVisible)
        {
            ProductImageOverlay.IsVisible = true;
            ProductImageOverlay.Opacity = 1;
            OverlayCard.Scale = 1;
            OverlayCard.Opacity = 1;
        }

    }

    private void RebuildBundleStepDots(ProductItem bundleParent)
    {
        OverlayBundleStepDots.Children.Clear();
        if (bundleParent.BundleComponents == null) return;

        var verified = 0;
        for (var i = 0; i < bundleParent.BundleComponents.Count; i++)
        {
            var comp = bundleParent.BundleComponents[i];
            if (comp.IsFullyVerified) verified++;

            var dot = new BoxView
            {
                WidthRequest = 10, HeightRequest = 10, CornerRadius = 5,
                Color = comp.IsFullyVerified ? Color.FromArgb("#22c55e")
                    : i == _activeComponentIndex ? Color.FromArgb("#7c3aed")
                    : Color.FromArgb("#3b82f6"),
                VerticalOptions = LayoutOptions.Center,
            };

            if (i == _activeComponentIndex)
            {
                dot.Shadow = new Shadow
                {
                    Brush = Color.FromArgb("#40c4b5fd"),
                    Offset = new Point(0, 0),
                    Radius = 3,
                };
            }

            var dotIndex = i;
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => ActivateOverlayComponent(dotIndex);
            dot.GestureRecognizers.Add(tap);

            OverlayBundleStepDots.Add(dot);
        }

        OverlayBundleBarCounter.Text = $"{verified} / {bundleParent.BundleComponents.Count}";
    }

    private void PopulateStandardPanelForBundle(ProductItem bundleParent)
    {
        BundleComponentItem? comp = null;
        if (_activeComponentIndex >= 0 && bundleParent.BundleComponents != null
            && _activeComponentIndex < bundleParent.BundleComponents.Count)
        {
            comp = bundleParent.BundleComponents[_activeComponentIndex];
        }

        if (comp != null)
        {
            OverlayComponentHint.IsVisible = true;
            OverlayComponentHintText.Text = $"Component of {bundleParent.BaseName}";
        }
        else
        {
            OverlayComponentHint.IsVisible = false;
        }

        if (comp != null)
        {
            OverlayVerifiedQty.Text = comp.VerifiedQuantity.ToString();
            OverlayReqQty.Text = comp.RequiredQuantity.ToString();
            OverlayVerifiedQty.TextColor = OverlayCountColor(comp.VerifiedQuantity, comp.RequiredQuantity);

            OverlayProductName.Text = comp.Name;
            RefreshOverlaySkuPills();

            if (comp.HasVariation)
            {
                OverlayVariationLabel.Text = comp.Variation;
                OverlayVariationLabel.TextColor = comp.VariationBadgeTextColor;
                OverlayVariationBorder.IsVisible = true;
                OverlayVariationBorder.BackgroundColor = comp.VariationBadgeBg;
                OverlayVariationBorder.Stroke = comp.VariationBorderColor;
                OverlayVariationBorder.StrokeThickness = 1.5;
                if (comp.HasSwatch)
                {
                    OverlayVariationSwatch.IsVisible = true;
                    OverlayVariationSwatch.Color = comp.SwatchColor!;
                }
                else
                    OverlayVariationSwatch.IsVisible = false;
                OverlayVariationSwatch2.IsVisible = comp.HasSwatch2;
                if (comp.HasSwatch2)
                    OverlayVariationSwatch2.Color = comp.SwatchColor2!;
            }
            else
                OverlayVariationBorder.IsVisible = false;

            if (comp.HasQcNotes)
            {
                OverlayNotesLabel.Text = $"📢 QC: {comp.QcNotes}";
                OverlayNotesLabel.TextColor = Color.FromArgb("#b45309");
                OverlayNotesBorder.BackgroundColor = Color.FromArgb("#fffbeb");
                OverlayNotesBorder.Stroke = Color.FromArgb("#fbbf24");
                OverlayNotesBorder.StrokeThickness = 1;
            }
            else
            {
                OverlayNotesLabel.Text = "no notes";
                OverlayNotesLabel.TextColor = Color.FromArgb("#d1d5db");
                OverlayNotesBorder.BackgroundColor = Color.FromArgb("#fafafa");
                OverlayNotesBorder.Stroke = Colors.Transparent;
                OverlayNotesBorder.StrokeThickness = 0;
            }
        }
        else
        {
            OverlayVerifiedQty.Text = bundleParent.VerifiedQuantity.ToString();
            OverlayReqQty.Text = bundleParent.RequiredQuantity.ToString();
            OverlayVerifiedQty.TextColor = bundleParent.IsBundleFullyVerified
                ? Color.FromArgb("#10B981")
                : bundleParent.BundleVerifiedCount > 0
                    ? Color.FromArgb("#c2410c")
                    : Color.FromArgb("#111827");

            OverlayProductName.Text = bundleParent.BaseName;
            RefreshOverlaySkuPills();

            if (bundleParent.HasVariation)
            {
                OverlayVariationLabel.Text = bundleParent.Variation;
                OverlayVariationLabel.TextColor = bundleParent.VariationBadgeTextColor;
                OverlayVariationBorder.IsVisible = true;
                OverlayVariationBorder.BackgroundColor = bundleParent.VariationBadgeBg;
                OverlayVariationBorder.Stroke = bundleParent.VariationBorderColor;
                OverlayVariationBorder.StrokeThickness = 1.5;
                if (bundleParent.HasSwatch)
                {
                    OverlayVariationSwatch.IsVisible = true;
                    OverlayVariationSwatch.Color = bundleParent.SwatchColor!;
                }
                else
                    OverlayVariationSwatch.IsVisible = false;
                OverlayVariationSwatch2.IsVisible = bundleParent.HasSwatch2;
                if (bundleParent.HasSwatch2)
                    OverlayVariationSwatch2.Color = bundleParent.SwatchColor2!;
            }
            else
                OverlayVariationBorder.IsVisible = false;

            if (bundleParent.HasQcNotes)
            {
                OverlayNotesLabel.Text = $"📢 QC: {bundleParent.QcNotes}";
                OverlayNotesLabel.TextColor = Color.FromArgb("#b45309");
                OverlayNotesBorder.BackgroundColor = Color.FromArgb("#fffbeb");
                OverlayNotesBorder.Stroke = Color.FromArgb("#fbbf24");
                OverlayNotesBorder.StrokeThickness = 1;
            }
            else
            {
                OverlayNotesLabel.Text = "no notes";
                OverlayNotesLabel.TextColor = Color.FromArgb("#d1d5db");
                OverlayNotesBorder.BackgroundColor = Color.FromArgb("#fafafa");
                OverlayNotesBorder.Stroke = Colors.Transparent;
                OverlayNotesBorder.StrokeThickness = 0;
            }
        }
    }

    private void UpdateOverlayImageForActiveComponent(ProductItem bundleParent)
    {
        BundleComponentItem? activeComp = null;
        if (_activeComponentIndex >= 0 && bundleParent.BundleComponents != null
            && _activeComponentIndex < bundleParent.BundleComponents.Count)
            activeComp = bundleParent.BundleComponents[_activeComponentIndex];

        if (activeComp != null && activeComp.HasImage)
        {
            OverlayImage.Source = activeComp.ImageSource;
            OverlayImage.IsVisible = true;
            OverlayNoImage.IsVisible = false;
            OverlayActiveCompLabel.IsVisible = true;
            OverlayActiveCompText.Text = activeComp.Name;
        }
        else if (bundleParent.HasLocalImage)
        {
            OverlayImage.Source = ImageSource.FromFile(bundleParent.LocalImagePath);
            OverlayImage.IsVisible = true;
            OverlayNoImage.IsVisible = false;
            OverlayActiveCompLabel.IsVisible = _activeComponentIndex >= 0;
            OverlayActiveCompText.Text = activeComp?.Name ?? bundleParent.BaseName;
        }
        else if (bundleParent.HasImage)
        {
            OverlayImage.Source = bundleParent.ImageSource;
            OverlayImage.IsVisible = true;
            OverlayNoImage.IsVisible = false;
            OverlayActiveCompLabel.IsVisible = _activeComponentIndex >= 0;
            OverlayActiveCompText.Text = activeComp?.Name ?? bundleParent.BaseName;
        }
        else
        {
            OverlayImage.IsVisible = false;
            OverlayNoImage.IsVisible = true;
            OverlayActiveCompLabel.IsVisible = false;
        }
    }

    private void ActivateOverlayComponent(int index)
    {
        if (_overlayItem == null || !_overlayItem.IsBundle) return;
        _activeComponentIndex = index;
        UpdateOverlayImageForActiveComponent(_overlayItem);
        RebuildBundleStepDots(_overlayItem);
        PopulateStandardPanelForBundle(_overlayItem);
    }

    /// <summary>Immediately saves partially-picked orders as QC Hold after each deduction.</summary>
    private async Task SaveQcHoldImmediateAsync()
    {
        foreach (var order in Results)
        {
            if (_completedPackingIds.Contains(order.PackingId)) continue;
            if (!order.HasProducts) continue;
            if (order.ParsedProducts.All(p => p.IsFullyPicked)) continue;  // QC Passed path
            if (!order.ParsedProducts.Any(p => p.Quantity != p.OriginalQuantity)) continue;

            var wasHeld = string.Equals(order.PackingStatus, "QC Hold", StringComparison.OrdinalIgnoreCase);
            foreach (var p in order.ParsedProducts)
                p.PopulateBundleComponentStates();
            var payload = new ProductListPayload([.. order.ParsedProducts]);
            var now = DateTime.UtcNow;
            var ok = await ApiService.UpdatePackingStatusAsync(order.PackingId, "QC Hold", payload,
                checkedBy: EffectiveOperator, checkingStationId: AppSettings.ResolvedStationId);
            if (ok)
            {
                var firstHold = !wasHeld;
                order.PackingStatus = "QC Hold";
                order.UpdatedAt = now;
                order.CheckedAt = now;
                // Do NOT set OrderQcContext here — cards stay white while the user is scanning.
                _carouselDirty = true;
                if (firstHold) UpdateHeaderOrderInfo();

                // Only emit on the first transition into QC Hold — subsequent deductions
                // on the same held order would drown out the genuine state change.
                // if (!wasHeld)
                // {
                //     EmitQcEvent(
                //         stepId: "order_held",
                //         trigger: "qty_deduction",
                //         trackingNumber: order.TrackingNumber,
                //         fromState: "picking",
                //         toState: "held",
                //         bumpSequence: false,
                //         payload: new Dictionary<string, object?>
                //         {
                //             ["itemsRemaining"] = order.ParsedProducts.Count(p => !p.IsFullyPicked),
                //         });
                // }
            }
        }
    }

    private async Task CheckCompletedOrdersAsync()
    {
        foreach (var order in Results)
        {
            if (_completedPackingIds.Contains(order.PackingId)) continue;
            if (!order.HasProducts) continue;
            if (!order.ParsedProducts.All(p => p.IsFullyPicked)) continue;
            // Skip if no quantities were actually changed (order was only viewed)
            if (!order.ParsedProducts.Any(p => p.Quantity != p.OriginalQuantity)) continue;

            _completedPackingIds.Add(order.PackingId);
            foreach (var p in order.ParsedProducts)
                p.PopulateBundleComponentStates();
            var payload = new ProductListPayload([.. order.ParsedProducts]);
            var now = DateTime.UtcNow;
            var ok = await ApiService.UpdatePackingStatusAsync(
                order.PackingId, "QC Passed", payload,
                checkedBy: EffectiveOperator, checkingStationId: AppSettings.ResolvedStationId);
            if (ok)
            {
                order.PackingStatus = "QC Passed";
                order.CheckedBy = EffectiveOperator;
                order.UpdatedAt = now;
                order.CheckedAt = now;
                foreach (var p in order.ParsedProducts)
                    p.OrderQcContext = "QC Passed";

                EmitQcEvent(
                    stepId: "order_passed",
                    trigger: "qc_complete",
                    trackingNumber: order.TrackingNumber,
                    fromState: "picking",
                    toState: "passed",
                    payload: new Dictionary<string, object?>
                    {
                        ["checkedBy"] = EffectiveOperator,
                        ["itemsPicked"] = order.ParsedProducts.Sum(p => p.RequiredQuantity),
                    });
            }
            UpdateSearchStatus(ok
                ? $"✓ {order.TrackingNumber} — QC Passed · {EffectiveOperator}"
                : $"⚠ {order.TrackingNumber} — all picked but DB update failed");
            _carouselDirty = true;
        }

        if (Results.Count > 0 && Results.All(o => _completedPackingIds.Contains(o.PackingId) ||
            string.Equals(o.PackingStatus, "QC Passed", StringComparison.OrdinalIgnoreCase)))
        {
            if (ProductImageOverlay.IsVisible)
            {
                for (int i = 0; i < 20 && ProductImageOverlay.IsVisible; i++)
                    await Task.Delay(100);
                if (ProductImageOverlay.IsVisible)
                {
                    ProductImageOverlay.IsVisible = false;
                    _overlayItem = null;
                }
            }
            var totalItems = Results.Sum(o => o.ParsedProducts.Sum(p => p.RequiredQuantity));
            ShowCompletionSummary(totalItems);
        }
    }

    /// <summary>
    /// Called before loading a new search when an order is already active.
    /// Saves any partially-picked orders (with remaining items) as "QC Hold".
    /// Only updates orders where at least one SKU was actually scanned.
    /// </summary>
    private async Task SaveQcHoldForRemainingOrdersAsync(string? newTrackingNumber = null)
    {
        foreach (var order in Results)
        {
            if (_completedPackingIds.Contains(order.PackingId)) continue;
            if (!order.HasProducts) continue;
            if (order.ParsedProducts.All(p => p.IsFullyPicked)) continue; // already QC Passed
            // Skip if nothing was actually picked (order was only viewed)
            if (!order.ParsedProducts.Any(p => p.Quantity != p.OriginalQuantity)) continue;

            foreach (var p in order.ParsedProducts)
                p.PopulateBundleComponentStates();
            var dbPayload = new ProductListPayload([.. order.ParsedProducts]);
            var now = DateTime.UtcNow;
            var ok = await ApiService.UpdatePackingStatusAsync(
                order.PackingId, "QC Hold", dbPayload,
                checkedBy: EffectiveOperator, checkingStationId: AppSettings.ResolvedStationId);
            if (ok)
            {
                order.PackingStatus = "QC Hold";
                order.UpdatedAt = now;
                order.CheckedAt = now;
                foreach (var p in order.ParsedProducts)
                    p.OrderQcContext = "QC Hold";

                var trigger = newTrackingNumber != null ? "new_order_scanned" : "leave_page";
                var eventPayload = new Dictionary<string, object?>
                {
                    ["itemsRemaining"] = order.ParsedProducts.Count(p => !p.IsFullyPicked),
                };
                if (newTrackingNumber != null)
                {
                    eventPayload["newTrackingNumber"] = newTrackingNumber;
                    eventPayload["remainingProducts"] = order.ParsedProducts
                        .Where(p => !p.IsFullyPicked)
                        .Select(p => new Dictionary<string, object?> { ["sku"] = p.SellerSku, ["qtyRemaining"] = p.Quantity })
                        .ToList<object?>();
                }
                EmitQcEvent(
                    stepId: "order_held",
                    trigger: trigger,
                    trackingNumber: order.TrackingNumber,
                    fromState: "picking",
                    toState: "held",
                    bumpSequence: false,
                    payload: eventPayload);
            }
            Logger.Log($"OrderSearch: {order.TrackingNumber} → QC Hold ({(ok ? "saved" : "DB failed")})");
        }
    }

    private static T? FindDescendant<T>(IVisualTreeElement root, Func<T, bool> predicate) where T : VisualElement
    {
        foreach (var child in root.GetVisualChildren())
        {
            if (child is T element && predicate(element)) return element;
            var found = FindDescendant(child, predicate);
            if (found != null) return found;
        }
        return null;
    }

    // ── Quantity tap (manual QC deduction) ───────────────────────────────────

    private void OnQuantityTapped(object sender, TappedEventArgs e)
    {
        ProductItem? item = null;
        if (sender is VisualElement ve)
            item = ve.BindingContext as ProductItem;
        if (item == null) return;
        ShowProductImageOverlay(item, "qty_tap");
    }

    // ── Product image overlay ──────────────────────────────────────────────

    private ProductItem? _overlayItem;
    private int _activeComponentIndex = -1; // -1 = parent, 0..N = component index

    private void OnProductCardTapped(object sender, TappedEventArgs e)
    {
        ProductItem? item = null;
        if (sender is VisualElement ve)
            item = ve.BindingContext as ProductItem;
        if (item == null) return;

        ShowProductImageOverlay(item, "card_click");
    }

    private void ShowProductImageOverlay(ProductItem item, string? openTrigger = null)
    {
        _completionDismissCts?.Cancel();
        if (item.IsBundle)
        {
            ShowBundleOverlay(item);
            if (openTrigger != null)
            {
                var owner = FindOrderForItem(item);
                EmitQcEvent(
                    stepId: "image_peek",
                    trigger: openTrigger,
                    trackingNumber: owner?.TrackingNumber,
                    fromState: _isFirstItemScan ? "order-loaded" : "picking",
                    toState: _isFirstItemScan ? "order-loaded" : "picking",
                    payload: new Dictionary<string, object?> { ["sku"] = item.SellerSku, ["isBundle"] = true });
            }
            return;
        }

        // Ensure standard panel visible, bundle bar hidden
        OverlayStandardPanel.IsVisible = true;
        OverlayBundleBar.IsVisible = false;
        OverlayBundleBarLine.IsVisible = false;
        OverlayNavHint.IsVisible = false;
        OverlayActiveCompLabel.IsVisible = false;
        OverlayComponentHint.IsVisible = false;

        _overlayItem = item;

        // Clear previous image immediately to avoid stale flash
        OverlayImage.Source = null;

        // Image
        if (item.HasLocalImage)
        {
            OverlayImage.Source = ImageSource.FromFile(item.LocalImagePath);
            OverlayImage.IsVisible = true;
            OverlayNoImage.IsVisible = false;
        }
        else if (item.HasImage)
        {
            OverlayImage.Source = item.ImageSource;
            OverlayImage.IsVisible = true;
            OverlayNoImage.IsVisible = false;
        }
        else
        {
            OverlayImage.IsVisible = false;
            OverlayNoImage.IsVisible = true;
        }

        // Item position (e.g., "ITEM 03 of 14")
        var order = FindOrderForItem(item);
        if (order != null)
        {
            var idx = order.ParsedProducts.IndexOf(item) + 1;
            var total = order.ParsedProducts.Count;
            OverlayItemPosition.Text = $"ITEM {idx:D2} of {total}";
        }
        else
        {
            OverlayItemPosition.Text = "";
        }

        // Counter — show verified / required, green when complete
        OverlayVerifiedQty.Text = item.VerifiedQuantity.ToString();
        OverlayReqQty.Text = item.RequiredQuantity.ToString();
        OverlayVerifiedQty.TextColor = OverlayCountColor(item.VerifiedQuantity, item.RequiredQuantity);

        // Product info
        OverlayProductName.Text = item.BaseName;
        RefreshOverlaySkuPills();

        // Variation badge with state-colored text
        if (item.HasVariation)
        {
            OverlayVariationLabel.Text = item.Variation;
            OverlayVariationLabel.TextColor = item.VariationBadgeTextColor;
            OverlayVariationBorder.IsVisible = true;
            OverlayVariationBorder.BackgroundColor = item.VariationBadgeBg;
            OverlayVariationBorder.Stroke = item.VariationBorderColor;
            OverlayVariationBorder.StrokeThickness = 1.5;
            if (item.HasSwatch)
            {
                OverlayVariationSwatch.IsVisible = true;
                OverlayVariationSwatch.Color = item.SwatchColor!;
            }
            else
                OverlayVariationSwatch.IsVisible = false;
            OverlayVariationSwatch2.IsVisible = item.HasSwatch2;
            if (item.HasSwatch2)
                OverlayVariationSwatch2.Color = item.SwatchColor2!;
        }
        else
        {
            OverlayVariationBorder.IsVisible = false;
        }

        if (item.HasQcNotes)
        {
            OverlayNotesLabel.Text = $"📢 QC: {item.QcNotes}";
            OverlayNotesLabel.TextColor = Color.FromArgb("#b45309");
            OverlayNotesBorder.BackgroundColor = Color.FromArgb("#fffbeb");
            OverlayNotesBorder.Stroke = Color.FromArgb("#fbbf24");
            OverlayNotesBorder.StrokeThickness = 1;
        }
        else
        {
            OverlayNotesLabel.Text = "no notes";
            OverlayNotesLabel.TextColor = Color.FromArgb("#d1d5db");
            OverlayNotesBorder.BackgroundColor = Color.FromArgb("#fafafa");
            OverlayNotesBorder.Stroke = Colors.Transparent;
            OverlayNotesBorder.StrokeThickness = 0;
        }

        OverlayPickEntry.IsVisible = false;
        OverlayPickEntry.Text = "";
        OverlayVerifiedQty.IsVisible = true;

        OverlayMinusBtn.IsVisible = true;
        OverlayPlusBtn.IsVisible = true;

        if (!ProductImageOverlay.IsVisible)
        {
            ProductImageOverlay.IsVisible = true;
            ProductImageOverlay.Opacity = 1;
            OverlayCard.Scale = 1;
            OverlayCard.Opacity = 1;
        }

        if (openTrigger != null)
        {
            var owner = FindOrderForItem(item);
            var currentState = _isFirstItemScan ? "order-loaded" : "picking";
            EmitQcEvent(
                stepId: "image_peek",
                trigger: openTrigger,
                trackingNumber: owner?.TrackingNumber,
                fromState: currentState,
                toState: currentState,
                payload: new Dictionary<string, object?>
                {
                    ["sku"] = item.SellerSku,
                });
        }

    }

    /// <summary>
    /// Opens the overlay for the auto-selected sole product, but only once its
    /// image is ready. Product images download in the background after
    /// enrichment, so opening immediately would show a blank image. Wait for the
    /// download to complete; if no image becomes available, skip the auto-open
    /// rather than presenting an empty overlay. When it does open, fade/scale in
    /// so the appearance is not abrupt.
    /// </summary>
    private async Task AutoOpenSingleProductOverlayAsync(ProductItem item)
    {
        if (!item.HasLocalImage && !item.HasImage && item.HasImagePath)
        {
            try
            {
                var apiBase = AppSettings.ApiUrl ?? "http://localhost:8080";
                var path = await ProductImageCache.EnsureAsync(
                    item.SellerSku, apiBase, item.ProductId, item.ProductVersion);
                if (path != null)
                    item.LocalImagePath = path;
            }
            catch (Exception ex)
            {
                Logger.Log($"Auto-open image preload failed ({item.SellerSku}): {ex.Message}");
            }
        }

        // Only auto-open once the image is actually available.
        if (!item.HasLocalImage && !item.HasImage)
            return;

        bool wasHidden = !ProductImageOverlay.IsVisible;
        ShowProductImageOverlay(item, "auto_single_product");

        if (wasHidden)
        {
            // ShowProductImageOverlay snaps the overlay to fully visible; reset and
            // animate in to mirror the dismiss transition (fade + scale).
            ProductImageOverlay.Opacity = 0;
            OverlayCard.Scale = 0.85;
            await Task.WhenAll(
                ProductImageOverlay.FadeToAsync(1, 180, Easing.CubicOut),
                OverlayCard.ScaleToAsync(1, 180, Easing.CubicOut));
        }
    }

    /// <summary>
    /// Color for the large overlay verified/required counter, matching the
    /// normal product card: green when complete, orange when partially
    /// verified, neutral dark when nothing scanned yet.
    /// </summary>
    private static Color OverlayCountColor(int verified, int required)
    {
        if (required > 0 && verified >= required) return Color.FromArgb("#10B981"); // complete
        if (verified > 0) return Color.FromArgb("#c2410c");                          // partial (matches card)
        return Color.FromArgb("#111827");                                            // none scanned
    }

    private void RefreshOverlayQuantity()
    {
        if (_overlayItem == null) return;
        OverlayVerifiedQty.Text = _overlayItem.VerifiedQuantity.ToString();
        OverlayReqQty.Text = _overlayItem.RequiredQuantity.ToString();
        OverlayVerifiedQty.TextColor = OverlayCountColor(_overlayItem.VerifiedQuantity, _overlayItem.RequiredQuantity);
        RefreshOverlaySkuPills();
    }

    private void PopulateOverlaySkuPills(List<string> skus, Color bg, Color border, Color text)
    {
        OverlaySkuPills.Children.Clear();
        foreach (var sku in skus)
        {
            var pill = new Border
            {
                BackgroundColor = bg,
                Stroke = border,
                StrokeThickness = 1,
                StrokeShape = new RoundRectangle { CornerRadius = 12 },
                Padding = new Thickness(10, 4),
                Margin = new Thickness(0, 2, 6, 2),
                Content = new Label
                {
                    Text = sku,
                    FontSize = 20,
                    FontFamily = "Consolas",
                    FontAttributes = FontAttributes.Bold,
                    TextColor = text,
                    LineBreakMode = LineBreakMode.NoWrap,
                }
            };
            OverlaySkuPills.Add(pill);
        }
    }

    private void RefreshOverlaySkuPills()
    {
        if (_overlayItem == null) return;

        if (_overlayItem.IsBundle && _activeComponentIndex >= 0
            && _overlayItem.BundleComponents != null
            && _activeComponentIndex < _overlayItem.BundleComponents.Count)
        {
            var comp = _overlayItem.BundleComponents[_activeComponentIndex];
            PopulateOverlaySkuPills(comp.SkuPillSkus, comp.SkuPillBg, comp.SkuPillBorder, comp.SkuPillText);
        }
        else
        {
            PopulateOverlaySkuPills(_overlayItem.SkuPillSkus, _overlayItem.SkuPillBg, _overlayItem.SkuPillBorder, _overlayItem.SkuPillText);
        }
    }

    private async void OnImageOverlayBackdropTapped(object sender, TappedEventArgs e)
    {
        await DismissImageOverlayAsync();
    }

    private async Task DismissImageOverlayAsync(string closeTrigger = "close_tap")
    {
        var closingItem = _overlayItem;

        await Task.WhenAll(
            ProductImageOverlay.FadeToAsync(0, 180, Easing.CubicIn),
            OverlayCard.ScaleToAsync(0.85, 180, Easing.CubicIn));
        ProductImageOverlay.IsVisible = false;
        HideOverlayPickEntry();
        _overlayItem = null;

        if (closingItem != null)
        {
            var owner = FindOrderForItem(closingItem);
            var currentState = _isFirstItemScan ? "order-loaded" : "picking";
            EmitQcEvent(
                stepId: "image_collapse",
                trigger: closeTrigger,
                trackingNumber: owner?.TrackingNumber,
                fromState: currentState,
                toState: currentState,
                payload: new Dictionary<string, object?>
                {
                    ["sku"] = closingItem.SellerSku,
                });

            if (closeTrigger == "auto_complete")
                ActivateNextUnfinishedProduct(closingItem);
        }
    }

    private void ActivateNextUnfinishedProduct(ProductItem completed)
    {
        var allProducts = Results.SelectMany(o => o.ParsedProducts).ToList();
        var currentIdx = allProducts.IndexOf(completed);
        if (currentIdx < 0) return;

        for (int i = 1; i <= allProducts.Count; i++)
        {
            var candidate = allProducts[(currentIdx + i) % allProducts.Count];
            if (!candidate.IsFullyPicked)
            {
                SetActiveProduct(candidate);
                ScrollToProduct(candidate);
                return;
            }
        }
    }

    private async void OnOverlayImageTapped(object sender, TappedEventArgs e)
    {
        _ = AnimateScanButtonAsync();

        if (_overlayItem?.IsBundle == true && _activeComponentIndex >= 0
            && _overlayItem.BundleComponents is { } comps && _activeComponentIndex < comps.Count)
        {
            var comp = comps[_activeComponentIndex];
            if (comp.IsFullyVerified) return;

            var order = FindOrderForItem(_overlayItem);
            if (order == null || IsOrderQcPassed(order)) return;

            comp.VerifiedQuantity++;
            _overlayItem.NotifyBundleProgressChanged();
            if (_overlayItem.IsBundleFullyVerified && _overlayItem.Quantity > 0)
                _overlayItem.Quantity = 0;
            _ = CheckAndSaveQcStatusAsync();
            if (comp.IsFullyVerified)
                _ = AnimateOverlayComponentCompletion(_overlayItem, comp);
            else
                ShowBundleOverlay(_overlayItem, comp);
            return;
        }

        DoOverlayPlus(DeductionSource.CardTap);
    }

    private async void OnOverlayPlusTapped(object sender, TappedEventArgs e)
    {
        _ = AnimateOverlayBtnAsync(OverlayPlusBtn, "#7C5CF0", "#5B31E0");

        if (_overlayItem?.IsBundle == true && _activeComponentIndex >= 0
            && _overlayItem.BundleComponents is { } comps && _activeComponentIndex < comps.Count)
        {
            var comp = comps[_activeComponentIndex];
            if (comp.IsFullyVerified) return;

            var order = FindOrderForItem(_overlayItem);
            if (order == null || IsOrderQcPassed(order)) return;

            comp.VerifiedQuantity++;
            _overlayItem.NotifyBundleProgressChanged();
            if (_overlayItem.IsBundleFullyVerified && _overlayItem.Quantity > 0)
                _overlayItem.Quantity = 0;

            EmitQcEvent(
                stepId: "component_card_clicked",
                trigger: "overlay_tap_plus",
                trackingNumber: order.TrackingNumber,
                fromState: ConsumePickingFromState(),
                toState: "picking",
                payload: new Dictionary<string, object?>
                {
                    ["sku"] = comp.SellerSku,
                    ["componentName"] = comp.Name,
                    ["verified"] = comp.VerifiedQuantity,
                    ["required"] = comp.RequiredQuantity,
                    ["bundleSku"] = _overlayItem.SellerSku,
                    ["bundleComplete"] = _overlayItem.IsBundleFullyVerified,
                });

            _ = CheckAndSaveQcStatusAsync();
            if (comp.IsFullyVerified)
                _ = AnimateOverlayComponentCompletion(_overlayItem, comp);
            else
                ShowBundleOverlay(_overlayItem, comp);
            return;
        }

        DoOverlayPlus(DeductionSource.CardTap);
    }

    private async void OnOverlayMinusTapped(object sender, TappedEventArgs e)
    {
        _ = AnimateOverlayBtnAsync(OverlayMinusBtn, "#fca5a5", "#ef4444");

        if (_overlayItem?.IsBundle == true && _activeComponentIndex >= 0
            && _overlayItem.BundleComponents is { } comps && _activeComponentIndex < comps.Count)
        {
            var comp = comps[_activeComponentIndex];
            if (comp.IsFullyVerified || comp.VerifiedQuantity <= 0) return;

            var order = FindOrderForItem(_overlayItem);
            if (order == null || IsOrderQcPassed(order)) return;

            EmitQcEvent(
                stepId: "component_card_unclicked",
                trigger: "overlay_tap_minus",
                trackingNumber: order.TrackingNumber,
                fromState: ConsumePickingFromState(),
                toState: "picking",
                payload: new Dictionary<string, object?>
                {
                    ["sku"] = comp.SellerSku,
                    ["componentName"] = comp.Name,
                    ["qtyBefore"] = comp.VerifiedQuantity,
                    ["qtyAfter"] = comp.VerifiedQuantity - 1,
                    ["bundleSku"] = _overlayItem.SellerSku,
                });

            comp.VerifiedQuantity--;
            _overlayItem.NotifyBundleProgressChanged();
            if (!_overlayItem.IsBundleFullyVerified && _overlayItem.Quantity <= 0)
                _overlayItem.Quantity = _overlayItem.RequiredQuantity;
            _ = CheckAndSaveQcStatusAsync();
            ShowBundleOverlay(_overlayItem, comp);
            return;
        }

        DoOverlayMinus("card_tap_minus");
    }

    private void DoOverlayPlus(DeductionSource source)
    {
        if (_overlayItem == null || _overlayItem.Quantity <= 0) return;

        var order = FindOrderForItem(_overlayItem);
        if (order == null || IsOrderQcPassed(order)) return;

        if (_pendingSkuProduct != null && _pendingSkuProduct != _overlayItem)
            FlushPendingDeduction();

        SetActiveProduct(_overlayItem);
        ApplySkuDeduction(_overlayItem, "1", source);
        SyncOverlayAfterDeduction(_overlayItem);
    }

    private void DoOverlayMinus(string trigger)
    {
        if (_overlayItem == null || _overlayItem.IsFullyPicked) return;
        if (_overlayItem.Quantity >= _overlayItem.RequiredQuantity) return;

        var order = FindOrderForItem(_overlayItem);
        if (order == null || IsOrderQcPassed(order)) return;

        if (_pendingSkuProduct != null && _pendingSkuProduct != _overlayItem)
            FlushPendingDeduction();

        SetActiveProduct(_overlayItem);

        EmitQcEvent(
            stepId: "manual_card_unclicked",
            trigger: trigger,
            trackingNumber: order.TrackingNumber,
            fromState: ConsumePickingFromState(),
            toState: "picking",
            payload: new Dictionary<string, object?>
            {
                ["sku"] = _overlayItem.SellerSku,
                ["qtyBefore"] = _overlayItem.Quantity,
                ["qtyAfter"] = _overlayItem.Quantity + 1,
            });

        _overlayItem.Quantity += 1;
        _overlayItem.OrderQcContext = _overlayItem.VerifiedQuantity > 0 ? "QC Hold" : "";
        SyncOverlayAfterDeduction(_overlayItem);
        _ = CheckAndSaveQcStatusAsync();
    }

    private async Task ShowCompletionAndDismiss(ProductItem item)
    {
        _completionDismissCts?.Cancel();
        var cts = _completionDismissCts = new CancellationTokenSource();
        try
        {
            var green = Color.FromArgb("#10B981");
            OverlayCard.Stroke = green;
            OverlayCard.StrokeThickness = 4;
            await Task.Delay(1200, cts.Token);
            OverlayCard.Stroke = Colors.Transparent;
            OverlayCard.StrokeThickness = 0;
            if (ProductImageOverlay.IsVisible)
                await DismissImageOverlayAsync("auto_complete");
            // Slide the completed item (incl. bundle parent) to the bottom, matching the
            // list-card completion path. Overlay completions previously skipped the reorder,
            // so verified bundles collapsed in place instead of moving below the open items.
            // Self-gates to a no-op when the item is already last (standard-via-overlay case).
            _ = AnimateAndMoveItemToBottomAsync(item);
        }
        catch (TaskCanceledException)
        {
            OverlayCard.Stroke = Colors.Transparent;
            OverlayCard.StrokeThickness = 0;
        }
    }

    private async Task ApplyVerificationThenDismiss(ProductItem item, string targetVerified)
    {
        await Task.Delay(400);
        if (item.IsFullyPicked) return;
        ApplyVerifiedOverride(item, targetVerified, "item_scanned_await_qty", "sku_scan");
        RefreshOverlayQuantity();
        if (item.IsFullyPicked)
            await ShowCompletionAndDismiss(item);
        else if (ProductImageOverlay.IsVisible)
            await DismissImageOverlayAsync("auto_complete");
    }

    private async Task ApplyComponentVerificationThenDismiss(
        BundleComponentItem component, ProductItem bundleParent, PackingList order, int targetVerified)
    {
        await Task.Delay(400);

        if (component.IsFullyVerified) return;

        component.VerifiedQuantity = Math.Min(targetVerified, component.RequiredQuantity);
        bundleParent.NotifyBundleProgressChanged();

        if (bundleParent.IsBundleFullyVerified && bundleParent.Quantity > 0)
            bundleParent.Quantity = 0;

        RebuildBundleStepDots(bundleParent);
        PopulateStandardPanelForBundle(bundleParent);

        EmitQcEvent(
            stepId: "component_scanned",
            trigger: "sku_scan",
            trackingNumber: order.TrackingNumber,
            fromState: "picking",
            toState: "picking",
            payload: new Dictionary<string, object?>
            {
                ["sku"] = component.SellerSku,
                ["componentName"] = component.Name,
                ["verified"] = component.VerifiedQuantity,
                ["required"] = component.RequiredQuantity,
                ["bundleSku"] = bundleParent.SellerSku,
                ["bundleComplete"] = bundleParent.IsBundleFullyVerified,
            });

        _ = CheckAndSaveQcStatusAsync();

        if (bundleParent.IsBundleFullyVerified)
        {
            UpdateSearchStatus($"✓ Bundle '{bundleParent.BaseName}' fully verified — all components complete");
            await ShowCompletionAndDismiss(bundleParent);
        }
        else if (component.IsFullyVerified)
        {
            UpdateSearchStatus($"✓ {component.Name} verified ({component.VerifiedQuantity}/{component.RequiredQuantity}) — done!");
            await AnimateComponentCompleteMoment();
            AdvanceToNextUnverifiedComponent(bundleParent, component);
        }
        else
        {
            UpdateSearchStatus($"✓ {component.Name} — {component.VerifiedQuantity}/{component.RequiredQuantity}");
        }
    }

    private async Task AnimateOverlayBtnAsync(Border btn, string flashColor, string restColor)
    {
        btn.BackgroundColor = Color.FromArgb(flashColor);
        await btn.ScaleToAsync(0.90, 80, Easing.CubicIn);
        await btn.ScaleToAsync(1.0, 120, Easing.CubicOut);
        btn.BackgroundColor = Color.FromArgb(restColor);
    }

    private async Task AnimateScanButtonAsync()
    {
        await AnimateOverlayBtnAsync(OverlayPlusBtn, "#7C5CF0", "#5B31E0");
    }

    private CancellationTokenSource? _completionDismissCts;

    private async void OnOverlayCloseTapped(object sender, TappedEventArgs e)
    {
        await DismissImageOverlayAsync();
    }

    private void OnOverlayPrevTapped(object sender, TappedEventArgs e)
        => NavigateOverlayProduct(-1);

    private void OnOverlayNextTapped(object sender, TappedEventArgs e)
        => NavigateOverlayProduct(1);

    private void OnOverlayQtyTapped(object sender, TappedEventArgs e) { }

    private void OnOverlayPickEntryCompleted(object sender, EventArgs e)
    {
        if (_overlayItem == null) return;
        if (_overlayItem.IsBundle && _activeComponentIndex >= 0
            && _overlayItem.BundleComponents is { } pickedComps && _activeComponentIndex < pickedComps.Count)
        {
            ApplyComponentVerifiedOverride(_overlayItem, pickedComps[_activeComponentIndex], OverlayPickEntry.Text);
        }
        else
        {
            ApplyVerifiedOverride(_overlayItem, OverlayPickEntry.Text);
            SyncOverlayAfterDeduction(_overlayItem);
        }
        HideOverlayPickEntry();
    }

    private void OnOverlayPickEntryTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_overlayItem == null) return;
        var raw = e.NewTextValue ?? "";
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (digits != raw) { OverlayPickEntry.Text = digits; return; }
        if (digits.Length > 1 && digits[0] == '0') { OverlayPickEntry.Text = digits.TrimStart('0'); return; }
        if (string.IsNullOrEmpty(digits)) return;
        var maxQty = _overlayItem.RequiredQuantity;
        if (_overlayItem.IsBundle && _activeComponentIndex >= 0
            && _overlayItem.BundleComponents is { } clampComps && _activeComponentIndex < clampComps.Count)
            maxQty = clampComps[_activeComponentIndex].RequiredQuantity;
        if (int.TryParse(digits, out var qty) && qty > maxQty)
            OverlayPickEntry.Text = maxQty.ToString();
    }

    private void SyncOverlayAfterDeduction(ProductItem item)
    {
        OverlayVerifiedQty.Text = item.VerifiedQuantity.ToString();
        OverlayVerifiedQty.TextColor = OverlayCountColor(item.VerifiedQuantity, item.RequiredQuantity);
        RefreshOverlaySkuPills();

        if (item.IsFullyPicked)
            _ = AnimateOverlayCompletionThenAdvance(item);
    }

    private async Task AnimateOverlayCompletionThenAdvance(ProductItem item)
    {
        await ShowCompletionAndDismiss(item);
    }

    private async Task AnimateOverlayComponentCompletion(ProductItem bundleParent, BundleComponentItem comp)
    {
        RebuildBundleStepDots(bundleParent);
        PopulateStandardPanelForBundle(bundleParent);

        if (bundleParent.IsBundleFullyVerified)
        {
            await ShowCompletionAndDismiss(bundleParent);
        }
        else
        {
            await AnimateComponentCompleteMoment();
            AdvanceToNextUnverifiedComponent(bundleParent, comp);
        }
    }

    private async Task AnimateComponentCompleteMoment()
    {
        _completionDismissCts?.Cancel();
        var cts = _completionDismissCts = new CancellationTokenSource();
        try
        {
            var green = Color.FromArgb("#10B981");

            OverlayVerifiedQty.TextColor = green;
            await Task.WhenAll(
                OverlayVerifiedQty.ScaleToAsync(1.3, 80, Easing.SinIn),
                OverlayVerifiedQty.FadeToAsync(0.6, 80));
            await Task.WhenAll(
                OverlayVerifiedQty.ScaleToAsync(1.0, 100, Easing.SinOut),
                OverlayVerifiedQty.FadeToAsync(1.0, 100));

            OverlayCard.Stroke = green;
            OverlayCard.StrokeThickness = 4;
            await Task.Delay(800, cts.Token);
            if (ProductImageOverlay.IsVisible)
            {
                OverlayCard.Stroke = Colors.Transparent;
                OverlayCard.StrokeThickness = 0;
            }
        }
        catch (TaskCanceledException)
        {
            OverlayCard.Stroke = Colors.Transparent;
            OverlayCard.StrokeThickness = 0;
        }
    }

    private void AdvanceToNextUnverifiedComponent(ProductItem bundleParent, BundleComponentItem justCompleted)
    {
        if (bundleParent.BundleComponents == null) return;

        HideOverlayPickEntry();

        var comps = bundleParent.BundleComponents;
        var currentIdx = comps.IndexOf(justCompleted);
        if (currentIdx < 0)
        {
            _activeComponentIndex = -1;
            UpdateOverlayImageForActiveComponent(bundleParent);
            RebuildBundleStepDots(bundleParent);
            PopulateStandardPanelForBundle(bundleParent);
            return;
        }

        for (int i = 1; i <= comps.Count; i++)
        {
            var nextIdx = (currentIdx + i) % comps.Count;
            var candidate = comps[nextIdx];
            if (!candidate.IsFullyVerified)
            {
                _activeComponentIndex = nextIdx;
                UpdateOverlayImageForActiveComponent(bundleParent);
                RebuildBundleStepDots(bundleParent);
                PopulateStandardPanelForBundle(bundleParent);
                return;
            }
        }
    }

    private async Task AnimateBundleCompletionThenAdvance(ProductItem bundleParent)
    {
        RebuildBundleStepDots(bundleParent);
        await ShowCompletionAndDismiss(bundleParent);
    }

    private void ShowOverlayPickEntry(string initialValue)
    {
        OverlayVerifiedQty.IsVisible = false;
        OverlayPickEntry.Text = initialValue;
        OverlayPickEntry.IsVisible = true;
        _ = Dispatcher.DispatchAsync(async () =>
        {
            await Task.Delay(80);
            OverlayPickEntry.Focus();
        });
    }

    private void HideOverlayPickEntry()
    {
        OverlayPickEntry.IsVisible = false;
        OverlayVerifiedQty.IsVisible = true;
    }



    private void NavigateOverlayProduct(int direction)
    {
        if (_overlayItem == null) return;
        var allProducts = Results.SelectMany(o => o.ParsedProducts).ToList();
        var currentIdx = allProducts.IndexOf(_overlayItem);
        if (currentIdx < 0) return;

        int nextIdx = currentIdx + direction;
        if (nextIdx < 0) nextIdx = allProducts.Count - 1;
        if (nextIdx >= allProducts.Count) nextIdx = 0;

        ShowProductImageOverlay(allProducts[nextIdx]);
    }

    // ── Completion summary overlay ───────────────────────────────────────────

    private async void ShowCompletionSummary(int totalItems)
    {
        CompletionCountLabel.Text = totalItems.ToString();
        CompletionProgressBar.WidthRequest = 240;
        CompletionSummaryOverlay.Opacity = 0;
        CompletionSummaryOverlay.IsVisible = true;
        await CompletionSummaryOverlay.FadeToAsync(1, 250, Easing.CubicOut);

        var anim = new Animation(v => CompletionProgressBar.WidthRequest = v, 240, 0);
        anim.Commit(CompletionProgressBar, "CountdownBar", length: 1500, easing: Easing.Linear);

        await Task.Delay(1500);

        if (CompletionSummaryOverlay.IsVisible)
            await DismissCompletionSummaryAsync();
    }

    private async void OnCompletionSummaryBackdropTapped(object sender, TappedEventArgs e)
        => await DismissCompletionSummaryAsync();

    private async Task DismissCompletionSummaryAsync()
    {
        await CompletionSummaryOverlay.FadeToAsync(0, 300, Easing.CubicIn);
        CompletionSummaryOverlay.IsVisible = false;
    }

    // ── Product card hover ───────────────────────────────────────────────────

    private void OnProductCardEntered(object sender, PointerEventArgs e)
    {
        if (sender is PointerGestureRecognizer { Parent: Border card })
        {
            var item = card.BindingContext as ProductItem;
            var baseColor = item?.CardBgColor ?? Colors.White;
            card.BackgroundColor = DarkenColor(baseColor, 0.05f);
        }
    }

    private void OnProductCardExited(object sender, PointerEventArgs e)
    {
        if (sender is PointerGestureRecognizer { Parent: Border card })
        {
            var item = card.BindingContext as ProductItem;
            card.BackgroundColor = item?.CardBgColor ?? Colors.White;
        }
    }

    private static Color DarkenColor(Color c, float amount)
    {
        float r = Math.Max(0, c.Red - amount);
        float g = Math.Max(0, c.Green - amount);
        float b = Math.Max(0, c.Blue - amount);
        return new Color(r, g, b, c.Alpha);
    }

    private void SetActiveProduct(ProductItem? item)
    {
        if (_pendingSkuProduct != null) _pendingSkuProduct.IsActive = false;
        _pendingSkuProduct = item;
        if (item != null) item.IsActive = true;
        if (item == null || !item.IsBundle)
            SetActiveComponent(null);
    }

    private void SetActiveComponent(BundleComponentItem? comp)
    {
        if (_activeComponent != null) _activeComponent.IsActiveComponent = false;
        _activeComponent = comp;
        if (comp != null) comp.IsActiveComponent = true;
    }

    private void ScrollToProduct(ProductItem item)
    {
        var border = FindDescendant<Border>(this, b => b.BindingContext == item);
        if (border != null)
        {
            var y = border.Y;
            var parent = border.Parent as VisualElement;
            while (parent != null && parent != ResultsScroll.Content)
            {
                y += parent.Y;
                parent = parent.Parent as VisualElement;
            }
            _ = ResultsScroll.ScrollToAsync(0, Math.Max(0, y - 100), true);
        }
    }

    private void ScrollToComponent(BundleComponentItem comp)
    {
        var border = FindDescendant<Border>(this, b => b.BindingContext == comp);
        if (border != null)
        {
            var y = border.Y;
            var parent = border.Parent as VisualElement;
            while (parent != null && parent != ResultsScroll.Content)
            {
                y += parent.Y;
                parent = parent.Parent as VisualElement;
            }
            _ = ResultsScroll.ScrollToAsync(0, Math.Max(0, y - 100), true);
        }
    }

    // ── Reset ────────────────────────────────────────────────────────────────

    private async void OnResetClicked(object sender, EventArgs e)
    {
        if (sender is not VisualElement el || el.BindingContext is not PackingList order) return;

        var previousStatus = order.PackingStatus;

        var ok = await ApiService.ResetQcHoldAsync(order.PackingId);
        if (!ok)
        {
            UpdateSearchStatus($"⚠ Reset failed for {order.TrackingNumber}");
            return;
        }

        // Remove from completed set so it can be re-processed
        _completedPackingIds.Remove(order.PackingId);

        // Clear pending pick if it belongs to this order
        if (_pendingSkuProduct != null && order.ParsedProducts.Contains(_pendingSkuProduct))
        {
            _pendingSkuProduct.IsBeingPicked = false;
            SetActiveProduct(null);
        }

        // Reset in-memory state
        order.PackingStatus = "To be packed";
        order.CheckedBy = null;
        order.UpdatedAt = DateTime.UtcNow;
        order.CheckedAt = null;
        order.UpdatedProductLists = null;
        order.ResetToOriginalQuantities();

        EmitQcEvent(
            stepId: "order_reset",
            trigger: "reset_clicked",
            trackingNumber: order.TrackingNumber,
            fromState: string.Equals(previousStatus, "QC Passed", StringComparison.OrdinalIgnoreCase) ? "passed" : "held",
            toState: "idle",
            bumpSequence: false,
            payload: new Dictionary<string, object?>
            {
                ["previousStatus"] = previousStatus,
            });

        UpdateSearchStatus($"↺ {order.TrackingNumber} — reset to original");
        Logger.Log($"OrderSearch: {order.TrackingNumber} → reset QC Hold");
        UpdateHeaderOrderInfo();
        BuildCarouselUI();
        UpdateSessionStats();
    }

    // ── Carousel ──────────────────────────────────────────────────────────────

    private void BuildCarouselUI()
    {
        CarouselLayout.Children.Clear();
        var count = _sessions.Count;

        if (count == 0)
        {
            CarouselLayout.Children.Add(new Label
            {
                Text = "Scanned orders appear here",
                FontSize = 11,
                TextColor = Color.FromArgb("#9ca3af"),
                VerticalOptions = LayoutOptions.Center,
                Margin = new Thickness(8, 0),
            });
            return;
        }

        const int MaxVisibleCards = 25;
        var filteredIndices = new List<int>();
        for (int i = count - 1; i >= 0; i--)
        {
            var ss = ClassifySessionStatus(_sessions[i].Data);
            if (_carouselFilter is null || ss == _carouselFilter)
                filteredIndices.Add(i);
        }

        int activePos = filteredIndices.IndexOf(_sessionIndex);
        int winStart = 0, winEnd = Math.Min(MaxVisibleCards - 1, filteredIndices.Count - 1);
        if (activePos >= 0 && filteredIndices.Count > MaxVisibleCards)
        {
            winStart = Math.Max(0, activePos - MaxVisibleCards / 2);
            winEnd = Math.Min(filteredIndices.Count - 1, winStart + MaxVisibleCards - 1);
            winStart = Math.Max(0, winEnd - MaxVisibleCards + 1);
        }
        var visibleSet = new HashSet<int>(
            filteredIndices.Skip(winStart).Take(winEnd - winStart + 1));

        if (winStart > 0)
            CarouselLayout.Children.Add(new Label
            {
                Text = $"+{winStart}",
                FontSize = 10,
                TextColor = Color.FromArgb("#9ca3af"),
                VerticalOptions = LayoutOptions.Center,
                Margin = new Thickness(4, 0),
            });

        VisualElement? activeCard = null;
        for (int i = count - 1; i >= 0; i--)
        {
            if (!visibleSet.Contains(i)) continue;

            var capturedIdx = i;
            var isActive = i == _sessionIndex;
            var query = _sessions[i].Query;
            var sessionStatus = ClassifySessionStatus(_sessions[i].Data);

            Color bgInactive, bgHover, strokeInactive, titleInactive, idxInactive;

            // Returns mode: color by return type instead of QC status
            if (_currentMode == AppMode.Returns && _sessionReturnType.TryGetValue(i, out var retType))
            {
                if (retType == "return")
                {
                    bgInactive = Color.FromArgb("#fef2f2"); bgHover = Color.FromArgb("#fee2e2");
                    strokeInactive = Color.FromArgb("#fecaca"); titleInactive = Color.FromArgb("#991b1b"); idxInactive = Color.FromArgb("#fca5a5");
                }
                else
                {
                    bgInactive = Color.FromArgb("#fdf4ff"); bgHover = Color.FromArgb("#fae8ff");
                    strokeInactive = Color.FromArgb("#e9d5ff"); titleInactive = Color.FromArgb("#86198f"); idxInactive = Color.FromArgb("#d8b4fe");
                }
            }
            else if (sessionStatus == "preProcessed")
            {
                bgInactive = Color.FromArgb("#f9fafb"); bgHover = Color.FromArgb("#f3f4f6");
                strokeInactive = Color.FromArgb("#e5e7eb"); titleInactive = Color.FromArgb("#6b7280"); idxInactive = Color.FromArgb("#9ca3af");
            }
            else if (sessionStatus == "completed")
            {
                bgInactive = Color.FromArgb("#f0fdf4"); bgHover = Color.FromArgb("#dcfce7");
                strokeInactive = Color.FromArgb("#bbf7d0"); titleInactive = Color.FromArgb("#166534"); idxInactive = Color.FromArgb("#86efac");
            }
            else if (sessionStatus == "incomplete")
            {
                bgInactive = Color.FromArgb("#fffbeb"); bgHover = Color.FromArgb("#fef3c7");
                strokeInactive = Color.FromArgb("#fde68a"); titleInactive = Color.FromArgb("#b45309"); idxInactive = Color.FromArgb("#fcd34d");
            }
            else
            {
                bgInactive = Color.FromArgb("#eff6ff"); bgHover = Color.FromArgb("#dbeafe");
                strokeInactive = Color.FromArgb("#bfdbfe"); titleInactive = Color.FromArgb("#1d4ed8"); idxInactive = Color.FromArgb("#93c5fd");
            }

            Color bgColor = isActive ? Color.FromArgb("#f0fdf4") : bgInactive;
            Color strokeColor = isActive ? Color.FromArgb("#2563eb") : strokeInactive;
            Color titleColor = isActive ? Color.FromArgb("#111827") : titleInactive;
            Color indexColor = isActive ? Color.FromArgb("#2563eb") : idxInactive;

            var rawPlatform = _sessions[i].Data.FirstOrDefault()?.Platform ?? "";
            string? platformName = rawPlatform.ToLower() switch
            {
                var p when p.Contains("shopee") => "Shopee",
                var p when p.Contains("lazada") => "Lazada",
                var p when p.Contains("tiktok") => "TikTok",
                _ => null,
            };
            Color platformTagColor = platformName switch
            {
                "Shopee" => Color.FromArgb("#EE4D2D"),
                "Lazada" => Color.FromArgb("#0F146D"),
                "TikTok" => Color.FromArgb("#000000"),
                _ => Colors.Transparent,
            };

            // Single-line layout: [#Index] [Platform Badge] [Tracking Number]
            var row = new HorizontalStackLayout { Spacing = 6, VerticalOptions = LayoutOptions.Center };

            row.Children.Add(new Label
            {
                Text = $"#{i + 1}",
                FontSize = 11,
                FontAttributes = FontAttributes.Bold,
                TextColor = indexColor,
                VerticalOptions = LayoutOptions.Center,
            });

            if (platformName != null)
            {
                row.Children.Add(new Border
                {
                    BackgroundColor = platformTagColor,
                    StrokeThickness = 0,
                    StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(2) },
                    Padding = new Thickness(4, 1),
                    HeightRequest = 16,
                    VerticalOptions = LayoutOptions.Center,
                    Content = new Label
                    {
                        Text = platformName,
                        FontSize = 8,
                        FontAttributes = FontAttributes.Bold,
                        TextColor = Colors.White,
                        VerticalOptions = LayoutOptions.Center,
                    },
                });
            }

            row.Children.Add(new Label
            {
                Text = query,
                FontSize = 11,
                FontAttributes = FontAttributes.Bold,
                TextColor = titleColor,
                LineBreakMode = LineBreakMode.NoWrap,
                VerticalOptions = LayoutOptions.Center,
            });

            // Carrier label in Returns mode
            if (_currentMode == AppMode.Returns)
            {
                var carrierName = _sessions[i].Data.FirstOrDefault()?.ShippingOptions;
                if (!string.IsNullOrWhiteSpace(carrierName))
                {
                    row.Children.Add(new Label
                    {
                        Text = carrierName,
                        FontSize = 9,
                        FontAttributes = FontAttributes.Bold,
                        TextColor = Color.FromArgb("#6b7280"),
                        BackgroundColor = Color.FromArgb("#f3f4f6"),
                        Padding = new Thickness(4, 1),
                        VerticalOptions = LayoutOptions.Center,
                    });
                }
            }

            var card = new Border
            {
                StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(4) },
                Stroke = strokeColor,
                StrokeThickness = isActive ? 2 : 1,
                BackgroundColor = bgColor,
                Padding = new Thickness(8, 4),
                VerticalOptions = LayoutOptions.Center,
                Content = row,
            };

            if (!isActive)
            {
                var capturedBg = bgInactive;
                var capturedHover = bgHover;
                var ptr = new PointerGestureRecognizer();
                ptr.PointerEntered += (_, _) => card.BackgroundColor = capturedHover;
                ptr.PointerExited += (_, _) => card.BackgroundColor = capturedBg;
                card.GestureRecognizers.Add(ptr);
            }

            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => NavigateToSession(capturedIdx);
            card.GestureRecognizers.Add(tap);

            if (isActive) activeCard = card;
            CarouselLayout.Children.Add(card);
        }

        int olderOverflow = filteredIndices.Count - 1 - winEnd;
        if (olderOverflow > 0)
            CarouselLayout.Children.Add(new Label
            {
                Text = $"+{olderOverflow}",
                FontSize = 10,
                TextColor = Color.FromArgb("#9ca3af"),
                VerticalOptions = LayoutOptions.Center,
                Margin = new Thickness(4, 0),
            });

        if (activeCard is not null)
            _ = Dispatcher.DispatchAsync(async () =>
            {
                await Task.Delay(80);
                await NavRow.ScrollToAsync(activeCard, ScrollToPosition.MakeVisible, animated: true);
            });
    }

    private void FlushCarouselIfDirty()
    {
        if (!_carouselDirty) return;
        _carouselDirty = false;
        BuildCarouselUI();
        UpdateSessionStats();
    }

    private void OnCarouselPrevTapped(object? sender, TappedEventArgs e) => NavigateSession(+1);
    private void OnCarouselNextTapped(object? sender, TappedEventArgs e) => NavigateSession(-1);

    // Returns "preProcessed" | "completed" | "incomplete" | "incoming"
    private string ClassifySessionStatus(List<PackingList> data)
    {
        var qualified = data
            .Where(o => _qualifiedPackingIds.Contains(o.PackingId))
            .ToList();
        if (qualified.Count == 0)
            return "preProcessed";
        if (qualified.All(o => string.Equals(o.PackingStatus, "QC Passed", StringComparison.OrdinalIgnoreCase)))
            return "completed";
        if (qualified.Any(o => string.Equals(o.PackingStatus, "QC Hold", StringComparison.OrdinalIgnoreCase)))
            return "incomplete";
        return "incoming";
    }

    private async Task AnimateAllCarouselCardsAsync()
    {
        var children = CarouselLayout.Children.OfType<VisualElement>().ToList();
        if (children.Count == 0 || children[0] is not Border) return;

        children[0].TranslationX = -260;
        children[0].Opacity = 0;
        await SlideCardInAsync(children[0], 0);
    }

    private static async Task SlideCardInAsync(VisualElement card, int delayMs)
    {
        if (delayMs > 0) await Task.Delay(delayMs);
        await Task.WhenAll(
            card.TranslateToAsync(0, 0, 300, Easing.SinOut),
            card.FadeToAsync(1.0, 260, Easing.SinIn));
    }

    // ── Session badge filter ──────────────────────────────────────────────────

    private void OnSessionCompletedBadgeTapped(object sender, TappedEventArgs e)
    {
        _carouselFilter = _carouselFilter == "completed" ? null : "completed";
        UpdateBadgeFilterState();
        BuildCarouselUI();
    }

    private void OnSessionIncompleteBadgeTapped(object sender, TappedEventArgs e)
    {
        _carouselFilter = _carouselFilter == "incomplete" ? null : "incomplete";
        UpdateBadgeFilterState();
        BuildCarouselUI();
    }

    private void OnSessionIncomingBadgeTapped(object sender, TappedEventArgs e)
    {
        _carouselFilter = _carouselFilter == "incoming" ? null : "incoming";
        UpdateBadgeFilterState();
        BuildCarouselUI();
    }

    private void UpdateBadgeFilterState()
    {
        SessionCompletedBadge.Opacity = _carouselFilter is null or "completed" ? 1.0 : 0.4;
        SessionIncompleteBadge.Opacity = _carouselFilter is null or "incomplete" ? 1.0 : 0.4;
        SessionIncomingBadge.Opacity = _carouselFilter is null or "incoming" ? 1.0 : 0.4;
    }

    private void NavigateToSession(int index)
    {
        if (_sessions.Count == 0 || index == _sessionIndex) return;

        if (_pendingSkuProduct != null)
            ApplySkuDeduction(_pendingSkuProduct, "1", DeductionSource.AutoPrior);

        var prevSession = _sessions[_sessionIndex];
        string sourceState = GetSessionWorkflowState(prevSession.Data);
        bool hadIncompleteWork = prevSession.Data.Any(o =>
            _qualifiedPackingIds.Contains(o.PackingId) &&
            !string.Equals(o.PackingStatus, "QC Passed", StringComparison.OrdinalIgnoreCase) &&
            o.ParsedProducts.Any(p => p.Quantity != p.OriginalQuantity));

        // Detect order-loaded: no item scans done yet and all qualified orders still "To be packed"
        bool sourceInOrderLoaded = _isFirstItemScan && _orderLoaded &&
            prevSession.Data.Where(o => _qualifiedPackingIds.Contains(o.PackingId))
                            .All(o => string.Equals(o.PackingStatus, "To be packed", StringComparison.OrdinalIgnoreCase));
        string sourceFromState = sourceInOrderLoaded ? "order-loaded" : sourceState;
        string sourceToState = sourceInOrderLoaded ? "idle" : "held";
        string sourceStepId = sourceInOrderLoaded ? "order_abandoned"
            : hadIncompleteWork ? "incomplete_order_abandoned"
            : "order_held";

        _sessionIndex = index;
        var session = _sessions[_sessionIndex];
        string destFromState = GetSessionWorkflowState(session.Data);

        Results.Clear();
        ActiveResults.Clear();
        foreach (var r in session.Data) { Results.Add(r); ActiveResults.Add(r); }
        UpdateHeaderOrderInfo();

        foreach (var pl in Results)
        {
            if (pl.HasProducts && pl.ParsedProducts.Any(p => p.ProductId == 0))
                _ = EnrichProductItemsAsync(pl.ParsedProducts);
        }

        _orderLoaded = Results.Count > 0;
        _isFirstItemScan = _orderLoaded;
        if (Results.Count > 0)
        {
            var firstProduct = Results[0].ParsedProducts.FirstOrDefault();
            if (firstProduct != null) SetActiveProduct(firstProduct);
        }
        NotFoundCard.IsVisible = Results.Count == 0;
        NotFoundLabel.Text = Results.Count == 0 ? $"{session.Query} not found" : "";
        UpdateSearchStatus(session.Query);
        BuildCarouselUI();
        UpdateSessionStats();
        _ = ResultsScroll.ScrollToAsync(0, 0, false);

        // Emit source order leaving (skip if already idle or passed — nothing to record)
        if (!string.Equals(sourceState, "passed", StringComparison.Ordinal) &&
            !string.Equals(sourceState, "idle", StringComparison.Ordinal))
            EmitQcEvent(
                stepId: sourceStepId,
                trigger: "order_card_selected",
                trackingNumber: prevSession.Query,
                fromState: sourceFromState,
                toState: sourceToState,
                bumpSequence: false,
                payload: new Dictionary<string, object?>
                {
                    ["toTrackingNumber"] = session.Query,
                    ["hadIncompleteWork"] = hadIncompleteWork,
                });

        // Emit destination order loading
        if (!string.Equals(destFromState, "passed", StringComparison.Ordinal))
            EmitQcEvent(
                stepId: "session_navigated",
                trigger: "order_card_selected",
                trackingNumber: session.Query,
                fromState: destFromState,
                toState: "order-loaded",
                bumpSequence: false,
                payload: new Dictionary<string, object?>
                {
                    ["fromTrackingNumber"] = prevSession.Query,
                });
    }

    private string GetSessionWorkflowState(List<PackingList> orders)
    {
        var qualified = orders.Where(o => _qualifiedPackingIds.Contains(o.PackingId)).ToList();
        if (qualified.Count == 0) return "idle";
        if (qualified.All(o => string.Equals(o.PackingStatus, "QC Passed", StringComparison.OrdinalIgnoreCase)))
            return "passed";
        if (qualified.Any(o => string.Equals(o.PackingStatus, "QC Hold", StringComparison.OrdinalIgnoreCase)))
            return "held";
        return "picking";
    }

    private void NavigateSession(int delta)
    {
        if (_sessions.Count == 0) return;
        NavigateToSession(Math.Clamp(_sessionIndex + delta, 0, _sessions.Count - 1));
    }

#if WINDOWS
    private void RegisterKeyboardHandler()
    {
        if (Application.Current?.Windows is { Count: > 0 } wins &&
            wins[0].Handler?.PlatformView is Microsoft.UI.Xaml.Window w)
            // PreviewKeyDown (tunneling) fires before the ScrollViewer acts on arrow keys,
            // so e.Handled=true in the bundle-component nav block suppresses list scrolling.
            // Bubbling KeyDown ran too late (ScrollView already scrolled / marked handled).
            w.Content.PreviewKeyDown += OnWindowKeyDown;
    }

    private void UnregisterKeyboardHandler()
    {
        if (Application.Current?.Windows is { Count: > 0 } wins &&
            wins[0].Handler?.PlatformView is Microsoft.UI.Xaml.Window w)
            w.Content.PreviewKeyDown -= OnWindowKeyDown;
    }

    private void OnWindowKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        // Don't intercept while typing in any Entry / TextBox
        if (e.OriginalSource is Microsoft.UI.Xaml.Controls.TextBox tb)
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                if (PopupSearchBar.IsVisible)
                {
                    DismissPopupSearch();
                    e.Handled = true;
                }
                else if (ReferenceEquals(tb, HeaderSearchEntry.Handler?.PlatformView))
                {
                    tb.IsEnabled = false; tb.IsEnabled = true;
                    e.Handled = true;
                }
                else if (OverlayPickEntry.IsVisible && ReferenceEquals(tb, OverlayPickEntry.Handler?.PlatformView))
                {
                    HideOverlayPickEntry();
                    e.Handled = true;
                }
                else if (_pendingSkuProduct != null && _pendingSkuProduct.IsBeingPicked)
                {
                    _pendingSkuProduct.IsBeingPicked = false;
                    tb.IsEnabled = false; tb.IsEnabled = true;
                    e.Handled = true;
                }
            }
            return;
        }

        // "/" focuses search entry (command-palette pattern)
        const Windows.System.VirtualKey VkSlash = (Windows.System.VirtualKey)191;
        if (e.Key == VkSlash)
        {
            ActivateSearchEntry();
            e.Handled = true;
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Escape && ReturnSuccessOverlay.IsVisible)
        {
            _ = DismissReturnSuccessAsync();
            e.Handled = true;
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Escape && CompletionSummaryOverlay.IsVisible)
        {
            _ = DismissCompletionSummaryAsync();
            e.Handled = true;
            return;
        }

        // Returns mode keyboard shortcuts
        if (_currentMode == AppMode.Returns)
        {
            if (e.Key == Windows.System.VirtualKey.R)
            {
                OnSelectReturnType(null, EventArgs.Empty);
                e.Handled = true;
                return;
            }
            if (e.Key == Windows.System.VirtualKey.P)
            {
                OnSelectPendingPickupType(null, EventArgs.Empty);
                e.Handled = true;
                return;
            }
            if (e.Key == Windows.System.VirtualKey.Enter && _selectedReturnReason is not null)
            {
                OnConfirmReturn(null, EventArgs.Empty);
                e.Handled = true;
                return;
            }
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                OnSkipReturn(null, EventArgs.Empty);
                e.Handled = true;
                return;
            }
            if (e.Key >= Windows.System.VirtualKey.Number1 && e.Key <= Windows.System.VirtualKey.Number5)
            {
                var idx = (int)e.Key - (int)Windows.System.VirtualKey.Number1;
                var reasons = _returnsActionType == "return" ? _returnReasons : _pickupReasons;
                if (idx < reasons.Length)
                {
                    _selectedReturnReason = reasons[idx];
                    BuildReturnReasonChips();
                }
                e.Handled = true;
                return;
            }
        }

        // Enter → apply pending qty (entry always shows target verified count)
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            if (ProductImageOverlay.IsVisible && OverlayPickEntry.IsVisible && _overlayItem != null)
            {
                if (_overlayItem.IsBundle && _activeComponentIndex >= 0
                    && _overlayItem.BundleComponents is { } enterComps && _activeComponentIndex < enterComps.Count)
                {
                    ApplyComponentVerifiedOverride(_overlayItem, enterComps[_activeComponentIndex], OverlayPickEntry.Text);
                }
                else
                {
                    ApplyVerifiedOverride(_overlayItem, OverlayPickEntry.Text);
                    SyncOverlayAfterDeduction(_overlayItem);
                }
                HideOverlayPickEntry();
                e.Handled = true;
                return;
            }
        }

        // Number keys → activate qty field on active product (or active component in bundle overlay)
        if (e.Key >= Windows.System.VirtualKey.Number0 && e.Key <= Windows.System.VirtualKey.Number9
            || e.Key >= Windows.System.VirtualKey.NumberPad0 && e.Key <= Windows.System.VirtualKey.NumberPad9)
        {
            int digit = e.Key >= Windows.System.VirtualKey.NumberPad0
                ? (int)e.Key - (int)Windows.System.VirtualKey.NumberPad0
                : (int)e.Key - (int)Windows.System.VirtualKey.Number0;

            if (ProductImageOverlay.IsVisible && _overlayItem != null)
            {
                // Bundle with active component — allow number input if component not fully verified
                if (_overlayItem.IsBundle && _activeComponentIndex >= 0
                    && _overlayItem.BundleComponents is { } numComps && _activeComponentIndex < numComps.Count)
                {
                    var comp = numComps[_activeComponentIndex];
                    if (!comp.IsFullyVerified)
                    {
                        ShowOverlayPickEntry(digit.ToString());
                        e.Handled = true;
                        return;
                    }
                }
                else if (_overlayItem.Quantity > 0)
                {
                    ShowOverlayPickEntry(digit.ToString());
                    e.Handled = true;
                    return;
                }
            }
        }

        if (ProductImageOverlay.IsVisible && _overlayItem != null)
        {
            var overlayLeft = e.Key == Windows.System.VirtualKey.Left;
            var overlayRight = e.Key == Windows.System.VirtualKey.Right;
            if (overlayLeft || overlayRight)
            {
                NavigateOverlayProduct(overlayRight ? 1 : -1);
                e.Handled = true;
                return;
            }
        }

        // UP/DOWN for bundle component navigation
        if (ProductImageOverlay.IsVisible && _overlayItem != null && _overlayItem.IsBundle)
        {
            var overlayUp = e.Key == Windows.System.VirtualKey.Up;
            var overlayDown = e.Key == Windows.System.VirtualKey.Down;
            if (overlayUp || overlayDown)
            {
                var compCount = _overlayItem.BundleComponents?.Count ?? 0;
                if (overlayDown)
                {
                    if (_activeComponentIndex < compCount - 1)
                        ActivateOverlayComponent(_activeComponentIndex + 1);
                }
                else
                {
                    if (_activeComponentIndex > -1)
                        ActivateOverlayComponent(_activeComponentIndex - 1);
                }
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Windows.System.VirtualKey.Escape && ProductImageOverlay.IsVisible)
        {
            _ = DismissImageOverlayAsync("keyboard_escape"); e.Handled = true; return;
        }
        if (e.Key == Windows.System.VirtualKey.I && !ProductImageOverlay.IsVisible)
        {
            var target = _pendingSkuProduct
                ?? Results.SelectMany(o => o.ParsedProducts).FirstOrDefault(p => !p.IsFullyPicked);
            if (target != null) { ShowProductImageOverlay(target, "keyboard_i"); e.Handled = true; return; }
        }

        // +/- keys verify/unverify active product card
        const Windows.System.VirtualKey VkPlus = (Windows.System.VirtualKey)187;  // = / + key
        const Windows.System.VirtualKey VkMinus = (Windows.System.VirtualKey)189; // - / _ key
        var plusTarget = ProductImageOverlay.IsVisible ? _overlayItem : null;
        if ((e.Key == VkPlus || e.Key == Windows.System.VirtualKey.Add) && plusTarget != null)
        {
            if (ProductImageOverlay.IsVisible)
            {
                _ = AnimateOverlayBtnAsync(OverlayPlusBtn, "#7C5CF0", "#5B31E0");
                if (_overlayItem?.IsBundle == true && _activeComponentIndex >= 0
                    && _overlayItem.BundleComponents is { } compsPlus && _activeComponentIndex < compsPlus.Count)
                {
                    var comp = compsPlus[_activeComponentIndex];
                    if (!comp.IsFullyVerified)
                    {
                        var order = FindOrderForItem(_overlayItem);
                        if (order != null && !IsOrderQcPassed(order))
                        {
                            comp.VerifiedQuantity++;
                            _overlayItem.NotifyBundleProgressChanged();
                            if (_overlayItem.IsBundleFullyVerified && _overlayItem.Quantity > 0)
                                _overlayItem.Quantity = 0;

                            EmitQcEvent(
                                stepId: "component_card_clicked",
                                trigger: "keyboard_plus",
                                trackingNumber: order.TrackingNumber,
                                fromState: ConsumePickingFromState(),
                                toState: "picking",
                                payload: new Dictionary<string, object?>
                                {
                                    ["sku"] = comp.SellerSku,
                                    ["componentName"] = comp.Name,
                                    ["verified"] = comp.VerifiedQuantity,
                                    ["required"] = comp.RequiredQuantity,
                                    ["bundleSku"] = _overlayItem.SellerSku,
                                    ["bundleComplete"] = _overlayItem.IsBundleFullyVerified,
                                });

                            _ = CheckAndSaveQcStatusAsync();
                            if (comp.IsFullyVerified)
                                _ = AnimateOverlayComponentCompletion(_overlayItem, comp);
                            else
                                ShowBundleOverlay(_overlayItem, comp);
                        }
                    }
                }
                else
                {
                    DoOverlayPlus(DeductionSource.KeyboardPlus);
                }
            }
            e.Handled = true;
            return;
        }
        if ((e.Key == VkMinus || e.Key == Windows.System.VirtualKey.Subtract) && plusTarget != null)
        {
            if (ProductImageOverlay.IsVisible)
            {
                _ = AnimateOverlayBtnAsync(OverlayMinusBtn, "#fca5a5", "#ef4444");
                if (_overlayItem?.IsBundle == true && _activeComponentIndex >= 0
                    && _overlayItem.BundleComponents is { } compsMinus && _activeComponentIndex < compsMinus.Count)
                {
                    var comp = compsMinus[_activeComponentIndex];
                    if (!comp.IsFullyVerified && comp.VerifiedQuantity > 0)
                    {
                        var order = FindOrderForItem(_overlayItem);
                        if (order != null && !IsOrderQcPassed(order))
                        {
                            EmitQcEvent(
                                stepId: "component_card_unclicked",
                                trigger: "keyboard_minus",
                                trackingNumber: order.TrackingNumber,
                                fromState: ConsumePickingFromState(),
                                toState: "picking",
                                payload: new Dictionary<string, object?>
                                {
                                    ["sku"] = comp.SellerSku,
                                    ["componentName"] = comp.Name,
                                    ["qtyBefore"] = comp.VerifiedQuantity,
                                    ["qtyAfter"] = comp.VerifiedQuantity - 1,
                                    ["bundleSku"] = _overlayItem.SellerSku,
                                });

                            comp.VerifiedQuantity--;
                            _overlayItem.NotifyBundleProgressChanged();
                            if (!_overlayItem.IsBundleFullyVerified && _overlayItem.Quantity <= 0)
                                _overlayItem.Quantity = _overlayItem.RequiredQuantity;
                            _ = CheckAndSaveQcStatusAsync();
                            ShowBundleOverlay(_overlayItem, comp);
                        }
                    }
                }
                else
                {
                    DoOverlayMinus("keyboard_minus");
                }
            }
            e.Handled = true;
            return;
        }

        // Up/Down: navigate product cards when order loaded and overlay closed
        var isUp = e.Key == Windows.System.VirtualKey.Up;
        var isDown = e.Key == Windows.System.VirtualKey.Down;
        if ((isUp || isDown) && _orderLoaded && !ProductImageOverlay.IsVisible && Results.Count > 0)
        {
            var navList = new List<object>();
            foreach (var order in Results)
            {
                foreach (var p in order.ParsedProducts)
                {
                    navList.Add(p);
                    if (p.IsBundle && p.IsBundleExpanded && p.BundleComponents != null)
                    {
                        foreach (var c in p.BundleComponents)
                            navList.Add(c);
                    }
                }
            }

            if (navList.Count > 0)
            {
                object? current = _activeComponent != null ? (object)_activeComponent : _pendingSkuProduct;
                var currentIdx = current != null ? navList.IndexOf(current) : -1;

                int nextIdx;
                if (isDown)
                    nextIdx = currentIdx < navList.Count - 1 ? currentIdx + 1 : 0;
                else
                    nextIdx = currentIdx > 0 ? currentIdx - 1 : navList.Count - 1;

                var next = navList[nextIdx];
                if (next is ProductItem pi)
                {
                    SetActiveComponent(null);
                    SetActiveProduct(pi);
                    ScrollToProduct(pi);
                }
                else if (next is BundleComponentItem ci)
                {
                    SetActiveProduct(null);
                    SetActiveComponent(ci);
                    ScrollToComponent(ci);
                }
                e.Handled = true;
                return;
            }
        }

        // Left/Right: session navigation (carousel)
        var isLeft = e.Key == Windows.System.VirtualKey.Left;
        var isRight = e.Key == Windows.System.VirtualKey.Right;
        if (!isLeft && !isRight) return;

        if (_sessions.Count > 1)
        {
            if (isLeft) NavigateSession(+1);
            else        NavigateSession(-1);
            e.Handled = true;
            return;
        }

        // No sessions yet — cycle recent search queries into the search box
        var items = SearchHistoryService.Instance.Items;
        if (items.Count == 0) return;

        if (isLeft)
        {
            _historyNavIndex = Math.Min(_historyNavIndex + 1, items.Count - 1);
            HeaderSearchEntry.Text = items[_historyNavIndex];
            e.Handled = true;
        }
        else
        {
            if (_historyNavIndex > 0)
            {
                _historyNavIndex--;
                HeaderSearchEntry.Text = items[_historyNavIndex];
            }
            else
            {
                _historyNavIndex = -1;
                HeaderSearchEntry.Text = string.Empty;
            }
            e.Handled = true;
        }
    }
#endif

    // ── Session stats ─────────────────────────────────────────────────────────

    private int _completedStatCount = -1; // -1 forces animation on first update
    private int _incompleteStatCount = -1;
    private int _incomingStatCount = -1;

    // null = no filter; "completed" | "incomplete" | "incoming" = filter carousel by status
    private string? _carouselFilter;

    private void ResetSessionStatCounts()
    {
        _completedStatCount = -1;
        _incompleteStatCount = -1;
        _incomingStatCount = -1;
    }

    private void UpdateSessionStats()
    {
        // Only count orders that were "To be packed" when first scanned (QC work done this session)
        int completed = 0, incomplete = 0, incoming = 0;
        foreach (var session in _sessions)
        {
            foreach (var order in session.Data)
            {
                if (!_qualifiedPackingIds.Contains(order.PackingId)) continue;
                if (string.Equals(order.PackingStatus, "QC Passed", StringComparison.OrdinalIgnoreCase))
                    completed++;
                else if (string.Equals(order.PackingStatus, "QC Hold", StringComparison.OrdinalIgnoreCase))
                    incomplete++;
                else
                    incoming++;
            }
        }

        if (completed != _completedStatCount) { _completedStatCount = completed; _ = AnimateCountLabelAsync(SessionCompletedLabel, completed.ToString()); }
        if (incomplete != _incompleteStatCount) { _incompleteStatCount = incomplete; _ = AnimateCountLabelAsync(SessionIncompleteLabel, incomplete.ToString()); }
        if (incoming != _incomingStatCount) { _incomingStatCount = incoming; _ = AnimateCountLabelAsync(SessionIncomingLabel, incoming.ToString()); }

        int total = completed + incomplete + incoming;
        SessionTotalLabel.Text = total.ToString();
    }

    private static async Task AnimateCountLabelAsync(Label label, string newValue)
    {
        if (!int.TryParse(label.Text, out var from)) from = 0;
        if (!int.TryParse(newValue, out var to)) to = 0;
        if (from == to) return;

        int diff = to - from;
        int step = diff > 0 ? 1 : -1;
        int steps = Math.Abs(diff);

        if (steps <= 8)
        {
            // Progressive step-by-step counting
            for (int n = 0; n < steps; n++)
            {
                from += step;
                await Task.WhenAll(
                    label.ScaleToAsync(1.18, 55, Easing.SinIn),
                    label.FadeToAsync(0.55, 55));
                label.Text = from.ToString();
                await Task.WhenAll(
                    label.ScaleToAsync(1.0, 70, Easing.SinOut),
                    label.FadeToAsync(1.0, 70));
            }
        }
        else
        {
            // Large jump — single fade-out/in
            await Task.WhenAll(label.ScaleToAsync(0.55, 110, Easing.SinIn), label.FadeToAsync(0.0, 110));
            label.Text = newValue;
            await Task.WhenAll(label.ScaleToAsync(1.0, 160, Easing.SinOut), label.FadeToAsync(1.0, 160));
        }
    }

    // ── Item reordering ───────────────────────────────────────────────────────

    private async Task AnimateAndMoveItemToBottomAsync(ProductItem item)
    {
        // Short delay so the green CardBgColor renders before we grab the element
        await Task.Delay(40);

        var completedBorder = FindDescendant<Border>(this, b => b.BindingContext == item && b.IsVisible);
        if (completedBorder == null) { MoveCompletedItemToBottom(item); return; }

        // Locate owner order and item index
        PackingList? ownerOrder = null;
        int itemIndex = -1;
        foreach (var order in Results)
        {
            for (int i = 0; i < order.ParsedProducts.Count; i++)
            {
                if (order.ParsedProducts[i] == item) { ownerOrder = order; itemIndex = i; break; }
            }
            if (ownerOrder != null) break;
        }

        // Nothing to animate if the item is already last
        if (ownerOrder == null || itemIndex == ownerOrder.ParsedProducts.Count - 1)
        {
            MoveCompletedItemToBottom(item);
            return;
        }

        double slideY = completedBorder.Height + 4; // card height + spacing

        // Collect borders of every item that sits after the completed one
        var bordersBelow = new List<Border>();
        for (int i = itemIndex + 1; i < ownerOrder.ParsedProducts.Count; i++)
        {
            var sibling = ownerOrder.ParsedProducts[i];
            var b = FindDescendant<Border>(this, x => x.BindingContext == sibling && x.IsVisible);
            if (b != null) bordersBelow.Add(b);
        }

        // Pre-offset sibling cards so they stay visually in place during the upcoming reorder
        foreach (var b in bordersBelow)
            b.TranslationY = slideY;

        // Phase 1 — completed card slides down + fades out
        await Task.WhenAll(
            completedBorder.TranslateToAsync(0, slideY, 300, Easing.SinIn),
            completedBorder.FadeToAsync(0.0, 300, Easing.SinIn));

        // Reset before reorder (card is invisible, so position jump is hidden)
        completedBorder.TranslationY = 0;

        // Reorder in collection — siblings get new layout positions (shifted up by slideY),
        // but their TranslationY offset keeps them visually where they were
        MoveCompletedItemToBottom(item);

        await Task.Delay(30); // let the layout pass complete

        // Phase 2 — siblings slide up to their new positions,
        //            completed card appears at the bottom with a slide-in
        completedBorder.TranslationY = slideY * 0.6;
        var tasks = bordersBelow.Select(b => b.TranslateToAsync(0, 0, 280, Easing.SinOut)).ToList();
        tasks.Add(completedBorder.TranslateToAsync(0, 0, 280, Easing.SinOut));
        tasks.Add(completedBorder.FadeToAsync(1.0, 280, Easing.SinOut));
        await Task.WhenAll(tasks);
    }

    private void MoveCompletedItemToBottom(ProductItem item)
    {
        foreach (var order in Results)
        {
            var products = order.ParsedProducts;
            if (!products.Contains(item)) continue;
            if (products.Any(p => !p.IsFullyPicked))
            {
                products.Remove(item);
                products.Add(item);
            }
            break;
        }
    }


    private void OnBundleChevronTapped(object sender, TappedEventArgs e)
    {
        ProductItem? item = null;
        if (sender is VisualElement ve)
            item = ve.BindingContext as ProductItem;
        if (item == null || !item.IsBundle) return;
        item.IsBundleExpanded = !item.IsBundleExpanded;
    }

    // ── Operator UI ──────────────────────────────────────────────────────────

    private void UpdateNavOperatorUI(string? displayName)
    {
        if (displayName is null)
        {
            OperatorHeaderBadge.IsVisible = false;
        }
        else
        {
            HeaderComputerOperatorLabel.Text = $"{StationName} · {displayName.ToUpperInvariant()}";
            OperatorHeaderBadge.IsVisible = true;
        }
    }

    private void OnModeBadgeTapped(object sender, TappedEventArgs e)
    {
        ModeDropdownBackdrop.IsVisible = !ModeDropdownBackdrop.IsVisible;
    }

    private void OnModeDropdownBackdropTapped(object? sender, TappedEventArgs e)
    {
        ModeDropdownBackdrop.IsVisible = false;
    }

    private void OnModeSelectQC(object? sender, TappedEventArgs e)
    {
        ApplyMode(AppMode.QC);
        ModeDropdownBackdrop.IsVisible = false;
    }

    private void OnModeSelectReturns(object? sender, EventArgs e)
    {
        ModeDropdownBackdrop.IsVisible = false;
        ApplyMode(AppMode.Returns);
    }

    private static string GetModeDisplayName(AppMode mode) => mode switch
    {
        AppMode.QC => "QC",
        AppMode.Returns => "Returns",
        _ => mode.ToString(),
    };

    private void ApplyMode(AppMode mode)
    {
        _currentMode = mode;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            ModeLabel.Text = GetModeDisplayName(mode);

            bool isReturns = mode == AppMode.Returns;
            bool hasOrder = Results.Count > 0;

            // Mode dropdown checkmarks
            ModeCheckQC.IsVisible = !isReturns;
            ModeOptionQC.BackgroundColor = !isReturns ? Color.FromArgb("#f5f5ff") : Colors.Transparent;
            ModeCheckReturns.IsVisible = isReturns;
            ModeOptionReturns.BackgroundColor = isReturns ? Color.FromArgb("#f5f5ff") : Colors.Transparent;

            ReturnsActionForm.IsVisible = isReturns && hasOrder;
            ReturnsEmptyState.IsVisible = isReturns && !hasOrder;
            ReturnsSuccessCard.IsVisible = false;
            QcShortcuts.IsVisible = !isReturns;
            ReturnsShortcuts.IsVisible = isReturns;
            if (!isReturns)
                TrackingCarrierPill.IsVisible = false;

            // Toggle stat pills
            SessionCompletedBadge.IsVisible = !isReturns;
            SessionIncompleteBadge.IsVisible = !isReturns;
            SessionIncomingBadge.IsVisible = !isReturns;
            ReturnsReturnedBadge.IsVisible = isReturns;
            ReturnsPendingBadge.IsVisible = isReturns;

            // Side panel
            ReturnsSidePanel.IsVisible = isReturns;

            if (isReturns)
            {
                OnSelectReturnType(null, EventArgs.Empty);
                UpdateHeaderOrderInfo();
                UpdateReturnsSidePanel();
            }
        });
    }

    // ── Returns Mode ──────────────────────────────────────────────────────

    private void OnSelectReturnType(object? sender, EventArgs e)
    {
        _returnsActionType = "return";
        _selectedReturnReason = null;

        BtnReturn.Stroke = Color.FromArgb("#dc2626");
        BtnReturn.StrokeThickness = 2;
        BtnReturn.BackgroundColor = Color.FromArgb("#fef2f2");
        BtnPendingPickup.Stroke = Color.FromArgb("#e5e7eb");
        BtnPendingPickup.StrokeThickness = 1;
        BtnPendingPickup.BackgroundColor = Colors.Transparent;

        ReturnsFormIcon.BackgroundColor = Color.FromArgb("#dc2626");
        ReturnsFormIconLabel.Text = "✕";
        ReturnsFormTitle.Text = "Return Details";
        ConfirmReturnBtn.Text = "✕ Confirm Return";
        ConfirmReturnBtn.BackgroundColor = Color.FromArgb("#dc2626");

        ReturnReasonsSection.IsVisible = true;
        PickupReasonsSection.IsVisible = false;
        BuildReturnReasonChips();
    }

    private void OnSelectPendingPickupType(object? sender, EventArgs e)
    {
        _returnsActionType = "pending_pickup";
        _selectedReturnReason = null;

        BtnReturn.Stroke = Color.FromArgb("#e5e7eb");
        BtnReturn.StrokeThickness = 1;
        BtnReturn.BackgroundColor = Colors.Transparent;
        BtnPendingPickup.Stroke = Color.FromArgb("#a21caf");
        BtnPendingPickup.StrokeThickness = 2;
        BtnPendingPickup.BackgroundColor = Color.FromArgb("#fdf4ff");

        ReturnsFormIcon.BackgroundColor = Color.FromArgb("#a21caf");
        ReturnsFormIconLabel.Text = "⏳";
        ReturnsFormTitle.Text = "Pending Pickup";
        ConfirmReturnBtn.Text = "⏳ Mark Pending Pickup";
        ConfirmReturnBtn.BackgroundColor = Color.FromArgb("#a21caf");

        ReturnReasonsSection.IsVisible = false;
        PickupReasonsSection.IsVisible = true;
        BuildReturnReasonChips();
    }

    private void OnReturnBtnPointerEntered(object? sender, PointerEventArgs e)
    {
        if (_returnsActionType != "return")
            BtnReturn.BackgroundColor = Color.FromArgb("#fff5f5");
    }

    private void OnReturnBtnPointerExited(object? sender, PointerEventArgs e)
    {
        if (_returnsActionType != "return")
            BtnReturn.BackgroundColor = Colors.Transparent;
    }

    private void OnPickupBtnPointerEntered(object? sender, PointerEventArgs e)
    {
        if (_returnsActionType != "pending_pickup")
            BtnPendingPickup.BackgroundColor = Color.FromArgb("#fdf4ff");
    }

    private void OnPickupBtnPointerExited(object? sender, PointerEventArgs e)
    {
        if (_returnsActionType != "pending_pickup")
            BtnPendingPickup.BackgroundColor = Colors.Transparent;
    }

    private void BuildReturnReasonChips()
    {
        var container = _returnsActionType == "return" ? ReturnReasonChips : PickupReasonChips;
        container.Children.Clear();
        var reasons = _returnsActionType == "return" ? _returnReasons : _pickupReasons;

        foreach (var reason in reasons)
        {
            var isSelected = reason == _selectedReturnReason;
            var chip = new Border
            {
                BackgroundColor = isSelected
                    ? (_returnsActionType == "return" ? Color.FromArgb("#fef2f2") : Color.FromArgb("#fdf4ff"))
                    : Colors.White,
                Stroke = isSelected
                    ? (_returnsActionType == "return" ? Color.FromArgb("#dc2626") : Color.FromArgb("#a21caf"))
                    : Color.FromArgb("#e5e7eb"),
                StrokeThickness = isSelected ? 2 : 1,
                StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(6) },
                Padding = new Thickness(12, 8),
                Margin = new Thickness(0, 0, 8, 8),
            };

            var label = new Label
            {
                Text = reason,
                FontSize = 13,
                FontAttributes = isSelected ? FontAttributes.Bold : FontAttributes.None,
                TextColor = isSelected
                    ? (_returnsActionType == "return" ? Color.FromArgb("#991b1b") : Color.FromArgb("#86198f"))
                    : Color.FromArgb("#374151"),
                InputTransparent = true,
            };

            chip.Content = label;
            var capturedReason = reason;
            chip.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(() =>
                {
                    _selectedReturnReason = capturedReason;
                    BuildReturnReasonChips();
                }),
            });

            // Hover effect
            var chipRef = chip;
            var defaultBg = chip.BackgroundColor;
            var hoverBg = isSelected ? defaultBg : Color.FromArgb("#f9fafb");
            var ptr = new PointerGestureRecognizer();
            ptr.PointerEntered += (_, _) => { if (chipRef.BackgroundColor == defaultBg) chipRef.BackgroundColor = hoverBg; };
            ptr.PointerExited += (_, _) => chipRef.BackgroundColor = defaultBg;
            chip.GestureRecognizers.Add(ptr);

            container.Children.Add(chip);
        }
    }

    private async void OnConfirmReturn(object? sender, EventArgs e)
    {
        if (_selectedReturnReason is null) return;
        var order = Results.FirstOrDefault();
        if (order is null) return;
        var trackingNumber = CurrentTrackingNumber;
        if (trackingNumber is null) return;

        ConfirmReturnBtn.IsEnabled = false;
        var success = await ApiService.CreateReturnRecordAsync(
            trackingNumber, _returnsActionType, _selectedReturnReason,
            ReturnsNotesEditor.Text, order.ShippingOptions, order.Platform,
            _currentOperatorId, AppSettings.ResolvedStationId);
        ConfirmReturnBtn.IsEnabled = true;

        if (success)
        {
            StationEvents.Emit(
                workflowName: "Returns",
                stepId: "return_recorded",
                trigger: "confirm_button",
                trackingNumber: trackingNumber,
                fromState: "order-loaded",
                toState: _returnsActionType == "return" ? "returned" : "pending-pickup",
                stationId: AppSettings.ResolvedStationId,
                @operator: EffectiveOperator,
                sequenceInSession: 0,
                payload: new Dictionary<string, object?>
                {
                    ["recordType"] = _returnsActionType,
                    ["reason"] = _selectedReturnReason,
                    ["shippingOptions"] = order.ShippingOptions,
                    ["platform"] = order.Platform,
                });

            // Track return type for carousel coloring
            _sessionReturnType[_sessionIndex] = _returnsActionType;

            // Update stat pills
            if (_returnsActionType == "return")
            {
                _returnsReturnedCount++;
                _ = AnimateCountLabelAsync(ReturnsReturnedLabel, _returnsReturnedCount.ToString());
            }
            else
            {
                _returnsPendingCount++;
                _ = AnimateCountLabelAsync(ReturnsPendingLabel, _returnsPendingCount.ToString());
            }

            // Show overlay + transition to inline success state
            ShowReturnSuccessCard(trackingNumber, order.Platform, order.ShippingOptions,
                _selectedReturnReason!, _returnsActionType);
            ShowInlineReturnSuccess(trackingNumber, order.Platform, order.ShippingOptions,
                _selectedReturnReason!, _returnsActionType);

            // Update side panel
            TrackReturnForSidePanel(_selectedReturnReason, order.Platform);
            UpdateReturnsSidePanel();

            _selectedReturnReason = null;
            ReturnsNotesEditor.Text = "";
            BuildReturnReasonChips();

            _carouselDirty = true;
            BuildCarouselUI();
        }
    }

    private void OnSkipReturn(object? sender, EventArgs e)
    {
        _selectedReturnReason = null;
        ReturnsNotesEditor.Text = "";
        BuildReturnReasonChips();
    }

    private async void ShowReturnSuccessCard(string tracking, string? platform, string? carrier, string reason, string actionType)
    {
        bool isReturn = actionType == "return";
        var accentColor = isReturn ? Color.FromArgb("#dc2626") : Color.FromArgb("#a21caf");
        var bgColor = isReturn ? Color.FromArgb("#f0fdf4") : Color.FromArgb("#fdf4ff");
        var titleColor = isReturn ? Color.FromArgb("#166534") : Color.FromArgb("#86198f");

        ReturnSuccessIcon.BackgroundColor = isReturn ? Color.FromArgb("#16a34a") : Color.FromArgb("#a21caf");
        ReturnSuccessIconLabel.Text = isReturn ? "✓" : "⏳";
        ReturnSuccessTitle.Text = isReturn ? "Returned" : "Pending Pickup";
        ReturnSuccessTitle.TextColor = titleColor;
        ReturnSuccessTracking.Text = tracking;
        ReturnSuccessSubtitle.Text = isReturn ? "Ready for next scan..." : "Stays in queue for next carrier visit.";
        ReturnSuccessBar.BackgroundColor = accentColor;

        if (ReturnSuccessOverlay.Children.OfType<Border>().FirstOrDefault() is { } card)
            card.BackgroundColor = bgColor;

        ReturnSuccessTags.Children.Clear();
        void AddTag(string text, Color dotColor, Color textColor)
        {
            var tag = new Border
            {
                BackgroundColor = Colors.White,
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(4) },
                Padding = new Thickness(8, 4),
                Margin = new Thickness(2),
                Content = new HorizontalStackLayout
                {
                    Spacing = 5,
                    Children =
                    {
                        new BoxView { WidthRequest = 6, HeightRequest = 6, CornerRadius = 3, Color = dotColor, VerticalOptions = LayoutOptions.Center },
                        new Label { Text = text, FontSize = 12, FontAttributes = FontAttributes.Bold, TextColor = textColor },
                    }
                }
            };
            ReturnSuccessTags.Children.Add(tag);
        }

        if (!string.IsNullOrWhiteSpace(platform))
        {
            var pLower = platform.ToLowerInvariant();
            var pColor = pLower switch
            {
                var p when p.Contains("shopee") => Color.FromArgb("#ee4d2d"),
                var p when p.Contains("lazada") => Color.FromArgb("#0f146d"),
                var p when p.Contains("tiktok") => Color.FromArgb("#111827"),
                _ => Color.FromArgb("#6b7280"),
            };
            AddTag(platform, pColor, pColor);
        }
        if (!string.IsNullOrWhiteSpace(carrier))
            AddTag(carrier, Color.FromArgb("#ea580c"), Color.FromArgb("#c2410c"));
        AddTag(reason, accentColor, isReturn ? Color.FromArgb("#991b1b") : Color.FromArgb("#86198f"));

        ReturnSuccessBar.WidthRequest = 200;
        ReturnSuccessOverlay.Opacity = 0;
        ReturnSuccessOverlay.IsVisible = true;
        await ReturnSuccessOverlay.FadeToAsync(1, 250, Easing.CubicOut);

        var anim = new Animation(v => ReturnSuccessBar.WidthRequest = v, 200, 0);
        anim.Commit(ReturnSuccessBar, "ReturnCountdown", length: 2000, easing: Easing.Linear);
        await Task.Delay(2000);

        if (ReturnSuccessOverlay.IsVisible)
            await DismissReturnSuccessAsync();
    }

    private async void OnReturnSuccessBackdropTapped(object? sender, TappedEventArgs e)
        => await DismissReturnSuccessAsync();

    private async Task DismissReturnSuccessAsync()
    {
        await ReturnSuccessOverlay.FadeToAsync(0, 300, Easing.CubicIn);
        ReturnSuccessOverlay.IsVisible = false;
    }

    private void ShowInlineReturnSuccess(string tracking, string? platform, string? carrier, string reason, string actionType)
    {
        bool isReturn = actionType == "return";
        var accentColor = isReturn ? Color.FromArgb("#dc2626") : Color.FromArgb("#a21caf");

        // Style the card border
        ReturnsSuccessCard.BackgroundColor = isReturn ? Color.FromArgb("#f0fdf4") : Color.FromArgb("#fdf4ff");
        ReturnsSuccessCard.Stroke = isReturn ? Color.FromArgb("#bbf7d0") : Color.FromArgb("#e9d5ff");

        // Icon
        InlineSuccessIcon.BackgroundColor = isReturn ? Color.FromArgb("#16a34a") : Color.FromArgb("#a21caf");
        InlineSuccessIconLabel.Text = isReturn ? "✓" : "⏳";

        // Title
        InlineSuccessTitle.Text = isReturn ? "Returned" : "Pending Pickup";
        InlineSuccessTitle.TextColor = isReturn ? Color.FromArgb("#166534") : Color.FromArgb("#86198f");
        InlineSuccessTracking.Text = tracking;

        // Subtitle
        InlineSuccessSubtitle.Text = isReturn
            ? "Ready for next scan..."
            : "Stays in queue for next carrier visit.";

        // Tags
        InlineSuccessTags.Children.Clear();
        void AddTag(string text, Color dotColor, Color textColor)
        {
            var tag = new Border
            {
                BackgroundColor = Colors.White,
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(4) },
                Padding = new Thickness(8, 4),
                Margin = new Thickness(2),
                Content = new HorizontalStackLayout
                {
                    Spacing = 5,
                    Children =
                    {
                        new BoxView { WidthRequest = 6, HeightRequest = 6, CornerRadius = 3, Color = dotColor, VerticalOptions = LayoutOptions.Center },
                        new Label { Text = text, FontSize = 12, FontAttributes = FontAttributes.Bold, TextColor = textColor },
                    }
                }
            };
            InlineSuccessTags.Children.Add(tag);
        }

        if (!string.IsNullOrWhiteSpace(platform))
        {
            var pLower = platform.ToLowerInvariant();
            var pColor = pLower switch
            {
                var p when p.Contains("shopee") => Color.FromArgb("#ee4d2d"),
                var p when p.Contains("lazada") => Color.FromArgb("#0f146d"),
                var p when p.Contains("tiktok") => Color.FromArgb("#111827"),
                _ => Color.FromArgb("#6b7280"),
            };
            AddTag(platform, pColor, pColor);
        }
        if (!string.IsNullOrWhiteSpace(carrier))
            AddTag(carrier, Color.FromArgb("#ea580c"), Color.FromArgb("#c2410c"));
        AddTag(reason, accentColor, isReturn ? Color.FromArgb("#991b1b") : Color.FromArgb("#86198f"));

        // Toggle visibility: hide form, show success card
        ReturnsActionForm.IsVisible = false;
        ReturnsSuccessCard.IsVisible = true;
    }

    // ── Returns side panel ──────────────────────────────────────────────────

    private readonly Dictionary<string, int> _returnsReasonCounts = new();
    private readonly Dictionary<string, int> _returnsPlatformCounts = new();

    private void UpdateReturnsSidePanel()
    {
        SidePanelReturnedCount.Text = _returnsReturnedCount.ToString();
        SidePanelPendingCount.Text = _returnsPendingCount.ToString();
        BuildCarrierCountsPanel();
        BuildReasonBreakdown();
        BuildPlatformBreakdown();
    }

    private void TrackReturnForSidePanel(string? reason, string? platform)
    {
        if (!string.IsNullOrWhiteSpace(reason))
        {
            if (!_returnsReasonCounts.TryAdd(reason, 1))
                _returnsReasonCounts[reason]++;
        }
        if (!string.IsNullOrWhiteSpace(platform))
        {
            if (!_returnsPlatformCounts.TryAdd(platform, 1))
                _returnsPlatformCounts[platform]++;
        }
    }

    private void BuildReasonBreakdown()
    {
        ReasonBreakdownPanel.Children.Clear();
        if (_returnsReasonCounts.Count == 0) return;

        int maxCount = _returnsReasonCounts.Values.Max();
        Color[] barColors = [Color.FromArgb("#4318B0"), Color.FromArgb("#f97316"),
            Color.FromArgb("#eab308"), Color.FromArgb("#8b5cf6"), Color.FromArgb("#06b6d4")];
        int colorIdx = 0;

        foreach (var (reason, count) in _returnsReasonCounts.OrderByDescending(x => x.Value))
        {
            double pct = maxCount > 0 ? (double)count / maxCount : 0;
            var barColor = barColors[colorIdx % barColors.Length];
            colorIdx++;

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new(new GridLength(90)),
                    new(GridLength.Star),
                    new(new GridLength(24)),
                },
                ColumnSpacing = 8,
            };

            row.Add(new Label
            {
                Text = reason,
                FontSize = 11,
                TextColor = Color.FromArgb("#374151"),
                HorizontalTextAlignment = TextAlignment.End,
                LineBreakMode = LineBreakMode.TailTruncation,
                VerticalOptions = LayoutOptions.Center,
            }, 0);

            var trackGrid = new Grid { HeightRequest = 8 };
            trackGrid.Add(new BoxView
            {
                BackgroundColor = Color.FromArgb("#f3f4f6"),
                CornerRadius = 4,
            });
            trackGrid.Add(new BoxView
            {
                BackgroundColor = barColor,
                CornerRadius = 4,
                HorizontalOptions = LayoutOptions.Start,
                WidthRequest = pct * 80,
            });
            row.Add(trackGrid, 1);

            row.Add(new Label
            {
                Text = count.ToString(),
                FontSize = 11,
                FontAttributes = FontAttributes.Bold,
                FontFamily = "Consolas",
                TextColor = Color.FromArgb("#111827"),
                HorizontalTextAlignment = TextAlignment.End,
                VerticalOptions = LayoutOptions.Center,
            }, 2);

            ReasonBreakdownPanel.Children.Add(row);
        }
    }

    private readonly Dictionary<string, int> _carrierExpectedCounts = new();
    private readonly Dictionary<string, Entry> _carrierActualEntries = new();

    private void BuildCarrierCountsPanel()
    {
        CarrierCountsPanel.Children.Clear();

        // Header row: Expected / Actual labels
        var hdr = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new(new GridLength(14)),
                new(GridLength.Star),
                new(new GridLength(56)),
                new(new GridLength(56)),
            },
            ColumnSpacing = 8,
            Padding = new Thickness(0, 0, 0, 4),
        };
        hdr.Add(new Label(), 0);
        hdr.Add(new Label(), 1);
        hdr.Add(new Label
        {
            Text = "EXPECTED",
            FontSize = 9,
            FontAttributes = FontAttributes.Bold,
            TextColor = Color.FromArgb("#9ca3af"),
            HorizontalTextAlignment = TextAlignment.Center,
        }, 2);
        hdr.Add(new Label
        {
            Text = "ACTUAL",
            FontSize = 9,
            FontAttributes = FontAttributes.Bold,
            TextColor = Color.FromArgb("#9ca3af"),
            HorizontalTextAlignment = TextAlignment.Center,
        }, 3);
        CarrierCountsPanel.Children.Add(hdr);

        // Collect carriers from sessions
        var carrierCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in _sessions)
        {
            var carrier = s.Data.FirstOrDefault()?.ShippingOptions;
            if (!string.IsNullOrWhiteSpace(carrier))
            {
                if (!carrierCounts.TryAdd(carrier, 1))
                    carrierCounts[carrier]++;
            }
        }

        // Merge with known expected counts
        foreach (var (carrier, expected) in _carrierExpectedCounts)
        {
            carrierCounts.TryAdd(carrier, 0);
        }

        Color[] dotColors = [Color.FromArgb("#ea580c"), Color.FromArgb("#e11d48"),
            Color.FromArgb("#ca8a04"), Color.FromArgb("#dc2626"), Color.FromArgb("#a21caf")];
        int colorIdx = 0;

        foreach (var (carrier, sessionCount) in carrierCounts.OrderByDescending(x => x.Value))
        {
            var expected = _carrierExpectedCounts.GetValueOrDefault(carrier, sessionCount);
            var dotColor = dotColors[colorIdx % dotColors.Length];
            colorIdx++;

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new(new GridLength(14)),
                    new(GridLength.Star),
                    new(new GridLength(56)),
                    new(new GridLength(56)),
                },
                ColumnSpacing = 8,
                Padding = new Thickness(0, 4),
            };

            row.Add(new BoxView
            {
                WidthRequest = 8, HeightRequest = 8,
                CornerRadius = 2, Color = dotColor,
                VerticalOptions = LayoutOptions.Center,
            }, 0);

            row.Add(new Label
            {
                Text = carrier,
                FontSize = 12,
                TextColor = Color.FromArgb("#374151"),
                VerticalOptions = LayoutOptions.Center,
                LineBreakMode = LineBreakMode.TailTruncation,
            }, 1);

            row.Add(new Label
            {
                Text = expected.ToString(),
                FontSize = 13,
                FontAttributes = FontAttributes.Bold,
                FontFamily = "Consolas",
                TextColor = Color.FromArgb("#6b7280"),
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalOptions = LayoutOptions.Center,
            }, 2);

            var entry = new Entry
            {
                Placeholder = "0",
                FontSize = 13,
                FontFamily = "Consolas",
                FontAttributes = FontAttributes.Bold,
                Keyboard = Keyboard.Numeric,
                HorizontalTextAlignment = TextAlignment.Center,
                HeightRequest = 28,
                BackgroundColor = Colors.White,
            };
            var capturedCarrier = carrier;
            entry.Unfocused += async (_, _) =>
            {
                if (int.TryParse(entry.Text, out var actualCount))
                    await ApiService.UpsertCarrierParcelCountAsync(capturedCarrier, actualCount, _currentOperatorId);
            };

            if (_carrierActualEntries.TryGetValue(carrier, out var existingEntry))
                entry.Text = existingEntry.Text;
            _carrierActualEntries[carrier] = entry;

            row.Add(entry, 3);
            CarrierCountsPanel.Children.Add(row);
        }
    }

    private void BuildPlatformBreakdown()
    {
        PlatformBreakdownPanel.Children.Clear();
        if (_returnsPlatformCounts.Count == 0) return;

        int maxCount = _returnsPlatformCounts.Values.Max();

        foreach (var (platform, count) in _returnsPlatformCounts.OrderByDescending(x => x.Value))
        {
            double pct = maxCount > 0 ? (double)count / maxCount : 0;
            var pLower = platform.ToLowerInvariant();
            var dotColor = pLower switch
            {
                var p when p.Contains("shopee") => Color.FromArgb("#ee4d2d"),
                var p when p.Contains("lazada") => Color.FromArgb("#0f146d"),
                var p when p.Contains("tiktok") => Color.FromArgb("#1a1a2e"),
                _ => Color.FromArgb("#6b7280"),
            };

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new(new GridLength(14)),
                    new(GridLength.Star),
                    new(new GridLength(50)),
                    new(new GridLength(24)),
                },
                ColumnSpacing = 8,
            };

            row.Add(new BoxView
            {
                WidthRequest = 8,
                HeightRequest = 8,
                CornerRadius = 2,
                Color = dotColor,
                VerticalOptions = LayoutOptions.Center,
            }, 0);

            row.Add(new Label
            {
                Text = platform,
                FontSize = 12,
                TextColor = Color.FromArgb("#374151"),
                VerticalOptions = LayoutOptions.Center,
            }, 1);

            var barGrid = new Grid { HeightRequest = 6 };
            barGrid.Add(new BoxView
            {
                BackgroundColor = Color.FromArgb("#f3f4f6"),
                CornerRadius = 3,
            });
            barGrid.Add(new BoxView
            {
                BackgroundColor = dotColor,
                CornerRadius = 3,
                HorizontalOptions = LayoutOptions.Start,
                WidthRequest = pct * 50,
            });
            row.Add(barGrid, 2);

            row.Add(new Label
            {
                Text = count.ToString(),
                FontSize = 12,
                FontAttributes = FontAttributes.Bold,
                FontFamily = "Consolas",
                TextColor = Color.FromArgb("#111827"),
                HorizontalTextAlignment = TextAlignment.End,
                VerticalOptions = LayoutOptions.Center,
            }, 3);

            PlatformBreakdownPanel.Children.Add(row);
        }
    }

    private void ShowLoginOverlay() =>
        MainThread.BeginInvokeOnMainThread(() => LoginOverlay.IsVisible = true);

    private void HideLoginOverlay() =>
        MainThread.BeginInvokeOnMainThread(() => LoginOverlay.IsVisible = false);

    private async Task ShowWelcomeAnimationAsync(string name)
    {
        WelcomeLabel.Text = $"Welcome, {name}";
        WelcomeBanner.IsVisible = true;
        WelcomeBanner.Opacity = 0;
        WelcomeBanner.Scale = 0.85;
        await Task.WhenAll(
            WelcomeBanner.FadeToAsync(1.0, 280, Easing.SinOut),
            WelcomeBanner.ScaleToAsync(1.0, 280, Easing.SinOut));
        await Task.Delay(1800);
        await WelcomeBanner.FadeToAsync(0.0, 350, Easing.SinIn);
        WelcomeBanner.IsVisible = false;
        WelcomeBanner.Scale = 1.0;
    }

    private void StartInactivityTimer()
    {
        StopInactivityTimer();
        var minutes = AppSettings.QcInactivityMinutes;
        if (minutes <= 0) return;
        _inactivityTimer = Dispatcher.CreateTimer();
        _inactivityTimer.Interval = TimeSpan.FromMinutes(minutes);
        _inactivityTimer.IsRepeating = false;
        _inactivityTimer.Tick += OnInactivityTimerTick;
        _inactivityTimer.Start();
    }

    private void StopInactivityTimer()
    {
        if (_inactivityTimer is null) return;
        _inactivityTimer.Stop();
        _inactivityTimer.Tick -= OnInactivityTimerTick;
        _inactivityTimer = null;
    }

    private void OnInactivityTimerTick(object? sender, EventArgs e)
    {
        var displayName = _currentOperatorFirstName ?? _currentOperator;
        _currentOperator = null;
        _currentOperatorFirstName = null;
        _currentOperatorId = null;
        StopInactivityTimer();
        Services.StationWsClient.SendOperatorLogout();
        UpdateNavOperatorUI(null);
        ShowLoginOverlay();
        if (displayName is not null)
            _ = Toast.Make($"Session ended — {displayName}").Show();
        Logger.Log($"OrderSearch: Operator logged out (inactivity)");
    }


}
