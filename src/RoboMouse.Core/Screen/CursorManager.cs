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
    /// Uses warp-back approach instead of clipping for delta tracking.
    /// </summary>
    public void Capture(int x, int y)
    {
        _capturedX = x;
        _capturedY = y;
        _isCaptured = true;

        // Move cursor to capture point (will be warped back here after each move)
        InputSimulator.MoveTo(x, y);
    }

    /// <summary>
    /// Warps the cursor back to the captured position.
    /// Call this after processing mouse movement to reset for next delta.
    /// </summary>
    public void WarpBack()
    {
        if (_isCaptured)
        {
            InputSimulator.MoveTo(_capturedX, _capturedY);
        }
    }

    /// <summary>
    /// Releases the cursor from capture.
    /// </summary>
    public void Release()
    {
        if (!_isCaptured)
            return;

        _isCaptured = false;
        // No need to release clip since we're not using ClipCursor anymore
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
