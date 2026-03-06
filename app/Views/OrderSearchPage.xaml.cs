using app.Models;
using app.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Ports;
using System.Management;
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
                MainThread.BeginInvokeOnMainThread(async () => await ExecuteSearchAsync(line));
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

    private async Task ExecuteSearchAsync(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || _isSearching) return;

        if (string.IsNullOrWhiteSpace(AppSettings.ConnectionString))
        {
            UpdateSearchStatus("No database connection configured — open Settings to add one.");
            return;
        }

        _isSearching = true;
        UpdateSearchStatus("Searching…");
        Results.Clear();

        Logger.Log($"OrderSearch: querying for '{input}'");
        var rows = await DatabaseService.SearchAsync(input);

        foreach (var r in rows)
            Results.Add(r);

        var msg = rows.Count > 0 ? $"{rows.Count} result(s) for '{input}'" : $"No results found for '{input}'";
        UpdateSearchStatus(msg);
        EmptyLabel.Text = rows.Count == 0 ? $"No results found for '{input}'" : "";
        _isSearching = false;
    }

    // ── Product card hover ───────────────────────────────────────────────────

    private void OnProductCardEntered(object sender, PointerEventArgs e)
    {
        if (sender is PointerGestureRecognizer pgr && pgr.Parent is Border card)
            card.BackgroundColor = Color.FromArgb("#f8fafc");
    }

    private void OnProductCardExited(object sender, PointerEventArgs e)
    {
        if (sender is PointerGestureRecognizer pgr && pgr.Parent is Border card)
            card.BackgroundColor = Colors.White;
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
