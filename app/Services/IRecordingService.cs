using System.Runtime.Versioning;
namespace app.Services;

[SupportedOSPlatform("windows")]

public interface IRecordingService
{
    void StartRecording(string barcode);
    void StopRecording();
    string? CurrentFile { get; }
}
