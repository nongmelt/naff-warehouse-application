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

    // Long mask — fills the field width, hiding actual password length
    private const string PasswordMask = "••••••••••••••••••••••••••••••••••••••••••••••••••••••••••••";
    private bool _passwordEditing;

    public SettingsPage()
    {
        InitializeComponent();
        ShowPanel("general");
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
        DbPasswordEntry.Text  = string.IsNullOrEmpty(AppSettings.DbPassword) ? "" : PasswordMask;
        _passwordEditing = false;
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

    private void OnToggleErrorDetail(object sender, TappedEventArgs e)
    {
        ErrorDetailSection.IsVisible = !ErrorDetailSection.IsVisible;
        MoreDetailLabel.Text = ErrorDetailSection.IsVisible ? "▲ Less detail" : "▼ More detail";
    }

    private void OnPasswordFocused(object sender, FocusEventArgs e)
    {
        if (!_passwordEditing)
        {
            DbPasswordEntry.Text = "";
            _passwordEditing = true;
        }
    }

    private void OnPasswordUnfocused(object sender, FocusEventArgs e)
    {
        // If user cleared the field without typing, restore mask for existing password
        if (string.IsNullOrEmpty(DbPasswordEntry.Text) && !string.IsNullOrEmpty(AppSettings.DbPassword))
        {
            DbPasswordEntry.Text = PasswordMask;
            _passwordEditing = false;
        }
    }

    private void SaveDbSettings()
    {
        AppSettings.DbHost     = DbHostEntry.Text?.Trim() ?? "localhost";
        AppSettings.DbPort     = int.TryParse(DbPortEntry.Text?.Trim(), out var p) ? p : 5432;
        AppSettings.DbDatabase = DbDatabaseEntry.Text?.Trim() ?? "";
        AppSettings.DbUser     = DbUserEntry.Text?.Trim() ?? "";
        // Only update password if user actually typed a new one (not showing the mask)
        if (_passwordEditing || string.IsNullOrEmpty(AppSettings.DbPassword))
            AppSettings.DbPassword = DbPasswordEntry.Text ?? "";
        _passwordEditing = false;
        DbPasswordEntry.Text = string.IsNullOrEmpty(AppSettings.DbPassword) ? "" : PasswordMask;
        Logger.Log($"DB settings saved — Host: {AppSettings.DbHost}:{AppSettings.DbPort}, DB: {AppSettings.DbDatabase}");
    }

    private async Task TestAndShowResultAsync()
    {
        NotificationCard.IsVisible   = false;
        ErrorDetailSection.IsVisible = false;

        var (success, error) = await DatabaseService.TestConnectionAsync();

        Color cardBg, cardBorder, msgColor, retryBase, retryHover, retryText;
        if (success)
        {
            cardBg      = Color.FromArgb("#dcfce7");
            cardBorder  = Color.FromArgb("#86efac");
            msgColor    = Color.FromArgb("#15803d");
            retryBase   = Color.FromArgb("#dcfce7");
            retryHover  = Color.FromArgb("#86efac");
            retryText   = Color.FromArgb("#15803d");
        }
        else
        {
            cardBg      = Color.FromArgb("#fee2e2");
            cardBorder  = Color.FromArgb("#fca5a5");
            msgColor    = Color.FromArgb("#dc2626");
            retryBase   = Color.FromArgb("#fee2e2");
            retryHover  = Color.FromArgb("#fca5a5");
            retryText   = Color.FromArgb("#dc2626");
        }

        NotificationCard.BackgroundColor   = cardBg;
        NotificationCard.Stroke            = new SolidColorBrush(cardBorder);
        NotificationMessageLabel.Text      = success ? "Connection tested successfully" : "Couldn't connect with these settings";
        NotificationMessageLabel.TextColor = msgColor;
        MoreDetailLabel.IsVisible = !success;
        if (!success && error != null)
            ErrorDetailLabel.Text = error;

        // Style the Retry button to match the card
        _retryBase  = retryBase;
        _retryHover = retryHover;
        _retryText  = retryText;
        RetryButton.BackgroundColor = retryBase;
        RetryButton.BorderColor     = cardBorder;
        RetryButton.TextColor       = retryText;

        NotificationCard.IsVisible = true;
    }
}
