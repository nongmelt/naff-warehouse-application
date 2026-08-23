using app.Models;
using app.Services;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using System.Runtime.Versioning;

namespace app.Views;

// Duplicate-order card (spec §13.6 / #116). A blocking, amber ALERT overlay
// raised on the QC/scan page when the scanned parcel is a reissue duplicate
// (Shopee + Instant Delivery whose order's summed parcel qty overflows the
// ordered qty — the backend `possibleReissue` signal). The operator decides:
//   Dismiss          → treat as a normal tracking, no DB write, re-blocks on a
//                       fresh rescan.
//   Mark as duplicate → PATCH …/duplicate → status 'Duplicate' (QC-locked, not
//                       billed). Reversible via the undo endpoint.
[SupportedOSPlatform("windows")]
public partial class OrderSearchPage
{
    // Fires the card at most once per scanned tracking; cleared at the start of
    // every search so a fresh rescan re-arms the block.
    private string? _dupOverlayShownFor;
    private PackingList? _dupScanned;
    private PackingList? _dupSibling;
    private PackingList? _dupMarkTarget;
    private string? _dupMarkHint;

    // Cheapness gate mirroring the backend: only a Shopee + Instant Delivery
    // parcel can possibly be a reissue, so every other scan skips the extra
    // get_detail round-trip entirely.
    private static bool IsShopeeInstant(PackingList? p) =>
        p is not null
        && string.Equals(p.Platform, "Shopee", StringComparison.OrdinalIgnoreCase)
        && (p.ShippingOptions?.Contains("instant delivery", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>
    /// After a scan, ask the detail endpoint whether the scanned parcel is a
    /// reissue duplicate. If so, fetch the already-processed sibling and raise
    /// the Duplicate-order card. Fire-and-forget; guarded against a newer scan
    /// superseding this one while the awaits are in flight.
    /// </summary>
    private async Task CheckReissueAsync(string scannedInput)
    {
        var scanned = Results.FirstOrDefault(r =>
            string.Equals(r.TrackingNumber, scannedInput, StringComparison.OrdinalIgnoreCase));
        if (scanned is null) return;                    // not a tracking scan (order-number scan / no match)
        if (scanned.IsDuplicate) return;                // already marked — nothing to prompt
        if (!IsShopeeInstant(scanned)) return;          // cheapness gate
        if (string.Equals(_dupOverlayShownFor, scanned.TrackingNumber, StringComparison.OrdinalIgnoreCase))
            return;                                     // already shown for this parcel this search

        var detail = await ApiService.GetDetailAsync(scanned.TrackingNumber);
        if (detail is not { PossibleReissue: true } || string.IsNullOrWhiteSpace(detail.ReissueExistingTracking))
            return;

        // A newer scan may have loaded a different order while we awaited.
        if (!string.Equals(CurrentOrder?.TrackingNumber, scanned.TrackingNumber, StringComparison.OrdinalIgnoreCase))
            return;

        var sibling = await ApiService.GetDetailAsync(detail.ReissueExistingTracking!);
        if (sibling is null) return;
        // Reissue already resolved — a Duplicate sibling means the card is noise.
        if (sibling.IsDuplicate) return;
        await EnrichProductItemsAsync(sibling.ParsedProducts);

        // Meta line shows both roles by nickname (falls back to the raw code).
        var packedName = string.IsNullOrWhiteSpace(sibling.PackedBy) ? null
            : await ApiService.ResolveOperatorNicknameAsync(sibling.PackedBy) ?? sibling.PackedBy;
        var checkedName = string.IsNullOrWhiteSpace(sibling.CheckedBy) ? null
            : await ApiService.ResolveOperatorNicknameAsync(sibling.CheckedBy) ?? sibling.CheckedBy;

        // Re-check after the awaits above.
        if (!string.Equals(CurrentOrder?.TrackingNumber, scanned.TrackingNumber, StringComparison.OrdinalIgnoreCase))
            return;

        _dupOverlayShownFor = scanned.TrackingNumber;
        MainThread.BeginInvokeOnMainThread(() => ShowDuplicateOverlay(scanned, sibling, packedName, checkedName));
    }

    private void ShowDuplicateOverlay(PackingList scanned, PackingList sibling,
        string? packedName, string? checkedName)
    {
        _dupScanned = scanned;
        _dupSibling = sibling;

        DupOrderNumber.Text = scanned.OrderNumber;

        // Platform logo (same asset as the header tracking card) — no text badge.
        DupPlatformIcon.IsVisible = scanned.HasPlatformIcon;
        if (scanned.HasPlatformIcon)
            DupPlatformIcon.Source = scanned.PlatformIcon;

        // Ship chip (Instant Delivery is the only firing condition, but render
        // whatever the parcel actually carries).
        if (!string.IsNullOrWhiteSpace(scanned.ShippingOptions))
        {
            DupShipChip.IsVisible = true;
            DupShipLabel.Text = "⚡ " + scanned.ShippingOptions;
        }
        else DupShipChip.IsVisible = false;

        DupSiblingColumn.BindingContext = sibling;
        DupScannedColumn.BindingContext = scanned;

        // Meta lines in tracking-card grammar (faint label + slate value),
        // exact timestamps per the 2026-08-23 mockup.
        DupSiblingMetaLabel.FormattedText = checkedName is not null
            ? MetaLine(("Packed:", packedName ?? "—"), ("Checked:", checkedName),
                       ("Checked at:", sibling.CheckedAtDisplay), ("Items:", sibling.TotalItemsDisplay))
            : packedName is not null
                ? MetaLine(("Packed:", packedName), ("Packed at:", sibling.UpdatedAtDisplay),
                           ("Items:", sibling.TotalItemsDisplay))
                : MetaLine(("Created:", sibling.CreatedAtDisplay), ("Items:", sibling.TotalItemsDisplay));

        // The scan moment IS the check moment for the parcel in hand.
        DupScannedMetaLabel.FormattedText = MetaLine(
            ("Checked at:", DateTime.Now.ToString("yyyy-MM-dd HH:mm")),
            ("Items:", scanned.TotalItemsDisplay));

        // §13.6 honesty fix: the backend fires possibleReissue on qty overflow
        // alone — the sibling may itself be unprocessed. Don't claim
        // "Already processed" when it isn't.
        var siblingProcessed = !string.Equals(
            sibling.PackingStatus, "To be packed", StringComparison.OrdinalIgnoreCase);
        DupSiblingHeaderLabel.Text = siblingProcessed
            ? "✓ Already processed"
            : "◷ Other parcel";
        DupSiblingHeaderLabel.TextColor = Color.FromArgb(siblingProcessed ? "#166534" : "#6b7280");
        DupBothUnprocessedBanner.IsVisible = !siblingProcessed && string.Equals(
            scanned.PackingStatus, "To be packed", StringComparison.OrdinalIgnoreCase);

        // Neither-processed: the parcel in hand ships; Mark targets the sibling.
        _dupMarkTarget = DuplicateMarkPolicy.MarksSibling(sibling.PackingStatus, scanned.PackingStatus)
            ? sibling : scanned;
        var shipSide = ReferenceEquals(_dupMarkTarget, sibling) ? scanned : sibling;
        _dupMarkHint = DuplicateMarkPolicy.BuildMarkTooltip(_dupMarkTarget.TrackingNumber, shipSide.TrackingNumber);

        DupMarkButtonLabel.Text = "Mark as duplicate";
        DupMarkButton.Opacity = 1;
        DupFooterHint.Opacity = 0;

        _ = ShowDuplicateOverlayAnimatedAsync();
    }

    private static FormattedString MetaLine(params (string Label, string Value)[] parts)
    {
        var fs = new FormattedString();
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0) fs.Spans.Add(new Span { Text = "   " });
            fs.Spans.Add(new Span
            {
                Text = parts[i].Label + " ",
                TextColor = Color.FromArgb("#9ca3af"),
                FontSize = 11.5,
            });
            fs.Spans.Add(new Span
            {
                Text = parts[i].Value,
                TextColor = Color.FromArgb("#374151"),
                FontSize = 11.5,
                FontAttributes = FontAttributes.Bold,
            });
        }
        return fs;
    }

    private async Task ShowDuplicateOverlayAnimatedAsync()
    {
        // #119: an auto-opened single-product image overlay (ZIndex 8) would sit
        // on top of this card (ZIndex 7). Dismiss it first so the card is the
        // only surface demanding the reissue decision.
        if (ProductImageOverlay.IsVisible)
            await DismissImageOverlayAsync("duplicate_card_raised");

        DuplicateOrderOverlay.Opacity = 0;
        DuplicateOrderOverlay.IsVisible = true;
        await DuplicateOrderOverlay.FadeToAsync(1, 220, Easing.CubicOut);
    }

    // Hide the card without a DB write. Called for both Dismiss and backdrop tap,
    // and by a fresh search so a lingering card doesn't sit over a new order.
    private async Task DismissDuplicateOverlayAsync()
    {
        if (!DuplicateOrderOverlay.IsVisible) return;
        await DuplicateOrderOverlay.FadeToAsync(0, 180, Easing.CubicIn);
        DuplicateOrderOverlay.IsVisible = false;
    }

    private async void OnDuplicateOverlayBackdropTapped(object sender, TappedEventArgs e)
        => await DismissDuplicateOverlayAsync();

    private async void OnDuplicateDismissTapped(object sender, TappedEventArgs e)
        => await DismissDuplicateOverlayAsync();

    // Instant footer hint replacing native ToolTipProperties (too slow to
    // respond per live QA feedback). Opacity-only toggle onto a reserved-space
    // label so button layout never shifts.
    private void OnDupDismissHintEntered(object? sender, PointerEventArgs e)
    {
        DupFooterHint.Text = DuplicateMarkPolicy.DismissTooltip;
        DupFooterHint.Opacity = 1;
    }

    private void OnDupMarkHintEntered(object? sender, PointerEventArgs e)
    {
        DupFooterHint.Text = _dupMarkHint ?? string.Empty;
        DupFooterHint.Opacity = 1;
    }

    private void OnDupFooterHintExited(object? sender, PointerEventArgs e)
        => DupFooterHint.Opacity = 0;

    private async void OnDuplicateMarkTapped(object sender, TappedEventArgs e)
    {
        var target = _dupMarkTarget ?? _dupScanned;
        if (target is null) { await DismissDuplicateOverlayAsync(); return; }

        DupMarkButtonLabel.Text = "Marking…";
        DupMarkButton.Opacity = 0.6;

        var result = await ApiService.MarkDuplicateAsync(
            target.TrackingNumber, EffectiveOperator, AppSettings.ResolvedStationId);

        if (result.Marked || result.AlreadyMarked)
        {
            // Status flip updates the pill and QC-locks the parcel reactively.
            target.PackingStatus = "Duplicate";
            UpdateHeaderOrderInfo();
            UpdateSearchStatus($"{target.TrackingNumber} marked as duplicate — QC locked, not billed.");
            await DismissDuplicateOverlayAsync();
        }
        else
        {
            // 409 = the parcel is no longer 'To be packed' (e.g. packing started
            // under it). Leave the card up so the operator can Dismiss.
            DupMarkButtonLabel.Text = "Mark as duplicate";
            DupMarkButton.Opacity = 1;
            UpdateSearchStatus(result.Status == 409
                ? "Can't mark — parcel is no longer 'To be packed'."
                : "Mark failed — check the connection and try again.");
        }
    }

    private CancellationTokenSource? _dupToastCts;

    // The search-status confirmation sits BEHIND the card backdrop, so copy
    // feedback surfaces as a tiny toast next to the tapped value instead.
    private async Task ShowDupCopiedToastAsync(VisualElement toast)
    {
        _dupToastCts?.Cancel();
        var cts = _dupToastCts = new CancellationTokenSource();
        try
        {
            toast.Opacity = 0;
            toast.IsVisible = true;
            await toast.FadeToAsync(1, 120, Easing.CubicOut);
            await Task.Delay(900, cts.Token);
            await toast.FadeToAsync(0, 180, Easing.CubicIn);
        }
        catch (TaskCanceledException) { }
        finally
        {
            toast.IsVisible = false;
            toast.Opacity = 0;
        }
    }

    private async void OnDuplicateCopyOrderTapped(object sender, TappedEventArgs e)
    {
        if (_dupScanned is null) return;
        await Clipboard.Default.SetTextAsync(_dupScanned.OrderNumber);
        UpdateSearchStatus($"Copied  {_dupScanned.OrderNumber}");
        await ShowDupCopiedToastAsync(DupOrderCopiedToast);
    }

    private async void OnDuplicateCopyTrackingTapped(object sender, TappedEventArgs e)
    {
        if (sender is VisualElement { BindingContext: PackingList pl })
        {
            await Clipboard.Default.SetTextAsync(pl.TrackingNumber);
            UpdateSearchStatus($"Copied  {pl.TrackingNumber}");
            await ShowDupCopiedToastAsync(
                ReferenceEquals(pl, _dupSibling) ? DupSiblingCopiedToast : DupScannedCopiedToast);
        }
    }

    // Click a product photo → re-open the existing QC image viewer on top of the
    // card (ZIndex 8 > 7). Read-only peek; the viewer's picking state doesn't
    // apply to a card parcel. Prev/next arrows browse the tapped parcel's own
    // products, staying read-only (#118).
    private void OnDuplicateProductTapped(object sender, TappedEventArgs e)
    {
        if (sender is VisualElement { BindingContext: ProductItem item })
        {
            var source = _dupSibling?.ParsedProducts.Contains(item) == true ? _dupSibling : _dupScanned;
            _overlayReadOnlyList = source?.ParsedProducts;
            ShowProductImageOverlay(item, "duplicate_card_peek", readOnly: true);
        }
    }

    // Undo a duplicate mark from the parcel header (any operator). The backend
    // restores whichever status the parcel had before the mark — currently
    // always 'To be packed', since a parcel can only be marked while
    // to-be-packed (spec §13.6).
    private async void OnUndoDuplicateButtonTapped(object sender, TappedEventArgs e)
    {
        var order = CurrentOrder;
        if (order is null || !order.IsDuplicate) return;

        var status = await ApiService.UndoDuplicateAsync(
            order.TrackingNumber, EffectiveOperator, AppSettings.ResolvedStationId);

        if (status is >= 200 and < 300)
        {
            order.PackingStatus = "To be packed";
            UpdateHeaderOrderInfo();
            UpdateSearchStatus($"{order.TrackingNumber} duplicate mark undone — restored to 'To be packed'.");
        }
        else if (status == 409)
        {
            // Server says it's no longer a duplicate — our cached status is stale.
            // Re-sync so the Undo button clears instead of 409-ing on every click.
            var fresh = await ApiService.GetDetailAsync(order.TrackingNumber);
            if (fresh is not null)
            {
                order.PackingStatus = fresh.PackingStatus;
                UpdateHeaderOrderInfo();
                UpdateSearchStatus($"{order.TrackingNumber} is no longer a duplicate — view refreshed.");
            }
            else
            {
                UpdateSearchStatus("Undo failed — check the connection and try again.");
            }
        }
        else
        {
            UpdateSearchStatus("Undo failed — check the connection and try again.");
        }
    }
}
