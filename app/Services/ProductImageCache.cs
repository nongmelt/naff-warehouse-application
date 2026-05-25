using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;

namespace app.Services;

[SupportedOSPlatform("windows")]
public static class ProductImageCache
{
    private static readonly string CacheDir = Path.Combine(FileSystem.CacheDirectory, "product-images");
    private static readonly HashSet<string> InFlight = [];
    private const long MaxCacheBytes = 500 * 1024 * 1024; // 500 MB

    static ProductImageCache()
    {
        Directory.CreateDirectory(CacheDir);
        EvictIfOverLimit();
    }

    private static void EvictIfOverLimit()
    {
        try
        {
            var dir = new DirectoryInfo(CacheDir);
            var files = dir.GetFiles().OrderBy(f => f.LastAccessTime).ToArray();
            var totalSize = files.Sum(f => f.Length);
            foreach (var file in files)
            {
                if (totalSize <= MaxCacheBytes) break;
                totalSize -= file.Length;
                file.Delete();
            }
        }
        catch { }
    }

    public static string? GetCachedPath(string sku, string? version)
    {
        if (version != null)
        {
            var versionedPattern = $"{sku}_v{version}.*";
            var versionedFiles = Directory.GetFiles(CacheDir, versionedPattern);
            if (versionedFiles.Length > 0) return versionedFiles[0];

            PurgeSkuFiles(sku);
            return null;
        }

        var pattern = $"{sku}.*";
        var files = Directory.GetFiles(CacheDir, pattern);
        return files.Length > 0 ? files[0] : null;
    }

    private static void PurgeSkuFiles(string sku)
    {
        try
        {
            foreach (var f in Directory.GetFiles(CacheDir, $"{sku}.*"))
                File.Delete(f);
            foreach (var f in Directory.GetFiles(CacheDir, $"{sku}_v*.*"))
                File.Delete(f);
        }
        catch { }
    }

    public static async Task<string?> EnsureAsync(string sku, string apiBaseUrl, int? productId = null, string? version = null)
    {
        var existing = GetCachedPath(sku, version);
        if (existing != null) return existing;

        lock (InFlight)
        {
            if (InFlight.Contains(sku)) return null;
            InFlight.Add(sku);
        }

        try
        {
            var http = ApiService.GetHttpClient();
            int resolvedId;
            if (productId.HasValue)
            {
                resolvedId = productId.Value;
            }
            else
            {
                var productResp = await http.GetAsync($"products/by-sku/{Uri.EscapeDataString(sku)}");
                if (!productResp.IsSuccessStatusCode) return null;

                var json = await productResp.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonNode>();
                var id = json?["id"]?.GetValue<int>();
                if (id is null) return null;
                resolvedId = id.Value;
            }

            var imgResp = await http.GetAsync($"products/{resolvedId}/image");
            if (!imgResp.IsSuccessStatusCode) return null;

            var contentType = imgResp.Content.Headers.ContentType?.MediaType ?? "";
            var ext = contentType switch
            {
                "image/png"  => "png",
                "image/webp" => "webp",
                _            => "jpg",
            };

            var filename = version != null ? $"{sku}_v{version}.{ext}" : $"{sku}.{ext}";
            var localPath = Path.Combine(CacheDir, filename);
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
