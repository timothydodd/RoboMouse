using RoboMouse.Core.Configuration;
using RoboMouse.Core.Input;

namespace RoboMouse.Core.Screen;

/// <summary>
/// Places the cursor on screen edges during transitions between machines.
/// </summary>
public class CursorManager
{
    private readonly ScreenInfo _screenInfo;

    public CursorManager(ScreenInfo screenInfo)
    {
        _screenInfo = screenInfo;
    }

    /// <summary>
    /// Computes the pixel position on the given edge of the virtual screen for a normalized
    /// (0..1) position along that edge.
    /// </summary>
    public (int X, int Y) GetEdgePoint(ScreenPosition edge, float normalizedPosition)
    {
        var bounds = _screenInfo.VirtualBounds;
        normalizedPosition = Math.Clamp(normalizedPosition, 0f, 1f);

        return edge switch
        {
            ScreenPosition.Left => (bounds.Left, bounds.Top + (int)(normalizedPosition * (bounds.Height - 1))),
            ScreenPosition.Right => (bounds.Right - 1, bounds.Top + (int)(normalizedPosition * (bounds.Height - 1))),
            ScreenPosition.Top => (bounds.Left + (int)(normalizedPosition * (bounds.Width - 1)), bounds.Top),
            ScreenPosition.Bottom => (bounds.Left + (int)(normalizedPosition * (bounds.Width - 1)), bounds.Bottom - 1),
            _ => (bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2)
        };
    }

    /// <summary>
    /// Moves the cursor to the given edge at a normalized position along it.
    /// </summary>
    public void PlaceAtEdge(ScreenPosition edge, float normalizedPosition)
    {
        var (x, y) = GetEdgePoint(edge, normalizedPosition);
        InputSimulator.MoveTo(x, y);
    }

    /// <summary>
    /// Returns the normalized (0..1) position of a point along the given edge of the virtual screen.
    /// </summary>
    public float GetNormalizedPositionOnEdge(ScreenPosition edge, int x, int y)
    {
        var bounds = _screenInfo.VirtualBounds;
        var value = edge is ScreenPosition.Left or ScreenPosition.Right
            ? (y - bounds.Top) / (float)Math.Max(1, bounds.Height - 1)
            : (x - bounds.Left) / (float)Math.Max(1, bounds.Width - 1);
        return Math.Clamp(value, 0f, 1f);
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
