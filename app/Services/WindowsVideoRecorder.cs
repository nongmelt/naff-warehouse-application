using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using CommunityToolkit.Maui.Views;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace app.Services;

/// <summary>
/// Windows video recorder that drives the toolkit's underlying MediaCapture directly,
/// following the LowLagMediaRecording contract the way the built-in Camera app does:
/// fixed encoding profile chosen before Prepare, native StorageFile sink, no media-type
/// change after Prepare, and StopAsync + FinishAsync per clip so the native record sink
/// is fully released between recordings.
///
/// Why: CommunityToolkit.Maui.Camera 5.0.0's Windows record path breaks that contract
/// (SetFormatAsync after Prepare, managed-stream sink, never calls FinishAsync), which
/// wedges Media Foundation on Windows 10 1909 (PACKING-RAM07). Verified unchanged in
/// toolkit 6.1.0 — see docs/plans/2026-06-10-camera-record-path.md.
///
/// MediaCapture is reached via reflection (handler → internal CameraManager → private
/// mediaCapture); members verified against the 5.0.0 DLL. If the package is ever bumped
/// and internals change, IsAvailable turns false and StationView falls back to the
/// toolkit record path (logged loudly).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsVideoRecorder(CameraView cameraView, int stationId)
{
    private LowLagMediaRecording? _recording;

    public bool IsAvailable => TryGetMediaCapture(out _);

    /// <summary>True while a started recording has not yet been claimed by StopAsync.</summary>
    public bool HasActiveRecording => _recording is not null;

    /// <summary>Prepares and starts a LowLag recording into <paramref name="outputFilePath"/>.</summary>
    /// <remarks>
    /// <paramref name="outputFilePath"/> must be an absolute path and its directory must
    /// already exist (StationView creates it before calling).
    /// </remarks>
    public async Task StartAsync(string outputFilePath, CancellationToken token)
    {
        if (!TryGetMediaCapture(out var mediaCapture) || mediaCapture is null)
            throw new InvalidOperationException("Toolkit MediaCapture not reachable via reflection");

        // A previous clip that was never stopped (a stop that raced ahead of a slow
        // start, or a wedged stop that was abandoned) leaves a live sink that would
        // wedge the next Prepare — finish it first so every clip starts clean.
        var stale = Interlocked.Exchange(ref _recording, null);
        if (stale is not null)
        {
            Logger.Log($"Station {stationId}: [REC] [WARN] Stale recording found at start — finishing it");
            try { await stale.FinishAsync(); }
            catch (Exception ex) { Logger.Log($"Station {stationId}: [REC] Stale FinishAsync failed: {ex.Message}"); }
        }

        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(outputFilePath)!);
        var file = await folder.CreateFileAsync(Path.GetFileName(outputFilePath),
            CreationCollisionOption.ReplaceExisting);

        // Fixed profile chosen BEFORE Prepare. The source keeps whatever format the
        // preview runs at; never change it between Prepare and Start. If 1080p still
        // wedges on PACKING-RAM07, HD720p here is the next diagnostic knob.
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);

        var sw = Stopwatch.StartNew();
        var recording = await mediaCapture.PrepareLowLagRecordToStorageFileAsync(profile, file).AsTask(token);
        Logger.Log($"Station {stationId}: [REC] LowLag prepared (1080p → file sink) in {sw.ElapsedMilliseconds} ms");

        try
        {
            await recording.StartAsync().AsTask(token);
        }
        catch (Exception startEx)
        {
            Logger.Log($"Station {stationId}: [REC] StartAsync threw: {startEx.Message}");
            // Never orphan a prepared sink — release it before surfacing the failure.
            try { await recording.FinishAsync(); }
            catch (Exception ex) { Logger.Log($"Station {stationId}: [REC] FinishAsync after failed start: {ex.Message}"); }
            throw;
        }
        _recording = recording;
        Logger.Log($"Station {stationId}: [REC] LowLag record started ({sw.ElapsedMilliseconds} ms total)");
    }

    /// <summary>
    /// Stops and FINISHES the recording — FinishAsync finalizes the MP4 and releases the
    /// native record sink so the next clip can Prepare cleanly. Deliberately unbounded:
    /// the caller wall-clock-bounds it, and an abandoned call drains naturally if the
    /// pipeline recovers late.
    /// </summary>
    public async Task StopAsync()
    {
        var recording = Interlocked.Exchange(ref _recording, null);
        if (recording is null)
        {
            Logger.Log($"Station {stationId}: [REC] StopAsync called with no active recording");
            return;
        }

        var sw = Stopwatch.StartNew();
        Exception? stopError = null;
        try
        {
            await recording.StopAsync();
            Logger.Log($"Station {stationId}: [REC] StopAsync done in {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            stopError = ex;
            Logger.Log($"Station {stationId}: [REC] StopAsync threw: {ex.Message}");
        }

        sw.Restart();
        try
        {
            // The one call that releases the native record sink. FinishAsync stops and
            // finalizes on its own, so it is valid even when StopAsync failed.
            await recording.FinishAsync();
            Logger.Log($"Station {stationId}: [REC] FinishAsync done in {sw.ElapsedMilliseconds} ms — sink released");
        }
        catch (Exception ex)
        {
            Logger.Log($"Station {stationId}: [REC] FinishAsync failed: {ex.Message}");
            if (stopError is null) throw;
        }

        if (stopError is not null)
            ExceptionDispatchInfo.Capture(stopError).Throw();
    }

    private bool TryGetMediaCapture(out MediaCapture? mediaCapture)
    {
        mediaCapture = null;
        try
        {
            var handler = cameraView.Handler;
            if (handler is null) return false;

            var manager = handler.GetType()
                    .GetProperty("CameraManager", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.GetValue(handler)
                ?? handler.GetType()
                    .GetField("cameraManager", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.GetValue(handler);
            if (manager is null) return false;

            mediaCapture = manager.GetType()
                .GetField("mediaCapture", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(manager) as MediaCapture;
            return mediaCapture is not null;
        }
        catch (Exception ex)
        {
            Logger.Log($"Station {stationId}: [REC] Reflection failed: {ex.Message}");
            return false;
        }
    }
}
