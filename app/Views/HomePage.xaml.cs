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

    // ── Navigation ───────────────────────────────────────────────────────────

    private async void OnGoToStations(object sender, TappedEventArgs e)
        => await Shell.Current.GoToAsync("//main");

    private async void OnGoToOrders(object sender, TappedEventArgs e)
        => await Shell.Current.GoToAsync("//orders");

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

    // ── Shared helper ────────────────────────────────────────────────────────

    private void ApplyHover(Border card, Label arrow, bool hovered)
    {
        card.BackgroundColor = hovered ? _cardHover : _cardDefault;
        card.Stroke          = new SolidColorBrush(hovered ? _strokeHover : _strokeDefault);
        arrow.TextColor      = hovered ? _arrowHover : _arrowDefault;
    }
}
