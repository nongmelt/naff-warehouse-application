using System.Runtime.Versioning;
using System.Text.Json;

namespace app.Services;

[SupportedOSPlatform("windows")]
public static class AppSettings
{
    private const string KeyVideoFolder = "settings.video_folder";
    private const string KeyWebhookUrl  = "settings.webhook_url";
    private const string KeyApiUrl      = "settings.api_url";
    private const string KeySeeded      = "settings.seeded";

    public static readonly string DefaultVideoFolder =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Warehouse Videos"
        );

    public const string DefaultWebhookUrl =
        "http://localhost:5678/webhook-test/7842c780-4224-4c16-abb7-2973e1407835";

    public const string DefaultApiUrl = "http://localhost:8080";

    // ── First-run seeding ────────────────────────────────────────────────────

    /// <summary>
    /// Call once at startup before InitializeComponent.
    /// Reads appsettings.json from the install directory and seeds Preferences
    /// on the first run only. Subsequent launches skip this so user overrides
    /// via the Settings page are preserved.
    /// </summary>
    public static void Initialize()
    {
        if (Preferences.Default.Get(KeySeeded, false)) return;

        try
        {
            var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(configPath)) return;

            var doc = JsonDocument.Parse(File.ReadAllText(configPath)).RootElement;

            if (doc.TryGetProperty("webhookUrl", out var wh) &&
                !string.IsNullOrWhiteSpace(wh.GetString()))
                Preferences.Default.Set(KeyWebhookUrl, wh.GetString()!);

            if (doc.TryGetProperty("videoFolder", out var vf) &&
                !string.IsNullOrWhiteSpace(vf.GetString()))
                Preferences.Default.Set(KeyVideoFolder, vf.GetString()!);

            if (doc.TryGetProperty("apiUrl", out var api) &&
                !string.IsNullOrWhiteSpace(api.GetString()))
                Preferences.Default.Set(KeyApiUrl, api.GetString()!);

            Logger.Log("AppSettings: seeded from appsettings.json");
        }
        catch (Exception ex)
        {
            Logger.Log($"AppSettings.Initialize: {ex.Message}");
        }
        finally
        {
            // Mark seeded even on failure so we don't retry every launch
            Preferences.Default.Set(KeySeeded, true);
        }
    }

    // ── Settings ─────────────────────────────────────────────────────────────

    public static string VideoFolder
    {
        get => Preferences.Default.Get(KeyVideoFolder, DefaultVideoFolder);
        set => Preferences.Default.Set(KeyVideoFolder, value);
    }

    public static string WebhookUrl
    {
        get => Preferences.Default.Get(KeyWebhookUrl, DefaultWebhookUrl);
        set => Preferences.Default.Set(KeyWebhookUrl, value);
    }

    public static string ApiUrl
    {
        get => Preferences.Default.Get(KeyApiUrl, DefaultApiUrl);
        set => Preferences.Default.Set(KeyApiUrl, value);
    }
}
