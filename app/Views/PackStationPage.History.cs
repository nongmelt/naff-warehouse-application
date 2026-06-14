using app.Models;
using Microsoft.Maui.Controls.Shapes;
using System.Runtime.Versioning;

namespace app.Views;

[SupportedOSPlatform("windows")]
public partial class PackStationPage
{
    private const int HistoryMaxItems = 40;

    // One belt entry per parcel scan this session. Card design mirrors the Order Search
    // carousel card exactly; colour is driven by the parcel's platform.
    private readonly record struct ScanEntry(
        int Seq,
        string Tracking,
        string? Platform,
        string? Shipping);

    private readonly List<ScanEntry> _history = [];
    private int _scanSeq;

    // ── Public hooks (called from the scan / login flow) ──────────────────────────

    private void AddScanToHistory(PackingList? match, string tracking)
    {
        _scanSeq++;
        _history.Insert(0, new ScanEntry(_scanSeq, tracking, match?.Platform, match?.ShippingOptions));
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

        UpdatePlatformCounts();
    }

    // Platform tallies — always shown, including zeros (mirrors Order Search badges).
    private void UpdatePlatformCounts()
    {
        ShopeeCountLabel.Text = _history.Count(h => PlatformTag(h.Platform).Name == "Shopee").ToString();
        LazadaCountLabel.Text = _history.Count(h => PlatformTag(h.Platform).Name == "Lazada").ToString();
        TiktokCountLabel.Text = _history.Count(h => PlatformTag(h.Platform).Name == "TikTok").ToString();
    }

    // Mirrors OrderSearchPage.Carousel BuildCarouselUI card: [#Seq] [platform badge]
    // [tracking] [carrier], coloured by the platform's primary colour.
    private static View BuildHistoryCard(ScanEntry e)
    {
        var (platName, platColor) = PlatformTag(e.Platform);
        var (bg, stroke, title, idx) = PlatformTints(platName);

        var row = new HorizontalStackLayout { Spacing = 6, VerticalOptions = LayoutOptions.Center };

        row.Children.Add(new Label
        {
            Text = $"#{e.Seq}",
            FontSize = 11,
            FontAttributes = FontAttributes.Bold,
            TextColor = idx,
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
            TextColor = title,
            LineBreakMode = LineBreakMode.NoWrap,
            VerticalOptions = LayoutOptions.Center,
        });

        var carrier = ShortCarrier(e.Shipping);
        if (carrier != null)
        {
            row.Children.Add(new Label
            {
                Text = carrier,
                FontSize = 9,
                FontAttributes = FontAttributes.Bold,
                TextColor = Color.FromArgb("#6b7280"),
                BackgroundColor = Color.FromArgb("#f3f4f6"),
                Padding = new Thickness(4, 1),
                VerticalOptions = LayoutOptions.Center,
            });
        }

        return new Border
        {
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(4) },
            Stroke = stroke,
            StrokeThickness = 1,
            BackgroundColor = bg,
            Padding = new Thickness(8, 4),
            VerticalOptions = LayoutOptions.Center,
            Content = row,
        };
    }

    // ── Pure helpers ──────────────────────────────────────────────────────────────

    private static (string? Name, Color Color) PlatformTag(string? platform)
    {
        var p = (platform ?? "").ToLowerInvariant();
        if (p.Contains("shopee")) return ("Shopee", Color.FromArgb("#EE4D2D"));
        if (p.Contains("lazada")) return ("Lazada", Color.FromArgb("#0F146D"));
        if (p.Contains("tiktok")) return ("TikTok", Color.FromArgb("#000000"));
        return (null, Colors.Transparent);
    }

    // Card bg / stroke / tracking-title / index colours derived from the platform.
    private static (Color Bg, Color Stroke, Color Title, Color Idx) PlatformTints(string? platName) => platName switch
    {
        "Shopee" => (Color.FromArgb("#fff1ed"), Color.FromArgb("#EE4D2D"), Color.FromArgb("#c2410c"), Color.FromArgb("#f59e7d")),
        "Lazada" => (Color.FromArgb("#eef0fb"), Color.FromArgb("#0F146D"), Color.FromArgb("#0F146D"), Color.FromArgb("#9aa3e0")),
        "TikTok" => (Color.FromArgb("#f3f4f6"), Color.FromArgb("#111827"), Color.FromArgb("#111827"), Color.FromArgb("#6b7280")),
        _ => (Color.FromArgb("#f9fafb"), Color.FromArgb("#e5e7eb"), Color.FromArgb("#6b7280"), Color.FromArgb("#9ca3af")),
    };

    // Collapse the verbose shipping_options string to a short carrier keyword.
    private static string? ShortCarrier(string? shipping)
    {
        if (string.IsNullOrWhiteSpace(shipping)) return null;
        var s = shipping.ToUpperInvariant();
        if (s.Contains("J&T")) return "J&T";
        if (s.Contains("SPX")) return "SPX";
        if (s.Contains("FLASH")) return "Flash";
        if (s.Contains("LEX")) return "LEX";
        if (s.Contains("KERRY")) return "Kerry";
        if (s.Contains("DHL")) return "DHL";
        if (s.Contains("NINJA")) return "Ninja";
        var w = shipping.Trim().Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries);
        return w.Length > 0 ? w[0] : null;
    }
}
