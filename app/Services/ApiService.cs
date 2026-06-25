using app.Models;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

namespace app.Services;

[SupportedOSPlatform("windows")]
public static class ApiService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static HttpClient? _http;
    private static string _httpBase = "";
    private static string _httpCfId = "";
    private static string _httpCfSecret = "";

    private static HttpClient Http
    {
        get
        {
            var url = (AppSettings.ApiUrl?.TrimEnd('/') ?? "http://localhost:8080") + "/";
            var cfId = AppSettings.CfAccessClientId;
            var cfSecret = AppSettings.CfAccessClientSecret;

            // Rebuild when the URL or the Cloudflare Access token changes so edits made in
            // Settings take effect without restarting the app.
            if (_http is null || _httpBase != url || _httpCfId != cfId || _httpCfSecret != cfSecret)
            {
                _http?.Dispose();
                _http = new HttpClient
                {
                    BaseAddress = new Uri(url),
                    Timeout = TimeSpan.FromSeconds(30),
                };

                // Cloudflare Access service-token headers — only added when present, so the
                // app works fine against a backend with no Cloudflare Access in front of it
                // (both values empty/null => no headers sent).
                if (!string.IsNullOrWhiteSpace(cfId))
                    _http.DefaultRequestHeaders.Add("CF-Access-Client-Id", cfId);
                if (!string.IsNullOrWhiteSpace(cfSecret))
                    _http.DefaultRequestHeaders.Add("CF-Access-Client-Secret", cfSecret);

                _httpBase = url;
                _httpCfId = cfId;
                _httpCfSecret = cfSecret;
            }
            return _http;
        }
    }

    internal static HttpClient GetHttpClient() => Http;

    // ── Operator lookup ───────────────────────────────────────────────────────

    /// <summary>
    /// Resolves a staff_code QR scan to the operator's first name.
    /// Returns null on 404 or network failure — caller falls back to staff_code display.
    /// </summary>
    public static async Task<string?> GetOperatorFirstNameAsync(string staffCode)
    {
        try
        {
            var resp = await Http.GetAsync($"operator-lists/by-staff-code/{Uri.EscapeDataString(staffCode)}");
            if (!resp.IsSuccessStatusCode) return null;
            var node = await resp.Content.ReadFromJsonAsync<JsonNode>(JsonOpts);
            return node?["nickname"]?.GetValue<string>()
                ?? node?["firstName"]?.GetValue<string>();
        }
        catch (Exception ex)
        {
            Logger.Log($"ApiService.GetOperatorFirstNameAsync: {ex.Message}");
            return null;
        }
    }

    public static async Task<(string? FirstName, int? Id)> GetOperatorInfoAsync(string staffCode)
    {
        try
        {
            var resp = await Http.GetAsync($"operator-lists/by-staff-code/{Uri.EscapeDataString(staffCode)}");
            if (!resp.IsSuccessStatusCode) return (null, null);
            var node = await resp.Content.ReadFromJsonAsync<JsonNode>(JsonOpts);
            var name = node?["nickname"]?.GetValue<string>()
                    ?? node?["firstName"]?.GetValue<string>();
            return (name, node?["id"]?.GetValue<int>());
        }
        catch (Exception ex)
        {
            Logger.Log($"ApiService.GetOperatorInfoAsync: {ex.Message}");
            return (null, null);
        }
    }

    private static readonly Dictionary<string, string?> _nicknameCache = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<string?> ResolveOperatorNicknameAsync(string? staffCode)
    {
        if (string.IsNullOrWhiteSpace(staffCode)) return null;
        if (_nicknameCache.TryGetValue(staffCode, out var cached)) return cached;
        var name = await GetOperatorFirstNameAsync(staffCode);
        _nicknameCache[staffCode] = name;
        return name;
    }

    // ── Station resolution ────────────────────────────────────────────────────

    /// <summary>
    /// Resolves this computer's name to its integer station FK.
    /// Called once at startup; result cached in <see cref="AppSettings.ResolvedStationId"/>.
    /// </summary>
    public static async Task<int?> ResolveStationIdAsync(string computerName)
    {
        try
        {
            var resp = await Http.GetAsync($"stations/by-station/{Uri.EscapeDataString(computerName)}");
            if (!resp.IsSuccessStatusCode) return null;
            var node = await resp.Content.ReadFromJsonAsync<JsonNode>(JsonOpts);
            return node?["id"]?.GetValue<int>();
        }
        catch (Exception ex)
        {
            Logger.Log($"ApiService.ResolveStationIdAsync: {ex.Message}");
            return null;
        }
    }

    // ── Heartbeat ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Fires a one-way heartbeat to inform the backend this station is alive.
    /// Swallows all exceptions — failure is silent (backend marks station offline).
    /// </summary>
    public static async Task SendHeartbeatAsync(int stationId)
    {
        try
        {
            await Http.PostAsync($"stations/{stationId}/heartbeat", null);
        }
        catch
        {
            // silent — heartbeat loss just marks station offline in the dashboard
        }
    }

    // ── Health / connection test ──────────────────────────────────────────────

    public static async Task<(bool Success, string? Error)> TestConnectionAsync()
    {
        try
        {
            var resp = await Http.GetAsync("health");
            return resp.IsSuccessStatusCode ? (true, null) : (false, $"HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            Logger.Log($"ApiService.TestConnectionAsync: {ex.Message}");
            return (false, ex.Message);
        }
    }

    // ── Search ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Outcome of a search that distinguishes a successful (possibly empty) response
    /// from a failed call. <see cref="Succeeded"/> is false only when the HTTP call
    /// itself errored (network/timeout/non-2xx) — never for a genuine empty match.
    /// </summary>
    public readonly record struct SearchOutcome(bool Succeeded, List<PackingList> Results);

    public static async Task<List<PackingList>> SearchAsync(string input)
        => (await SearchResultAsync(input)).Results;

    /// <summary>
    /// Search variant for the orphan-recovery path: returns Succeeded=true with an
    /// (empty) list only when the backend genuinely responded; Succeeded=false on
    /// any error so the caller can leave the file on disk instead of orphaning it.
    /// </summary>
    public static async Task<SearchOutcome> SearchResultAsync(string input)
    {
        try
        {
            var url = $"packing-lists?q={Uri.EscapeDataString(input)}";
            var resp = await Http.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
            {
                Logger.Log($"ApiService.SearchResultAsync: HTTP {(int)resp.StatusCode}");
                return new SearchOutcome(false, []);
            }
            var list = await resp.Content.ReadFromJsonAsync<List<PackingList>>(JsonOpts);
            return new SearchOutcome(true, list ?? []);
        }
        catch (Exception ex)
        {
            Logger.Log($"ApiService.SearchResultAsync: {ex.Message}");
            return new SearchOutcome(false, []);
        }
    }

    // ── Update status ─────────────────────────────────────────────────────────

    public static async Task<bool> UpdatePackingStatusAsync(
        int packingId, string status, ProductListPayload updatedProductLists,
        string? checkedBy = null, int? checkingStationId = null)
    {
        try
        {
            var body = new StatusRequest(status, updatedProductLists, checkedBy?.Replace(' ', '-'), checkingStationId);
            var json = JsonSerializer.Serialize(body, JsonOpts);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await Http.PatchAsync($"packing-lists/{packingId}/status", content);
            if (!resp.IsSuccessStatusCode)
                Logger.Log($"ApiService.UpdatePackingStatusAsync: HTTP {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Logger.Log($"ApiService.UpdatePackingStatusAsync: {ex.Message}");
            return false;
        }
    }

    // ── Reset QC Hold ─────────────────────────────────────────────────────────

    public static async Task<bool> ResetQcHoldAsync(int packingId)
    {
        try
        {
            var resp = await Http.PostAsync($"packing-lists/{packingId}/reset", null);
            if (!resp.IsSuccessStatusCode)
                Logger.Log($"ApiService.ResetQcHoldAsync: HTTP {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Logger.Log($"ApiService.ResetQcHoldAsync: {ex.Message}");
            return false;
        }
    }

    // ── Videos ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a video record in the backend and classifies the outcome:
    /// Created (2xx, with id), NoPackingList (HTTP 404 = definitive: no packing_lists
    /// row → caller routes to the orphan path), or Failed (any other code / network
    /// error → caller leaves the file on disk and retries later). 404 is the ONLY
    /// orphan signal; 422 (sqlx FK/constraint) and 5xx map to Failed so we never
    /// orphan on an ambiguous failure.
    /// </summary>
    public static async Task<CreateVideoResult> CreateVideoResultAsync(
        string trackingNumber, string filePath, int? stationId, string @operator)
    {
        try
        {
            var body = new CreateVideoRequest(trackingNumber, filePath, Path.GetFileName(filePath), stationId, @operator);
            var json = JsonSerializer.Serialize(body, JsonOpts);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await Http.PostAsync("videos", content);

            var kind = OrphanCapture.ClassifyCreateVideoStatus((int)resp.StatusCode);
            if (kind == CreateVideoResultKind.Created)
            {
                var result = await resp.Content.ReadFromJsonAsync<VideoRecord>(JsonOpts);
                if (result is not null && result.Id > 0)
                    return CreateVideoResult.Created(result.Id);
                Logger.Log("ApiService.CreateVideoResultAsync: 2xx but missing/invalid id in body");
                return CreateVideoResult.Failed;
            }

            if (kind == CreateVideoResultKind.NoPackingList)
            {
                Logger.Log($"ApiService.CreateVideoResultAsync: HTTP 404 for '{trackingNumber}' — no packing_lists row (orphan)");
                return CreateVideoResult.NoPackingList;
            }

            Logger.Log($"ApiService.CreateVideoResultAsync: HTTP {(int)resp.StatusCode} — leaving on disk");
            return CreateVideoResult.Failed;
        }
        catch (Exception ex)
        {
            Logger.Log($"ApiService.CreateVideoResultAsync: {ex.Message}");
            return CreateVideoResult.Failed;
        }
    }

    /// <summary>
    /// Backwards-compatible wrapper: returns the new record id, or -1 on failure
    /// (including 404). Existing call sites that only need ">0" keep working.
    /// New callers that must distinguish a 404 should use
    /// <see cref="CreateVideoResultAsync"/>.
    /// </summary>
    public static async Task<int> CreateVideoRecordAsync(
        string trackingNumber, string filePath, int? stationId, string @operator)
    {
        var result = await CreateVideoResultAsync(trackingNumber, filePath, stationId, @operator);
        return result.Kind == CreateVideoResultKind.Created ? result.VideoId : -1;
    }

    /// <summary>
    /// Updates packing status by scan input (tracking number or order number).
    /// Pass packedBy only when status is "Packed".
    /// </summary>
    public static async Task<bool> UpdatePackingStatusByScanAsync(
        string barcode, string status, string? packedBy = null, int? packingStationId = null)
    {
        try
        {
            var body = new ScanStatusRequest(status, packedBy?.Replace(' ', '-'), packingStationId);
            var json = JsonSerializer.Serialize(body, JsonOpts);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await Http.PatchAsync(
                $"packing-lists/scan/{Uri.EscapeDataString(barcode)}/status", content);
            if (!resp.IsSuccessStatusCode)
                Logger.Log($"ApiService.UpdatePackingStatusByScanAsync: HTTP {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Logger.Log($"ApiService.UpdatePackingStatusByScanAsync: {ex.Message}");
            return false;
        }
    }

    public readonly record struct ShipResult(int Status, bool PackedBackfilled, bool AlreadyShipped);

    public static async Task<ShipResult> ShipAsync(
        string barcode, string? shippedBy = null, int? shippingStationId = null,
        bool force = false, string? forcedBy = null)
    {
        try
        {
            var body = new { shippedBy = shippedBy?.Replace(' ', '-'), shippingStationId, force, forcedBy };
            var json = JsonSerializer.Serialize(body, JsonOpts);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await Http.PatchAsync(
                $"packing-lists/scan/{Uri.EscapeDataString(barcode)}/ship", content);
            var status = (int)resp.StatusCode;
            if (!resp.IsSuccessStatusCode)
            {
                Logger.Log($"ApiService.ShipAsync: HTTP {status}");
                return new ShipResult(status, false, false);
            }
            var node = await resp.Content.ReadFromJsonAsync<JsonNode>(JsonOpts);
            return new ShipResult(
                status,
                node?["packedBackfilled"]?.GetValue<bool>() ?? false,
                node?["alreadyShipped"]?.GetValue<bool>() ?? false);
        }
        catch (Exception ex)
        {
            Logger.Log($"ApiService.ShipAsync: {ex.Message}");
            return new ShipResult(0, false, false);
        }
    }

    public static async Task<(int Total, List<string?> Platforms)> GetShippedTodayAsync(int stationId)
    {
        try
        {
            var from = DateTime.Today.ToUniversalTime().ToString("o");
            var resp = await Http.GetFromJsonAsync<PackedTodayResponse>(
                $"packing-lists/list?status=Shipped&shippingStationId={stationId}" +
                $"&from={Uri.EscapeDataString(from)}&limit=1000&offset=0", JsonOpts);

            var platforms = new List<string?>();
            if (resp?.Items is { } items)
                foreach (var i in items) platforms.Add(i.Platform);
            return (resp?.Total ?? platforms.Count, platforms);
        }
        catch (Exception ex)
        {
            Logger.Log($"ApiService.GetShippedTodayAsync: {ex.Message}");
            return (0, []);
        }
    }

    public static async Task<bool> UpdateVideoStatusAsync(
        int videoId, string status, string? remoteFilePath = null,
        string? failureReason = null, int? uploadAttempts = null)
    {
        try
        {
            var body = new UpdateVideoStatusRequest(status, remoteFilePath, failureReason, uploadAttempts);
            var json = JsonSerializer.Serialize(body, JsonOpts);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await Http.PatchAsync($"videos/{videoId}/status", content);
            if (!resp.IsSuccessStatusCode)
                Logger.Log($"ApiService.UpdateVideoStatusAsync: HTTP {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Logger.Log($"ApiService.UpdateVideoStatusAsync: {ex.Message}");
            return false;
        }
    }

    // ── Orphan videos ──────────────────────────────────────────────────────────

    /// <summary>
    /// Registers an orphan video (no packing_lists row) via POST /orphan-videos.
    /// Idempotent on objectKey server-side. Returns the new/existing row id, or
    /// -1 on any failure (caller leaves the file on disk).
    /// </summary>
    public static async Task<int> CreateOrphanVideoAsync(
        string objectKey, string bucket, string? scannedText, int? stationId,
        string? @operator, string filePath, string fileName,
        DateTime recordedAtUtc, long fileSize)
    {
        try
        {
            var body = new CreateOrphanRequest(
                objectKey, bucket, scannedText, stationId,
                @operator?.Replace(' ', '-'),
                recordedAtUtc.ToUniversalTime().ToString("o"),
                fileSize, filePath, fileName);
            var json = JsonSerializer.Serialize(body, JsonOpts);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await Http.PostAsync("orphan-videos", content);
            if (!resp.IsSuccessStatusCode)
            {
                Logger.Log($"ApiService.CreateOrphanVideoAsync: HTTP {(int)resp.StatusCode} for {objectKey}");
                return -1;
            }
            var result = await resp.Content.ReadFromJsonAsync<OrphanVideoRecord>(JsonOpts);
            return result?.Id ?? -1;
        }
        catch (Exception ex)
        {
            Logger.Log($"ApiService.CreateOrphanVideoAsync: {ex.Message}");
            return -1;
        }
    }

    /// <summary>
    /// PATCH /orphan-videos/{id}/status — twin of UpdateVideoStatusAsync, used by
    /// VideoWorkflowRunner when running an orphan capture.
    /// </summary>
    public static async Task<bool> UpdateOrphanVideoStatusAsync(
        int id, string status, string? remoteFilePath = null,
        string? failureReason = null, int? uploadAttempts = null)
    {
        try
        {
            var body = new UpdateVideoStatusRequest(status, remoteFilePath, failureReason, uploadAttempts);
            var json = JsonSerializer.Serialize(body, JsonOpts);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await Http.PatchAsync($"orphan-videos/{id}/status", content);
            if (!resp.IsSuccessStatusCode)
                Logger.Log($"ApiService.UpdateOrphanVideoStatusAsync: HTTP {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Logger.Log($"ApiService.UpdateOrphanVideoStatusAsync: {ex.Message}");
            return false;
        }
    }

    // ── Video queries ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all videos for this station with status Recorded, Uploading, or Uploaded
    /// (i.e. crashed before completing). Used by VideoWorkflowManager.RecoverAsync.
    /// Does NOT return Failed videos — those require a dashboard-triggered retry.
    /// </summary>
    public static async Task<List<PendingVideoRecord>> GetPendingVideosForStationAsync(int stationId)
    {
        try
        {
            var result = await Http.GetFromJsonAsync<List<PendingVideoRecord>>(
                $"videos/station/{stationId}/pending", JsonOpts);
            return result ?? [];
        }
        catch (Exception ex)
        {
            Logger.Log($"ApiService.GetPendingVideosForStationAsync: {ex.Message}");
            return [];
        }
    }

    public static async Task<List<VideoDetail>> ResolveVideosByFileNamesAsync(int stationId, List<string> fileNames)
    {
        if (fileNames.Count == 0) return [];
        try
        {
            var body = new { stationId, fileNames };
            var resp = await Http.PostAsJsonAsync("videos/resolve", body, JsonOpts);
            if (!resp.IsSuccessStatusCode)
            {
                Logger.Log($"ApiService.ResolveVideosByFileNamesAsync: HTTP {(int)resp.StatusCode}");
                return [];
            }
            var result = await resp.Content.ReadFromJsonAsync<List<VideoDetail>>(JsonOpts);
            return result ?? [];
        }
        catch (Exception ex)
        {
            Logger.Log($"ApiService.ResolveVideosByFileNamesAsync: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Fetches a single video record by id. Used by UploadCommandListener to
    /// get the local file path for a dashboard-triggered retry (replaces ReuploadQueue).
    /// </summary>
    public static async Task<VideoDetail?> GetVideoAsync(int videoId)
    {
        try
        {
            return await Http.GetFromJsonAsync<VideoDetail>($"videos/{videoId}", JsonOpts);
        }
        catch (Exception ex)
        {
            Logger.Log($"ApiService.GetVideoAsync: {ex.Message}");
            return null;
        }
    }

    // ── Video count ───────────────────────────────────────────────────────────

    public static async Task<int> GetTodayVideoCountAsync(int stationId)
    {
        try
        {
            // Local midnight → UTC so backend counts from operator's local "today"
            var from = DateTime.Today.ToUniversalTime().ToString("o");
            var node = await Http.GetFromJsonAsync<JsonNode>(
                $"videos/station/{stationId}/count?from={Uri.EscapeDataString(from)}", JsonOpts);
            return node?["count"]?.GetValue<int>() ?? 0;
        }
        catch (Exception ex)
        {
            Logger.Log($"ApiService.GetTodayVideoCountAsync: {ex.Message}");
            return 0;
        }
    }


    // ── Manual upload notifications ──────────────────────────────────────────

    /// <summary>
    /// Tells the backend that this video has permanently failed and needs
    /// manual intervention. The backend fires pg_notify('manual_upload_needed')
    /// so connected frontends receive a WebSocket push.
    /// </summary>
    public static async Task<bool> NotifyManualUploadNeededAsync(int videoId)
    {
        try
        {
            var resp = await Http.PostAsync($"videos/{videoId}/manual-upload-needed", null);
            if (!resp.IsSuccessStatusCode)
                Logger.Log($"ApiService.NotifyManualUploadNeededAsync: HTTP {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Logger.Log($"ApiService.NotifyManualUploadNeededAsync: {ex.Message}");
            return false;
        }
    }

    // ── Upload commands (dashboard-initiated retry) ───────────────────────────

    /// <summary>
    /// Station ACKs / completes / rejects an upload-command row. Used by
    /// <c>UploadCommandListener</c> to report progress back to the dashboard.
    /// </summary>
    public static async Task<bool> PatchUploadCommandAsync(
        long commandId, string status, string? reasonOnRejection = null)
    {
        try
        {
            var body = new UploadCommandPatch(status, reasonOnRejection);
            var json = JsonSerializer.Serialize(body, JsonOpts);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await Http.PatchAsync($"upload-commands/{commandId}", content);
            if (!resp.IsSuccessStatusCode)
                Logger.Log($"ApiService.PatchUploadCommandAsync: HTTP {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Logger.Log($"ApiService.PatchUploadCommandAsync: {ex.Message}");
            return false;
        }
    }

    public static async Task<bool> UpdateVideoRemotePathAsync(int videoId, string remoteFilePath)
    {
        try
        {
            var body = new UpdateVideoRemotePathRequest(remoteFilePath);
            var json = JsonSerializer.Serialize(body, JsonOpts);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await Http.PatchAsync($"videos/{videoId}/remote-path", content);
            if (!resp.IsSuccessStatusCode)
                Logger.Log($"ApiService.UpdateVideoRemotePathAsync: HTTP {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Logger.Log($"ApiService.UpdateVideoRemotePathAsync: {ex.Message}");
            return false;
        }
    }

    // ── Products ──────────────────────────────────────────────────────────────

    public static async Task<ProductInfo?> GetProductBySkuAsync(string sku)
    {
        try
        {
            return await Http.GetFromJsonAsync<ProductInfo>(
                $"products/by-sku/{Uri.EscapeDataString(sku)}", JsonOpts);
        }
        catch (Exception ex)
        {
            Logger.Log($"ApiService.GetProductBySkuAsync: {ex.Message}");
            return null;
        }
    }

    public static async Task<byte[]?> GetProductImageAsync(int productId)
    {
        try
        {
            var resp = await Http.GetAsync($"products/{productId}/image");
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsByteArrayAsync();
        }
        catch (Exception ex)
        {
            Logger.Log($"ApiService.GetProductImageAsync: {ex.Message}");
            return null;
        }
    }

    public static async Task<List<BundleComponentInfo>> GetBundleComponentsAsync(int productId)
    {
        try
        {
            var list = await Http.GetFromJsonAsync<List<BundleComponentInfo>>(
                $"products/{productId}/components", JsonOpts);
            return list ?? [];
        }
        catch (Exception ex)
        {
            Logger.Log($"ApiService.GetBundleComponentsAsync: {ex.Message}");
            return [];
        }
    }

    public static async Task<Dictionary<string, ProductEnrichment>> EnrichProductsAsync(List<string> skus)
    {
        if (skus.Count == 0) return [];
        try
        {
            var body = new { skus };
            var resp = await Http.PostAsJsonAsync("products/enrich", body, JsonOpts);
            if (!resp.IsSuccessStatusCode)
            {
                Logger.Log($"ApiService.EnrichProductsAsync: HTTP {(int)resp.StatusCode}");
                return [];
            }
            var list = await resp.Content.ReadFromJsonAsync<List<ProductEnrichment>>(JsonOpts);
            return list?.ToDictionary(e => e.SellerSku, StringComparer.OrdinalIgnoreCase) ?? [];
        }
        catch (Exception ex)
        {
            Logger.Log($"ApiService.EnrichProductsAsync: {ex.Message}");
            return [];
        }
    }

    // ── Returns ──────────────────────────────────────────────────────────────

    public static async Task<bool> CreateReturnRecordAsync(
        string trackingNumber, string recordType, string? reason,
        string? notes, string? shippingOptions, string? platform,
        int? operatorId, int? stationId)
    {
        try
        {
            var body = new { trackingNumber, recordType, reason, notes, shippingOptions, platform, operatorId, stationId };
            var resp = await Http.PostAsJsonAsync("returns", body, JsonOpts);
            if (!resp.IsSuccessStatusCode)
                Logger.Log($"ApiService.CreateReturnRecordAsync: HTTP {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Logger.Log($"ApiService.CreateReturnRecordAsync: {ex.Message}");
            return false;
        }
    }

    public static async Task<bool> UpsertCarrierParcelCountAsync(string shippingOptions, int actualCount, int? operatorId)
    {
        try
        {
            var body = new { shippingOptions, actualCount, operatorId };
            var json = JsonSerializer.Serialize(body, JsonOpts);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await Http.PutAsync("returns/carrier-counts", content);
            if (!resp.IsSuccessStatusCode)
                Logger.Log($"ApiService.UpsertCarrierParcelCountAsync: HTTP {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Logger.Log($"ApiService.UpsertCarrierParcelCountAsync: {ex.Message}");
            return false;
        }
    }

    public static async Task<bool> BackfillExpectedCarrierCountsAsync()
    {
        try
        {
            var resp = await Http.PostAsync("returns/carrier-counts/backfill", null);
            if (!resp.IsSuccessStatusCode)
                Logger.Log($"ApiService.BackfillExpectedCarrierCountsAsync: HTTP {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Logger.Log($"ApiService.BackfillExpectedCarrierCountsAsync: {ex.Message}");
            return false;
        }
    }

    // ── Private DTOs ──────────────────────────────────────────────────────────

    private record StatusRequest(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("updatedProductLists")] ProductListPayload UpdatedProductLists,
        [property: JsonPropertyName("checkedBy")] string? CheckedBy,
        [property: JsonPropertyName("checkingStationId")] int? CheckingStationId);

    private record CreateVideoRequest(
        [property: JsonPropertyName("trackingNumber")] string TrackingNumber,
        [property: JsonPropertyName("filePath")] string FilePath,
        [property: JsonPropertyName("fileName")] string FileName,
        [property: JsonPropertyName("stationId")] int? StationId,
        [property: JsonPropertyName("operator")] string Operator);

    private record UpdateVideoStatusRequest(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("remoteFilePath")] string? RemoteFilePath,
        [property: JsonPropertyName("failureReason")] string? FailureReason,
        [property: JsonPropertyName("uploadAttempts")] int? UploadAttempts);

    private record UploadCommandPatch(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("reasonOnRejection")] string? ReasonOnRejection);

    private record UpdateVideoRemotePathRequest(
        [property: JsonPropertyName("remoteFilePath")] string RemoteFilePath);

    private record ScanStatusRequest(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("packedBy")] string? PackedBy,
        [property: JsonPropertyName("packingStationId")] int? PackingStationId);

    private record PackedTodayResponse(
        [property: JsonPropertyName("total")] int Total,
        [property: JsonPropertyName("items")] List<PackedTodayItem>? Items);

    private record PackedTodayItem(
        [property: JsonPropertyName("platform")] string? Platform);

    private record VideoRecord(
        [property: JsonPropertyName("id")] int Id);

    public record PendingVideoRecord(
        [property: JsonPropertyName("id")]             int     Id,
        [property: JsonPropertyName("trackingNumber")] string? TrackingNumber,
        [property: JsonPropertyName("filePath")]       string  FilePath,
        [property: JsonPropertyName("operator")]       string? Operator);

    public record VideoDetail(
        [property: JsonPropertyName("id")]              int     Id,
        [property: JsonPropertyName("trackingNumber")]  string? TrackingNumber,
        [property: JsonPropertyName("filePath")]        string  FilePath,
        [property: JsonPropertyName("operator")]        string? Operator,
        [property: JsonPropertyName("status")]          string? Status,
        [property: JsonPropertyName("remoteFilePath")]  string? RemoteFilePath = null);

    public record ProductInfo(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("productName")] string ProductName,
        [property: JsonPropertyName("productType")] string ProductType,
        [property: JsonPropertyName("imagePath")] string? ImagePath);

    public record BundleComponentInfo(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("componentProductId")] int ComponentProductId,
        [property: JsonPropertyName("quantity")] int Quantity,
        [property: JsonPropertyName("productName")] string ProductName,
        [property: JsonPropertyName("productVariation")] string? ProductVariation,
        [property: JsonPropertyName("sellerSku")] string SellerSku,
        [property: JsonPropertyName("imagePath")] string? ImagePath,
        [property: JsonPropertyName("qcNotes")] string? QcNotes = null);

    // DTOs for the orphan create round-trip.
    private record CreateOrphanRequest(
        [property: JsonPropertyName("objectKey")]   string  ObjectKey,
        [property: JsonPropertyName("bucket")]      string  Bucket,
        [property: JsonPropertyName("scannedText")] string? ScannedText,
        [property: JsonPropertyName("stationId")]   int?    StationId,
        [property: JsonPropertyName("operator")]    string? Operator,
        [property: JsonPropertyName("recordedAt")]  string  RecordedAt,
        [property: JsonPropertyName("fileSize")]    long    FileSize,
        [property: JsonPropertyName("filePath")]    string  FilePath,
        [property: JsonPropertyName("fileName")]    string  FileName);

    public sealed class OrphanVideoRecord
    {
        [JsonPropertyName("id")]          public int    Id          { get; set; }
        [JsonPropertyName("status")]      public string? Status     { get; set; }
        [JsonPropertyName("matchStatus")] public string? MatchStatus { get; set; }
    }
}
