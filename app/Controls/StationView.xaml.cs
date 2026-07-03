using app.Services;
using app.Workflows;
using CommunityToolkit.Maui.Alerts;
using CommunityToolkit.Maui.Core;
using CommunityToolkit.Maui.Views;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Ports;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
#if WINDOWS
using System.Management;
#endif

namespace app.Controls;

[SupportedOSPlatform("windows")]
public partial class StationView : ContentView, IDisposable
{
    private readonly int _stationId;

    // Camera state
    private List<CameraInfo> _availableCameras = new();
    private bool _cameraPreviewActive;

    // Recording state
    private string? _activeBarcode;
    private bool _isRecording;
    private CancellationTokenSource? _recordingCts;
    private Task? _recordingTask;
    private string? _pendingFilePath;
    private FileStream? _recordingFileStream;
    private WindowsVideoRecorder? _recorder;
    private bool _usingFallbackRecorder;

    // Diagnostics
    private DateTime _recordingStartedAt;
    private static readonly TimeSpan RecordingDurationWarnThreshold = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StopRecordingTimeout = TimeSpan.FromSeconds(10);

    // Serializes barcode events so rapid double-scans cannot interleave state
    private readonly SemaphoreSlim _barcodeLock = new(1, 1);

    // Serial barcode scanner
    private SerialPort? _serialPort;

    // COM port entries — use a named record to avoid WinRT generic-ABI issues with value tuples
    private record ComPortEntry(string PortName, string DisplayName);
    private List<ComPortEntry> _comPorts = [];

    // Editable station name (backing field so file-naming doesn't need to touch the UI)
    private string _stationName = string.Empty;

    // Full barcode of the logged-in operator — null when no operator active
    private string? _currentOperator;

    // Resolved first name from API — null until lookup completes or when no operator active
    private string? _currentOperatorFirstName;

    // Operator name for API calls — falls back to station name when no operator is logged in
    private string EffectiveOperator => _currentOperator ?? _stationName.Replace(' ', '-');

    // Video count for today (fetched from backend, then incremented in-memory)
    private int _videoCount;

    // Lock flags — prevent picker changes and skip re-population on Refresh
    private bool _cameraLocked;
    private bool _scannerLocked;

    // Controls panel visibility state (collapsed by default)
    private bool _controlsVisible;

    // Inactivity auto-logout timer
    private IDispatcherTimer? _inactivityTimer;

    // Prevents OnCameraSelected from firing while the picker is being populated
    private bool _loadingDevices;

    // ── Cross-station camera registry ─────────────────────────────────────────

    /// <summary>
    /// Tracks every station's current camera selection.
    /// Key = stationId, Value = (index into _availableCameras, station display name).
    /// cameraIndex == -1 means no camera selected.
    /// </summary>
    private static readonly ConcurrentDictionary<int, (int CameraIndex, string StationName)>
        _stationCameraMap = new();

    /// <summary>Fired whenever any station selects or deselects a camera.</summary>
    public static event Action? AnyCameraSelectionChanged;

    private void RegisterCameraSelection(int cameraIndex)
    {
        _stationCameraMap[_stationId] = (cameraIndex, _stationName);
        AnyCameraSelectionChanged?.Invoke();
    }

    private void OnAnyCameraSelectionChanged() =>
        TryDispatchUI(RefreshCameraPickerLabels);

    private void OnUploadProgressChanged() =>
        TryDispatchUI(UpdateUploadProgressBadge);

    private void UpdateUploadProgressBadge()
    {
        var items = VideoWorkflowManager.GetSnapshot();
        if (items.Count == 0)
        {
            UploadProgressBadge.IsVisible = false;
            return;
        }

        var uploading = items.Count(i => i.Status == "Uploading");
        var verifying = items.Count(i => i.Status == "Verifying");
        var retrying  = items.Count(i => i.Status.StartsWith("Retry"));

        string title;
        if (uploading > 0)
            title = $"Uploading {uploading} file{(uploading > 1 ? "s" : "")}";
        else if (verifying > 0)
            title = "Verifying...";
        else if (retrying > 0)
            title = "Retrying...";
        else
            title = "Processing...";

        var completed = items.Count(i => i.Status == "Completed");
        var detail = $"{completed}/{items.Count} done";

        UploadProgressTitle.Text  = title;
        UploadProgressDetail.Text = detail;
        UploadProgressBadge.IsVisible = true;
    }

    /// <summary>
    /// Rebuilds the camera picker display names from the already-loaded camera list —
    /// no device re-fetch. Called when another station changes its selection.
    /// </summary>
    private void RefreshCameraPickerLabels()
    {
        if (_availableCameras.Count == 0) return;
        var prev = CameraPicker.SelectedIndex;
        _loadingDevices = true;
        try
        {
            CameraPicker.ItemsSource = BuildCameraDisplayNames();
            if (prev >= 0 && prev < ((System.Collections.IList)CameraPicker.ItemsSource).Count)
                CameraPicker.SelectedIndex = prev;
        }
        finally { _loadingDevices = false; }
    }

    /// <summary>
    /// Builds camera picker items.
    /// Appends " #N" to cameras that share a name so users can distinguish them,
    /// and appends "  (in use – StationX)" for cameras held by another station.
    /// </summary>
    private List<string> BuildCameraDisplayNames()
    {
        // How many cameras share each name
        var nameCounts = _availableCameras
            .GroupBy(c => c.Name)
            .ToDictionary(g => g.Key, g => g.Count());

        // Indices in use by OTHER stations
        var inUseByOthers = _stationCameraMap
            .Where(kv => kv.Key != _stationId && kv.Value.CameraIndex >= 0)
            .ToDictionary(kv => kv.Value.CameraIndex, kv => kv.Value.StationName);

        var occurrence = new Dictionary<string, int>();
        var items = new List<string> { "(None)" };

        for (int i = 0; i < _availableCameras.Count; i++)
        {
            var cam = _availableCameras[i];
            string name;

            if (nameCounts[cam.Name] > 1)
            {
                occurrence[cam.Name] = occurrence.GetValueOrDefault(cam.Name) + 1;
                name = $"{cam.Name} #{occurrence[cam.Name]}";
            }
            else
            {
                name = cam.Name;
            }

            if (inUseByOthers.TryGetValue(i, out var usingStation))
                name += $"  (in use \u2013 {usingStation})";

            items.Add(name);
        }

        return items;
    }

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
        // Update station name in the registry so other stations' "in use" labels stay accurate
        if (_stationCameraMap.TryGetValue(_stationId, out var entry))
            _stationCameraMap[_stationId] = (entry.CameraIndex, _stationName);
        AnyCameraSelectionChanged?.Invoke();
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
        AnyCameraSelectionChanged += OnAnyCameraSelectionChanged;
        VideoWorkflowManager.ProgressChanged += OnUploadProgressChanged;
        // Load devices AFTER the view (and its CameraView handler) is fully in the visual tree
        Loaded += OnViewLoaded;
    }

    private void OnViewLoaded(object? sender, EventArgs e)
    {
        UpdateUploadProgressBadge();
        _ = LoadDevicesAsync();
        _ = LoadTodayVideoCountAsync();
    }

    // ── Device Discovery ────────────────────────────────────────────────────

    public async Task LoadDevicesAsync()
    {
        if (_loadingDevices) return;
        _loadingDevices = true;
        try
        {
            // Enumerate cameras — safe to call any time; returns empty list if handler not ready
            _availableCameras = (await CameraFeed.GetAvailableCameras(CancellationToken.None)).ToList();

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
                    // Preserve selected index (stable within a session even if names changed)
                    var prevIdx = CameraPicker.SelectedIndex;
                    var cameraItems = BuildCameraDisplayNames();
                    CameraPicker.ItemsSource = cameraItems;
                    if (prevIdx > 0 && prevIdx < cameraItems.Count)
                        CameraPicker.SelectedIndex = prevIdx;
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
            RegisterCameraSelection(-1);
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
            RegisterCameraSelection(cameraIdx);
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
        if (!await _barcodeLock.WaitAsync(0))
        {
            Logger.Log($"Station {_stationId}: Barcode {barcode} dropped — previous scan still processing");
            return;
        }
        try
        {
            Logger.Log($"Station {_stationId}: Barcode received: {barcode}");

            if (!AppSettings.StationIdReady.IsCompleted)
            {
                UpdateStatus("Connecting to server...");
                await AppSettings.StationIdReady;
                UpdateStatus("Ready");
            }

            // Operator badge detection — must run before dash guard
            if (AppSettings.TryParseOperatorBarcode(barcode) is { } badge)
            {
                bool loggingOut = _currentOperator == barcode;
                if (loggingOut)
                {
                    var logoutName = _currentOperatorFirstName ?? barcode;
                    _currentOperator = null;
                    _currentOperatorFirstName = null;
                    StopInactivityTimer();
                    StationWsClient.SendOperatorLogout();
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        UpdateOperatorUI(null);
                        UpdateStatus("Ready to Scan");
                        _ = Toast.Make("Logged out").Show();
                    });
                    Logger.Log($"Station {_stationId}: Operator logged out — {logoutName}");
                }
                else
                {
                    _currentOperator = barcode;
                    _currentOperatorFirstName = null;
                    StartInactivityTimer();
                    StationWsClient.SendOperatorLogin(barcode, SessionKind.Packing);
                    // Show full staff_code immediately, then update to first name once API resolves
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        UpdateOperatorUI(barcode);
                        UpdateStatus($"Welcome, {barcode}");
                        _ = Toast.Make($"Welcome, {barcode}").Show();
                    });
                    Logger.Log($"Station {_stationId}: Operator logged in — {barcode}");
                    _ = Task.Run(async () =>
                    {
                        var firstName = await ApiService.GetOperatorFirstNameAsync(barcode);
                        if (firstName is null || _currentOperator != barcode) return;
                        _currentOperatorFirstName = firstName;
                        MainThread.BeginInvokeOnMainThread(() => UpdateOperatorUI(firstName));
                    });
                }
                return;
            }

            // Normalize KEX QR codes: "KEXLM1000234185 1 1 DJD001" → "KEXLM1000234185"
            var raw = barcode;
            barcode = AppSettings.NormalizeTrackingNumber(barcode);
            if (barcode != raw)
                Logger.Log($"Station {_stationId}: KEX normalized: {raw} → {barcode}");

            if (barcode.Contains('-'))
            {
                Logger.Log($"Station {_stationId}: Barcode ignored (contains dash): {barcode}");
                return;
            }

            // Reset inactivity timer — operator is actively scanning
            if (_currentOperator is not null)
                StartInactivityTimer();

            // If recording task faulted, clean up state before processing next scan
            if (_activeBarcode != null && _recordingTask is { IsCompleted: true, IsFaulted: true })
            {
                Logger.Log($"Station {_stationId}: Cleaning up faulted recording for {_activeBarcode}");
                _activeBarcode = null;
                _isRecording = false;
                _recordingCts?.Cancel();
                _recordingCts?.Dispose();
                _recordingCts = null;
                _recordingTask = null;
                if (_recordingFileStream != null)
                {
                    await _recordingFileStream.DisposeAsync();
                    _recordingFileStream = null;
                }
                _pendingFilePath = null;
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    BarcodeBadge.IsVisible = false;
                    BarcodeLabel.Text = "";
                    RecBadge.IsVisible = false;
                    RecordingBorder.IsVisible = false;
                    UpdateCardBorder();
                    UpdateStatusFromDevices();
                });
            }

            var stationName = Environment.MachineName;
            var stationLabel = $"{stationName}-{_stationName.Replace(' ', '-')}";

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
                _ = ApiService.UpdatePackingStatusByScanAsync(barcode, "Packing",
                    packingStationId: AppSettings.ResolvedStationId);

                StationEvents.Emit(
                    workflowName: "Packing",
                    stepId: "tracking_scanned",
                    trigger: "barcode_scan",
                    trackingNumber: barcode,
                    fromState: "idle",
                    toState: "recording",
                    stationId: AppSettings.ResolvedStationId,
                    @operator: EffectiveOperator,
                    payload: new Dictionary<string, object?>
                    {
                        ["stationLabel"] = stationLabel,
                    });
            }
            else if (barcode == "Reset")
            {
                // Reset scan — stop and upload under the active barcode, logged as packing_reset
                _isRecording = false;
                UpdateCardBorder();
                BarcodeBadge.IsVisible = false;
                BarcodeLabel.Text = "";
                RecBadge.IsVisible = false;
                RecordingBorder.IsVisible = false;
                var recordingStartedAt = _recordingStartedAt;
                var filePath = await StopRecordingAsync();
                var resetBarcode = _activeBarcode;
                _activeBarcode = null;

                if (!string.IsNullOrEmpty(filePath))
                {
                    IncrementVideoCount();
                    UpdateStatusFromDevices();

                    _ = ApiService.UpdatePackingStatusByScanAsync(resetBarcode!, "Packed", EffectiveOperator,
                        packingStationId: AppSettings.ResolvedStationId);

                    var durationSeconds = (int)Math.Round((DateTime.UtcNow - recordingStartedAt).TotalSeconds);
                    long fileSizeBytes = 0;
                    try { fileSizeBytes = new FileInfo(filePath).Length; } catch { /* best-effort */ }

                    StationEvents.Emit(
                        workflowName: "Packing",
                        stepId: "packing_reset",
                        trigger: "barcode_scan",
                        trackingNumber: resetBarcode,
                        fromState: "recording",
                        toState: "uploading",
                        stationId: AppSettings.ResolvedStationId,
                            @operator: EffectiveOperator,
                        payload: new Dictionary<string, object?>
                        {
                            ["stationLabel"] = stationLabel,
                            ["packedBy"] = EffectiveOperator,
                            ["durationSeconds"] = durationSeconds,
                            ["videoFileSizeBytes"] = fileSizeBytes,
                        });

                    if (!IsValidMp4(filePath))
                    {
                        Logger.Log($"Station {_stationId}: recording invalid or empty — skipping upload ({filePath})");
                        UpdateStatusFromDevices();
                    }
                    else
                    {
                        var stationId = await AppSettings.EnsureStationIdAsync();
                        var result = await ApiService.CreateVideoResultAsync(
                            resetBarcode!, filePath, stationId, EffectiveOperator);

                        if (result.Kind == CreateVideoResultKind.Created)
                        {
                            StationEvents.Emit(
                                workflowName: "Packing",
                                stepId: "upload_started",
                                trigger: "barcode_scan",
                                trackingNumber: resetBarcode,
                                fromState: "recording",
                                toState: "uploading",
                                stationId: stationId,
                                @operator: EffectiveOperator,
                                payload: new Dictionary<string, object?>
                                {
                                    ["videoId"] = result.VideoId,
                                });
                            VideoWorkflowManager.Start(
                                result.VideoId, filePath, resetBarcode!,
                                EffectiveOperator, stationId);
                        }
                        else if (result.Kind == CreateVideoResultKind.NoPackingList)
                        {
                            Logger.Log($"Station {_stationId}: no packing list for '{resetBarcode}' — capturing as orphan via workflow");
                            await StartOrphanWorkflowAsync(filePath, resetBarcode!, stationId, recordingStartedAt);
                        }
                        else
                        {
                            Logger.Log($"Station {_stationId}: failed to create video record for reset, leaving on disk");
                        }
                    }
                }
                else
                {
                    UpdateStatus("Save failed — Ready to Scan");
                }
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
                var recordingStartedAt = _recordingStartedAt;
                var filePath = await StopRecordingAsync();
                var finishedBarcode = _activeBarcode;
                _activeBarcode = null;

                if (!string.IsNullOrEmpty(filePath))
                {
                    IncrementVideoCount();
                    UpdateStatusFromDevices();

                    // Update packing status to Packed
                    _ = ApiService.UpdatePackingStatusByScanAsync(finishedBarcode!, "Packed", EffectiveOperator,
                        packingStationId: AppSettings.ResolvedStationId);

                    var durationSeconds = (int)Math.Round((DateTime.UtcNow - recordingStartedAt).TotalSeconds);
                    long fileSizeBytes = 0;
                    try { fileSizeBytes = new FileInfo(filePath).Length; } catch { /* best-effort */ }

                    StationEvents.Emit(
                        workflowName: "Packing",
                        stepId: "packing_stopped",
                        trigger: "barcode_scan",
                        trackingNumber: finishedBarcode,
                        fromState: "recording",
                        toState: "uploading",
                        stationId: AppSettings.ResolvedStationId,
                            @operator: EffectiveOperator,
                        payload: new Dictionary<string, object?>
                        {
                            ["stationLabel"] = stationLabel,
                            ["packedBy"] = EffectiveOperator,
                            ["durationSeconds"] = durationSeconds,
                            ["videoFileSizeBytes"] = fileSizeBytes,
                        });

                    if (!IsValidMp4(filePath))
                    {
                        Logger.Log($"Station {_stationId}: recording invalid or empty — skipping upload ({filePath})");
                        UpdateStatusFromDevices();
                    }
                    else
                    {
                        var stationId = await AppSettings.EnsureStationIdAsync();
                        var result = await ApiService.CreateVideoResultAsync(
                            finishedBarcode!, filePath, stationId, EffectiveOperator);

                        if (result.Kind == CreateVideoResultKind.Created)
                        {
                            StationEvents.Emit(
                                workflowName: "Packing",
                                stepId: "upload_started",
                                trigger: "barcode_scan",
                                trackingNumber: finishedBarcode,
                                fromState: "recording",
                                toState: "uploading",
                                stationId: stationId,
                                @operator: EffectiveOperator,
                                payload: new Dictionary<string, object?>
                                {
                                    ["videoId"] = result.VideoId,
                                });
                            VideoWorkflowManager.Start(
                                result.VideoId, filePath, finishedBarcode!,
                                EffectiveOperator, stationId);
                        }
                        else if (result.Kind == CreateVideoResultKind.NoPackingList)
                        {
                            Logger.Log($"Station {_stationId}: no packing list for '{finishedBarcode}' — capturing as orphan via workflow");
                            await StartOrphanWorkflowAsync(filePath, finishedBarcode!, stationId, recordingStartedAt);
                        }
                        else
                        {
                            Logger.Log($"Station {_stationId}: failed to create video record, leaving on disk");
                        }
                    }
                }
                else
                {
                    UpdateStatus("Save failed — Ready to Scan");
                }
            }
            else
            {
                // Different barcode while recording — ignore but record the mismatch
                // so the dashboard can flag stations with high mismatch rates.
                Logger.Log($"Station {_stationId}: Barcode mismatch (active: {_activeBarcode}, got: {barcode})");
                StationEvents.Emit(
                    workflowName: "Packing",
                    stepId: "barcode_mismatch",
                    trigger: "barcode_scan",
                    trackingNumber: _activeBarcode,
                    fromState: "recording",
                    toState: "recording",
                    stationId: AppSettings.ResolvedStationId,
                    @operator: EffectiveOperator,
                    payload: new Dictionary<string, object?>
                    {
                        ["activeBarcode"] = _activeBarcode,
                        ["scanned"] = barcode,
                    });
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"Station {_stationId} HandleBarcode: {ex}");
        }
        finally
        {
            _barcodeLock.Release();
        }
    }

    // ── Recording ───────────────────────────────────────────────────────────

    private void StartRecording(string barcode)
    {
        try
        {
            _recordingStartedAt = DateTime.UtcNow;
            var memBefore = GC.GetTotalMemory(false);
            Logger.Log($"Station {_stationId}: [DIAG] Memory before start: {memBefore / 1_048_576.0:F1} MB");

            // Build the output path now so we can open the file before recording begins
            var prefix = SanitizeFileName(_stationName).Replace(' ', '-');
            var dir = Path.Combine(AppSettings.GetAvailableVideoFolder(), DateTime.Now.ToString("yyyy-MM-dd"), prefix);
            Directory.CreateDirectory(dir);
            _pendingFilePath = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd_HHmmss}_{Environment.MachineName}_{prefix}_{barcode}.mp4");

            var sw = Stopwatch.StartNew();
            _recordingCts = new CancellationTokenSource();
            _recorder ??= new WindowsVideoRecorder(CameraFeed, _stationId);
            if (_recorder.IsAvailable)
            {
                // Contract-correct LowLag path: 1080p profile fixed before Prepare, native
                // file sink, FinishAsync per clip. Works around the toolkit record path
                // that wedges Media Foundation on Win10 1909.
                _usingFallbackRecorder = false;
                _recordingTask = _recorder.StartAsync(_pendingFilePath, _recordingCts.Token);
            }
            else
            {
                Logger.Log($"Station {_stationId}: [WARN] WindowsVideoRecorder unavailable " +
                           "(handler detached or toolkit internals changed) — using toolkit record path");
                _usingFallbackRecorder = true;
                // Open a seekable FileStream — toolkit calls .AsRandomAccessStream() on it internally,
                // which requires CanSeek = true. Encoded MP4 bytes go straight to disk; no MemoryStream.
                _recordingFileStream = new FileStream(_pendingFilePath,
                    FileMode.Create, FileAccess.ReadWrite, FileShare.None,
                    bufferSize: 65536, FileOptions.Asynchronous);
                _recordingTask = CameraFeed.StartVideoRecording(_recordingFileStream, _recordingCts.Token);
            }
            _ = _recordingTask.ContinueWith(t =>
            {
                if (!_isRecording) return;
                var msg = t.Exception?.InnerException?.Message ?? "unknown error";
                Logger.Log($"Station {_stationId}: [ERROR] Recording task faulted mid-recording: {msg}");
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (!_isRecording) return;
                    _isRecording = false;
                    RecBadge.IsVisible = false;
                    RecordingBorder.IsVisible = false;
                    UpdateCardBorder();
                    UpdateStatus("Recording failed — scan again");
                });
            }, TaskContinuationOptions.OnlyOnFaulted);
            sw.Stop();

            Logger.Log($"Station {_stationId}: Recording started for barcode {barcode} → {_pendingFilePath} " +
                       $"(StartVideoRecording call: {sw.ElapsedMilliseconds} ms)");
        }
        catch (Exception ex)
        {
            Logger.Log($"Station {_stationId} StartRecording: {ex}");
        }
    }

    private async Task<string?> StopRecordingAsync()
    {
        try
        {
            if (_recordingTask == null) return null;

            var duration = DateTime.UtcNow - _recordingStartedAt;
            if (duration > RecordingDurationWarnThreshold)
                Logger.Log($"Station {_stationId}: [WARN] Long recording: " +
                           $"{duration.TotalMinutes:F1} min");

            Logger.Log($"Station {_stationId}: [DIAG] Memory before StopVideoRecording: " +
                       $"{GC.GetTotalMemory(false) / 1_048_576.0:F1} MB");

            await TryDispatchUIAsync(() => SavingOverlay.IsVisible = true);

            var swStop = Stopwatch.StartNew();

            // Give an in-flight start a moment to land so the stop doesn't no-op
            // (recorder start is fire-and-forget and Prepare can be slow on the wedging box).
            if (!_usingFallbackRecorder && _recordingTask is { IsCompleted: false })
                await Task.WhenAny(_recordingTask, Task.Delay(TimeSpan.FromSeconds(2)));

            // Phase 1: Stop encoding (wall-clock bounded — a wedged native call can
            // ignore its cancellation token, so CancellationTokenSource(timeout) alone
            // is not enough).
            using var stopCts = new CancellationTokenSource(StopRecordingTimeout);
            Task stopCall = _usingFallbackRecorder
                ? CameraFeed.StopVideoRecording(stopCts.Token)
                : _recorder!.StopAsync();
            using var stopDelayCts = new CancellationTokenSource();
            var stopWinner = await Task.WhenAny(stopCall, Task.Delay(StopRecordingTimeout, stopDelayCts.Token));
            if (stopWinner == stopCall)
            {
                stopDelayCts.Cancel();
                try
                {
                    await stopCall;
                }
                catch (OperationCanceledException)
                {
                    Logger.Log($"Station {_stationId}: [WARN] Stop cancelled — forcing cancel");
                    _recordingCts?.Cancel();
                }
                catch (Exception ex)
                {
                    Logger.Log($"Station {_stationId}: Stop threw: {ex.Message}");
                    _recordingCts?.Cancel();
                }
            }
            else
            {
                Logger.Log($"Station {_stationId}: [WARN] Stop exceeded " +
                           $"{StopRecordingTimeout.TotalSeconds}s wall clock — abandoning, forcing cancel");
                _recordingCts?.Cancel();
                // Observe the abandoned task's eventual fault so it can't raise UnobservedTaskException.
                _ = stopCall.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
            }

            // Phase 2: Wait for recording task to finish (bounded).
            if (_recordingTask is { IsCompleted: false })
            {
                _recordingCts?.Cancel();
                using var delayCts = new CancellationTokenSource();
                var completed = await Task.WhenAny(_recordingTask, Task.Delay(StopRecordingTimeout, delayCts.Token));
                if (completed == _recordingTask)
                    delayCts.Cancel();
                if (completed != _recordingTask)
                    Logger.Log($"Station {_stationId}: [WARN] Recording task did not complete within {StopRecordingTimeout.TotalSeconds}s — abandoning");
                else if (_recordingTask.IsFaulted)
                {
                    var ex = _recordingTask.Exception;
                    Logger.Log($"Station {_stationId}: Recording task faulted: {ex?.InnerException?.Message}");
                }
            }

            // A start that was still preparing when Phase 1 ran may have landed during
            // the Phase-2 wait — reap it so the camera isn't left recording invisibly.
            if (!_usingFallbackRecorder && _recorder is { HasActiveRecording: true })
            {
                Logger.Log($"Station {_stationId}: [WARN] Late-started recording detected after stop — stopping again");
                var lateStop = _recorder.StopAsync();
                using var lateDelayCts = new CancellationTokenSource();
                if (await Task.WhenAny(lateStop, Task.Delay(StopRecordingTimeout, lateDelayCts.Token)) == lateStop)
                {
                    lateDelayCts.Cancel();
                    try { await lateStop; }
                    catch (Exception ex) { Logger.Log($"Station {_stationId}: Late stop threw: {ex.Message}"); }
                }
                else
                {
                    Logger.Log($"Station {_stationId}: [WARN] Late stop exceeded {StopRecordingTimeout.TotalSeconds}s — abandoning");
                    _ = lateStop.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
                }
            }

            // Phase 3: Flush and close the FileStream.
            if (_recordingFileStream != null)
            {
                await _recordingFileStream.FlushAsync();
                await _recordingFileStream.DisposeAsync();
                _recordingFileStream = null;
            }

            swStop.Stop();

            var filePath = _pendingFilePath;
            _pendingFilePath = null;

            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                throw new Exception("Recording file missing after stop");

            var fileMb = new FileInfo(filePath).Length / 1_048_576.0;
            Logger.Log($"Station {_stationId}: [DIAG] StopVideoRecording: {swStop.ElapsedMilliseconds} ms | " +
                       $"File: {fileMb:F1} MB | Memory: {GC.GetTotalMemory(false) / 1_048_576.0:F1} MB");
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
            if (_recordingFileStream != null)
            {
                await _recordingFileStream.DisposeAsync();
                _recordingFileStream = null;
            }
            _recordingCts?.Cancel();
            _recordingCts?.Dispose();
            _recordingCts = null;
            _recordingTask = null;
            _pendingFilePath = null;

            Logger.Log($"Station {_stationId}: [DIAG] Memory after recording: " +
                       $"{GC.GetTotalMemory(false) / 1_048_576.0:F1} MB");

            await TryDispatchUIAsync(() => SavingOverlay.IsVisible = false);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Registers an order-less recording as an orphan_videos row, then runs the
    /// shared VideoWorkflowRunner against warehouse-raw + the /orphan-videos
    /// status route (isOrphan: true). Best-effort: a failed register leaves the
    /// file on disk for the next recovery pass.
    /// </summary>
    private async Task StartOrphanWorkflowAsync(
        string filePath, string scannedText, int? stationId, DateTime recordedAtUtc)
    {
        var rawBucket = AppSettings.MinioRawBucket?.Trim();
        if (string.IsNullOrWhiteSpace(rawBucket))
        {
            Logger.Log($"Station {_stationId}: raw bucket not configured — leaving orphan on disk");
            return;
        }

        var objectKey = OrphanCapture.BuildRawObjectKey(filePath, DateTime.Now);
        long fileSize = 0;
        try { fileSize = new FileInfo(filePath).Length; } catch { /* best-effort */ }

        var orphanId = await ApiService.CreateOrphanVideoAsync(
            objectKey, rawBucket, scannedText, stationId, EffectiveOperator,
            filePath, Path.GetFileName(filePath), recordedAtUtc, fileSize);

        if (orphanId > 0)
            VideoWorkflowManager.Start(
                orphanId, filePath, scannedText, EffectiveOperator, stationId, isOrphan: true);
        else
            Logger.Log($"Station {_stationId}: orphan register failed for '{scannedText}' — leaving on disk");
    }

    private void UpdateStatus(string text) =>
        TryDispatchUI(() => StatusLabel.Text = text);

    private void TryDispatchUI(Action action)
    {
        if (App.IsWindowActive)
            MainThread.BeginInvokeOnMainThread(action);
    }

    private async Task TryDispatchUIAsync(Action action)
    {
        if (App.IsWindowActive)
            await MainThread.InvokeOnMainThreadAsync(action);
    }

    /// <summary>Sets StatusLabel based on which devices are currently connected.</summary>
    private void UpdateStatusFromDevices()
    {
        bool hasCamera = _cameraPreviewActive;
        bool hasScanner = _serialPort?.IsOpen == true;
        string portName = _serialPort?.PortName ?? "";

        string status = (hasCamera, hasScanner) switch
        {
            (false, false) => "Waiting for camera and scanner",
            (true, false) => "Camera ready · Waiting for scanner",
            (false, true) => $"Waiting for camera · Scanner ready ({portName})",
            (true, true) => $"Camera ready · Scanner ready ({portName})",
        };
        UpdateStatus(status);
    }

    private void IncrementVideoCount()
    {
        var newCount = Interlocked.Increment(ref _videoCount);
        TryDispatchUI(() => VideoCountLabel.Text = newCount.ToString());
    }

    /// <summary>
    /// Fetches today's video count from the backend API for this station
    /// and sets <see cref="_videoCount"/>. Called at startup and on camera attach;
    /// afterwards the counter is incremented in-memory each time a recording completes.
    /// </summary>
    public async Task LoadTodayVideoCountAsync()
    {
        try
        {
            var stationId = AppSettings.ResolvedStationId;
            var count = stationId is not null
                ? await ApiService.GetTodayVideoCountAsync(stationId.Value)
                : 0;
            Interlocked.Exchange(ref _videoCount, count);

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                VideoCountLabel.Text = _videoCount.ToString();
                VideoCountBadge.IsVisible = _cameraPreviewActive;
            });
        }
        catch (Exception ex)
        {
            Logger.Log($"Station {_stationId} LoadTodayVideoCountAsync: {ex.Message}");
        }
    }

    /// <summary>
    /// Enumerates available serial ports with friendly display names.
    /// On Windows: queries WMI (Win32_PnPEntity) for USB-to-serial adapters with full device names.
    /// On macOS: lists /dev/tty.* and /dev/cu.* USB serial devices from IOKit via SerialPort.GetPortNames().
    /// </summary>
    private static Task<List<ComPortEntry>> GetFriendlyComPortsAsync() => Task.Run(() =>
    {
#if WINDOWS
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
                var label = name[..m.Index].Trim().TrimEnd(',', '-', ' ');
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
#elif MACCATALYST
        // On macOS, USB serial adapters appear as /dev/tty.* (call-in) or /dev/cu.* (call-out).
        // We prefer /dev/cu.* for outgoing connections (more reliable for USB scanners).
        var ports = SerialPort.GetPortNames();
        Logger.Log($"GetFriendlyComPortsAsync: {ports.Length} port(s) on macOS");

        return ports
            .Where(p => p.StartsWith("/dev/cu.", StringComparison.Ordinal)
                     || p.StartsWith("/dev/tty.", StringComparison.Ordinal))
            .OrderBy(p => p)
            .Select(p =>
            {
                var label = p.StartsWith("/dev/cu.", StringComparison.Ordinal)
                    ? p["/dev/cu.".Length..]
                    : p["/dev/tty.".Length..];
                return new ComPortEntry(p, $"{label} — {p}");
            })
            .ToList();
#else
        return SerialPort.GetPortNames()
            .OrderBy(p => p)
            .Select(p => new ComPortEntry(p, p))
            .ToList();
#endif
    });

    /// <summary>Replaces characters that are illegal in Windows file names with underscores.</summary>
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c)).Trim();
        return string.IsNullOrEmpty(safe) ? "Station" : safe;
    }

    /// <summary>
    /// Validates that a recorded file is a non-empty mp4.
    /// Checks: exists, ≥1 KB (rules out silent-failure empty files),
    /// and the 'ftyp' ISO Base Media box marker at bytes 4–7.
    /// </summary>
    private static bool IsValidMp4(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return false;
        if (new FileInfo(filePath).Length < 1024) return false;
        try
        {
            Span<byte> header = stackalloc byte[8];
            using var fs = File.OpenRead(filePath);
            if (fs.Read(header) < 8) return false;
            // bytes 4–7 == 'ftyp' (0x66 0x74 0x79 0x70)
            return header[4] == 0x66 && header[5] == 0x74 &&
                   header[6] == 0x79 && header[7] == 0x70;
        }
        catch { return false; }
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

    // ── Operator UI ─────────────────────────────────────────────────────────

    private void UpdateOperatorUI(string? displayName)
    {
        if (displayName is null)
        {
            OperatorDot.Color = Color.FromArgb("#9ca3af");
            OperatorFooterLabel.Text = "No Operator";
            OperatorFooterLabel.TextColor = Color.FromArgb("#9ca3af");
            OperatorVideoLabel.IsVisible = false;
        }
        else
        {
            OperatorDot.Color = Color.FromArgb("#16a34a");
            OperatorFooterLabel.Text = displayName;
            OperatorFooterLabel.TextColor = Color.FromArgb("#15803d");
            OperatorVideoLabel.Text = displayName;
            OperatorVideoLabel.IsVisible = true;
        }
    }

    private void StartInactivityTimer()
    {
        StopInactivityTimer();
        var minutes = AppSettings.PackingInactivityMinutes;
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
        StationWsClient.SendOperatorLogout();
        UpdateOperatorUI(null);
        UpdateStatus("Ready to Scan");
        if (displayName is not null)
            _ = Toast.Make($"Session ended — {displayName}").Show();
        Logger.Log($"Station {_stationId}: Operator logged out (inactivity)");
    }

    // ── Cleanup ─────────────────────────────────────────────────────────────

    public void Dispose()
    {
        VideoWorkflowManager.ProgressChanged -= OnUploadProgressChanged;
        AnyCameraSelectionChanged -= OnAnyCameraSelectionChanged;
        _stationCameraMap.TryRemove(_stationId, out _);
        AnyCameraSelectionChanged?.Invoke(); // release "in use" label on other stations

        CloseSerialPort();
        if (_cameraPreviewActive)
        {
            try { CameraFeed.StopCameraPreview(); } catch { }
            _cameraPreviewActive = false;
        }
        StopInactivityTimer();

        // Best-effort: release the recorder's native sink so the MP4 gets finalized and
        // the camera isn't left pinned (the StorageFile sink is independent of
        // _recordingFileStream, so disposing the stream below doesn't cover it).
        _ = _recorder?.StopAsync().ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);

        // Cancel recording first — gives the recording task a chance to exit
        // before we yank the FileStream from under it.
        _recordingCts?.Cancel();

        // Best-effort wait for recording task to notice cancellation.
        // Can't await in Dispose, so use synchronous Wait with timeout.
        if (_recordingTask is { IsCompleted: false })
        {
            try { _recordingTask.Wait(TimeSpan.FromSeconds(2)); }
            catch { /* task may fault — that's fine during shutdown */ }
        }
        _recordingTask = null;

        _recordingCts?.Dispose();
        _recordingFileStream?.Dispose();
        _recordingFileStream = null;
    }
}
