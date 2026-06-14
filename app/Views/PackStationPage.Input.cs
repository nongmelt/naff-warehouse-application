using app.Services;
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
    private long _lastSerialDataTicks;

    // ── COM port management ──────────────────────────────────────────────────────

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
            OverlayComPortPicker.Items.Add(p.DisplayName);
        OverlayComPortPicker.SelectedIndex = selIdx;
        _syncingPickers = false;
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
            return;
        }

        var portIdx = idx - 1;
        if (portIdx >= _comPorts.Count) return;

        var portName = _comPorts[portIdx].PortName;
        _selectedPortName = portName;
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

    private void OnSerialDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        Interlocked.Exchange(ref _lastSerialDataTicks, DateTime.UtcNow.Ticks);
        try
        {
            var line = _serialPort?.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(line))
                MainThread.BeginInvokeOnMainThread(async () => await HandleScanAsync(line));
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
        Interlocked.Exchange(ref _lastSerialDataTicks, DateTime.UtcNow.Ticks);
        _comHeartbeatTimer = Dispatcher.CreateTimer();
        _comHeartbeatTimer.Interval = TimeSpan.FromSeconds(10);
        _comHeartbeatTimer.Tick += (_, _) =>
        {
            if (_serialPort is not { IsOpen: true })
            {
                if (_selectedPortName != null) TryReconnectComPort();
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
        }
    }
#endif
}
