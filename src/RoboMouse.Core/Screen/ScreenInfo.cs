using System.Windows.Forms;

namespace RoboMouse.Core.Screen;

/// <summary>
/// Information about the local screen configuration.
/// </summary>
public class ScreenInfo
{
    /// <summary>
    /// Primary screen bounds.
    /// </summary>
    public Rectangle PrimaryBounds { get; }

    /// <summary>
    /// Virtual screen bounds (all monitors combined).
    /// </summary>
    public Rectangle VirtualBounds { get; }

    /// <summary>
    /// All screen bounds.
    /// </summary>
    public IReadOnlyList<Rectangle> AllScreenBounds { get; }

    public ScreenInfo()
    {
        PrimaryBounds = System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        VirtualBounds = SystemInformation.VirtualScreen;
        AllScreenBounds = System.Windows.Forms.Screen.AllScreens.Select(s => s.Bounds).ToList();
    }

    /// <summary>
    /// Gets the screen that contains the specified point.
    /// </summary>
    public Rectangle GetScreenAt(int x, int y)
    {
        var screen = System.Windows.Forms.Screen.FromPoint(new Point(x, y));
        return screen.Bounds;
    }

    /// <summary>
    /// Checks if a point is at the edge of the virtual screen.
    /// </summary>
    public EdgeInfo? GetEdgeAt(int x, int y, int threshold = 0)
    {
        // Check if at left edge
        if (x <= VirtualBounds.Left + threshold)
        {
            return new EdgeInfo(Configuration.ScreenPosition.Left, x, y,
                (y - VirtualBounds.Top) / (float)VirtualBounds.Height);
        }

        // Check if at right edge
        if (x >= VirtualBounds.Right - 1 - threshold)
        {
            return new EdgeInfo(Configuration.ScreenPosition.Right, x, y,
                (y - VirtualBounds.Top) / (float)VirtualBounds.Height);
        }

        // Check if at top edge
        if (y <= VirtualBounds.Top + threshold)
        {
            return new EdgeInfo(Configuration.ScreenPosition.Top, x, y,
                (x - VirtualBounds.Left) / (float)VirtualBounds.Width);
        }

        // Check if at bottom edge
        if (y >= VirtualBounds.Bottom - 1 - threshold)
        {
            return new EdgeInfo(Configuration.ScreenPosition.Bottom, x, y,
                (x - VirtualBounds.Left) / (float)VirtualBounds.Width);
        }

        return null;
    }
}

/// <summary>
/// Information about a screen edge detection.
/// </summary>
public class EdgeInfo
{
    /// <summary>
    /// Which edge was detected.
    /// </summary>
    public Configuration.ScreenPosition Edge { get; }

    /// <summary>
    /// X coordinate at the edge.
    /// </summary>
    public int X { get; }

    /// <summary>
    /// Y coordinate at the edge.
    /// </summary>
    public int Y { get; }

    /// <summary>
    /// Normalized position along the edge (0-1).
    /// For Left/Right edges, this is the vertical position.
    /// For Top/Bottom edges, this is the horizontal position.
    /// </summary>
    public float NormalizedPosition { get; }

    public EdgeInfo(Configuration.ScreenPosition edge, int x, int y, float normalizedPosition)
    {
        Edge = edge;
        X = x;
        Y = y;
        NormalizedPosition = Math.Clamp(normalizedPosition, 0f, 1f);
    }
}
