using RoboMouse.Core.Logging;

namespace RoboMouse.Core.Input;

/// <summary>
/// Knows whether a full-screen program (a game, a video, a presentation) is in the foreground, for the
/// "don't cross while a full-screen app is in front" guard. The shell's own answer
/// (<c>SHQueryUserNotificationState</c>) catches exclusive full-screen and presentation mode; a
/// borderless window covering its whole monitor catches the rest. Checking is too slow for the mouse
/// hook, so a timer polls twice a second while the guard is on and the hook reads the last answer.
/// </summary>
public sealed unsafe class FullScreenDetector : IDisposable
{
    private const int PollMs = 500;

    private readonly object _lock = new();
    private Timer? _timer;
    private volatile bool _isFullScreen;

    /// <summary>The last answer: true while a full-screen program was in front. False while not polling.</summary>
    public bool IsFullScreen => _isFullScreen;

    /// <summary>Starts or stops polling.</summary>
    public bool Enabled
    {
        get { lock (_lock) return _timer != null; }
        set
        {
            lock (_lock)
            {
                if (value == (_timer != null))
                    return;
                if (value)
                {
                    _timer = new Timer(_ => Poll(), null, 0, PollMs);
                }
                else
                {
                    _timer!.Dispose();
                    _timer = null;
                    _isFullScreen = false;
                }
            }
        }
    }

    private void Poll()
    {
        try
        {
            _isFullScreen = Check();
        }
        catch (Exception ex)
        {
            _isFullScreen = false;
            SimpleLogger.Log("Crossing", $"Full-screen check failed: {ex.Message}");
            Enabled = false;
        }
    }

    private static bool Check()
    {
        int state;
        if (NativeMethods.SHQueryUserNotificationState(&state) == 0 && IsFullScreenState(state))
            return true;

        var window = NativeMethods.GetForegroundWindow();
        if (window == 0 || window == NativeMethods.GetShellWindow() || window == NativeMethods.GetDesktopWindow() || IsDesktopClass(window))
            return false;

        NativeMethods.RECT rect;
        if (!NativeMethods.GetWindowRect(window, &rect))
            return false;
        var monitor = NativeMethods.MonitorFromWindow(window, NativeMethods.MONITOR_DEFAULTTONULL);
        if (monitor == 0)
            return false;
        var info = new NativeMethods.MONITORINFO { cbSize = (uint)sizeof(NativeMethods.MONITORINFO) };
        if (!NativeMethods.GetMonitorInfoW(monitor, &info))
            return false;

        return Covers(rect.Left, rect.Top, rect.Right, rect.Bottom,
            info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom);
    }

    /// <summary>The shell's notification states that mean something is running full screen.</summary>
    public static bool IsFullScreenState(int state) =>
        state is NativeMethods.QUNS_BUSY or NativeMethods.QUNS_RUNNING_D3D_FULL_SCREEN or NativeMethods.QUNS_PRESENTATION_MODE;

    /// <summary>
    /// True when a window rectangle covers the whole monitor (a maximized window stops at the taskbar,
    /// so it does not).
    /// </summary>
    public static bool Covers(int left, int top, int right, int bottom, int monitorLeft, int monitorTop, int monitorRight, int monitorBottom) =>
        left <= monitorLeft && top <= monitorTop && right >= monitorRight && bottom >= monitorBottom;

    /// <summary>The desktop behind the icons (Progman / WorkerW) covers the monitor but is not a program in front.</summary>
    private static bool IsDesktopClass(nint window)
    {
        var buffer = stackalloc char[32];
        var length = NativeMethods.GetClassNameW(window, buffer, 32);
        if (length <= 0)
            return false;
        var name = new ReadOnlySpan<char>(buffer, length);
        return name.SequenceEqual("Progman") || name.SequenceEqual("WorkerW");
    }

    public void Dispose() => Enabled = false;
}
