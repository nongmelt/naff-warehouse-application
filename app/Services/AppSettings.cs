using System.Runtime.Versioning;
using System.Text.Json;

namespace app.Services;

[SupportedOSPlatform("windows")]
public static class AppSettings
{
    private const string KeyVideoFolder = "settings.video_folder";
    private const string KeyVideoFolders = "settings.video_folders";            // semicolon-separated ordered list
    private const string KeyVideoFolderMinFreeSpace = "settings.video_folder_min_free_bytes";
    private const string KeyWebhookUrl = "settings.webhook_url";
    private const string KeyApiUrl = "settings.api_url";
    private const string KeyStationId = "settings.station_id";
    private const string KeyUseDeclarativeWorkflow = "settings.use_declarative_workflow";
    private const string KeySeeded = "settings.seeded.v3";

    private const string KeyMinioBucket = "settings.minio.bucket";
    private const string KeyMinioAccess = "settings.minio.access_key";
    private const string KeyMinioSecret = "settings.minio.secret_key";
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

                    // Seed ordered folder list (new multi-path feature)
                    if (doc.TryGetProperty("videoFolders", out var vfs) &&
                        vfs.ValueKind == JsonValueKind.Array)
                    {
                        var paths = vfs.EnumerateArray()
                            .Select(e => e.GetString())
                            .Where(s => !string.IsNullOrWhiteSpace(s))
                            .Select(s => s!)
                            .ToList();
                        if (paths.Count > 0)
                            Preferences.Default.Set(KeyVideoFolders, string.Join(";", paths));
                    }

                    if (doc.TryGetProperty("videoFolderMinFreeGb", out var minFree) &&
                        minFree.TryGetInt64(out var minFreeGb) && minFreeGb > 0)
                        Preferences.Default.Set(KeyVideoFolderMinFreeSpace, minFreeGb * 1_073_741_824L);

                    if (doc.TryGetProperty("apiUrl", out var api) &&
                        !string.IsNullOrWhiteSpace(api.GetString()))
                        Preferences.Default.Set(KeyApiUrl, api.GetString()!);

                    if (doc.TryGetProperty("stationId", out var sid) &&
                        !string.IsNullOrWhiteSpace(sid.GetString()))
                        Preferences.Default.Set(KeyStationId, sid.GetString()!);

                    if (doc.TryGetProperty("useDeclarativeWorkflow", out var udw) &&
                        udw.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        Preferences.Default.Set(KeyUseDeclarativeWorkflow, udw.GetBoolean());

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
                    SeedIfEmpty(KeyMinioBucket, minio, "bucket");
                    SeedIfEmpty(KeyMinioAccess, minio, "accessKey");
                    SeedIfEmpty(KeyMinioSecret, minio, "secretKey");
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

    /// <summary>
    /// Ordered list of video destination folders. Primary folder first, fallbacks after.
    /// Stored as a semicolon-separated string. Falls back to the legacy single-folder key
    /// for backward compatibility with existing installations.
    /// </summary>
    public static List<string> VideoFolders
    {
        get
        {
            var raw = Preferences.Default.Get(KeyVideoFolders, string.Empty);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var list = raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                if (list.Count > 0) return list;
            }
            // Legacy fallback: single folder key
            var legacy = Preferences.Default.Get(KeyVideoFolder, string.Empty);
            return string.IsNullOrWhiteSpace(legacy)
                ? [DefaultVideoFolder]
                : [legacy];
        }
        set
        {
            var filtered = (value ?? [])
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();
            Preferences.Default.Set(KeyVideoFolders, string.Join(";", filtered));
        }
    }

    /// <summary>
    /// Minimum free space (bytes) required on a drive before falling back to the next folder.
    /// Default: 10 GB.
    /// </summary>
    public static long VideoFolderMinFreeSpaceBytes
    {
        get => Preferences.Default.Get(KeyVideoFolderMinFreeSpace, 10_737_418_240L);
        set => Preferences.Default.Set(KeyVideoFolderMinFreeSpace, value);
    }

    /// <summary>
    /// First path from the VideoFolders list (backward-compatible alias used by
    /// LoadTodayVideoCountAsync and ScriptService which always reference the primary folder).
    /// Setting this replaces the entire list with a single entry.
    /// </summary>
    public static string VideoFolder
    {
        get => VideoFolders[0];
        set => VideoFolders = [value];
    }

    /// <summary>
    /// Evaluates the ordered VideoFolders list and returns the first path whose drive
    /// is accessible and has free space above VideoFolderMinFreeSpaceBytes.
    ///
    /// Fallback tiers:
    ///   1. First path with drive ready AND free space above threshold.
    ///   2. First accessible path (below threshold) — logs a warning, recording continues.
    ///   3. DefaultVideoFolder — logs critical, used only when no path is accessible at all.
    ///
    /// UNC paths (\\server\share\...): DriveInfo does not apply; accessibility is checked
    /// via Directory.Exists instead and the free-space threshold is skipped.
    /// </summary>
    public static string GetAvailableVideoFolder()
    {
        var paths = VideoFolders;
        var threshold = VideoFolderMinFreeSpaceBytes;
        string? firstAccessible = null;
        double firstAccessibleFreeGb = 0;

        for (int i = 0; i < paths.Count; i++)
        {
            var path = paths[i];
            var label = i == 0 ? "primary" : $"fallback #{i}";

            try
            {
                var root = Path.GetPathRoot(path);
                bool isUnc = path.StartsWith(@"\\") || path.StartsWith("//");

                if (isUnc)
                {
                    // For UNC paths: only check reachability, skip free-space threshold.
                    if (Directory.Exists(path))
                    {
                        Logger.Log($"[VideoFolder] Selected {path} ({label}, UNC — space check skipped)");
                        return path;
                    }
                    Logger.Log($"[VideoFolder] Skipping {path} ({label}): UNC path not reachable");
                    continue;
                }

                if (string.IsNullOrEmpty(root))
                {
                    Logger.Log($"[VideoFolder] Skipping {path} ({label}): cannot determine drive root");
                    continue;
                }

                var drive = new DriveInfo(root);
                if (!drive.IsReady)
                {
                    Logger.Log($"[VideoFolder] Skipping {path} ({label}): drive not ready");
                    continue;
                }

                var freeGb = drive.AvailableFreeSpace / 1_073_741_824.0;

                if (firstAccessible is null)
                {
                    firstAccessible = path;
                    firstAccessibleFreeGb = freeGb;
                }

                if (drive.AvailableFreeSpace >= threshold)
                {
                    Logger.Log($"[VideoFolder] Selected {path} ({label}): {freeGb:F1} GB free");
                    return path;
                }

                Logger.Log($"[VideoFolder] {path} ({label}): {freeGb:F1} GB free — below threshold, trying next");
            }
            catch (Exception ex)
            {
                Logger.Log($"[VideoFolder] Skipping {path} ({label}): {ex.Message}");
            }
        }

        if (firstAccessible is not null)
        {
            Logger.Log($"[VideoFolder] WARNING: All folders below threshold. Using first accessible {firstAccessible} ({firstAccessibleFreeGb:F1} GB free). Recording continues.");
            return firstAccessible;
        }

        Logger.Log($"[VideoFolder] CRITICAL: No accessible video folder found. Falling back to default {DefaultVideoFolder}");
        return DefaultVideoFolder;
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

    private const string KeySearchHistoryMax = "search.history.max";

    public static int SearchHistoryMaxItems
    {
        get => Preferences.Default.Get(KeySearchHistoryMax, 50);
        set => Preferences.Default.Set(KeySearchHistoryMax, Math.Max(1, value));
    }

    /// <summary>
    public static string StationId
    {
        get
        {
            var val = Preferences.Default.Get(KeyStationId, string.Empty);
            return string.IsNullOrWhiteSpace(val) ? Environment.MachineName : val;
        }
        set => Preferences.Default.Set(KeyStationId, value ?? string.Empty);
    }

    /// <summary>
    /// Resolved at startup from <c>GET /stations/by-computer/{name}</c>.
    /// Null until the async resolution completes; events emitted before that
    /// will have a null station_id (acceptable for the first few seconds).
    /// </summary>
    public static int? ResolvedStationId { get; set; }

    /// <summary>
    /// When true, host pages (StationView / OrderSearchPage) route trigger input
    /// through <c>WorkflowEngine</c> instead of the inline state-machine code.
    /// Kept as a kill-switch during rollout; once all stations have run green for
    /// a week the inline paths get removed.
    /// </summary>
    public static bool UseDeclarativeWorkflow
    {
        get => Preferences.Default.Get(KeyUseDeclarativeWorkflow, false);
        set => Preferences.Default.Set(KeyUseDeclarativeWorkflow, value);
    }
}
