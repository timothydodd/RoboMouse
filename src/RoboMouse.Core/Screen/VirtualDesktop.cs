using System.Drawing;
using RoboMouse.Core.Configuration;

namespace RoboMouse.Core.Screen;

/// <summary>
/// One screen on a <see cref="VirtualDesktop"/>: a monitor of some machine, placed in the layout.
/// <paramref name="Owner"/> says whose it is (<see cref="VirtualDesktop.Local"/> for the machine
/// that built the desktop, otherwise a peer id); <paramref name="MonitorId"/> is that machine's own
/// name for the monitor.
/// </summary>
public readonly record struct LayoutScreen(string Owner, string MonitorId, Rectangle Rect)
{
    public bool IsLocal => Owner == VirtualDesktop.Local;
}

/// <summary>Where a crossing lands: a screen and a point on it, in layout coordinates.</summary>
public readonly record struct LayoutTarget(LayoutScreen Screen, int X, int Y, ScreenPosition EntryEdge);

/// <summary>
/// Every screen one machine's layout knows about, placed in one coordinate space: that machine's own
/// virtual-screen pixels, with its monitors exactly where Windows has them and each peer monitor
/// wherever it was placed. Screens never overlap. Immutable, so a new one is built on any change and
/// swapped in whole; <see cref="Resolve"/> allocates nothing and is safe from the mouse hook.
/// </summary>
public sealed class VirtualDesktop
{
    /// <summary>The owner of the screens belonging to the machine that built the desktop.</summary>
    public const string Local = "";

    /// <summary>A desktop with no screens.</summary>
    public static readonly VirtualDesktop Empty = new(Array.Empty<LayoutScreen>());

    private readonly LayoutScreen[] _screens;

    public IReadOnlyList<LayoutScreen> Screens => _screens;

    public VirtualDesktop(IEnumerable<LayoutScreen> screens)
    {
        _screens = screens.Where(s => s.Rect.Width > 0 && s.Rect.Height > 0).ToArray();
    }

    /// <summary>Bounding box of every screen.</summary>
    public Rectangle Bounds
    {
        get
        {
            if (_screens.Length == 0)
                return Rectangle.Empty;
            var union = _screens[0].Rect;
            foreach (var s in _screens)
                union = Rectangle.Union(union, s.Rect);
            return union;
        }
    }

    /// <summary>The screen containing the point, if any.</summary>
    public LayoutScreen? ScreenAt(int x, int y)
    {
        foreach (var s in _screens)
        {
            if (s.Rect.Contains(x, y))
                return s;
        }
        return null;
    }

    /// <summary>The screen of the given owner and monitor, if it is on the desktop.</summary>
    public LayoutScreen? Find(string owner, string monitorId)
    {
        foreach (var s in _screens)
        {
            if (s.Owner == owner && s.MonitorId == monitorId)
                return s;
        }
        return null;
    }

    /// <summary>
    /// Where the cursor goes when pushed through <paramref name="edge"/> of <paramref name="from"/> at
    /// <paramref name="along"/> (the y coordinate on a left or right edge, the x on a top or bottom
    /// one): the screen touching that edge at that point, entered on its facing side. Screens beyond
    /// the edge but not touching it (a gap) are not reached. With <paramref name="wrap"/>, an edge with
    /// nothing beyond it leads to the farthest screen the other way on the same row or column, entered
    /// from its far side; null if that is <paramref name="from"/> itself.
    /// </summary>
    public LayoutTarget? Resolve(LayoutScreen from, ScreenPosition edge, int along, bool wrap)
    {
        var f = from.Rect;
        var vertical = edge is ScreenPosition.Left or ScreenPosition.Right;
        along = vertical ? Math.Clamp(along, f.Top, f.Bottom - 1) : Math.Clamp(along, f.Left, f.Right - 1);

        foreach (var s in _screens)
        {
            if (s == from || !Spans(s.Rect, vertical, along))
                continue;
            var r = s.Rect;
            var touches = edge switch
            {
                ScreenPosition.Left => r.Right == f.Left,
                ScreenPosition.Right => r.Left == f.Right,
                ScreenPosition.Top => r.Bottom == f.Top,
                _ => r.Top == f.Bottom
            };
            if (touches)
                return Enter(s, CursorManager.GetOppositeEdge(edge), along);
        }

        if (!wrap)
            return null;

        // Nothing beyond: the farthest screen on this row or column in the other direction.
        LayoutScreen? farthest = null;
        foreach (var s in _screens)
        {
            if (!Spans(s.Rect, vertical, along))
                continue;
            if (farthest is not { } best || edge switch
                {
                    ScreenPosition.Left => s.Rect.Right > best.Rect.Right,
                    ScreenPosition.Right => s.Rect.Left < best.Rect.Left,
                    ScreenPosition.Top => s.Rect.Bottom > best.Rect.Bottom,
                    _ => s.Rect.Top < best.Rect.Top
                })
            {
                farthest = s;
            }
        }
        if (farthest is not { } target || target == from)
            return null;
        // Out through the right edge, in through the far screen's left edge, as if the row were a loop.
        return Enter(target, CursorManager.GetOppositeEdge(edge), along);
    }

    private static bool Spans(Rectangle r, bool vertical, int along) =>
        vertical ? along >= r.Top && along < r.Bottom : along >= r.Left && along < r.Right;

    /// <summary>The pixel on <paramref name="edge"/> of a screen at a position along it.</summary>
    private static LayoutTarget Enter(LayoutScreen s, ScreenPosition edge, int along)
    {
        var r = s.Rect;
        return edge switch
        {
            ScreenPosition.Left => new LayoutTarget(s, r.Left, Math.Clamp(along, r.Top, r.Bottom - 1), edge),
            ScreenPosition.Right => new LayoutTarget(s, r.Right - 1, Math.Clamp(along, r.Top, r.Bottom - 1), edge),
            ScreenPosition.Top => new LayoutTarget(s, Math.Clamp(along, r.Left, r.Right - 1), r.Top, edge),
            _ => new LayoutTarget(s, Math.Clamp(along, r.Left, r.Right - 1), r.Bottom - 1, edge)
        };
    }

    #region Mapping between a layout rectangle and real pixels

    /// <summary>A point in <paramref name="rect"/> as fractions (0..1) of its width and height.</summary>
    public static (float X, float Y) Normalize(Rectangle rect, int x, int y) => (
        Math.Clamp((x - rect.Left) / (float)Math.Max(1, rect.Width - 1), 0f, 1f),
        Math.Clamp((y - rect.Top) / (float)Math.Max(1, rect.Height - 1), 0f, 1f));

    /// <summary>The pixel in <paramref name="rect"/> at fractions (0..1) of its width and height.</summary>
    public static (int X, int Y) Denormalize(Rectangle rect, float fx, float fy) => (
        rect.Left + (int)Math.Round(Math.Clamp(fx, 0f, 1f) * (rect.Width - 1)),
        rect.Top + (int)Math.Round(Math.Clamp(fy, 0f, 1f) * (rect.Height - 1)));

    /// <summary>Maps a point proportionally from one rectangle to another (a monitor and its layout screen).</summary>
    public static (int X, int Y) Map(Rectangle from, Rectangle to, int x, int y)
    {
        var (fx, fy) = Normalize(from, x, y);
        return Denormalize(to, fx, fy);
    }

    #endregion

    #region Placing screens

    /// <summary>
    /// The nearest position to <paramref name="desired"/> (same size) that overlaps none of
    /// <paramref name="others"/> and touches at least one along an edge, so the cursor can cross.
    /// Returns <paramref name="desired"/> when it already does, and when there is nothing to touch.
    /// </summary>
    public static Rectangle FindFree(Rectangle desired, IReadOnlyCollection<Rectangle> others)
    {
        if (others.Count == 0 || (!OverlapsAny(desired, others) && TouchesAny(desired, others)))
            return desired;

        Rectangle? best = null;
        long bestDistance = long.MaxValue;
        foreach (var (candidate, _) in Candidates(desired, others, alignThreshold: 0))
        {
            var d = DistanceSquared(candidate.Location, desired.Location);
            if (d < bestDistance && !OverlapsAny(candidate, others))
                (best, bestDistance) = (candidate, d);
        }
        if (best is { } found)
            return found;

        // Everything around is taken: to the right of all of it.
        var bounds = others.Aggregate(Rectangle.Union);
        return new Rectangle(bounds.Right, bounds.Top, desired.Width, desired.Height);
    }

    /// <summary>
    /// Where a screen dropped at <paramref name="dropped"/> settles: against the nearest edge of another
    /// screen, overlapping none, with its sides lined up with that screen's when they are within
    /// <paramref name="alignThreshold"/> units of each other.
    /// </summary>
    public static Rectangle Snap(Rectangle dropped, IReadOnlyCollection<Rectangle> others, int alignThreshold)
    {
        if (others.Count == 0)
            return dropped;

        Rectangle? best = null;
        long bestDistance = long.MaxValue;
        // Scored by where the screen would sit before lining up, so lining up never loses to a spot
        // that is nearer only because it is not lined up; the lined-up version comes first and wins ties.
        foreach (var (candidate, basis) in Candidates(dropped, others, alignThreshold))
        {
            var d = DistanceSquared(basis, dropped.Location);
            if (d < bestDistance && !OverlapsAny(candidate, others))
                (best, bestDistance) = (candidate, d);
        }
        return best ?? FindFree(dropped, others);
    }

    /// <summary>
    /// Positions touching each side of each other screen, as close to <paramref name="near"/> along that
    /// side as still shares at least some of the edge; with an alignment threshold, also the lined-up
    /// versions (ends flush, or centred) when close enough.
    /// </summary>
    private static IEnumerable<(Rectangle Candidate, Point Basis)> Candidates(Rectangle near, IReadOnlyCollection<Rectangle> others, int alignThreshold)
    {
        var w = near.Width;
        var h = near.Height;
        foreach (var o in others)
        {
            // Share at least a quarter of the shorter edge, so the crossing is not a pixel wide.
            var overlapY = Math.Max(1, Math.Min(h, o.Height) / 4);
            var overlapX = Math.Max(1, Math.Min(w, o.Width) / 4);
            var y = Math.Clamp(near.Top, o.Top - h + overlapY, o.Bottom - overlapY);
            var x = Math.Clamp(near.Left, o.Left - w + overlapX, o.Right - overlapX);

            foreach (var ay in Aligned(y, o.Top, o.Bottom, h, alignThreshold))
            {
                yield return (new Rectangle(o.Left - w, ay, w, h), new Point(o.Left - w, y));
                yield return (new Rectangle(o.Right, ay, w, h), new Point(o.Right, y));
            }
            foreach (var ax in Aligned(x, o.Left, o.Right, w, alignThreshold))
            {
                yield return (new Rectangle(ax, o.Top - h, w, h), new Point(x, o.Top - h));
                yield return (new Rectangle(ax, o.Bottom, w, h), new Point(x, o.Bottom));
            }
        }
    }

    /// <summary>The start position itself, snapped to line up with the other's start, end or centre when close.</summary>
    private static IEnumerable<int> Aligned(int start, int otherStart, int otherEnd, int size, int threshold)
    {
        if (threshold <= 0)
        {
            yield return start;
            yield break;
        }
        var snapped = start;
        var bestGap = threshold + 1;
        foreach (var option in new[] { otherStart, otherEnd - size, otherStart + (otherEnd - otherStart - size) / 2 })
        {
            var gap = Math.Abs(option - start);
            if (gap <= threshold && gap < bestGap)
                (snapped, bestGap) = (option, gap);
        }
        yield return snapped;
        if (snapped != start)
            yield return start;
    }

    public static bool OverlapsAny(Rectangle r, IEnumerable<Rectangle> others)
    {
        foreach (var o in others)
        {
            if (r.IntersectsWith(o))
                return true;
        }
        return false;
    }

    /// <summary>True when the two share a stretch of edge (touching corners do not count).</summary>
    public static bool Touches(Rectangle a, Rectangle b)
    {
        var sideBySide = (a.Right == b.Left || b.Right == a.Left) && a.Top < b.Bottom && b.Top < a.Bottom;
        var stacked = (a.Bottom == b.Top || b.Bottom == a.Top) && a.Left < b.Right && b.Left < a.Right;
        return sideBySide || stacked;
    }

    private static bool TouchesAny(Rectangle r, IEnumerable<Rectangle> others) => others.Any(o => Touches(r, o));

    private static long DistanceSquared(Point a, Point b)
    {
        long dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    #endregion
}
