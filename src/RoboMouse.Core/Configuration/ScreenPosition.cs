namespace RoboMouse.Core.Configuration;

/// <summary>
/// Defines the relative position of a peer screen to the local screen.
/// </summary>
public enum ScreenPosition
{
    /// <summary>Peer screen is to the left of the local screen.</summary>
    Left,

    /// <summary>Peer screen is to the right of the local screen.</summary>
    Right,

    /// <summary>Peer screen is above the local screen.</summary>
    Top,

    /// <summary>Peer screen is below the local screen.</summary>
    Bottom
}
