using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RoboMouse.Core.Input;

/// <summary>
/// Global low-level keyboard hook for capturing all keyboard input.
/// </summary>
public sealed unsafe class KeyboardHook : IDisposable
{
    // Low-level hooks call back through a static unmanaged entry point; one hook per process is all
    // the app ever installs, so the instance is kept in a static.
    private static KeyboardHook? s_current;

    private nint _hookId;
    private bool _disposed;

    private const uint LLKHF_EXTENDED = 0x01;
    private const uint LLKHF_INJECTED = 0x10;

    /// <summary>
    /// Event raised when any keyboard event occurs.
    /// </summary>
    public event EventHandler<KeyboardEventArgs>? KeyboardEvent;

    /// <summary>
    /// Whether the hook is currently active.
    /// </summary>
    public bool IsHooked => _hookId != 0;

    /// <summary>
    /// Installs the global keyboard hook.
    /// </summary>
    public void Install()
    {
        if (_hookId != 0)
            return;

        if (s_current != null && s_current != this && s_current._hookId != 0)
            throw new InvalidOperationException("A keyboard hook is already installed.");
        s_current = this;

        _hookId = NativeMethods.SetWindowsHookExW(
            NativeMethods.WH_KEYBOARD_LL,
            &HookCallback,
            NativeMethods.GetModuleHandleW(null),
            0);

        if (_hookId == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            s_current = null;
            throw new InvalidOperationException($"Failed to install keyboard hook. Error code: {error}");
        }
    }

    /// <summary>
    /// Removes the global keyboard hook.
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
                var eventArgs = CreateEventArgs((int)wParam, *(NativeMethods.KBDLLHOOKSTRUCT*)lParam);
                if (eventArgs != null)
                {
                    hook.KeyboardEvent?.Invoke(hook, eventArgs);

                    if (eventArgs.Handled)
                    {
                        return 1; // Block the event
                    }
                }
            }
            catch (Exception ex)
            {
                // Never let an exception escape into the hook chain.
                Logging.SimpleLogger.Log("KeyboardHook", ex.ToString());
            }
        }

        return NativeMethods.CallNextHookEx(hook._hookId, nCode, wParam, lParam);
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
            hookStruct.time,
            (hookStruct.flags & LLKHF_INJECTED) != 0);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Uninstall();
        _disposed = true;
    }
}
