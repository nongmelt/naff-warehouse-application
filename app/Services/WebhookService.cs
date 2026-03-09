using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;

namespace app.Services;

[SupportedOSPlatform("windows")]

public class WebhookService
{
    private readonly HttpClient _httpClient = new();

    public async Task SendAsync(string barcode, string filePath, string stationName)
    {
        Logger.Log($"Sending webhook for barcode: {barcode}, filePath: {filePath}");
        try
        {
            var payload = new
            {
                barcode = barcode,
                filePath = filePath,
                fileName = Path.GetFileName(filePath),
                finishedAt = DateTime.UtcNow,
                stationName = $"{Environment.MachineName}-{stationName.Replace(' ', '-')}"
            };

            var json = JsonSerializer.Serialize(payload);

            var content = new StringContent(
                json,
                Encoding.UTF8,
                "application/json"
            );

            await _httpClient.PostAsync(
                AppSettings.WebhookUrl,
                content
            );
        }
        catch (Exception ex)
        {
            Logger.Log(ex);
        }
    }
}
