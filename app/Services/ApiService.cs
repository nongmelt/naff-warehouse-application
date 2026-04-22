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

    private static HttpClient Http
    {
        get
        {
            var url = (AppSettings.ApiUrl?.TrimEnd('/') ?? "http://localhost:8080") + "/";
            if (_http is null || _httpBase != url)
            {
                _http?.Dispose();
                _http = new HttpClient { BaseAddress = new Uri(url) };
                _httpBase = url;
            }
            return _http;
        }
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

    public static async Task<List<PackingList>> SearchAsync(string input)
    {
        try
        {
            var url = $"packing-lists?q={Uri.EscapeDataString(input)}";
            var list = await Http.GetFromJsonAsync<List<PackingList>>(url, JsonOpts);
            return list ?? [];
        }
        catch (Exception ex)
        {
            Logger.Log($"ApiService.SearchAsync: {ex.Message}");
            return [];
        }
    }

    // ── Update status ─────────────────────────────────────────────────────────

    public static async Task<bool> UpdatePackingStatusAsync(
        int packingId, string status, ProductListPayload updatedProductLists,
        string? checkedBy = null)
    {
        try
        {
            var body = new StatusRequest(status, updatedProductLists, checkedBy?.Replace(' ', '-'));
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
    /// Creates a video record in the backend (status = "recorded") and returns
    /// the new record id, or -1 on failure.
    /// </summary>
    public static async Task<int> CreateVideoRecordAsync(
        string trackingNumber, string filePath, string stationName, string @operator)
    {
        try
        {
            var body = new CreateVideoRequest(trackingNumber, filePath, Path.GetFileName(filePath), stationName, @operator);
            var json = JsonSerializer.Serialize(body, JsonOpts);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await Http.PostAsync("videos", content);
            if (!resp.IsSuccessStatusCode)
            {
                Logger.Log($"ApiService.CreateVideoRecordAsync: HTTP {(int)resp.StatusCode}");
                return -1;
            }
            var result = await resp.Content.ReadFromJsonAsync<VideoRecord>(JsonOpts);
            return result?.Id ?? -1;
        }
        catch (Exception ex)
        {
            Logger.Log($"ApiService.CreateVideoRecordAsync: {ex.Message}");
            return -1;
        }
    }

    /// <summary>
    /// Updates packing status by scan input (tracking number or order number).
    /// Pass packedBy only when status is "Packed".
    /// </summary>
    public static async Task<bool> UpdatePackingStatusByScanAsync(
        string barcode, string status, string? packedBy = null)
    {
        try
        {
            var body = new ScanStatusRequest(status, packedBy?.Replace(' ', '-'));
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

    // ── Private DTOs ──────────────────────────────────────────────────────────

    private record StatusRequest(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("updatedProductLists")] ProductListPayload UpdatedProductLists,
        [property: JsonPropertyName("checkedBy")] string? CheckedBy);

    private record CreateVideoRequest(
        [property: JsonPropertyName("trackingNumber")] string TrackingNumber,
        [property: JsonPropertyName("filePath")] string FilePath,
        [property: JsonPropertyName("fileName")] string FileName,
        [property: JsonPropertyName("stationName")] string StationName,
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
        [property: JsonPropertyName("packedBy")] string? PackedBy);

    private record VideoRecord(
        [property: JsonPropertyName("id")] int Id);
}
