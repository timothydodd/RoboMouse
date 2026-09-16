using RoboMouse.Core.Network.Protocol;

namespace RoboMouse.App;

/// <summary>Plain-language status strings shared by the tray and the settings window.</summary>
internal static class StatusText
{
    /// <summary>Describes what is happening while controlling <paramref name="peerName"/>.</summary>
    public static string Controlling(string? peerName, InputBlockReason blocked) => blocked switch
    {
        InputBlockReason.SecureDesktop => $"Waiting for UAC on {peerName}",
        InputBlockReason.ElevatedWindow => $"{peerName} has an elevated window in front",
        _ => $"Controlling {peerName}"
    };
}
