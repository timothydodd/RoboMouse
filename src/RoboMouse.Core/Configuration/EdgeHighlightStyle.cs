namespace RoboMouse.Core.Configuration;

/// <summary>How the screen is highlighted when the mouse arrives on it from another machine.</summary>
public enum EdgeHighlightStyle
{
    /// <summary>No visual cue.</summary>
    None,
    /// <summary>A thin border around the whole screen.</summary>
    Border,
    /// <summary>A soft glow along the edge the cursor came in on.</summary>
    Fade
}
