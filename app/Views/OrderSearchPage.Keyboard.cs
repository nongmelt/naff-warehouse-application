using app.Models;
using app.Services;
using System.Runtime.Versioning;

namespace app.Views;

[SupportedOSPlatform("windows")]
public partial class OrderSearchPage
{
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

    private static bool IsKeyDown(Windows.System.VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void OnWindowKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        // Admin bypass chord: Ctrl+Shift+A+M (all held) → log in as "admin" without a badge scan.
        // Checked before the TextBox guard so it works even when a field has focus.
        if ((e.Key == Windows.System.VirtualKey.M || e.Key == Windows.System.VirtualKey.A)
            && IsKeyDown(Windows.System.VirtualKey.Control)
            && IsKeyDown(Windows.System.VirtualKey.Shift)
            && IsKeyDown(Windows.System.VirtualKey.A)
            && IsKeyDown(Windows.System.VirtualKey.M))
        {
            LoginAsAdmin();
            e.Handled = true;
            return;
        }

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

        if (e.Key == Windows.System.VirtualKey.Escape && CancelledOrderOverlay.IsVisible)
        {
            _ = DismissCancelledOverlayAsync();
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
}
