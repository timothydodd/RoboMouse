using System.Windows.Forms;

namespace RoboMouse.Core.Input;

/// <summary>
/// A dedicated single-threaded-apartment thread with a message pump. COM objects created on it, such
/// as the clipboard data object that serves virtual files, receive their calls on this thread, so a
/// slow operation (Explorer pulling a large file) never blocks the main UI thread or the input hooks.
/// </summary>
public sealed class StaWorker : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private Control? _pump;
    private bool _disposed;

    public StaWorker(string name)
    {
        _thread = new Thread(Run) { Name = name, IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
    }

    private void Run()
    {
        _pump = new Control();
        _pump.CreateControl();
        _ = _pump.Handle;
        _ready.Set();
        Application.Run();
    }

    /// <summary>Runs <paramref name="action"/> on the STA thread and waits for it.</summary>
    public void Invoke(Action action)
    {
        if (_pump == null || _disposed)
            throw new ObjectDisposedException(nameof(StaWorker));

        if (_pump.InvokeRequired)
            _pump.Invoke(action);
        else
            action();
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
            _pump?.BeginInvoke(() =>
            {
                _pump.Dispose();
                Application.ExitThread();
            });
        }
        catch { }
    }
}
