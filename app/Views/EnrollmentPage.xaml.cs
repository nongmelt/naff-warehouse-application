using System.Runtime.Versioning;
using app.Services;

namespace app.Views;

/// <summary>
/// First-launch enrollment page. Shown as the root page when no MinIO credentials
/// exist yet (boot gate C4). The operator presses Connect once — EnrollClient handles
/// the POST and mints MinIO creds. No codes are typed.
///
/// EnrollResult routing:
///   Success    → navigate into AppShell
///   Rejected   → show "ask an admin to add it" + Retry
///   Unreachable → show "can't reach server" + Retry
/// </summary>
[SupportedOSPlatform("windows")]
public partial class EnrollmentPage : ContentPage
{
    public EnrollmentPage()
    {
        InitializeComponent();

        // Show station name for context; fall back to neutral placeholder.
        var name = AppSettings.StationName;
        StationNameLabel.Text = string.IsNullOrWhiteSpace(name) ? "this station" : name;

        // Show the configured API host so the operator knows which server is targeted.
        ServerHostLabel.Text = HostOf(AppSettings.ApiUrl);
    }

    // ── Button handler ────────────────────────────────────────────────────────

    private async void OnConnectClicked(object? sender, EventArgs e)
    {
        // Busy state: disable button, show spinner, clear status.
        ActionButton.IsEnabled = false;
        ActionButton.Text = "Connecting…";
        ConnSpinner.IsVisible = true;
        ConnSpinner.IsRunning = true;
        ConnPinGlyph.IsVisible = false;
        StatusLabel.Text = "Connecting to the warehouse… Verifying this station and minting storage keys.";
        StatusLabel.TextColor = Color.FromArgb("#4326a8");
        StatusBorder.BackgroundColor = Color.FromArgb("#f0ecfd");
        StatusDot.Fill = Color.FromArgb("#512BD4");

        EnrollResult result;
        try
        {
            result = await EnrollClient.EnrollAsync();
        }
        catch (Exception ex)
        {
            Logger.Log($"EnrollmentPage: unexpected error — {ex.Message}");
            result = EnrollResult.Unreachable;
        }

        // Hide spinner regardless of outcome.
        ConnSpinner.IsVisible = false;
        ConnSpinner.IsRunning = false;
        ConnPinGlyph.IsVisible = true;

        switch (result)
        {
            case EnrollResult.Success:
                ConnPin.Stroke = Color.FromArgb("#16a34a");
                ConnPinGlyph.Text = "✓";
                ConnPinGlyph.TextColor = Colors.White;
                ConnPin.BackgroundColor = Color.FromArgb("#16a34a");
                ServerNode.BackgroundColor = Color.FromArgb("#ecfdf3");
                ServerNode.Stroke = Color.FromArgb("#bbf7d0");
                StatusBorder.BackgroundColor = Color.FromArgb("#ecfdf3");
                StatusDot.Fill = Color.FromArgb("#16a34a");
                StatusLabel.Text = "Connected. This station is registered and ready. Opening the start page…";
                StatusLabel.TextColor = Color.FromArgb("#15803d");
                ActionButton.IsVisible = false;
                await Task.Delay(900);
                ProceedToApp();
                return;

            case EnrollResult.Rejected:
                StatusBorder.BackgroundColor = Color.FromArgb("#fff7ed");
                StatusDot.Fill = Color.FromArgb("#b45309");
                StatusLabel.Text = "This station isn't registered yet. Ask an admin to add it, then tap Retry.";
                StatusLabel.TextColor = Color.FromArgb("#9a4708");
                ActionButton.Text = "Retry";
                ActionButton.IsEnabled = true;
                return;

            case EnrollResult.Unreachable:
            default:
                StatusBorder.BackgroundColor = Color.FromArgb("#eff6ff");
                StatusDot.Fill = Color.FromArgb("#9ca3af");
                StatusLabel.Text = "Can't reach the server. Check the connection and tap Retry.";
                StatusLabel.TextColor = Color.FromArgb("#1e40af");
                ActionButton.Text = "Retry";
                ActionButton.IsEnabled = true;
                return;
        }
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    private static void ProceedToApp()
    {
        if (Application.Current?.Windows.Count > 0)
            Application.Current.Windows[0].Page = new AppShell();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Returns host:port from a URL, or the raw string if parsing fails.</summary>
    private static string HostOf(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var u)
            ? (u.IsDefaultPort ? u.Host : $"{u.Host}:{u.Port}")
            : url;
    }
}
