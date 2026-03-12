using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;

namespace app.Services;

[SupportedOSPlatform("windows")]
public class WebhookService
{
    private static readonly HttpClient _httpClient = new();
    private const int MaxRetries = 3;

    /// <summary>
    /// Fires the webhook in a background thread and retries up to 3 times.
    /// Returns immediately so the caller is never blocked.
    /// <paramref name="onComplete"/> is invoked on a thread-pool thread when finished.
    /// </summary>
    public static void FireAndRetry(string barcode, string filePath, string stationName,
        Action<bool>? onComplete = null)
    {
        _ = Task.Run(async () =>
        {
            var result = await SendAsync(barcode, filePath, stationName);
            onComplete?.Invoke(result);
        });
    }

    /// <summary>
    /// Posts webhook to n8n. Retries up to 3 times on transient failures.
    /// Returns true if the webhook was delivered successfully.
    /// </summary>
    public static async Task<bool> SendAsync(string barcode, string filePath, string stationName)
    {
        Logger.Log($"Sending webhook for barcode: {barcode}, filePath: {filePath}");

        var payload = new
        {
            barcode,
            filePath,
            fileName = Path.GetFileName(filePath),
            finishedAt = DateTime.UtcNow,
            stationName = $"{Environment.MachineName}-{stationName.Replace(' ', '-')}"
        };

        var json = JsonSerializer.Serialize(payload);

        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync(AppSettings.WebhookUrl, content);

                if (response.IsSuccessStatusCode)
                {
                    Logger.Log($"Webhook sent (attempt {attempt}): {(int)response.StatusCode}");
                    return true;
                }

                Logger.Log($"Webhook attempt {attempt} failed: HTTP {(int)response.StatusCode}");
            }
            catch (Exception ex)
            {
                Logger.Log($"Webhook attempt {attempt} error: {ex.Message}");
            }

            if (attempt < MaxRetries)
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2));
        }

        Logger.Log($"Webhook failed after {MaxRetries} attempts for barcode: {barcode}");
        return false;
    }
}
