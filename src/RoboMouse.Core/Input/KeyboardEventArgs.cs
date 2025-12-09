using System.Windows.Forms;

namespace RoboMouse.Core.Input;

/// <summary>
/// Type of keyboard event.
/// </summary>
public enum KeyboardEventType
{
    KeyDown,
    KeyUp,
    SysKeyDown,
    SysKeyUp
}

/// <summary>
/// Event arguments for keyboard events.
/// </summary>
public class KeyboardEventArgs : EventArgs
{
    /// <summary>
    /// Virtual key code.
    /// </summary>
    public Keys KeyCode { get; }

    /// <summary>
    /// Scan code.
    /// </summary>
    public uint ScanCode { get; }

    /// <summary>
    /// Type of keyboard event.
    /// </summary>
    public KeyboardEventType EventType { get; }

    /// <summary>
    /// Whether this is an extended key (e.g., right Ctrl, right Alt).
    /// </summary>
    public bool IsExtendedKey { get; }

    /// <summary>
    /// Timestamp of the event.
    /// </summary>
    public uint Timestamp { get; }

    /// <summary>
    /// Set to true to prevent the event from being passed to other applications.
    /// </summary>
    public bool Handled { get; set; }

    public KeyboardEventArgs(Keys keyCode, uint scanCode, KeyboardEventType eventType, bool isExtendedKey, uint timestamp = 0)
    {
        KeyCode = keyCode;
        ScanCode = scanCode;
        EventType = eventType;
        IsExtendedKey = isExtendedKey;
        Timestamp = timestamp;
    }
}
