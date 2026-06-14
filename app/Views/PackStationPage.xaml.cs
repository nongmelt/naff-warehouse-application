using app.Models;
using app.Services;
using app.Workflows;
using CommunityToolkit.Maui.Alerts;
using System.Runtime.Versioning;

namespace app.Views;

[SupportedOSPlatform("windows")]
public partial class PackStationPage : ContentPage
{
    // Station identifier — computer name, resolved once.
    private static readonly string StationName = Environment.MachineName;

    // Full badge of the logged-in operator — null when nobody is logged in.
    // This raw staff-code string is what gets written as packed_by.
    private string? _currentOperator;
    private string? _currentOperatorFirstName; // resolved display name (UI only)

    private IDispatcherTimer? _inactivityTimer;

    // Guards the async SearchAsync -> write round-trip against rapid double-scans.
    private bool _processing;

    public PackStationPage()
    {
        InitializeComponent();
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────────

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

        // Returning to this reused page while still logged in: OnDisappearing closed the
        // serial port and stopped the inactivity timer, but _currentOperator is still set.
        // Re-arm both so scanning keeps working and auto-logout stays active.
        if (_currentOperator != null)
        {
            StartInactivityTimer();
            ShowHistoryBelt();
            if (OverlayComPortPicker.SelectedIndex > 0)
                ApplyComPortSelection(OverlayComPortPicker.SelectedIndex);
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

    private async void OnGoHome(object sender, EventArgs e)
        => await Shell.Current.GoToAsync("//home");

    // ── Operator UI ──────────────────────────────────────────────────────────────

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

    private void ShowLoginOverlay() =>
        MainThread.BeginInvokeOnMainThread(() => LoginOverlay.IsVisible = true);

    private void HideLoginOverlay() =>
        MainThread.BeginInvokeOnMainThread(() => LoginOverlay.IsVisible = false);

    // ── Login / logout ────────────────────────────────────────────────────────────

    private void LoginOperator(string badge)
    {
        _currentOperator = badge;
        _currentOperatorFirstName = null;
        StartInactivityTimer();
        StationWsClient.SendOperatorLogin(badge, SessionKind.Packing);
        UpdateNavOperatorUI(badge);
        HideLoginOverlay();
        ShowHistoryBelt();
        _ = ShowWelcomeAnimationAsync(badge);
        Logger.Log($"PackStation: Operator logged in — {badge}");
        _ = Task.Run(async () =>
        {
            var (firstName, _) = await ApiService.GetOperatorInfoAsync(badge);
            if (firstName is null || _currentOperator != badge) return;
            _currentOperatorFirstName = firstName;
            MainThread.BeginInvokeOnMainThread(() =>
            {
                UpdateNavOperatorUI(firstName);
                if (WelcomeBanner.IsVisible)
                    WelcomeLabel.Text = $"Welcome, {firstName}";
            });
        });
    }

    private void LogoutOperator()
    {
        var displayName = _currentOperatorFirstName ?? _currentOperator;
        _currentOperator = null;
        _currentOperatorFirstName = null;
        StopInactivityTimer();
        StationWsClient.SendOperatorLogout();
        UpdateNavOperatorUI(null);
        ShowLoginOverlay();
        ClearHistory();
        HideHistoryBelt();
        if (displayName is not null)
            _ = Toast.Make($"Logged out — {displayName}").Show();
        Logger.Log($"PackStation: Operator logged out — {displayName}");
    }

    // Admin bypass chord (Ctrl+Shift+A+M) — log in without a badge scan.
    private void LoginAsAdmin()
    {
        const string adminName = "admin";
        if (_currentOperator == adminName) return;
        LoginOperator(adminName);
        _ = Toast.Make("Logged in as admin").Show();
        Logger.Log("PackStation: Operator logged in as admin (Ctrl+Shift+A+M bypass)");
    }

    private async Task ShowWelcomeAnimationAsync(string name)
    {
        WelcomeLabel.Text = $"Welcome, {name}";
        WelcomeBanner.IsVisible = true;
        WelcomeBanner.Opacity = 0;
        WelcomeBanner.Scale = 0.85;
        await Task.WhenAll(
            WelcomeBanner.FadeToAsync(1.0, 280, Easing.SinOut),
            WelcomeBanner.ScaleToAsync(1.0, 280, Easing.SinOut));
        await Task.Delay(1500);
        await WelcomeBanner.FadeToAsync(0.0, 350, Easing.SinIn);
        WelcomeBanner.IsVisible = false;
        WelcomeBanner.Scale = 1.0;
    }

    // ── Inactivity auto-logout ──────────────────────────────────────────────────

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

    private void OnInactivityTimerTick(object? sender, EventArgs e) => LogoutOperator();

    // Each successful scan resets the inactivity countdown.
    private void BumpActivity()
    {
        if (_currentOperator is null) return;
        StartInactivityTimer();
    }

    // ── Scan routing ────────────────────────────────────────────────────────────
    // Called from OnSerialDataReceived (Input.cs) on the main thread.

    private async Task HandleScanAsync(string line)
    {
        // Operator badge? -> toggle login/logout. Checked before any tracking handling.
        if (AppSettings.TryParseOperatorBarcode(line) is { })
        {
            if (_currentOperator == line) LogoutOperator();
            else LoginOperator(line);
            return;
        }

        // Require login before any packing scan is honoured.
        if (_currentOperator is null)
        {
            UpdateOverlayScannerStatus("Scan your badge to begin");
            return;
        }

        var tracking = AppSettings.NormalizeTrackingNumber(line);
        await HandlePackScanAsync(tracking);
    }

    private async Task HandlePackScanAsync(string tracking)
    {
        // Gate rapid double-scans: ignore while a verdict is flashing or a write is in flight.
        if (_processing || VerdictOverlay.IsVisible) return;
        _processing = true;
        // Capture the operator for THIS scan up front: a logout / inactivity-timeout
        // landing inside the awaits below must not change who the parcel is attributed to.
        var packer = _currentOperator!;
        var packerName = _currentOperatorFirstName;
        try
        {
            BumpActivity();
            var rows = await ApiService.SearchAsync(tracking);
            var match = rows.FirstOrDefault(r =>
                string.Equals(r.TrackingNumber, tracking, StringComparison.OrdinalIgnoreCase));

            var verdict = PackVerdict.Evaluate(
                found: match != null,
                cancelled: match?.IsCancelledOrder ?? false,
                packingStatus: match?.PackingStatus);

            var fromState = match?.PackingStatus;

            if (verdict.ShouldWrite)
            {
                // Resolve the station id before sealing — never read ResolvedStationId
                // directly in a scan-critical path (it may be unset at startup). This
                // ensures packing_station_id is written, mirroring StationView.
                var stationId = await AppSettings.EnsureStationIdAsync();

                // packed_by MUST be the raw badge string, not the display name.
                var ok = await ApiService.UpdatePackingStatusByScanAsync(
                    tracking, "Packed", packer,
                    packingStationId: stationId);

                if (!ok)
                {
                    Logger.Log($"PackStation: write failed for {tracking}");
                    var failed = PackVerdict.SaveFailed();
                    AddScanToHistory(match, tracking, PackOutcome.SaveFailed);
                    await ShowVerdictAsync(failed, tracking);
                    return;
                }

                StationEvents.Emit(
                    workflowName: "Packing",
                    stepId: "packed_no_video",
                    trigger: "barcode_scan",
                    trackingNumber: tracking,
                    fromState: fromState,
                    toState: "Packed",
                    stationId: stationId,
                    @operator: packer,
                    payload: new Dictionary<string, object?>
                    {
                        ["packedBy"] = packer,
                        ["source"] = "no-video",
                    });

                Logger.Log($"PackStation: {tracking} -> Packed by {packer}");
            }

            AddScanToHistory(match, tracking, verdict.Outcome);
            await ShowVerdictAsync(verdict, tracking, packerName);
        }
        finally
        {
            _processing = false;
        }
    }

    // ── Verdict flash ─────────────────────────────────────────────────────────────

    private async Task ShowVerdictAsync(PackVerdictResult v, string tracking, string? packedByName = null)
    {
        VerdictOverlay.BackgroundColor = Color.FromArgb(v.Color);
        VerdictGlyph.Text = v.Glyph;
        VerdictWord.Text = v.Word;
        VerdictTracking.Text = tracking;
        VerdictSub.Text = v.Outcome == PackOutcome.Pack && packedByName is { } fn
            ? $"by {fn}"
            : v.Sub;

        VerdictOverlay.Opacity = 0;
        VerdictOverlay.IsVisible = true;
        await VerdictOverlay.FadeToAsync(1.0, 120, Easing.SinOut);
        await Task.Delay(1500);
        await VerdictOverlay.FadeToAsync(0.0, 220, Easing.SinIn);
        VerdictOverlay.IsVisible = false;
    }
}
