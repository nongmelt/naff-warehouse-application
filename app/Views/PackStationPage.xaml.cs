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
    private volatile bool _currentOperatorIsSupervisor; // true when the logged-in operator's role == "supervisor"

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
            HideParcelPanel();
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
        _currentOperatorIsSupervisor = false;
        StartInactivityTimer();
        StationWsClient.SendOperatorLogin(badge, SessionKind.Packing);
        UpdateNavOperatorUI(badge);
        HideLoginOverlay();
        ShowHistoryBelt();
        _ = SeedPackedTallyAsync(badge);
        _ = ShowWelcomeAnimationAsync(badge);
        Logger.Log($"PackStation: Operator logged in — {badge}");
        _ = Task.Run(async () =>
        {
            var (firstName, _, role) = await ApiService.GetOperatorInfoAsync(badge);
            if (_currentOperator != badge) return;
            _currentOperatorIsSupervisor = string.Equals(role, "supervisor", StringComparison.OrdinalIgnoreCase);
            if (firstName is null) return;
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
        _currentOperatorIsSupervisor = false;
        StopInactivityTimer();
        StationWsClient.SendOperatorLogout();
        UpdateNavOperatorUI(null);
        ShowLoginOverlay();
        ClearHistory();
        HideHistoryBelt();
        HideParcelPanel();
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
        if (_processing) return;
        _processing = true;
        var packer = _currentOperator!;
        var supervisorCode = _currentOperator!;
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
                packingStatus: match?.PackingStatus,
                allItemsCleared: match?.AllItemsCleared ?? false);

            var fromState = match?.PackingStatus;

            if (verdict.ShouldWrite)
            {
                var stationId = await AppSettings.EnsureStationIdAsync();

                var result = await ApiService.ShipAsync(tracking, packer, shippingStationId: stationId);

                if (result.Status != 200)
                {
                    // Server is authoritative: map its rejection to the right verdict.
                    var serverVerdict = result.Status switch
                    {
                        404 => new PackVerdictResult(PackOutcome.NotFound, false, "NOT FOUND", "No matching order", "?", PackVerdict.ColorRed),
                        410 => new PackVerdictResult(PackOutcome.Cancelled, false, "CANCELLED", "Order cancelled", "✕", PackVerdict.ColorRed),
                        409 => new PackVerdictResult(PackOutcome.Blocked, false, "BLOCKED", "Not ready to ship", "!", PackVerdict.ColorAmber),
                        _   => PackVerdict.SaveFailed(),
                    };
                    Logger.Log($"PackStation: ship rejected {tracking} HTTP {result.Status}");
                    AddScanToHistory(match, tracking, serverVerdict.Outcome);
                    ShowParcelPanel(match, serverVerdict, packerName);
                    return;
                }

                // Race guard: another station shipped it between our search and this ship call.
                // Server returns 200 alreadyShipped=true with no write — show the no-op verdict.
                if (result.AlreadyShipped)
                {
                    var already = new PackVerdictResult(PackOutcome.AlreadyShipped, false,
                        "ALREADY SHIPPED", "Already shipped", "↻", PackVerdict.ColorGrey);
                    AddScanToHistory(match, tracking, PackOutcome.AlreadyShipped);
                    ShowParcelPanel(match, already, packerName);
                    return;
                }

                // Record the Packed milestone first when the server backfilled it.
                if (result.PackedBackfilled)
                {
                    StationEvents.Emit(
                        workflowName: "Shipping",
                        stepId: "packed",
                        trigger: "barcode_scan",
                        trackingNumber: tracking,
                        fromState: fromState,
                        toState: "Packed",
                        stationId: stationId,
                        @operator: packer,
                        payload: new Dictionary<string, object?> { ["source"] = "ship-backfill" });
                }

                StationEvents.Emit(
                    workflowName: "Shipping",
                    stepId: "shipped",
                    trigger: "barcode_scan",
                    trackingNumber: tracking,
                    fromState: result.PackedBackfilled ? "Packed" : fromState,
                    toState: "Shipped",
                    stationId: stationId,
                    @operator: packer,
                    payload: new Dictionary<string, object?>
                    {
                        ["shippedBy"] = packer,
                        ["carrier"] = match?.ShippingOptions,
                    });

                Logger.Log($"PackStation: {tracking} -> Shipped by {packer}");
            }

            // Not QC-cleared but otherwise valid → logged-in supervisor may force it through.
            if (PackVerdict.IsForceable(verdict) && _currentOperatorIsSupervisor)
            {
                var ok = await DisplayAlert(
                    "Force ship?",
                    $"{tracking} is not QC-cleared. Force-ship as supervisor {_currentOperatorFirstName ?? _currentOperator}?",
                    "Force", "Cancel");
                if (ok)
                {
                    await ForceShipAsync(tracking, match, supervisorCode);
                    return;
                }
                // Cancel → fall through to show the blocked panel
            }

            AddScanToHistory(match, tracking, verdict.Outcome);
            ShowParcelPanel(match, verdict, packerName);
        }
        finally
        {
            _processing = false;
        }
    }

    // Called from within HandlePackScanAsync's try block (which already holds _processing).
    // Do NOT add a _processing guard here — the caller owns the lock.
    private async Task ForceShipAsync(string tracking, PackingList? match, string supervisorCode)
    {
        var stationId = await AppSettings.EnsureStationIdAsync();
        var result = await ApiService.ShipAsync(
            tracking, supervisorCode, shippingStationId: stationId,
            force: true, forcedBy: supervisorCode);

        // Server is authoritative. Backend writes the forced audit, so we do NOT emit a
        // normal "shipped" StationEvents here (avoids a duplicate ship event).
        var v = result.Status switch
        {
            200 when !result.AlreadyShipped =>
                new PackVerdictResult(PackOutcome.Ship, true, "FORCED", "Force-shipped (supervisor)", "✓", PackVerdict.ColorGreen),
            200 =>
                new PackVerdictResult(PackOutcome.AlreadyShipped, false, "ALREADY SHIPPED", "Already shipped", "↻", PackVerdict.ColorGrey),
            403 =>
                new PackVerdictResult(PackOutcome.Blocked, false, "FORCE DENIED", "Not an active supervisor", "✕", PackVerdict.ColorRed),
            410 =>
                new PackVerdictResult(PackOutcome.Cancelled, false, "CANCELLED", "Order cancelled", "✕", PackVerdict.ColorRed),
            404 =>
                new PackVerdictResult(PackOutcome.NotFound, false, "NOT FOUND", "No matching order", "?", PackVerdict.ColorRed),
            409 =>
                new PackVerdictResult(PackOutcome.Blocked, false, "BLOCKED", "Cannot force this parcel", "!", PackVerdict.ColorAmber),
            _ => PackVerdict.SaveFailed(),
        };

        Logger.Log($"PackStation: force ship {tracking} by supervisor {supervisorCode} -> HTTP {result.Status}");
        AddScanToHistory(match, tracking, v.Outcome);
        ShowParcelPanel(match, v, _currentOperatorFirstName);
        UpdateOverlayScannerStatus(
            v.Outcome == PackOutcome.Ship ? $"Force-shipped {tracking}" : "Scan a parcel");
    }

}
