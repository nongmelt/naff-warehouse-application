using app.Models;
using Microsoft.Maui.Controls.Shapes;
using System.Runtime.Versioning;

namespace app.Views;

[SupportedOSPlatform("windows")]
public partial class OrderSearchPage
{


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

            if (sessionStatus == "preProcessed")
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
}
