using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;

namespace app.Services;

[SupportedOSPlatform("windows")]
public static class ProductImageCache
{
    private static readonly string CacheDir = Path.Combine(FileSystem.CacheDirectory, "product-images");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly HashSet<string> InFlight = [];

    static ProductImageCache()
    {
        Directory.CreateDirectory(CacheDir);
    }

    public static string? GetCachedPath(string sku)
    {
        var pattern = $"{sku}.*";
        var files = Directory.GetFiles(CacheDir, pattern);
        return files.Length > 0 ? files[0] : null;
    }

    public static async Task<string?> EnsureAsync(string sku, string apiBaseUrl)
        => await EnsureAsync(sku, apiBaseUrl, productId: null);

    public static async Task<string?> EnsureAsync(string sku, string apiBaseUrl, int? productId)
    {
        var existing = GetCachedPath(sku);
        if (existing != null) return existing;

        lock (InFlight)
        {
            if (InFlight.Contains(sku)) return null;
            InFlight.Add(sku);
        }

        try
        {
            int resolvedId;
            if (productId.HasValue)
            {
                resolvedId = productId.Value;
            }
            else
            {
                var url = $"{apiBaseUrl.TrimEnd('/')}/products/by-sku/{Uri.EscapeDataString(sku)}";
                var productResp = await Http.GetAsync(url);
                if (!productResp.IsSuccessStatusCode) return null;

                var json = await productResp.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonNode>();
                var id = json?["id"]?.GetValue<int>();
                if (id is null) return null;
                resolvedId = id.Value;
            }

            var imgUrl = $"{apiBaseUrl.TrimEnd('/')}/products/{resolvedId}/image";
            var imgResp = await Http.GetAsync(imgUrl);
            if (!imgResp.IsSuccessStatusCode) return null;

            var contentType = imgResp.Content.Headers.ContentType?.MediaType ?? "";
            var ext = contentType switch
            {
                "image/png"  => "png",
                "image/webp" => "webp",
                _            => "jpg",
            };

            var localPath = Path.Combine(CacheDir, $"{sku}.{ext}");
            var bytes = await imgResp.Content.ReadAsByteArrayAsync();
            await File.WriteAllBytesAsync(localPath, bytes);
            return localPath;
        }
        catch (Exception ex)
        {
            Logger.Log($"ProductImageCache.EnsureAsync({sku}): {ex.Message}");
            return null;
        }
        finally
        {
            lock (InFlight) { InFlight.Remove(sku); }
        }
    }
}
