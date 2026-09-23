namespace RoboMouse.Core.Configuration;

/// <summary>A modifier key that must be held for the cursor to cross to another screen.</summary>
public enum CrossingModifier
{
    None,
    Ctrl,
    Alt,
    Shift,
    Win
}

/// <summary>
/// Guards against the cursor crossing to another screen by accident. Checked by
/// <see cref="CrossingGuard"/> each time the cursor reaches an edge that leads to a peer.
/// </summary>
public class CrossingSettings
{
    /// <summary>
    /// Never cross while a mouse button is held (dragging a window or selecting text): the drag would
    /// be cut off here and jump to the middle of the screen.
    /// </summary>
    public bool BlockWhileButtonHeld { get; set; } = true;

    /// <summary>
    /// Pixels at each end of an edge where pushing does not cross, so a close button or Start button in
    /// the corner can be hit without landing on the other screen. 0 turns it off.
    /// </summary>
    public int CornerDeadZone { get; set; } = 20;

    /// <summary>Cross only when the cursor hits the edge twice within <see cref="DoubleTapWindowMs"/>.</summary>
    public bool DoubleTap { get; set; } = false;

    /// <summary>How quickly the second push has to follow the first for <see cref="DoubleTap"/>.</summary>
    public int DoubleTapWindowMs { get; set; } = 500;

    /// <summary>
    /// Keep pushing against the edge this long before crossing. 0 crosses at once. With
    /// <see cref="DoubleTap"/> on as well, either one is enough.
    /// </summary>
    public int DelayMs { get; set; } = 0;

    /// <summary>A modifier that must be held to cross, or <see cref="CrossingModifier.None"/>.</summary>
    public CrossingModifier RequiredModifier { get; set; } = CrossingModifier.None;

    /// <summary>Never cross while a full-screen program (a game, a video, a presentation) is in front.</summary>
    public bool BlockWhileFullScreen { get; set; } = false;
}
