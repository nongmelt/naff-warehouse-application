using app.Services;
using CommunityToolkit.Maui.Alerts;
using System.Runtime.Versioning;

namespace app.Views;

[SupportedOSPlatform("windows")]
public partial class OrderSearchPage
{
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

    // ── Mode Switching ───────────────────────────────────────────────────────

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

    private void OnModeSelectReturns(object? sender, EventArgs e)
    {
        ModeDropdownBackdrop.IsVisible = false;
        ApplyMode(AppMode.Returns);
    }

    private static string GetModeDisplayName(AppMode mode) => mode switch
    {
        AppMode.QC => "QC",
        AppMode.Returns => "Returns",
        _ => mode.ToString(),
    };

    private void ApplyMode(AppMode mode)
    {
        _currentMode = mode;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            ModeLabel.Text = GetModeDisplayName(mode);

            bool isReturns = mode == AppMode.Returns;
            bool hasOrder = Results.Count > 0;

            // Mode dropdown checkmarks
            ModeCheckQC.IsVisible = !isReturns;
            ModeOptionQC.BackgroundColor = !isReturns ? Color.FromArgb("#f5f5ff") : Colors.Transparent;
            ModeCheckReturns.IsVisible = isReturns;
            ModeOptionReturns.BackgroundColor = isReturns ? Color.FromArgb("#f5f5ff") : Colors.Transparent;

            ReturnsActionForm.IsVisible = isReturns && hasOrder;
            ReturnsEmptyState.IsVisible = isReturns && !hasOrder;
            ReturnsSuccessCard.IsVisible = false;
            QcShortcuts.IsVisible = !isReturns;
            ReturnsShortcuts.IsVisible = isReturns;
            if (!isReturns)
                TrackingCarrierPill.IsVisible = false;

            // Toggle stat pills
            SessionCompletedBadge.IsVisible = !isReturns;
            SessionIncompleteBadge.IsVisible = !isReturns;
            SessionIncomingBadge.IsVisible = !isReturns;
            ReturnsReturnedBadge.IsVisible = isReturns;
            ReturnsPendingBadge.IsVisible = isReturns;

            // Side panel
            ReturnsSidePanel.IsVisible = isReturns;

            if (isReturns)
            {
                OnSelectReturnType(null, EventArgs.Empty);
                UpdateHeaderOrderInfo();
                UpdateReturnsSidePanel();
            }
        });
    }

    // ── Login & Inactivity ───────────────────────────────────────────────────

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
        _currentOperatorId = null;
        StopInactivityTimer();
        Services.StationWsClient.SendOperatorLogout();
        UpdateNavOperatorUI(null);
        ShowLoginOverlay();
        if (displayName is not null)
            _ = Toast.Make($"Session ended — {displayName}").Show();
        Logger.Log($"OrderSearch: Operator logged out (inactivity)");
    }
}
