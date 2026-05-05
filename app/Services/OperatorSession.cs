using System.Runtime.Versioning;

namespace app.Services;

public enum SessionKind { Packing, QC }

/// <summary>
/// Per-station operator session with configurable inactivity auto-logout.
/// Thread-safe: Login/Logout/TouchActivity are safe to call from any thread.
/// Events fire on a thread-pool thread — callers must marshal to the UI thread.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OperatorSession : IDisposable
{
    private readonly System.Timers.Timer _inactivityTimer;
    private readonly SessionKind _kind;
    private volatile OperatorBadge? _badge;
    private DateTime _lastActivity = DateTime.UtcNow;

    public OperatorBadge? Badge => _badge;
    public bool IsLoggedIn => _badge is not null;

    /// <summary>Fired when an operator logs in or out. Argument = new badge, or null on logout.</summary>
    public event Action<OperatorBadge?>? OperatorChanged;

    /// <summary>Fired after auto-logout due to inactivity.</summary>
    public event Action? SessionExpired;

    public OperatorSession(SessionKind kind = SessionKind.Packing)
    {
        _kind = kind;
        _inactivityTimer = new System.Timers.Timer(TimeSpan.FromSeconds(30));
        _inactivityTimer.Elapsed += OnTimerElapsed;
        _inactivityTimer.AutoReset = true;
        _inactivityTimer.Start();
    }

    public void Login(OperatorBadge badge)
    {
        _badge = badge;
        _lastActivity = DateTime.UtcNow;
        Logger.Log($"OperatorSession({_kind}): Login — {badge.DisplayName}");
        OperatorChanged?.Invoke(badge);
    }

    public void Logout()
    {
        var prev = _badge;
        _badge = null;
        if (prev is not null)
        {
            Logger.Log($"OperatorSession({_kind}): Logout — {prev.DisplayName}");
            OperatorChanged?.Invoke(null);
        }
    }

    /// <summary>Call on any meaningful operator scan to reset the inactivity clock.</summary>
    public void TouchActivity() => _lastActivity = DateTime.UtcNow;

    private void OnTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (!IsLoggedIn) return;
        var idle = DateTime.UtcNow - _lastActivity;
        var limit = _kind == SessionKind.QC
            ? AppSettings.QcInactivityMinutes
            : AppSettings.PackingInactivityMinutes;
        if (idle.TotalMinutes >= limit)
        {
            Logger.Log($"OperatorSession({_kind}): Auto-logout after {idle.TotalMinutes:F1} min idle");
            Logout();
            SessionExpired?.Invoke();
        }
    }

    public void Dispose()
    {
        _inactivityTimer.Stop();
        _inactivityTimer.Dispose();
    }
}
