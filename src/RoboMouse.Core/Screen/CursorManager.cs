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
    public (int X, int Y) GetEdgePoint(ScreenPosition edge, float normalizedPosition) =>
        _screenInfo.Layout.GetEdgePoint(edge, normalizedPosition);

    /// <summary>
    /// Returns the normalized (0..1) position of a point along the given edge of the virtual screen.
    /// </summary>
    public float GetNormalizedPositionOnEdge(ScreenPosition edge, int x, int y) =>
        _screenInfo.Layout.GetNormalizedPositionOnEdge(edge, x, y);

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
