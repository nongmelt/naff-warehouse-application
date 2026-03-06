using app.Services;
using CommunityToolkit.Maui.Core;
using CommunityToolkit.Maui.Views;
using System.IO.Ports;
using System.Runtime.Versioning;
using System.Management;
using System.Text.RegularExpressions;

namespace app.Controls;

[SupportedOSPlatform("windows")]
public partial class StationView : ContentView, IDisposable
{
    private readonly int _stationId;
    private readonly WebhookService _webhookService = new();

    // Camera state
    private List<CameraInfo> _availableCameras = new();
    private bool _cameraPreviewActive;

    // Recording state
    private string? _activeBarcode;
    private bool _isRecording;
    private CancellationTokenSource? _recordingCts;
    private Task? _recordingTask;
    private Stream? _recordedStream;

    // Serial barcode scanner
    private SerialPort? _serialPort;

    // COM port entries — use a named record to avoid WinRT generic-ABI issues with value tuples
    private record ComPortEntry(string PortName, string DisplayName);
    private List<ComPortEntry> _comPorts = [];

    // Editable station name (backing field so file-naming doesn't need to touch the UI)
    private string _stationName = string.Empty;

    // Video count for today (initialised from disk, then incremented in-memory)
    private int _videoCount;

    // Lock flags — prevent picker changes and skip re-population on Refresh
    private bool _cameraLocked;
    private bool _scannerLocked;

    // Controls panel visibility state (collapsed by default)
    private bool _controlsVisible;

    // Prevents OnCameraSelected from firing while the picker is being populated
    private bool _loadingDevices;

    // ── Selection ─────────────────────────────────────────────────────────────

    /// <summary>Fired when the user taps the camera area to activate this station.</summary>
    public event EventHandler? StationSelected;

    private bool _isSelected;

    /// <summary>Highlights the card border when true; resets it when false.</summary>
    public bool IsSelected
    {
        set { _isSelected = value; UpdateCardBorder(); }
    }

    /// <summary>
    /// Red when recording; green when both camera and scanner are live; blue when selected; gray otherwise.
    /// </summary>
    private void UpdateCardBorder()
    {
        if (_isRecording)
        {
            CardBorder.Stroke = new SolidColorBrush(Color.FromArgb("#ef4444"));
            CardBorder.StrokeThickness = 2.5;
        }
        else if (_cameraPreviewActive && (_serialPort?.IsOpen == true))
        {
            CardBorder.Stroke = new SolidColorBrush(Color.FromArgb("#22c55e"));
            CardBorder.StrokeThickness = 2.5;
        }
        else if (_isSelected)
        {
            CardBorder.Stroke = new SolidColorBrush(Color.FromArgb("#3b82f6"));
            CardBorder.StrokeThickness = 2.5;
        }
        else
        {
            CardBorder.Stroke = new SolidColorBrush(Color.FromArgb("#d1d5db"));
            CardBorder.StrokeThickness = 1;
        }
    }

    private void OnCameraAreaTapped(object sender, TappedEventArgs e) =>
        StationSelected?.Invoke(this, EventArgs.Empty);

    // ── Station name inline editing ──────────────────────────────────────────

    private void OnEditNameClicked(object sender, EventArgs e)
    {
        StationNameLabel.IsVisible = false;
        EditNameButton.IsVisible = false;
        StationNameEntry.Text = _stationName;
        StationNameEntry.IsVisible = true;
        StationNameEntry.Focus();
    }

    private void OnNameEntryCompleted(object sender, EventArgs e) => CommitNameEdit();
    private void OnNameEntryUnfocused(object sender, FocusEventArgs e) => CommitNameEdit();

    private void CommitNameEdit()
    {
        if (!StationNameEntry.IsVisible) return; // already committed
        var text = StationNameEntry.Text?.Trim();
        if (!string.IsNullOrEmpty(text)) _stationName = text;
        StationNameLabel.Text = _stationName;
        StationNameEntry.IsVisible = false;
        StationNameLabel.IsVisible = true;
        EditNameButton.IsVisible = true;
    }

    // ── Controls panel toggle ────────────────────────────────────────────────

    private void OnToggleControls(object sender, EventArgs e)
    {
        _controlsVisible = !_controlsVisible;
        ControlsPanel.IsVisible = _controlsVisible;
        ToggleButton.Text = _controlsVisible ? "▲" : "▼";
    }

    // ── Picker lock toggles ──────────────────────────────────────────────────

    private void OnCameraLockClicked(object sender, EventArgs e)
    {
        _cameraLocked = !_cameraLocked;
        CameraPicker.IsEnabled = !_cameraLocked;
        CameraLockButton.Text = _cameraLocked ? "🔒" : "🔓";
        CameraLockButton.TextColor = _cameraLocked
            ? Color.FromArgb("#3b82f6")
            : Color.FromArgb("#9ca3af");
        Logger.Log($"Station {_stationId}: Camera {(_cameraLocked ? "locked" : "unlocked")}");
    }

    private void OnScannerLockClicked(object sender, EventArgs e)
    {
        _scannerLocked = !_scannerLocked;
        ComPortPicker.IsEnabled = !_scannerLocked;
        ScannerLockButton.Text = _scannerLocked ? "🔒" : "🔓";
        ScannerLockButton.TextColor = _scannerLocked
            ? Color.FromArgb("#3b82f6")
            : Color.FromArgb("#9ca3af");
        Logger.Log($"Station {_stationId}: Scanner {(_scannerLocked ? "locked" : "unlocked")}");
    }

    public StationView(int stationId)
    {
        _stationId = stationId;
        InitializeComponent();
        _stationName = $"Station {stationId}";
        StationNameLabel.Text = _stationName;
        // Load devices AFTER the view (and its CameraView handler) is fully in the visual tree
        Loaded += OnViewLoaded;
    }

    private void OnViewLoaded(object? sender, EventArgs e)
    {
        _ = LoadDevicesAsync();
        _ = LoadTodayVideoCountAsync();
    }

    // ── Device Discovery ────────────────────────────────────────────────────

    public async Task LoadDevicesAsync()
    {
        _loadingDevices = true;
        try
        {
            // Enumerate cameras — safe to call any time; returns empty list if handler not ready
            _availableCameras = (await CameraFeed.GetAvailableCameras(CancellationToken.None)).ToList();
            // Index 0 is always "(None)"; real cameras start at index 1
            var cameraItems = new List<string> { "(None)" };
            cameraItems.AddRange(_availableCameras.Select(c => c.Name));

            // Enumerate COM ports with friendly device names
            _comPorts = await GetFriendlyComPortsAsync();
            // Index 0 is always "(None)"; real ports start at index 1
            var portItems = new List<string> { "(None)" };
            portItems.AddRange(_comPorts.Select(p => p.DisplayName));

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                // Camera — skip re-population when locked to preserve the user's fixed selection
                if (!_cameraLocked)
                {
                    var prevCamera = CameraPicker.SelectedItem as string;
                    CameraPicker.ItemsSource = cameraItems;
                    if (prevCamera != null && prevCamera != "(None)" && cameraItems.Contains(prevCamera))
                        CameraPicker.SelectedItem = prevCamera;
                }

                // Scanner — same: restore by port name, or skip entirely when locked
                if (!_scannerLocked)
                {
                    var prevPortName = _serialPort?.PortName;
                    ComPortPicker.ItemsSource = portItems;
                    if (prevPortName != null)
                    {
                        var idx = _comPorts.FindIndex(p => p.PortName == prevPortName);
                        if (idx >= 0) ComPortPicker.SelectedIndex = idx + 1;
                    }
                }
            });

            Logger.Log($"Station {_stationId}: {_availableCameras.Count} camera(s), {_comPorts.Count} COM port(s)");
        }
        catch (Exception ex)
        {
            Logger.Log($"Station {_stationId} LoadDevicesAsync: {ex}");
        }
        finally
        {
            _loadingDevices = false;
        }
    }

    // ── Camera ──────────────────────────────────────────────────────────────

    private async void OnCameraSelected(object sender, EventArgs e)
    {
        if (_loadingDevices) return;
        var idx = CameraPicker.SelectedIndex;
        if (idx < 0) return;

        // Stop any running preview first
        if (_cameraPreviewActive)
        {
            CameraFeed.StopCameraPreview();
            CameraFeed.IsVisible = false;
            NoCameraPlaceholder.IsVisible = true;
            _cameraPreviewActive = false;
            UpdateCardBorder();
        }

        // Index 0 = "(None)" — detach and done
        if (idx == 0)
        {
            Logger.Log($"Station {_stationId}: Camera detached");
            VideoCountBadge.IsVisible = false;
            UpdateStatusFromDevices();
            return;
        }

        // Real camera: picker index 1 → _availableCameras[0], etc.
        var cameraIdx = idx - 1;
        if (cameraIdx >= _availableCameras.Count) return;

        var camera = _availableCameras[cameraIdx];
        try
        {
            CameraFeed.SelectedCamera = camera;
            CameraFeed.IsVisible = true;
            NoCameraPlaceholder.IsVisible = false;
            await CameraFeed.StartCameraPreview(CancellationToken.None);
            _cameraPreviewActive = true;
            UpdateCardBorder();
            UpdateStatusFromDevices();
            _ = LoadTodayVideoCountAsync();
            Logger.Log($"Station {_stationId}: Camera started ({camera.Name})");
        }
        catch (Exception ex)
        {
            Logger.Log($"Station {_stationId} camera: {ex}");
            UpdateStatus($"Camera error: {ex.Message}");
            CameraFeed.IsVisible = false;
            NoCameraPlaceholder.IsVisible = true;
            _cameraPreviewActive = false;
            UpdateCardBorder();
        }
    }

    // ── Barcode Scanner (COM Port) ──────────────────────────────────────────

    private void OnComPortSelected(object sender, EventArgs e)
    {
        var idx = ComPortPicker.SelectedIndex;
        if (idx < 0) return;

        // Index 0 = "(None)" — close any open port and done
        if (idx == 0)
        {
            CloseSerialPort();
            Logger.Log($"Station {_stationId}: Scanner detached");
            UpdateStatusFromDevices();
            return;
        }

        // Real port: picker index 1 → _comPorts[0], etc.
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
            UpdateCardBorder();
            UpdateStatusFromDevices();
            Logger.Log($"Station {_stationId}: Serial port {portName} opened");
        }
        catch (Exception ex)
        {
            Logger.Log($"Station {_stationId} serial port: {ex}");
            UpdateStatus($"COM error: {ex.Message}");
        }
    }

    private void OnSerialDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        try
        {
            var line = _serialPort?.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(line))
                MainThread.BeginInvokeOnMainThread(() => HandleBarcode(line));
        }
        catch (TimeoutException) { }
        catch (Exception ex)
        {
            Logger.Log($"Station {_stationId} serial read: {ex.Message}");
        }
    }

    // ── Barcode State Machine ───────────────────────────────────────────────

    private async void HandleBarcode(string barcode)
    {
        try
        {
            Logger.Log($"Station {_stationId}: Barcode received: {barcode}");

            if (_activeBarcode == null)
            {
                // First scan → start recording
                if (!_cameraPreviewActive)
                {
                    UpdateStatus("Select a camera first");
                    return;
                }

                _activeBarcode = barcode;
                _isRecording = true;
                UpdateCardBorder();
                BarcodeLabel.Text = barcode;
                BarcodeBadge.IsVisible = true;
                RecBadge.IsVisible = true;
                RecordingBorder.IsVisible = true;
                StartRecording(barcode);
                UpdateStatus("🔴 RECORDING");
            }
            else if (_activeBarcode == barcode)
            {
                // Second matching scan → stop recording
                _isRecording = false;
                UpdateCardBorder();
                BarcodeBadge.IsVisible = false;
                BarcodeLabel.Text = "";
                RecBadge.IsVisible = false;
                RecordingBorder.IsVisible = false;
                var filePath = await StopRecordingAsync();
                var finishedBarcode = _activeBarcode;
                _activeBarcode = null;

                if (!string.IsNullOrEmpty(filePath))
                {
                    UpdateStatus("Sending webhook…");
                    await _webhookService.SendAsync(finishedBarcode, filePath);
                    _videoCount++;
                    MainThread.BeginInvokeOnMainThread(() => VideoCountLabel.Text = _videoCount.ToString());
                    UpdateStatusFromDevices();
                }
                else
                {
                    UpdateStatus("Save failed — Ready to Scan");
                }
            }
            else
            {
                // Different barcode while recording — ignore
                Logger.Log($"Station {_stationId}: Barcode mismatch (active: {_activeBarcode}, got: {barcode})");
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"Station {_stationId} HandleBarcode: {ex}");
        }
    }

    // ── Recording ───────────────────────────────────────────────────────────

    private void StartRecording(string barcode)
    {
        try
        {
            _recordingCts = new CancellationTokenSource();
            _recordingTask = CameraFeed.StartVideoRecording(_recordingCts.Token);
            Logger.Log($"Station {_stationId}: Recording started for barcode {barcode}");
        }
        catch (Exception ex)
        {
            Logger.Log($"Station {_stationId} StartRecording: {ex}");
        }
    }

    private async Task<string?> StopRecordingAsync()
    {
        // Capture the editable station name (backing field — no UI access needed).
        var stationName = SanitizeFileName(_stationName);

        try
        {
            if (_recordingTask == null) return null;

            _recordedStream = await CameraFeed.StopVideoRecording(CancellationToken.None);
            await _recordingTask;

            if (_recordedStream == null || _recordedStream.Length == 0)
                throw new Exception("Recorded stream is empty");

            var dir = Path.Combine(AppSettings.VideoFolder, DateTime.Now.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(dir);

            // Format: {station_name}_{barcode}_{date}_{time}.mp4  (spaces in name → dash)
            var prefix   = stationName.Replace(' ', '-');
            var filePath = Path.Combine(dir, $"{prefix}_{_activeBarcode}_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
            _recordedStream.Position = 0;
            await using var fs = File.Create(filePath);
            await _recordedStream.CopyToAsync(fs);

            Logger.Log($"Station {_stationId}: Video saved to {filePath}");
            return filePath;
        }
        catch (Exception ex)
        {
            Logger.Log($"Station {_stationId} StopRecording: {ex}");
            return null;
        }
        finally
        {
            _recordedStream?.Dispose();
            _recordedStream = null;
            _recordingCts?.Dispose();
            _recordingCts = null;
            _recordingTask = null;
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private void UpdateStatus(string text) =>
        MainThread.BeginInvokeOnMainThread(() => StatusLabel.Text = text);

    /// <summary>Sets StatusLabel based on which devices are currently connected.</summary>
    private void UpdateStatusFromDevices()
    {
        bool hasCamera  = _cameraPreviewActive;
        bool hasScanner = _serialPort?.IsOpen == true;
        string portName = _serialPort?.PortName ?? "";

        string status = (hasCamera, hasScanner) switch
        {
            (false, false) => "Waiting for camera and scanner",
            (true,  false) => "Camera ready · Waiting for scanner",
            (false, true)  => $"Waiting for camera · Scanner ready ({portName})",
            (true,  true)  => $"Camera ready · Scanner ready ({portName})",
        };
        UpdateStatus(status);
    }

    /// <summary>
    /// Scans today's folder for existing recordings matching this station's name prefix
    /// and sets <see cref="_videoCount"/>. Called once at startup; afterwards the counter
    /// is incremented in-memory each time the app saves a new file.
    /// </summary>
    private async Task LoadTodayVideoCountAsync()
    {
        try
        {
            var dir    = Path.Combine(AppSettings.VideoFolder, DateTime.Now.ToString("yyyy-MM-dd"));
            var prefix = SanitizeFileName(_stationName).Replace(' ', '-');
            _videoCount = Directory.Exists(dir)
                ? Directory.GetFiles(dir, $"{prefix}_*.mp4").Length
                : 0;

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                VideoCountLabel.Text      = _videoCount.ToString();
                VideoCountBadge.IsVisible = _cameraPreviewActive;
            });
        }
        catch (Exception ex)
        {
            Logger.Log($"Station {_stationId} LoadTodayVideoCountAsync: {ex.Message}");
        }
    }

    /// <summary>
    /// Queries Win32_PnPEntity (WMI / Device Manager) for every Ports-class device.
    /// This covers all USB-to-serial adapters (CH340, CP2102, PL2303, FTDI, etc.)
    /// whether they are plugged in directly or through a USB hub, because WMI reads
    /// the full Device Manager tree — not just WinRT-compatible interfaces.
    /// Falls back to plain SerialPort.GetPortNames() if WMI is unavailable.
    /// </summary>
    private static Task<List<ComPortEntry>> GetFriendlyComPortsAsync() => Task.Run(() =>
    {
        var result = new List<ComPortEntry>();
        try
        {
            // PNPClass = 'Ports' covers COM ports (and LPT ports); we filter to COM only.
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, Status FROM Win32_PnPEntity WHERE PNPClass = 'Ports'");

            foreach (ManagementObject obj in searcher.Get().Cast<ManagementObject>())
            {
                var name = obj["Name"]?.ToString();
                if (name == null) continue;

                // Device Manager always formats the name as "Friendly Name (COMx)"
                var m = Regex.Match(name, @"\(COM(\d+)\)", RegexOptions.IgnoreCase);
                if (!m.Success) continue;

                var portName = $"COM{m.Groups[1].Value}";
                var label    = name[..m.Index].Trim().TrimEnd(',', '-', ' ');
                if (string.IsNullOrWhiteSpace(label)) label = "Serial Device";

                result.Add(new ComPortEntry(portName, $"{label} — {portName}"));
            }

            result = [.. result.OrderBy(x => int.TryParse(x.PortName[3..], out var n) ? n : 999)];
            Logger.Log($"GetFriendlyComPortsAsync: {result.Count} port(s) via WMI");

            if (result.Count > 0)
                return result;
        }
        catch (Exception ex)
        {
            Logger.Log($"GetFriendlyComPortsAsync WMI error: {ex.Message}");
        }

        // Fallback — plain port names when WMI is unavailable
        return SerialPort.GetPortNames()
            .Where(p => p.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => int.TryParse(p[3..], out var n) ? n : 999)
            .Select(p => new ComPortEntry(p, p))
            .ToList();
    });

    /// <summary>Replaces characters that are illegal in Windows file names with underscores.</summary>
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c)).Trim();
        return string.IsNullOrEmpty(safe) ? "Station" : safe;
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
            UpdateCardBorder();
        }
    }

    // ── Cleanup ─────────────────────────────────────────────────────────────

    public void Dispose()
    {
        CloseSerialPort();
        if (_cameraPreviewActive)
        {
            try { CameraFeed.StopCameraPreview(); } catch { }
            _cameraPreviewActive = false;
        }
        _recordingCts?.Cancel();
        _recordingCts?.Dispose();
        _recordedStream?.Dispose();
    }
}
