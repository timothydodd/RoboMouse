using Avalonia.Controls;
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

    /// <summary>
    /// Moves <paramref name="peer"/> to <paramref name="position"/>. If another peer already occupies that
    /// edge the user is offered a swap. Returns true when the position changed; saves settings on success.
    /// </summary>
    public static async Task<bool> TrySetAsync(AppSettings settings, PeerConfig peer, ScreenPosition position, Window? owner)
    {
        if (peer.Position == position)
            return false;

        var occupant = settings.Peers.FirstOrDefault(p => p != peer && p.Position == position);
        if (occupant != null)
        {
            var swap = await Dialogs.ShowAsync(owner,
                $"{occupant.Name} is already {Describe(position).ToLower()} of this screen.\n\n" +
                $"Swap them so {occupant.Name} moves {Describe(peer.Position).ToLower()}?",
                "Edge already in use", DialogButtons.YesNo, DialogIcon.Question);

            if (swap != DialogResult.Yes)
                return false;

            occupant.Position = peer.Position;
        }

        peer.Position = position;
        settings.Save();
        return true;
    }
}
