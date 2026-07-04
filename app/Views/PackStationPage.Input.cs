using app.Services;
using Microsoft.Maui.Controls.Shapes;
using System.IO.Ports;
using System.Management;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace app.Views;

[SupportedOSPlatform("windows")]
public partial class PackStationPage
{
    private record ComPortEntry(string PortName, string DisplayName);

    private SerialPort? _serialPort;
    private string? _selectedPortName;
    private List<ComPortEntry> _comPorts = [];
    private bool _syncingPickers;
    private IDispatcherTimer? _comHeartbeatTimer;

    // ── COM port management ──────────────────────────────────────────────────────

    public async Task LoadComPortsAsync()
    {
        _comPorts = await GetFriendlyComPortsAsync();

        // Base the picker on the persisted selection, not the live port: after returning
        // to the page the port is closed but _selectedPortName still holds the intent.
        var selectedPort = _selectedPortName;
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
            OverlayComPortPicker.Items.Add(p.DisplayName);
        OverlayComPortPicker.SelectedIndex = selIdx;
        _syncingPickers = false;
        ComPortBadgeLabel.Text = selIdx == 0 ? "None" : _comPorts[selIdx - 1].PortName;
    }

    private async void OnOverlayRefreshPorts(object sender, EventArgs e)
        => await LoadComPortsAsync();

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
            UpdateOverlayScannerStatus($"Scanner ready ({portName}) — scan your badge");
            Logger.Log($"PackStation: Serial port {portName} opened");
        }
        catch (Exception ex)
        {
            Logger.Log($"PackStation serial port: {ex}");
            UpdateOverlayScannerStatus($"COM error: {ex.Message}");
        }
    }

    // ── COM port header badge + dropdown (forked from Order Search) ───────────────

    private void OnComPortBadgeTapped(object sender, TappedEventArgs e)
    {
        BuildComPortDropdown();
        ComPortDropdownBackdrop.IsVisible = true;
    }

    private void OnComPortDropdownBackdropTapped(object sender, TappedEventArgs e)
        => ComPortDropdownBackdrop.IsVisible = false;

    private void BuildComPortDropdown()
    {
        ComPortDropdownList.Children.Clear();

        var noneRow = BuildComPortOption("None", _selectedPortName == null);
        noneRow.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => SelectComPort(-1)) });
        ComPortDropdownList.Children.Add(noneRow);

        for (int i = 0; i < _comPorts.Count; i++)
        {
            var port = _comPorts[i];
            var isActive = _selectedPortName == port.PortName;
            var row = BuildComPortOption(port.DisplayName, isActive);
            var idx = i;
            row.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => SelectComPort(idx)) });
            ComPortDropdownList.Children.Add(row);
        }

        var refreshRow = new Border
        {
            BackgroundColor = Colors.Transparent,
            Stroke = Colors.Transparent,
            StrokeShape = new RoundRectangle { CornerRadius = 6 },
            Padding = new Thickness(16, 10),
            Margin = new Thickness(0, 4, 0, 0),
        };
        refreshRow.Content = new Label { Text = "↻ Refresh ports", FontSize = 11, TextColor = Color.FromArgb("#6b7280") };
        refreshRow.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(async () => { await LoadComPortsAsync(); BuildComPortDropdown(); })
        });
        ComPortDropdownList.Children.Add(refreshRow);
    }

    private static Border BuildComPortOption(string label, bool isActive)
    {
        var border = new Border
        {
            BackgroundColor = isActive ? Color.FromArgb("#f5f5ff") : Colors.Transparent,
            Stroke = Colors.Transparent,
            StrokeShape = new RoundRectangle { CornerRadius = 6 },
            Padding = new Thickness(16, 12),
        };
        var grid = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)] };
        grid.Add(new Label
        {
            Text = label,
            FontSize = 13,
            FontAttributes = isActive ? FontAttributes.Bold : FontAttributes.None,
            TextColor = isActive ? Color.FromArgb("#4338ca") : Color.FromArgb("#374151"),
        });
        if (isActive)
        {
            var check = new Label { Text = "✓", FontSize = 14, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#4338ca"), VerticalOptions = LayoutOptions.Center };
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
        ComPortBadgeLabel.Text = portIndex < 0 ? "None"
            : portIndex < _comPorts.Count ? _comPorts[portIndex].PortName : "None";
        _syncingPickers = true;
        OverlayComPortPicker.SelectedIndex = portIndex + 1;
        _syncingPickers = false;
    }

    private void OnSerialDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        try
        {
            var line = _serialPort?.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(line))
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    try { await HandleScanAsync(line); }
                    catch (Exception ex) { Logger.Log($"PackStation HandleScan error: {ex}"); }
                });
        }
        catch (TimeoutException) { }
        catch (Exception ex)
        {
            Logger.Log($"PackStation serial read: {ex.Message}");
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
            UpdateOverlayScannerStatus($"Scanner reconnected ({_selectedPortName})");
            Logger.Log($"PackStation: COM port {_selectedPortName} reconnected");
        }
        catch
        {
            try { _serialPort?.Dispose(); } catch { }
            _serialPort = null;
        }
    }

    private void StartComHeartbeatTimer()
    {
        _comHeartbeatTimer?.Stop();
        _comHeartbeatTimer = Dispatcher.CreateTimer();
        _comHeartbeatTimer.Interval = TimeSpan.FromSeconds(10);
        _comHeartbeatTimer.Tick += (_, _) =>
        {
            if (_serialPort is not { IsOpen: true })
            {
                if (_selectedPortName != null && _currentOperator != null) TryReconnectComPort();
            }
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
            Logger.Log($"PackStation GetFriendlyComPortsAsync WMI error: {ex.Message}");
        }

        return SerialPort.GetPortNames()
            .Where(p => p.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => int.TryParse(p[3..], out var n) ? n : 999)
            .Select(p => new ComPortEntry(p, p))
            .ToList();
    });

    // ── Keyboard: admin chord ─────────────────────────────────────────────────────
#if WINDOWS
    private void RegisterKeyboardHandler()
    {
        if (Application.Current?.Windows is { Count: > 0 } wins &&
            wins[0].Handler?.PlatformView is Microsoft.UI.Xaml.Window w)
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
        // Admin bypass chord: Ctrl+Shift+A+M (all held) -> log in as "admin".
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

        // Don't steal arrows while typing in any Entry / TextBox (mirrors Order Search).
        if (e.OriginalSource is Microsoft.UI.Xaml.Controls.TextBox) return;

        // Returns confirm overlay: 1-5 select a reason chip, Enter confirms (once a reason is
        // chosen), Esc cancels. Mirrors OrderSearchPage.Keyboard.cs's Returns-mode block.
        if (ReturnsConfirmOverlay.IsVisible)
        {
            if (e.Key >= Windows.System.VirtualKey.Number1 && e.Key <= Windows.System.VirtualKey.Number5)
            {
                var idx = (int)e.Key - (int)Windows.System.VirtualKey.Number1;
                if (idx < ReturnReasons.Length) SelectReasonChip(ReturnReasons[idx]);
                e.Handled = true;
                return;
            }
            if (e.Key == Windows.System.VirtualKey.Enter && ReturnsConfirmBtn.IsEnabled)
            {
                OnReturnConfirmClicked(null, EventArgs.Empty);
                e.Handled = true;
                return;
            }
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                HideReturnsConfirm();
                e.Handled = true;
                return;
            }
        }

        // ← newer · → older : step the read-only history carousel. Pure review — NavigateHistory
        // opens the cached parcel and never calls ShipAsync, so arrows can't ship or change QC.
        var isLeft = e.Key == Windows.System.VirtualKey.Left;
        var isRight = e.Key == Windows.System.VirtualKey.Right;
        if ((isLeft || isRight) && _currentOperator != null && _belt.Count > 0)
        {
            NavigateHistory(isLeft ? -1 : +1);
            e.Handled = true;
        }
    }
#endif
}
