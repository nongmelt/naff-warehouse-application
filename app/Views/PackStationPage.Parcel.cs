using app.Models;
using app.Services;
using Microsoft.Maui.Controls.Shapes;
using System.Runtime.Versioning;

namespace app.Views;

[SupportedOSPlatform("windows")]
public partial class PackStationPage
{
    // Show the just-scanned parcel: verdict banner + carrier + contents (with images).
    private void ShowParcelPanel(PackingList? match, PackVerdictResult verdict, string? packerName)
    {
        // A logout / inactivity timeout can land between the scan's awaits; if nobody is
        // logged in, don't surface the panel underneath the login overlay.
        if (_currentOperator is null) return;

        IdlePrompt.IsVisible = false;
        ParcelPanel.IsVisible = true;

        var green = verdict.Outcome is PackOutcome.Ship or PackOutcome.AlreadyShipped;

        ParcelBanner.BackgroundColor = Color.FromArgb(verdict.Color);
        ParcelGlyph.Text = verdict.Glyph;
        ParcelWord.Text = verdict.Word;
        // Parcel title (ORDER {num} · platform · tracking) lives in the page header now, mirroring Order Search.
        var orderNo = match?.OrderNumber;
        var (platColor, platName) = PlatformBadge(match?.Platform);
        HeaderParcelInfo.IsVisible = match is not null;
        HeaderParcelOrderLabel.IsVisible = !string.IsNullOrWhiteSpace(orderNo);
        HeaderParcelOrderNumber.Text = orderNo ?? "";
        HeaderParcelPlatformBadge.IsVisible = platName != null;
        HeaderParcelPlatformLabel.Text = platName ?? "";
        if (platName != null) HeaderParcelPlatformBadge.BackgroundColor = platColor;  // platform accent (mirrors Order Search)
        HeaderParcelTracking.Text = match?.TrackingNumber ?? "";

        // shipping_options as a pill on the banner (carrier token removed).
        var shipping = match?.ShippingOptions;
        ParcelShippingPill.IsVisible = !string.IsNullOrWhiteSpace(shipping);
        ParcelShippingLabel.Text = shipping ?? "";

        ParcelByLabel.Text = verdict.Outcome == PackOutcome.Ship
            ? (packerName is { } fn ? $"by {fn}" : "")
            : verdict.Sub;

        if (match is null)
        {
            // Not found — empty state, no contents, no count.
            ParcelScroll.IsVisible = false;
            ParcelEmpty.IsVisible = true;
            ParcelCountBox.IsVisible = false;
            ParcelContentsStack.Children.Clear();
            return;
        }

        ParcelEmpty.IsVisible = false;
        ParcelScroll.IsVisible = true;

        var items = BuildParcelItems(match);
        var hasProgress = match.UpdatedProductLists?.Items is { Count: > 0 };
        ParcelContentsStack.Children.Clear();
        foreach (var item in items)
            ParcelContentsStack.Children.Add(BuildParcelCard(item, green, hasProgress));

        // Total = ordered units (RequiredQuantity); item.Quantity now holds remaining-to-pick.
        var units = items.Sum(i => i.RequiredQuantity);
        ParcelCountBox.IsVisible = true;
        ParcelCountLabel.Text = units.ToString();

        _ = EnrichParcelImagesAsync(items);
    }

    private void HideParcelPanel()
    {
        ParcelPanel.IsVisible = false;
        HeaderParcelInfo.IsVisible = false;
        ParcelContentsStack.Children.Clear();
        IdlePrompt.IsVisible = true;
    }

    // Fresh copies of the ordered line items so async enrichment never mutates the cached payload.
    private static List<ProductItem> BuildParcelItems(PackingList match)
    {
        // product_lists = ordered (what should be in the box). UpdatedProductLists holds
        // remaining-to-pick per SKU; verified = ordered − remaining. Carry both so the card
        // shows verified/ordered progress (e.g. 2/3), matching the dashboard.
        var ordered = (match.ProductLists?.Items is { Count: > 0 }
            ? match.ProductLists
            : match.UpdatedProductLists)?.Items ?? [];

        var remainingMap = new Dictionary<string, int>();
        if (match.UpdatedProductLists?.Items is { Count: > 0 } upd)
            foreach (var u in upd)
                remainingMap[u.SellerSku] = u.Quantity;

        var list = new List<ProductItem>();
        var row = 0;
        foreach (var p in ordered)
        {
            var required = p.Quantity;
            var remaining = remainingMap.TryGetValue(p.SellerSku, out var r) ? r : 0;
            list.Add(new ProductItem
            {
                Name = p.Name,
                Variation = p.Variation,
                SellerSku = p.SellerSku,
                Quantity = remaining,          // remaining-to-pick
                OriginalQuantity = required,
                RequiredQuantity = required,   // ordered
                RowNumber = ++row,
            });
        }
        return list;
    }

    // Compact card mirroring Order Search: [4px strip][96px image][name + variation][verified/ordered].
    private static View BuildParcelCard(ProductItem item, bool green, bool showProgress)
    {
        var bg      = green ? Color.FromArgb("#ECFDF5") : Color.FromArgb("#FFF7ED");
        var stroke  = green ? Color.FromArgb("#86efac") : Color.FromArgb("#fdba74");
        var fg      = green ? Color.FromArgb("#166534") : Color.FromArgb("#9a3412");
        var badgeBg = green ? Color.FromArgb("#dcfce7") : Color.FromArgb("#ffedd5");

        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            },
            Padding = new Thickness(0, 6, 14, 6),
            ColumnSpacing = 0,
        };

        grid.Add(new BoxView { WidthRequest = 4, Color = stroke, VerticalOptions = LayoutOptions.Fill }, 0, 0);

        grid.Add(BuildParcelImage(item), 1, 0);

        var info = new VerticalStackLayout { Spacing = 5, Padding = new Thickness(6, 0), VerticalOptions = LayoutOptions.Center };
        info.Children.Add(new Label
        {
            Text = item.BaseName,
            FontSize = 15,
            FontAttributes = FontAttributes.Bold,
            TextColor = fg,
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 1,
        });
        if (item.HasVariation)
        {
            info.Children.Add(new Border
            {
                BackgroundColor = badgeBg,
                Stroke = stroke,
                StrokeThickness = 1,
                StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(6) },
                Padding = new Thickness(8, 3),
                HorizontalOptions = LayoutOptions.Start,
                Content = new Label { Text = item.Variation, FontSize = 12, FontAttributes = FontAttributes.Bold, TextColor = fg },
            });
        }
        grid.Add(info, 2, 0);

        grid.Add(new Border
        {
            BackgroundColor = badgeBg,
            Stroke = stroke,
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(7) },
            Padding = new Thickness(12, 6),
            VerticalOptions = LayoutOptions.Center,
            Content = new Label { Text = showProgress ? $"{item.VerifiedQuantity}/{item.RequiredQuantity}" : $"×{item.RequiredQuantity}", FontSize = 15, FontAttributes = FontAttributes.Bold, TextColor = fg, FontFamily = "Consolas" },
        }, 3, 0);

        return new Border
        {
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(10) },
            Stroke = stroke,
            StrokeThickness = 1,
            BackgroundColor = bg,
            Padding = 0,
            Content = grid,
        };
    }

    // Two stacked images bound to the item so async-loaded thumbnails appear without a rebuild.
    private static View BuildParcelImage(ProductItem item)
    {
        var wrap = new Grid { Padding = new Thickness(8, 0), BindingContext = item, VerticalOptions = LayoutOptions.Center };

        var remote = new Image { Aspect = Aspect.AspectFit, WidthRequest = 96, HeightRequest = 96 };
        remote.SetBinding(Image.SourceProperty, new Binding(nameof(ProductItem.ImageSource)));
        remote.SetBinding(VisualElement.IsVisibleProperty, new Binding(nameof(ProductItem.HasNoLocalImage)));

        var local = new Image { Aspect = Aspect.AspectFit, WidthRequest = 96, HeightRequest = 96 };
        local.SetBinding(Image.SourceProperty, new Binding(nameof(ProductItem.LocalImagePath)));
        local.SetBinding(VisualElement.IsVisibleProperty, new Binding(nameof(ProductItem.HasLocalImage)));

        wrap.Children.Add(remote);
        wrap.Children.Add(local);
        return wrap;
    }

    // Enrich the parcel's products and stream thumbnails into the bound cards (fire-and-forget).
    private static async Task EnrichParcelImagesAsync(List<ProductItem> items)
    {
        if (items.Count == 0) return;

        var skus = items.Select(i => i.SellerSku).Distinct().ToList();
        Dictionary<string, ProductEnrichment> enrich;
        try { enrich = await ApiService.EnrichProductsAsync(skus); }
        catch (Exception ex) { Logger.Log($"PackStation: enrich failed — {ex.Message}"); return; }
        if (enrich.Count == 0) return;

        var byAnySku = new Dictionary<string, ProductEnrichment>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in enrich.Values)
            foreach (var s in e.AllSkus)
                byAnySku.TryAdd(s, e);

        var apiBase = AppSettings.ApiUrl ?? "http://localhost:8080";
        foreach (var item in items)
        {
            if (!byAnySku.TryGetValue(item.SellerSku, out var e)) continue;
            item.ProductId = e.Id;
            item.ProductVersion = e.Version;
            item.ImagePath = e.ImagePath;
            if (string.IsNullOrEmpty(e.ImagePath)) continue;

            var captured = item;
            _ = Task.Run(async () =>
            {
                try
                {
                    var path = await ProductImageCache.EnsureAsync(
                        captured.SellerSku, apiBase, captured.ProductId, captured.ProductVersion);
                    if (path != null)
                        MainThread.BeginInvokeOnMainThread(() => captured.LocalImagePath = path);
                }
                catch (Exception ex)
                {
                    Logger.Log($"PackStation: image failed ({captured.SellerSku}) — {ex.Message}");
                }
            });
        }
    }
}
