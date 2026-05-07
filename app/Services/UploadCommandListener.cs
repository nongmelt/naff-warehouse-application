using System.Net.WebSockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace app.Services;

/// <summary>
/// Subscribes to backend WS broadcasts and reacts to
/// <c>upload_command_issued</c> notifications by re-uploading the
/// previously-failed video (local path resolved via <see cref="ApiService.GetVideoAsync"/>).
///
/// Filtering is by file existence rather than a local queue — if we don't hold the
/// local file on disk, the command isn't ours.
/// Handling is idempotent — if two stations claim the same video, only the
/// one with the file on disk succeeds.
/// </summary>
[SupportedOSPlatform("windows")]
public static class UploadCommandListener
{
    private static CancellationTokenSource? _cts;
    private static bool _started;

    public static void Start()
    {
        if (_started) return;
        _started = true;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => RunAsync(_cts.Token));
    }

    public static void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _started = false;
    }

    private static async Task RunAsync(CancellationToken ct)
    {
        var backoffSec = 2;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAndPumpAsync(ct);
                backoffSec = 2; // reset on clean disconnect
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Logger.Log($"UploadCommandListener: {ex.Message} — reconnecting in {backoffSec}s");
                try { await Task.Delay(TimeSpan.FromSeconds(backoffSec), ct); } catch { }
                backoffSec = Math.Min(backoffSec * 2, 60); // cap at 60s
            }
        }
    }

    private static async Task ConnectAndPumpAsync(CancellationToken ct)
    {
        var wsUrl = BuildWsUrl();
        if (wsUrl == null)
        {
            // No API configured — wait and retry; user may still be in Settings.
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return;
        }

        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

        await socket.ConnectAsync(new Uri(wsUrl), ct);
        Logger.Log($"UploadCommandListener: connected to {wsUrl}");

        var buffer = new byte[16 * 1024];
        var ms = new MemoryStream();

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            ms.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "server closed", ct);
                    return;
                }
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            var text = Encoding.UTF8.GetString(ms.ToArray());
            HandleMessage(text);
        }
    }

    private static string? BuildWsUrl()
    {
        var api = AppSettings.ApiUrl?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(api)) return null;
        // http://host:port → ws://host:port/packing-lists/events
        if (api.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return "ws://" + api["http://".Length..] + "/packing-lists/events";
        if (api.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return "wss://" + api["https://".Length..] + "/packing-lists/events";
        return "ws://" + api + "/packing-lists/events";
    }

    private static void HandleMessage(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!root.TryGetProperty("event", out var evEl)) return;
            var eventName = evEl.GetString();
            if (eventName != "upload_command_issued") return;
            if (!root.TryGetProperty("data", out var data)) return;

            var commandId = data.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var cid) ? cid : -1;
            var videoId   = data.TryGetProperty("videoId", out var vEl) && vEl.TryGetInt32(out var vid) ? vid : -1;
            if (commandId < 0 || videoId < 0) return;

            _ = Task.Run(() => HandleRetryAsync(commandId, videoId));
        }
        catch (Exception ex)
        {
            Logger.Log($"UploadCommandListener.HandleMessage: {ex.Message}");
        }
    }

    private static async Task HandleRetryAsync(long commandId, int videoId)
    {
        var video = await ApiService.GetVideoAsync(videoId);
        if (video is null)
        {
            // Video not found — not our concern or already deleted
            return;
        }

        if (string.IsNullOrEmpty(video.FilePath) || !File.Exists(video.FilePath))
        {
            Logger.Log($"UploadCommandListener: command {commandId} video {videoId} — local file gone ({video.FilePath})");
            await ApiService.PatchUploadCommandAsync(commandId, "rejected", "local_file_missing");
            return;
        }

        Logger.Log($"UploadCommandListener: command {commandId} → acknowledged; retrying {video.FilePath}");
        await ApiService.PatchUploadCommandAsync(commandId, "acknowledged");

        VideoWorkflowManager.HandleRetry(videoId, video.FilePath,
            video.TrackingNumber ?? "", AppSettings.ResolvedStationId);

        // Poll backend status until completed, failed, or timeout (~120s)
        var deadline = DateTime.UtcNow.AddSeconds(120);
        var lastStatus = video.Status;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            var current = await ApiService.GetVideoAsync(videoId);
            if (current is null) break;

            if (current.Status == "Completed")
            {
                await ApiService.PatchUploadCommandAsync(commandId, "completed");
                Logger.Log($"UploadCommandListener: command {commandId} → completed");
                return;
            }
            if (current.Status == "Failed" && current.Status != lastStatus)
            {
                await ApiService.PatchUploadCommandAsync(commandId, "rejected", "upload_failed");
                Logger.Log($"UploadCommandListener: command {commandId} → rejected (upload_failed)");
                return;
            }
            lastStatus = current.Status;
        }

        await ApiService.PatchUploadCommandAsync(commandId, "rejected", "timeout");
        Logger.Log($"UploadCommandListener: command {commandId} → rejected (timeout)");
    }
}
