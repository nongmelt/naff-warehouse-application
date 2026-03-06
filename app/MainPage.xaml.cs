using app.Controls;
using System.Runtime.Versioning;
using app.Services;
using app.Views;

namespace app;

[SupportedOSPlatform("windows")]
public partial class MainPage : ContentPage
{
    private readonly List<StationView> _stations = new();
    private int _nextId = 0;
    private StationView? _selectedStation;

    public MainPage()
    {
        InitializeComponent();
        Logger.Log("App started");
        AddStation(); // default first station
    }

    // ── Station Management ───────────────────────────────────────────────────

    private void AddStation()
    {
        var station = new StationView(++_nextId);
        station.StationSelected += OnStationSelected;
        _stations.Add(station);
        RearrangeGrid();
        Logger.Log($"Station {_nextId} added (total: {_stations.Count})");
    }

    private void OnStationSelected(object? sender, EventArgs e)
    {
        if (sender is not StationView tapped) return;
        if (_selectedStation == tapped) return; // already selected

        _selectedStation?.IsSelected = false;   // deselect previous
        _selectedStation = tapped;
        _selectedStation.IsSelected = true;
    }

    private void OnAddStation(object sender, EventArgs e) => AddStation();

    private void OnRemoveStation(object sender, EventArgs e)
    {
        // Remove the selected station; fall back to the last station if none is selected
        var target = _selectedStation ?? (_stations.Count > 0 ? _stations[^1] : null);
        if (target == null) return;

        if (_selectedStation == target) _selectedStation = null;
        target.IsSelected = false;
        _stations.Remove(target);
        target.Dispose();
        RearrangeGrid();
        Logger.Log($"Station removed (total: {_stations.Count})");
    }

    private async void OnRefreshDevices(object sender, EventArgs e)
    {
        await Task.WhenAll(_stations.Select(s => s.LoadDevicesAsync()));
    }

    private void OnOpenLogs(object sender, EventArgs e)
    {
        System.Diagnostics.Process.Start("explorer.exe", FileSystem.AppDataDirectory);
    }

    private async void OnOpenSettings(object sender, EventArgs e)
    {
        await Navigation.PushModalAsync(new SettingsPage());
    }

    private async void OnGoHome(object sender, EventArgs e)
        => await Shell.Current.GoToAsync("//home");

    // ── Square Grid Layout ───────────────────────────────────────────────────

    /// <summary>
    /// Arranges stations in a square-ish grid.
    /// 1 station → 1×1, 2–4 → 2×2, 5–9 → 3×3, 10–16 → 4×4, etc.
    /// Each row is 380px tall; columns share equal width.
    ///
    /// IMPORTANT: never calls Children.Clear() — removing a StationView from the
    /// visual tree destroys its CameraView handler and kills any running preview.
    /// Instead we only add/remove the delta and update Grid.Row/Column in place.
    /// </summary>
    private void RearrangeGrid()
    {
        int count = _stations.Count;

        if (count == 0)
        {
            StationsGrid.Children.Clear();
            StationsGrid.RowDefinitions.Clear();
            StationsGrid.ColumnDefinitions.Clear();
            return;
        }

        int cols = (int)Math.Ceiling(Math.Sqrt(count));
        int rows = (int)Math.Ceiling((double)count / cols);

        // Sync row definitions (grow or shrink as needed)
        while (StationsGrid.RowDefinitions.Count < rows)
            StationsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Star });
        while (StationsGrid.RowDefinitions.Count > rows)
            StationsGrid.RowDefinitions.RemoveAt(StationsGrid.RowDefinitions.Count - 1);

        // Sync column definitions
        while (StationsGrid.ColumnDefinitions.Count < cols)
            StationsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
        while (StationsGrid.ColumnDefinitions.Count > cols)
            StationsGrid.ColumnDefinitions.RemoveAt(StationsGrid.ColumnDefinitions.Count - 1);

        // Remove any deleted stations from the grid without touching the live ones
        var toRemove = StationsGrid.Children
            .OfType<StationView>()
            .Where(s => !_stations.Contains(s))
            .ToList();
        foreach (var s in toRemove)
            StationsGrid.Children.Remove(s);

        // Add new stations and update every station's row/column position
        for (int i = 0; i < count; i++)
        {
            var station = _stations[i];
            if (!StationsGrid.Children.Contains(station))
                StationsGrid.Children.Add(station);
            Grid.SetColumn(station, i % cols);
            Grid.SetRow(station, i / cols);
        }
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        foreach (var s in _stations)
            s.Dispose();
    }
}
