using app.Helpers;
using app.Models;
using app.Services;
using app.Workflows;
using CommunityToolkit.Maui.Alerts;
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
    private enum AppMode { QC }

    private record ComPortEntry(string PortName, string DisplayName);

    private SerialPort? _serialPort;
    private List<ComPortEntry> _comPorts = [];
    private bool _isSearching;
    private AppMode _currentMode = AppMode.QC;
    private IDispatcherTimer? _comHeartbeatTimer;
    private long _lastSerialDataTicks;
    private int _historyNavIndex = -1;

    // Search-session navigation (back / forward through previous scans)
    private record SearchSession(string Query, List<PackingList> Data);
    private readonly List<SearchSession> _sessions = [];
    private int _sessionIndex = -1;
    private readonly Queue<string> _pendingScanQueue = new();
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

    // ── COM Port Management ──────────────────────────────────────────────────

    public async Task LoadComPortsAsync()
    {
        _comPorts = await GetFriendlyComPortsAsync();

        var selectedPort = _serialPort?.IsOpen == true ? _serialPort.PortName : null;
        int selIdx = 0;
        if (selectedPort != null)
        {
            var idx = _comPorts.FindIndex(p => p.PortName == selectedPort);
            if (idx >= 0) selIdx = idx + 1;
        }

        _syncingPickers = true;
        ComPortPicker.Items.Clear();
        OverlayComPortPicker.Items.Clear();
        ComPortPicker.Items.Add("(None)");
        OverlayComPortPicker.Items.Add("(None)");
        foreach (var p in _comPorts)
        {
            ComPortPicker.Items.Add(p.DisplayName);
            OverlayComPortPicker.Items.Add(p.DisplayName);
        }
        ComPortPicker.SelectedIndex = selIdx;
        OverlayComPortPicker.SelectedIndex = selIdx;
        _syncingPickers = false;
    }

    private async void OnOverlayRefreshPorts(object sender, EventArgs e)
        => await LoadComPortsAsync();

    private void OnComPortSelected(object sender, EventArgs e)
    {
        if (_syncingPickers) return;
        _syncingPickers = true;
        OverlayComPortPicker.SelectedIndex = ComPortPicker.SelectedIndex;
        _syncingPickers = false;
        ApplyComPortSelection(ComPortPicker.SelectedIndex);
    }

    private void OnOverlayComPortSelected(object sender, EventArgs e)
    {
        if (_syncingPickers) return;
        _syncingPickers = true;
        ComPortPicker.SelectedIndex = OverlayComPortPicker.SelectedIndex;
        _syncingPickers = false;
        ApplyComPortSelection(OverlayComPortPicker.SelectedIndex);
    }

    private void ApplyComPortSelection(int idx)
    {
        if (idx < 0) return;

        if (idx == 0)
        {
            CloseSerialPort();
            UpdateScannerStatus("No scanner connected");
            UpdateOverlayScannerStatus("No scanner connected");
            MainThread.BeginInvokeOnMainThread(() => HeaderComPortLabel.Text = "");
            return;
        }

        var portIdx = idx - 1;
        if (portIdx >= _comPorts.Count) return;

        var portName = _comPorts[portIdx].PortName;
        MainThread.BeginInvokeOnMainThread(() => HeaderComPortLabel.Text = portName);
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
            StartComHeartbeatTimer();
            UpdateScannerStatus($"Scanner ready ({portName}) — waiting for scan");
            UpdateOverlayScannerStatus($"Scanner ready ({portName}) — scan your badge");
            Logger.Log($"OrderSearch: Serial port {portName} opened");
        }
        catch (Exception ex)
        {
            Logger.Log($"OrderSearch serial port: {ex}");
            UpdateScannerStatus($"COM error: {ex.Message}");
            UpdateOverlayScannerStatus($"COM error: {ex.Message}");
        }
    }

    private void OnSerialDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        Interlocked.Exchange(ref _lastSerialDataTicks, DateTime.UtcNow.Ticks);
        try
        {
            var line = _serialPort?.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(line))
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    // Operator badge detection — intercept before search
                    if (AppSettings.TryParseOperatorBarcode(line) is { } badge)
                    {
                        bool loggingOut = _currentOperator == line;
                        if (loggingOut)
                        {
                            var logoutName = _currentOperatorFirstName ?? line;
                            _currentOperator = null;
                            _currentOperatorFirstName = null;
                            StopInactivityTimer();
                            Services.StationWsClient.SendOperatorLogout();
                            UpdateNavOperatorUI(null);
                            ShowLoginOverlay();
                            UpdateOverlayScannerStatus($"Logged out — {logoutName}");
                            _ = Toast.Make("Logged out").Show();
                            Logger.Log($"OrderSearch: Operator logged out — {logoutName}");
                        }
                        else
                        {
                            _currentOperator = line;
                            _currentOperatorFirstName = null;
                            StartInactivityTimer();
                            Services.StationWsClient.SendOperatorLogin(line, Services.SessionKind.QC);
                            UpdateNavOperatorUI(line);
                            HideLoginOverlay();
                            _ = ShowWelcomeAnimationAsync(line);
                            Logger.Log($"OrderSearch: Operator logged in — {line}");
                            _ = Task.Run(async () =>
                            {
                                var firstName = await ApiService.GetOperatorFirstNameAsync(line);
                                if (firstName is null || _currentOperator != line) return;
                                _currentOperatorFirstName = firstName;
                                MainThread.BeginInvokeOnMainThread(() =>
                                {
                                    UpdateNavOperatorUI(firstName);
                                    // Update banner if animation still running
                                    if (WelcomeBanner.IsVisible)
                                        WelcomeLabel.Text = $"Welcome, {firstName}";
                                });
                            });
                        }
                        return;
                    }

                    // Normalize KEX QR codes: "KEXLM1000234185 1 1 DJD001" → "KEXLM1000234185"
                    var rawLine = line;
                    line = AppSettings.NormalizeTrackingNumber(line);
                    if (line != rawLine)
                        Logger.Log($"OrderSearch: KEX normalized: {rawLine} → {line}");

                    if (_isSearching)
                    {
                        _pendingScanQueue.Enqueue(line);
                        UpdateSearchStatus($"Queued: {line} (processing previous scan…)");
                        Logger.Log($"OrderSearch: queued scan '{line}' (busy)");
                        return;
                    }

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
        StopComHeartbeatTimer();
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
        if (_pendingSkuProduct != null) { _pendingSkuProduct.IsBeingPicked = false; SetActiveProduct(null); }
        Results.Clear();
        UpdateHeaderOrderInfo();
        NotFoundCard.IsVisible = false;

        Logger.Log($"OrderSearch: querying for '{input}'");
        var rows = preloaded ?? await ApiService.SearchAsync(input);

        foreach (var r in rows) { Results.Add(r); ActiveResults.Add(r); }
        UpdateHeaderOrderInfo();

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
            UpdateScanIndicator(barcode, found: false);
            UpdateSearchStatus(blockedByQcPassed
                ? $"SKU '{barcode}' belongs to a QC Passed order — no changes allowed"
                : $"SKU '{barcode}' not found in this order");
            return;
        }

        UpdateScanIndicator(barcode, found: true);
        SetActiveProduct(found);

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
            found.PickQtyText = "1";
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
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (digits != raw) { entry.Text = digits; return; }
        if (digits.Length > 1 && digits[0] == '0') { entry.Text = digits.TrimStart('0'); return; }

        if (string.IsNullOrEmpty(digits)) return;
        if (int.TryParse(digits, out var qty) && qty > item.Quantity)
        {
            // Overflow: use only the last typed digit if it fits, otherwise cap
            var lastDigit = digits[^1..];
            if (int.TryParse(lastDigit, out var single) && single <= item.Quantity)
                entry.Text = lastDigit;
            else
                entry.Text = item.Quantity.ToString();
        }
    }

    private void ApplySkuDeduction(ProductItem item, string? qtyText, DeductionSource source)
    {
        if (!int.TryParse(qtyText?.Trim(), out var qty)) qty = 1; // invalid input → default 1
        qty = Math.Max(0, Math.Min(qty, item.Quantity));           // clamp [0, remaining]

        if (qty == 0)
        {
            // User entered 0 — dismiss entry without deducting
            item.IsBeingPicked = false;
            if (item == _pendingSkuProduct) SetActiveProduct(null);
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
        item.OrderQcContext = item.VerifiedQuantity > 0 ? "QC Hold" : ""; // reset to white when no items verified

        if (item == _pendingSkuProduct) SetActiveProduct(null);

        // Animate fully-picked item sliding to bottom
        if (item.IsFullyPicked)
            _ = AnimateAndMoveItemToBottomAsync(item);

        UpdateSearchStatus(item.IsFullyPicked
            ? $"✓ {item.SellerSku} fully verified"
            : $"{item.SellerSku} — {item.VerifiedQuantity}/{item.RequiredQuantity} verified");
        Logger.Log($"OrderSearch: verified {qty} for '{item.SellerSku}', now {item.VerifiedQuantity}/{item.RequiredQuantity}");

        _ = CheckAndSaveQcStatusAsync();
    }

    private async Task CheckAndSaveQcStatusAsync()
    {
        await CheckCompletedOrdersAsync();
        await SaveQcHoldImmediateAsync();
        FlushCarouselIfDirty();
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
            var ok = await ApiService.UpdatePackingStatusAsync(order.PackingId, "QC Hold", payload,
                checkedBy: EffectiveOperator, checkingStationId: AppSettings.ResolvedStationId);
            if (ok)
            {
                order.PackingStatus = "QC Hold";
                order.UpdatedAt = now;
                order.CheckedAt = now;
                // Do NOT set OrderQcContext here — cards stay white while the user is scanning.
                _carouselDirty = true;

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
                        ["itemsPicked"] = order.ParsedProducts.Count,
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
            var totalItems = Results.Sum(o => o.ParsedProducts.Count);
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

    // ── Focus helper ─────────────────────────────────────────────────────────

    private void FocusItemEntry(ProductItem item)
    {
        _ = Dispatcher.DispatchAsync(async () =>
        {
            await Task.Delay(80); // allow Entry to become visible after IsBeingPicked = true
            var entry = FindDescendant<Entry>(this, e => e.BindingContext == item && e.IsVisible);
            if (entry is null) return;
            entry.Focus();
            entry.CursorPosition = entry.Text?.Length ?? 0;
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

        SetActiveProduct(item);

        if (item.Quantity == 1)
        {
            ApplySkuDeduction(item, "1", DeductionSource.CardTap);
        }
        else
        {
            item.PickQtyText = "0";
            item.IsBeingPicked = true;
            FocusItemEntry(item);
            var label = item.Name + (item.HasVariation ? $" · {item.Variation}" : "");
            UpdateSearchStatus($"Matched: {label} — enter qty and press Enter");
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

        foreach (var item in allProducts)
        {
            if (!enrichments.TryGetValue(item.SellerSku, out var e)) continue;
            item.CategoryName = e.CategoryName;
            item.CategoryId = e.CategoryId;
            item.ImagePath = e.ImagePath;
            item.QcNotes = e.QcNotes;
            item.Brand = e.Brand;
            item.SwatchColor = ColorSwatchHelper.ParseSwatchColor(item.Variation);
        }

        foreach (var order in Results)
            CategoryBadgeHelper.AssignBadges(order.ParsedProducts);

        var apiBase = AppSettings.ApiUrl ?? "http://localhost:8080";
        foreach (var item in allProducts)
        {
            if (!item.HasImagePath) continue;
            var captured = item;
            _ = Task.Run(async () =>
            {
                var path = await ProductImageCache.EnsureAsync(captured.SellerSku, apiBase);
                if (path != null)
                    MainThread.BeginInvokeOnMainThread(() => captured.LocalImagePath = path);
            });
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
            var componentItems = components.Select(c => new BundleComponentItem
            {
                ComponentProductId = c.ComponentProductId,
                Name = c.ProductName,
                Variation = c.ProductVariation,
                SellerSku = c.SellerSku,
                Quantity = c.Quantity,
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

    // ── Product image overlay ──────────────────────────────────────────────

    private ProductItem? _overlayItem;

    private void OnProductCardTapped(object sender, TappedEventArgs e)
    {
        ProductItem? item = null;
        if (sender is VisualElement ve)
            item = ve.BindingContext as ProductItem;
        if (item == null) return;

        ShowProductImageOverlay(item);
    }

    private void ShowProductImageOverlay(ProductItem item)
    {
        _overlayItem = item;

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

        // Category badge
        if (!string.IsNullOrWhiteSpace(item.CategoryBadge))
        {
            OverlayCategoryBadge.IsVisible = true;
            OverlayCategoryLabel.Text = item.CategoryBadge;
            OverlayCategoryBadge.BackgroundColor = item.CategoryBadgeBg;
        }
        else
        {
            OverlayCategoryBadge.IsVisible = false;
        }

        // Item position (e.g., "ITEM 03 of 14")
        var order = Results.FirstOrDefault(o => o.ParsedProducts.Contains(item));
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
        OverlayVerifiedQty.TextColor = item.VerifiedQuantity >= item.RequiredQuantity
            ? Color.FromArgb("#10B981")
            : Color.FromArgb("#111827");

        // Product info
        OverlayProductName.Text = item.BaseName;
        OverlaySkuLabel.Text = item.SellerSku;

        // Variation badge (purple)
        if (item.HasVariation)
        {
            OverlayVariationLabel.Text = item.Variation;
            OverlayVariationBorder.IsVisible = true;
        }
        else
        {
            OverlayVariationBorder.IsVisible = false;
        }

        // QC Notes (borderless style)
        if (item.HasQcNotes)
        {
            OverlayNotesLabel.Text = item.QcNotes!;
            OverlayNotesLabel.TextColor = Color.FromArgb("#dc2626");
            OverlayNotesBorder.BackgroundColor = Color.FromArgb("#fef2f2");
        }
        else
        {
            OverlayNotesLabel.Text = "no notes";
            OverlayNotesLabel.TextColor = Color.FromArgb("#d1d5db");
            OverlayNotesBorder.BackgroundColor = Color.FromArgb("#fafafa");
        }

        OverlayPickEntry.IsVisible = false;
        OverlayPickEntry.Text = "";

        // Show with animation
        ProductImageOverlay.IsVisible = true;
        ProductImageOverlay.Opacity = 0;
        OverlayCard.Scale = 0.85;
        _ = Task.WhenAll(
            ProductImageOverlay.FadeToAsync(1, 200, Easing.CubicOut),
            OverlayCard.ScaleToAsync(1, 250, Easing.CubicOut));
    }

    private void RefreshOverlayQuantity()
    {
        if (_overlayItem == null) return;
        OverlayVerifiedQty.Text = _overlayItem.VerifiedQuantity.ToString();
        OverlayReqQty.Text = _overlayItem.RequiredQuantity.ToString();
        OverlayVerifiedQty.TextColor = _overlayItem.VerifiedQuantity >= _overlayItem.RequiredQuantity
            ? Color.FromArgb("#10B981")
            : Color.FromArgb("#111827");
    }

    private async void OnImageOverlayBackdropTapped(object sender, TappedEventArgs e)
    {
        await DismissImageOverlayAsync();
    }

    private async Task DismissImageOverlayAsync()
    {
        await Task.WhenAll(
            ProductImageOverlay.FadeToAsync(0, 180, Easing.CubicIn),
            OverlayCard.ScaleToAsync(0.85, 180, Easing.CubicIn));
        ProductImageOverlay.IsVisible = false;
        _overlayItem = null;
    }

    private void OnOverlayImageTapped(object sender, TappedEventArgs e)
    {
        if (_overlayItem == null || _overlayItem.Quantity <= 0) return;

        PackingList? order = null;
        foreach (var o in Results)
            if (o.ParsedProducts.Contains(_overlayItem)) { order = o; break; }
        if (order == null) return;

        bool isQcPassed = _completedPackingIds.Contains(order.PackingId)
            || string.Equals(order.PackingStatus, "QC Passed", StringComparison.OrdinalIgnoreCase);
        if (isQcPassed) return;

        if (_pendingSkuProduct != null && _pendingSkuProduct != _overlayItem)
            ApplySkuDeduction(_pendingSkuProduct, "1", DeductionSource.AutoPrior);

        SetActiveProduct(_overlayItem);
        ApplySkuDeduction(_overlayItem, "1", DeductionSource.CardTap);
        SyncOverlayAfterDeduction(_overlayItem);
    }

    private async void OnOverlayCloseTapped(object sender, TappedEventArgs e)
    {
        await DismissImageOverlayAsync();
    }

    private void OnOverlayPrevTapped(object sender, TappedEventArgs e)
        => NavigateOverlayProduct(-1);

    private void OnOverlayNextTapped(object sender, TappedEventArgs e)
        => NavigateOverlayProduct(1);

    private void OnOverlayQtyTapped(object sender, TappedEventArgs e)
    {
        if (_overlayItem == null) return;

        PackingList? order = null;
        foreach (var o in Results)
            if (o.ParsedProducts.Contains(_overlayItem)) { order = o; break; }
        if (order == null) return;

        bool isQcPassed = _completedPackingIds.Contains(order.PackingId)
            || string.Equals(order.PackingStatus, "QC Passed", StringComparison.OrdinalIgnoreCase);
        if (isQcPassed) { UpdateSearchStatus($"Order {order.TrackingNumber} is QC Passed — no changes allowed"); return; }
        if (_overlayItem.Quantity <= 0) { UpdateSearchStatus($"{_overlayItem.SellerSku} — already fully picked"); return; }

        if (_pendingSkuProduct != null && _pendingSkuProduct != _overlayItem)
            ApplySkuDeduction(_pendingSkuProduct, "1", DeductionSource.AutoPrior);

        SetActiveProduct(_overlayItem);

        if (_overlayItem.Quantity == 1)
        {
            ApplySkuDeduction(_overlayItem, "1", DeductionSource.CardTap);
            SyncOverlayAfterDeduction(_overlayItem);
        }
        else
        {
            OverlayPickEntry.Text = "0";
            OverlayPickEntry.IsVisible = true;
            _ = Dispatcher.DispatchAsync(async () =>
            {
                await Task.Delay(80);
                OverlayPickEntry.Focus();
            });
        }
    }

    private void OnOverlayPickEntryCompleted(object sender, EventArgs e)
    {
        if (_overlayItem == null) return;
        ApplySkuDeduction(_overlayItem, OverlayPickEntry.Text, DeductionSource.ManualQty);
        OverlayPickEntry.IsVisible = false;
        SyncOverlayAfterDeduction(_overlayItem);
    }

    private void OnOverlayPickEntryTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_overlayItem == null) return;
        var raw = e.NewTextValue ?? "";
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (digits != raw) { OverlayPickEntry.Text = digits; return; }
        if (digits.Length > 1 && digits[0] == '0') { OverlayPickEntry.Text = digits.TrimStart('0'); return; }
        if (string.IsNullOrEmpty(digits)) return;
        if (int.TryParse(digits, out var qty) && qty > _overlayItem.Quantity)
        {
            var lastDigit = digits[^1..];
            if (int.TryParse(lastDigit, out var single) && single <= _overlayItem.Quantity)
                OverlayPickEntry.Text = lastDigit;
            else
                OverlayPickEntry.Text = _overlayItem.Quantity.ToString();
        }
    }

    private void SyncOverlayAfterDeduction(ProductItem item)
    {
        OverlayVerifiedQty.Text = item.VerifiedQuantity.ToString();
        OverlayVerifiedQty.TextColor = item.VerifiedQuantity >= item.RequiredQuantity
            ? Color.FromArgb("#10B981")
            : Color.FromArgb("#111827");

        if (item.IsFullyPicked)
            _ = AnimateOverlayAdvanceAsync(item);
    }

    private async Task AnimateOverlayAdvanceAsync(ProductItem item)
    {
        // Green flash on completed item
        OverlayCard.Stroke = Color.FromArgb("#86efac");
        OverlayCard.StrokeThickness = 3;
        OverlayCard.BackgroundColor = Color.FromArgb("#dcfce7");
        await Task.Delay(450);

        var allProducts = Results.SelectMany(o => o.ParsedProducts).ToList();
        var currentIdx = allProducts.IndexOf(item);
        ProductItem? nextUnfinished = null;

        for (int i = 1; i <= allProducts.Count; i++)
        {
            var candidate = allProducts[(currentIdx + i) % allProducts.Count];
            if (!candidate.IsFullyPicked) { nextUnfinished = candidate; break; }
        }

        if (nextUnfinished != null)
        {
            // Crossfade: fade out card, swap content, fade back in
            await OverlayCard.FadeToAsync(0, 150, Easing.CubicIn);

            OverlayCard.Stroke = Colors.Transparent;
            OverlayCard.StrokeThickness = 0;
            OverlayCard.BackgroundColor = Colors.White;

            ShowProductImageOverlay(nextUnfinished);
            SetActiveProduct(nextUnfinished);
            ScrollToProduct(nextUnfinished);

            OverlayCard.Opacity = 0;
            await OverlayCard.FadeToAsync(1, 200, Easing.CubicOut);
        }
        else
        {
            // All done — smooth scale-down dismiss
            await Task.WhenAll(
                OverlayCard.FadeToAsync(0, 300, Easing.CubicIn),
                OverlayCard.ScaleToAsync(0.9, 300, Easing.CubicIn),
                ProductImageOverlay.FadeToAsync(0, 300, Easing.CubicIn));

            ProductImageOverlay.IsVisible = false;
            OverlayCard.BackgroundColor = Colors.White;
            OverlayCard.Stroke = Colors.Transparent;
            OverlayCard.StrokeThickness = 0;
            OverlayCard.Scale = 1;
            OverlayCard.Opacity = 1;
            _overlayItem = null;
        }
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

        if (e.Key == Windows.System.VirtualKey.Escape && CompletionSummaryOverlay.IsVisible)
        {
            _ = DismissCompletionSummaryAsync();
            e.Handled = true;
            return;
        }

        // Block Enter from propagating (prevents navigation to home)
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            return;
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

        if (e.Key == Windows.System.VirtualKey.Escape && ProductImageOverlay.IsVisible)
        {
            _ = DismissImageOverlayAsync(); e.Handled = true; return;
        }
        if (e.Key == Windows.System.VirtualKey.I && !ProductImageOverlay.IsVisible)
        {
            var target = _pendingSkuProduct
                ?? Results.SelectMany(o => o.ParsedProducts).FirstOrDefault(p => !p.IsFullyPicked);
            if (target != null) { ShowProductImageOverlay(target); e.Handled = true; return; }
        }

        // +/- keys verify/unverify active product card
        const Windows.System.VirtualKey VkPlus = (Windows.System.VirtualKey)187;  // = / + key
        const Windows.System.VirtualKey VkMinus = (Windows.System.VirtualKey)189; // - / _ key
        var plusTarget = ProductImageOverlay.IsVisible ? _overlayItem : _pendingSkuProduct;
        if ((e.Key == VkPlus || e.Key == Windows.System.VirtualKey.Add) && plusTarget != null)
        {
            var fakeEl = new Label { BindingContext = plusTarget };
            OnPlusClicked(fakeEl, EventArgs.Empty);
            if (ProductImageOverlay.IsVisible) RefreshOverlayQuantity();
            e.Handled = true;
            return;
        }
        if ((e.Key == VkMinus || e.Key == Windows.System.VirtualKey.Subtract) && plusTarget != null)
        {
            var fakeEl = new Label { BindingContext = plusTarget };
            OnMinusClicked(fakeEl, EventArgs.Empty);
            if (ProductImageOverlay.IsVisible) RefreshOverlayQuantity();
            e.Handled = true;
            return;
        }

        // Up/Down: navigate product cards when order loaded and overlay closed
        var isUp = e.Key == Windows.System.VirtualKey.Up;
        var isDown = e.Key == Windows.System.VirtualKey.Down;
        if ((isUp || isDown) && _orderLoaded && !ProductImageOverlay.IsVisible && Results.Count > 0)
        {
            var allProducts = Results.SelectMany(o => o.ParsedProducts).ToList();
            if (allProducts.Count > 0)
            {
                var currentIdx = _pendingSkuProduct != null ? allProducts.IndexOf(_pendingSkuProduct) : -1;
                int nextIdx;
                if (isDown)
                    nextIdx = currentIdx < allProducts.Count - 1 ? currentIdx + 1 : 0;
                else
                    nextIdx = currentIdx > 0 ? currentIdx - 1 : allProducts.Count - 1;

                SetActiveProduct(allProducts[nextIdx]);
                ScrollToProduct(allProducts[nextIdx]);
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

    // ── Search history ────────────────────────────────────────────────────────

    private void OnHistoryClearAll(object sender, TappedEventArgs e)
    {
        SearchHistoryService.Instance.Clear();
    }

    private void RefreshHistoryItems() { }

    private void UpdateHistoryHeader() { }

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

    // ── Scan indicator ────────────────────────────────────────────────────────

    private void UpdateScanIndicator(string barcode, bool found)
    {
        _lastScanBarcode = barcode;
        _lastScanFound = found;
        _lastScanTime = DateTime.Now;
        StartLastScanTimer();
        UpdateScanIndicatorUI();
    }

    private IDispatcherTimer? _scanAlertRevertTimer;

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
        ShowProductImageOverlay(item);
    }

    // ── +/- button handlers ──────────────────────────────────────────────────

    private void OnPlusClicked(object sender, EventArgs e)
    {
        if (sender is not VisualElement el || el.BindingContext is not ProductItem item) return;
        if (item.Quantity <= 0) return; // already fully verified

        PackingList? order = null;
        foreach (var o in Results)
            if (o.ParsedProducts.Contains(item)) { order = o; break; }
        if (order == null) return;

        bool isQcPassed = _completedPackingIds.Contains(order.PackingId)
            || string.Equals(order.PackingStatus, "QC Passed", StringComparison.OrdinalIgnoreCase);
        if (isQcPassed) { UpdateSearchStatus($"Order {order.TrackingNumber} is QC Passed — no changes allowed"); return; }

        if (_pendingSkuProduct != null && _pendingSkuProduct != item)
            ApplySkuDeduction(_pendingSkuProduct, "1", DeductionSource.AutoPrior);

        SetActiveProduct(item);
        ApplySkuDeduction(item, "1", DeductionSource.CardTap);
    }

    private void OnMinusClicked(object sender, EventArgs e)
    {
        if (sender is not VisualElement el || el.BindingContext is not ProductItem item) return;
        if (item.Quantity >= item.RequiredQuantity) return; // already at 0 verified

        PackingList? order = null;
        foreach (var o in Results)
            if (o.ParsedProducts.Contains(item)) { order = o; break; }
        if (order == null) return;

        bool isQcPassed = _completedPackingIds.Contains(order.PackingId)
            || string.Equals(order.PackingStatus, "QC Passed", StringComparison.OrdinalIgnoreCase);
        if (isQcPassed) { UpdateSearchStatus($"Order {order.TrackingNumber} is QC Passed — no changes allowed"); return; }

        item.Quantity += 1;
        item.OrderQcContext = item.VerifiedQuantity > 0 ? "QC Hold" : "";
        UpdateSearchStatus($"{item.SellerSku} — unverified, {item.VerifiedQuantity}/{item.RequiredQuantity}");
        _ = CheckAndSaveQcStatusAsync();
    }

    private void SimulatePlusOnActiveProduct()
    {
        if (_pendingSkuProduct == null) return;
        var fakeEl = new Label { BindingContext = _pendingSkuProduct };
        OnPlusClicked(fakeEl, EventArgs.Empty);
    }

    private void SimulateMinusOnActiveProduct()
    {
        if (_pendingSkuProduct == null) return;
        var fakeEl = new Label { BindingContext = _pendingSkuProduct };
        OnMinusClicked(fakeEl, EventArgs.Empty);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

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

    private static string GetModeDisplayName(AppMode mode) => mode switch
    {
        AppMode.QC => "QC",
        _ => mode.ToString(),
    };

    private void ApplyMode(AppMode mode)
    {
        _currentMode = mode;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            ModeLabel.Text = GetModeDisplayName(mode);
        });
    }

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

            ResetButton.IsVisible = string.Equals(order.PackingStatus, "QC Hold", StringComparison.OrdinalIgnoreCase);
        });
    }

    private void OnResetButtonTapped(object sender, TappedEventArgs e)
    {
        if (CurrentOrder == null) return;
        var fakeEl = new Label { BindingContext = CurrentOrder };
        OnResetClicked(fakeEl, EventArgs.Empty);
        ResetButton.IsVisible = false;
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
        StopInactivityTimer();
        Services.StationWsClient.SendOperatorLogout();
        UpdateNavOperatorUI(null);
        ShowLoginOverlay();
        if (displayName is not null)
            _ = Toast.Make($"Session ended — {displayName}").Show();
        Logger.Log($"OrderSearch: Operator logged out (inactivity)");
    }

    private enum ComState { Disconnected, Open, Ready }

    private void UpdateScannerStatus(string msg)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var state = msg.Contains("ready", StringComparison.OrdinalIgnoreCase)
                ? ComState.Ready
                : msg.Contains("error", StringComparison.OrdinalIgnoreCase) || msg.Contains("No scanner")
                    ? ComState.Disconnected
                    : ComState.Open;
            ApplyComStateVisuals(state);
        });
    }

    private void ApplyComStateVisuals(ComState state)
    {
        var (color, label, opacity) = state switch
        {
            ComState.Ready => (Color.FromArgb("#4ade80"), "ready", 0.9),        // green
            ComState.Open => (Color.FromArgb("#facc15"), "no data", 0.7),       // yellow
            _ => (Color.FromArgb("#ef4444"), "disconnected", 0.5),              // red
        };
        ScannerStatusDot.TextColor = color;
        HeaderComStatusLabel.Text = label;
        HeaderComStatusLabel.Opacity = opacity;
    }

    private void StartComHeartbeatTimer()
    {
        _comHeartbeatTimer?.Stop();
        Interlocked.Exchange(ref _lastSerialDataTicks, DateTime.UtcNow.Ticks);
        _comHeartbeatTimer = Dispatcher.CreateTimer();
        _comHeartbeatTimer.Interval = TimeSpan.FromSeconds(10);
        _comHeartbeatTimer.Tick += (_, _) =>
        {
            if (_serialPort is not { IsOpen: true })
            {
                ApplyComStateVisuals(ComState.Disconnected);
                _comHeartbeatTimer?.Stop();
                return;
            }
            var lastTicks = Interlocked.Read(ref _lastSerialDataTicks);
            var elapsed = DateTime.UtcNow - new DateTime(lastTicks, DateTimeKind.Utc);
            if (elapsed > TimeSpan.FromSeconds(30))
                ApplyComStateVisuals(ComState.Open);
        };
        _comHeartbeatTimer.Start();
    }

    private void StopComHeartbeatTimer()
    {
        _comHeartbeatTimer?.Stop();
        _comHeartbeatTimer = null;
    }

    private void UpdateOverlayScannerStatus(string msg) =>
        MainThread.BeginInvokeOnMainThread(() => OverlayScannerStatusLabel.Text = msg);

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
