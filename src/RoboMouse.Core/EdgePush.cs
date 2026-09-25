using RoboMouse.Core.Configuration;

namespace RoboMouse.Core;

/// <summary>
/// "Push past the edge" (<see cref="CrossingSettings.PushDistance"/>): once the guards allow a crossing,
/// the mouse must keep moving this far into the edge before the cursor goes over. Windows pins the
/// cursor at the edge, so the push is measured from raw mouse motion. Pure; only the hook's thread uses it.
/// </summary>
public sealed class EdgePush
{
    private int _pushed;

    /// <summary>The edge being pushed against, while armed.</summary>
    public ScreenPosition? Edge { get; private set; }

    public bool IsArmed => Edge != null;

    /// <summary>Starts measuring against <paramref name="edge"/>. Keeps the count when it is already that edge.</summary>
    public void Arm(ScreenPosition edge)
    {
        if (Edge == edge)
            return;
        Edge = edge;
        _pushed = 0;
    }

    public void Cancel()
    {
        Edge = null;
        _pushed = 0;
    }

    /// <summary>
    /// Adds raw motion. Motion into the edge counts up, motion back out counts down (never below 0).
    /// Returns true, and disarms, once the push reaches <paramref name="distance"/>.
    /// </summary>
    public bool Add(int dx, int dy, int distance)
    {
        if (Edge is not { } edge)
            return false;
        var push = edge switch
        {
            ScreenPosition.Left => -dx,
            ScreenPosition.Right => dx,
            ScreenPosition.Top => -dy,
            _ => dy
        };
        _pushed = Math.Max(0, _pushed + push);
        if (_pushed < distance)
            return false;
        Cancel();
        return true;
    }
}
