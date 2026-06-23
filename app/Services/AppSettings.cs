using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace app.Services;

[SupportedOSPlatform("windows")]
public static class AppSettings
{
    private const string KeyVideoFolder = "settings.video_folder";
    private const string KeyVideoFolders = "settings.video_folders";            // semicolon-separated ordered list
    private const string KeyVideoFolderMinFreeSpace = "settings.video_folder_min_free_bytes";
    private const string KeyApiUrl = "settings.api_url";
    private const string KeyStationId = "settings.station_id";
    private const string KeyUseDeclarativeWorkflow = "settings.use_declarative_workflow";
    private const string KeyAutoDeleteCompletedVideos = "settings.auto_delete_completed_videos";
    private const string KeyMaxConcurrentUploads = "settings.max_concurrent_uploads";
    private const string KeySeeded = "settings.seeded.v3";

    private const string KeyMinioBucket = "settings.minio.bucket";
    private const string KeyMinioAccess = "settings.minio.access_key";
    private const string KeyMinioSecret = "settings.minio.secret_key";
    private const string KeyMinioEndpoint = "settings.minio.endpoint";
    private const string KeyMinioRawBucket = "settings.minio.raw_bucket";

    /// <summary>Default bucket for stranded (orphan) videos with no packing_lists row.</summary>
    public const string DefaultMinioRawBucket = "warehouse-raw";

    private const string KeyCfAccessClientId = "settings.cf.access_client_id";
    private const string KeyCfAccessClientSecret = "settings.cf.access_client_secret";

    public static readonly string DefaultVideoFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Warehouse");

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

                    if (doc.TryGetProperty("autoDeleteCompletedVideos", out var adv) &&
                        adv.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        Preferences.Default.Set(KeyAutoDeleteCompletedVideos, adv.GetBoolean());

                    if (doc.TryGetProperty("maxConcurrentUploads", out var mcu) &&
                        mcu.TryGetInt32(out var mcuVal) && mcuVal > 0)
                        Preferences.Default.Set(KeyMaxConcurrentUploads, mcuVal);

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
                    SeedIfEmpty(KeyMinioRawBucket, minio, "rawBucket");
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
    /// First path from the VideoFolders list (backward-compatible alias).
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

    public static bool AutoDeleteCompletedVideos
    {
        get => Preferences.Default.Get(KeyAutoDeleteCompletedVideos, false);
        set => Preferences.Default.Set(KeyAutoDeleteCompletedVideos, value);
    }

    public static int MaxConcurrentUploads
    {
        get => Math.Max(1, Preferences.Default.Get(KeyMaxConcurrentUploads, 3));
        set => Preferences.Default.Set(KeyMaxConcurrentUploads, Math.Max(1, value));
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

    /// <summary>
    /// Bucket for orphan (order-less) videos. Defaults to "warehouse-raw" so the
    /// orphan path works even on installs whose appsettings.json predates rawBucket.
    /// </summary>
    public static string MinioRawBucket
    {
        get
        {
            var v = Preferences.Default.Get(KeyMinioRawBucket, string.Empty);
            return string.IsNullOrWhiteSpace(v) ? DefaultMinioRawBucket : v;
        }
        set => Preferences.Default.Set(KeyMinioRawBucket, value);
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

    // ── Cloudflare Access service token ────────────────────────────────────────
    // Sent as CF-Access-Client-Id / CF-Access-Client-Secret on every backend request
    // when present. Resolution order: the value saved in Settings (Preferences) first,
    // then the CF_ACCESS_CLIENT_ID / CF_ACCESS_CLIENT_SECRET environment variables,
    // otherwise empty. Empty means the header is simply not sent, so a backend that is
    // not behind Cloudflare Access keeps working.

    public static string CfAccessClientId
    {
        get
        {
            var stored = Preferences.Default.Get(KeyCfAccessClientId, string.Empty);
            return !string.IsNullOrWhiteSpace(stored)
                ? stored
                : Environment.GetEnvironmentVariable("CF_ACCESS_CLIENT_ID") ?? string.Empty;
        }
        set => Preferences.Default.Set(KeyCfAccessClientId, value ?? string.Empty);
    }

    public static string CfAccessClientSecret
    {
        get
        {
            var stored = Preferences.Default.Get(KeyCfAccessClientSecret, string.Empty);
            return !string.IsNullOrWhiteSpace(stored)
                ? stored
                : Environment.GetEnvironmentVariable("CF_ACCESS_CLIENT_SECRET") ?? string.Empty;
        }
        set => Preferences.Default.Set(KeyCfAccessClientSecret, value ?? string.Empty);
    }

    private const string KeySearchHistoryMax = "search.history.max";
    private const string KeyOperatorBranchCodes = "settings.operator.branch_codes";
    private const string KeyOperatorPositionCodes = "settings.operator.position_codes";
    private const string KeyPackingInactivityMinutes = "settings.operator.packing_inactivity_minutes";
    private const string KeyQcInactivityMinutes = "settings.operator.qc_inactivity_minutes";

    public static int SearchHistoryMaxItems
    {
        get => Preferences.Default.Get(KeySearchHistoryMax, 50);
        set => Preferences.Default.Set(KeySearchHistoryMax, Math.Max(1, value));
    }

    /// <summary>
    /// Allowed branch codes. Must be exactly 3 uppercase letters (e.g. ["BKK", "CMI"]).
    /// Defaults to ["BKK"] when no value is stored.
    /// </summary>
    public static List<string> OperatorBranchCodes
    {
        get
        {
            var raw = Preferences.Default.Get(KeyOperatorBranchCodes, string.Empty);
            if (string.IsNullOrWhiteSpace(raw))
                return ["BKK"];
            var codes = raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                           .Select(s => s.ToUpperInvariant())
                           .Where(s => s.Length == 3 && s.All(char.IsAsciiLetterUpper))
                           .ToList();
            return codes.Count > 0 ? codes : ["BKK"];
        }
        set => Preferences.Default.Set(KeyOperatorBranchCodes,
            string.Join(";", (value ?? [])
                .Select(s => s.Trim().ToUpperInvariant())
                .Where(s => s.Length == 3 && s.All(char.IsAsciiLetterUpper))));
    }

    /// <summary>
    /// Allowed position codes in badges (e.g. ["PK", "QC", "SAL", "ACC", "WH"]).
    /// Defaults to PK, QC, SAL, ACC, WH when empty.
    /// </summary>
    public static List<string> OperatorPositionCodes
    {
        get
        {
            var raw = Preferences.Default.Get(KeyOperatorPositionCodes, string.Empty);
            return string.IsNullOrWhiteSpace(raw)
                ? ["PK", "QC", "SAL", "ACC", "WH"]
                : [.. raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                         .Select(s => s.ToUpperInvariant())];
        }
        set => Preferences.Default.Set(KeyOperatorPositionCodes,
            string.Join(";", (value ?? []).Select(s => s.Trim().ToUpperInvariant()).Where(s => s.Length > 0)));
    }

    public static int PackingInactivityMinutes
    {
        get => Preferences.Default.Get(KeyPackingInactivityMinutes, 120);
        set => Preferences.Default.Set(KeyPackingInactivityMinutes, Math.Max(1, value));
    }

    public static int QcInactivityMinutes
    {
        get => Preferences.Default.Get(KeyQcInactivityMinutes, 40);
        set => Preferences.Default.Set(KeyQcInactivityMinutes, Math.Max(1, value));
    }

    /// <summary>
    /// Returns the regex pattern built from current branch and position settings.
    /// Example: ^\d{2}(BKK|CMI)(PK|QC|SAL|ACC|WH)\d{3}$
    /// </summary>
    public static string BuildBadgePattern()
    {
        var branches = OperatorBranchCodes;
        var positions = OperatorPositionCodes;

        var branchPart = branches.Count > 0
            ? $"(?<branch>{string.Join("|", branches.Select(Regex.Escape))})"
            : @"(?<branch>[A-Z]{3})";

        var positionPart = positions.Count > 0
            ? $"(?<position>{string.Join("|", positions.Select(Regex.Escape))})"
            : "(?<position>PK|QC|SAL|ACC|WH)";

        return $@"^\d{{2}}{branchPart}{positionPart}\d{{3}}$";
    }

    /// <summary>
    /// Parses a structured operator badge using the regex built from settings.
    /// Format: YYBBBPPPnnn — year(2) branch(3) position(2-3) employee(3).
    /// Returns null if barcode does not match.
    /// </summary>
    public static OperatorBadge? TryParseOperatorBarcode(string barcode)
    {
        var match = Regex.Match(barcode, BuildBadgePattern(), RegexOptions.IgnoreCase);
        if (!match.Success) return null;

        var branch = match.Groups["branch"].Value.ToUpperInvariant();
        var position = match.Groups["position"].Value.ToUpperInvariant();
        var employee = barcode[^3..];

        return new OperatorBadge(branch, position, employee);
    }

    /// <summary>
    /// KEX QR codes embed extra fields after the tracking number (e.g. "KEXLM1000234185 1 1 DJD001").
    /// If the barcode starts with "KEX", return only the first space-delimited token.
    /// All other barcodes pass through unchanged.
    /// </summary>
    public static string NormalizeTrackingNumber(string barcode)
    {
        if (!barcode.StartsWith("KEX", StringComparison.OrdinalIgnoreCase))
            return barcode;

        var spaceIdx = barcode.IndexOf(' ');
        return spaceIdx > 0 ? barcode[..spaceIdx] : barcode;
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
    /// Resolved at startup from GET /stations/by-station/{MachineName}.
    /// Awaitable via <see cref="StationIdReady"/> — do not read this directly
    /// before resolution completes.
    /// </summary>
    public static int? ResolvedStationId { get; private set; }

    private static readonly TaskCompletionSource<int?> _stationIdTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes when station ID resolution finishes. Await before using
    /// <see cref="ResolvedStationId"/> in scan-critical paths.
    /// </summary>
    public static Task<int?> StationIdReady => _stationIdTcs.Task;

    /// <summary>
    /// Called once by App.xaml.cs after ResolveStationIdAsync returns.
    /// </summary>
    public static void CompleteStationResolution(int? id)
    {
        ResolvedStationId = id;
        _stationIdTcs.TrySetResult(id);
    }

    /// <summary>
    /// Returns cached station ID if non-null, otherwise retries resolution once.
    /// Use at points that need station_id (video creation, status updates).
    /// </summary>
    public static async Task<int?> EnsureStationIdAsync()
    {
        if (ResolvedStationId is not null) return ResolvedStationId;

        var id = await ApiService.ResolveStationIdAsync(Environment.MachineName);
        if (id is not null)
            ResolvedStationId = id;
        return id;
    }

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

/// <summary>
/// Parsed operator badge. Position is the raw code string (e.g. "PK", "QC", "SAL", "ACC", "WH").
/// DisplayName is shown in UI and stored as packed_by / checked_by.
/// </summary>
public sealed record OperatorBadge(string Branch, string Position, string EmployeeNumber)
{
    public string DisplayName => $"{Position}{EmployeeNumber}";
}
