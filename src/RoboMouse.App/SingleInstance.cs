using System.Runtime.InteropServices;
using RoboMouse.Core.Logging;

namespace RoboMouse.App;

/// <summary>
/// One running copy per user session. The first instance owns a named mutex and waits on a named
/// event; a second launch signals that event (asking the running copy to show its Settings window)
/// and exits without showing anything of its own.
/// </summary>
internal sealed partial class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\RoboMouse_SingleInstance";
    private const string ShowEventName = @"Local\RoboMouse_ShowSettings";

    private readonly Mutex? _mutex;
    private readonly EventWaitHandle? _showEvent;
    private readonly bool _isFirst;
    private Thread? _listener;
    private volatile bool _disposed;

    /// <summary>True when this process is the running copy; false when another instance already exists.</summary>
    public bool IsFirstInstance => _isFirst;

    /// <summary>
    /// True when the running copy was started as administrator: its mutex and event are then out of
    /// reach of this (normal) process, so it can neither be asked to show Settings nor be joined.
    /// </summary>
    public bool OtherCopyIsElevated { get; }

    /// <summary>Raised on a background thread when another launch asked for the Settings window.</summary>
    public event Action? ShowRequested;

    public SingleInstance()
    {
        try
        {
            _mutex = new Mutex(true, MutexName, out _isFirst);
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        }
        catch (UnauthorizedAccessException)
        {
            // An elevated copy created these with a DACL a normal process may not open.
            _mutex?.Dispose();
            _mutex = null;
            _isFirst = false;
            OtherCopyIsElevated = true;
        }
    }

    /// <summary>
    /// Called by a second launch: hands the foreground to the running copy and signals it. Windows only
    /// lets the foreground process grant that right, which is why the new process does it before exiting.
    /// </summary>
    public void SignalExistingInstance()
    {
        if (_showEvent == null)
            return;
        AllowSetForegroundWindow(ASFW_ANY);
        _showEvent.Set();
    }

    /// <summary>Tells the user another copy is running as administrator (there is no UI framework yet).</summary>
    public static void ShowElevatedCopyMessage()
    {
        MessageBoxW(IntPtr.Zero,
            "RoboMouse is already running as administrator, so this copy cannot start or reach it.\n\n" +
            "Exit that copy from its tray icon, then start RoboMouse again normally.",
            "RoboMouse", MB_OK | MB_ICONINFORMATION);
    }

    /// <summary>Called by the running copy: starts listening for show requests from later launches.</summary>
    public void Listen()
    {
        if (_listener != null || _showEvent == null)
            return;
        _listener = new Thread(() =>
        {
            while (!_disposed)
            {
                try
                {
                    if (_showEvent!.WaitOne(500) && !_disposed)
                        ShowRequested?.Invoke();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    SimpleLogger.Log("SingleInstance", ex.ToString());
                }
            }
        })
        {
            Name = "RoboMouse-SingleInstance",
            IsBackground = true
        };
        _listener.Start();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_isFirst)
        {
            try { _mutex?.ReleaseMutex(); } catch { }
        }
        _mutex?.Dispose();
        _showEvent?.Dispose();
    }

    private const int ASFW_ANY = -1;
    private const uint MB_OK = 0x0;
    private const uint MB_ICONINFORMATION = 0x40;

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);


    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int dwProcessId);
}
