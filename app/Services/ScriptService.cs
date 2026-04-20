using System.Runtime.Versioning;
using System.Text;

namespace app.Services;

[SupportedOSPlatform("windows")]
public static class ScriptService
{
    public static void RegenerateScripts()
    {
        var appDir     = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var scriptsDir = Path.Combine(appDir, "Scripts");
        Directory.CreateDirectory(scriptsDir);

        var pcName      = Environment.MachineName;
        var bucket      = AppSettings.MinioBucket;
        var accessKey   = AppSettings.MinioAccessKey;
        var secretKey   = AppSettings.MinioSecretKey;
        var endpoint    = AppSettings.MinioEndpoint;
        var videoFolder = AppSettings.VideoFolder;
        var confPath    = Path.Combine(scriptsDir, "rclone.conf");
        var rclonePath  = Path.Combine(scriptsDir, "rclone.exe");

        // ── rclone.conf ──────────────────────────────────────────────────────
        var conf = new StringBuilder();
        conf.AppendLine("[minio]");
        conf.AppendLine("type = s3");
        conf.AppendLine("provider = Minio");
        conf.AppendLine("env_auth = true");
        conf.AppendLine($"access_key_id = {accessKey}");
        conf.AppendLine($"secret_access_key = {secretKey}");
        conf.AppendLine($"endpoint = {endpoint}");
        conf.AppendLine("acl = private");
        File.WriteAllText(confPath, conf.ToString());

        // ── sync_to_minio.bat ────────────────────────────────────────────────
        var sync = new StringBuilder();
        sync.AppendLine("@echo off");
        sync.AppendLine(":: MinIO Video Sync Script");
        sync.AppendLine($"set LOCAL_VIDEO_FOLDER={videoFolder}");
        sync.AppendLine($"set MINIO_BUCKET={bucket}");
        sync.AppendLine($"set RCLONE_CONFIG={confPath}");
        sync.AppendLine($"set RCLONE_EXE={rclonePath}");
        sync.AppendLine($"set PC_NAME={pcName}");
        sync.AppendLine(@"set LOG_DIR=%LOCALAPPDATA%\Warehouse\logs\sync");
        sync.AppendLine(@"set LOG_FILE=%LOG_DIR%\sync_%PC_NAME%.log");
        sync.AppendLine();
        sync.AppendLine("if not exist \"%LOG_DIR%\" mkdir \"%LOG_DIR%\"");
        sync.AppendLine();
        sync.AppendLine("echo [%date% %time%] Starting upload for %PC_NAME% >> \"%LOG_FILE%\"");
        sync.AppendLine();
        sync.AppendLine("\"%RCLONE_EXE%\" copy \"%LOCAL_VIDEO_FOLDER%\" minio:%MINIO_BUCKET%/%PC_NAME%/ ^");
        sync.AppendLine("  --config \"%RCLONE_CONFIG%\" ^");
        sync.AppendLine("  --min-age 1h ^");
        sync.AppendLine("  --transfers 4 ^");
        sync.AppendLine("  --checkers 8 ^");
        sync.AppendLine("  --log-file \"%LOG_FILE%\" ^");
        sync.AppendLine("  --log-level INFO ^");
        sync.AppendLine("  --stats 30s");
        sync.AppendLine();
        sync.AppendLine("echo [%date% %time%] Upload complete >> \"%LOG_FILE%\"");
        File.WriteAllText(Path.Combine(scriptsDir, "sync_to_minio.bat"), sync.ToString());

        Logger.Log($"ScriptService: regenerated scripts in {scriptsDir}");
    }
}
