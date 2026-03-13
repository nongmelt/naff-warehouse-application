using app.Services;
using CommunityToolkit.Maui.Storage;
using System.Runtime.Versioning;

namespace app.Views;

[SupportedOSPlatform("windows")]
public partial class SettingsPage : ContentPage
{
    private Color _retryBase  = Colors.Transparent;
    private Color _retryHover = Colors.Transparent;
    private Color _retryText  = Colors.Black;

    public SettingsPage()
    {
        InitializeComponent();
        ShowPanel("general");
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        VideoFolderEntry.Text    = AppSettings.VideoFolder;
        WebhookUrlEntry.Text     = AppSettings.WebhookUrl;
        ApiUrlEntry.Text         = AppSettings.ApiUrl;
        MinioPcNameEntry.Text    = AppSettings.MinioPcName;
        MinioBucketEntry.Text    = AppSettings.MinioBucket;
        MinioEndpointEntry.Text  = AppSettings.MinioEndpoint;
        MinioAccessKeyEntry.Text = AppSettings.MinioAccessKey;
        MinioSecretKeyEntry.Text = AppSettings.MinioSecretKey;
    }

    // ── Navigation ───────────────────────────────────────────────────────────

    private async void OnCancel(object sender, EventArgs e) =>
        await Navigation.PopModalAsync();

    // ── Sidebar ───────────────────────────────────────────────────────────────

    private void OnNavGeneral(object sender, TappedEventArgs e) => ShowPanel("general");
    private void OnNavApi(object sender, TappedEventArgs e)     => ShowPanel("api");
    private void OnNavMinio(object sender, TappedEventArgs e)   => ShowPanel("minio");

    private void ShowPanel(string panel)
    {
        PanelGeneral.IsVisible = panel == "general";
        PanelApi.IsVisible     = panel == "api";
        PanelMinio.IsVisible   = panel == "minio";
        SetNavActive(NavGeneralBorder, NavGeneralLabel, panel == "general");
        SetNavActive(NavApiBorder,     NavApiLabel,     panel == "api");
        SetNavActive(NavMinioBorder,   NavMinioLabel,   panel == "minio");
    }

    private static void SetNavActive(Border border, Label label, bool active)
    {
        border.BackgroundColor = active ? Color.FromArgb("#eff6ff") : Colors.Transparent;
        label.TextColor        = active ? Color.FromArgb("#2563eb") : Color.FromArgb("#374151");
        label.FontAttributes   = active ? FontAttributes.Bold : FontAttributes.None;
    }

    // ── General ───────────────────────────────────────────────────────────────

    private async void OnBrowseFolder(object sender, EventArgs e)
    {
        var result = await FolderPicker.Default.PickAsync(CancellationToken.None);
        if (result.IsSuccessful)
            VideoFolderEntry.Text = result.Folder.Path;
    }

    private void OnSaveGeneral(object sender, EventArgs e)
    {
        AppSettings.VideoFolder = VideoFolderEntry.Text?.Trim() ?? AppSettings.DefaultVideoFolder;
        AppSettings.WebhookUrl  = WebhookUrlEntry.Text?.Trim()  ?? AppSettings.DefaultWebhookUrl;
        Logger.Log($"Settings saved — VideoFolder: {AppSettings.VideoFolder}, WebhookUrl: {AppSettings.WebhookUrl}");
        GeneralSavedLabel.IsVisible = true;
        Dispatcher.DispatchDelayed(TimeSpan.FromSeconds(3), () => GeneralSavedLabel.IsVisible = false);
    }

    // ── MinIO Sync ────────────────────────────────────────────────────────────

    private void OnSaveMinio(object sender, EventArgs e)
    {
        AppSettings.MinioPcName    = MinioPcNameEntry.Text?.Trim()    ?? string.Empty;
        AppSettings.MinioBucket    = MinioBucketEntry.Text?.Trim()    ?? string.Empty;
        AppSettings.MinioEndpoint  = MinioEndpointEntry.Text?.Trim()  ?? string.Empty;
        AppSettings.MinioAccessKey = MinioAccessKeyEntry.Text?.Trim() ?? string.Empty;
        AppSettings.MinioSecretKey = MinioSecretKeyEntry.Text?.Trim() ?? string.Empty;

        string statusText;
        try
        {
            ScriptService.RegenerateScripts();
            Logger.Log("MinIO settings saved and scripts regenerated.");
            statusText = "✓ Saved — scripts updated";
        }
        catch (Exception ex)
        {
            Logger.Log($"ScriptService.RegenerateScripts failed: {ex.Message}");
            statusText = "✓ Saved — script update failed (check logs)";
        }

        MinioSavedLabel.Text      = statusText;
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
