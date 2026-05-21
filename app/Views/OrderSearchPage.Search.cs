using app.Helpers;
using app.Models;
using app.Services;
using System.Collections.ObjectModel;
using System.Runtime.Versioning;

namespace app.Views;

[SupportedOSPlatform("windows")]
public partial class OrderSearchPage
{
    // ── Search ───────────────────────────────────────────────────────────────

    private void SetSearchLoading(bool loading)
    {
        _isSearching = loading;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            PopupSearchEntry.IsEnabled = !loading;
            PopupSearchEntry.Placeholder = loading ? "searching…" : "Tracking or order number…";
            HeaderSearchEntry.IsEnabled = !loading;
            HeaderSearchEntry.Placeholder = loading ? "searching…" : "search";
            SearchPlaceholderLabel.Text = loading ? "searching…" : "search";
        });
    }

    private async void OnSearchCommitted(object sender, EventArgs e)
        => await ExecuteSearchAsync(HeaderSearchEntry.Text?.Trim() ?? "", trigger: "manual_search");

    private async Task ExecuteSearchAsync(string input, List<PackingList>? preloaded = null, string trigger = "tracking_scan")
    {
        if (string.IsNullOrWhiteSpace(input) || _isSearching) return;
        input = AppSettings.NormalizeTrackingNumber(input);
        HeaderSearchEntry.Text = string.Empty;

        if (string.IsNullOrWhiteSpace(AppSettings.ApiUrl))
        {
            UpdateSearchStatus("No backend API URL configured — open Settings to add one.");
            return;
        }

        SetSearchLoading(true);
        if (_currentOperator is not null)
            StartInactivityTimer();
        _historyNavIndex = -1;

        // Capture before session mutation so it can appear in the new event payload
        string? prevTracking = CurrentTrackingNumber;

        // Check if rescanning an existing session (dedup)
        bool isSameQuery = _sessions.Any(s =>
            string.Equals(s.Query, input, StringComparison.OrdinalIgnoreCase));

        // Immediately clear the visible results so the user knows the scan registered
        ActiveResults.Clear();
        UpdateSearchStatus("Searching…");

        // Save any partial picks before discarding the current result set
        if (_orderLoaded && Results.Count > 0)
            await SaveQcHoldForRemainingOrdersAsync(isSameQuery ? null : input);

        _orderLoaded = false;
        _isFirstItemScan = false;
        _completedPackingIds.Clear();
        _altSkuMap.Clear();
        if (_pendingSkuProduct != null) { _pendingSkuProduct.IsBeingPicked = false; SetActiveProduct(null); }
        Results.Clear();
        UpdateHeaderOrderInfo();
        NotFoundCard.IsVisible = false;

        Logger.Log($"OrderSearch: querying for '{input}'");
        var rows = preloaded ?? await ApiService.SearchAsync(input);

        foreach (var r in rows) { Results.Add(r); ActiveResults.Add(r); }
        UpdateHeaderOrderInfo();

        _ = ResolveOperatorNicknamesAsync(rows);

        // Returns mode: toggle empty → loaded state
        if (_currentMode == AppMode.Returns)
        {
            bool hasResults = rows.Count > 0;
            ReturnsEmptyState.IsVisible = !hasResults;
            ReturnsSuccessCard.IsVisible = false;
            ReturnsActionForm.IsVisible = hasResults;
        }

        await EnrichProductItemsAsync();

        // Mark orders that were "To be packed" or "QC Hold" on arrival — only these count in the session card
        // (QC Passed on arrival = pre-processed, shown as grey, not counted)
        foreach (var r in rows)
            if (string.Equals(r.PackingStatus, "To be packed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(r.PackingStatus, "QC Hold", StringComparison.OrdinalIgnoreCase))
                _qualifiedPackingIds.Add(r.PackingId);

        // Session management: dedup matching query anywhere, otherwise push new session
        int existingIdx = _sessions.FindIndex(s =>
            string.Equals(s.Query, input, StringComparison.OrdinalIgnoreCase));

        if (existingIdx >= 0)
        {
            _sessions[existingIdx] = new SearchSession(input, rows.ToList());
            _sessionIndex = existingIdx;
        }
        else
        {
            // Trim forward sessions (new search from mid-history) then push
            if (_sessionIndex < _sessions.Count - 1)
                _sessions.RemoveRange(_sessionIndex + 1, _sessions.Count - _sessionIndex - 1);
            _sessions.Add(new SearchSession(input, rows.ToList()));
            _sessionIndex = _sessions.Count - 1;

            // Cap history — evict by priority: completed first, then incoming, incomplete last
            var maxSessions = AppSettings.SearchHistoryMaxItems;
            if (_sessions.Count > maxSessions)
            {
                var excess = _sessions.Count - maxSessions;
                var evictIndices = Enumerable.Range(0, _sessions.Count)
                    .Where(i => i != _sessionIndex)
                    .Select(i =>
                    {
                        var status = ClassifySessionStatus(_sessions[i].Data);
                        int priority = status is "completed" or "preProcessed" ? 0
                            : status == "incoming" ? 1
                            : 2;
                        return (index: i, priority);
                    })
                    .OrderBy(x => x.priority)
                    .ThenBy(x => x.index)
                    .Take(excess)
                    .Select(x => x.index)
                    .OrderByDescending(x => x)
                    .ToList();

                foreach (var idx in evictIndices)
                {
                    _sessions.RemoveAt(idx);
                    if (_sessionIndex > idx)
                        _sessionIndex--;
                }
            }
        }

        _orderLoaded = rows.Count > 0;
        _isFirstItemScan = rows.Count > 0;
        if (rows.Count > 0)
        {
            var firstProduct = rows[0].ParsedProducts.FirstOrDefault();
            if (firstProduct != null) SetActiveProduct(firstProduct);
        }
        ResetSessionStatCounts();

        // New tracking scan resets the sequence counter — analytics treats the
        // counter as "nth mutation of this session", where a session is one
        // tracking scan's worth of picking.
        _sequenceInSession = 0;
        bool anyQcHold = rows.Any(r => string.Equals(r.PackingStatus, "QC Hold", StringComparison.OrdinalIgnoreCase));
        bool anyToBePacked = rows.Any(r => string.Equals(r.PackingStatus, "To be packed", StringComparison.OrdinalIgnoreCase));
        bool anyActionable = anyQcHold || anyToBePacked;
        string initialFromState = anyQcHold ? "held" : "idle";
        if (anyActionable || rows.Count == 0)
        {
            var trackingPayload = new Dictionary<string, object?>
            {
                ["trackingNumber"] = input,
                ["ordersFound"] = rows.Count,
            };
            if (prevTracking != null && !string.Equals(prevTracking, input, StringComparison.OrdinalIgnoreCase))
                trackingPayload["previousTrackingNumber"] = prevTracking;
            EmitQcEvent(
                stepId: "tracking_scanned",
                trigger: trigger,
                trackingNumber: input,
                fromState: initialFromState,
                toState: rows.Count > 0 ? "order-loaded" : "idle",
                payload: trackingPayload);
        }

        BuildCarouselUI();
        _carouselDirty = false;
        if (!isSameQuery) _ = AnimateAllCarouselCardsAsync();
        UpdateSessionStats();
        NotFoundCard.IsVisible = rows.Count == 0;
        NotFoundLabel.Text = rows.Count == 0 ? $"{input} not found" : "";
        var msg = rows.Count > 0 ? $"{rows.Count} result(s) for '{input}'" : $"No results for '{input}'";
        UpdateSearchStatus(msg);
        SearchHistoryService.Instance.Push(input);
        RefreshHistoryItems();
        UpdateHistoryHeader();
        SetSearchLoading(false);

        // Auto-open the image overlay when the whole result set is a single
        // non-bundle product that still needs picking (spec: one single product).
        var soleProduct = SingleProductOverlayPolicy.PickSoleProduct(
            Results, _pendingScanQueue.Count > 0);
        if (soleProduct != null)
            await AutoOpenSingleProductOverlayAsync(soleProduct);

        // Drain queued scans
        if (_pendingScanQueue.Count > 0)
        {
            var next = _pendingScanQueue.Dequeue();
            Logger.Log($"OrderSearch: processing queued scan '{next}'");
            var queuedRows = await ApiService.SearchAsync(next);
            if (queuedRows.Count > 0)
                await ExecuteSearchAsync(next, queuedRows);
            else if (_orderLoaded)
                HandleSkuScan(next);
            else
                await ExecuteSearchAsync(next, queuedRows);
        }
    }

    // ── Product enrichment ──────────────────────────────────────────────────

    private async Task EnrichProductItemsAsync()
    {
        var allProducts = Results.SelectMany(o => o.ParsedProducts).ToList();
        if (allProducts.Count == 0) return;

        var skus = allProducts.Select(p => p.SellerSku).Distinct().ToList();
        var enrichments = await ApiService.EnrichProductsAsync(skus);
        if (enrichments.Count == 0) return;

        // Build enrichment lookup by ALL known SKUs (primary + aliases)
        var enrichByAnySku = new Dictionary<string, ProductEnrichment>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in enrichments.Values)
            foreach (var s in e.AllSkus)
                enrichByAnySku.TryAdd(s, e);

        foreach (var item in allProducts)
        {
            if (!enrichByAnySku.TryGetValue(item.SellerSku, out var e)) continue;
            item.ProductId = e.Id;
            item.ProductType = e.ProductType;
            item.CategoryName = e.CategoryName;
            item.CategoryId = e.CategoryId;
            item.ImagePath = e.ImagePath;
            item.ProductVersion = e.Version;
            item.QcNotes = e.QcNotes;
            item.Brand = e.Brand;
            item.AllSkus = e.AllSkus;
            var (sc1, sc2) = ColorSwatchHelper.ParseSwatchColors(item.Variation);
            item.SwatchColor = sc1;
            item.SwatchColor2 = sc2;

            if (e.Components.Count > 0)
            {
                item.BundleComponents = new ObservableCollection<BundleComponentItem>(
                    e.Components.Select((c, idx) => new BundleComponentItem
                    {
                        ComponentProductId = c.ComponentProductId,
                        Name = c.ProductName,
                        Variation = c.ProductVariation,
                        SellerSku = c.SellerSku,
                        ProductVersion = c.Version,
                        RequiredQuantity = c.Quantity * item.RequiredQuantity,
                        VerifiedQuantity = 0,
                        AllSkus = c.AllSkus,
                        QcNotes = c.QcNotes,
                        SubRowNumber = $"{item.RowNumber}.{idx + 1}",
                        SwatchColor = ColorSwatchHelper.ParseSwatchColors(c.ProductVariation).Item1,
                        SwatchColor2 = ColorSwatchHelper.ParseSwatchColors(c.ProductVariation).Item2,
                    }));

                if (item.BundleComponentStates is { Count: > 0 })
                {
                    foreach (var comp in item.BundleComponents)
                    {
                        var saved = item.BundleComponentStates.FirstOrDefault(s =>
                            string.Equals(s.SellerSku, comp.SellerSku, StringComparison.OrdinalIgnoreCase));
                        if (saved != null)
                            comp.VerifiedQuantity = saved.VerifiedQuantity;
                    }
                    item.NotifyBundleProgressChanged();

                    if (item.IsBundleFullyVerified && item.Quantity > 0)
                        item.Quantity = 0;
                }
            }
        }

        foreach (var order in Results)
            CategoryBadgeHelper.AssignBadges(order.ParsedProducts);

        // Build unified SKU → ProductItem list map (alias SKUs + component SKUs)
        _altSkuMap.Clear();
        foreach (var item in allProducts)
        {
            if (enrichByAnySku.TryGetValue(item.SellerSku, out var enrich))
                foreach (var alias in enrich.AllSkus)
                {
                    if (!_altSkuMap.TryGetValue(alias, out var list))
                        _altSkuMap[alias] = list = [];
                    list.Add(item);
                }

            if (item.BundleComponents == null) continue;
            foreach (var comp in item.BundleComponents)
            {
                if (!_altSkuMap.TryGetValue(comp.SellerSku, out var list))
                    _altSkuMap[comp.SellerSku] = list = [];
                list.Add(item);

                foreach (var altSku in comp.AllSkus)
                {
                    if (!_altSkuMap.TryGetValue(altSku, out var altList))
                        _altSkuMap[altSku] = altList = [];
                    if (!altList.Contains(item))
                        altList.Add(item);
                }
            }
        }

        var apiBase = AppSettings.ApiUrl ?? "http://localhost:8080";

        // Download images for main products
        foreach (var item in allProducts)
        {
            if (!item.HasImagePath) continue;
            var captured = item;
            _ = Task.Run(async () =>
            {
                try
                {
                    var path = await ProductImageCache.EnsureAsync(
                        captured.SellerSku, apiBase, captured.ProductId, captured.ProductVersion);
                    if (path != null)
                        MainThread.BeginInvokeOnMainThread(() => captured.LocalImagePath = path);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Image download failed ({captured.SellerSku}): {ex.Message}");
                }
            });
        }

        // Download images for bundle components
        foreach (var item in allProducts)
        {
            if (item.BundleComponents == null) continue;
            foreach (var comp in item.BundleComponents)
            {
                var captured = comp;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var path = await ProductImageCache.EnsureAsync(
                            captured.SellerSku, apiBase, captured.ComponentProductId, captured.ProductVersion);
                        if (path != null)
                        {
                            var bytes = await File.ReadAllBytesAsync(path);
                            MainThread.BeginInvokeOnMainThread(() =>
                                captured.ImageSource = ImageSource.FromStream(() => new MemoryStream(bytes)));
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Component image download failed ({captured.SellerSku}): {ex.Message}");
                    }
                });
            }
        }
    }

    /// <summary>Legacy per-order enrichment used during session navigation.</summary>
    private static async Task EnrichProductItemsAsync(IEnumerable<ProductItem> items)
    {
        var tasks = items.Select(async item =>
        {
            var info = await ApiService.GetProductBySkuAsync(item.SellerSku);
            if (info == null) return;

            item.ProductId = info.Id;
            item.ProductType = info.ProductType;

            if (info.ImagePath != null)
            {
                var bytes = await ApiService.GetProductImageAsync(info.Id);
                if (bytes != null)
                    item.ImageSource = ImageSource.FromStream(() => new MemoryStream(bytes));
            }
        });
        await Task.WhenAll(tasks);
    }

    private async void OnBundleToggleTapped(object sender, TappedEventArgs e)
    {
        ProductItem? item = null;
        if (sender is VisualElement ve)
            item = ve.BindingContext as ProductItem;
        if (item == null || !item.IsBundle) return;

        if (item.IsExpanded)
        {
            item.IsExpanded = false;
            return;
        }

        if (item.BundleComponents == null)
        {
            item.IsLoadingComponents = true;
            var components = await ApiService.GetBundleComponentsAsync(item.ProductId);
            var componentItems = components.Select((c, idx) => new BundleComponentItem
            {
                ComponentProductId = c.ComponentProductId,
                Name = c.ProductName,
                Variation = c.ProductVariation,
                SellerSku = c.SellerSku,
                RequiredQuantity = c.Quantity * item.RequiredQuantity,
                VerifiedQuantity = 0,
                QcNotes = c.QcNotes,
                SubRowNumber = $"{item.RowNumber}.{idx + 1}",
                SwatchColor = ColorSwatchHelper.ParseSwatchColors(c.ProductVariation).Item1,
                SwatchColor2 = ColorSwatchHelper.ParseSwatchColors(c.ProductVariation).Item2,
            }).ToList();

            item.BundleComponents = new ObservableCollection<BundleComponentItem>(componentItems);
            item.IsLoadingComponents = false;

            _ = Task.WhenAll(componentItems.Select(async comp =>
            {
                var bytes = await ApiService.GetProductImageAsync(comp.ComponentProductId);
                if (bytes != null)
                    comp.ImageSource = ImageSource.FromStream(() => new MemoryStream(bytes));
            }));
        }

        item.IsExpanded = true;
    }

    // ── Search history ────────────────────────────────────────────────────────

    private void OnHistoryClearAll(object sender, TappedEventArgs e)
    {
        SearchHistoryService.Instance.Clear();
    }

    private void RefreshHistoryItems() { }

    private void UpdateHistoryHeader() { }

    // ── Scan indicator ────────────────────────────────────────────────────────

    private IDispatcherTimer? _scanAlertRevertTimer;

    private void UpdateScanIndicator(string barcode, bool found)
    {
        _lastScanBarcode = barcode;
        _lastScanFound = found;
        _lastScanTime = DateTime.Now;
        StartLastScanTimer();
        UpdateScanIndicatorUI();
    }

    private void UpdateScanIndicatorUI()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_lastScanBarcode == null) return;

            // Configure alert pill colors and message
            if (_lastScanFound)
            {
                HeaderScanAlert.BackgroundColor = Color.FromArgb("#059669");
                ScanAlertSkuLabel.Text = _lastScanBarcode;
                ScanAlertMessage.Text = "✓ SKU Verified";
            }
            else
            {
                HeaderScanAlert.BackgroundColor = Color.FromArgb("#e11d48");
                ScanAlertSkuLabel.Text = _lastScanBarcode;
                ScanAlertMessage.Text = "⚠ Not in this order";
            }

            // Swap: hide order info, show scan alert with drop-in animation
            _ = ShowScanAlertAsync();

            // Auto-revert after 1.5s for success, 3s for error
            _scanAlertRevertTimer?.Stop();
            _scanAlertRevertTimer = Dispatcher.CreateTimer();
            _scanAlertRevertTimer.Interval = TimeSpan.FromMilliseconds(_lastScanFound ? 1500 : 3000);
            _scanAlertRevertTimer.IsRepeating = false;
            _scanAlertRevertTimer.Tick += (_, _) => _ = HideScanAlertAsync();
            _scanAlertRevertTimer.Start();
        });
    }

    private async Task ShowScanAlertAsync()
    {
        HeaderScanAlert.IsVisible = true;
        HeaderScanAlert.TranslationY = -10;
        HeaderScanAlert.Opacity = 0;

        await Task.WhenAll(
            HeaderOrderInfo.FadeToAsync(0, 150, Easing.SinIn),
            HeaderScanAlert.TranslateToAsync(0, 0, 250, Easing.SinOut),
            HeaderScanAlert.FadeToAsync(1.0, 250, Easing.SinOut));

        HeaderOrderInfo.IsVisible = false;
    }

    private async Task HideScanAlertAsync()
    {
        await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            HeaderOrderInfo.IsVisible = true;
            HeaderOrderInfo.Opacity = 0;

            await Task.WhenAll(
                HeaderScanAlert.FadeToAsync(0, 200, Easing.SinIn),
                HeaderOrderInfo.FadeToAsync(1.0, 250, Easing.SinOut));

            HeaderScanAlert.IsVisible = false;
        });
    }

    private void StartLastScanTimer()
    {
        _lastScanTimer?.Stop();
        _lastScanTimer = Dispatcher.CreateTimer();
        _lastScanTimer.Interval = TimeSpan.FromSeconds(1);
        _lastScanTimer.IsRepeating = true;
        _lastScanTimer.Tick += OnLastScanTimerTick;
        _lastScanTimer.Start();
    }

    private void OnLastScanTimerTick(object? sender, EventArgs e)
    {
        if (_lastScanTime is null) return;
        var elapsed = DateTime.Now - _lastScanTime.Value;
        var text = elapsed.TotalSeconds < 60
            ? $"Last scan {(int)elapsed.TotalSeconds}s ago"
            : $"Last scan {(int)elapsed.TotalMinutes}m ago";
        MainThread.BeginInvokeOnMainThread(() => { if (LastScanLabel != null) LastScanLabel.Text = text; });
    }

    private void OnProductRowTapped(object sender, TappedEventArgs e)
    {
        if (sender is not VisualElement el) return;
        if (el.BindingContext is not ProductItem item) return;
        ShowProductImageOverlay(item, "card_click");
    }

    // ── Search popup ──────────────────────────────────────────────────────────

    private void OnSearchBoxTapped(object? sender, TappedEventArgs e)
    {
        ActivateSearchEntry();
    }

    private void ActivateSearchEntry()
    {
        PopupSearchBar.IsVisible = true;
        PopupSearchBackdrop.IsVisible = true;
        PopupSearchEntry.Text = "";
        PopupSearchEntry.Focus();
    }

    private async void OnPopupSearchCommitted(object sender, EventArgs e)
    {
        var query = PopupSearchEntry.Text?.Trim() ?? "";
        DismissPopupSearch();
        if (!string.IsNullOrWhiteSpace(query))
            await ExecuteSearchAsync(query, trigger: "manual_search");
    }

    private void OnPopupSearchBackdropTapped(object? sender, TappedEventArgs e)
    {
        DismissPopupSearch();
    }

    private void DismissPopupSearch()
    {
        PopupSearchBar.IsVisible = false;
        PopupSearchBackdrop.IsVisible = false;
        PopupSearchEntry.Unfocus();
    }

    private void OnSearchEntryFocused(object? sender, FocusEventArgs e) { }

    private void OnSearchEntryUnfocused(object? sender, FocusEventArgs e) { }

    // ── Header order info ─────────────────────────────────────────────────────

    private void UpdateHeaderOrderInfo()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var order = Results.FirstOrDefault();
            CurrentOrder = order;
            if (order is null)
            {
                HeaderOrderLabel.IsVisible = false;
                HeaderOrderNumber.IsVisible = false;
                HeaderPlatformBadge.IsVisible = false;
                HeaderTrackingLabel.IsVisible = false;
                return;
            }

            HeaderOrderLabel.IsVisible = true;
            HeaderOrderLabel.Text = "ORDER";
            HeaderOrderNumber.IsVisible = true;
            HeaderOrderNumber.Text = order.OrderNumber;

            if (!string.IsNullOrWhiteSpace(order.Platform))
            {
                HeaderPlatformBadge.IsVisible = true;
                HeaderPlatformLabel.Text = order.Platform.ToUpperInvariant();
                var platformLower = order.Platform.ToLowerInvariant();
                HeaderPlatformBadge.BackgroundColor = platformLower switch
                {
                    var p when p.Contains("shopee") => Color.FromArgb("#ee4d2d"),
                    var p when p.Contains("lazada") => Color.FromArgb("#0f146d"),
                    var p when p.Contains("tiktok") => Color.FromArgb("#111827"),
                    _ => Color.FromArgb("#6b7280"),
                };
            }
            else
            {
                HeaderPlatformBadge.IsVisible = false;
            }

            HeaderTrackingLabel.IsVisible = true;
            HeaderTrackingLabel.Text = order.TrackingNumber;

            var isQcPassed = string.Equals(order.PackingStatus, "QC Passed", StringComparison.OrdinalIgnoreCase);
            var hasVerified = Results.Any(o => o.HasProducts && o.ParsedProducts.Any(p => p.Quantity != p.OriginalQuantity));
            var isHold = string.Equals(order.PackingStatus, "QC Hold", StringComparison.OrdinalIgnoreCase);
            ResetButton.IsVisible = !isQcPassed && (hasVerified || isHold);

            // Carrier pill — visible in Returns mode when shipping_options exists
            var carrier = order.ShippingOptions;
            var showCarrier = _currentMode == AppMode.Returns && !string.IsNullOrWhiteSpace(carrier);
            TrackingCarrierPill.IsVisible = showCarrier;
            if (showCarrier)
            {
                TrackingCarrierLabel.Text = carrier;
                TrackingCarrierDotLabel.Text = carrier![..1].ToUpperInvariant();
            }
        });
    }

    private void OnResetButtonTapped(object sender, TappedEventArgs e)
    {
        if (CurrentOrder == null) return;
        var fakeEl = new Label { BindingContext = CurrentOrder };
        OnResetClicked(fakeEl, EventArgs.Empty);
    }

    // ── Operator nickname resolution ────────────────────────────────────────

    private async Task ResolveOperatorNicknamesAsync(IReadOnlyList<PackingList> rows)
    {
        var codes = rows
            .SelectMany(r => new[] { r.PackedBy, r.CheckedBy })
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var code in codes)
        {
            var nickname = await ApiService.ResolveOperatorNicknameAsync(code);
            if (nickname is null || nickname == code) continue;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                foreach (var r in rows)
                {
                    if (string.Equals(r.PackedBy, code, StringComparison.OrdinalIgnoreCase))
                        r.PackedBy = nickname;
                    if (string.Equals(r.CheckedBy, code, StringComparison.OrdinalIgnoreCase))
                        r.CheckedBy = nickname;
                }
            });
        }
    }

    // ── Search status ─────────────────────────────────────────────────────────

    private IDispatcherTimer? _statusRevertTimer;
    private void UpdateSearchStatus(string msg)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            StatusLabel.Text = msg;
            StatusLabel.Opacity = 1;

            _statusRevertTimer?.Stop();
            _statusRevertTimer = Dispatcher.CreateTimer();
            _statusRevertTimer.Interval = TimeSpan.FromMilliseconds(4000);
            _statusRevertTimer.IsRepeating = false;
            _statusRevertTimer.Tick += (_, _) =>
            {
                _ = StatusLabel.FadeToAsync(0, 500, Easing.SinIn);
            };
            _statusRevertTimer.Start();
        });
    }
}
