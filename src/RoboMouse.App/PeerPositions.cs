using RoboMouse.Core.Configuration;

namespace RoboMouse.App;

/// <summary>
/// The sides of this PC a peer can be added on. Where it is added only decides where its monitors are
/// first placed; the Layout page arranges them after that.
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

    /// <summary>"to the left of this screen", "above this screen".</summary>
    public static string Phrase(ScreenPosition position) => position switch
    {
        ScreenPosition.Left => "to the left of this screen",
        ScreenPosition.Right => "to the right of this screen",
        ScreenPosition.Top => "above this screen",
        _ => "below this screen"
    };

    public static readonly ScreenPosition[] All =
    {
        ScreenPosition.Left, ScreenPosition.Right, ScreenPosition.Top, ScreenPosition.Bottom
    };
}
