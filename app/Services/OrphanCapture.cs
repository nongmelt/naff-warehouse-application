namespace app.Services;

/// <summary>
/// Tri-state outcome of POST /videos. NoPackingList is the STRICT/definitive
/// orphan signal (backend returned HTTP 404 = AppError::NotFound). Failed is any
/// other non-success (5xx, timeout, connection refused) and must NEVER orphan.
/// </summary>
public enum CreateVideoResultKind
{
    Created,        // 2xx, record id available
    NoPackingList,  // HTTP 404 — definitive: no packing_lists row → orphan path
    Failed,         // anything else (5xx / 4xx-other / network) → leave on disk, retry later
}

/// <summary>
/// Result of <c>ApiService.CreateVideoResultAsync</c>. <see cref="VideoId"/> is set
/// only when <see cref="Kind"/> is <see cref="CreateVideoResultKind.Created"/>.
/// </summary>
public readonly record struct CreateVideoResult(CreateVideoResultKind Kind, int VideoId)
{
    public static CreateVideoResult Created(int id) => new(CreateVideoResultKind.Created, id);
    public static readonly CreateVideoResult NoPackingList = new(CreateVideoResultKind.NoPackingList, -1);
    public static readonly CreateVideoResult Failed = new(CreateVideoResultKind.Failed, -1);
}

/// <summary>
/// Pure (MAUI-free, side-effect-free) helpers for orphan video capture.
/// Kept here so they can be unit-tested without the Windows-only MAUI host.
/// </summary>
public static class OrphanCapture
{
    /// <summary>
    /// Builds the MinIO object key for a recorded video from its local path,
    /// matching the live convention: "{yyyy-MM-dd}/{fileName}". The date segment
    /// is the file's grandparent directory name (the per-day folder created in
    /// StationView.StartRecording). Falls back to today's date when the path has
    /// no grandparent directory.
    /// </summary>
    public static string BuildRawObjectKey(string localFilePath, DateTime nowLocal)
    {
        var fileName = Path.GetFileName(localFilePath);
        var grandparent = Path.GetDirectoryName(Path.GetDirectoryName(localFilePath));
        var dateDir = string.IsNullOrEmpty(grandparent)
            ? nowLocal.ToString("yyyy-MM-dd")
            : Path.GetFileName(grandparent);
        if (string.IsNullOrEmpty(dateDir))
            dateDir = nowLocal.ToString("yyyy-MM-dd");
        return $"{dateDir}/{fileName}";
    }

    /// <summary>
    /// Classifies a POST /videos HTTP status into the tri-state orphan decision.
    /// 404 (AppError::NotFound) is the ONLY definitive "no packing list" signal —
    /// 422 (sqlx 23xxx FK/constraint) and every other code map to Failed so we
    /// never orphan on an ambiguous failure (decision #3).
    /// </summary>
    public static CreateVideoResultKind ClassifyCreateVideoStatus(int httpStatusCode)
    {
        if (httpStatusCode >= 200 && httpStatusCode < 300) return CreateVideoResultKind.Created;
        if (httpStatusCode == 404) return CreateVideoResultKind.NoPackingList;
        return CreateVideoResultKind.Failed;
    }
}
