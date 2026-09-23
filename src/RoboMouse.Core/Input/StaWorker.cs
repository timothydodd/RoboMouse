using System.Runtime.InteropServices;

namespace RoboMouse.Core.Input;

/// <summary>
/// A dedicated single-threaded-apartment thread with a message pump. COM objects created on it, such
/// as the clipboard data object that serves virtual files, receive their calls on this thread, so a
/// slow operation (Explorer pulling a large file) never blocks the main UI thread or the input hooks.
/// </summary>
public sealed unsafe class StaWorker : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private MessageWindow? _pump;
    private Exception? _startupError;
    private bool _disposed;

    public StaWorker(string name)
    {
        _thread = new Thread(Run) { Name = name, IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
        if (_startupError != null)
            throw new InvalidOperationException("The STA worker could not start.", _startupError);
    }

    private void Run()
    {
        // SetApartmentState only calls CoInitialize. The OLE clipboard (OleSetClipboard) needs OleInitialize,
        // which a UI framework's main thread gets implicitly but a hand-made thread does not.
        var hr = NativeMethods.OleInitialize(0);
        if (hr < 0)
        {
            _startupError = Marshal.GetExceptionForHR(hr);
            _ready.Set();
            return;
        }

        try
        {
            try
            {
                _pump = new MessageWindow();
            }
            catch (Exception ex)
            {
                _startupError = ex;
                return;
            }
            finally
            {
                _ready.Set();
            }

            NativeMethods.MSG msg;
            while (NativeMethods.GetMessageW(&msg, 0, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(&msg);
                NativeMethods.DispatchMessageW(&msg);
            }
        }
        finally
        {
            NativeMethods.OleUninitialize();
        }
    }

    /// <summary>Runs <paramref name="action"/> on the STA thread and waits for it.</summary>
    public void Invoke(Action action)
    {
        if (_pump == null || _disposed)
            throw new ObjectDisposedException(nameof(StaWorker));

        _pump.Invoke(action);
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the STA thread and waits up to <paramref name="timeout"/> for it.
    /// Returns false when it did not finish in time (it may still run later) or failed.
    /// </summary>
    public bool TryInvoke(Action action, TimeSpan timeout)
    {
        var pump = _pump;
        if (pump == null || _disposed)
            return false;
        if (pump.IsOwnerThread)
        {
            try { action(); return true; }
            catch { return false; }
        }

        var done = new ManualResetEventSlim(false);
        var ok = false;
        pump.BeginInvoke(() =>
        {
            try
            {
                action();
                ok = true;
            }
            catch
            {
            }
            finally
            {
                done.Set();
            }
        });

        // Not disposed here when it timed out: the action may still set it.
        if (!done.Wait(timeout))
            return false;
        done.Dispose();
        return ok;
    }

    /// <summary>Runs <paramref name="func"/> on the STA thread and returns its result.</summary>
    public T Invoke<T>(Func<T> func)
    {
        T result = default!;
        Invoke(() => { result = func(); });
        return result;
    }

    /// <summary>Queues <paramref name="action"/> on the STA thread without waiting.</summary>
    public void BeginInvoke(Action action)
    {
        if (_pump == null || _disposed)
            return;
        _pump.BeginInvoke(action);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            var pump = _pump;
            pump?.BeginInvoke(() =>
            {
                pump.Dispose();
                NativeMethods.PostQuitMessage(0);
            });
        }
        catch { }
    }
}
