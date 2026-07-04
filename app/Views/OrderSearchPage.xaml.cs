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
    private record ComPortEntry(string PortName, string DisplayName);

    private SerialPort? _serialPort;
    private string? _selectedPortName;
    private List<ComPortEntry> _comPorts = [];
    private bool _isSearching;
    private IDispatcherTimer? _comHeartbeatTimer;
    private long _lastSerialDataTicks;
    private int _historyNavIndex = -1;
    private int? _currentOperatorId;

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


    // ── Session stats ─────────────────────────────────────────────────────────

    private int _completedStatCount = -1; // -1 forces animation on first update
    private int _incompleteStatCount = -1;
    private int _incomingStatCount = -1;

    // null = no filter; "completed" | "incomplete" | "incoming" = filter carousel by status
    private string? _carouselFilter;

}
