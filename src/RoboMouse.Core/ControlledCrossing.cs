using RoboMouse.Core.Configuration;
using RoboMouse.Core.Screen;

namespace RoboMouse.Core;

/// <summary>What the controlled machine does after applying one motion delta.</summary>
public enum CrossingAction
{
    /// <summary>Nothing: the cursor is where it should be.</summary>
    None,

    /// <summary>Move the cursor to (<see cref="CrossingStep.X"/>, <see cref="CrossingStep.Y"/>) on this machine.</summary>
    MoveTo,

    /// <summary>
    /// Hand the cursor back to the controller, at (<see cref="CrossingStep.X"/>, <see cref="CrossingStep.Y"/>)
    /// on its layout, or wherever it left when <see cref="CrossingStep.Released"/>.
    /// </summary>
    HandBack
}

public readonly record struct CrossingStep(CrossingAction Action, int X = 0, int Y = 0, bool Released = false)
{
    public static readonly CrossingStep None = new(CrossingAction.None);
}

/// <summary>
/// The controlled side of crossing between screens. Only this machine knows where its cursor really is
/// (Windows applies pointer acceleration here), so after each injected delta it checks the cursor
/// against the controller's layout (<see cref="Network.Protocol.VirtualLayoutMessage"/>): pushed
/// against an edge of the monitor it is on, it goes to whatever the layout has beyond that edge (one
/// of this machine's monitors, which may not be the one Windows has there, or someone else's screen,
/// which hands the cursor back), and a move Windows made onto another monitor is corrected when the
/// layout disagrees. Handing back needs a sustained push, so leaning on an edge briefly is harmless.
/// Pure: the caller reads the cursor and applies the step.
/// </summary>
public sealed class ControlledCrossing
{
    /// <summary>Motion counts pushed into an edge before the cursor goes back to the controller.</summary>
    public const int DefaultHandBackPush = 12;

    // A move this far from the current monitor is not a crossing: someone used this machine's own mouse.
    private const int JumpTolerance = 64;

    // How far Windows may land from where the layout says before the cursor is put right.
    private const int AlongTolerance = 2;

    private string? _monitorId;
    private int _overshoot;

    /// <summary>The monitor the cursor is on, as far as crossings are concerned.</summary>
    public string? CurrentMonitor => _monitorId;

    /// <summary>Starts over on <paramref name="monitorId"/> (the cursor just entered there).</summary>
    public void Reset(string? monitorId)
    {
        _monitorId = monitorId;
        _overshoot = 0;
    }

    /// <summary>Forgets any push so far (the cursor lock changed).</summary>
    public void ResetPush() => _overshoot = 0;

    /// <param name="local">This machine's monitors.</param>
    /// <param name="layout">The controller's layout, or null before it arrived (then any outer edge of the desktop hands back).</param>
    /// <param name="wrap">Whether an edge with nothing beyond leads round to the far side.</param>
    /// <param name="x">Cursor position after the delta was applied.</param>
    /// <param name="y">Cursor position after the delta was applied.</param>
    /// <param name="dx">The delta.</param>
    /// <param name="dy">The delta.</param>
    /// <param name="handBackPush">Push needed before handing back.</param>
    public CrossingStep Step(MonitorLayout local, VirtualDesktop? layout, bool wrap, int x, int y, int dx, int dy, int handBackPush = DefaultHandBackPush)
    {
        var now = local.GetMonitorAt(x, y);
        if (_monitorId == null || local.FindMonitor(_monitorId) is not { } current)
        {
            // Unknown or unplugged: follow Windows.
            _monitorId = now.Id;
            current = now;
        }

        if (layout == null)
            return StepWithoutLayout(local, x, y, dx, dy, handBackPush);

        if (layout.Find(VirtualDesktop.Local, current.Id) is not { } screen)
        {
            // This monitor is not on the controller's layout (it was just plugged in): no crossings from it.
            _monitorId = now.Id;
            _overshoot = 0;
            return CrossingStep.None;
        }

        var b = current.Bounds;
        if (!b.Contains(x, y))
        {
            if (Distance(b, x, y) > Math.Max(Math.Abs(dx), Math.Abs(dy)) + JumpTolerance)
            {
                _monitorId = now.Id;
                _overshoot = 0;
                return CrossingStep.None;
            }

            // Windows moved the cursor onto a neighbouring monitor: check that the layout agrees.
            var crossed = CrossedEdge(b, x, y);
            var push = crossed is ScreenPosition.Left or ScreenPosition.Right ? Math.Abs(dx) : Math.Abs(dy);
            var ex = Math.Clamp(x, b.Left, b.Right - 1);
            var ey = Math.Clamp(y, b.Top, b.Bottom - 1);
            return Cross(local, layout, current, screen, crossed, ex, ey, push, wrap, handBackPush, landed: (now, x, y)).Step;
        }

        // Still on this monitor: pinned against one of its edges and pushed further into it?
        // In a corner both edges are pushed; the one that leads somewhere counts.
        foreach (var (edge, push) in Pushes(b, x, y, dx, dy))
        {
            var (step, hit) = Cross(local, layout, current, screen, edge, x, y, push, wrap, handBackPush, landed: null);
            if (hit)
                return step;
        }
        _overshoot = 0;
        return CrossingStep.None;
    }

    /// <summary>
    /// Leaving <paramref name="current"/> through <paramref name="edge"/> at (<paramref name="ex"/>,
    /// <paramref name="ey"/>). <paramref name="landed"/> is where Windows already put the cursor, if it
    /// moved it onto another monitor. <c>Hit</c> is false when nothing lies beyond the edge.
    /// </summary>
    private (CrossingStep Step, bool Hit) Cross(MonitorLayout local, VirtualDesktop layout, MonitorRect current, LayoutScreen screen,
        ScreenPosition edge, int ex, int ey, int push, bool wrap, int handBackPush, (MonitorRect Monitor, int X, int Y)? landed)
    {
        var (lx, ly) = VirtualDesktop.Map(current.Bounds, screen.Rect, ex, ey);
        var along = edge is ScreenPosition.Left or ScreenPosition.Right ? ly : lx;

        // Put the cursor back on the edge it crossed, unless it is going somewhere.
        var back = landed == null ? CrossingStep.None : new CrossingStep(CrossingAction.MoveTo, ex, ey);

        if (layout.Resolve(screen, edge, along, wrap) is not { } target)
        {
            if (landed != null)
                _overshoot = 0;
            return (back, false);
        }

        if (target.Screen.IsLocal)
        {
            if (local.FindMonitor(target.Screen.MonitorId) is not { } monitor)
            {
                if (landed != null)
                    _overshoot = 0;
                return (back, false);
            }
            _overshoot = 0;
            _monitorId = monitor.Id;
            var (tx, ty) = VirtualDesktop.Map(target.Screen.Rect, monitor.Bounds, target.X, target.Y);
            if (landed is { } l && l.Monitor.Id == monitor.Id)
            {
                // Windows took it to the right monitor; keep how far in it went, fix the position along the edge.
                var vertical = edge is ScreenPosition.Left or ScreenPosition.Right;
                var off = vertical ? Math.Abs(l.Y - ty) : Math.Abs(l.X - tx);
                if (off <= AlongTolerance)
                    return (CrossingStep.None, true);
                return (vertical ? new CrossingStep(CrossingAction.MoveTo, l.X, ty) : new CrossingStep(CrossingAction.MoveTo, tx, l.Y), true);
            }
            return (new CrossingStep(CrossingAction.MoveTo, tx, ty), true);
        }

        // Someone else's screen: hand back once pushed far enough.
        _overshoot += push;
        if (_overshoot < handBackPush)
            return (back, true);
        _overshoot = 0;
        return (new CrossingStep(CrossingAction.HandBack, target.X, target.Y), true);
    }

    /// <summary>Before the layout arrives: any outer edge of the desktop hands back, to where the cursor left the controller.</summary>
    private CrossingStep StepWithoutLayout(MonitorLayout local, int x, int y, int dx, int dy, int handBackPush)
    {
        var b = local.GetScreenAt(x, y);
        foreach (var (edge, push) in Pushes(b, x, y, dx, dy))
        {
            if (!local.IsAtOuterEdge(edge, x, y))
                continue;
            _overshoot += push;
            if (_overshoot < handBackPush)
                return CrossingStep.None;
            _overshoot = 0;
            return new CrossingStep(CrossingAction.HandBack, Released: true);
        }
        _overshoot = 0;
        return CrossingStep.None;
    }

    /// <summary>The edges of <paramref name="b"/> the cursor is on while the delta pushes into them, with how hard.</summary>
    private static IEnumerable<(ScreenPosition Edge, int Push)> Pushes(System.Drawing.Rectangle b, int x, int y, int dx, int dy)
    {
        if (x <= b.Left && dx < 0)
            yield return (ScreenPosition.Left, -dx);
        if (x >= b.Right - 1 && dx > 0)
            yield return (ScreenPosition.Right, dx);
        if (y <= b.Top && dy < 0)
            yield return (ScreenPosition.Top, -dy);
        if (y >= b.Bottom - 1 && dy > 0)
            yield return (ScreenPosition.Bottom, dy);
    }

    /// <summary>The side of <paramref name="b"/> a point outside it went through: the one it is furthest beyond.</summary>
    private static ScreenPosition CrossedEdge(System.Drawing.Rectangle b, int x, int y)
    {
        var best = ScreenPosition.Right;
        var bestBy = int.MinValue;
        void Consider(ScreenPosition edge, int by)
        {
            if (by > bestBy)
                (best, bestBy) = (edge, by);
        }
        Consider(ScreenPosition.Left, b.Left - x);
        Consider(ScreenPosition.Right, x - (b.Right - 1));
        Consider(ScreenPosition.Top, b.Top - y);
        Consider(ScreenPosition.Bottom, y - (b.Bottom - 1));
        return best;
    }

    private static int Distance(System.Drawing.Rectangle r, int x, int y) =>
        Math.Max(Math.Max(r.Left - x, x - (r.Right - 1)), Math.Max(r.Top - y, y - (r.Bottom - 1)));

    /// <summary>The side of a monitor nearest a point given as fractions of its size (where the cursor came in).</summary>
    public static ScreenPosition NearestEdge(float fx, float fy)
    {
        var edges = new[] { (ScreenPosition.Left, fx), (ScreenPosition.Right, 1 - fx), (ScreenPosition.Top, fy), (ScreenPosition.Bottom, 1 - fy) };
        return edges.MinBy(e => e.Item2).Item1;
    }
}
