using app.Models;
using app.Services;
using app.Workflows;
using Microsoft.Maui.Controls.Shapes;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Ports;
using System.Management;
using System.Net;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace app.Views;

[SupportedOSPlatform("windows")]
public partial class OrderSearchPage : ContentPage
{
    private record ComPortEntry(string PortName, string DisplayName);

    private SerialPort? _serialPort;
    private List<ComPortEntry> _comPorts = [];
    private bool _isSearching;
    private bool _historyExpanded;
    private int _historyNavIndex = -1;

    // Search-session navigation (back / forward through previous scans)
    private record SearchSession(string Query, List<PackingList> Data);
    private readonly List<SearchSession> _sessions = [];
    private int _sessionIndex = -1;

    // Station identifier — computer name, resolved once
    private static readonly string StationName = Environment.MachineName;
    public string StationNameDisplay => $"Station: {StationName}";

    // SKU picking state — set after an order loads; cleared on new search
    private bool _orderLoaded;
    private bool _isFirstItemScan;
    private ProductItem? _pendingSkuProduct;
    private readonly HashSet<int> _completedPackingIds = [];

    // Orders that were "To be packed" when first scanned this session — the only ones counted in SessionCard
    private readonly HashSet<int> _qualifiedPackingIds = [];

    // Increments on every QC mutation within a scan session — shipped on workflow_events
    // so analytics can answer "which SKUs get scanned first on average?"
    private int _sequenceInSession;

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
            @operator: StationName.Replace(' ', '-'),
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
        RefreshHistoryItems();
        UpdateHistoryHeader();
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

    // ── COM Port Management ──────────────────────────────────────────────────

    public async Task LoadComPortsAsync()
    {
        _comPorts = await GetFriendlyComPortsAsync();

        var selectedPort = _serialPort?.IsOpen == true ? _serialPort.PortName : null;

        ComPortPicker.Items.Clear();
        ComPortPicker.Items.Add("(None)");
        foreach (var p in _comPorts)
            ComPortPicker.Items.Add(p.DisplayName);

        // Restore selection if the previously open port is still present
        if (selectedPort != null)
        {
            var idx = _comPorts.FindIndex(p => p.PortName == selectedPort);
            ComPortPicker.SelectedIndex = idx >= 0 ? idx + 1 : 0;
        }
        else
        {
            ComPortPicker.SelectedIndex = 0;
        }
    }

    private async void OnRefreshPorts(object sender, EventArgs e)
        => await LoadComPortsAsync();

    private void OnComPortSelected(object sender, EventArgs e)
    {
        var idx = ComPortPicker.SelectedIndex;
        if (idx < 0) return;

        if (idx == 0)
        {
            CloseSerialPort();
            UpdateScannerStatus("No scanner connected");
            return;
        }

        var portIdx = idx - 1;
        if (portIdx >= _comPorts.Count) return;

        var portName = _comPorts[portIdx].PortName;
        CloseSerialPort();
        try
        {
            _serialPort = new SerialPort(portName, 9600, Parity.None, 8, StopBits.One)
            {
                NewLine = "\r",
                ReadTimeout = 500,
                DtrEnable = true
            };
            _serialPort.DataReceived += OnSerialDataReceived;
            _serialPort.Open();
            UpdateScannerStatus($"Scanner ready ({portName}) — waiting for scan");
            Logger.Log($"OrderSearch: Serial port {portName} opened");
        }
        catch (Exception ex)
        {
            Logger.Log($"OrderSearch serial port: {ex}");
            UpdateScannerStatus($"COM error: {ex.Message}");
        }
    }

    private void OnSerialDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        try
        {
            var line = _serialPort?.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(line))
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    // Single API call: if results come back it's a tracking number; otherwise it's a SKU.
                    var rows = await ApiService.SearchAsync(line);
                    if (rows.Count > 0)
                        await ExecuteSearchAsync(line, rows);
                    else if (_orderLoaded)
                        HandleSkuScan(line);
                    else
                        await ExecuteSearchAsync(line, rows);
                });
        }
        catch (TimeoutException) { }
        catch (Exception ex)
        {
            Logger.Log($"OrderSearch serial read: {ex.Message}");
        }
    }

    private void CloseSerialPort()
    {
        if (_serialPort == null) return;
        try
        {
            _serialPort.DataReceived -= OnSerialDataReceived;
            if (_serialPort.IsOpen) _serialPort.Close();
            _serialPort.Dispose();
        }
        catch { }
        finally
        {
            _serialPort = null;
        }
    }

    // ── Search ───────────────────────────────────────────────────────────────

    private async void OnSearchClicked(object sender, EventArgs e)
        => await ExecuteSearchAsync(SearchEntry.Text?.Trim() ?? "", trigger: "manual_search");

    private async void OnSearchCommitted(object sender, EventArgs e)
        => await ExecuteSearchAsync(SearchEntry.Text?.Trim() ?? "", trigger: "manual_search");

    private async Task ExecuteSearchAsync(string input, List<PackingList>? preloaded = null, string trigger = "tracking_scan")
    {
        if (string.IsNullOrWhiteSpace(input) || _isSearching) return;
        SearchEntry.Text = string.Empty;

        if (string.IsNullOrWhiteSpace(AppSettings.ApiUrl))
        {
            UpdateSearchStatus("No backend API URL configured — open Settings to add one.");
            return;
        }

        _isSearching = true;
        _historyNavIndex = -1;

        // Capture before session mutation so it can appear in the new event payload
        string? prevTracking = CurrentTrackingNumber;

        // Check if scanning the same tracking number (dedup)
        bool isSameQuery = _sessionIndex >= 0 &&
            string.Equals(_sessions[_sessionIndex].Query, input, StringComparison.OrdinalIgnoreCase);

        // Immediately clear the visible results so the user knows the scan registered
        ActiveResults.Clear();
        UpdateSearchStatus("Searching…");

        // Save any partial picks before discarding the current result set
        if (_orderLoaded && Results.Count > 0)
            await SaveQcHoldForRemainingOrdersAsync(isSameQuery ? null : input);

        _orderLoaded = false;
        _isFirstItemScan = false;
        _completedPackingIds.Clear();
        if (_pendingSkuProduct != null) { _pendingSkuProduct.IsBeingPicked = false; _pendingSkuProduct = null; }
        Results.Clear();
        NotFoundCard.IsVisible = false;

        Logger.Log($"OrderSearch: querying for '{input}'");
        var rows = preloaded ?? await ApiService.SearchAsync(input);

        foreach (var r in rows) { Results.Add(r); ActiveResults.Add(r); }

        // Mark orders that were "To be packed" or "QC Hold" on arrival — only these count in the session card
        // (QC Passed on arrival = pre-processed, shown as grey, not counted)
        foreach (var r in rows)
            if (string.Equals(r.PackingStatus, "To be packed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(r.PackingStatus, "QC Hold", StringComparison.OrdinalIgnoreCase))
                _qualifiedPackingIds.Add(r.PackingId);

        // Session management: dedup same query, otherwise push new session
        if (isSameQuery)
        {
            // Refresh current session data in place
            _sessions[_sessionIndex] = new SearchSession(input, rows.ToList());
        }
        else
        {
            // Trim forward sessions (new search from mid-history) then push
            if (_sessionIndex < _sessions.Count - 1)
                _sessions.RemoveRange(_sessionIndex + 1, _sessions.Count - _sessionIndex - 1);
            _sessions.Add(new SearchSession(input, rows.ToList()));
            _sessionIndex = _sessions.Count - 1;

            // Cap history at 50 sessions (oldest removed from the front)
            const int MaxSessions = 50;
            if (_sessions.Count > MaxSessions)
            {
                var excess = _sessions.Count - MaxSessions;
                _sessions.RemoveRange(0, excess);
                _sessionIndex = Math.Max(0, _sessionIndex - excess);
            }
        }

        _orderLoaded = rows.Count > 0;
        _isFirstItemScan = rows.Count > 0;
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
        if (!isSameQuery) _ = AnimateAllCarouselCardsAsync();
        UpdateSessionStats();
        NotFoundCard.IsVisible = rows.Count == 0;
        NotFoundLabel.Text = rows.Count == 0 ? $"{input} not found" : "";
        var msg = rows.Count > 0 ? $"{rows.Count} result(s) for '{input}'" : $"No results for '{input}'";
        UpdateSearchStatus(msg);
        SearchHistoryService.Instance.Push(input);
        RefreshHistoryItems();
        UpdateHistoryHeader();
        _isSearching = false;
    }

    // ── SKU picking ───────────────────────────────────────────────────────────

    private void HandleSkuScan(string barcode)
    {
        // Auto-deduct 1 from the previous pending item before handling the new scan
        if (_pendingSkuProduct != null)
        {
            EmitQcEvent(
                stepId: "item_scanned_auto",
                trigger: "sku_scan",
                trackingNumber: CurrentTrackingNumber,
                fromState: ConsumePickingFromState(),
                toState: "picking",
                payload: new Dictionary<string, object?>
                {
                    ["sku"] = barcode,
                    ["qtyBefore"] = _pendingSkuProduct.Quantity,
                    ["qtyAfter"] = _pendingSkuProduct.Quantity - 1,
                });
            ApplySkuDeduction(_pendingSkuProduct, "1", DeductionSource.AutoPrior);
        }

        ProductItem? found = null;
        PackingList? foundOrder = null;
        bool blockedByQcPassed = false;

        foreach (var order in Results)
        {
            // Lock: QC Passed orders (from this session or already in DB) cannot be modified
            bool isQcPassed = _completedPackingIds.Contains(order.PackingId)
                || string.Equals(order.PackingStatus, "QC Passed", StringComparison.OrdinalIgnoreCase);

            var match = order.ParsedProducts.FirstOrDefault(p =>
                string.Equals(p.SellerSku, barcode, StringComparison.OrdinalIgnoreCase));

            if (match == null) continue;

            if (isQcPassed)
            {
                blockedByQcPassed = true;
                continue; // SKU exists but order is locked
            }

            found = match;
            foundOrder = order;
            break;
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
            UpdateSearchStatus(blockedByQcPassed
                ? $"SKU '{barcode}' belongs to a QC Passed order — no changes allowed"
                : $"SKU '{barcode}' not found in this order");
            return;
        }

        _pendingSkuProduct = found;

        if (found.Quantity == 1)
        {
            // Only one left — deduct immediately, no input needed
            EmitQcEvent(
                stepId: "item_scanned_auto",
                trigger: "sku_scan",
                trackingNumber: foundOrder?.TrackingNumber,
                fromState: ConsumePickingFromState(),
                toState: "picking",
                payload: new Dictionary<string, object?>
                {
                    ["sku"] = barcode,
                    ["qtyBefore"] = found.Quantity,
                    ["qtyAfter"] = 0,
                });
            ApplySkuDeduction(found, "1", DeductionSource.ScanAuto);
        }
        else
        {
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
            found.IsBeingPicked = true;
            FocusItemEntry(found);
            var label = found.Name + (found.HasVariation ? $" · {found.Variation}" : "");
            UpdateSearchStatus($"Matched: {label} — enter qty and press Enter");
            Logger.Log($"OrderSearch: SKU matched '{barcode}'");
        }
    }

    // Differentiates which user action triggered a deduction — so ApplySkuDeduction
    // emits the right workflow_event (manual qty entry vs card tap vs a prior auto-complete).
    private enum DeductionSource
    {
        AutoPrior,   // pending item auto-deducted because user moved on to the next scan/tap
        ScanAuto,    // scan matched a qty=1 row — immediate deduct; event already emitted in HandleSkuScan
        ManualQty,   // user typed a number in the qty entry
        CardTap,     // user tapped the qty area on a card
    }

    private void OnPickQtyEntryCompleted(object sender, EventArgs e)
    {
        if (sender is Entry entry && entry.BindingContext is ProductItem item)
            ApplySkuDeduction(item, entry.Text, DeductionSource.ManualQty);
    }

    private void OnPickQtyTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not Entry entry) return;
        if (entry.BindingContext is not ProductItem item) return;

        var raw = e.NewTextValue ?? "";
        // Strip any non-digit characters
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (digits != raw) { entry.Text = digits; return; }

        if (string.IsNullOrEmpty(digits)) return;
        if (int.TryParse(digits, out var qty) && qty > item.Quantity)
            entry.Text = item.Quantity.ToString();
    }

    private void ApplySkuDeduction(ProductItem item, string? qtyText, DeductionSource source)
    {
        if (!int.TryParse(qtyText?.Trim(), out var qty)) qty = 1; // invalid input → default 1
        qty = Math.Max(0, Math.Min(qty, item.Quantity));           // clamp [0, remaining]

        if (qty == 0)
        {
            // User entered 0 — dismiss entry without deducting
            item.IsBeingPicked = false;
            if (item == _pendingSkuProduct) _pendingSkuProduct = null;
            UpdateSearchStatus($"{item.SellerSku} — no deduction (0 entered)");
            return;
        }

        // Emit the action-origin event BEFORE mutating, so qtyBefore/qtyAfter reflect the change.
        // ScanAuto is already reported in HandleSkuScan (as item_scanned_auto); skip here.
        // AutoPrior is a housekeeping side-effect (not a user action on this item) → no event.
        if (source is DeductionSource.ManualQty or DeductionSource.CardTap)
        {
            var owner = Results.FirstOrDefault(o => o.ParsedProducts.Contains(item));
            var stepId = source == DeductionSource.ManualQty ? "manual_qty_entered" : "card_clicked";
            var trigger = source == DeductionSource.ManualQty ? "qty_entered" : "card_tap";
            var payload = new Dictionary<string, object?>
            {
                ["sku"] = item.SellerSku,
                ["qtyBefore"] = item.Quantity,
                ["qtyAfter"] = item.Quantity - qty,
            };
            if (source == DeductionSource.ManualQty) payload["qtyEntered"] = qty;
            else payload["qtyDeducted"] = qty;
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
        item.OrderQcContext = "QC Hold"; // highlight yellow immediately (green if IsFullyPicked takes priority)

        if (item == _pendingSkuProduct) _pendingSkuProduct = null;

        // Animate fully-picked item sliding to bottom
        if (item.IsFullyPicked)
            _ = AnimateAndMoveItemToBottomAsync(item);

        UpdateSearchStatus(item.Quantity == 0
            ? $"✓ {item.SellerSku} fully picked"
            : $"{item.SellerSku} — {item.Quantity} remaining");
        Logger.Log($"OrderSearch: deducted {qty} from '{item.SellerSku}', remaining: {item.Quantity}");

        _ = CheckAndSaveQcStatusAsync();
    }

    private async Task CheckAndSaveQcStatusAsync()
    {
        await CheckCompletedOrdersAsync();
        await SaveQcHoldImmediateAsync();
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
            var payload = new ProductListPayload([.. order.ParsedProducts]);
            var now = DateTime.UtcNow;
            var ok = await ApiService.UpdatePackingStatusAsync(order.PackingId, "QC Hold", payload, checkedBy: StationName);
            if (ok)
            {
                order.PackingStatus = "QC Hold";
                order.UpdatedAt = now;
                order.CheckedAt = now;
                // Do NOT set OrderQcContext here — cards stay white while the user is scanning.
                BuildCarouselUI();
                UpdateSessionStats();

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
            var payload = new ProductListPayload([.. order.ParsedProducts]);
            var now = DateTime.UtcNow;
            var ok = await ApiService.UpdatePackingStatusAsync(
                order.PackingId, "QC Passed", payload, checkedBy: StationName);
            if (ok)
            {
                order.PackingStatus = "QC Passed";
                order.CheckedBy = StationName;
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
                        ["checkedBy"] = StationName.Replace(' ', '-'),
                        ["itemsPicked"] = order.ParsedProducts.Count,
                    });
            }
            UpdateSearchStatus(ok
                ? $"✓ {order.TrackingNumber} — QC Passed · {StationName}"
                : $"⚠ {order.TrackingNumber} — all picked but DB update failed");
            BuildCarouselUI();
            UpdateSessionStats();
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

            var dbPayload = new ProductListPayload([.. order.ParsedProducts]);
            var now = DateTime.UtcNow;
            var ok = await ApiService.UpdatePackingStatusAsync(
                order.PackingId, "QC Hold", dbPayload, checkedBy: StationName);
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

    // ── Focus helper ─────────────────────────────────────────────────────────

    private void FocusItemEntry(ProductItem item)
    {
        _ = Dispatcher.DispatchAsync(async () =>
        {
            await Task.Delay(80); // allow Entry to become visible after IsBeingPicked = true
            var entry = FindDescendant<Entry>(this, e => e.BindingContext == item && e.IsVisible);
            entry?.Focus();
        });
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
        if (sender is not VerticalStackLayout layout) return;
        if (layout.BindingContext is not ProductItem item) return;

        PackingList? order = null;
        foreach (var o in Results)
            if (o.ParsedProducts.Contains(item)) { order = o; break; }
        if (order == null) return;

        bool isQcPassed = _completedPackingIds.Contains(order.PackingId)
            || string.Equals(order.PackingStatus, "QC Passed", StringComparison.OrdinalIgnoreCase);
        if (isQcPassed) { UpdateSearchStatus($"Order {order.TrackingNumber} is QC Passed — no changes allowed"); return; }
        if (item.Quantity <= 0) { UpdateSearchStatus($"{item.SellerSku} — already fully picked"); return; }

        if (_pendingSkuProduct != null)
            ApplySkuDeduction(_pendingSkuProduct, "1", DeductionSource.AutoPrior);

        _pendingSkuProduct = item;

        if (item.Quantity == 1)
        {
            ApplySkuDeduction(item, "1", DeductionSource.CardTap);
        }
        else
        {
            item.IsBeingPicked = true;
            FocusItemEntry(item);
            var label = item.Name + (item.HasVariation ? $" · {item.Variation}" : "");
            UpdateSearchStatus($"Matched: {label} — enter qty and press Enter");
        }
    }

    // ── Product card hover ───────────────────────────────────────────────────

    private void OnProductCardEntered(object sender, PointerEventArgs e)
    {
        if (sender is PointerGestureRecognizer { Parent: Border card })
        {
            var item = card.BindingContext as ProductItem;
            if (item?.IsFullyPicked != true)
                card.BackgroundColor = Color.FromArgb("#f8fafc");
        }
    }

    private void OnProductCardExited(object sender, PointerEventArgs e)
    {
        if (sender is PointerGestureRecognizer { Parent: Border card })
        {
            var item = card.BindingContext as ProductItem;
            // Restore the color driven by CardBgColor (respects OrderQcContext)
            card.BackgroundColor = item?.CardBgColor ?? Colors.White;
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
            _pendingSkuProduct = null;
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

        // Display newest first (leftmost). Session list order is unchanged.
        for (int i = count - 1; i >= 0; i--)
        {
            var capturedIdx = i;
            var isActive = i == _sessionIndex;
            var query = _sessions[i].Query;

            // ── Determine color palette (status-based for both active and inactive) ──
            var sessionOrders = _sessions[i].Data;
            var qualified = sessionOrders
                .Where(o => _qualifiedPackingIds.Contains(o.PackingId))
                .ToList();
            bool allPassed = qualified.Count > 0 &&
                qualified.All(o => string.Equals(o.PackingStatus, "QC Passed", StringComparison.OrdinalIgnoreCase));
            bool anyHold = qualified.Any(o =>
                string.Equals(o.PackingStatus, "QC Hold", StringComparison.OrdinalIgnoreCase));

            // ── Apply carousel filter ─────────────────────────────────────────────────
            string sessionStatus = qualified.Count == 0 ? "preProcessed"
                : allPassed ? "completed"
                : anyHold ? "incomplete"
                : "incoming";
            if (_carouselFilter is not null && sessionStatus != _carouselFilter)
                continue;

            Color bgActive, strokeActive, idxActive;
            Color bgInactive, bgHover, strokeInactive, titleInactive, idxInactive;

            if (qualified.Count == 0)
            {
                // Grey: all orders were already QC Hold/Passed when scanned
                bgActive = Color.FromArgb("#6b7280"); strokeActive = Color.FromArgb("#4b5563"); idxActive = Color.FromArgb("#d1d5db");
                bgInactive = Color.FromArgb("#f9fafb"); bgHover = Color.FromArgb("#f3f4f6");
                strokeInactive = Color.FromArgb("#e5e7eb"); titleInactive = Color.FromArgb("#6b7280"); idxInactive = Color.FromArgb("#9ca3af");
            }
            else if (allPassed)
            {
                // Green: all qualified orders are QC Passed
                bgActive = Color.FromArgb("#16a34a"); strokeActive = Color.FromArgb("#15803d"); idxActive = Color.FromArgb("#bbf7d0");
                bgInactive = Color.FromArgb("#f0fdf4"); bgHover = Color.FromArgb("#dcfce7");
                strokeInactive = Color.FromArgb("#bbf7d0"); titleInactive = Color.FromArgb("#166534"); idxInactive = Color.FromArgb("#86efac");
            }
            else if (anyHold)
            {
                // Yellow: at least one qualified order is QC Hold
                bgActive = Color.FromArgb("#d97706"); strokeActive = Color.FromArgb("#b45309"); idxActive = Color.FromArgb("#fef3c7");
                bgInactive = Color.FromArgb("#fffbeb"); bgHover = Color.FromArgb("#fef3c7");
                strokeInactive = Color.FromArgb("#fde68a"); titleInactive = Color.FromArgb("#b45309"); idxInactive = Color.FromArgb("#fcd34d");
            }
            else
            {
                // Blue: all qualified orders still To be packed (incoming)
                bgActive = Color.FromArgb("#2563eb"); strokeActive = Color.FromArgb("#1d4ed8"); idxActive = Color.FromArgb("#bfdbfe");
                bgInactive = Color.FromArgb("#eff6ff"); bgHover = Color.FromArgb("#dbeafe");
                strokeInactive = Color.FromArgb("#bfdbfe"); titleInactive = Color.FromArgb("#1d4ed8"); idxInactive = Color.FromArgb("#93c5fd");
            }

            Color bgColor = isActive ? bgActive : bgInactive;
            Color strokeColor = isActive ? strokeActive : strokeInactive;
            Color titleColor = isActive ? Colors.White : titleInactive;
            Color indexColor = isActive ? idxActive : idxInactive;

            var titleLabel = new Label
            {
                Text = query,
                FontSize = 11,
                FontAttributes = FontAttributes.Bold,
                TextColor = titleColor,
                LineBreakMode = LineBreakMode.NoWrap,
                HorizontalTextAlignment = TextAlignment.Center,
            };

            var indexLabel = new Label
            {
                Text = $"#{i + 1}",
                FontSize = 13,
                FontAttributes = FontAttributes.Bold,
                TextColor = indexColor,
                HorizontalTextAlignment = TextAlignment.Center,
            };

            // ── Platform tag (coloured pill label) ───────────────────────────────────
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

            // ── Build platform tag border (top-right) ────────────────────────────────
            Border? platformTagBorder = null;
            if (platformName != null)
                platformTagBorder = new Border
                {
                    BackgroundColor = platformTagColor,
                    StrokeThickness = 0,
                    StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(3) },
                    Padding = new Thickness(5, 2),
                    VerticalOptions = LayoutOptions.Center,
                    Content = new Label
                    {
                        Text = platformName,
                        FontSize = 9,
                        FontAttributes = FontAttributes.Bold,
                        TextColor = Colors.White,
                        VerticalOptions = LayoutOptions.Center,
                    },
                };

            // ── Top row: #N (left) ── spacer ── platform tag (right) ─────────────────
            var topRow = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new ColumnDefinition(GridLength.Auto),  // #N
                    new ColumnDefinition(GridLength.Star),  // spacer
                    new ColumnDefinition(GridLength.Auto),  // platform tag
                },
                HorizontalOptions = LayoutOptions.Fill,
                RowSpacing = 0,
            };
            topRow.Add(indexLabel, 0, 0);
            if (platformTagBorder != null)
                topRow.Add(platformTagBorder, 2, 0);

            // Tracking number spans all columns below
            Grid.SetColumnSpan(titleLabel, 3);
            var cardContent = new Grid
            {
                RowDefinitions = new RowDefinitionCollection
                {
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto),
                },
                RowSpacing = 4,
                HorizontalOptions = LayoutOptions.Fill,
            };
            cardContent.Add(topRow, 0, 0);
            cardContent.Add(titleLabel, 0, 1);

            var card = new Border
            {
                StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(8) },
                Stroke = strokeColor,
                StrokeThickness = 1,
                BackgroundColor = bgColor,
                Padding = new Thickness(14, 6),
                Content = cardContent,
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

            CarouselLayout.Children.Add(card);
        }

        // Auto-scroll so the active card is visible after layout settles
        _ = Dispatcher.DispatchAsync(async () =>
        {
            await Task.Delay(80);
            int n = _sessions.Count;
            if (_sessionIndex < 0 || _sessionIndex >= n || CarouselLayout.Children.Count == 0) return;
            // Reversed display: newest (highest session index) is at display position 0 (leftmost)
            int displayPos = n - 1 - _sessionIndex;
            if (displayPos >= 0 && displayPos < CarouselLayout.Children.Count &&
                CarouselLayout.Children[displayPos] is VisualElement ve)
                await NavRow.ScrollToAsync(ve, ScrollToPosition.MakeVisible, animated: true);
        });
    }

    private async Task AnimateAllCarouselCardsAsync()
    {
        var children = CarouselLayout.Children.OfType<VisualElement>().ToList();
        if (children.Count == 0) return;

        // New card (leftmost, index 0): slide in from off-screen left
        if (children.Count > 0)
        {
            children[0].TranslationX = -260;
            children[0].Opacity = 0;
        }

        // Existing cards (index 1+): nudge from a slight rightward offset
        // (they appear to shift right as the new card pushes in from the left)
        for (int j = 1; j < children.Count; j++)
        {
            children[j].TranslationX = 60;
            children[j].Opacity = 0.7;
        }

        var tasks = new List<Task>();
        if (children.Count > 0)
            tasks.Add(SlideCardInAsync(children[0], 0));
        for (int j = 1; j < children.Count; j++)
            tasks.Add(SlideExistingCardAsync(children[j], j * 30));
        await Task.WhenAll(tasks);
    }

    private static async Task SlideCardInAsync(VisualElement card, int delayMs)
    {
        if (delayMs > 0) await Task.Delay(delayMs);
        await Task.WhenAll(
            card.TranslateToAsync(0, 0, 300, Easing.SinOut),
            card.FadeToAsync(1.0, 260, Easing.SinIn));
    }

    private static async Task SlideExistingCardAsync(VisualElement card, int delayMs)
    {
        if (delayMs > 0) await Task.Delay(delayMs);
        await Task.WhenAll(
            card.TranslateToAsync(0, 0, 220, Easing.SinOut),
            card.FadeToAsync(1.0, 180, Easing.SinIn));
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

        _orderLoaded = Results.Count > 0;
        _isFirstItemScan = _orderLoaded;
        NotFoundCard.IsVisible = Results.Count == 0;
        NotFoundLabel.Text = Results.Count == 0 ? $"{session.Query} not found" : "";
        UpdateSearchStatus(session.Query);
        BuildCarouselUI();
        UpdateSessionStats(); // stats span all sessions — no reset needed here
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
            w.Content.KeyDown += OnWindowKeyDown;
    }

    private void UnregisterKeyboardHandler()
    {
        if (Application.Current?.Windows is { Count: > 0 } wins &&
            wins[0].Handler?.PlatformView is Microsoft.UI.Xaml.Window w)
            w.Content.KeyDown -= OnWindowKeyDown;
    }

    private void OnWindowKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        // Don't intercept arrows while typing in any Entry / TextBox
        if (e.OriginalSource is Microsoft.UI.Xaml.Controls.TextBox) return;

        var isLeft = e.Key == Windows.System.VirtualKey.Left;
        var isRight = e.Key == Windows.System.VirtualKey.Right;
        if (!isLeft && !isRight) return;

        // Session navigation — carousel is newest-left, so Left = newer (+1 index), Right = older (-1 index)
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
            SearchEntry.Text = items[_historyNavIndex];
            e.Handled = true;
        }
        else
        {
            if (_historyNavIndex > 0)
            {
                _historyNavIndex--;
                SearchEntry.Text = items[_historyNavIndex];
            }
            else
            {
                _historyNavIndex = -1;
                SearchEntry.Text = string.Empty;
            }
            e.Handled = true;
        }
    }
#endif

    // ── Search history ────────────────────────────────────────────────────────

    private void OnHistoryToggle(object sender, TappedEventArgs e)
    {
        _historyExpanded = !_historyExpanded;
        HistoryDropdown.IsVisible = _historyExpanded;
        HistoryChevron.Text = _historyExpanded ? "▴" : "▾";
    }

    private void OnHistoryClearAll(object sender, TappedEventArgs e)
    {
        SearchHistoryService.Instance.Clear();
        RefreshHistoryItems();
        UpdateHistoryHeader();
    }

    private void RefreshHistoryItems()
    {
        HistoryItemsLayout.Children.Clear();
        var items = SearchHistoryService.Instance.Items;

        for (var i = 0; i < items.Count; i++)
        {
            var captured = items[i];

            var icon = new Label
            {
                Text = "↩",
                FontSize = 11,
                TextColor = Color.FromArgb("#9ca3af"),
                VerticalOptions = LayoutOptions.Center,
                WidthRequest = 18
            };

            var text = new Label
            {
                Text = captured,
                FontSize = 12,
                TextColor = Color.FromArgb("#374151"),
                VerticalOptions = LayoutOptions.Center,
                LineBreakMode = LineBreakMode.TailTruncation
            };

            var del = new Label
            {
                Text = "×",
                FontSize = 15,
                TextColor = Color.FromArgb("#d1d5db"),
                Margin = new Thickness(6, 0, 10, 0),
                VerticalOptions = LayoutOptions.Center
            };
            del.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(() =>
                {
                    SearchHistoryService.Instance.Remove(captured);
                    RefreshHistoryItems();
                    UpdateHistoryHeader();
                })
            });

            // Left clickable area: icon + text
            var searchGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Star)
                },
                ColumnSpacing = 8,
                Padding = new Thickness(10, 9, 6, 9)
            };
            searchGrid.Add(icon, 0, 0);
            searchGrid.Add(text, 1, 0);
            searchGrid.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(async () =>
                {
                    SearchEntry.Text = captured;
                    await ExecuteSearchAsync(captured);
                })
            });

            // Row container
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto)
                },
                BackgroundColor = Colors.White
            };

            // Hover effects via local helpers (capture row-level variables)
            void EnterHover()
            {
                row.BackgroundColor = Color.FromArgb("#f5f3ff");
                text.TextColor = Color.FromArgb("#6d28d9");
                icon.TextColor = Color.FromArgb("#7c3aed");
                del.TextColor = Color.FromArgb("#9ca3af");
            }
            void ExitHover()
            {
                row.BackgroundColor = Colors.White;
                text.TextColor = Color.FromArgb("#374151");
                icon.TextColor = Color.FromArgb("#9ca3af");
                del.TextColor = Color.FromArgb("#d1d5db");
            }

            var pRow = new PointerGestureRecognizer();
            pRow.PointerEntered += (s, e) => EnterHover();
            pRow.PointerExited += (s, e) => ExitHover();
            row.GestureRecognizers.Add(pRow);

            // Also register on searchGrid so hover fires reliably inside that area
            var pSearch = new PointerGestureRecognizer();
            pSearch.PointerEntered += (s, e) => EnterHover();
            pSearch.PointerExited += (s, e) => ExitHover();
            searchGrid.GestureRecognizers.Add(pSearch);

            row.Add(searchGrid, 0, 0);
            row.Add(del, 1, 0);
            HistoryItemsLayout.Children.Add(row);

            // Divider between items
            if (i < items.Count - 1)
                HistoryItemsLayout.Children.Add(new BoxView
                {
                    HeightRequest = 1,
                    BackgroundColor = Color.FromArgb("#f3f4f6"),
                    Margin = new Thickness(10, 0)
                });
        }
    }

    private void UpdateHistoryHeader()
    {
        var hasItems = SearchHistoryService.Instance.Items.Count > 0;
        HistoryHeaderRow.IsVisible = hasItems;
        if (!hasItems)
        {
            _historyExpanded = false;
            HistoryDropdown.IsVisible = false;
            HistoryChevron.Text = "▾";
        }
    }

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

        var completedBorder = FindDescendant<Border>(this, b => b.BindingContext == item);
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
            var b = FindDescendant<Border>(this, x => x.BindingContext == sibling);
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

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void UpdateScannerStatus(string msg) =>
        MainThread.BeginInvokeOnMainThread(() => ScannerStatusLabel.Text = msg);

    private void UpdateSearchStatus(string msg) =>
        MainThread.BeginInvokeOnMainThread(() => SearchStatusLabel.Text = msg);

    private static Task<List<ComPortEntry>> GetFriendlyComPortsAsync() => Task.Run(() =>
    {
        var result = new List<ComPortEntry>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%(COM%)'");

            foreach (ManagementObject obj in searcher.Get().Cast<ManagementObject>())
            {
                var name = obj["Name"]?.ToString();
                if (name == null) continue;

                var m = Regex.Match(name, @"\(COM(\d+)\)", RegexOptions.IgnoreCase);
                if (!m.Success) continue;

                var portName = $"COM{m.Groups[1].Value}";
                var label = name[..m.Index].Trim().TrimEnd(',', '-', ' ');
                if (string.IsNullOrWhiteSpace(label)) label = "Serial Device";

                result.Add(new ComPortEntry(portName, $"{label} — {portName}"));
            }

            result = [.. result.OrderBy(x => int.TryParse(x.PortName[3..], out var n) ? n : 999)];
            if (result.Count > 0) return result;
        }
        catch (Exception ex)
        {
            Logger.Log($"OrderSearch GetFriendlyComPortsAsync WMI error: {ex.Message}");
        }

        return SerialPort.GetPortNames()
            .Where(p => p.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => int.TryParse(p[3..], out var n) ? n : 999)
            .Select(p => new ComPortEntry(p, p))
            .ToList();
    });
}
