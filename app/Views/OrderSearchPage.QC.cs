using app.Helpers;
using app.Models;
using app.Services;
using app.Workflows;
using Microsoft.Maui.Controls.Shapes;
using System.Runtime.Versioning;

namespace app.Views;

[SupportedOSPlatform("windows")]
public partial class OrderSearchPage
{

    private enum DeductionSource
    {
        AutoPrior,     // pending item auto-deducted because user moved on to the next scan/tap
        ScanAuto,      // scan matched a qty=1 row — immediate deduct; event already emitted in HandleSkuScan
        ManualQty,     // user typed a number in the qty entry
        CardTap,       // user tapped the qty area on a card
        KeyboardPlus,  // user pressed + key on keyboard
    }

    // ── Product image overlay ──────────────────────────────────────────────

    private ProductItem? _overlayItem;
    private int _activeComponentIndex = -1; // -1 = parent, 0..N = component index

    private CancellationTokenSource? _completionDismissCts;

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
                ? $"SKU '{barcode}' belongs to a finalized order (QC Passed or Duplicate) — no changes allowed"
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

    // "Finalized / read-only" — the order can no longer be edited by a scan or a
    // qty change. Named QcPassed for history, but it now also covers a reissue
    // Duplicate (spec §13.6): the backend 409s every status writer on a Duplicate
    // parcel, and the client mirrors that lock across all edit paths that gate on
    // this helper (pick, qty-change overlay, keyboard qty).
    private bool IsOrderQcPassed(PackingList order) =>
        _completedPackingIds.Contains(order.PackingId)
        || string.Equals(order.PackingStatus, "QC Passed", StringComparison.OrdinalIgnoreCase)
        || order.IsDuplicate;

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
            owner.CheckedBy = _currentOperatorFirstName ?? EffectiveOperator;
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
            owner.CheckedBy = _currentOperatorFirstName ?? EffectiveOperator;
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
                order.CheckedBy = _currentOperatorFirstName ?? EffectiveOperator;
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

    // ── Quantity tap (manual QC deduction) ───────────────────────────────────

    private void OnQuantityTapped(object sender, TappedEventArgs e)
    {
        ProductItem? item = null;
        if (sender is VisualElement ve)
            item = ve.BindingContext as ProductItem;
        if (item == null) return;
        ShowProductImageOverlay(item, "qty_tap");
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


    private void OnBundleChevronTapped(object sender, TappedEventArgs e)
    {
        ProductItem? item = null;
        if (sender is VisualElement ve)
            item = ve.BindingContext as ProductItem;
        if (item == null || !item.IsBundle) return;
        item.IsBundleExpanded = !item.IsBundleExpanded;
    }
}
