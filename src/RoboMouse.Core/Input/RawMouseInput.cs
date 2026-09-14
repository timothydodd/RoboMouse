using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RoboMouse.Core.Input;

/// <summary>
/// Receives raw (unaccelerated, unclamped) mouse motion via the Raw Input API.
/// Raw input is delivered regardless of cursor position and is not affected by
/// low-level hooks blocking the cooked mouse messages, which lets us freeze the
/// local cursor while still reading exact hardware motion.
/// Must be created on a thread that pumps messages (the UI thread).
/// </summary>
public sealed class RawMouseInput : NativeWindow, IDisposable
{
    private bool _registered;
    private bool _disposed;
    private IntPtr _buffer;
    private uint _bufferSize;

    // For absolute-mode devices (tablets, some RDP/VM mice) we convert to deltas ourselves.
    private bool _haveLastAbsolute;
    private int _lastAbsX;
    private int _lastAbsY;

    /// <summary>
    /// Raised with the relative motion of the physical mouse, in hardware counts.
    /// </summary>
    public event Action<int, int>? Motion;

    public RawMouseInput()
    {
        CreateHandle(new CreateParams { Parent = NativeMethods.HWND_MESSAGE });
        _bufferSize = (uint)Marshal.SizeOf<NativeMethods.RAWINPUT>() + 64;
        _buffer = Marshal.AllocHGlobal((int)_bufferSize);
    }

    /// <summary>
    /// Starts receiving raw mouse input (even while another window is focused).
    /// </summary>
    public void Start()
    {
        if (_registered)
            return;

        var devices = new[]
        {
            new NativeMethods.RAWINPUTDEVICE
            {
                usUsagePage = NativeMethods.HID_USAGE_PAGE_GENERIC,
                usUsage = NativeMethods.HID_USAGE_GENERIC_MOUSE,
                dwFlags = NativeMethods.RIDEV_INPUTSINK,
                hwndTarget = Handle
            }
        };

        if (!NativeMethods.RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<NativeMethods.RAWINPUTDEVICE>()))
        {
            throw new InvalidOperationException($"RegisterRawInputDevices failed. Error code: {Marshal.GetLastWin32Error()}");
        }

        _registered = true;
        _haveLastAbsolute = false;
    }

    /// <summary>
    /// Stops receiving raw mouse input.
    /// </summary>
    public void Stop()
    {
        if (!_registered)
            return;

        var devices = new[]
        {
            new NativeMethods.RAWINPUTDEVICE
            {
                usUsagePage = NativeMethods.HID_USAGE_PAGE_GENERIC,
                usUsage = NativeMethods.HID_USAGE_GENERIC_MOUSE,
                dwFlags = NativeMethods.RIDEV_REMOVE,
                hwndTarget = IntPtr.Zero
            }
        };
        NativeMethods.RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<NativeMethods.RAWINPUTDEVICE>());
        _registered = false;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_INPUT && _registered)
        {
            HandleRawInput(m.LParam);
        }

        base.WndProc(ref m);
    }

    private void HandleRawInput(IntPtr hRawInput)
    {
        uint size = 0;
        var headerSize = (uint)Marshal.SizeOf<NativeMethods.RAWINPUTHEADER>();
        NativeMethods.GetRawInputData(hRawInput, NativeMethods.RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size == 0)
            return;

        if (size > _bufferSize)
        {
            Marshal.FreeHGlobal(_buffer);
            _bufferSize = size;
            _buffer = Marshal.AllocHGlobal((int)_bufferSize);
        }

        if (NativeMethods.GetRawInputData(hRawInput, NativeMethods.RID_INPUT, _buffer, ref size, headerSize) != size)
            return;

        var raw = Marshal.PtrToStructure<NativeMethods.RAWINPUT>(_buffer);
        if (raw.header.dwType != NativeMethods.RIM_TYPEMOUSE)
            return;

        // hDevice is null for input injected via SendInput. We never want to echo our own injection.
        if (raw.header.hDevice == IntPtr.Zero)
            return;

        int dx, dy;
        if ((raw.mouse.usFlags & NativeMethods.MOUSE_MOVE_ABSOLUTE) != 0)
        {
            // Absolute devices report 0..65535 over the (virtual) desktop; convert to a pixel delta.
            var (vx, vy, vw, vh) = InputSimulator.GetVirtualScreenBounds();
            var absX = (int)(raw.mouse.lLastX / 65535.0 * vw) + vx;
            var absY = (int)(raw.mouse.lLastY / 65535.0 * vh) + vy;

            if (!_haveLastAbsolute)
            {
                _lastAbsX = absX;
                _lastAbsY = absY;
                _haveLastAbsolute = true;
                return;
            }

            dx = absX - _lastAbsX;
            dy = absY - _lastAbsY;
            _lastAbsX = absX;
            _lastAbsY = absY;
        }
        else
        {
            dx = raw.mouse.lLastX;
            dy = raw.mouse.lLastY;
        }

        if (dx != 0 || dy != 0)
        {
            Motion?.Invoke(dx, dy);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        Stop();
        DestroyHandle();

        if (_buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
        }
    }
}
