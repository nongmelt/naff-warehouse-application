using System.Runtime.Versioning;
using System.Text.Json;

namespace app.Services;

[SupportedOSPlatform("windows")]
public static class AppSettings
{
    private const string KeyVideoFolder = "settings.video_folder";
    private const string KeyWebhookUrl = "settings.webhook_url";
    private const string KeyApiUrl = "settings.api_url";
    private const string KeySeeded = "settings.seeded.v3";

    private const string KeyMinioBucket   = "settings.minio.bucket";
    private const string KeyMinioAccess   = "settings.minio.access_key";
    private const string KeyMinioSecret   = "settings.minio.secret_key";
    private const string KeyMinioEndpoint = "settings.minio.endpoint";

    public static readonly string DefaultVideoFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Warehouse");

    public const string DefaultWebhookUrl =
        "http://localhost:5678/webhook-test/7842c780-4224-4c16-abb7-2973e1407835";

    public const string DefaultApiUrl = "http://localhost:8080";

    // ── Startup seeding ──────────────────────────────────────────────────────

    /// <summary>
    /// Called once at startup. Seeds Preferences from appsettings.json:
    /// - General settings: seeded once (flag-gated so user overrides are preserved).
    /// - MinIO settings: seeded whenever a field is empty, so reinstalls always
    ///   pick up new credentials without relying on a flag that persists in the registry.
    /// </summary>
    public static void Initialize()
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

        // ── General (webhook, videoFolder, apiUrl) — seed once ───────────────
        if (!Preferences.Default.Get(KeySeeded, false))
        {
            try
            {
                if (File.Exists(configPath))
                {
                    var doc = JsonDocument.Parse(File.ReadAllText(configPath)).RootElement;

                    if (doc.TryGetProperty("webhookUrl", out var wh) &&
                        !string.IsNullOrWhiteSpace(wh.GetString()))
                        Preferences.Default.Set(KeyWebhookUrl, wh.GetString()!);

                    if (doc.TryGetProperty("videoFolder", out var vf) &&
                        !string.IsNullOrWhiteSpace(vf.GetString()))
                        Preferences.Default.Set(KeyVideoFolder, vf.GetString()!);
                    else
                        Preferences.Default.Remove(KeyVideoFolder);

                    if (doc.TryGetProperty("apiUrl", out var api) &&
                        !string.IsNullOrWhiteSpace(api.GetString()))
                        Preferences.Default.Set(KeyApiUrl, api.GetString()!);

                    Logger.Log("AppSettings: seeded general settings from appsettings.json");
                }
            }
            catch (Exception ex) { Logger.Log($"AppSettings.Initialize: {ex.Message}"); }
            finally { Preferences.Default.Set(KeySeeded, true); }
        }

        // ── MinIO — seed any empty field from appsettings.json ───────────────
        // No flag: runs every launch so reinstalls always pick up new credentials.
        // User overrides in Settings are preserved because we only write when empty.
        try
        {
            if (File.Exists(configPath))
            {
                var doc = JsonDocument.Parse(File.ReadAllText(configPath)).RootElement;
                if (doc.TryGetProperty("minio", out var minio))
                {
                    SeedIfEmpty(KeyMinioBucket,   minio, "bucket");
                    SeedIfEmpty(KeyMinioAccess,   minio, "accessKey");
                    SeedIfEmpty(KeyMinioSecret,   minio, "secretKey");
                    SeedIfEmpty(KeyMinioEndpoint, minio, "endpoint");
                    Logger.Log("AppSettings: MinIO fields synced from appsettings.json");
                }
            }
        }
        catch (Exception ex) { Logger.Log($"AppSettings.InitializeMinio: {ex.Message}"); }
    }

    private static void SeedIfEmpty(string key, JsonElement parent, string property)
    {
        if (parent.TryGetProperty(property, out var val) &&
            !string.IsNullOrWhiteSpace(val.GetString()) &&
            string.IsNullOrWhiteSpace(Preferences.Default.Get(key, string.Empty)))
            Preferences.Default.Set(key, val.GetString()!);
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

    public static string MinioBucket
    {
        get => Preferences.Default.Get(KeyMinioBucket, string.Empty);
        set => Preferences.Default.Set(KeyMinioBucket, value);
    }

    public static string MinioAccessKey
    {
        get => Preferences.Default.Get(KeyMinioAccess, string.Empty);
        set => Preferences.Default.Set(KeyMinioAccess, value);
    }

    public static string MinioSecretKey
    {
        get => Preferences.Default.Get(KeyMinioSecret, string.Empty);
        set => Preferences.Default.Set(KeyMinioSecret, value);
    }

    public static string MinioEndpoint
    {
        get => Preferences.Default.Get(KeyMinioEndpoint, string.Empty);
        set => Preferences.Default.Set(KeyMinioEndpoint, value);
    }
}
