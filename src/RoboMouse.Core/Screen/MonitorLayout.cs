using System.Drawing;
using RoboMouse.Core.Configuration;

namespace RoboMouse.Core.Screen;

/// <summary>One monitor of the local desktop, in virtual-screen pixels.</summary>
public readonly record struct MonitorRect(Rectangle Bounds, Rectangle WorkingArea, bool Primary);

/// <summary>
/// An immutable snapshot of the monitor arrangement and the edge geometry on it. The desktop is the
/// union of the monitors, which need not fill its bounding box (different sizes, staggered
/// monitors), so an "outer edge" is a monitor edge with no other monitor beyond it.
/// </summary>
public sealed class MonitorLayout
{
    private static readonly Rectangle Fallback = new(0, 0, 1920, 1080);
    private static readonly ScreenPosition[] Edges = { ScreenPosition.Left, ScreenPosition.Right, ScreenPosition.Top, ScreenPosition.Bottom };

    public IReadOnlyList<MonitorRect> Monitors { get; }

    /// <summary>Bounds of the primary monitor (the one whose top-left is the coordinate origin).</summary>
    public Rectangle PrimaryBounds { get; }

    /// <summary>Bounding box of all monitors.</summary>
    public Rectangle VirtualBounds { get; }

    public MonitorLayout(IEnumerable<MonitorRect> monitors)
    {
        var list = monitors.Where(m => m.Bounds.Width > 0 && m.Bounds.Height > 0).ToList();
        if (list.Count == 0)
            list.Add(new MonitorRect(Fallback, Fallback, true));
        Monitors = list;

        var primary = list.FirstOrDefault(m => m.Primary);
        PrimaryBounds = primary.Bounds.IsEmpty ? list[0].Bounds : primary.Bounds;

        var union = list[0].Bounds;
        foreach (var m in list)
            union = Rectangle.Union(union, m.Bounds);
        VirtualBounds = union;
    }

    /// <summary>The monitor containing the point, or the nearest one.</summary>
    public Rectangle GetScreenAt(int x, int y)
    {
        var best = Monitors[0].Bounds;
        long bestDistance = long.MaxValue;
        foreach (var m in Monitors)
        {
            var d = DistanceSquared(m.Bounds, x, y);
            if (d < bestDistance)
                (best, bestDistance) = (m.Bounds, d);
        }
        return best;
    }

    /// <summary>
    /// The outer edge of the desktop the point is on, if any. The normalized position is measured
    /// along the bounding box so it lines up with the layout the user arranged.
    /// </summary>
    public EdgeInfo? GetEdgeAt(int x, int y, int threshold = 0)
    {
        foreach (var edge in Edges)
        {
            if (IsAtOuterEdge(edge, x, y, threshold))
                return new EdgeInfo(edge, x, y, GetNormalizedPositionOnEdge(edge, x, y));
        }
        return null;
    }

    /// <summary>
    /// True when the point is within <paramref name="threshold"/> pixels of the given edge of its
    /// monitor and no other monitor continues beyond that edge there.
    /// </summary>
    public bool IsAtOuterEdge(ScreenPosition edge, int x, int y, int threshold = 0)
    {
        var screen = GetScreenAt(x, y);
        x = Math.Clamp(x, screen.Left, screen.Right - 1);
        y = Math.Clamp(y, screen.Top, screen.Bottom - 1);

        var (near, beyondX, beyondY) = edge switch
        {
            ScreenPosition.Left => (x <= screen.Left + threshold, screen.Left - 1, y),
            ScreenPosition.Right => (x >= screen.Right - 1 - threshold, screen.Right, y),
            ScreenPosition.Top => (y <= screen.Top + threshold, x, screen.Top - 1),
            ScreenPosition.Bottom => (y >= screen.Bottom - 1 - threshold, x, screen.Bottom),
            _ => (false, x, y)
        };
        return near && !Contains(beyondX, beyondY);
    }

    /// <summary>
    /// The pixel on the given outer edge for a normalized (0..1) position along the bounding box.
    /// Where the bounding box edge is empty space, this is the outermost monitor at that position.
    /// </summary>
    public (int X, int Y) GetEdgePoint(ScreenPosition edge, float normalizedPosition)
    {
        var bounds = VirtualBounds;
        normalizedPosition = Math.Clamp(normalizedPosition, 0f, 1f);
        var vertical = edge is ScreenPosition.Left or ScreenPosition.Right;
        var along = vertical
            ? bounds.Top + (int)(normalizedPosition * (bounds.Height - 1))
            : bounds.Left + (int)(normalizedPosition * (bounds.Width - 1));

        // Monitors spanning that position; if none does (a gap), the one closest to it.
        static int Gap(Rectangle r, bool vertical, int along) => vertical
            ? Math.Max(0, Math.Max(r.Top - along, along - (r.Bottom - 1)))
            : Math.Max(0, Math.Max(r.Left - along, along - (r.Right - 1)));

        var minGap = Monitors.Min(m => Gap(m.Bounds, vertical, along));
        var candidates = Monitors.Select(m => m.Bounds).Where(r => Gap(r, vertical, along) == minGap);

        var target = edge switch
        {
            ScreenPosition.Left => candidates.MinBy(r => r.Left),
            ScreenPosition.Right => candidates.MaxBy(r => r.Right),
            ScreenPosition.Top => candidates.MinBy(r => r.Top),
            ScreenPosition.Bottom => candidates.MaxBy(r => r.Bottom),
            _ => PrimaryBounds
        };

        return edge switch
        {
            ScreenPosition.Left => (target.Left, Math.Clamp(along, target.Top, target.Bottom - 1)),
            ScreenPosition.Right => (target.Right - 1, Math.Clamp(along, target.Top, target.Bottom - 1)),
            ScreenPosition.Top => (Math.Clamp(along, target.Left, target.Right - 1), target.Top),
            ScreenPosition.Bottom => (Math.Clamp(along, target.Left, target.Right - 1), target.Bottom - 1),
            _ => (target.Left + target.Width / 2, target.Top + target.Height / 2)
        };
    }

    /// <summary>The normalized (0..1) position of a point along the given edge of the bounding box.</summary>
    public float GetNormalizedPositionOnEdge(ScreenPosition edge, int x, int y)
    {
        var bounds = VirtualBounds;
        var value = edge is ScreenPosition.Left or ScreenPosition.Right
            ? (y - bounds.Top) / (float)Math.Max(1, bounds.Height - 1)
            : (x - bounds.Left) / (float)Math.Max(1, bounds.Width - 1);
        return Math.Clamp(value, 0f, 1f);
    }

    private bool Contains(int x, int y)
    {
        foreach (var m in Monitors)
        {
            if (m.Bounds.Contains(x, y))
                return true;
        }
        return false;
    }

    private static long DistanceSquared(Rectangle r, int x, int y)
    {
        long dx = Math.Max(0, Math.Max(r.Left - x, x - (r.Right - 1)));
        long dy = Math.Max(0, Math.Max(r.Top - y, y - (r.Bottom - 1)));
        return dx * dx + dy * dy;
    }
}
