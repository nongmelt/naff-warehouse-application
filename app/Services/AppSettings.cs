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
}
