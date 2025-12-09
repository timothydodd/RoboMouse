using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RoboMouse.Core.Input;

/// <summary>
/// Global low-level mouse hook for capturing all mouse input.
/// </summary>
public sealed class MouseHook : IDisposable
{
    private IntPtr _hookId = IntPtr.Zero;
    private readonly NativeMethods.LowLevelMouseProc _proc;
    private bool _disposed;

    /// <summary>
    /// Event raised when any mouse event occurs.
    /// </summary>
    public event EventHandler<MouseEventArgs>? MouseEvent;

    /// <summary>
    /// Whether the hook is currently active.
    /// </summary>
    public bool IsHooked => _hookId != IntPtr.Zero;

    public MouseHook()
    {
        _proc = HookCallback;
    }

    /// <summary>
    /// Installs the global mouse hook.
    /// </summary>
    public void Install()
    {
        if (_hookId != IntPtr.Zero)
            return;

        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;

        if (module == null)
            throw new InvalidOperationException("Could not get main module for hook installation.");

        _hookId = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_MOUSE_LL,
            _proc,
            NativeMethods.GetModuleHandle(module.ModuleName),
            0);

        if (_hookId == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"Failed to install mouse hook. Error code: {error}");
        }
    }

    /// <summary>
    /// Removes the global mouse hook.
    /// </summary>
    public void Uninstall()
    {
        if (_hookId == IntPtr.Zero)
            return;

        NativeMethods.UnhookWindowsHookEx(_hookId);
        _hookId = IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var hookStruct = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
            var eventArgs = CreateEventArgs((int)wParam, hookStruct);

            if (eventArgs != null)
            {
                MouseEvent?.Invoke(this, eventArgs);

                if (eventArgs.Handled)
                {
                    return (IntPtr)1; // Block the event
                }
            }
        }

        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
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
            hookStruct.time);
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
