using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RoboMouse.Core.Input;

/// <summary>
/// Global low-level keyboard hook for capturing all keyboard input.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private IntPtr _hookId = IntPtr.Zero;
    private readonly NativeMethods.LowLevelKeyboardProc _proc;
    private bool _disposed;

    private const uint LLKHF_EXTENDED = 0x01;

    /// <summary>
    /// Event raised when any keyboard event occurs.
    /// </summary>
    public event EventHandler<KeyboardEventArgs>? KeyboardEvent;

    /// <summary>
    /// Whether the hook is currently active.
    /// </summary>
    public bool IsHooked => _hookId != IntPtr.Zero;

    public KeyboardHook()
    {
        _proc = HookCallback;
    }

    /// <summary>
    /// Installs the global keyboard hook.
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
            NativeMethods.WH_KEYBOARD_LL,
            _proc,
            NativeMethods.GetModuleHandle(module.ModuleName),
            0);

        if (_hookId == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"Failed to install keyboard hook. Error code: {error}");
        }
    }

    /// <summary>
    /// Removes the global keyboard hook.
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
            var hookStruct = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            var eventArgs = CreateEventArgs((int)wParam, hookStruct);

            if (eventArgs != null)
            {
                KeyboardEvent?.Invoke(this, eventArgs);

                if (eventArgs.Handled)
                {
                    return (IntPtr)1; // Block the event
                }
            }
        }

        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private static KeyboardEventArgs? CreateEventArgs(int wParam, NativeMethods.KBDLLHOOKSTRUCT hookStruct)
    {
        KeyboardEventType? eventType = wParam switch
        {
            NativeMethods.WM_KEYDOWN => KeyboardEventType.KeyDown,
            NativeMethods.WM_KEYUP => KeyboardEventType.KeyUp,
            NativeMethods.WM_SYSKEYDOWN => KeyboardEventType.SysKeyDown,
            NativeMethods.WM_SYSKEYUP => KeyboardEventType.SysKeyUp,
            _ => null
        };

        if (eventType == null)
            return null;

        var isExtended = (hookStruct.flags & LLKHF_EXTENDED) != 0;

        return new KeyboardEventArgs(
            (Keys)hookStruct.vkCode,
            hookStruct.scanCode,
            eventType.Value,
            isExtended,
            hookStruct.time);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Uninstall();
        _disposed = true;
    }
}
