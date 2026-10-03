using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Maui.Storage;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace app.Services;

/// <summary>
/// Audio-feedback helper for scan verdicts.
/// <para>
/// A shipped parcel plays a single high success tone (880 Hz), rendered in memory and played via
/// winmm PlaySound. Every other outcome (a not-shipped parcel) plays the bundled
/// <c>error-pop.mp3</c> sound effect through the WinRT <see cref="MediaPlayer"/> (winmm can't
/// decode MP3); if that asset can't be loaded/played, it falls back to a synthesised error-pop
/// tone so the operator always hears something.
/// </para>
/// Playback is delayed slightly so it doesn't collide with the barcode scanner's own hardware
/// beep, and is fire-and-forget on a thread-pool thread (wrapped in try/catch) so it never blocks
/// the UI or throws.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Sound
{
    private const int SuccessFreq = 880;
    private const int SuccessMs   = 200;
    private const double Amplitude = 0.95;  // near full-scale — as loud as the output allows
    private const int DelayMs = 350;        // let the scanner's own beep finish first

    private const string ErrorSoundAsset = "error-pop.mp3"; // MauiAsset in Resources/Raw

    // winmm PlaySound flags.
    private const uint SND_SYNC      = 0x0000;
    private const uint SND_MEMORY    = 0x0004;
    private const uint SND_NODEFAULT = 0x0002;

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern bool PlaySound(byte[] data, IntPtr hMod, uint flags);

    // Cached MP3 bytes (loaded once from the app package) and the players kept alive until they
    // finish — a MediaPlayer that goes out of scope mid-playback would be collected and cut off.
    private static byte[]? _errorMp3;
    private static readonly SemaphoreSlim _loadLock = new(1, 1);
    private static readonly List<MediaPlayer> _livePlayers = new();

    /// <summary>True only for outcomes that represent a successful ship.</summary>
    public static bool IsSuccess(PackOutcome outcome) => outcome == PackOutcome.Ship;

    /// <summary>
    /// Fire-and-forget: after a short delay (so it doesn't overlap the scanner beep), plays the
    /// success tone for a ship, or the bundled error-pop sound for any not-shipped outcome,
    /// without blocking the UI.
    /// </summary>
    public static void PlayFor(PackOutcome outcome)
    {
        if (IsSuccess(outcome))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(DelayMs);
                    byte[] wav = BuildWavTone(SuccessFreq, SuccessMs, Amplitude);
                    PlaySound(wav, IntPtr.Zero, SND_MEMORY | SND_SYNC | SND_NODEFAULT);
                }
                catch { /* no audio device / not supported — swallow silently */ }
            });
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(DelayMs);
            try { await PlayErrorSoundAsync(); }
            catch { PlaySynthErrorPop(); }  // codec/device failure → audible fallback
        });
    }

    /// <summary>Plays the bundled error-pop MP3 via the WinRT MediaPlayer (off the UI thread).</summary>
    private static async Task PlayErrorSoundAsync()
    {
        byte[]? bytes = await LoadErrorMp3Async();
        if (bytes is null) { PlaySynthErrorPop(); return; }

        // Feed the MP3 to MediaPlayer as an in-memory random-access stream (no temp file, no
        // dependency on where MauiAsset deploys on an unpackaged Windows build).
        var ras = new InMemoryRandomAccessStream();
        using (var dw = new DataWriter(ras))
        {
            dw.WriteBytes(bytes);
            await dw.StoreAsync();
            dw.DetachStream();
        }
        ras.Seek(0);

        var player = new MediaPlayer { Volume = 1.0 };
        player.Source = MediaSource.CreateFromStream(ras, "audio/mpeg");
        lock (_livePlayers) _livePlayers.Add(player);

        void Cleanup()
        {
            lock (_livePlayers) _livePlayers.Remove(player);
            try { player.Dispose(); } catch { /* already gone */ }
        }
        player.MediaEnded  += (_, _) => Cleanup();
        player.MediaFailed += (_, _) => { Cleanup(); PlaySynthErrorPop(); };

        player.Play();
    }

    private static async Task<byte[]?> LoadErrorMp3Async()
    {
        if (_errorMp3 is not null) return _errorMp3;
        await _loadLock.WaitAsync();
        try
        {
            if (_errorMp3 is not null) return _errorMp3;
            using var s = await FileSystem.OpenAppPackageFileAsync(ErrorSoundAsset);
            using var ms = new MemoryStream();
            await s.CopyToAsync(ms);
            _errorMp3 = ms.ToArray();
            return _errorMp3;
        }
        catch { return null; }
        finally { _loadLock.Release(); }
    }

    /// <summary>Synthesised fallback when the MP3 can't be played: a short descending error-pop.</summary>
    private static void PlaySynthErrorPop()
    {
        try
        {
            byte[] wav = BuildErrorPopWav();
            PlaySound(wav, IntPtr.Zero, SND_MEMORY | SND_SYNC | SND_NODEFAULT);
        }
        catch { /* swallow */ }
    }

    /// <summary>Builds a 16-bit PCM mono WAV sine tone (5 ms fade in/out to avoid clicks).</summary>
    private static byte[] BuildWavTone(int freq, int durationMs, double amplitude)
    {
        const int sampleRate = 44100;
        int sampleCount = sampleRate * durationMs / 1000;
        int fade = (int)(sampleRate * 0.005);
        int dataBytes = sampleCount * 2;

        using var stream = new MemoryStream(44 + dataBytes);
        using var w = new BinaryWriter(stream);
        WriteWavHeader(w, sampleRate, dataBytes);
        for (int i = 0; i < sampleCount; i++)
        {
            double env = 1.0;
            if (i < fade) env = (double)i / fade;
            else if (i > sampleCount - fade) env = (double)(sampleCount - i) / fade;
            double sample = Math.Sin(2 * Math.PI * freq * i / sampleRate) * amplitude * env;
            w.Write((short)(sample * short.MaxValue));
        }
        w.Flush();
        return stream.ToArray();
    }

    /// <summary>
    /// Fallback "error pop": two quick blips whose pitch glides downward, each with a sharp attack
    /// and exponential decay so it reads as a percussive pop rather than a sustained beep.
    /// </summary>
    private static byte[] BuildErrorPopWav()
    {
        const int sampleRate = 44100;
        (double f0, double f1, int ms)[] pops = { (520, 180, 110), (430, 150, 130) };
        const int gapMs = 45;

        int gapSamples = sampleRate * gapMs / 1000;
        int totalSamples = gapSamples; // one gap between the two pops
        foreach (var p in pops) totalSamples += sampleRate * p.ms / 1000;
        int dataBytes = totalSamples * 2;

        using var stream = new MemoryStream(44 + dataBytes);
        using var w = new BinaryWriter(stream);
        WriteWavHeader(w, sampleRate, dataBytes);

        for (int pi = 0; pi < pops.Length; pi++)
        {
            var (f0, f1, ms) = pops[pi];
            int n = sampleRate * ms / 1000;
            int attack = (int)(sampleRate * 0.003);
            double phase = 0;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / n;
                double freq = f0 * Math.Pow(f1 / f0, t);
                phase += 2 * Math.PI * freq / sampleRate;
                double env = i < attack ? (double)i / attack : Math.Exp(-3.5 * t);
                double sample = Math.Sin(phase) * Amplitude * env;
                w.Write((short)(sample * short.MaxValue));
            }
            if (pi < pops.Length - 1)
                for (int i = 0; i < gapSamples; i++) w.Write((short)0);
        }
        w.Flush();
        return stream.ToArray();
    }

    /// <summary>Writes a 44-byte canonical WAV header for 16-bit PCM mono audio.</summary>
    private static void WriteWavHeader(BinaryWriter w, int sampleRate, int dataBytes)
    {
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataBytes);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);
        w.Write((short)1);           // PCM
        w.Write((short)1);           // mono
        w.Write(sampleRate);
        w.Write(sampleRate * 2);     // byte rate
        w.Write((short)2);           // block align
        w.Write((short)16);          // bits/sample
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(dataBytes);
    }
}
