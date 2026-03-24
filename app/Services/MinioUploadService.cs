using Minio;
using Minio.DataModel.Args;
using System.Runtime.Versioning;

namespace app.Services;

[SupportedOSPlatform("windows")]
public static class MinioUploadService
{
    private const int MaxRetries = 3;

    /// <summary>
    /// Fire-and-forget: uploads the recorded video to MinIO, updating the
    /// video record status at each stage via ApiService.
    /// </summary>
    public static void UploadAsync(int videoId, string filePath, string trackingNumber)
    {
        Task.Run(() => RunAsync(videoId, filePath, trackingNumber));
    }

    private static async Task RunAsync(int videoId, string filePath, string trackingNumber)
    {
        var endpoint  = AppSettings.MinioEndpoint;
        var accessKey = AppSettings.MinioAccessKey;
        var secretKey = AppSettings.MinioSecretKey;
        var bucket    = AppSettings.MinioBucket;

        if (string.IsNullOrWhiteSpace(endpoint) ||
            string.IsNullOrWhiteSpace(accessKey) ||
            string.IsNullOrWhiteSpace(secretKey) ||
            string.IsNullOrWhiteSpace(bucket))
        {
            Logger.Log($"MinioUploadService: MinIO not configured, skipping upload for video {videoId}");
            return;
        }

        if (!File.Exists(filePath))
        {
            Logger.Log($"MinioUploadService: file not found at {filePath}");
            await ApiService.UpdateVideoStatusAsync(videoId, "failed");
            return;
        }

        var objectName = $"{trackingNumber}/{Path.GetFileName(filePath)}";

        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                await ApiService.UpdateVideoStatusAsync(videoId, "uploading");

                var minio = new MinioClient()
                    .WithEndpoint(endpoint)
                    .WithCredentials(accessKey, secretKey)
                    .Build();

                using var stream = File.OpenRead(filePath);
                var size = stream.Length;

                await minio.PutObjectAsync(new PutObjectArgs()
                    .WithBucket(bucket)
                    .WithObject(objectName)
                    .WithStreamData(stream)
                    .WithObjectSize(size)
                    .WithContentType("video/mp4"));

                await ApiService.UpdateVideoStatusAsync(videoId, "uploaded");
                Logger.Log($"MinioUploadService: uploaded {objectName} ({size / 1_048_576.0:F1} MB)");

                // Validate the file exists on MinIO
                bool exists = await ObjectExistsAsync(minio, bucket, objectName);
                await ApiService.UpdateVideoStatusAsync(videoId, exists ? "completed" : "failed");

                if (!exists)
                    Logger.Log($"MinioUploadService: post-upload validation failed for {objectName}");

                return; // success
            }
            catch (Exception ex)
            {
                Logger.Log($"MinioUploadService: attempt {attempt}/{MaxRetries} failed — {ex.Message}");
                if (attempt < MaxRetries)
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 2));
            }
        }

        // All retries exhausted
        Logger.Log($"MinioUploadService: giving up after {MaxRetries} attempts for video {videoId}");
        await ApiService.UpdateVideoStatusAsync(videoId, "failed");
    }

    private static async Task<bool> ObjectExistsAsync(IMinioClient minio, string bucket, string objectName)
    {
        try
        {
            await minio.StatObjectAsync(new StatObjectArgs()
                .WithBucket(bucket)
                .WithObject(objectName));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
