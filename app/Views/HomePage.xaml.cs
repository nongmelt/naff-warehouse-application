using System.Runtime.Versioning;

namespace app.Views;

[SupportedOSPlatform("windows")]
public partial class HomePage : ContentPage
{
    private static readonly Color _cardDefault   = Colors.White;
    private static readonly Color _cardHover     = Color.FromArgb("#f3f4f6");
    private static readonly Color _cardPressed   = Color.FromArgb("#e5e7eb");
    private static readonly Color _strokeDefault = Color.FromArgb("#e5e7eb");
    private static readonly Color _strokeHover   = Color.FromArgb("#9ca3af");
    private static readonly Color _arrowDefault  = Color.FromArgb("#9ca3af");
    private static readonly Color _arrowHover    = Color.FromArgb("#374151");

    public HomePage()
    {
        InitializeComponent();
    }

    // ── Responsive card sizing ────────────────────────────────────────────────

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        if (width <= 0 || height <= 0) return;

        // Three cards across: 2 gaps of 24px + ~120px side margins.
        var cardWidth  = Math.Clamp((width - 168) / 3, 200, 420);
        var cardHeight = Math.Clamp(height * 0.46,      200, 480);
        var padH       = Math.Clamp(cardWidth  * 0.13,   28,  60);
        var padV       = Math.Clamp(cardHeight * 0.11,   24,  52);

        // Scale font sizes relative to card width (baseline card = 280px).
        var scale      = cardWidth / 280.0;
        var iconSize   = Math.Clamp(44 * scale, 36, 88);
        var titleSize  = Math.Clamp(16 * scale, 14, 30);
        var descSize   = Math.Clamp(12 * scale, 11, 22);
        var arrowSize  = Math.Clamp(12 * scale, 11, 20);
        var spacing    = Math.Clamp(14 * scale, 10, 26);

        var padding = new Thickness(padH, padV);

        StationsCard.WidthRequest  = cardWidth;
        StationsCard.HeightRequest = cardHeight;
        StationsCard.Padding       = padding;
        StationsStack.Spacing      = spacing;
        StationsIcon.FontSize      = iconSize;
        StationsTitle.FontSize     = titleSize;
        StationsDesc.FontSize      = descSize;
        StationsArrow.FontSize     = arrowSize;

        OrdersCard.WidthRequest    = cardWidth;
        OrdersCard.HeightRequest   = cardHeight;
        OrdersCard.Padding         = padding;
        OrdersStack.Spacing        = spacing;
        OrdersIcon.FontSize        = iconSize;
        OrdersTitle.FontSize       = titleSize;
        OrdersDesc.FontSize        = descSize;
        OrdersArrow.FontSize       = arrowSize;

        PackCard.WidthRequest      = cardWidth;
        PackCard.HeightRequest     = cardHeight;
        PackCard.Padding           = padding;
        PackStack.Spacing          = spacing;
        PackIcon.FontSize          = iconSize;
        PackTitle.FontSize         = titleSize;
        PackDesc.FontSize          = descSize;
        PackArrow.FontSize         = arrowSize;
    }

    // ── Navigation ───────────────────────────────────────────────────────────

    private async void OnGoToStations(object sender, TappedEventArgs e)
        => await Shell.Current.GoToAsync("//main");

    private async void OnGoToOrders(object sender, TappedEventArgs e)
        => await Shell.Current.GoToAsync("//orders");

    private async void OnGoToPack(object sender, TappedEventArgs e)
        => await Shell.Current.GoToAsync("//packstation");

    private async void OnOpenSettings(object sender, EventArgs e)
        => await Navigation.PushModalAsync(new SettingsPage());

    // ── Packing Stations card hover ──────────────────────────────────────────

    private void OnStationsCardEntered(object sender, PointerEventArgs e)
        => ApplyHover(StationsCard, StationsArrow, true);

    private void OnStationsCardExited(object sender, PointerEventArgs e)
        => ApplyHover(StationsCard, StationsArrow, false);

    private void OnStationsCardPressed(object sender, PointerEventArgs e)
        => StationsCard.BackgroundColor = _cardPressed;

    private void OnStationsCardReleased(object sender, PointerEventArgs e)
        => StationsCard.BackgroundColor = _cardHover;

    // ── Order Search card hover ──────────────────────────────────────────────

    private void OnOrdersCardEntered(object sender, PointerEventArgs e)
        => ApplyHover(OrdersCard, OrdersArrow, true);

    private void OnOrdersCardExited(object sender, PointerEventArgs e)
        => ApplyHover(OrdersCard, OrdersArrow, false);

    private void OnOrdersCardPressed(object sender, PointerEventArgs e)
        => OrdersCard.BackgroundColor = _cardPressed;

    private void OnOrdersCardReleased(object sender, PointerEventArgs e)
        => OrdersCard.BackgroundColor = _cardHover;

    // ── Pack Station card hover ──────────────────────────────────────────────

    private void OnPackCardEntered(object sender, PointerEventArgs e)
        => ApplyHover(PackCard, PackArrow, true);

    private void OnPackCardExited(object sender, PointerEventArgs e)
        => ApplyHover(PackCard, PackArrow, false);

    private void OnPackCardPressed(object sender, PointerEventArgs e)
        => PackCard.BackgroundColor = _cardPressed;

    private void OnPackCardReleased(object sender, PointerEventArgs e)
        => PackCard.BackgroundColor = _cardHover;

    // ── Shared helper ────────────────────────────────────────────────────────

    private void ApplyHover(Border card, Label arrow, bool hovered)
    {
        card.BackgroundColor = hovered ? _cardHover : _cardDefault;
        card.Stroke          = new SolidColorBrush(hovered ? _strokeHover : _strokeDefault);
        arrow.TextColor      = hovered ? _arrowHover : _arrowDefault;
    }
}
