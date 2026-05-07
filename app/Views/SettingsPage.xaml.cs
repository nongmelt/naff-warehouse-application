using app.Services;
using CommunityToolkit.Maui.Storage;
using MauiShapes = Microsoft.Maui.Controls.Shapes;
using System.Runtime.Versioning;

namespace app.Views;

[SupportedOSPlatform("windows")]
public partial class SettingsPage : ContentPage
{
    private Color _retryBase  = Colors.Transparent;
    private Color _retryHover = Colors.Transparent;
    private Color _retryText  = Colors.Black;
    private List<string> _videoFolderPaths = [];
    private List<string> _branchCodes  = [];
    private List<string> _positionCodes = [];

    public SettingsPage()
    {
        InitializeComponent();
        ShowPanel("general");
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _videoFolderPaths          = new List<string>(AppSettings.VideoFolders);
        MinFreeSpaceEntry.Text     = (AppSettings.VideoFolderMinFreeSpaceBytes / 1_073_741_824L).ToString();
        RebuildFolderRows();
        AutoDeleteSwitch.IsToggled  = AppSettings.AutoDeleteCompletedVideos;
        ApiUrlEntry.Text            = AppSettings.ApiUrl;
        SearchHistoryMaxEntry.Text  = AppSettings.SearchHistoryMaxItems.ToString();
        MinioBucketEntry.Text    = AppSettings.MinioBucket;
        MinioEndpointEntry.Text  = AppSettings.MinioEndpoint;
        MinioAccessKeyEntry.Text = AppSettings.MinioAccessKey;
        MinioSecretKeyEntry.Text = AppSettings.MinioSecretKey;

        _branchCodes   = new List<string>(AppSettings.OperatorBranchCodes);
        _positionCodes = new List<string>(AppSettings.OperatorPositionCodes);
        PackingInactivityEntry.Text = AppSettings.PackingInactivityMinutes.ToString();
        QcInactivityEntry.Text      = AppSettings.QcInactivityMinutes.ToString();
        RebuildBranchRows();
        RebuildPositionRows();
        RefreshRegexPreview();
    }

    // ── Navigation ───────────────────────────────────────────────────────────

    private async void OnCancel(object sender, EventArgs e) =>
        await Navigation.PopModalAsync();

    // ── Sidebar ───────────────────────────────────────────────────────────────

    private void OnNavGeneral(object sender, TappedEventArgs e)  => ShowPanel("general");
    private void OnNavApi(object sender, TappedEventArgs e)      => ShowPanel("api");
    private void OnNavMinio(object sender, TappedEventArgs e)    => ShowPanel("minio");
    private void OnNavOperator(object sender, TappedEventArgs e) => ShowPanel("operator");

    private void ShowPanel(string panel)
    {
        PanelGeneral.IsVisible  = panel == "general";
        PanelApi.IsVisible      = panel == "api";
        PanelMinio.IsVisible    = panel == "minio";
        PanelOperator.IsVisible = panel == "operator";
        SetNavActive(NavGeneralBorder,  NavGeneralLabel,  panel == "general");
        SetNavActive(NavApiBorder,      NavApiLabel,      panel == "api");
        SetNavActive(NavMinioBorder,    NavMinioLabel,     panel == "minio");
        SetNavActive(NavOperatorBorder, NavOperatorLabel, panel == "operator");
    }

    private static void SetNavActive(Border border, Label label, bool active)
    {
        border.BackgroundColor = active ? Color.FromArgb("#eff6ff") : Colors.Transparent;
        label.TextColor        = active ? Color.FromArgb("#2563eb") : Color.FromArgb("#374151");
        label.FontAttributes   = active ? FontAttributes.Bold : FontAttributes.None;
    }

    // ── General ───────────────────────────────────────────────────────────────

    private async void OnAddVideoFolder(object sender, EventArgs e)
    {
        var result = await FolderPicker.Default.PickAsync(CancellationToken.None);
        if (!result.IsSuccessful) return;
        var path = result.Folder.Path;
        if (!_videoFolderPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            _videoFolderPaths.Add(path);
            RebuildFolderRows();
        }
    }

    private void RebuildFolderRows()
    {
        VideoFoldersStack.Children.Clear();

        for (int i = 0; i < _videoFolderPaths.Count; i++)
        {
            var path = _videoFolderPaths[i];
            var index = i; // capture for lambda

            // Compute free space label
            string spaceLabel;
            try
            {
                bool isUnc = path.StartsWith(@"\\") || path.StartsWith("//");
                if (isUnc)
                {
                    spaceLabel = Directory.Exists(path) ? "reachable" : "unreachable";
                }
                else
                {
                    var root = Path.GetPathRoot(path);
                    if (!string.IsNullOrEmpty(root))
                    {
                        var drive = new DriveInfo(root);
                        spaceLabel = drive.IsReady
                            ? $"{drive.AvailableFreeSpace / 1_073_741_824.0:F1} GB free"
                            : "drive not ready";
                    }
                    else spaceLabel = "unknown";
                }
            }
            catch { spaceLabel = "unavailable"; }

            var tag = i == 0 ? " (primary)" : $" (fallback {i})";

            var row = new Grid
            {
                ColumnDefinitions =
                [
                    new ColumnDefinition { Width = GridLength.Star },
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = GridLength.Auto },
                ],
                ColumnSpacing = 8,
            };

            var pathLabel = new Label
            {
                Text = path + tag,
                FontSize = 13,
                TextColor = Color.FromArgb("#111827"),
                VerticalOptions = LayoutOptions.Center,
                LineBreakMode = LineBreakMode.MiddleTruncation,
            };

            var spaceInfo = new Label
            {
                Text = spaceLabel,
                FontSize = 11,
                TextColor = Color.FromArgb("#6b7280"),
                VerticalOptions = LayoutOptions.Center,
            };

            var removeBtn = new Button
            {
                Text = "✕",
                FontSize = 12,
                WidthRequest = 32,
                HeightRequest = 32,
                Padding = new Thickness(0),
                CornerRadius = 6,
                BackgroundColor = Color.FromArgb("#fee2e2"),
                TextColor = Color.FromArgb("#dc2626"),
                BorderWidth = 0,
            };
            removeBtn.Clicked += (_, _) =>
            {
                _videoFolderPaths.RemoveAt(index);
                RebuildFolderRows();
            };

            Grid.SetColumn(pathLabel, 0);
            Grid.SetColumn(spaceInfo, 1);
            Grid.SetColumn(removeBtn, 2);
            row.Children.Add(pathLabel);
            row.Children.Add(spaceInfo);
            row.Children.Add(removeBtn);

            VideoFoldersStack.Children.Add(row);
        }
    }

    private void OnSaveGeneral(object sender, EventArgs e)
    {
        AppSettings.VideoFolders = _videoFolderPaths;
        if (long.TryParse(MinFreeSpaceEntry.Text?.Trim(), out var gb) && gb >= 1)
            AppSettings.VideoFolderMinFreeSpaceBytes = gb * 1_073_741_824L;
        AppSettings.AutoDeleteCompletedVideos = AutoDeleteSwitch.IsToggled;
        if (int.TryParse(SearchHistoryMaxEntry.Text?.Trim(), out var maxHistory) && maxHistory >= 1)
            AppSettings.SearchHistoryMaxItems = maxHistory;
        Logger.Log($"Settings saved — VideoFolders: {string.Join(";", _videoFolderPaths)}, AutoDelete: {AppSettings.AutoDeleteCompletedVideos}");
        GeneralSavedLabel.IsVisible = true;
        Dispatcher.DispatchDelayed(TimeSpan.FromSeconds(3), () => GeneralSavedLabel.IsVisible = false);
    }

    // ── MinIO Sync ────────────────────────────────────────────────────────────

    private void OnSaveMinio(object sender, EventArgs e)
    {
        AppSettings.MinioBucket    = MinioBucketEntry.Text?.Trim()    ?? string.Empty;
        AppSettings.MinioEndpoint  = MinioEndpointEntry.Text?.Trim()  ?? string.Empty;
        AppSettings.MinioAccessKey = MinioAccessKeyEntry.Text?.Trim() ?? string.Empty;
        AppSettings.MinioSecretKey = MinioSecretKeyEntry.Text?.Trim() ?? string.Empty;

        Logger.Log("MinIO settings saved.");

        MinioSavedLabel.Text      = "✓ Saved";
        MinioSavedLabel.IsVisible = true;
        Dispatcher.DispatchDelayed(TimeSpan.FromSeconds(4), () => MinioSavedLabel.IsVisible = false);
    }

    // ── Backend API ───────────────────────────────────────────────────────────

    private async void OnSaveApi(object sender, EventArgs e)
    {
        AppSettings.ApiUrl = ApiUrlEntry.Text?.Trim() ?? AppSettings.DefaultApiUrl;
        Logger.Log($"API URL saved — {AppSettings.ApiUrl}");
        await TestAndShowResultAsync();
    }

    private async void OnRetryConnection(object sender, EventArgs e) =>
        await TestAndShowResultAsync();

    private void OnRetryButtonEntered(object sender, PointerEventArgs e)
    {
        RetryButton.BackgroundColor = _retryHover;
        RetryButton.TextColor       = Colors.White;
    }

    private void OnRetryButtonExited(object sender, PointerEventArgs e)
    {
        RetryButton.BackgroundColor = _retryBase;
        RetryButton.TextColor       = _retryText;
    }

    // ── Operator ──────────────────────────────────────────────────────────────

    private void OnAddBranchCode(object sender, EventArgs e)
    {
        var code = NewBranchEntry.Text?.Trim().ToUpperInvariant() ?? string.Empty;
        if (code.Length != 3 || !code.All(char.IsAsciiLetterUpper)) return;
        if (!_branchCodes.Contains(code, StringComparer.OrdinalIgnoreCase))
        {
            _branchCodes.Add(code);
            RebuildBranchRows();
            RefreshRegexPreview();
        }
        NewBranchEntry.Text = string.Empty;
    }

    private void OnAddPositionCode(object sender, EventArgs e)
    {
        var code = NewPositionEntry.Text?.Trim().ToUpperInvariant() ?? string.Empty;
        if (code.Length == 0) return;
        if (!_positionCodes.Contains(code, StringComparer.OrdinalIgnoreCase))
        {
            _positionCodes.Add(code);
            RebuildPositionRows();
            RefreshRegexPreview();
        }
        NewPositionEntry.Text = string.Empty;
    }

    private void RebuildBranchRows() => RebuildTagRows(BranchCodesStack, _branchCodes, () =>
    {
        RebuildBranchRows();
        RefreshRegexPreview();
    });

    private void RebuildPositionRows() => RebuildTagRows(PositionCodesStack, _positionCodes, () =>
    {
        RebuildPositionRows();
        RefreshRegexPreview();
    });

    private static void RebuildTagRows(Layout stack, List<string> items, Action onRemove)
    {
        stack.Children.Clear();
        for (int i = 0; i < items.Count; i++)
        {
            var code = items[i];
            var idx  = i;

            var codeLabel = new Label
            {
                Text = code,
                FontSize = 12,
                TextColor = Color.FromArgb("#1e40af"),
                VerticalOptions = LayoutOptions.Center,
            };

            var removeLabel = new Label
            {
                Text = "✕",
                FontSize = 11,
                TextColor = Color.FromArgb("#6b7280"),
                VerticalOptions = LayoutOptions.Center,
            };
            removeLabel.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(() => { items.RemoveAt(idx); onRemove(); })
            });

            var chip = new Border
            {
                StrokeShape = new MauiShapes.RoundRectangle { CornerRadius = 10 },
                StrokeThickness = 0,
                BackgroundColor = Color.FromArgb("#dbeafe"),
                Padding = new Thickness(8, 3),
                Margin = new Thickness(0, 0, 4, 4),
                Content = new HorizontalStackLayout
                {
                    Spacing = 4,
                    Children = { codeLabel, removeLabel }
                },
            };

            stack.Children.Add(chip);
        }
    }

    private void RefreshRegexPreview()
    {
        // Temporarily apply lists to settings for preview calculation
        var savedBranches  = AppSettings.OperatorBranchCodes;
        var savedPositions = AppSettings.OperatorPositionCodes;
        AppSettings.OperatorBranchCodes  = _branchCodes;
        AppSettings.OperatorPositionCodes = _positionCodes;
        BadgeRegexPreview.Text = AppSettings.BuildBadgePattern();
        AppSettings.OperatorBranchCodes  = savedBranches;
        AppSettings.OperatorPositionCodes = savedPositions;
    }

    private void OnSaveOperator(object sender, EventArgs e)
    {
        AppSettings.OperatorBranchCodes  = _branchCodes;
        AppSettings.OperatorPositionCodes = _positionCodes;

        if (int.TryParse(PackingInactivityEntry.Text?.Trim(), out var pk) && pk >= 1)
            AppSettings.PackingInactivityMinutes = pk;
        if (int.TryParse(QcInactivityEntry.Text?.Trim(), out var qc) && qc >= 1)
            AppSettings.QcInactivityMinutes = qc;

        Logger.Log($"Operator settings saved — Pattern: {AppSettings.BuildBadgePattern()}, PackingIdle: {AppSettings.PackingInactivityMinutes}m, QcIdle: {AppSettings.QcInactivityMinutes}m");
        OperatorSavedLabel.IsVisible = true;
        Dispatcher.DispatchDelayed(TimeSpan.FromSeconds(3), () => OperatorSavedLabel.IsVisible = false);
    }

    private async Task TestAndShowResultAsync()
    {
        NotificationCard.IsVisible = false;

        var (success, error) = await ApiService.TestConnectionAsync();

        Color cardBg, cardBorder, msgColor, retryBase, retryHover, retryText;
        if (success)
        {
            cardBg     = Color.FromArgb("#dcfce7");
            cardBorder = Color.FromArgb("#86efac");
            msgColor   = Color.FromArgb("#15803d");
            retryBase  = Color.FromArgb("#dcfce7");
            retryHover = Color.FromArgb("#86efac");
            retryText  = Color.FromArgb("#15803d");
        }
        else
        {
            cardBg     = Color.FromArgb("#fee2e2");
            cardBorder = Color.FromArgb("#fca5a5");
            msgColor   = Color.FromArgb("#dc2626");
            retryBase  = Color.FromArgb("#fee2e2");
            retryHover = Color.FromArgb("#fca5a5");
            retryText  = Color.FromArgb("#dc2626");
        }

        NotificationCard.BackgroundColor   = cardBg;
        NotificationCard.Stroke            = new SolidColorBrush(cardBorder);
        NotificationMessageLabel.Text      = success
            ? "Backend API reachable"
            : $"Cannot reach API{(error != null ? $": {error}" : "")}";
        NotificationMessageLabel.TextColor = msgColor;

        _retryBase  = retryBase;
        _retryHover = retryHover;
        _retryText  = retryText;
        RetryButton.BackgroundColor = retryBase;
        RetryButton.BorderColor     = cardBorder;
        RetryButton.TextColor       = retryText;

        NotificationCard.IsVisible = true;
    }
}
