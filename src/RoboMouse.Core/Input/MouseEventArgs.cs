namespace RoboMouse.Core.Input;

/// <summary>
/// Type of mouse event.
/// </summary>
public enum MouseEventType
{
    Move,
    LeftDown,
    LeftUp,
    RightDown,
    RightUp,
    MiddleDown,
    MiddleUp,
    Wheel,
    HWheel,
    XButton1Down,
    XButton1Up,
    XButton2Down,
    XButton2Up
}

/// <summary>
/// Event arguments for mouse events.
/// </summary>
public class MouseEventArgs : EventArgs
{
    /// <summary>
    /// X coordinate of the mouse cursor.
    /// </summary>
    public int X { get; }

    /// <summary>
    /// Y coordinate of the mouse cursor.
    /// </summary>
    public int Y { get; }

    /// <summary>
    /// Type of mouse event.
    /// </summary>
    public MouseEventType EventType { get; }

    /// <summary>
    /// Wheel delta for scroll events.
    /// </summary>
    public int WheelDelta { get; }

    /// <summary>
    /// Timestamp of the event.
    /// </summary>
    public uint Timestamp { get; }

    /// <summary>
    /// Set to true to prevent the event from being passed to other applications.
    /// </summary>
    public bool Handled { get; set; }

    public MouseEventArgs(int x, int y, MouseEventType eventType, int wheelDelta = 0, uint timestamp = 0)
    {
        X = x;
        Y = y;
        EventType = eventType;
        WheelDelta = wheelDelta;
        Timestamp = timestamp;
    }
}
