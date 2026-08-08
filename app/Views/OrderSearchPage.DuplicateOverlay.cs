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
        await EnrichProductItemsAsync(sibling.ParsedProducts);

        // Re-check after the awaits above.
        if (!string.Equals(CurrentOrder?.TrackingNumber, scanned.TrackingNumber, StringComparison.OrdinalIgnoreCase))
            return;

        _dupOverlayShownFor = scanned.TrackingNumber;
        MainThread.BeginInvokeOnMainThread(() => ShowDuplicateOverlay(scanned, sibling));
    }

    private void ShowDuplicateOverlay(PackingList scanned, PackingList sibling)
    {
        _dupScanned = scanned;
        _dupSibling = sibling;

        DupOrderNumber.Text = scanned.OrderNumber;

        // Platform badge — same colour map as the header badge.
        if (!string.IsNullOrWhiteSpace(scanned.Platform))
        {
            DupPlatformBadge.IsVisible = true;
            DupPlatformLabel.Text = scanned.Platform.ToUpperInvariant();
            var p = scanned.Platform.ToLowerInvariant();
            DupPlatformBadge.BackgroundColor = p switch
            {
                var s when s.Contains("shopee") => Color.FromArgb("#ee4d2d"),
                var s when s.Contains("lazada") => Color.FromArgb("#0f146d"),
                var s when s.Contains("tiktok") => Color.FromArgb("#111827"),
                _ => Color.FromArgb("#6b7280"),
            };
        }
        else DupPlatformBadge.IsVisible = false;

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

        DupSiblingMeta.Text =
            $"Checked by {sibling.CheckedByDisplay} · {sibling.CheckedAtDisplay} · {sibling.ParsedProducts.Count} products";
        DupScannedMeta.Text = $"Just now · {scanned.ParsedProducts.Count} products";

        DupMarkButtonLabel.Text = "Mark as duplicate";
        DupMarkButton.Opacity = 1;

        _ = ShowDuplicateOverlayAnimatedAsync();
    }

    private async Task ShowDuplicateOverlayAnimatedAsync()
    {
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

    private async void OnDuplicateMarkTapped(object sender, TappedEventArgs e)
    {
        var scanned = _dupScanned;
        if (scanned is null) { await DismissDuplicateOverlayAsync(); return; }

        DupMarkButtonLabel.Text = "Marking…";
        DupMarkButton.Opacity = 0.6;

        var result = await ApiService.MarkDuplicateAsync(
            scanned.TrackingNumber, EffectiveOperator, AppSettings.ResolvedStationId);

        if (result.Marked || result.AlreadyMarked)
        {
            // Status flip updates the pill and QC-locks the parcel reactively.
            scanned.PackingStatus = "Duplicate";
            UpdateSearchStatus($"{scanned.TrackingNumber} marked as duplicate — QC locked, not billed.");
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

    private async void OnDuplicateCopyOrderTapped(object sender, TappedEventArgs e)
    {
        if (_dupScanned is null) return;
        await Clipboard.Default.SetTextAsync(_dupScanned.OrderNumber);
        UpdateSearchStatus($"Copied  {_dupScanned.OrderNumber}");
    }

    private async void OnDuplicateCopyTrackingTapped(object sender, TappedEventArgs e)
    {
        if (sender is VisualElement { BindingContext: PackingList pl })
        {
            await Clipboard.Default.SetTextAsync(pl.TrackingNumber);
            UpdateSearchStatus($"Copied  {pl.TrackingNumber}");
        }
    }

    // Click a product photo → re-open the existing QC image viewer on top of the
    // card (ZIndex 8 > 7). Read-only peek; the viewer's picking state doesn't
    // apply to a card parcel.
    private void OnDuplicateProductTapped(object sender, TappedEventArgs e)
    {
        if (sender is VisualElement { BindingContext: ProductItem item })
            ShowProductImageOverlay(item, "duplicate_card_peek", readOnly: true);
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
        else
        {
            UpdateSearchStatus(status == 409
                ? "Nothing to undo — parcel is not a duplicate."
                : "Undo failed — check the connection and try again.");
        }
    }
}
