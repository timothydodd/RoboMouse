using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RoboMouse.Core.Input;

/// <summary>
/// Simulates mouse and keyboard input using the Windows SendInput API.
/// </summary>
public static class InputSimulator
{
    private static readonly int InputSize = Marshal.SizeOf<NativeMethods.INPUT>();

    #region Mouse Simulation

    /// <summary>
    /// Moves the mouse cursor to the specified absolute coordinates.
    /// </summary>
    public static void MoveTo(int x, int y)
    {
        NativeMethods.SetCursorPos(x, y);
    }

    /// <summary>
    /// Moves the mouse cursor by the specified relative amount.
    /// </summary>
    public static void MoveBy(int deltaX, int deltaY)
    {
        NativeMethods.GetCursorPos(out var point);
        NativeMethods.SetCursorPos(point.X + deltaX, point.Y + deltaY);
    }

    /// <summary>
    /// Simulates a mouse event.
    /// </summary>
    public static void SimulateMouseEvent(MouseEventType eventType, int x = 0, int y = 0, int wheelDelta = 0)
    {
        var input = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_MOUSE,
            u = new NativeMethods.INPUTUNION
            {
                mi = new NativeMethods.MOUSEINPUT
                {
                    dx = x,
                    dy = y,
                    mouseData = 0,
                    dwFlags = GetMouseFlags(eventType),
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        // Handle wheel delta
        if (eventType == MouseEventType.Wheel || eventType == MouseEventType.HWheel)
        {
            input.u.mi.mouseData = (uint)wheelDelta;
        }
        // Handle X buttons
        else if (eventType is MouseEventType.XButton1Down or MouseEventType.XButton1Up)
        {
            input.u.mi.mouseData = NativeMethods.XBUTTON1;
        }
        else if (eventType is MouseEventType.XButton2Down or MouseEventType.XButton2Up)
        {
            input.u.mi.mouseData = NativeMethods.XBUTTON2;
        }

        SendInputs(input);
    }

    /// <summary>
    /// Simulates a left mouse click.
    /// </summary>
    public static void LeftClick()
    {
        SimulateMouseEvent(MouseEventType.LeftDown);
        SimulateMouseEvent(MouseEventType.LeftUp);
    }

    /// <summary>
    /// Simulates a right mouse click.
    /// </summary>
    public static void RightClick()
    {
        SimulateMouseEvent(MouseEventType.RightDown);
        SimulateMouseEvent(MouseEventType.RightUp);
    }

    /// <summary>
    /// Simulates a middle mouse click.
    /// </summary>
    public static void MiddleClick()
    {
        SimulateMouseEvent(MouseEventType.MiddleDown);
        SimulateMouseEvent(MouseEventType.MiddleUp);
    }

    /// <summary>
    /// Simulates mouse wheel scroll.
    /// </summary>
    /// <param name="delta">Positive for scroll up, negative for scroll down.</param>
    public static void Scroll(int delta)
    {
        SimulateMouseEvent(MouseEventType.Wheel, wheelDelta: delta);
    }

    /// <summary>
    /// Simulates horizontal mouse wheel scroll.
    /// </summary>
    /// <param name="delta">Positive for scroll right, negative for scroll left.</param>
    public static void HorizontalScroll(int delta)
    {
        SimulateMouseEvent(MouseEventType.HWheel, wheelDelta: delta);
    }

    private static uint GetMouseFlags(MouseEventType eventType)
    {
        return eventType switch
        {
            MouseEventType.Move => NativeMethods.MOUSEEVENTF_MOVE,
            MouseEventType.LeftDown => NativeMethods.MOUSEEVENTF_LEFTDOWN,
            MouseEventType.LeftUp => NativeMethods.MOUSEEVENTF_LEFTUP,
            MouseEventType.RightDown => NativeMethods.MOUSEEVENTF_RIGHTDOWN,
            MouseEventType.RightUp => NativeMethods.MOUSEEVENTF_RIGHTUP,
            MouseEventType.MiddleDown => NativeMethods.MOUSEEVENTF_MIDDLEDOWN,
            MouseEventType.MiddleUp => NativeMethods.MOUSEEVENTF_MIDDLEUP,
            MouseEventType.Wheel => NativeMethods.MOUSEEVENTF_WHEEL,
            MouseEventType.HWheel => NativeMethods.MOUSEEVENTF_HWHEEL,
            MouseEventType.XButton1Down or MouseEventType.XButton2Down => NativeMethods.MOUSEEVENTF_XDOWN,
            MouseEventType.XButton1Up or MouseEventType.XButton2Up => NativeMethods.MOUSEEVENTF_XUP,
            _ => 0
        };
    }

    #endregion

    #region Keyboard Simulation

    /// <summary>
    /// Simulates a keyboard event.
    /// </summary>
    public static void SimulateKeyboardEvent(Keys keyCode, uint scanCode, KeyboardEventType eventType, bool isExtended)
    {
        var input = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            u = new NativeMethods.INPUTUNION
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = (ushort)keyCode,
                    wScan = (ushort)scanCode,
                    dwFlags = GetKeyboardFlags(eventType, isExtended),
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        SendInputs(input);
    }

    /// <summary>
    /// Simulates pressing and releasing a key.
    /// </summary>
    public static void KeyPress(Keys keyCode)
    {
        KeyDown(keyCode);
        KeyUp(keyCode);
    }

    /// <summary>
    /// Simulates pressing a key down.
    /// </summary>
    public static void KeyDown(Keys keyCode)
    {
        SimulateKeyboardEvent(keyCode, 0, KeyboardEventType.KeyDown, IsExtendedKey(keyCode));
    }

    /// <summary>
    /// Simulates releasing a key.
    /// </summary>
    public static void KeyUp(Keys keyCode)
    {
        SimulateKeyboardEvent(keyCode, 0, KeyboardEventType.KeyUp, IsExtendedKey(keyCode));
    }

    /// <summary>
    /// Types a string by simulating key presses.
    /// </summary>
    public static void TypeText(string text)
    {
        foreach (var c in text)
        {
            TypeCharacter(c);
        }
    }

    private static void TypeCharacter(char c)
    {
        var inputs = new NativeMethods.INPUT[2];

        // Key down
        inputs[0] = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            u = new NativeMethods.INPUTUNION
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = 0,
                    wScan = c,
                    dwFlags = 0x0004, // KEYEVENTF_UNICODE
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        // Key up
        inputs[1] = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            u = new NativeMethods.INPUTUNION
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = 0,
                    wScan = c,
                    dwFlags = 0x0004 | NativeMethods.KEYEVENTF_KEYUP, // KEYEVENTF_UNICODE | KEYEVENTF_KEYUP
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        NativeMethods.SendInput(2, inputs, InputSize);
    }

    private static uint GetKeyboardFlags(KeyboardEventType eventType, bool isExtended)
    {
        uint flags = 0;

        if (eventType is KeyboardEventType.KeyUp or KeyboardEventType.SysKeyUp)
        {
            flags |= NativeMethods.KEYEVENTF_KEYUP;
        }

        if (isExtended)
        {
            flags |= NativeMethods.KEYEVENTF_EXTENDEDKEY;
        }

        return flags;
    }

    private static bool IsExtendedKey(Keys key)
    {
        return key is Keys.RMenu or Keys.RControlKey or Keys.Insert or Keys.Delete
            or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown
            or Keys.Left or Keys.Right or Keys.Up or Keys.Down
            or Keys.NumLock or Keys.PrintScreen or Keys.Divide;
    }

    #endregion

    #region Cursor Control

    /// <summary>
    /// Gets the current cursor position.
    /// </summary>
    public static (int X, int Y) GetCursorPosition()
    {
        NativeMethods.GetCursorPos(out var point);
        return (point.X, point.Y);
    }

    /// <summary>
    /// Clips the cursor to the specified rectangle.
    /// </summary>
    public static void ClipCursor(int left, int top, int right, int bottom)
    {
        var rect = new NativeMethods.RECT
        {
            Left = left,
            Top = top,
            Right = right,
            Bottom = bottom
        };
        NativeMethods.ClipCursor(ref rect);
    }

    /// <summary>
    /// Releases the cursor clip region.
    /// </summary>
    public static void ReleaseCursorClip()
    {
        NativeMethods.ClipCursor(IntPtr.Zero);
    }

    /// <summary>
    /// Shows or hides the cursor (unreliable due to counter system).
    /// </summary>
    public static void ShowCursor(bool show)
    {
        NativeMethods.ShowCursor(show);
    }

    // All system cursor IDs that need to be replaced for complete hiding
    private static readonly uint[] AllCursorIds = new uint[]
    {
        NativeMethods.OCR_NORMAL,
        NativeMethods.OCR_IBEAM,
        NativeMethods.OCR_WAIT,
        NativeMethods.OCR_CROSS,
        NativeMethods.OCR_UP,
        NativeMethods.OCR_SIZENWSE,
        NativeMethods.OCR_SIZENESW,
        NativeMethods.OCR_SIZEWE,
        NativeMethods.OCR_SIZENS,
        NativeMethods.OCR_SIZEALL,
        NativeMethods.OCR_NO,
        NativeMethods.OCR_HAND,
        NativeMethods.OCR_APPSTARTING
    };

    /// <summary>
    /// Hides the system cursor by replacing all cursor types with a blank cursor.
    /// Call RestoreSystemCursor to restore.
    /// </summary>
    public static void HideSystemCursor()
    {
        // Create a blank cursor using CreateCursor API
        // AND plane: all 0xFF = all transparent (AND with screen = keep screen pixels)
        // XOR plane: all 0x00 = no XOR (don't invert anything)
        var andPlane = new byte[32 * 4]; // 32x32 cursor, 1 bit per pixel = 32 * 32 / 8 = 128 bytes, but needs to be DWORD aligned per row
        var xorPlane = new byte[32 * 4];

        // Fill AND plane with 0xFF (transparent)
        for (int i = 0; i < andPlane.Length; i++)
            andPlane[i] = 0xFF;

        // XOR plane stays 0x00 (no color)

        // Replace all system cursors with blank cursor
        foreach (var cursorId in AllCursorIds)
        {
            var blankCursor = NativeMethods.CreateCursor(IntPtr.Zero, 0, 0, 32, 32, andPlane, xorPlane);
            if (blankCursor != IntPtr.Zero)
            {
                // SetSystemCursor destroys the cursor handle, so we don't need to destroy it
                NativeMethods.SetSystemCursor(blankCursor, cursorId);
            }
        }
    }

    /// <summary>
    /// Restores the system cursor to default.
    /// </summary>
    public static void RestoreSystemCursor()
    {
        // Restore default cursors from system
        NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETCURSORS, 0, IntPtr.Zero, 0);
    }

    #endregion

    #region Screen Info

    /// <summary>
    /// Gets the primary screen dimensions.
    /// </summary>
    public static (int Width, int Height) GetPrimaryScreenSize()
    {
        var width = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
        var height = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
        return (width, height);
    }

    /// <summary>
    /// Gets the virtual screen bounds (all monitors combined).
    /// </summary>
    public static (int X, int Y, int Width, int Height) GetVirtualScreenBounds()
    {
        var x = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        var y = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        var width = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        var height = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        return (x, y, width, height);
    }

    #endregion

    private static void SendInputs(params NativeMethods.INPUT[] inputs)
    {
        var result = NativeMethods.SendInput((uint)inputs.Length, inputs, InputSize);
        if (result != inputs.Length)
        {
            var error = Marshal.GetLastWin32Error();
            System.Diagnostics.Debug.WriteLine($"SendInput failed. Expected {inputs.Length}, sent {result}. Error: {error}");
        }
    }
}
