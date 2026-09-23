using RoboMouse.Core.Logging;

namespace RoboMouse.Core.Input;

/// <summary>
/// Reports when this Windows session is locked or unlocked. While the lock screen (or any secure
/// desktop) is up the low-level hooks see nothing, so key-ups pressed there never reach us and the
/// hook's idea of which keys are held goes stale. Uses a message-only window; create it on a thread
/// that pumps messages.
/// </summary>
public sealed class SessionMonitor : IDisposable
{
    private readonly MessageWindow _window;
    private readonly bool _registered;

    /// <summary>Raised on the window's thread: true when the session was locked, false when unlocked.</summary>
    public event Action<bool>? LockChanged;

    public SessionMonitor()
    {
        _window = new MessageWindow();
        _window.Message += OnMessage;
        _registered = NativeMethods.WTSRegisterSessionNotification(_window.Handle, NativeMethods.NOTIFY_FOR_THIS_SESSION);
        if (!_registered)
            SimpleLogger.Log("Session", "Could not register for lock/unlock notifications");
    }

    private void OnMessage(uint msg, nint wParam, nint lParam)
    {
        if (msg != NativeMethods.WM_WTSSESSION_CHANGE)
            return;
        switch ((int)wParam)
        {
            case NativeMethods.WTS_SESSION_LOCK:
                LockChanged?.Invoke(true);
                break;
            case NativeMethods.WTS_SESSION_UNLOCK:
                LockChanged?.Invoke(false);
                break;
        }
    }

    public void Dispose()
    {
        if (_registered)
            NativeMethods.WTSUnRegisterSessionNotification(_window.Handle);
        _window.Message -= OnMessage;
        _window.Dispose();
    }
}
