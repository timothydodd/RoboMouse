using System.Drawing;
using RoboMouse.Core.Configuration;

namespace RoboMouse.Core.Screen;

/// <summary>
/// Keeps a peer's <see cref="PeerConfig.Monitors"/> in step with the monitors it reports: sizes follow
/// the peer's resolution and scaling, monitors seen for the first time are placed next to the ones
/// already placed (or, for a new peer, as a group on its <see cref="PeerConfig.Position"/> side), and
/// nothing is left overlapping another screen. Pure: works on copies and returns new lists.
/// </summary>
public static class PlacementPlanner
{
    /// <summary>
    /// The size a remote monitor takes on the layout: its pixels scaled by this PC's main display
    /// scaling over the monitor's own, so a 4K laptop at 200 % is as big as a 1080p screen at 100 %.
    /// </summary>
    public static Size LayoutSize(Size remote, int remoteScale, int hostScale)
    {
        remoteScale = remoteScale > 0 ? remoteScale : 100;
        hostScale = hostScale > 0 ? hostScale : 100;
        return new Size(
            Math.Max(1, (int)Math.Round(remote.Width * (double)hostScale / remoteScale)),
            Math.Max(1, (int)Math.Round(remote.Height * (double)hostScale / remoteScale)));
    }

    /// <summary>
    /// The peer's placements after it reported <paramref name="reported"/>. Known monitors keep their
    /// place (moved only if their new size overlaps something), new ones are placed beside their
    /// neighbours as the peer arranges them, and placements of monitors it did not report are kept.
    /// </summary>
    /// <param name="saved">The peer's current placements.</param>
    /// <param name="reported">The monitors the peer has now, in its own pixels.</param>
    /// <param name="hostScale">This PC's main display scaling, in percent.</param>
    /// <param name="obstacles">Every other screen on the layout (this PC's monitors, other peers' monitors).</param>
    /// <param name="localBounds">Bounding box of this PC's monitors, for the first placement.</param>
    /// <param name="side">Which side of this PC a new peer goes on.</param>
    /// <param name="offsetX">Shift along a top or bottom side for the first placement.</param>
    /// <param name="offsetY">Shift along a left or right side for the first placement.</param>
    public static List<MonitorPlacement> Reconcile(
        IReadOnlyList<MonitorPlacement> saved, IReadOnlyList<MonitorRect> reported, int hostScale,
        IReadOnlyCollection<Rectangle> obstacles, Rectangle localBounds, ScreenPosition side, int offsetX, int offsetY)
    {
        var result = new List<MonitorPlacement>();
        var byId = new Dictionary<string, MonitorPlacement>();
        foreach (var p in saved)
        {
            if (byId.ContainsKey(p.Id))
                continue;
            var copy = p.Clone();
            byId[p.Id] = copy;
            result.Add(copy);
        }

        var known = new List<MonitorPlacement>();
        var fresh = new List<MonitorRect>();
        foreach (var monitor in reported)
        {
            if (byId.TryGetValue(monitor.Id, out var p))
            {
                Describe(p, monitor, hostScale);
                known.Add(p);
            }
            else
            {
                fresh.Add(monitor);
            }
        }

        // Known monitors stay put unless they grew into something.
        var settled = new List<Rectangle>(obstacles);
        foreach (var p in known)
        {
            if (VirtualDesktop.OverlapsAny(p.Rect, settled))
                Move(p, VirtualDesktop.FindFree(p.Rect, settled));
            settled.Add(p.Rect);
        }

        if (fresh.Count == 0)
            return result;

        var placed = new List<MonitorPlacement>();
        var anchor = known.FirstOrDefault(p => p.Primary) ?? known.FirstOrDefault();
        if (anchor == null)
        {
            // A peer seen for the first time: its monitors as it arranges them, as one group on its side.
            var first = fresh.FirstOrDefault(m => m.Primary);
            if (string.IsNullOrEmpty(first.Id))
                first = fresh[0];
            var origin = new MonitorPlacement();
            Describe(origin, first, hostScale);
            foreach (var monitor in fresh)
            {
                var p = new MonitorPlacement();
                Describe(p, monitor, hostScale);
                Move(p, Beside(origin, p));
                placed.Add(p);
            }

            var group = placed.Select(p => p.Rect).Aggregate(Rectangle.Union);
            var (dx, dy) = side switch
            {
                ScreenPosition.Left => (localBounds.Left - group.Right, localBounds.Top + offsetY - group.Top),
                ScreenPosition.Top => (localBounds.Left + offsetX - group.Left, localBounds.Top - group.Bottom),
                ScreenPosition.Bottom => (localBounds.Left + offsetX - group.Left, localBounds.Bottom - group.Top),
                _ => (localBounds.Right - group.Left, localBounds.Top + offsetY - group.Top)
            };
            foreach (var p in placed)
                Move(p, new Rectangle(p.X + dx, p.Y + dy, p.Width, p.Height));

            // Settle nearest this PC first, so each monitor finds its neighbour already in place.
            placed.Sort((a, b) => Gap(a.Rect, localBounds).CompareTo(Gap(b.Rect, localBounds)));
        }
        else
        {
            foreach (var monitor in fresh)
            {
                var p = new MonitorPlacement();
                Describe(p, monitor, hostScale);
                Move(p, Beside(anchor, p));
                placed.Add(p);
            }
            placed.Sort((a, b) => Gap(a.RemoteRect, anchor.RemoteRect).CompareTo(Gap(b.RemoteRect, anchor.RemoteRect)));
        }

        foreach (var p in placed)
        {
            Move(p, VirtualDesktop.FindFree(p.Rect, settled));
            settled.Add(p.Rect);
            result.Add(p);
        }
        return result;
    }

    /// <summary>
    /// Moves any placement that overlaps <paramref name="obstacles"/> (this PC's monitors changed, say)
    /// to the nearest free spot. Null when nothing had to move.
    /// </summary>
    public static List<MonitorPlacement>? PushOut(IReadOnlyList<MonitorPlacement> placements, IReadOnlyCollection<Rectangle> obstacles)
    {
        if (!placements.Any(p => VirtualDesktop.OverlapsAny(p.Rect, obstacles)))
            return null;
        var settled = new List<Rectangle>(obstacles);
        var result = new List<MonitorPlacement>();
        // Those that are fine keep their place and block the ones being moved.
        foreach (var p in placements)
        {
            if (!VirtualDesktop.OverlapsAny(p.Rect, obstacles))
                settled.Add(p.Rect);
        }
        foreach (var p in placements)
        {
            var copy = p.Clone();
            if (VirtualDesktop.OverlapsAny(copy.Rect, obstacles))
            {
                Move(copy, VirtualDesktop.FindFree(copy.Rect, settled));
                settled.Add(copy.Rect);
            }
            result.Add(copy);
        }
        return result;
    }

    /// <summary>Copies what the peer reported into a placement, with the layout size that follows from it.</summary>
    private static void Describe(MonitorPlacement p, MonitorRect monitor, int hostScale)
    {
        p.Id = monitor.Id;
        p.RemoteX = monitor.Bounds.X;
        p.RemoteY = monitor.Bounds.Y;
        p.RemoteWidth = monitor.Bounds.Width;
        p.RemoteHeight = monitor.Bounds.Height;
        p.Scale = monitor.Scale;
        p.Primary = monitor.Primary;
        var size = LayoutSize(monitor.Bounds.Size, monitor.Scale, hostScale);
        p.Width = size.Width;
        p.Height = size.Height;
    }

    /// <summary>
    /// Where <paramref name="p"/> goes relative to the placed <paramref name="anchor"/>, keeping how the
    /// peer arranges the two: a monitor to the anchor's right starts where the anchor ends on the
    /// layout, one to its left ends where it starts, and so on, whatever their sizes.
    /// </summary>
    private static Rectangle Beside(MonitorPlacement anchor, MonitorPlacement p)
    {
        var a = anchor.RemoteRect;
        var r = p.RemoteRect;
        var sx = anchor.Width / (double)Math.Max(1, a.Width);
        var sy = anchor.Height / (double)Math.Max(1, a.Height);

        int x = r.Left >= a.Right ? anchor.X + anchor.Width + (int)Math.Round((r.Left - a.Right) * sx)
            : r.Right <= a.Left ? anchor.X - p.Width - (int)Math.Round((a.Left - r.Right) * sx)
            : anchor.X + (int)Math.Round((r.Left - a.Left) * sx);
        int y = r.Top >= a.Bottom ? anchor.Y + anchor.Height + (int)Math.Round((r.Top - a.Bottom) * sy)
            : r.Bottom <= a.Top ? anchor.Y - p.Height - (int)Math.Round((a.Top - r.Bottom) * sy)
            : anchor.Y + (int)Math.Round((r.Top - a.Top) * sy);
        return new Rectangle(x, y, p.Width, p.Height);
    }

    private static void Move(MonitorPlacement p, Rectangle r)
    {
        p.X = r.X;
        p.Y = r.Y;
    }

    /// <summary>Distance between two rectangles (0 when they touch or overlap).</summary>
    private static long Gap(Rectangle a, Rectangle b)
    {
        long dx = Math.Max(0, Math.Max(a.Left - b.Right, b.Left - a.Right));
        long dy = Math.Max(0, Math.Max(a.Top - b.Bottom, b.Top - a.Bottom));
        return dx * dx + dy * dy;
    }
}
