using app.Models;
using app.Services;
using Microsoft.Maui.Controls.Shapes;
using System.Runtime.Versioning;

namespace app.Views;

[SupportedOSPlatform("windows")]
public partial class PackStationPage
{
    private const int HistoryMaxItems = 40;

    private readonly List<ShipScan> _history = [];
    private int _scanSeq;

    // ── Public hooks (called from the scan / login flow) ──────────────────────────

    private void AddScanToHistory(PackingList? match, string tracking, PackOutcome outcome)
    {
        _scanSeq++;
        _history.Insert(0, new ShipScan(_scanSeq, tracking, match?.Platform, match?.ShippingOptions, outcome));
        if (_history.Count > HistoryMaxItems)
            _history.RemoveRange(HistoryMaxItems, _history.Count - HistoryMaxItems);
        RebuildHistoryUI();
    }

    private void ClearHistory()
    {
        _history.Clear();
        _scanSeq = 0;
        RebuildHistoryUI();
    }

    private void ShowHistoryBelt() => HistoryBelt.IsVisible = true;
    private void HideHistoryBelt() => HistoryBelt.IsVisible = false;

    // ── UI building ───────────────────────────────────────────────────────────────

    private void RebuildHistoryUI()
    {
        HistoryStrip.Children.Clear();

        if (_history.Count == 0)
        {
            HistoryStrip.Children.Add(new Label
            {
                Text = "Scanned parcels appear here",
                FontSize = 11,
                TextColor = Color.FromArgb("#9ca3af"),
                VerticalOptions = LayoutOptions.Center,
                Margin = new Thickness(8, 0),
            });
        }
        else
        {
            foreach (var e in _history)
                HistoryStrip.Children.Add(BuildHistoryCard(e));
        }

        UpdateBreakdowns();
    }

    // Platform + carrier tallies — seal-only counts (mirrors the persisted belt display).
    private void UpdateBreakdowns()
    {
        PackedTotalLabel.Text = ShippingHistory.SealedCount(_history).ToString();

        var (shopee, lazada, tiktok) = ShippingHistory.PlatformTally(_history);
        ShopeeCountLabel.Text = shopee.ToString();
        LazadaCountLabel.Text = lazada.ToString();
        TiktokCountLabel.Text = tiktok.ToString();

        CarrierPillStrip.Children.Clear();
        var carriers = ShippingHistory.CarrierTally(_history);
        if (carriers.Count == 0)
        {
            CarrierPillStrip.Children.Add(new Label
            {
                Text = "—",
                FontSize = 11,
                TextColor = Color.FromArgb("#9ca3af"),
                VerticalOptions = LayoutOptions.Center,
            });
        }
        else
        {
            foreach (var (carrier, count) in carriers)
                CarrierPillStrip.Children.Add(BuildCarrierPill(carrier, count));
        }
    }

    // Card background / stroke / foreground / glyph, derived from the scan verdict.
    private static (Color Bg, Color Stroke, Color Fg, string Glyph) StatusStyle(PackOutcome o) => o switch
    {
        PackOutcome.Pack          => (Color.FromArgb("#dcfce7"), Color.FromArgb("#86efac"), Color.FromArgb("#166534"), "✓"),
        PackOutcome.AlreadyPacked => (Color.FromArgb("#dcfce7"), Color.FromArgb("#86efac"), Color.FromArgb("#166534"), "↻"),
        PackOutcome.Blocked       => (Color.FromArgb("#fef3c7"), Color.FromArgb("#fcd34d"), Color.FromArgb("#92400e"), "!"),
        PackOutcome.Cancelled     => (Color.FromArgb("#fee2e2"), Color.FromArgb("#fca5a5"), Color.FromArgb("#991b1b"), "✕"),
        PackOutcome.NotFound      => (Color.FromArgb("#fee2e2"), Color.FromArgb("#fca5a5"), Color.FromArgb("#991b1b"), "?"),
        _                         => (Color.FromArgb("#fee2e2"), Color.FromArgb("#fca5a5"), Color.FromArgb("#991b1b"), "!"),
    };

    private static (Color Color, string? Name) PlatformBadge(string? platform) =>
        ShippingHistory.PlatformKey(platform) switch
        {
            "Shopee" => (Color.FromArgb("#EE4D2D"), "Shopee"),
            "Lazada" => (Color.FromArgb("#0F146D"), "Lazada"),
            "TikTok" => (Color.FromArgb("#000000"), "TikTok"),
            _        => (Colors.Transparent, null),
        };

    // [#Seq] [platform badge] [tracking] [carrier token] [status glyph], coloured by status.
    private static View BuildHistoryCard(ShipScan e)
    {
        var (bg, stroke, fg, glyph) = StatusStyle(e.Outcome);
        var (platColor, platName) = PlatformBadge(e.Platform);

        var row = new HorizontalStackLayout { Spacing = 6, VerticalOptions = LayoutOptions.Center };

        row.Children.Add(new Label
        {
            Text = $"#{e.Seq}",
            FontSize = 11,
            FontAttributes = FontAttributes.Bold,
            TextColor = fg,
            VerticalOptions = LayoutOptions.Center,
        });

        if (platName != null)
        {
            row.Children.Add(new Border
            {
                BackgroundColor = platColor,
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(2) },
                Padding = new Thickness(4, 1),
                HeightRequest = 16,
                VerticalOptions = LayoutOptions.Center,
                Content = new Label
                {
                    Text = platName,
                    FontSize = 8,
                    FontAttributes = FontAttributes.Bold,
                    TextColor = Colors.White,
                    VerticalOptions = LayoutOptions.Center,
                },
            });
        }

        row.Children.Add(new Label
        {
            Text = e.Tracking,
            FontSize = 11,
            FontAttributes = FontAttributes.Bold,
            TextColor = fg,
            LineBreakMode = LineBreakMode.NoWrap,
            VerticalOptions = LayoutOptions.Center,
        });

        var carrier = ShippingHistory.CarrierToken(e.Shipping);
        if (carrier != null)
        {
            row.Children.Add(new Label
            {
                Text = carrier,
                FontSize = 9,
                FontAttributes = FontAttributes.Bold,
                TextColor = Color.FromArgb("#475569"),
                BackgroundColor = Color.FromArgb("#f1f5f9"),
                Padding = new Thickness(4, 1),
                VerticalOptions = LayoutOptions.Center,
            });
        }

        row.Children.Add(new Label
        {
            Text = glyph,
            FontSize = 10,
            FontAttributes = FontAttributes.Bold,
            TextColor = fg,
            VerticalOptions = LayoutOptions.Center,
        });

        return new Border
        {
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(9) },
            Stroke = stroke,
            StrokeThickness = 1,
            BackgroundColor = bg,
            Padding = new Thickness(8, 4),
            VerticalOptions = LayoutOptions.Center,
            Content = row,
        };
    }

    // [●] [count] [carrier] pill for the persisted carrier breakdown.
    private static View BuildCarrierPill(string carrier, int count)
    {
        var row = new HorizontalStackLayout { Spacing = 6, VerticalOptions = LayoutOptions.Center };
        row.Children.Add(new BoxView { WidthRequest = 8, HeightRequest = 8, Color = Color.FromArgb("#0ea5e9"), CornerRadius = 4, VerticalOptions = LayoutOptions.Center });
        row.Children.Add(new Label { Text = count.ToString(), FontSize = 13, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#0369a1"), VerticalOptions = LayoutOptions.Center });
        row.Children.Add(new Label { Text = carrier, FontSize = 11, TextColor = Color.FromArgb("#374151"), VerticalOptions = LayoutOptions.Center });

        return new Border
        {
            BackgroundColor = Color.FromArgb("#f0f9ff"),
            Stroke = Color.FromArgb("#bae6fd"),
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(7) },
            Padding = new Thickness(8, 4),
            VerticalOptions = LayoutOptions.Center,
            Content = row,
        };
    }
}
