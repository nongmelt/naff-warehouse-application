using System.Runtime.Versioning;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace app.Services;

/// <summary>
/// Tracks failed uploads so that a later dashboard-initiated retry
/// (via <see cref="UploadCommandListener"/>) can locate the original
/// local file path. Lives as append-only JSONL in the app's data
/// directory; compaction runs on every write and drops entries whose
/// source file no longer exists.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ReuploadQueue
{
    public record Entry(
        [property: JsonPropertyName("videoId")]        int    VideoId,
        [property: JsonPropertyName("localPath")]      string LocalPath,
        [property: JsonPropertyName("trackingNumber")] string TrackingNumber,
        [property: JsonPropertyName("failureReason")]  string? FailureReason,
        [property: JsonPropertyName("failedAt")]       DateTime FailedAt);

    private static readonly object Lock = new();
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string StorePath
    {
        get
        {
            var dir = Path.Combine(FileSystem.AppDataDirectory, "reuploads");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "pending.jsonl");
        }
    }

    public static void Enqueue(int videoId, string localPath, string trackingNumber, string? failureReason)
    {
        try
        {
            lock (Lock)
            {
                var entries = LoadUnsafe().Where(e => e.VideoId != videoId).ToList();
                entries.Add(new Entry(videoId, localPath, trackingNumber, failureReason, DateTime.UtcNow));
                SaveUnsafe(entries);
            }
            Logger.Log($"ReuploadQueue: enqueued video {videoId} ({Path.GetFileName(localPath)}) reason={failureReason}");
        }
        catch (Exception ex)
        {
            Logger.Log($"ReuploadQueue.Enqueue: {ex.Message}");
        }
    }

    public static void Complete(int videoId)
    {
        try
        {
            lock (Lock)
            {
                var entries = LoadUnsafe().Where(e => e.VideoId != videoId).ToList();
                SaveUnsafe(entries);
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"ReuploadQueue.Complete: {ex.Message}");
        }
    }

    public static Entry? Find(int videoId)
    {
        try
        {
            lock (Lock)
            {
                return LoadUnsafe().FirstOrDefault(e => e.VideoId == videoId);
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"ReuploadQueue.Find: {ex.Message}");
            return null;
        }
    }

    public static IReadOnlyList<Entry> List()
    {
        try
        {
            lock (Lock) return LoadUnsafe();
        }
        catch { return []; }
    }

    private static List<Entry> LoadUnsafe()
    {
        var path = StorePath;
        if (!File.Exists(path)) return [];
        var lines = File.ReadAllLines(path);
        var result = new List<Entry>(lines.Length);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var e = JsonSerializer.Deserialize<Entry>(line, JsonOpts);
                if (e != null) result.Add(e);
            }
            catch { /* skip malformed line */ }
        }
        return result;
    }

    private static void SaveUnsafe(List<Entry> entries)
    {
        var path = StorePath;
        var tmp = path + ".tmp";
        using (var w = new StreamWriter(tmp, append: false))
            foreach (var e in entries)
                w.WriteLine(JsonSerializer.Serialize(e, JsonOpts));
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
    }
}
