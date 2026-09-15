using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RoboMouse.Core.Input;

/// <summary>
/// A Win32 message-only window (parent <c>HWND_MESSAGE</c>) that receives window messages on the thread
/// that created it and can run callbacks there. Replaces the hidden Windows Forms controls the app used
/// for clipboard notifications, raw input and cross-thread marshalling. The creating thread must pump
/// messages (the UI thread, or a <see cref="StaWorker"/>).
/// </summary>
public sealed unsafe class MessageWindow : IDisposable
{
    private const string ClassName = "RoboMouse.MessageWindow";
    private const uint WM_APP_INVOKE = 0x8000 + 1; // WM_APP + 1
    private const int GWLP_USERDATA = -21;

    private static readonly object RegisterLock = new();
    private static ushort s_classAtom;

    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private GCHandle _self;
    private bool _disposed;

    /// <summary>Raised for every message the window receives, on the owning thread.</summary>
    public event Action<uint, nint, nint>? Message;

    public nint Handle { get; private set; }

    public MessageWindow()
    {
        EnsureClassRegistered();

        _self = GCHandle.Alloc(this);
        Handle = NativeMethods.CreateWindowExW(0, ClassName, ClassName, 0, 0, 0, 0, 0, NativeMethods.HWND_MESSAGE, 0, NativeMethods.GetModuleHandleW(null), 0);
        if (Handle == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            _self.Free();
            throw new InvalidOperationException($"CreateWindowEx failed. Error code: {error}");
        }

        NativeMethods.SetWindowLongPtrW(Handle, GWLP_USERDATA, GCHandle.ToIntPtr(_self));
    }

    /// <summary>True when called on the thread that owns the window.</summary>
    public bool IsOwnerThread => Environment.CurrentManagedThreadId == _threadId;

    /// <summary>Queues <paramref name="action"/> to run on the owning thread without waiting.</summary>
    public void BeginInvoke(Action action)
    {
        if (_disposed)
            return;
        _queue.Enqueue(action);
        NativeMethods.PostMessageW(Handle, WM_APP_INVOKE, 0, 0);
    }

    /// <summary>Runs <paramref name="action"/> on the owning thread and waits for it.</summary>
    public void Invoke(Action action)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(MessageWindow));

        if (IsOwnerThread)
        {
            action();
            return;
        }

        using var done = new ManualResetEventSlim(false);
        Exception? failure = null;
        BeginInvoke(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally { done.Set(); }
        });
        done.Wait();
        if (failure != null)
            throw new InvalidOperationException("The invoked action failed.", failure);
    }

    private nint WndProc(uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_APP_INVOKE)
        {
            while (_queue.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { Logging.SimpleLogger.Log("MessageWindow", $"Queued action failed: {ex}"); }
            }
            return 0;
        }

        Message?.Invoke(msg, wParam, lParam);
        return NativeMethods.DefWindowProcW(Handle, msg, wParam, lParam);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static nint StaticWndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        var data = NativeMethods.GetWindowLongPtrW(hwnd, GWLP_USERDATA);
        if (data != 0 && GCHandle.FromIntPtr(data).Target is MessageWindow window)
        {
            try
            {
                return window.WndProc(msg, wParam, lParam);
            }
            catch (Exception ex)
            {
                Logging.SimpleLogger.Log("MessageWindow", $"WndProc failed: {ex}");
                return 0;
            }
        }
        return NativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private static void EnsureClassRegistered()
    {
        lock (RegisterLock)
        {
            if (s_classAtom != 0)
                return;

            fixed (char* name = ClassName)
            {
                var wc = new NativeMethods.WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(NativeMethods.WNDCLASSEXW),
                    lpfnWndProc = &StaticWndProc,
                    hInstance = NativeMethods.GetModuleHandleW(null),
                    lpszClassName = name
                };
                s_classAtom = NativeMethods.RegisterClassExW(&wc);
                if (s_classAtom == 0)
                    throw new InvalidOperationException($"RegisterClassEx failed. Error code: {Marshal.GetLastPInvokeError()}");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        void Destroy()
        {
            if (Handle != 0)
            {
                NativeMethods.SetWindowLongPtrW(Handle, GWLP_USERDATA, 0);
                NativeMethods.DestroyWindow(Handle);
                Handle = 0;
            }
            if (_self.IsAllocated)
                _self.Free();
        }

        // A window can only be destroyed by the thread that created it.
        if (IsOwnerThread)
            Destroy();
        else
        {
            _queue.Enqueue(Destroy);
            NativeMethods.PostMessageW(Handle, WM_APP_INVOKE, 0, 0);
        }
    }
}
