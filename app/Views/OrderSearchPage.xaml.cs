using app.Models;
using app.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Ports;
using System.Management;
using System.Runtime.Versioning;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace app.Views;

[SupportedOSPlatform("windows")]
public partial class OrderSearchPage : ContentPage
{
    private record ComPortEntry(string PortName, string DisplayName);

    private SerialPort? _serialPort;
    private List<ComPortEntry> _comPorts = [];
    private bool _isSearching;

    // Station identifier — computer name, resolved once
    private static readonly string StationName = Environment.MachineName;
    public string StationNameDisplay => $"Station: {StationName}";

    // SKU picking state — set after an order loads; cleared on new search
    private bool _orderLoaded;
    private ProductItem? _pendingSkuProduct;
    private readonly HashSet<int> _completedPackingIds = [];

    public ObservableCollection<PackingList> Results { get; } = new();

    public OrderSearchPage()
    {
        InitializeComponent();
        BindingContext = this;
        ResultsView.ItemsSource = Results;
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadComPortsAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        CloseSerialPort();
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
                        UpdateSearchStatus("Scan a tracking number to load an order first");
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
        => await ExecuteSearchAsync(SearchEntry.Text?.Trim() ?? "");

    private async void OnSearchCommitted(object sender, EventArgs e)
        => await ExecuteSearchAsync(SearchEntry.Text?.Trim() ?? "");

    private async Task ExecuteSearchAsync(string input, List<PackingList>? preloaded = null)
    {
        if (string.IsNullOrWhiteSpace(input) || _isSearching) return;
        SearchEntry.Text = string.Empty;

        if (string.IsNullOrWhiteSpace(AppSettings.ApiUrl))
        {
            UpdateSearchStatus("No backend API URL configured — open Settings to add one.");
            return;
        }

        _isSearching = true;

        // Before clearing, persist any partially-picked orders as QC Hold
        if (_orderLoaded && Results.Count > 0)
            await SaveQcHoldForRemainingOrdersAsync();

        _orderLoaded = false;
        _completedPackingIds.Clear();
        if (_pendingSkuProduct != null) { _pendingSkuProduct.IsBeingPicked = false; _pendingSkuProduct = null; }
        UpdateSearchStatus("Searching…");
        Results.Clear();

        Logger.Log($"OrderSearch: querying for '{input}'");
        var rows = preloaded ?? await ApiService.SearchAsync(input);

        foreach (var r in rows)
            Results.Add(r);

        _orderLoaded = rows.Count > 0;
        var msg = rows.Count > 0 ? $"{rows.Count} result(s) for '{input}'" : $"No results found for '{input}'";
        UpdateSearchStatus(msg);
        EmptyLabel.Text = rows.Count == 0 ? $"No results found for '{input}'" : "";
        _isSearching = false;
    }

    // ── SKU picking ───────────────────────────────────────────────────────────

    private void HandleSkuScan(string barcode)
    {
        // Auto-deduct 1 from the previous pending item before handling the new scan
        if (_pendingSkuProduct != null)
            ApplySkuDeduction(_pendingSkuProduct, "1");

        ProductItem? found = null;
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
            break;
        }

        if (found == null)
        {
            UpdateSearchStatus(blockedByQcPassed
                ? $"SKU '{barcode}' belongs to a QC Passed order — no changes allowed"
                : $"SKU '{barcode}' not found in this order");
            return;
        }

        _pendingSkuProduct = found;
        found.IsBeingPicked = true;
        var label = found.Name + (found.HasVariation ? $" · {found.Variation}" : "");
        UpdateSearchStatus($"Matched: {label} — enter qty and press Enter");
        Logger.Log($"OrderSearch: SKU matched '{barcode}'");
    }

    private void OnPickQtyEntryCompleted(object sender, EventArgs e)
    {
        if (sender is Entry entry && entry.BindingContext is ProductItem item)
            ApplySkuDeduction(item, entry.Text);
    }

    private void OnPickQtyTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not Entry entry) return;
        if (entry.BindingContext is not ProductItem item) return;
        if (string.IsNullOrEmpty(e.NewTextValue)) return;

        if (int.TryParse(e.NewTextValue, out var qty) && qty > item.Quantity)
            entry.Text = item.Quantity.ToString();
    }

    private void ApplySkuDeduction(ProductItem item, string? qtyText)
    {
        if (!int.TryParse(qtyText?.Trim(), out var qty) || qty <= 0) qty = 1;
        qty = Math.Min(qty, item.Quantity); // clamp to remaining qty

        item.Quantity -= qty;
        item.IsBeingPicked = false;

        if (item == _pendingSkuProduct) _pendingSkuProduct = null;

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

            var updatedJson = JsonSerializer.Serialize(order.ParsedProducts.ToList(),
                new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            var now = DateTime.UtcNow;
            var ok = await ApiService.UpdatePackingStatusAsync(order.PackingId, "QC Hold", updatedJson);
            if (ok)
            {
                order.PackingStatus = "QC Hold";
                order.UpdatedAt     = now;
                order.CheckedAt     = now;
                // Do NOT set OrderQcContext here — cards stay white while the user is scanning.
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
            var updatedJson = JsonSerializer.Serialize(order.ParsedProducts.ToList(),
                new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            var now = DateTime.UtcNow;
            var ok = await ApiService.UpdatePackingStatusAsync(
                order.PackingId, "QC Passed", updatedJson, checkedBy: StationName);
            if (ok)
            {
                order.PackingStatus = "QC Passed";
                order.CheckedBy     = StationName;
                order.UpdatedAt     = now;
                order.CheckedAt     = now;
                foreach (var p in order.ParsedProducts)
                    p.OrderQcContext = "QC Passed";
            }
            UpdateSearchStatus(ok
                ? $"✓ {order.TrackingNumber} — QC Passed · {StationName}"
                : $"⚠ {order.TrackingNumber} — all picked but DB update failed");
        }
    }

    /// <summary>
    /// Called before loading a new search when an order is already active.
    /// Saves any partially-picked orders (with remaining items) as "QC Hold".
    /// Only updates orders where at least one SKU was actually scanned.
    /// </summary>
    private async Task SaveQcHoldForRemainingOrdersAsync()
    {
        foreach (var order in Results)
        {
            if (_completedPackingIds.Contains(order.PackingId)) continue;
            if (!order.HasProducts) continue;
            if (order.ParsedProducts.All(p => p.IsFullyPicked)) continue; // already QC Passed
            // Skip if nothing was actually picked (order was only viewed)
            if (!order.ParsedProducts.Any(p => p.Quantity != p.OriginalQuantity)) continue;

            var updatedJson = JsonSerializer.Serialize(order.ParsedProducts.ToList(),
                new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            var now = DateTime.UtcNow;
            var ok = await ApiService.UpdatePackingStatusAsync(
                order.PackingId, "QC Hold", updatedJson);
            if (ok)
            {
                order.PackingStatus = "QC Hold";
                order.UpdatedAt     = now;
                order.CheckedAt     = now;
                foreach (var p in order.ParsedProducts)
                    p.OrderQcContext = "QC Hold";
            }
            Logger.Log($"OrderSearch: {order.TrackingNumber} → QC Hold ({(ok ? "saved" : "DB failed")})");
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
        if (sender is not Button btn || btn.BindingContext is not PackingList order) return;

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
        order.PackingStatus        = null;
        order.CheckedBy            = null;
        order.UpdatedAt            = DateTime.UtcNow;
        order.CheckedAt            = null;
        order.UpdatedProductLists  = null;
        order.ResetToOriginalQuantities();

        UpdateSearchStatus($"↺ {order.TrackingNumber} — reset to original");
        Logger.Log($"OrderSearch: {order.TrackingNumber} → reset QC Hold");
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
                "SELECT Name, Status FROM Win32_PnPEntity WHERE PNPClass = 'Ports'");

            foreach (ManagementObject obj in searcher.Get().Cast<ManagementObject>())
            {
                var name = obj["Name"]?.ToString();
                if (name == null) continue;

                var m = Regex.Match(name, @"\(COM(\d+)\)", RegexOptions.IgnoreCase);
                if (!m.Success) continue;

                var portName = $"COM{m.Groups[1].Value}";
                var label    = name[..m.Index].Trim().TrimEnd(',', '-', ' ');
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
