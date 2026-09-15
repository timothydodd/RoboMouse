using RoboMouse.Core.Configuration;

namespace RoboMouse.App;

/// <summary>
/// Shared logic for changing which edge a configured peer sits on.
/// </summary>
internal static class PeerPositions
{
    public static string Describe(ScreenPosition position) => position switch
    {
        ScreenPosition.Left => "Left",
        ScreenPosition.Right => "Right",
        ScreenPosition.Top => "Above",
        ScreenPosition.Bottom => "Below",
        _ => position.ToString()
    };

    public static readonly ScreenPosition[] All =
    {
        ScreenPosition.Left, ScreenPosition.Right, ScreenPosition.Top, ScreenPosition.Bottom
    };
}
