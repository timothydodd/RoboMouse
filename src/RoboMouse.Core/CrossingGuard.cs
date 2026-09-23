using RoboMouse.Core.Configuration;

namespace RoboMouse.Core;

/// <summary>What the cursor is doing when it reaches an edge that leads to a peer.</summary>
/// <param name="Edge">The local edge it reached.</param>
/// <param name="NearCorner">Within the corner dead zone of that edge (the caller measures it).</param>
/// <param name="ButtonHeld">A mouse button is down.</param>
/// <param name="HeldModifiers">The modifiers held right now.</param>
/// <param name="FullScreenInFront">A full-screen program is in the foreground.</param>
public readonly record struct CrossingAttempt(
    ScreenPosition Edge,
    bool NearCorner,
    bool ButtonHeld,
    CrossingModifiers HeldModifiers,
    bool FullScreenInFront);

/// <summary>Modifier keys held, as flags.</summary>
[Flags]
public enum CrossingModifiers
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
    Win = 8
}

/// <summary>Whether the cursor may cross now, and if not, why.</summary>
public enum CrossingDecision
{
    Allow,
    ButtonHeld,
    Corner,
    ModifierMissing,
    FullScreen,
    /// <summary>Waiting for the second push (<see cref="CrossingSettings.DoubleTap"/>) or the delay.</summary>
    NotYet
}

/// <summary>
/// Decides whether the cursor may cross to another screen, from <see cref="CrossingSettings"/>. Pure
/// and allocation-free: the mouse hook calls it on every move against an edge, so everything it needs
/// is measured by the caller and passed in, and the time comes from the caller too. Only the hook's
/// thread uses an instance.
/// </summary>
public sealed class CrossingGuard
{
    // The cursor's current stay on an edge: which edge and when it arrived.
    private bool _atEdge;
    private ScreenPosition _edge;
    private long _arrivedAt;

    // The previous arrival, for push-twice.
    private bool _hasTap;
    private ScreenPosition _tapEdge;
    private long _tapAt;

    /// <summary>
    /// Called for each move while the cursor is on an edge that leads to a peer. <paramref name="now"/>
    /// is in milliseconds (Environment.TickCount64). Returns <see cref="CrossingDecision.Allow"/> when it may cross.
    /// </summary>
    public CrossingDecision Evaluate(CrossingSettings settings, in CrossingAttempt attempt, long now)
    {
        // Track arrivals first, so a push that is refused for another reason still counts as a tap.
        var arrived = !_atEdge || _edge != attempt.Edge;
        var secondTap = false;
        if (arrived)
        {
            secondTap = _hasTap && _tapEdge == attempt.Edge && now - _tapAt <= settings.DoubleTapWindowMs;
            _atEdge = true;
            _edge = attempt.Edge;
            _arrivedAt = now;
            _hasTap = true;
            _tapEdge = attempt.Edge;
            _tapAt = now;
        }

        if (settings.BlockWhileButtonHeld && attempt.ButtonHeld)
            return CrossingDecision.ButtonHeld;
        if (attempt.NearCorner)
            return CrossingDecision.Corner;
        if (!HasModifier(settings.RequiredModifier, attempt.HeldModifiers))
            return CrossingDecision.ModifierMissing;
        if (settings.BlockWhileFullScreen && attempt.FullScreenInFront)
            return CrossingDecision.FullScreen;

        var waitForTap = settings.DoubleTap;
        var waitForDelay = settings.DelayMs > 0;
        if (!waitForTap && !waitForDelay)
            return Crossed();
        if (waitForTap && secondTap)
            return Crossed();
        if (waitForDelay && now - _arrivedAt >= settings.DelayMs)
            return Crossed();
        return CrossingDecision.NotYet;
    }

    /// <summary>Called when the cursor moves off every edge (or onto one that leads nowhere).</summary>
    public void LeftEdge() => _atEdge = false;

    /// <summary>Forgets everything, as after crossing.</summary>
    public void Reset()
    {
        _atEdge = false;
        _hasTap = false;
    }

    private CrossingDecision Crossed()
    {
        // The next visit to an edge (on the way back) starts from scratch.
        Reset();
        return CrossingDecision.Allow;
    }

    private static bool HasModifier(CrossingModifier required, CrossingModifiers held) => required switch
    {
        CrossingModifier.None => true,
        CrossingModifier.Ctrl => held.HasFlag(CrossingModifiers.Ctrl),
        CrossingModifier.Alt => held.HasFlag(CrossingModifiers.Alt),
        CrossingModifier.Shift => held.HasFlag(CrossingModifiers.Shift),
        CrossingModifier.Win => held.HasFlag(CrossingModifiers.Win),
        _ => true
    };
}
