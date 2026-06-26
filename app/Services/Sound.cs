using System.Runtime.Versioning;

namespace app.Services;

/// <summary>
/// Thin audio-feedback helper for scan verdicts.
/// Plays a success tone (880 Hz, 120 ms) for a shipped parcel and an error tone
/// (220 Hz, 320 ms) for every other outcome, mirroring the web dashboard beeps.
/// Playback is fire-and-forget on a thread-pool thread so it never blocks the UI.
/// Console.Beep is Windows-only; the app targets Windows only, but the call is
/// wrapped in try/catch so it never throws if the PC has no PC speaker.
/// </summary>
public static class Sound
{
    private const int SuccessFreq = 880;
    private const int SuccessMs   = 120;
    private const int ErrorFreq   = 220;
    private const int ErrorMs     = 320;

    /// <summary>True only for outcomes that represent a successful ship.</summary>
    public static bool IsSuccess(PackOutcome outcome) => outcome == PackOutcome.Ship;

    /// <summary>
    /// Fire-and-forget: plays the appropriate beep for <paramref name="outcome"/>
    /// without blocking the calling (UI) thread.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void PlayFor(PackOutcome outcome)
    {
        var (freq, ms) = IsSuccess(outcome)
            ? (SuccessFreq, SuccessMs)
            : (ErrorFreq,   ErrorMs);

        _ = Task.Run(() =>
        {
            try { Console.Beep(freq, ms); }
            catch { /* no PC speaker / not supported — swallow silently */ }
        });
    }
}
