using app.Helpers;
using app.Services;
using CommunityToolkit.Maui.Alerts;
using Microsoft.Maui.Controls.Shapes;
using System.IO.Ports;
using System.Management;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace app.Views;

[SupportedOSPlatform("windows")]
public partial class OrderSearchPage
{
    private enum ComState { Disconnected, Open, Ready }

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
        OverlayComPortPicker.Items.Clear();
        OverlayComPortPicker.Items.Add("(None)");
        foreach (var p in _comPorts)
        {
            OverlayComPortPicker.Items.Add(p.DisplayName);
        }
        OverlayComPortPicker.SelectedIndex = selIdx;
        _syncingPickers = false;

        // Update badge
        if (selIdx == 0)
            ComPortBadgeLabel.Text = "None";
        else if (selIdx - 1 < _comPorts.Count)
            ComPortBadgeLabel.Text = _comPorts[selIdx - 1].PortName;
    }

    private async void OnOverlayRefreshPorts(object sender, EventArgs e)
        => await LoadComPortsAsync();

    private void OnComPortBadgeTapped(object sender, TappedEventArgs e)
    {
        BuildComPortDropdown();
        ComPortDropdownBackdrop.IsVisible = true;
    }

    private void OnComPortDropdownBackdropTapped(object sender, TappedEventArgs e)
    {
        ComPortDropdownBackdrop.IsVisible = false;
    }

    private void BuildComPortDropdown()
    {
        ComPortDropdownList.Children.Clear();

        // "None" option
        var noneRow = BuildComPortOption("None", _selectedPortName == null);
        noneRow.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(() => SelectComPort(-1))
        });
        ComPortDropdownList.Children.Add(noneRow);

        // Port options
        for (int i = 0; i < _comPorts.Count; i++)
        {
            var port = _comPorts[i];
            var isActive = _selectedPortName == port.PortName;
            var row = BuildComPortOption(port.DisplayName, isActive);
            var idx = i;
            row.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(() => SelectComPort(idx))
            });
            ComPortDropdownList.Children.Add(row);
        }

        // Refresh button
        var refreshRow = new Border
        {
            BackgroundColor = Colors.Transparent,
            Stroke = Colors.Transparent,
            StrokeShape = new RoundRectangle { CornerRadius = 6 },
            Padding = new Thickness(16, 10),
            Margin = new Thickness(0, 4, 0, 0),
        };
        refreshRow.Content = new Label
        {
            Text = "↻ Refresh ports",
            FontSize = 11,
            TextColor = Color.FromArgb("#6b7280"),
        };
        refreshRow.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(async () =>
            {
                await LoadComPortsAsync();
                BuildComPortDropdown();
            })
        });
        ComPortDropdownList.Children.Add(refreshRow);
    }

    private Border BuildComPortOption(string label, bool isActive)
    {
        var border = new Border
        {
            BackgroundColor = isActive ? Color.FromArgb("#f5f5ff") : Colors.Transparent,
            Stroke = Colors.Transparent,
            StrokeShape = new RoundRectangle { CornerRadius = 6 },
            Padding = new Thickness(16, 12),
        };

        var grid = new Grid
        {
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)],
        };
        grid.Add(new Label
        {
            Text = label,
            FontSize = 13,
            FontAttributes = isActive ? FontAttributes.Bold : FontAttributes.None,
            TextColor = isActive ? Color.FromArgb("#4338ca") : Color.FromArgb("#374151"),
        });
        if (isActive)
        {
            var check = new Label
            {
                Text = "✓",
                FontSize = 14,
                FontAttributes = FontAttributes.Bold,
                TextColor = Color.FromArgb("#4338ca"),
                VerticalOptions = LayoutOptions.Center,
            };
            Grid.SetColumn(check, 1);
            grid.Add(check);
        }
        border.Content = grid;
        return border;
    }

    private void SelectComPort(int portIndex)
    {
        ComPortDropdownBackdrop.IsVisible = false;
        ApplyComPortSelection(portIndex + 1);
        if (portIndex < 0)
            ComPortBadgeLabel.Text = "None";
        else if (portIndex < _comPorts.Count)
            ComPortBadgeLabel.Text = _comPorts[portIndex].PortName;
        _syncingPickers = true;
        OverlayComPortPicker.SelectedIndex = portIndex + 1;
        _syncingPickers = false;
    }

    private void OnOverlayComPortSelected(object sender, EventArgs e)
    {
        if (_syncingPickers) return;
        ApplyComPortSelection(OverlayComPortPicker.SelectedIndex);
    }

    private void ApplyComPortSelection(int idx)
    {
        if (idx < 0) return;

        if (idx == 0)
        {
            _selectedPortName = null;
            CloseSerialPort();
            UpdateScannerStatus("No scanner connected");
            UpdateOverlayScannerStatus("No scanner connected");
            MainThread.BeginInvokeOnMainThread(() => ComPortBadgeLabel.Text = "None");
            return;
        }

        var portIdx = idx - 1;
        if (portIdx >= _comPorts.Count) return;

        var portName = _comPorts[portIdx].PortName;
        _selectedPortName = portName;
        MainThread.BeginInvokeOnMainThread(() => ComPortBadgeLabel.Text = portName);
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
                            _currentOperatorId = null;
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
                            _currentOperatorId = null;
                            StartInactivityTimer();
                            Services.StationWsClient.SendOperatorLogin(line, Services.SessionKind.QC);
                            UpdateNavOperatorUI(line);
                            HideLoginOverlay();
                            _ = ShowWelcomeAnimationAsync(line);
                            Logger.Log($"OrderSearch: Operator logged in — {line}");
                            _ = Task.Run(async () =>
                            {
                                var (firstName, operatorId) = await ApiService.GetOperatorInfoAsync(line);
                                if (firstName is null || _currentOperator != line) return;
                                _currentOperatorFirstName = firstName;
                                _currentOperatorId = operatorId;
                                MainThread.BeginInvokeOnMainThread(() =>
                                {
                                    UpdateNavOperatorUI(firstName);
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

    private void TryReconnectComPort()
    {
        if (_selectedPortName == null) return;
        try
        {
            _serialPort?.Dispose();
            _serialPort = new SerialPort(_selectedPortName, 9600, Parity.None, 8, StopBits.One)
            {
                NewLine = "\r",
                ReadTimeout = 500,
                DtrEnable = true
            };
            _serialPort.DataReceived += OnSerialDataReceived;
            _serialPort.Open();
            ApplyComStateVisuals(ComState.Ready);
            UpdateScannerStatus($"Scanner reconnected ({_selectedPortName})");
            UpdateOverlayScannerStatus($"Scanner ready ({_selectedPortName})");
            Logger.Log($"OrderSearch: COM port {_selectedPortName} reconnected");
        }
        catch
        {
            try { _serialPort?.Dispose(); } catch { }
            _serialPort = null;
        }
    }

    // ── COM Port Status & Helpers ────────────────────────────────────────────

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
        var strokeColor = state switch
        {
            ComState.Ready => Color.FromArgb("#4ade80"),
            ComState.Open => Color.FromArgb("#facc15"),
            _ => Color.FromArgb("#30ffffff"),
        };
        ComPortBadge.Stroke = strokeColor;
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
                if (_selectedPortName != null && _currentOperator != null)
                    TryReconnectComPort();
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
