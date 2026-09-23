using System.Runtime.InteropServices;

namespace RoboMouse.Core.Input;

/// <summary>
/// Receives raw (unaccelerated, unclamped) mouse motion via the Raw Input API.
/// Raw input is delivered regardless of cursor position and is not affected by
/// low-level hooks blocking the cooked mouse messages, which lets us freeze the
/// local cursor while still reading exact hardware motion.
/// Must be created on a thread that pumps messages (the UI thread).
/// </summary>
public sealed unsafe class RawMouseInput : IDisposable
{
    private readonly MessageWindow _window;
    private bool _registered;
    private bool _disposed;
    private byte* _buffer;
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
        _window = new MessageWindow();
        _window.Message += OnMessage;
        _bufferSize = (uint)sizeof(NativeMethods.RAWINPUT) + 64;
        _buffer = (byte*)NativeMemory.Alloc(_bufferSize);
    }

    /// <summary>
    /// Starts receiving raw mouse input (even while another window is focused).
    /// </summary>
    public void Start()
    {
        if (_registered)
            return;

        var device = new NativeMethods.RAWINPUTDEVICE
        {
            usUsagePage = NativeMethods.HID_USAGE_PAGE_GENERIC,
            usUsage = NativeMethods.HID_USAGE_GENERIC_MOUSE,
            dwFlags = NativeMethods.RIDEV_INPUTSINK,
            hwndTarget = _window.Handle
        };

        if (!NativeMethods.RegisterRawInputDevices(&device, 1, (uint)sizeof(NativeMethods.RAWINPUTDEVICE)))
        {
            throw new InvalidOperationException($"RegisterRawInputDevices failed. Error code: {Marshal.GetLastPInvokeError()}");
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

        var device = new NativeMethods.RAWINPUTDEVICE
        {
            usUsagePage = NativeMethods.HID_USAGE_PAGE_GENERIC,
            usUsage = NativeMethods.HID_USAGE_GENERIC_MOUSE,
            dwFlags = NativeMethods.RIDEV_REMOVE,
            hwndTarget = 0
        };
        NativeMethods.RegisterRawInputDevices(&device, 1, (uint)sizeof(NativeMethods.RAWINPUTDEVICE));
        _registered = false;
    }

    private void OnMessage(uint msg, nint wParam, nint lParam)
    {
        if (msg == NativeMethods.WM_INPUT && _registered)
            HandleRawInput(lParam);
    }

    private void HandleRawInput(nint hRawInput)
    {
        uint size = 0;
        var headerSize = (uint)sizeof(NativeMethods.RAWINPUTHEADER);
        NativeMethods.GetRawInputData(hRawInput, NativeMethods.RID_INPUT, null, &size, headerSize);
        if (size == 0)
            return;

        if (size > _bufferSize)
        {
            NativeMemory.Free(_buffer);
            _bufferSize = size;
            _buffer = (byte*)NativeMemory.Alloc(_bufferSize);
        }

        if (NativeMethods.GetRawInputData(hRawInput, NativeMethods.RID_INPUT, _buffer, &size, headerSize) != size)
            return;

        var raw = (NativeMethods.RAWINPUT*)_buffer;
        if (raw->header.dwType != NativeMethods.RIM_TYPEMOUSE)
            return;

        // hDevice is null for input injected via SendInput. We never want to echo our own injection.
        if (raw->header.hDevice == 0)
            return;

        int dx, dy;
        if ((raw->mouse.usFlags & NativeMethods.MOUSE_MOVE_ABSOLUTE) != 0)
        {
            // Absolute devices report 0..65535 over the primary monitor, or over the whole virtual
            // desktop when they say so; convert to a pixel position, then to a delta.
            var isVirtual = (raw->mouse.usFlags & NativeMethods.MOUSE_VIRTUAL_DESKTOP) != 0;
            var (absX, absY) = AbsoluteToPixel(raw->mouse.lLastX, raw->mouse.lLastY,
                isVirtual ? InputSimulator.GetVirtualScreenBounds() : PrimaryBounds());

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
            dx = raw->mouse.lLastX;
            dy = raw->mouse.lLastY;
        }

        if (dx != 0 || dy != 0)
        {
            Motion?.Invoke(dx, dy);
        }
    }

    private static (int X, int Y, int Width, int Height) PrimaryBounds()
    {
        var (width, height) = InputSimulator.GetPrimaryScreenSize();
        return (0, 0, width, height);
    }

    /// <summary>
    /// Maps an absolute raw-input position (0..65535 on each axis) to a pixel on <paramref name="area"/>,
    /// the virtual desktop when the device set MOUSE_VIRTUAL_DESKTOP, otherwise the primary monitor,
    /// whose top-left is always the origin.
    /// </summary>
    internal static (int X, int Y) AbsoluteToPixel(int lastX, int lastY, (int X, int Y, int Width, int Height) area)
    {
        var x = area.X + (int)(Math.Clamp(lastX, 0, 65535) / 65535.0 * (area.Width - 1));
        var y = area.Y + (int)(Math.Clamp(lastY, 0, 65535) / 65535.0 * (area.Height - 1));
        return (x, y);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        Stop();
        _window.Message -= OnMessage;
        _window.Dispose();

        if (_buffer != null)
        {
            NativeMemory.Free(_buffer);
            _buffer = null;
        }
    }
}
