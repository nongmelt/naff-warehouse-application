using app.Models;
using app.Services;
using app.Workflows;
using Microsoft.Maui.Controls.Shapes;
using System.Runtime.Versioning;

namespace app.Views;

// Returns side of the Ship/Returns toggle: mode dropdown, return-scan routing (invoked from
// HandlePackScanAsync in PackStationPage.xaml.cs), the reason-chip confirm overlay, and the
// supervisor undo-return path. Mirrors the force-ship offer pattern already used for Shipping.
[SupportedOSPlatform("windows")]
public partial class PackStationPage
{
    private static readonly string[] ReturnReasons =
        ["Customer request", "Damaged package", "Duplicate order", "Wrong product", "Other"];

    private string? _pendingReturnTracking;
    private PackingList? _pendingReturnMatch;
    private string? _selectedReturnReason;
    private string? _pendingUndoTracking;
    private readonly List<Border> _reasonChipBorders = [];

    // TEMP STUB (Task 11): the restock items panel is implemented in Task 12. Until that lands,
    // this is a no-op so the AlreadyReturned / post-confirm call sites below compile and run.
    // Task 12 replaces this stub with the real panel loader.
    private Task LoadReturnItemsPanelAsync(string _) => Task.CompletedTask;

    // ── Mode dropdown ─────────────────────────────────────────────────────────────

    private void OnPackModeBadgeTapped(object? sender, EventArgs e)
    {
        var opening = !PackModeDropdownBackdrop.IsVisible;
        if (opening) ComPortDropdownBackdrop.IsVisible = false; // close the sibling overlay first
        PackModeDropdownBackdrop.IsVisible = opening;
    }

    private void OnPackModeDropdownBackdropTapped(object? sender, EventArgs e) =>
        PackModeDropdownBackdrop.IsVisible = false;

    private void OnPackModeSelectShip(object? sender, EventArgs e) => ApplyPackMode(PackMode.Ship);

    private void OnPackModeSelectReturns(object? sender, EventArgs e) => ApplyPackMode(PackMode.Returns);

    private void ApplyPackMode(PackMode mode)
    {
        _currentMode = mode;
        PackModeDropdownBackdrop.IsVisible = false;
        PackModeLabel.Text = mode == PackMode.Ship ? "SHIP" : "RETURNS";
        PackModeCheckShip.IsVisible = mode == PackMode.Ship;
        PackModeCheckReturns.IsVisible = mode == PackMode.Returns;
        PageSubtitleLabel.Text = mode == PackMode.Ship ? "SEAL & VERIFY" : "RETURN & RESTOCK";
        HideForceOffer();
        HideUndoOffer();
        HideReturnsConfirm();
        HideParcelPanel();
        Logger.Log($"PackStation: mode -> {mode}");
    }

    // ── Return scan ───────────────────────────────────────────────────────────────

    private async Task HandleReturnScanAsync(string tracking, PackingList? match)
    {
        // Ignore scans while the confirm dialog is open — operator must Confirm/Cancel first.
        if (ReturnsConfirmOverlay.IsVisible) return;

        var rv = ReturnVerdict.Evaluate(match != null, match?.PackingStatus);

        switch (rv.Outcome)
        {
            case ReturnOutcome.Return:
                _pendingReturnTracking = tracking;
                _pendingReturnMatch = match;
                ShowReturnsConfirm(tracking, match);
                return;

            case ReturnOutcome.AlreadyReturned:
                ShowReturnDisplay(match, tracking, PackOutcome.AlreadyShipped, rv);
                if (_currentOperatorIsSupervisor) ShowUndoOffer(tracking);
                await LoadReturnItemsPanelAsync(tracking); // Task 12: late restock scanning
                return;

            case ReturnOutcome.NotFound:
                ShowReturnDisplay(match, tracking, PackOutcome.NotFound, rv);
                return;

            default: // NotShipped
                ShowReturnDisplay(match, tracking, PackOutcome.Blocked, rv);
                return;
        }
    }

    // Map a return verdict onto the existing parcel panel + belt (display only, no write).
    private void ShowReturnDisplay(PackingList? match, string tracking, PackOutcome beltOutcome, ReturnVerdictResult rv)
    {
        // Clear any undo button armed for a previously-scanned parcel before showing this one.
        HideUndoOffer();
        var display = new PackVerdictResult(beltOutcome, false, rv.Word, rv.Sub, rv.Glyph, rv.Color);
        AddScanToHistory(match, tracking, beltOutcome);
        ShowParcelPanel(match, display, _currentOperatorFirstName);
    }

    // ── Confirm overlay ───────────────────────────────────────────────────────────

    private void ShowReturnsConfirm(string tracking, PackingList? match)
    {
        _selectedReturnReason = null;
        ReturnsNotesEditor.Text = "";
        ReturnsConfirmBtn.IsEnabled = false;
        ReturnsConfirmSummary.Text =
            $"{tracking} · {match?.Platform ?? "—"} · {match?.ShippingOptions ?? "—"} · {match?.TotalItems?.ToString() ?? "?"} items";
        BuildReasonChips();
        ReturnsConfirmOverlay.IsVisible = true;
    }

    private void HideReturnsConfirm()
    {
        ReturnsConfirmOverlay.IsVisible = false;
        _pendingReturnTracking = null;
        _pendingReturnMatch = null;
        _selectedReturnReason = null;
    }

    private void BuildReasonChips()
    {
        ReturnsReasonChips.Children.Clear();
        _reasonChipBorders.Clear();
        foreach (var reason in ReturnReasons)
        {
            var label = new Label { Text = reason, FontSize = 13, TextColor = Color.FromArgb("#374151") };
            var chip = new Border
            {
                StrokeShape = new RoundRectangle { CornerRadius = 14 },
                Stroke = Color.FromArgb("#e5e7eb"),
                StrokeThickness = 1,
                BackgroundColor = Color.FromArgb("#f9fafb"),
                Padding = new Thickness(14, 8),
                Margin = new Thickness(0, 0, 8, 8),
                Content = label,
            };
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => SelectReasonChip(reason);
            chip.GestureRecognizers.Add(tap);
            _reasonChipBorders.Add(chip);
            ReturnsReasonChips.Children.Add(chip);
        }
    }

    private void SelectReasonChip(string reason)
    {
        _selectedReturnReason = reason;
        ReturnsConfirmBtn.IsEnabled = true;
        for (var i = 0; i < _reasonChipBorders.Count; i++)
        {
            var selected = ReturnReasons[i] == reason;
            _reasonChipBorders[i].Stroke = selected ? Color.FromArgb("#dc2626") : Color.FromArgb("#e5e7eb");
            _reasonChipBorders[i].StrokeThickness = selected ? 2 : 1;
            _reasonChipBorders[i].BackgroundColor = selected ? Color.FromArgb("#fef2f2") : Color.FromArgb("#f9fafb");
        }
    }

    private void OnReturnCancelClicked(object? sender, EventArgs e) => HideReturnsConfirm();

    private async void OnReturnConfirmClicked(object? sender, EventArgs e)
    {
        if (_processing) return;
        var tracking = _pendingReturnTracking;
        var reason = _selectedReturnReason;
        var match = _pendingReturnMatch;
        if (tracking is null || reason is null || _currentOperator is null) return;

        _processing = true;
        try
        {
            BumpActivity();
            var notes = string.IsNullOrWhiteSpace(ReturnsNotesEditor.Text) ? null : ReturnsNotesEditor.Text.Trim();
            var stationId = await AppSettings.EnsureStationIdAsync();
            var result = await ApiService.ReturnScanAsync(
                tracking, _currentOperator, stationId, operatorId: null, reason, notes);

            HideReturnsConfirm();

            if (result.Status == 200 && !result.AlreadyReturned)
            {
                var display = new PackVerdictResult(PackOutcome.Ship, true, "RETURNED", $"{reason}", "⮌", ReturnVerdict.ColorGreen);
                StationEvents.Emit(
                    workflowName: "Returns",
                    stepId: "returned",
                    trigger: "barcode_scan",
                    trackingNumber: tracking,
                    fromState: "Shipped",
                    toState: "Returned",
                    stationId: stationId,
                    @operator: _currentOperator,
                    payload: new Dictionary<string, object?> { ["reason"] = reason, ["carrier"] = match?.ShippingOptions });
                Logger.Log($"PackStation: {tracking} -> Returned by {_currentOperator} ({reason})");
                AddScanToHistory(match, tracking, PackOutcome.Ship);
                ShowParcelPanel(match, display, _currentOperatorFirstName);
                if (_currentOperatorIsSupervisor) ShowUndoOffer(tracking);
                await LoadReturnItemsPanelAsync(tracking); // Task 12
                return;
            }

            var v = result.Status switch
            {
                200 => new PackVerdictResult(PackOutcome.AlreadyShipped, false, "ALREADY RETURNED", "Already returned", "↻", ReturnVerdict.ColorGrey),
                409 => new PackVerdictResult(PackOutcome.Blocked, false, "NOT SHIPPED", "Only shipped parcels return", "!", ReturnVerdict.ColorAmber),
                404 => new PackVerdictResult(PackOutcome.NotFound, false, "NOT FOUND", "No matching order", "?", ReturnVerdict.ColorRed),
                400 => new PackVerdictResult(PackOutcome.Blocked, false, "REASON REQUIRED", "Pick a reason", "!", ReturnVerdict.ColorRed),
                _ => PackVerdict.SaveFailed(),
            };
            // Clear any undo button armed for a previously-scanned parcel before showing this one.
            HideUndoOffer();
            AddScanToHistory(match, tracking, v.Outcome);
            ShowParcelPanel(match, v, _currentOperatorFirstName);
        }
        finally
        {
            _processing = false;
        }
    }

    // ── Supervisor undo ───────────────────────────────────────────────────────────

    private void ShowUndoOffer(string tracking)
    {
        _pendingUndoTracking = tracking;
        UndoReturnButton.Text = $"⮌ Undo return {tracking}";
        UndoReturnButton.IsVisible = true;
    }

    private void HideUndoOffer()
    {
        _pendingUndoTracking = null;
        UndoReturnButton.IsVisible = false;
    }

    private async void OnUndoReturnClicked(object? sender, EventArgs e)
    {
        if (_processing) return;
        var tracking = _pendingUndoTracking;
        if (tracking is null) return;
        if (_currentOperator is null || !_currentOperatorIsSupervisor) { HideUndoOffer(); return; }

        BumpActivity();
        var ok = await DisplayAlert(
            "Undo return?",
            $"{tracking} will be restored to Shipped. Undo as supervisor {_currentOperatorFirstName ?? _currentOperator}?",
            "Undo", "Cancel");
        if (!ok) return;

        _processing = true;
        try
        {
            HideUndoOffer();
            var status = await ApiService.UndoReturnAsync(tracking, _currentOperator!, AppSettings.ResolvedStationId);
            var v = status switch
            {
                200 => new PackVerdictResult(PackOutcome.Ship, true, "RETURN UNDONE", "Restored to Shipped", "✓", ReturnVerdict.ColorGreen),
                403 => new PackVerdictResult(PackOutcome.Blocked, false, "NOT SUPERVISOR", "Not an active supervisor", "✕", ReturnVerdict.ColorRed),
                409 => new PackVerdictResult(PackOutcome.Blocked, false, "NOT RETURNED", "Parcel is not returned", "!", ReturnVerdict.ColorAmber),
                _ => PackVerdict.SaveFailed(),
            };
            Logger.Log($"PackStation: undo return {tracking} by {_currentOperator} -> HTTP {status}");
            AddScanToHistory(null, tracking, v.Outcome);
            ShowParcelPanel(null, v, _currentOperatorFirstName);
        }
        finally
        {
            _processing = false;
        }
    }
}
