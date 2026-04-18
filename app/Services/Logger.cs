using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text;

namespace app.Services;

[SupportedOSPlatform("windows")]
public static class Logger
{
    private static readonly string LogsDir =
        Path.Combine(FileSystem.AppDataDirectory, "logs");
    private static readonly object _lock = new();

    public static void Log(string message, [CallerFilePath] string? source = null)
        => Write("INF", null, message, source);

    public static void Log(Exception ex, [CallerFilePath] string? source = null)
        => Write("ERR", ex, ex.Message, source);

    public static void LogDebug(string message, [CallerFilePath] string? source = null)
        => Write("DBG", null, message, source);

    public static void LogInfo(string message, [CallerFilePath] string? source = null)
        => Write("INF", null, message, source);

    public static void LogWarn(string message, [CallerFilePath] string? source = null)
        => Write("WRN", null, message, source);

    public static void LogError(string message, [CallerFilePath] string? source = null)
        => Write("ERR", null, message, source);

    private static void Write(string level, Exception? ex, string message, string? filePath)
    {
        var src  = string.IsNullOrEmpty(filePath) ? "app" : Path.GetFileNameWithoutExtension(filePath);
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] [{src}] {message}";

        Debug.WriteLine(line);

        try
        {
            Directory.CreateDirectory(LogsDir);
            var file = Path.Combine(LogsDir, $"{DateTime.Now:yyyy-MM-dd}.txt");
            lock (_lock)
            {
                File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8);
                if (ex != null)
                    File.AppendAllText(file, ex.ToString() + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception writeEx)
        {
            Debug.WriteLine($"Logger.Write failed: {writeEx.Message}");
        }
    }
}
