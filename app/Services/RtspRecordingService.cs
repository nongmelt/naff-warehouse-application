using System.Diagnostics;
using System.Runtime.Versioning;
namespace app.Services;

[SupportedOSPlatform("windows")]

public class RtspRecordingService : IRecordingService
{
    private Process? _ffmpeg;
    public string? CurrentFile { get; private set; }

    public void StartRecording(string barcode)
    {
        CurrentFile = Path.Combine(
            FileSystem.AppDataDirectory,
            $"{barcode}_{DateTime.Now:yyyyMMdd_HHmmss}.mkv"
        );

        var rtspUrl = "rtsp://pongporn.supa@gmail.com:mukfyq-1fobFa-zekpib@192.168.1.70:554/stream1"; // Replace with actual RTSP URL

        _ffmpeg = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(AppContext.BaseDirectory, "Tools", "ffmpeg.exe"),
                Arguments =
                    $"-rtsp_transport tcp -i \"{rtspUrl}\" -map 0 -c copy  -fflags +genpts -use_wallclock_as_timestamps 1 -reset_timestamps 1 -f matroska \"{CurrentFile}\"",
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        _ffmpeg.Start();
    }

    public void StopRecording()
    {
        if (_ffmpeg == null || _ffmpeg.HasExited)
            return;

        // Graceful stop so file is not corrupted
        _ffmpeg.StandardInput.WriteLine("q");
        _ffmpeg.WaitForExit();
        _ffmpeg.Dispose();
        _ffmpeg = null;
    }
}
