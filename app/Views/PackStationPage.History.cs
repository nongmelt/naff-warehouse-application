using app.Models;
using app.Services;
using Microsoft.Maui.Controls.Shapes;
using System.Linq;
using System.Runtime.Versioning;

namespace app.Views;

[SupportedOSPlatform("windows")]
public partial class PackStationPage
{
    // Belt display cap follows the operator-facing "Search History Limit" setting (the only
    // history limit the app exposes), read live so a Settings change takes effect on next scan.
    private static int HistoryMaxItems => AppSettings.SearchHistoryMaxItems;

    // One belt card = the scan summary + the full parcel it matched at scan time. Caching the
    // PackingList lets arrow/tap navigation re-open a card instantly with no re-fetch, mirroring
    // Order Search's cached-session carousel. ShipScan stays a pure (MAUI-free) value type.
    private sealed record BeltCard(ShipScan Scan, PackingList? Match);

    // Capped display belt — the most-recent scans shown as cards (any outcome).
    private readonly List<BeltCard> _belt = [];
    // Uncapped seals THIS session (Pack only) — drives the headline + platform + carrier counts.
    private readonly List<ShipScan> _sessionSeals = [];
    // DB-seeded seals for today at this station (platform only; shipping null) — set once per login.
    private readonly List<ShipScan> _seedSeals = [];
    private int _scanSeq;

    // Seq of the belt card currently shown in the parcel panel (-1 = none). Tracked by Seq, not
    // index, so a live scan inserting at the front never desyncs the highlight from its card.
    private int _historyActiveSeq = -1;

    // Every seal that counts toward the breakdowns: today's DB seed + this session's live seals.
    // Uncapped on purpose — only the display belt is capped.
    private List<ShipScan> AllSeals() => _seedSeals.Concat(_sessionSeals).ToList();

    // ── Public hooks (called from the scan / login flow) ──────────────────────────

    private void AddScanToHistory(PackingList? match, string tracking, PackOutcome outcome)
    {
        _scanSeq++;
        var scan = new ShipScan(_scanSeq, tracking, match?.Platform, match?.ShippingOptions, outcome);

        // Display belt: newest first, capped — old cards scroll off but never affect the counts.
        _belt.Insert(0, new BeltCard(scan, match));
        if (_belt.Count > HistoryMaxItems)
        {
            // At capacity: drop the oldest already-shipped ("passed") card first so blocked /
            // unresolved scans stay visible; fall back to the oldest card if every card passed.
            var evictIdx = _belt.FindLastIndex(c => c.Scan.Outcome is PackOutcome.Ship or PackOutcome.AlreadyShipped);
            if (evictIdx < 0) evictIdx = _belt.Count - 1;
            _belt.RemoveAt(evictIdx);
        }

        // A live scan takes over from any review-in-progress: the just-scanned parcel (already
        // shown by HandlePackScanAsync) becomes the active card, so the highlight resets to it.
        _historyActiveSeq = scan.Seq;

        // Counts: only real seals, kept uncapped for the whole session.
        if (ShippingHistory.IsSeal(outcome))
            _sessionSeals.Add(scan);

        RebuildHistoryUI();
    }

    private void ClearHistory()
    {
        _belt.Clear();
        _sessionSeals.Clear();
        _seedSeals.Clear();
        _scanSeq = 0;
        _historyActiveSeq = -1;
        RebuildHistoryUI();
    }

    // Open a belt card read-only from its cached parcel — no re-fetch, no ShipAsync, so reviewing
    // a past scan can never change state. The verdict is recomputed exactly as a live scan would,
    // so a reviewed parcel renders identically. Mirrors Order Search's cached-session navigation.
    private void OpenHistoryCard(BeltCard card)
    {
        var match = card.Match;
        var verdict = PackVerdict.Evaluate(
            found: match != null,
            cancelled: match?.IsCancelledOrder ?? false,
            packingStatus: match?.PackingStatus,
            allItemsCleared: match?.AllItemsCleared ?? false);
        ShowParcelPanel(match, verdict, null);
    }

    // Make a card the active (highlighted) card and open it read-only. The single entry point for
    // tap, ‹ › buttons and keyboard ← → — so all three behave identically.
    private void SelectHistoryCard(int seq)
    {
        var card = _belt.FirstOrDefault(c => c.Scan.Seq == seq);
        if (card is null) return;
        _historyActiveSeq = seq;
        RebuildHistoryUI();          // re-highlights + scrolls the active card into view
        OpenHistoryCard(card);
    }

    // Step the active card through the belt and open it. delta > 0 → older (rightward in the
    // newest-first strip); delta < 0 → newer. First press with nothing active selects the newest.
    private void NavigateHistory(int delta)
    {
        if (_belt.Count == 0) return;
        var cur = _belt.FindIndex(c => c.Scan.Seq == _historyActiveSeq);
        var next = cur < 0 ? 0 : Math.Clamp(cur + delta, 0, _belt.Count - 1);
        SelectHistoryCard(_belt[next].Scan.Seq);
    }

    private void ShowHistoryBelt() => HistoryBelt.IsVisible = true;
    private void HideHistoryBelt() => HistoryBelt.IsVisible = false;

    // ── Carousel navigation (‹ newer · › older) — keyboard ← → mirror these (OnWindowKeyDown) ──
    private void OnHistoryPrevTapped(object? sender, TappedEventArgs e) => NavigateHistory(-1);
    private void OnHistoryNextTapped(object? sender, TappedEventArgs e) => NavigateHistory(+1);

    // ── UI building ───────────────────────────────────────────────────────────────

    private void RebuildHistoryUI()
    {
        HistoryStrip.Children.Clear();

        if (_belt.Count == 0)
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
            View? activeCard = null;
            foreach (var entry in _belt)
            {
                var isActive = entry.Scan.Seq == _historyActiveSeq;
                var card = BuildHistoryCard(entry.Scan, isActive);
                var seq = entry.Scan.Seq;
                var tap = new TapGestureRecognizer();
                tap.Tapped += (_, _) => SelectHistoryCard(seq);   // OrderSearch pattern (event, not Command)
                card.GestureRecognizers.Add(tap);
                HistoryStrip.Children.Add(card);
                if (isActive) activeCard = card;
            }

            // Scroll the active card into view, mirroring Order Search's carousel (short delay so
            // the freshly-added card has a layout slot before ScrollTo measures it).
            if (activeCard is not null)
                _ = Dispatcher.DispatchAsync(async () =>
                {
                    await Task.Delay(80);
                    await HistoryScroll.ScrollToAsync(activeCard, ScrollToPosition.MakeVisible, animated: true);
                });
        }

        UpdateBreakdowns();
    }

    // Platform + carrier tallies — seal-only counts over the uncapped seal set
    // (today's DB seed + this session's live seals). Carriers come from live seals only,
    // since seeded rows have no shipping_options.
    private void UpdateBreakdowns()
    {
        var seals = AllSeals();

        PackedTotalLabel.Text = ShippingHistory.SealedCount(seals).ToString();

        var (shopee, lazada, tiktok) = ShippingHistory.PlatformTally(seals);
        ShopeeCountLabel.Text = shopee.ToString();
        LazadaCountLabel.Text = lazada.ToString();
        TiktokCountLabel.Text = tiktok.ToString();

        CarrierPillStrip.Children.Clear();
        var carriers = ShippingHistory.CarrierTally(seals);
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

    // Seed today's packed tallies for THIS station from the backend (reusing the list
    // endpoint), so the headline + platform counts reflect parcels already sealed today —
    // not just what this operator has scanned this session. Runs once per login; live seals
    // are layered on top via AddScanToHistory. Carrier pills are NOT seeded (the list
    // endpoint omits shipping_options) and so reflect this session only.
    private async Task SeedPackedTallyAsync(string badge)
    {
        if (_seedSeals.Count > 0) return; // already seeded this session

        var stationId = await AppSettings.EnsureStationIdAsync();
        if (stationId is null || _currentOperator != badge) return;

        var (total, platforms) = await ApiService.GetShippedTodayAsync(stationId.Value);

        // Bail if the operator logged out / changed, or another seed already ran, during the
        // awaits. (A parcel sealed by a live scan inside this window can briefly appear in both
        // the DB snapshot and _sessionSeals, over-counting by that parcel; it self-heals on the
        // next login via ClearHistory and is near-impossible in practice — no parcel scan occurs
        // in the sub-second between badge-in and seed completion.)
        if (_currentOperator != badge || _seedSeals.Count > 0) return;

        _seedSeals.AddRange(ShippingHistory.SeedScans(platforms));

        // The list endpoint caps returned rows at 1000; if more were packed today, pad with
        // platform-less seals so the headline still equals the true DB total.
        for (var i = _seedSeals.Count; i < total; i++)
            _seedSeals.Add(new ShipScan(0, "", null, null, PackOutcome.Ship));

        UpdateBreakdowns();
    }

    // Card background / stroke / foreground / glyph, derived from the scan verdict.
    private static (Color Bg, Color Stroke, Color Fg, string Glyph) StatusStyle(PackOutcome o) => o switch
    {
        PackOutcome.Ship           => (Color.FromArgb("#dcfce7"), Color.FromArgb("#86efac"), Color.FromArgb("#166534"), "✓"),
        PackOutcome.AlreadyShipped => (Color.FromArgb("#dcfce7"), Color.FromArgb("#86efac"), Color.FromArgb("#166534"), "↻"),
        PackOutcome.Blocked       => (Color.FromArgb("#fef3c7"), Color.FromArgb("#fcd34d"), Color.FromArgb("#92400e"), "!"),
        PackOutcome.Cancelled     => (Color.FromArgb("#fee2e2"), Color.FromArgb("#fca5a5"), Color.FromArgb("#991b1b"), "✕"),
        PackOutcome.NotFound      => (Color.FromArgb("#fee2e2"), Color.FromArgb("#fca5a5"), Color.FromArgb("#991b1b"), "?"),
        PackOutcome.SaveFailed    => (Color.FromArgb("#fee2e2"), Color.FromArgb("#fca5a5"), Color.FromArgb("#991b1b"), "!"),
        _                         => (Color.FromArgb("#fee2e2"), Color.FromArgb("#fca5a5"), Color.FromArgb("#991b1b"), "!"), // defensive: unmapped outcome -> red
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
    // The active (currently-reviewed) card gets a blue 2px ring, mirroring Order Search.
    private static View BuildHistoryCard(ShipScan e, bool isActive)
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
            Stroke = isActive ? Color.FromArgb("#2563eb") : stroke,
            StrokeThickness = isActive ? 2 : 1,
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
