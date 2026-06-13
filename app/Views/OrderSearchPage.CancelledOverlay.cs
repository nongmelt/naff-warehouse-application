using System.Runtime.Versioning;

namespace app.Views;

[SupportedOSPlatform("windows")]
public partial class OrderSearchPage
{
    private string? _cancelledOverlayShownFor;

    private async void ShowCancelledOverlay()
    {
        CancelledOrderOverlay.Opacity = 0;
        CancelledOrderOverlay.IsVisible = true;
        await CancelledOrderOverlay.FadeToAsync(1, 220, Easing.CubicOut);
    }

    private async void OnCancelledOverlayBackdropTapped(object sender, TappedEventArgs e)
        => await DismissCancelledOverlayAsync();

    private async void OnCancelledOverlayDismissTapped(object sender, TappedEventArgs e)
        => await DismissCancelledOverlayAsync();

    private async Task DismissCancelledOverlayAsync()
    {
        await CancelledOrderOverlay.FadeToAsync(0, 200, Easing.CubicIn);
        CancelledOrderOverlay.IsVisible = false;
    }
}
