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

    // ── Product image overlay ──────────────────────────────────────────────

    private void OnProductCardTapped(object sender, TappedEventArgs e)
    {
        ProductItem? item = null;
        if (sender is VisualElement ve)
            item = ve.BindingContext as ProductItem;
        if (item == null) return;

        ShowProductImageOverlay(item, "card_click");
    }

    private void ShowProductImageOverlay(ProductItem item, string? openTrigger = null, bool readOnly = false)
    {
        _completionDismissCts?.Cancel();
        _overlayReadOnly = readOnly;
        if (!readOnly) _overlayReadOnlyList = null;
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

        // Item position (e.g., "ITEM 03 of 14"). Read-only peeks position
        // within the card parcel's list — its items are not in Results.
        if (_overlayReadOnly && _overlayReadOnlyList is { Count: > 0 } roList)
        {
            OverlayItemPosition.Text = $"ITEM {roList.IndexOf(item) + 1:D2} of {roList.Count}";
        }
        else
        {
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

        OverlayMinusBtn.IsVisible = !readOnly;
        OverlayPlusBtn.IsVisible = !readOnly;

        OverlayReadOnlyPill.IsVisible = readOnly;

        // QC-verified item peeked from the card: carry the green accent onto
        // the viewer itself. Non-verified/pick-mode opens reset to no stroke
        // (the completion flash manages its own stroke later).
        if (readOnly && item.IsFullyPicked)
        {
            OverlayCard.Stroke = Color.FromArgb("#22c55e");
            OverlayCard.StrokeThickness = 3;
        }
        else
        {
            OverlayCard.Stroke = Colors.Transparent;
            OverlayCard.StrokeThickness = 0;
        }

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
        if (_overlayReadOnly) return;
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
        if (_overlayReadOnly) return;
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
        if (_overlayReadOnly) return;
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

        // #118: read-only peeks used to block nav outright because advancing
        // re-opened the overlay in pick mode. Navigate the card parcel's own
        // list instead, staying read-only.
        if (_overlayReadOnly)
        {
            if (_overlayReadOnlyList is not { Count: > 0 } list) return;
            var idx = list.IndexOf(_overlayItem);
            if (idx < 0) return;
            var next = idx + direction;
            if (next < 0) next = list.Count - 1;
            if (next >= list.Count) next = 0;
            ShowProductImageOverlay(list[next], readOnly: true);
            return;
        }

        var allProducts = Results.SelectMany(o => o.ParsedProducts).ToList();
        var currentIdx = allProducts.IndexOf(_overlayItem);
        if (currentIdx < 0) return;

        int nextIdx = currentIdx + direction;
        if (nextIdx < 0) nextIdx = allProducts.Count - 1;
        if (nextIdx >= allProducts.Count) nextIdx = 0;

        ShowProductImageOverlay(allProducts[nextIdx]);
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
}
