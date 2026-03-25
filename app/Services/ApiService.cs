using app.Models;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

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
    private static string      _httpBase = "";

    private static HttpClient Http
    {
        get
        {
            var url = (AppSettings.ApiUrl?.TrimEnd('/') ?? "http://localhost:8080") + "/";
            if (_http is null || _httpBase != url)
            {
                _http?.Dispose();
                _http     = new HttpClient { BaseAddress = new Uri(url) };
                _httpBase = url;
            }
            return _http;
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
            var url  = $"packing-lists?q={Uri.EscapeDataString(input)}";
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
        int packingId, string status, string updatedProductLists,
        string? checkedBy = null)
    {
        try
        {
            var body    = new StatusRequest(status, updatedProductLists, checkedBy);
            var json    = JsonSerializer.Serialize(body, JsonOpts);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp    = await Http.PatchAsync($"packing-lists/{packingId}/status", content);
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
        string trackingNumber, string filePath, string stationName)
    {
        try
        {
            var body    = new CreateVideoRequest(trackingNumber, filePath, Path.GetFileName(filePath), stationName);
            var json    = JsonSerializer.Serialize(body, JsonOpts);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp    = await Http.PostAsync("videos", content);
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
            var body    = new ScanStatusRequest(status, packedBy);
            var json    = JsonSerializer.Serialize(body, JsonOpts);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp    = await Http.PatchAsync(
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

    public static async Task<bool> UpdateVideoStatusAsync(int videoId, string status)
    {
        try
        {
            var body    = new UpdateVideoStatusRequest(status);
            var json    = JsonSerializer.Serialize(body, JsonOpts);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp    = await Http.PatchAsync($"videos/{videoId}/status", content);
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

    public static async Task<bool> UpdateVideoRemotePathAsync(int videoId, string remoteFilePath)
    {
        try
        {
            var body    = new UpdateVideoRemotePathRequest(remoteFilePath);
            var json    = JsonSerializer.Serialize(body, JsonOpts);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp    = await Http.PatchAsync($"videos/{videoId}/remote-path", content);
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
        [property: JsonPropertyName("status")]              string  Status,
        [property: JsonPropertyName("updatedProductLists")] string  UpdatedProductLists,
        [property: JsonPropertyName("checkedBy")]           string? CheckedBy);

    private record CreateVideoRequest(
        [property: JsonPropertyName("trackingNumber")] string TrackingNumber,
        [property: JsonPropertyName("filePath")]       string FilePath,
        [property: JsonPropertyName("fileName")]       string FileName,
        [property: JsonPropertyName("stationName")]    string StationName);

    private record UpdateVideoStatusRequest(
        [property: JsonPropertyName("status")] string Status);

    private record UpdateVideoRemotePathRequest(
        [property: JsonPropertyName("remoteFilePath")] string RemoteFilePath);

    private record ScanStatusRequest(
        [property: JsonPropertyName("status")]   string  Status,
        [property: JsonPropertyName("packedBy")] string? PackedBy);

    private record VideoRecord(
        [property: JsonPropertyName("id")] int Id);
}
