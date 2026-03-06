using app.Services;
using CommunityToolkit.Maui.Storage;
using System.Runtime.Versioning;

namespace app.Views;

[SupportedOSPlatform("windows")]
public partial class SettingsPage : ContentPage
{
    public SettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        VideoFolderEntry.Text = AppSettings.VideoFolder;
        WebhookUrlEntry.Text  = AppSettings.WebhookUrl;
        DbHostEntry.Text      = AppSettings.DbHost;
        DbPortEntry.Text      = AppSettings.DbPort.ToString();
        DbDatabaseEntry.Text  = AppSettings.DbDatabase;
        DbUserEntry.Text      = AppSettings.DbUser;
        DbPasswordEntry.Text  = AppSettings.DbPassword;
    }

    // ── Navigation ───────────────────────────────────────────────────────────

    private async void OnCancel(object sender, EventArgs e) =>
        await Navigation.PopModalAsync();

    // ── Sidebar ───────────────────────────────────────────────────────────────

    private void OnNavGeneral(object sender, TappedEventArgs e)  => ShowPanel("general");
    private void OnNavPostgres(object sender, TappedEventArgs e) => ShowPanel("postgres");

    private void ShowPanel(string panel)
    {
        PanelGeneral.IsVisible  = panel == "general";
        PanelPostgres.IsVisible = panel == "postgres";
        SetNavActive(NavGeneralBorder,  NavGeneralLabel,  panel == "general");
        SetNavActive(NavPostgresBorder, NavPostgresLabel, panel == "postgres");
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

    // ── PostgreSQL ────────────────────────────────────────────────────────────

    private async void OnSavePostgres(object sender, EventArgs e)
    {
        SaveDbSettings();
        await TestAndShowResultAsync();
    }

    private async void OnRetryConnection(object sender, EventArgs e)
    {
        SaveDbSettings();
        await TestAndShowResultAsync();
    }

    private void OnToggleErrorDetail(object sender, TappedEventArgs e)
    {
        ErrorDetailSection.IsVisible = !ErrorDetailSection.IsVisible;
        MoreDetailLabel.Text = ErrorDetailSection.IsVisible ? "▲ Less detail" : "▼ More detail";
    }

    private void SaveDbSettings()
    {
        AppSettings.DbHost     = DbHostEntry.Text?.Trim() ?? "localhost";
        AppSettings.DbPort     = int.TryParse(DbPortEntry.Text?.Trim(), out var p) ? p : 5432;
        AppSettings.DbDatabase = DbDatabaseEntry.Text?.Trim() ?? "";
        AppSettings.DbUser     = DbUserEntry.Text?.Trim() ?? "";
        AppSettings.DbPassword = DbPasswordEntry.Text ?? "";
        Logger.Log($"DB settings saved — Host: {AppSettings.DbHost}:{AppSettings.DbPort}, DB: {AppSettings.DbDatabase}");
    }

    private async Task TestAndShowResultAsync()
    {
        NotificationCard.IsVisible   = false;
        ErrorDetailSection.IsVisible = false;

        var (success, error) = await DatabaseService.TestConnectionAsync();

        NotificationCard.BackgroundColor = success ? Color.FromArgb("#dcfce7") : Color.FromArgb("#fee2e2");
        NotificationCard.Stroke          = new SolidColorBrush(success ? Color.FromArgb("#86efac") : Color.FromArgb("#fca5a5"));
        NotificationMessageLabel.Text      = success ? "Connection tested successfully" : "Couldn't connect with these settings";
        NotificationMessageLabel.TextColor = success ? Color.FromArgb("#15803d") : Color.FromArgb("#dc2626");
        MoreDetailLabel.IsVisible = !success;
        if (!success && error != null)
            ErrorDetailLabel.Text = error;

        NotificationCard.IsVisible = true;
    }
}
