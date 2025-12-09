using RoboMouse.Core.Configuration;
using RoboMouse.Core.Input;

namespace RoboMouse.Core.Screen;

/// <summary>
/// Manages cursor capture and transition between screens.
/// </summary>
public class CursorManager
{
    private readonly ScreenInfo _screenInfo;
    private bool _isCaptured;
    private int _capturedX;
    private int _capturedY;

    /// <summary>
    /// Whether the cursor is currently captured (controlling remote).
    /// </summary>
    public bool IsCaptured => _isCaptured;

    /// <summary>
    /// The last captured position.
    /// </summary>
    public (int X, int Y) CapturedPosition => (_capturedX, _capturedY);

    public CursorManager(ScreenInfo screenInfo)
    {
        _screenInfo = screenInfo;
    }

    /// <summary>
    /// Captures the cursor at its current position.
    /// </summary>
    public void Capture()
    {
        if (_isCaptured)
            return;

        var (x, y) = InputSimulator.GetCursorPosition();
        Capture(x, y);
    }

    /// <summary>
    /// Captures the cursor at the specified position.
    /// </summary>
    public void Capture(int x, int y)
    {
        if (_isCaptured)
            return;

        _capturedX = x;
        _capturedY = y;
        _isCaptured = true;

        // Clip cursor to a 1x1 pixel area to effectively lock it
        InputSimulator.ClipCursor(x, y, x + 1, y + 1);
    }

    /// <summary>
    /// Releases the cursor from capture.
    /// </summary>
    public void Release()
    {
        if (!_isCaptured)
            return;

        _isCaptured = false;
        InputSimulator.ReleaseCursorClip();
    }

    /// <summary>
    /// Releases the cursor and moves it to a position based on normalized coordinates.
    /// </summary>
    public void ReleaseAt(ScreenPosition entryEdge, float normalizedPosition)
    {
        Release();

        var bounds = _screenInfo.VirtualBounds;
        int x, y;

        switch (entryEdge)
        {
            case ScreenPosition.Left:
                x = bounds.Left;
                y = bounds.Top + (int)(normalizedPosition * bounds.Height);
                break;
            case ScreenPosition.Right:
                x = bounds.Right - 1;
                y = bounds.Top + (int)(normalizedPosition * bounds.Height);
                break;
            case ScreenPosition.Top:
                x = bounds.Left + (int)(normalizedPosition * bounds.Width);
                y = bounds.Top;
                break;
            case ScreenPosition.Bottom:
                x = bounds.Left + (int)(normalizedPosition * bounds.Width);
                y = bounds.Bottom - 1;
                break;
            default:
                return;
        }

        InputSimulator.MoveTo(x, y);
    }

    /// <summary>
    /// Gets the opposite edge (for return transitions).
    /// </summary>
    public static ScreenPosition GetOppositeEdge(ScreenPosition edge)
    {
        return edge switch
        {
            ScreenPosition.Left => ScreenPosition.Right,
            ScreenPosition.Right => ScreenPosition.Left,
            ScreenPosition.Top => ScreenPosition.Bottom,
            ScreenPosition.Bottom => ScreenPosition.Top,
            _ => edge
        };
    }
}
