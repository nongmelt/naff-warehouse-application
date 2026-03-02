using app.Services;
using CommunityToolkit.Maui.Storage;
using System.Runtime.Versioning;

namespace app.Views;

[SupportedOSPlatform("windows")]
public partial class SettingsPage : ContentPage
{
    public SettingsPage()
    {
        InitializeComponent();
        VideoFolderEntry.Text = AppSettings.VideoFolder;
        WebhookUrlEntry.Text  = AppSettings.WebhookUrl;
    }

    private async void OnBrowseFolder(object sender, EventArgs e)
    {
        try
        {
            var result = await FolderPicker.Default.PickAsync(CancellationToken.None);
            if (result.IsSuccessful)
                VideoFolderEntry.Text = result.Folder.Path;
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Error", $"Could not open folder picker: {ex.Message}", "OK");
        }
    }

    private async void OnSave(object sender, EventArgs e)
    {
        var folder = VideoFolderEntry.Text?.Trim() ?? string.Empty;
        var url    = WebhookUrlEntry.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(folder))
        {
            await DisplayAlertAsync("Validation", "Video folder path cannot be empty.", "OK");
            return;
        }

        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            await DisplayAlertAsync("Validation", "Please enter a valid webhook URL.", "OK");
            return;
        }

        AppSettings.VideoFolder = folder;
        AppSettings.WebhookUrl  = url;
        Logger.Log($"Settings saved — VideoFolder: {folder}, WebhookUrl: {url}");

        await Navigation.PopModalAsync();
    }

    private async void OnCancel(object sender, EventArgs e) =>
        await Navigation.PopModalAsync();
}
