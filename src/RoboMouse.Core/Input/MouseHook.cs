using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RoboMouse.Core.Input;

/// <summary>
/// Global low-level mouse hook for capturing all mouse input.
/// </summary>
public sealed unsafe class MouseHook : IDisposable
{
    // Low-level hooks call back through a static unmanaged entry point; one hook per process is all
    // the app ever installs, so the instance is kept in a static.
    private static MouseHook? s_current;

    private nint _hookId;
    private bool _disposed;

    /// <summary>
    /// Event raised when any mouse event occurs.
    /// </summary>
    public event EventHandler<MouseEventArgs>? MouseEvent;

    /// <summary>
    /// Whether the hook is currently active.
    /// </summary>
    public bool IsHooked => _hookId != 0;

    /// <summary>
    /// Installs the global mouse hook.
    /// </summary>
    public void Install()
    {
        if (_hookId != 0)
            return;

        if (s_current != null && s_current != this && s_current._hookId != 0)
            throw new InvalidOperationException("A mouse hook is already installed.");
        s_current = this;

        _hookId = NativeMethods.SetWindowsHookExW(
            NativeMethods.WH_MOUSE_LL,
            &HookCallback,
            NativeMethods.GetModuleHandleW(null),
            0);

        if (_hookId == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            s_current = null;
            throw new InvalidOperationException($"Failed to install mouse hook. Error code: {error}");
        }
    }

    /// <summary>
    /// Removes the global mouse hook.
    /// </summary>
    public void Uninstall()
    {
        if (_hookId == 0)
            return;

        NativeMethods.UnhookWindowsHookEx(_hookId);
        _hookId = 0;
        if (s_current == this)
            s_current = null;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        var hook = s_current;
        if (hook == null)
            return NativeMethods.CallNextHookEx(0, nCode, wParam, lParam);

        if (nCode >= 0)
        {
            try
            {
                var eventArgs = CreateEventArgs((int)wParam, *(NativeMethods.MSLLHOOKSTRUCT*)lParam);
                if (eventArgs != null)
                {
                    hook.MouseEvent?.Invoke(hook, eventArgs);

                    if (eventArgs.Handled)
                    {
                        return 1; // Block the event
                    }
                }
            }
            catch (Exception ex)
            {
                // Never let an exception escape into the hook chain.
                Logging.SimpleLogger.Log("MouseHook", ex.ToString());
            }
        }

        return NativeMethods.CallNextHookEx(hook._hookId, nCode, wParam, lParam);
    }

    private static MouseEventArgs? CreateEventArgs(int wParam, NativeMethods.MSLLHOOKSTRUCT hookStruct)
    {
        MouseEventType? eventType = wParam switch
        {
            NativeMethods.WM_MOUSEMOVE => MouseEventType.Move,
            NativeMethods.WM_LBUTTONDOWN => MouseEventType.LeftDown,
            NativeMethods.WM_LBUTTONUP => MouseEventType.LeftUp,
            NativeMethods.WM_RBUTTONDOWN => MouseEventType.RightDown,
            NativeMethods.WM_RBUTTONUP => MouseEventType.RightUp,
            NativeMethods.WM_MBUTTONDOWN => MouseEventType.MiddleDown,
            NativeMethods.WM_MBUTTONUP => MouseEventType.MiddleUp,
            NativeMethods.WM_MOUSEWHEEL => MouseEventType.Wheel,
            NativeMethods.WM_MOUSEHWHEEL => MouseEventType.HWheel,
            NativeMethods.WM_XBUTTONDOWN => GetXButtonDownType(hookStruct.mouseData),
            NativeMethods.WM_XBUTTONUP => GetXButtonUpType(hookStruct.mouseData),
            _ => null
        };

        if (eventType == null)
            return null;

        int wheelDelta = 0;
        if (eventType == MouseEventType.Wheel || eventType == MouseEventType.HWheel)
        {
            // High word of mouseData contains the wheel delta
            wheelDelta = (short)(hookStruct.mouseData >> 16);
        }

        return new MouseEventArgs(
            hookStruct.pt.X,
            hookStruct.pt.Y,
            eventType.Value,
            wheelDelta,
            hookStruct.time,
            (hookStruct.flags & NativeMethods.LLMHF_INJECTED) != 0);
    }

    private static MouseEventType GetXButtonDownType(uint mouseData)
    {
        var button = (mouseData >> 16) & 0xFFFF;
        return button == NativeMethods.XBUTTON1
            ? MouseEventType.XButton1Down
            : MouseEventType.XButton2Down;
    }

    private static MouseEventType GetXButtonUpType(uint mouseData)
    {
        var button = (mouseData >> 16) & 0xFFFF;
        return button == NativeMethods.XBUTTON1
            ? MouseEventType.XButton1Up
            : MouseEventType.XButton2Up;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Uninstall();
        _disposed = true;
    }
}
