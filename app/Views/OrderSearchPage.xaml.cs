using app.Helpers;
using app.Models;
using app.Services;
using app.Workflows;
using CommunityToolkit.Maui.Alerts;
using Microsoft.Maui.Controls.Shapes;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Ports;
using System.Net;
using System.Runtime.Versioning;

namespace app.Views;

[SupportedOSPlatform("windows")]
public partial class OrderSearchPage : ContentPage
{
    private enum AppMode { QC, Returns }

    private record ComPortEntry(string PortName, string DisplayName);

    private SerialPort? _serialPort;
    private string? _selectedPortName;
    private List<ComPortEntry> _comPorts = [];
    private bool _isSearching;
    private AppMode _currentMode = AppMode.QC;
    private IDispatcherTimer? _comHeartbeatTimer;
    private long _lastSerialDataTicks;
    private int _historyNavIndex = -1;

    // Returns mode state
    private string _returnsActionType = "return";
    private string? _selectedReturnReason;
    private int? _currentOperatorId;
    private int _returnsReturnedCount;
    private int _returnsPendingCount;
    private readonly Dictionary<int, string> _sessionReturnType = new();
    private readonly string[] _returnReasons = ["Customer request", "Damaged package", "Duplicate order", "Wrong product", "Other"];
    private readonly string[] _pickupReasons = ["Full capacity", "Carrier no-show", "Incomplete paperwork", "Other"];

    // Search-session navigation (back / forward through previous scans)
    private record SearchSession(string Query, List<PackingList> Data);
    private readonly List<SearchSession> _sessions = [];
    private int _sessionIndex = -1;
    private readonly Queue<string> _pendingScanQueue = new();
    private readonly Dictionary<string, List<ProductItem>> _altSkuMap = new(StringComparer.OrdinalIgnoreCase);
    private bool _carouselDirty;

    // Station identifier — computer name, resolved once
    private static readonly string StationName = Environment.MachineName;

    // Full barcode of the logged-in operator — null when no operator active
    private string? _currentOperator;

    // Resolved first name from API — null until lookup completes or no operator active
    private string? _currentOperatorFirstName;

    // Prevents OverlayComPortPicker ↔ ComPortPicker sync from firing recursively
    private bool _syncingPickers;

    // Inactivity auto-logout timer
    private IDispatcherTimer? _inactivityTimer;

    // Falls back to MachineName when no operator is logged in
    private string EffectiveOperator => _currentOperator ?? StationName;

    public string StationNameDisplay => $"Station: {StationName}";

    private PackingList? _currentOrder;
    public PackingList? CurrentOrder
    {
        get => _currentOrder;
        set
        {
            _currentOrder = value;
            OnPropertyChanged(nameof(CurrentOrder));
            OnPropertyChanged(nameof(HasCurrentOrder));
        }
    }
    public bool HasCurrentOrder => _currentOrder != null;

    // SKU picking state — set after an order loads; cleared on new search
    private bool _orderLoaded;
    private bool _isFirstItemScan;
    private ProductItem? _pendingSkuProduct;
    private BundleComponentItem? _activeComponent;
    private readonly HashSet<int> _completedPackingIds = [];

    // Orders that were "To be packed" when first scanned this session — the only ones counted in SessionCard
    private readonly HashSet<int> _qualifiedPackingIds = [];

    // Increments on every QC mutation within a scan session — shipped on workflow_events
    // so analytics can answer "which SKUs get scanned first on average?"
    private int _sequenceInSession;

    // Scan indicator state
    private string? _lastScanBarcode;
    private bool _lastScanFound;
    private DateTime? _lastScanTime;
    private IDispatcherTimer? _lastScanTimer;

    private int NextSequence() => ++_sequenceInSession;

    private string ConsumePickingFromState()
    {
        var s = _isFirstItemScan ? "order-loaded" : "picking";
        _isFirstItemScan = false;
        return s;
    }

    private void EmitQcEvent(
        string stepId,
        string trigger,
        string? trackingNumber,
        string? fromState,
        string? toState,
        Dictionary<string, object?>? payload = null,
        bool bumpSequence = true)
    {
        StationEvents.Emit(
            workflowName: "QC",
            stepId: stepId,
            trigger: trigger,
            trackingNumber: trackingNumber,
            fromState: fromState,
            toState: toState,
            stationId: AppSettings.ResolvedStationId,
            @operator: EffectiveOperator,
            sequenceInSession: bumpSequence ? NextSequence() : _sequenceInSession,
            payload: payload);
    }

    private string? CurrentTrackingNumber =>
        _sessionIndex >= 0 && _sessionIndex < _sessions.Count
            ? _sessions[_sessionIndex].Query
            : null;

    public ObservableCollection<PackingList> Results { get; } = new();
    public ObservableCollection<PackingList> ActiveResults { get; } = new();

    public OrderSearchPage()
    {
        InitializeComponent();
        BindingContext = this;
        ApplyMode(_currentMode);
        RefreshHistoryItems();
        UpdateHistoryHeader();

#if WINDOWS
        HeaderSearchEntry.HandlerChanged += (_, _) =>
        {
            if (HeaderSearchEntry.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.TextBox tb)
            {
                tb.VerticalContentAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center;
                tb.Padding = new Microsoft.UI.Xaml.Thickness(2, 0, 0, 0);
                tb.BorderThickness = new Microsoft.UI.Xaml.Thickness(0);
            }
        };
#endif
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    private bool _warmedUp;

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadComPortsAsync();
        if (!_warmedUp)
        {
            _warmedUp = true;
            _ = ApiService.TestConnectionAsync(); // establish TCP + DB pool in background
        }
#if WINDOWS
        RegisterKeyboardHandler();
#endif
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        CloseSerialPort();
        StopInactivityTimer();
#if WINDOWS
        UnregisterKeyboardHandler();
#endif
    }

    // ── Navigation ───────────────────────────────────────────────────────────

    private async void OnGoHome(object sender, EventArgs e)
        => await Shell.Current.GoToAsync("//home");

    private void OnOpenLogs(object sender, EventArgs e)
        => Process.Start("explorer.exe", FileSystem.AppDataDirectory);

    private async void OnOpenSettings(object sender, EventArgs e)
        => await Navigation.PushModalAsync(new SettingsPage());


    private static T? FindDescendant<T>(IVisualTreeElement root, Func<T, bool> predicate) where T : VisualElement
    {
        foreach (var child in root.GetVisualChildren())
        {
            if (child is T element && predicate(element)) return element;
            var found = FindDescendant(child, predicate);
            if (found != null) return found;
        }
        return null;
    }

    // ── Product card hover ───────────────────────────────────────────────────

    private static Color DarkenColor(Color c, float amount)
    {
        float r = Math.Max(0, c.Red - amount);
        float g = Math.Max(0, c.Green - amount);
        float b = Math.Max(0, c.Blue - amount);
        return new Color(r, g, b, c.Alpha);
    }


    // ── Carousel ──────────────────────────────────────────────────────────────

    private void BuildCarouselUI()
    {
        CarouselLayout.Children.Clear();
        var count = _sessions.Count;

        if (count == 0)
        {
            CarouselLayout.Children.Add(new Label
            {
                Text = "Scanned orders appear here",
                FontSize = 11,
                TextColor = Color.FromArgb("#9ca3af"),
                VerticalOptions = LayoutOptions.Center,
                Margin = new Thickness(8, 0),
            });
            return;
        }

        const int MaxVisibleCards = 25;
        var filteredIndices = new List<int>();
        for (int i = count - 1; i >= 0; i--)
        {
            var ss = ClassifySessionStatus(_sessions[i].Data);
            if (_carouselFilter is null || ss == _carouselFilter)
                filteredIndices.Add(i);
        }

        int activePos = filteredIndices.IndexOf(_sessionIndex);
        int winStart = 0, winEnd = Math.Min(MaxVisibleCards - 1, filteredIndices.Count - 1);
        if (activePos >= 0 && filteredIndices.Count > MaxVisibleCards)
        {
            winStart = Math.Max(0, activePos - MaxVisibleCards / 2);
            winEnd = Math.Min(filteredIndices.Count - 1, winStart + MaxVisibleCards - 1);
            winStart = Math.Max(0, winEnd - MaxVisibleCards + 1);
        }
        var visibleSet = new HashSet<int>(
            filteredIndices.Skip(winStart).Take(winEnd - winStart + 1));

        if (winStart > 0)
            CarouselLayout.Children.Add(new Label
            {
                Text = $"+{winStart}",
                FontSize = 10,
                TextColor = Color.FromArgb("#9ca3af"),
                VerticalOptions = LayoutOptions.Center,
                Margin = new Thickness(4, 0),
            });

        VisualElement? activeCard = null;
        for (int i = count - 1; i >= 0; i--)
        {
            if (!visibleSet.Contains(i)) continue;

            var capturedIdx = i;
            var isActive = i == _sessionIndex;
            var query = _sessions[i].Query;
            var sessionStatus = ClassifySessionStatus(_sessions[i].Data);

            Color bgInactive, bgHover, strokeInactive, titleInactive, idxInactive;

            // Returns mode: color by return type instead of QC status
            if (_currentMode == AppMode.Returns && _sessionReturnType.TryGetValue(i, out var retType))
            {
                if (retType == "return")
                {
                    bgInactive = Color.FromArgb("#fef2f2"); bgHover = Color.FromArgb("#fee2e2");
                    strokeInactive = Color.FromArgb("#fecaca"); titleInactive = Color.FromArgb("#991b1b"); idxInactive = Color.FromArgb("#fca5a5");
                }
                else
                {
                    bgInactive = Color.FromArgb("#fdf4ff"); bgHover = Color.FromArgb("#fae8ff");
                    strokeInactive = Color.FromArgb("#e9d5ff"); titleInactive = Color.FromArgb("#86198f"); idxInactive = Color.FromArgb("#d8b4fe");
                }
            }
            else if (sessionStatus == "preProcessed")
            {
                bgInactive = Color.FromArgb("#f9fafb"); bgHover = Color.FromArgb("#f3f4f6");
                strokeInactive = Color.FromArgb("#e5e7eb"); titleInactive = Color.FromArgb("#6b7280"); idxInactive = Color.FromArgb("#9ca3af");
            }
            else if (sessionStatus == "completed")
            {
                bgInactive = Color.FromArgb("#f0fdf4"); bgHover = Color.FromArgb("#dcfce7");
                strokeInactive = Color.FromArgb("#bbf7d0"); titleInactive = Color.FromArgb("#166534"); idxInactive = Color.FromArgb("#86efac");
            }
            else if (sessionStatus == "incomplete")
            {
                bgInactive = Color.FromArgb("#fffbeb"); bgHover = Color.FromArgb("#fef3c7");
                strokeInactive = Color.FromArgb("#fde68a"); titleInactive = Color.FromArgb("#b45309"); idxInactive = Color.FromArgb("#fcd34d");
            }
            else
            {
                bgInactive = Color.FromArgb("#eff6ff"); bgHover = Color.FromArgb("#dbeafe");
                strokeInactive = Color.FromArgb("#bfdbfe"); titleInactive = Color.FromArgb("#1d4ed8"); idxInactive = Color.FromArgb("#93c5fd");
            }

            Color bgColor = isActive ? Color.FromArgb("#f0fdf4") : bgInactive;
            Color strokeColor = isActive ? Color.FromArgb("#2563eb") : strokeInactive;
            Color titleColor = isActive ? Color.FromArgb("#111827") : titleInactive;
            Color indexColor = isActive ? Color.FromArgb("#2563eb") : idxInactive;

            var rawPlatform = _sessions[i].Data.FirstOrDefault()?.Platform ?? "";
            string? platformName = rawPlatform.ToLower() switch
            {
                var p when p.Contains("shopee") => "Shopee",
                var p when p.Contains("lazada") => "Lazada",
                var p when p.Contains("tiktok") => "TikTok",
                _ => null,
            };
            Color platformTagColor = platformName switch
            {
                "Shopee" => Color.FromArgb("#EE4D2D"),
                "Lazada" => Color.FromArgb("#0F146D"),
                "TikTok" => Color.FromArgb("#000000"),
                _ => Colors.Transparent,
            };

            // Single-line layout: [#Index] [Platform Badge] [Tracking Number]
            var row = new HorizontalStackLayout { Spacing = 6, VerticalOptions = LayoutOptions.Center };

            row.Children.Add(new Label
            {
                Text = $"#{i + 1}",
                FontSize = 11,
                FontAttributes = FontAttributes.Bold,
                TextColor = indexColor,
                VerticalOptions = LayoutOptions.Center,
            });

            if (platformName != null)
            {
                row.Children.Add(new Border
                {
                    BackgroundColor = platformTagColor,
                    StrokeThickness = 0,
                    StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(2) },
                    Padding = new Thickness(4, 1),
                    HeightRequest = 16,
                    VerticalOptions = LayoutOptions.Center,
                    Content = new Label
                    {
                        Text = platformName,
                        FontSize = 8,
                        FontAttributes = FontAttributes.Bold,
                        TextColor = Colors.White,
                        VerticalOptions = LayoutOptions.Center,
                    },
                });
            }

            row.Children.Add(new Label
            {
                Text = query,
                FontSize = 11,
                FontAttributes = FontAttributes.Bold,
                TextColor = titleColor,
                LineBreakMode = LineBreakMode.NoWrap,
                VerticalOptions = LayoutOptions.Center,
            });

            // Carrier label in Returns mode
            if (_currentMode == AppMode.Returns)
            {
                var carrierName = _sessions[i].Data.FirstOrDefault()?.ShippingOptions;
                if (!string.IsNullOrWhiteSpace(carrierName))
                {
                    row.Children.Add(new Label
                    {
                        Text = carrierName,
                        FontSize = 9,
                        FontAttributes = FontAttributes.Bold,
                        TextColor = Color.FromArgb("#6b7280"),
                        BackgroundColor = Color.FromArgb("#f3f4f6"),
                        Padding = new Thickness(4, 1),
                        VerticalOptions = LayoutOptions.Center,
                    });
                }
            }

            var card = new Border
            {
                StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(4) },
                Stroke = strokeColor,
                StrokeThickness = isActive ? 2 : 1,
                BackgroundColor = bgColor,
                Padding = new Thickness(8, 4),
                VerticalOptions = LayoutOptions.Center,
                Content = row,
            };

            if (!isActive)
            {
                var capturedBg = bgInactive;
                var capturedHover = bgHover;
                var ptr = new PointerGestureRecognizer();
                ptr.PointerEntered += (_, _) => card.BackgroundColor = capturedHover;
                ptr.PointerExited += (_, _) => card.BackgroundColor = capturedBg;
                card.GestureRecognizers.Add(ptr);
            }

            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => NavigateToSession(capturedIdx);
            card.GestureRecognizers.Add(tap);

            if (isActive) activeCard = card;
            CarouselLayout.Children.Add(card);
        }

        int olderOverflow = filteredIndices.Count - 1 - winEnd;
        if (olderOverflow > 0)
            CarouselLayout.Children.Add(new Label
            {
                Text = $"+{olderOverflow}",
                FontSize = 10,
                TextColor = Color.FromArgb("#9ca3af"),
                VerticalOptions = LayoutOptions.Center,
                Margin = new Thickness(4, 0),
            });

        if (activeCard is not null)
            _ = Dispatcher.DispatchAsync(async () =>
            {
                await Task.Delay(80);
                await NavRow.ScrollToAsync(activeCard, ScrollToPosition.MakeVisible, animated: true);
            });
    }

    private void FlushCarouselIfDirty()
    {
        if (!_carouselDirty) return;
        _carouselDirty = false;
        BuildCarouselUI();
        UpdateSessionStats();
    }

    private void OnCarouselPrevTapped(object? sender, TappedEventArgs e) => NavigateSession(+1);
    private void OnCarouselNextTapped(object? sender, TappedEventArgs e) => NavigateSession(-1);

    // Returns "preProcessed" | "completed" | "incomplete" | "incoming"
    private string ClassifySessionStatus(List<PackingList> data)
    {
        var qualified = data
            .Where(o => _qualifiedPackingIds.Contains(o.PackingId))
            .ToList();
        if (qualified.Count == 0)
            return "preProcessed";
        if (qualified.All(o => string.Equals(o.PackingStatus, "QC Passed", StringComparison.OrdinalIgnoreCase)))
            return "completed";
        if (qualified.Any(o => string.Equals(o.PackingStatus, "QC Hold", StringComparison.OrdinalIgnoreCase)))
            return "incomplete";
        return "incoming";
    }

    private async Task AnimateAllCarouselCardsAsync()
    {
        var children = CarouselLayout.Children.OfType<VisualElement>().ToList();
        if (children.Count == 0 || children[0] is not Border) return;

        children[0].TranslationX = -260;
        children[0].Opacity = 0;
        await SlideCardInAsync(children[0], 0);
    }

    private static async Task SlideCardInAsync(VisualElement card, int delayMs)
    {
        if (delayMs > 0) await Task.Delay(delayMs);
        await Task.WhenAll(
            card.TranslateToAsync(0, 0, 300, Easing.SinOut),
            card.FadeToAsync(1.0, 260, Easing.SinIn));
    }

    // ── Session badge filter ──────────────────────────────────────────────────

    private void OnSessionCompletedBadgeTapped(object sender, TappedEventArgs e)
    {
        _carouselFilter = _carouselFilter == "completed" ? null : "completed";
        UpdateBadgeFilterState();
        BuildCarouselUI();
    }

    private void OnSessionIncompleteBadgeTapped(object sender, TappedEventArgs e)
    {
        _carouselFilter = _carouselFilter == "incomplete" ? null : "incomplete";
        UpdateBadgeFilterState();
        BuildCarouselUI();
    }

    private void OnSessionIncomingBadgeTapped(object sender, TappedEventArgs e)
    {
        _carouselFilter = _carouselFilter == "incoming" ? null : "incoming";
        UpdateBadgeFilterState();
        BuildCarouselUI();
    }

    private void UpdateBadgeFilterState()
    {
        SessionCompletedBadge.Opacity = _carouselFilter is null or "completed" ? 1.0 : 0.4;
        SessionIncompleteBadge.Opacity = _carouselFilter is null or "incomplete" ? 1.0 : 0.4;
        SessionIncomingBadge.Opacity = _carouselFilter is null or "incoming" ? 1.0 : 0.4;
    }

    private void NavigateToSession(int index)
    {
        if (_sessions.Count == 0 || index == _sessionIndex) return;

        if (_pendingSkuProduct != null)
            ApplySkuDeduction(_pendingSkuProduct, "1", DeductionSource.AutoPrior);

        var prevSession = _sessions[_sessionIndex];
        string sourceState = GetSessionWorkflowState(prevSession.Data);
        bool hadIncompleteWork = prevSession.Data.Any(o =>
            _qualifiedPackingIds.Contains(o.PackingId) &&
            !string.Equals(o.PackingStatus, "QC Passed", StringComparison.OrdinalIgnoreCase) &&
            o.ParsedProducts.Any(p => p.Quantity != p.OriginalQuantity));

        // Detect order-loaded: no item scans done yet and all qualified orders still "To be packed"
        bool sourceInOrderLoaded = _isFirstItemScan && _orderLoaded &&
            prevSession.Data.Where(o => _qualifiedPackingIds.Contains(o.PackingId))
                            .All(o => string.Equals(o.PackingStatus, "To be packed", StringComparison.OrdinalIgnoreCase));
        string sourceFromState = sourceInOrderLoaded ? "order-loaded" : sourceState;
        string sourceToState = sourceInOrderLoaded ? "idle" : "held";
        string sourceStepId = sourceInOrderLoaded ? "order_abandoned"
            : hadIncompleteWork ? "incomplete_order_abandoned"
            : "order_held";

        _sessionIndex = index;
        var session = _sessions[_sessionIndex];
        string destFromState = GetSessionWorkflowState(session.Data);

        Results.Clear();
        ActiveResults.Clear();
        foreach (var r in session.Data) { Results.Add(r); ActiveResults.Add(r); }
        UpdateHeaderOrderInfo();

        foreach (var pl in Results)
        {
            if (pl.HasProducts && pl.ParsedProducts.Any(p => p.ProductId == 0))
                _ = EnrichProductItemsAsync(pl.ParsedProducts);
        }

        _orderLoaded = Results.Count > 0;
        _isFirstItemScan = _orderLoaded;
        if (Results.Count > 0)
        {
            var firstProduct = Results[0].ParsedProducts.FirstOrDefault();
            if (firstProduct != null) SetActiveProduct(firstProduct);
        }
        NotFoundCard.IsVisible = Results.Count == 0;
        NotFoundLabel.Text = Results.Count == 0 ? $"{session.Query} not found" : "";
        UpdateSearchStatus(session.Query);
        BuildCarouselUI();
        UpdateSessionStats();
        _ = ResultsScroll.ScrollToAsync(0, 0, false);

        // Emit source order leaving (skip if already idle or passed — nothing to record)
        if (!string.Equals(sourceState, "passed", StringComparison.Ordinal) &&
            !string.Equals(sourceState, "idle", StringComparison.Ordinal))
            EmitQcEvent(
                stepId: sourceStepId,
                trigger: "order_card_selected",
                trackingNumber: prevSession.Query,
                fromState: sourceFromState,
                toState: sourceToState,
                bumpSequence: false,
                payload: new Dictionary<string, object?>
                {
                    ["toTrackingNumber"] = session.Query,
                    ["hadIncompleteWork"] = hadIncompleteWork,
                });

        // Emit destination order loading
        if (!string.Equals(destFromState, "passed", StringComparison.Ordinal))
            EmitQcEvent(
                stepId: "session_navigated",
                trigger: "order_card_selected",
                trackingNumber: session.Query,
                fromState: destFromState,
                toState: "order-loaded",
                bumpSequence: false,
                payload: new Dictionary<string, object?>
                {
                    ["fromTrackingNumber"] = prevSession.Query,
                });
    }

    private string GetSessionWorkflowState(List<PackingList> orders)
    {
        var qualified = orders.Where(o => _qualifiedPackingIds.Contains(o.PackingId)).ToList();
        if (qualified.Count == 0) return "idle";
        if (qualified.All(o => string.Equals(o.PackingStatus, "QC Passed", StringComparison.OrdinalIgnoreCase)))
            return "passed";
        if (qualified.Any(o => string.Equals(o.PackingStatus, "QC Hold", StringComparison.OrdinalIgnoreCase)))
            return "held";
        return "picking";
    }

    private void NavigateSession(int delta)
    {
        if (_sessions.Count == 0) return;
        NavigateToSession(Math.Clamp(_sessionIndex + delta, 0, _sessions.Count - 1));
    }

#if WINDOWS
    private void RegisterKeyboardHandler()
    {
        if (Application.Current?.Windows is { Count: > 0 } wins &&
            wins[0].Handler?.PlatformView is Microsoft.UI.Xaml.Window w)
            // PreviewKeyDown (tunneling) fires before the ScrollViewer acts on arrow keys,
            // so e.Handled=true in the bundle-component nav block suppresses list scrolling.
            // Bubbling KeyDown ran too late (ScrollView already scrolled / marked handled).
            w.Content.PreviewKeyDown += OnWindowKeyDown;
    }

    private void UnregisterKeyboardHandler()
    {
        if (Application.Current?.Windows is { Count: > 0 } wins &&
            wins[0].Handler?.PlatformView is Microsoft.UI.Xaml.Window w)
            w.Content.PreviewKeyDown -= OnWindowKeyDown;
    }

    private void OnWindowKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        // Don't intercept while typing in any Entry / TextBox
        if (e.OriginalSource is Microsoft.UI.Xaml.Controls.TextBox tb)
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                if (PopupSearchBar.IsVisible)
                {
                    DismissPopupSearch();
                    e.Handled = true;
                }
                else if (ReferenceEquals(tb, HeaderSearchEntry.Handler?.PlatformView))
                {
                    tb.IsEnabled = false; tb.IsEnabled = true;
                    e.Handled = true;
                }
                else if (OverlayPickEntry.IsVisible && ReferenceEquals(tb, OverlayPickEntry.Handler?.PlatformView))
                {
                    HideOverlayPickEntry();
                    e.Handled = true;
                }
                else if (_pendingSkuProduct != null && _pendingSkuProduct.IsBeingPicked)
                {
                    _pendingSkuProduct.IsBeingPicked = false;
                    tb.IsEnabled = false; tb.IsEnabled = true;
                    e.Handled = true;
                }
            }
            return;
        }

        // "/" focuses search entry (command-palette pattern)
        const Windows.System.VirtualKey VkSlash = (Windows.System.VirtualKey)191;
        if (e.Key == VkSlash)
        {
            ActivateSearchEntry();
            e.Handled = true;
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Escape && ReturnSuccessOverlay.IsVisible)
        {
            _ = DismissReturnSuccessAsync();
            e.Handled = true;
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Escape && CompletionSummaryOverlay.IsVisible)
        {
            _ = DismissCompletionSummaryAsync();
            e.Handled = true;
            return;
        }

        // Returns mode keyboard shortcuts
        if (_currentMode == AppMode.Returns)
        {
            if (e.Key == Windows.System.VirtualKey.R)
            {
                OnSelectReturnType(null, EventArgs.Empty);
                e.Handled = true;
                return;
            }
            if (e.Key == Windows.System.VirtualKey.P)
            {
                OnSelectPendingPickupType(null, EventArgs.Empty);
                e.Handled = true;
                return;
            }
            if (e.Key == Windows.System.VirtualKey.Enter && _selectedReturnReason is not null)
            {
                OnConfirmReturn(null, EventArgs.Empty);
                e.Handled = true;
                return;
            }
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                OnSkipReturn(null, EventArgs.Empty);
                e.Handled = true;
                return;
            }
            if (e.Key >= Windows.System.VirtualKey.Number1 && e.Key <= Windows.System.VirtualKey.Number5)
            {
                var idx = (int)e.Key - (int)Windows.System.VirtualKey.Number1;
                var reasons = _returnsActionType == "return" ? _returnReasons : _pickupReasons;
                if (idx < reasons.Length)
                {
                    _selectedReturnReason = reasons[idx];
                    BuildReturnReasonChips();
                }
                e.Handled = true;
                return;
            }
        }

        // Enter → apply pending qty (entry always shows target verified count)
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            if (ProductImageOverlay.IsVisible && OverlayPickEntry.IsVisible && _overlayItem != null)
            {
                if (_overlayItem.IsBundle && _activeComponentIndex >= 0
                    && _overlayItem.BundleComponents is { } enterComps && _activeComponentIndex < enterComps.Count)
                {
                    ApplyComponentVerifiedOverride(_overlayItem, enterComps[_activeComponentIndex], OverlayPickEntry.Text);
                }
                else
                {
                    ApplyVerifiedOverride(_overlayItem, OverlayPickEntry.Text);
                    SyncOverlayAfterDeduction(_overlayItem);
                }
                HideOverlayPickEntry();
                e.Handled = true;
                return;
            }
        }

        // Number keys → activate qty field on active product (or active component in bundle overlay)
        if (e.Key >= Windows.System.VirtualKey.Number0 && e.Key <= Windows.System.VirtualKey.Number9
            || e.Key >= Windows.System.VirtualKey.NumberPad0 && e.Key <= Windows.System.VirtualKey.NumberPad9)
        {
            int digit = e.Key >= Windows.System.VirtualKey.NumberPad0
                ? (int)e.Key - (int)Windows.System.VirtualKey.NumberPad0
                : (int)e.Key - (int)Windows.System.VirtualKey.Number0;

            if (ProductImageOverlay.IsVisible && _overlayItem != null)
            {
                // Bundle with active component — allow number input if component not fully verified
                if (_overlayItem.IsBundle && _activeComponentIndex >= 0
                    && _overlayItem.BundleComponents is { } numComps && _activeComponentIndex < numComps.Count)
                {
                    var comp = numComps[_activeComponentIndex];
                    if (!comp.IsFullyVerified)
                    {
                        ShowOverlayPickEntry(digit.ToString());
                        e.Handled = true;
                        return;
                    }
                }
                else if (_overlayItem.Quantity > 0)
                {
                    ShowOverlayPickEntry(digit.ToString());
                    e.Handled = true;
                    return;
                }
            }
        }

        if (ProductImageOverlay.IsVisible && _overlayItem != null)
        {
            var overlayLeft = e.Key == Windows.System.VirtualKey.Left;
            var overlayRight = e.Key == Windows.System.VirtualKey.Right;
            if (overlayLeft || overlayRight)
            {
                NavigateOverlayProduct(overlayRight ? 1 : -1);
                e.Handled = true;
                return;
            }
        }

        // UP/DOWN for bundle component navigation
        if (ProductImageOverlay.IsVisible && _overlayItem != null && _overlayItem.IsBundle)
        {
            var overlayUp = e.Key == Windows.System.VirtualKey.Up;
            var overlayDown = e.Key == Windows.System.VirtualKey.Down;
            if (overlayUp || overlayDown)
            {
                var compCount = _overlayItem.BundleComponents?.Count ?? 0;
                if (overlayDown)
                {
                    if (_activeComponentIndex < compCount - 1)
                        ActivateOverlayComponent(_activeComponentIndex + 1);
                }
                else
                {
                    if (_activeComponentIndex > -1)
                        ActivateOverlayComponent(_activeComponentIndex - 1);
                }
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Windows.System.VirtualKey.Escape && ProductImageOverlay.IsVisible)
        {
            _ = DismissImageOverlayAsync("keyboard_escape"); e.Handled = true; return;
        }
        if (e.Key == Windows.System.VirtualKey.I && !ProductImageOverlay.IsVisible)
        {
            var target = _pendingSkuProduct
                ?? Results.SelectMany(o => o.ParsedProducts).FirstOrDefault(p => !p.IsFullyPicked);
            if (target != null) { ShowProductImageOverlay(target, "keyboard_i"); e.Handled = true; return; }
        }

        // +/- keys verify/unverify active product card
        const Windows.System.VirtualKey VkPlus = (Windows.System.VirtualKey)187;  // = / + key
        const Windows.System.VirtualKey VkMinus = (Windows.System.VirtualKey)189; // - / _ key
        var plusTarget = ProductImageOverlay.IsVisible ? _overlayItem : null;
        if ((e.Key == VkPlus || e.Key == Windows.System.VirtualKey.Add) && plusTarget != null)
        {
            if (ProductImageOverlay.IsVisible)
            {
                _ = AnimateOverlayBtnAsync(OverlayPlusBtn, "#7C5CF0", "#5B31E0");
                if (_overlayItem?.IsBundle == true && _activeComponentIndex >= 0
                    && _overlayItem.BundleComponents is { } compsPlus && _activeComponentIndex < compsPlus.Count)
                {
                    var comp = compsPlus[_activeComponentIndex];
                    if (!comp.IsFullyVerified)
                    {
                        var order = FindOrderForItem(_overlayItem);
                        if (order != null && !IsOrderQcPassed(order))
                        {
                            comp.VerifiedQuantity++;
                            _overlayItem.NotifyBundleProgressChanged();
                            if (_overlayItem.IsBundleFullyVerified && _overlayItem.Quantity > 0)
                                _overlayItem.Quantity = 0;

                            EmitQcEvent(
                                stepId: "component_card_clicked",
                                trigger: "keyboard_plus",
                                trackingNumber: order.TrackingNumber,
                                fromState: ConsumePickingFromState(),
                                toState: "picking",
                                payload: new Dictionary<string, object?>
                                {
                                    ["sku"] = comp.SellerSku,
                                    ["componentName"] = comp.Name,
                                    ["verified"] = comp.VerifiedQuantity,
                                    ["required"] = comp.RequiredQuantity,
                                    ["bundleSku"] = _overlayItem.SellerSku,
                                    ["bundleComplete"] = _overlayItem.IsBundleFullyVerified,
                                });

                            _ = CheckAndSaveQcStatusAsync();
                            if (comp.IsFullyVerified)
                                _ = AnimateOverlayComponentCompletion(_overlayItem, comp);
                            else
                                ShowBundleOverlay(_overlayItem, comp);
                        }
                    }
                }
                else
                {
                    DoOverlayPlus(DeductionSource.KeyboardPlus);
                }
            }
            e.Handled = true;
            return;
        }
        if ((e.Key == VkMinus || e.Key == Windows.System.VirtualKey.Subtract) && plusTarget != null)
        {
            if (ProductImageOverlay.IsVisible)
            {
                _ = AnimateOverlayBtnAsync(OverlayMinusBtn, "#fca5a5", "#ef4444");
                if (_overlayItem?.IsBundle == true && _activeComponentIndex >= 0
                    && _overlayItem.BundleComponents is { } compsMinus && _activeComponentIndex < compsMinus.Count)
                {
                    var comp = compsMinus[_activeComponentIndex];
                    if (!comp.IsFullyVerified && comp.VerifiedQuantity > 0)
                    {
                        var order = FindOrderForItem(_overlayItem);
                        if (order != null && !IsOrderQcPassed(order))
                        {
                            EmitQcEvent(
                                stepId: "component_card_unclicked",
                                trigger: "keyboard_minus",
                                trackingNumber: order.TrackingNumber,
                                fromState: ConsumePickingFromState(),
                                toState: "picking",
                                payload: new Dictionary<string, object?>
                                {
                                    ["sku"] = comp.SellerSku,
                                    ["componentName"] = comp.Name,
                                    ["qtyBefore"] = comp.VerifiedQuantity,
                                    ["qtyAfter"] = comp.VerifiedQuantity - 1,
                                    ["bundleSku"] = _overlayItem.SellerSku,
                                });

                            comp.VerifiedQuantity--;
                            _overlayItem.NotifyBundleProgressChanged();
                            if (!_overlayItem.IsBundleFullyVerified && _overlayItem.Quantity <= 0)
                                _overlayItem.Quantity = _overlayItem.RequiredQuantity;
                            _ = CheckAndSaveQcStatusAsync();
                            ShowBundleOverlay(_overlayItem, comp);
                        }
                    }
                }
                else
                {
                    DoOverlayMinus("keyboard_minus");
                }
            }
            e.Handled = true;
            return;
        }

        // Up/Down: navigate product cards when order loaded and overlay closed
        var isUp = e.Key == Windows.System.VirtualKey.Up;
        var isDown = e.Key == Windows.System.VirtualKey.Down;
        if ((isUp || isDown) && _orderLoaded && !ProductImageOverlay.IsVisible && Results.Count > 0)
        {
            var navList = new List<object>();
            foreach (var order in Results)
            {
                foreach (var p in order.ParsedProducts)
                {
                    navList.Add(p);
                    if (p.IsBundle && p.IsBundleExpanded && p.BundleComponents != null)
                    {
                        foreach (var c in p.BundleComponents)
                            navList.Add(c);
                    }
                }
            }

            if (navList.Count > 0)
            {
                object? current = _activeComponent != null ? (object)_activeComponent : _pendingSkuProduct;
                var currentIdx = current != null ? navList.IndexOf(current) : -1;

                int nextIdx;
                if (isDown)
                    nextIdx = currentIdx < navList.Count - 1 ? currentIdx + 1 : 0;
                else
                    nextIdx = currentIdx > 0 ? currentIdx - 1 : navList.Count - 1;

                var next = navList[nextIdx];
                if (next is ProductItem pi)
                {
                    SetActiveComponent(null);
                    SetActiveProduct(pi);
                    ScrollToProduct(pi);
                }
                else if (next is BundleComponentItem ci)
                {
                    SetActiveProduct(null);
                    SetActiveComponent(ci);
                    ScrollToComponent(ci);
                }
                e.Handled = true;
                return;
            }
        }

        // Left/Right: session navigation (carousel)
        var isLeft = e.Key == Windows.System.VirtualKey.Left;
        var isRight = e.Key == Windows.System.VirtualKey.Right;
        if (!isLeft && !isRight) return;

        if (_sessions.Count > 1)
        {
            if (isLeft) NavigateSession(+1);
            else        NavigateSession(-1);
            e.Handled = true;
            return;
        }

        // No sessions yet — cycle recent search queries into the search box
        var items = SearchHistoryService.Instance.Items;
        if (items.Count == 0) return;

        if (isLeft)
        {
            _historyNavIndex = Math.Min(_historyNavIndex + 1, items.Count - 1);
            HeaderSearchEntry.Text = items[_historyNavIndex];
            e.Handled = true;
        }
        else
        {
            if (_historyNavIndex > 0)
            {
                _historyNavIndex--;
                HeaderSearchEntry.Text = items[_historyNavIndex];
            }
            else
            {
                _historyNavIndex = -1;
                HeaderSearchEntry.Text = string.Empty;
            }
            e.Handled = true;
        }
    }
#endif

    // ── Session stats ─────────────────────────────────────────────────────────

    private int _completedStatCount = -1; // -1 forces animation on first update
    private int _incompleteStatCount = -1;
    private int _incomingStatCount = -1;

    // null = no filter; "completed" | "incomplete" | "incoming" = filter carousel by status
    private string? _carouselFilter;

    private void ResetSessionStatCounts()
    {
        _completedStatCount = -1;
        _incompleteStatCount = -1;
        _incomingStatCount = -1;
    }

    private void UpdateSessionStats()
    {
        // Only count orders that were "To be packed" when first scanned (QC work done this session)
        int completed = 0, incomplete = 0, incoming = 0;
        foreach (var session in _sessions)
        {
            foreach (var order in session.Data)
            {
                if (!_qualifiedPackingIds.Contains(order.PackingId)) continue;
                if (string.Equals(order.PackingStatus, "QC Passed", StringComparison.OrdinalIgnoreCase))
                    completed++;
                else if (string.Equals(order.PackingStatus, "QC Hold", StringComparison.OrdinalIgnoreCase))
                    incomplete++;
                else
                    incoming++;
            }
        }

        if (completed != _completedStatCount) { _completedStatCount = completed; _ = AnimateCountLabelAsync(SessionCompletedLabel, completed.ToString()); }
        if (incomplete != _incompleteStatCount) { _incompleteStatCount = incomplete; _ = AnimateCountLabelAsync(SessionIncompleteLabel, incomplete.ToString()); }
        if (incoming != _incomingStatCount) { _incomingStatCount = incoming; _ = AnimateCountLabelAsync(SessionIncomingLabel, incoming.ToString()); }

        int total = completed + incomplete + incoming;
        SessionTotalLabel.Text = total.ToString();
    }

    private static async Task AnimateCountLabelAsync(Label label, string newValue)
    {
        if (!int.TryParse(label.Text, out var from)) from = 0;
        if (!int.TryParse(newValue, out var to)) to = 0;
        if (from == to) return;

        int diff = to - from;
        int step = diff > 0 ? 1 : -1;
        int steps = Math.Abs(diff);

        if (steps <= 8)
        {
            // Progressive step-by-step counting
            for (int n = 0; n < steps; n++)
            {
                from += step;
                await Task.WhenAll(
                    label.ScaleToAsync(1.18, 55, Easing.SinIn),
                    label.FadeToAsync(0.55, 55));
                label.Text = from.ToString();
                await Task.WhenAll(
                    label.ScaleToAsync(1.0, 70, Easing.SinOut),
                    label.FadeToAsync(1.0, 70));
            }
        }
        else
        {
            // Large jump — single fade-out/in
            await Task.WhenAll(label.ScaleToAsync(0.55, 110, Easing.SinIn), label.FadeToAsync(0.0, 110));
            label.Text = newValue;
            await Task.WhenAll(label.ScaleToAsync(1.0, 160, Easing.SinOut), label.FadeToAsync(1.0, 160));
        }
    }

    // ── Item reordering ───────────────────────────────────────────────────────

    private async Task AnimateAndMoveItemToBottomAsync(ProductItem item)
    {
        // Short delay so the green CardBgColor renders before we grab the element
        await Task.Delay(40);

        var completedBorder = FindDescendant<Border>(this, b => b.BindingContext == item && b.IsVisible);
        if (completedBorder == null) { MoveCompletedItemToBottom(item); return; }

        // Locate owner order and item index
        PackingList? ownerOrder = null;
        int itemIndex = -1;
        foreach (var order in Results)
        {
            for (int i = 0; i < order.ParsedProducts.Count; i++)
            {
                if (order.ParsedProducts[i] == item) { ownerOrder = order; itemIndex = i; break; }
            }
            if (ownerOrder != null) break;
        }

        // Nothing to animate if the item is already last
        if (ownerOrder == null || itemIndex == ownerOrder.ParsedProducts.Count - 1)
        {
            MoveCompletedItemToBottom(item);
            return;
        }

        double slideY = completedBorder.Height + 4; // card height + spacing

        // Collect borders of every item that sits after the completed one
        var bordersBelow = new List<Border>();
        for (int i = itemIndex + 1; i < ownerOrder.ParsedProducts.Count; i++)
        {
            var sibling = ownerOrder.ParsedProducts[i];
            var b = FindDescendant<Border>(this, x => x.BindingContext == sibling && x.IsVisible);
            if (b != null) bordersBelow.Add(b);
        }

        // Pre-offset sibling cards so they stay visually in place during the upcoming reorder
        foreach (var b in bordersBelow)
            b.TranslationY = slideY;

        // Phase 1 — completed card slides down + fades out
        await Task.WhenAll(
            completedBorder.TranslateToAsync(0, slideY, 300, Easing.SinIn),
            completedBorder.FadeToAsync(0.0, 300, Easing.SinIn));

        // Reset before reorder (card is invisible, so position jump is hidden)
        completedBorder.TranslationY = 0;

        // Reorder in collection — siblings get new layout positions (shifted up by slideY),
        // but their TranslationY offset keeps them visually where they were
        MoveCompletedItemToBottom(item);

        await Task.Delay(30); // let the layout pass complete

        // Phase 2 — siblings slide up to their new positions,
        //            completed card appears at the bottom with a slide-in
        completedBorder.TranslationY = slideY * 0.6;
        var tasks = bordersBelow.Select(b => b.TranslateToAsync(0, 0, 280, Easing.SinOut)).ToList();
        tasks.Add(completedBorder.TranslateToAsync(0, 0, 280, Easing.SinOut));
        tasks.Add(completedBorder.FadeToAsync(1.0, 280, Easing.SinOut));
        await Task.WhenAll(tasks);
    }

    private void MoveCompletedItemToBottom(ProductItem item)
    {
        foreach (var order in Results)
        {
            var products = order.ParsedProducts;
            if (!products.Contains(item)) continue;
            if (products.Any(p => !p.IsFullyPicked))
            {
                products.Remove(item);
                products.Add(item);
            }
            break;
        }
    }

    // ── Operator UI ──────────────────────────────────────────────────────────

    private void UpdateNavOperatorUI(string? displayName)
    {
        if (displayName is null)
        {
            OperatorHeaderBadge.IsVisible = false;
        }
        else
        {
            HeaderComputerOperatorLabel.Text = $"{StationName} · {displayName.ToUpperInvariant()}";
            OperatorHeaderBadge.IsVisible = true;
        }
    }

    private void OnModeBadgeTapped(object sender, TappedEventArgs e)
    {
        ModeDropdownBackdrop.IsVisible = !ModeDropdownBackdrop.IsVisible;
    }

    private void OnModeDropdownBackdropTapped(object? sender, TappedEventArgs e)
    {
        ModeDropdownBackdrop.IsVisible = false;
    }

    private void OnModeSelectQC(object? sender, TappedEventArgs e)
    {
        ApplyMode(AppMode.QC);
        ModeDropdownBackdrop.IsVisible = false;
    }

    private void OnModeSelectReturns(object? sender, EventArgs e)
    {
        ModeDropdownBackdrop.IsVisible = false;
        ApplyMode(AppMode.Returns);
    }

    private static string GetModeDisplayName(AppMode mode) => mode switch
    {
        AppMode.QC => "QC",
        AppMode.Returns => "Returns",
        _ => mode.ToString(),
    };

    private void ApplyMode(AppMode mode)
    {
        _currentMode = mode;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            ModeLabel.Text = GetModeDisplayName(mode);

            bool isReturns = mode == AppMode.Returns;
            bool hasOrder = Results.Count > 0;

            // Mode dropdown checkmarks
            ModeCheckQC.IsVisible = !isReturns;
            ModeOptionQC.BackgroundColor = !isReturns ? Color.FromArgb("#f5f5ff") : Colors.Transparent;
            ModeCheckReturns.IsVisible = isReturns;
            ModeOptionReturns.BackgroundColor = isReturns ? Color.FromArgb("#f5f5ff") : Colors.Transparent;

            ReturnsActionForm.IsVisible = isReturns && hasOrder;
            ReturnsEmptyState.IsVisible = isReturns && !hasOrder;
            ReturnsSuccessCard.IsVisible = false;
            QcShortcuts.IsVisible = !isReturns;
            ReturnsShortcuts.IsVisible = isReturns;
            if (!isReturns)
                TrackingCarrierPill.IsVisible = false;

            // Toggle stat pills
            SessionCompletedBadge.IsVisible = !isReturns;
            SessionIncompleteBadge.IsVisible = !isReturns;
            SessionIncomingBadge.IsVisible = !isReturns;
            ReturnsReturnedBadge.IsVisible = isReturns;
            ReturnsPendingBadge.IsVisible = isReturns;

            // Side panel
            ReturnsSidePanel.IsVisible = isReturns;

            if (isReturns)
            {
                OnSelectReturnType(null, EventArgs.Empty);
                UpdateHeaderOrderInfo();
                UpdateReturnsSidePanel();
            }
        });
    }

    // ── Returns Mode ──────────────────────────────────────────────────────

    private void OnSelectReturnType(object? sender, EventArgs e)
    {
        _returnsActionType = "return";
        _selectedReturnReason = null;

        BtnReturn.Stroke = Color.FromArgb("#dc2626");
        BtnReturn.StrokeThickness = 2;
        BtnReturn.BackgroundColor = Color.FromArgb("#fef2f2");
        BtnPendingPickup.Stroke = Color.FromArgb("#e5e7eb");
        BtnPendingPickup.StrokeThickness = 1;
        BtnPendingPickup.BackgroundColor = Colors.Transparent;

        ReturnsFormIcon.BackgroundColor = Color.FromArgb("#dc2626");
        ReturnsFormIconLabel.Text = "✕";
        ReturnsFormTitle.Text = "Return Details";
        ConfirmReturnBtn.Text = "✕ Confirm Return";
        ConfirmReturnBtn.BackgroundColor = Color.FromArgb("#dc2626");

        ReturnReasonsSection.IsVisible = true;
        PickupReasonsSection.IsVisible = false;
        BuildReturnReasonChips();
    }

    private void OnSelectPendingPickupType(object? sender, EventArgs e)
    {
        _returnsActionType = "pending_pickup";
        _selectedReturnReason = null;

        BtnReturn.Stroke = Color.FromArgb("#e5e7eb");
        BtnReturn.StrokeThickness = 1;
        BtnReturn.BackgroundColor = Colors.Transparent;
        BtnPendingPickup.Stroke = Color.FromArgb("#a21caf");
        BtnPendingPickup.StrokeThickness = 2;
        BtnPendingPickup.BackgroundColor = Color.FromArgb("#fdf4ff");

        ReturnsFormIcon.BackgroundColor = Color.FromArgb("#a21caf");
        ReturnsFormIconLabel.Text = "⏳";
        ReturnsFormTitle.Text = "Pending Pickup";
        ConfirmReturnBtn.Text = "⏳ Mark Pending Pickup";
        ConfirmReturnBtn.BackgroundColor = Color.FromArgb("#a21caf");

        ReturnReasonsSection.IsVisible = false;
        PickupReasonsSection.IsVisible = true;
        BuildReturnReasonChips();
    }

    private void OnReturnBtnPointerEntered(object? sender, PointerEventArgs e)
    {
        if (_returnsActionType != "return")
            BtnReturn.BackgroundColor = Color.FromArgb("#fff5f5");
    }

    private void OnReturnBtnPointerExited(object? sender, PointerEventArgs e)
    {
        if (_returnsActionType != "return")
            BtnReturn.BackgroundColor = Colors.Transparent;
    }

    private void OnPickupBtnPointerEntered(object? sender, PointerEventArgs e)
    {
        if (_returnsActionType != "pending_pickup")
            BtnPendingPickup.BackgroundColor = Color.FromArgb("#fdf4ff");
    }

    private void OnPickupBtnPointerExited(object? sender, PointerEventArgs e)
    {
        if (_returnsActionType != "pending_pickup")
            BtnPendingPickup.BackgroundColor = Colors.Transparent;
    }

    private void BuildReturnReasonChips()
    {
        var container = _returnsActionType == "return" ? ReturnReasonChips : PickupReasonChips;
        container.Children.Clear();
        var reasons = _returnsActionType == "return" ? _returnReasons : _pickupReasons;

        foreach (var reason in reasons)
        {
            var isSelected = reason == _selectedReturnReason;
            var chip = new Border
            {
                BackgroundColor = isSelected
                    ? (_returnsActionType == "return" ? Color.FromArgb("#fef2f2") : Color.FromArgb("#fdf4ff"))
                    : Colors.White,
                Stroke = isSelected
                    ? (_returnsActionType == "return" ? Color.FromArgb("#dc2626") : Color.FromArgb("#a21caf"))
                    : Color.FromArgb("#e5e7eb"),
                StrokeThickness = isSelected ? 2 : 1,
                StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(6) },
                Padding = new Thickness(12, 8),
                Margin = new Thickness(0, 0, 8, 8),
            };

            var label = new Label
            {
                Text = reason,
                FontSize = 13,
                FontAttributes = isSelected ? FontAttributes.Bold : FontAttributes.None,
                TextColor = isSelected
                    ? (_returnsActionType == "return" ? Color.FromArgb("#991b1b") : Color.FromArgb("#86198f"))
                    : Color.FromArgb("#374151"),
                InputTransparent = true,
            };

            chip.Content = label;
            var capturedReason = reason;
            chip.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(() =>
                {
                    _selectedReturnReason = capturedReason;
                    BuildReturnReasonChips();
                }),
            });

            // Hover effect
            var chipRef = chip;
            var defaultBg = chip.BackgroundColor;
            var hoverBg = isSelected ? defaultBg : Color.FromArgb("#f9fafb");
            var ptr = new PointerGestureRecognizer();
            ptr.PointerEntered += (_, _) => { if (chipRef.BackgroundColor == defaultBg) chipRef.BackgroundColor = hoverBg; };
            ptr.PointerExited += (_, _) => chipRef.BackgroundColor = defaultBg;
            chip.GestureRecognizers.Add(ptr);

            container.Children.Add(chip);
        }
    }

    private async void OnConfirmReturn(object? sender, EventArgs e)
    {
        if (_selectedReturnReason is null) return;
        var order = Results.FirstOrDefault();
        if (order is null) return;
        var trackingNumber = CurrentTrackingNumber;
        if (trackingNumber is null) return;

        ConfirmReturnBtn.IsEnabled = false;
        var success = await ApiService.CreateReturnRecordAsync(
            trackingNumber, _returnsActionType, _selectedReturnReason,
            ReturnsNotesEditor.Text, order.ShippingOptions, order.Platform,
            _currentOperatorId, AppSettings.ResolvedStationId);
        ConfirmReturnBtn.IsEnabled = true;

        if (success)
        {
            StationEvents.Emit(
                workflowName: "Returns",
                stepId: "return_recorded",
                trigger: "confirm_button",
                trackingNumber: trackingNumber,
                fromState: "order-loaded",
                toState: _returnsActionType == "return" ? "returned" : "pending-pickup",
                stationId: AppSettings.ResolvedStationId,
                @operator: EffectiveOperator,
                sequenceInSession: 0,
                payload: new Dictionary<string, object?>
                {
                    ["recordType"] = _returnsActionType,
                    ["reason"] = _selectedReturnReason,
                    ["shippingOptions"] = order.ShippingOptions,
                    ["platform"] = order.Platform,
                });

            // Track return type for carousel coloring
            _sessionReturnType[_sessionIndex] = _returnsActionType;

            // Update stat pills
            if (_returnsActionType == "return")
            {
                _returnsReturnedCount++;
                _ = AnimateCountLabelAsync(ReturnsReturnedLabel, _returnsReturnedCount.ToString());
            }
            else
            {
                _returnsPendingCount++;
                _ = AnimateCountLabelAsync(ReturnsPendingLabel, _returnsPendingCount.ToString());
            }

            // Show overlay + transition to inline success state
            ShowReturnSuccessCard(trackingNumber, order.Platform, order.ShippingOptions,
                _selectedReturnReason!, _returnsActionType);
            ShowInlineReturnSuccess(trackingNumber, order.Platform, order.ShippingOptions,
                _selectedReturnReason!, _returnsActionType);

            // Update side panel
            TrackReturnForSidePanel(_selectedReturnReason, order.Platform);
            UpdateReturnsSidePanel();

            _selectedReturnReason = null;
            ReturnsNotesEditor.Text = "";
            BuildReturnReasonChips();

            _carouselDirty = true;
            BuildCarouselUI();
        }
    }

    private void OnSkipReturn(object? sender, EventArgs e)
    {
        _selectedReturnReason = null;
        ReturnsNotesEditor.Text = "";
        BuildReturnReasonChips();
    }

    private async void ShowReturnSuccessCard(string tracking, string? platform, string? carrier, string reason, string actionType)
    {
        bool isReturn = actionType == "return";
        var accentColor = isReturn ? Color.FromArgb("#dc2626") : Color.FromArgb("#a21caf");
        var bgColor = isReturn ? Color.FromArgb("#f0fdf4") : Color.FromArgb("#fdf4ff");
        var titleColor = isReturn ? Color.FromArgb("#166534") : Color.FromArgb("#86198f");

        ReturnSuccessIcon.BackgroundColor = isReturn ? Color.FromArgb("#16a34a") : Color.FromArgb("#a21caf");
        ReturnSuccessIconLabel.Text = isReturn ? "✓" : "⏳";
        ReturnSuccessTitle.Text = isReturn ? "Returned" : "Pending Pickup";
        ReturnSuccessTitle.TextColor = titleColor;
        ReturnSuccessTracking.Text = tracking;
        ReturnSuccessSubtitle.Text = isReturn ? "Ready for next scan..." : "Stays in queue for next carrier visit.";
        ReturnSuccessBar.BackgroundColor = accentColor;

        if (ReturnSuccessOverlay.Children.OfType<Border>().FirstOrDefault() is { } card)
            card.BackgroundColor = bgColor;

        ReturnSuccessTags.Children.Clear();
        void AddTag(string text, Color dotColor, Color textColor)
        {
            var tag = new Border
            {
                BackgroundColor = Colors.White,
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(4) },
                Padding = new Thickness(8, 4),
                Margin = new Thickness(2),
                Content = new HorizontalStackLayout
                {
                    Spacing = 5,
                    Children =
                    {
                        new BoxView { WidthRequest = 6, HeightRequest = 6, CornerRadius = 3, Color = dotColor, VerticalOptions = LayoutOptions.Center },
                        new Label { Text = text, FontSize = 12, FontAttributes = FontAttributes.Bold, TextColor = textColor },
                    }
                }
            };
            ReturnSuccessTags.Children.Add(tag);
        }

        if (!string.IsNullOrWhiteSpace(platform))
        {
            var pLower = platform.ToLowerInvariant();
            var pColor = pLower switch
            {
                var p when p.Contains("shopee") => Color.FromArgb("#ee4d2d"),
                var p when p.Contains("lazada") => Color.FromArgb("#0f146d"),
                var p when p.Contains("tiktok") => Color.FromArgb("#111827"),
                _ => Color.FromArgb("#6b7280"),
            };
            AddTag(platform, pColor, pColor);
        }
        if (!string.IsNullOrWhiteSpace(carrier))
            AddTag(carrier, Color.FromArgb("#ea580c"), Color.FromArgb("#c2410c"));
        AddTag(reason, accentColor, isReturn ? Color.FromArgb("#991b1b") : Color.FromArgb("#86198f"));

        ReturnSuccessBar.WidthRequest = 200;
        ReturnSuccessOverlay.Opacity = 0;
        ReturnSuccessOverlay.IsVisible = true;
        await ReturnSuccessOverlay.FadeToAsync(1, 250, Easing.CubicOut);

        var anim = new Animation(v => ReturnSuccessBar.WidthRequest = v, 200, 0);
        anim.Commit(ReturnSuccessBar, "ReturnCountdown", length: 2000, easing: Easing.Linear);
        await Task.Delay(2000);

        if (ReturnSuccessOverlay.IsVisible)
            await DismissReturnSuccessAsync();
    }

    private async void OnReturnSuccessBackdropTapped(object? sender, TappedEventArgs e)
        => await DismissReturnSuccessAsync();

    private async Task DismissReturnSuccessAsync()
    {
        await ReturnSuccessOverlay.FadeToAsync(0, 300, Easing.CubicIn);
        ReturnSuccessOverlay.IsVisible = false;
    }

    private void ShowInlineReturnSuccess(string tracking, string? platform, string? carrier, string reason, string actionType)
    {
        bool isReturn = actionType == "return";
        var accentColor = isReturn ? Color.FromArgb("#dc2626") : Color.FromArgb("#a21caf");

        // Style the card border
        ReturnsSuccessCard.BackgroundColor = isReturn ? Color.FromArgb("#f0fdf4") : Color.FromArgb("#fdf4ff");
        ReturnsSuccessCard.Stroke = isReturn ? Color.FromArgb("#bbf7d0") : Color.FromArgb("#e9d5ff");

        // Icon
        InlineSuccessIcon.BackgroundColor = isReturn ? Color.FromArgb("#16a34a") : Color.FromArgb("#a21caf");
        InlineSuccessIconLabel.Text = isReturn ? "✓" : "⏳";

        // Title
        InlineSuccessTitle.Text = isReturn ? "Returned" : "Pending Pickup";
        InlineSuccessTitle.TextColor = isReturn ? Color.FromArgb("#166534") : Color.FromArgb("#86198f");
        InlineSuccessTracking.Text = tracking;

        // Subtitle
        InlineSuccessSubtitle.Text = isReturn
            ? "Ready for next scan..."
            : "Stays in queue for next carrier visit.";

        // Tags
        InlineSuccessTags.Children.Clear();
        void AddTag(string text, Color dotColor, Color textColor)
        {
            var tag = new Border
            {
                BackgroundColor = Colors.White,
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(4) },
                Padding = new Thickness(8, 4),
                Margin = new Thickness(2),
                Content = new HorizontalStackLayout
                {
                    Spacing = 5,
                    Children =
                    {
                        new BoxView { WidthRequest = 6, HeightRequest = 6, CornerRadius = 3, Color = dotColor, VerticalOptions = LayoutOptions.Center },
                        new Label { Text = text, FontSize = 12, FontAttributes = FontAttributes.Bold, TextColor = textColor },
                    }
                }
            };
            InlineSuccessTags.Children.Add(tag);
        }

        if (!string.IsNullOrWhiteSpace(platform))
        {
            var pLower = platform.ToLowerInvariant();
            var pColor = pLower switch
            {
                var p when p.Contains("shopee") => Color.FromArgb("#ee4d2d"),
                var p when p.Contains("lazada") => Color.FromArgb("#0f146d"),
                var p when p.Contains("tiktok") => Color.FromArgb("#111827"),
                _ => Color.FromArgb("#6b7280"),
            };
            AddTag(platform, pColor, pColor);
        }
        if (!string.IsNullOrWhiteSpace(carrier))
            AddTag(carrier, Color.FromArgb("#ea580c"), Color.FromArgb("#c2410c"));
        AddTag(reason, accentColor, isReturn ? Color.FromArgb("#991b1b") : Color.FromArgb("#86198f"));

        // Toggle visibility: hide form, show success card
        ReturnsActionForm.IsVisible = false;
        ReturnsSuccessCard.IsVisible = true;
    }

    // ── Returns side panel ──────────────────────────────────────────────────

    private readonly Dictionary<string, int> _returnsReasonCounts = new();
    private readonly Dictionary<string, int> _returnsPlatformCounts = new();

    private void UpdateReturnsSidePanel()
    {
        SidePanelReturnedCount.Text = _returnsReturnedCount.ToString();
        SidePanelPendingCount.Text = _returnsPendingCount.ToString();
        BuildCarrierCountsPanel();
        BuildReasonBreakdown();
        BuildPlatformBreakdown();
    }

    private void TrackReturnForSidePanel(string? reason, string? platform)
    {
        if (!string.IsNullOrWhiteSpace(reason))
        {
            if (!_returnsReasonCounts.TryAdd(reason, 1))
                _returnsReasonCounts[reason]++;
        }
        if (!string.IsNullOrWhiteSpace(platform))
        {
            if (!_returnsPlatformCounts.TryAdd(platform, 1))
                _returnsPlatformCounts[platform]++;
        }
    }

    private void BuildReasonBreakdown()
    {
        ReasonBreakdownPanel.Children.Clear();
        if (_returnsReasonCounts.Count == 0) return;

        int maxCount = _returnsReasonCounts.Values.Max();
        Color[] barColors = [Color.FromArgb("#4318B0"), Color.FromArgb("#f97316"),
            Color.FromArgb("#eab308"), Color.FromArgb("#8b5cf6"), Color.FromArgb("#06b6d4")];
        int colorIdx = 0;

        foreach (var (reason, count) in _returnsReasonCounts.OrderByDescending(x => x.Value))
        {
            double pct = maxCount > 0 ? (double)count / maxCount : 0;
            var barColor = barColors[colorIdx % barColors.Length];
            colorIdx++;

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new(new GridLength(90)),
                    new(GridLength.Star),
                    new(new GridLength(24)),
                },
                ColumnSpacing = 8,
            };

            row.Add(new Label
            {
                Text = reason,
                FontSize = 11,
                TextColor = Color.FromArgb("#374151"),
                HorizontalTextAlignment = TextAlignment.End,
                LineBreakMode = LineBreakMode.TailTruncation,
                VerticalOptions = LayoutOptions.Center,
            }, 0);

            var trackGrid = new Grid { HeightRequest = 8 };
            trackGrid.Add(new BoxView
            {
                BackgroundColor = Color.FromArgb("#f3f4f6"),
                CornerRadius = 4,
            });
            trackGrid.Add(new BoxView
            {
                BackgroundColor = barColor,
                CornerRadius = 4,
                HorizontalOptions = LayoutOptions.Start,
                WidthRequest = pct * 80,
            });
            row.Add(trackGrid, 1);

            row.Add(new Label
            {
                Text = count.ToString(),
                FontSize = 11,
                FontAttributes = FontAttributes.Bold,
                FontFamily = "Consolas",
                TextColor = Color.FromArgb("#111827"),
                HorizontalTextAlignment = TextAlignment.End,
                VerticalOptions = LayoutOptions.Center,
            }, 2);

            ReasonBreakdownPanel.Children.Add(row);
        }
    }

    private readonly Dictionary<string, int> _carrierExpectedCounts = new();
    private readonly Dictionary<string, Entry> _carrierActualEntries = new();

    private void BuildCarrierCountsPanel()
    {
        CarrierCountsPanel.Children.Clear();

        // Header row: Expected / Actual labels
        var hdr = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new(new GridLength(14)),
                new(GridLength.Star),
                new(new GridLength(56)),
                new(new GridLength(56)),
            },
            ColumnSpacing = 8,
            Padding = new Thickness(0, 0, 0, 4),
        };
        hdr.Add(new Label(), 0);
        hdr.Add(new Label(), 1);
        hdr.Add(new Label
        {
            Text = "EXPECTED",
            FontSize = 9,
            FontAttributes = FontAttributes.Bold,
            TextColor = Color.FromArgb("#9ca3af"),
            HorizontalTextAlignment = TextAlignment.Center,
        }, 2);
        hdr.Add(new Label
        {
            Text = "ACTUAL",
            FontSize = 9,
            FontAttributes = FontAttributes.Bold,
            TextColor = Color.FromArgb("#9ca3af"),
            HorizontalTextAlignment = TextAlignment.Center,
        }, 3);
        CarrierCountsPanel.Children.Add(hdr);

        // Collect carriers from sessions
        var carrierCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in _sessions)
        {
            var carrier = s.Data.FirstOrDefault()?.ShippingOptions;
            if (!string.IsNullOrWhiteSpace(carrier))
            {
                if (!carrierCounts.TryAdd(carrier, 1))
                    carrierCounts[carrier]++;
            }
        }

        // Merge with known expected counts
        foreach (var (carrier, expected) in _carrierExpectedCounts)
        {
            carrierCounts.TryAdd(carrier, 0);
        }

        Color[] dotColors = [Color.FromArgb("#ea580c"), Color.FromArgb("#e11d48"),
            Color.FromArgb("#ca8a04"), Color.FromArgb("#dc2626"), Color.FromArgb("#a21caf")];
        int colorIdx = 0;

        foreach (var (carrier, sessionCount) in carrierCounts.OrderByDescending(x => x.Value))
        {
            var expected = _carrierExpectedCounts.GetValueOrDefault(carrier, sessionCount);
            var dotColor = dotColors[colorIdx % dotColors.Length];
            colorIdx++;

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new(new GridLength(14)),
                    new(GridLength.Star),
                    new(new GridLength(56)),
                    new(new GridLength(56)),
                },
                ColumnSpacing = 8,
                Padding = new Thickness(0, 4),
            };

            row.Add(new BoxView
            {
                WidthRequest = 8, HeightRequest = 8,
                CornerRadius = 2, Color = dotColor,
                VerticalOptions = LayoutOptions.Center,
            }, 0);

            row.Add(new Label
            {
                Text = carrier,
                FontSize = 12,
                TextColor = Color.FromArgb("#374151"),
                VerticalOptions = LayoutOptions.Center,
                LineBreakMode = LineBreakMode.TailTruncation,
            }, 1);

            row.Add(new Label
            {
                Text = expected.ToString(),
                FontSize = 13,
                FontAttributes = FontAttributes.Bold,
                FontFamily = "Consolas",
                TextColor = Color.FromArgb("#6b7280"),
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalOptions = LayoutOptions.Center,
            }, 2);

            var entry = new Entry
            {
                Placeholder = "0",
                FontSize = 13,
                FontFamily = "Consolas",
                FontAttributes = FontAttributes.Bold,
                Keyboard = Keyboard.Numeric,
                HorizontalTextAlignment = TextAlignment.Center,
                HeightRequest = 28,
                BackgroundColor = Colors.White,
            };
            var capturedCarrier = carrier;
            entry.Unfocused += async (_, _) =>
            {
                if (int.TryParse(entry.Text, out var actualCount))
                    await ApiService.UpsertCarrierParcelCountAsync(capturedCarrier, actualCount, _currentOperatorId);
            };

            if (_carrierActualEntries.TryGetValue(carrier, out var existingEntry))
                entry.Text = existingEntry.Text;
            _carrierActualEntries[carrier] = entry;

            row.Add(entry, 3);
            CarrierCountsPanel.Children.Add(row);
        }
    }

    private void BuildPlatformBreakdown()
    {
        PlatformBreakdownPanel.Children.Clear();
        if (_returnsPlatformCounts.Count == 0) return;

        int maxCount = _returnsPlatformCounts.Values.Max();

        foreach (var (platform, count) in _returnsPlatformCounts.OrderByDescending(x => x.Value))
        {
            double pct = maxCount > 0 ? (double)count / maxCount : 0;
            var pLower = platform.ToLowerInvariant();
            var dotColor = pLower switch
            {
                var p when p.Contains("shopee") => Color.FromArgb("#ee4d2d"),
                var p when p.Contains("lazada") => Color.FromArgb("#0f146d"),
                var p when p.Contains("tiktok") => Color.FromArgb("#1a1a2e"),
                _ => Color.FromArgb("#6b7280"),
            };

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new(new GridLength(14)),
                    new(GridLength.Star),
                    new(new GridLength(50)),
                    new(new GridLength(24)),
                },
                ColumnSpacing = 8,
            };

            row.Add(new BoxView
            {
                WidthRequest = 8,
                HeightRequest = 8,
                CornerRadius = 2,
                Color = dotColor,
                VerticalOptions = LayoutOptions.Center,
            }, 0);

            row.Add(new Label
            {
                Text = platform,
                FontSize = 12,
                TextColor = Color.FromArgb("#374151"),
                VerticalOptions = LayoutOptions.Center,
            }, 1);

            var barGrid = new Grid { HeightRequest = 6 };
            barGrid.Add(new BoxView
            {
                BackgroundColor = Color.FromArgb("#f3f4f6"),
                CornerRadius = 3,
            });
            barGrid.Add(new BoxView
            {
                BackgroundColor = dotColor,
                CornerRadius = 3,
                HorizontalOptions = LayoutOptions.Start,
                WidthRequest = pct * 50,
            });
            row.Add(barGrid, 2);

            row.Add(new Label
            {
                Text = count.ToString(),
                FontSize = 12,
                FontAttributes = FontAttributes.Bold,
                FontFamily = "Consolas",
                TextColor = Color.FromArgb("#111827"),
                HorizontalTextAlignment = TextAlignment.End,
                VerticalOptions = LayoutOptions.Center,
            }, 3);

            PlatformBreakdownPanel.Children.Add(row);
        }
    }

    private void ShowLoginOverlay() =>
        MainThread.BeginInvokeOnMainThread(() => LoginOverlay.IsVisible = true);

    private void HideLoginOverlay() =>
        MainThread.BeginInvokeOnMainThread(() => LoginOverlay.IsVisible = false);

    private async Task ShowWelcomeAnimationAsync(string name)
    {
        WelcomeLabel.Text = $"Welcome, {name}";
        WelcomeBanner.IsVisible = true;
        WelcomeBanner.Opacity = 0;
        WelcomeBanner.Scale = 0.85;
        await Task.WhenAll(
            WelcomeBanner.FadeToAsync(1.0, 280, Easing.SinOut),
            WelcomeBanner.ScaleToAsync(1.0, 280, Easing.SinOut));
        await Task.Delay(1800);
        await WelcomeBanner.FadeToAsync(0.0, 350, Easing.SinIn);
        WelcomeBanner.IsVisible = false;
        WelcomeBanner.Scale = 1.0;
    }

    private void StartInactivityTimer()
    {
        StopInactivityTimer();
        var minutes = AppSettings.QcInactivityMinutes;
        if (minutes <= 0) return;
        _inactivityTimer = Dispatcher.CreateTimer();
        _inactivityTimer.Interval = TimeSpan.FromMinutes(minutes);
        _inactivityTimer.IsRepeating = false;
        _inactivityTimer.Tick += OnInactivityTimerTick;
        _inactivityTimer.Start();
    }

    private void StopInactivityTimer()
    {
        if (_inactivityTimer is null) return;
        _inactivityTimer.Stop();
        _inactivityTimer.Tick -= OnInactivityTimerTick;
        _inactivityTimer = null;
    }

    private void OnInactivityTimerTick(object? sender, EventArgs e)
    {
        var displayName = _currentOperatorFirstName ?? _currentOperator;
        _currentOperator = null;
        _currentOperatorFirstName = null;
        _currentOperatorId = null;
        StopInactivityTimer();
        Services.StationWsClient.SendOperatorLogout();
        UpdateNavOperatorUI(null);
        ShowLoginOverlay();
        if (displayName is not null)
            _ = Toast.Make($"Session ended — {displayName}").Show();
        Logger.Log($"OrderSearch: Operator logged out (inactivity)");
    }


}
