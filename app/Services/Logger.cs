using System.Diagnostics;
using System.Runtime.Versioning;

namespace app.Services;

[SupportedOSPlatform("windows")]

public static class Logger
{
    static string LogFile
    {
        get
        {
            var dir = Path.Combine(FileSystem.AppDataDirectory, "logs");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, $"{DateTime.Now:yyyy-MM-dd}.txt");
        }
    }

    public static void Log(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
        Debug.WriteLine(line);
        File.AppendAllText(LogFile, line + Environment.NewLine);
    }

    public static void Log(Exception ex)
    {
        Log($"ERROR: {ex}");
    }
}
