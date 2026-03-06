using System.Runtime.Versioning;

namespace app.Services;

[SupportedOSPlatform("windows")]
public static class AppSettings
{
    private const string KeyVideoFolder = "settings.video_folder";
    private const string KeyWebhookUrl = "settings.webhook_url";

    public static readonly string DefaultVideoFolder =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Warehouse Videos"
        );

    public const string DefaultWebhookUrl =
        "http://localhost:5678/webhook-test/7842c780-4224-4c16-abb7-2973e1407835";

    public static string VideoFolder
    {
        get => Preferences.Default.Get(KeyVideoFolder, DefaultVideoFolder);
        set => Preferences.Default.Set(KeyVideoFolder, value);
    }

    public static string WebhookUrl
    {
        get => Preferences.Default.Get(KeyWebhookUrl, DefaultWebhookUrl);
        set => Preferences.Default.Set(KeyWebhookUrl, value);
    }

    private const string KeyDbHost     = "settings.db_host";
    private const string KeyDbPort     = "settings.db_port";
    private const string KeyDbDatabase = "settings.db_database";
    private const string KeyDbUser     = "settings.db_user";
    private const string KeyDbPassword = "settings.db_password";

    public static string DbHost
    {
        get => Preferences.Default.Get(KeyDbHost, "localhost");
        set => Preferences.Default.Set(KeyDbHost, value);
    }

    public static int DbPort
    {
        get => Preferences.Default.Get(KeyDbPort, 5432);
        set => Preferences.Default.Set(KeyDbPort, value);
    }

    public static string DbDatabase
    {
        get => Preferences.Default.Get(KeyDbDatabase, "");
        set => Preferences.Default.Set(KeyDbDatabase, value);
    }

    public static string DbUser
    {
        get => Preferences.Default.Get(KeyDbUser, "");
        set => Preferences.Default.Set(KeyDbUser, value);
    }

    public static string DbPassword
    {
        get => Preferences.Default.Get(KeyDbPassword, "");
        set => Preferences.Default.Set(KeyDbPassword, value);
    }

    public static string ConnectionString =>
        string.IsNullOrWhiteSpace(DbHost) ? "" :
        $"Host={DbHost};Port={DbPort};Database={DbDatabase};Username={DbUser};Password={DbPassword}";


}
