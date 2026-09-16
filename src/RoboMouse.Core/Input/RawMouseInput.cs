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
            // Absolute devices report 0..65535 over the (virtual) desktop; convert to a pixel delta.
            var (vx, vy, vw, vh) = InputSimulator.GetVirtualScreenBounds();
            var absX = (int)(raw->mouse.lLastX / 65535.0 * vw) + vx;
            var absY = (int)(raw->mouse.lLastY / 65535.0 * vh) + vy;

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
