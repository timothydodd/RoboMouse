using RoboMouse.Core.Configuration;
using RoboMouse.Core.Network;

namespace RoboMouse.App.Services;

/// <summary>
/// Adding a peer, shared by the Peers page and the tray menu so both follow the same flow: the
/// config is added and saved first, then connected.
/// </summary>
public static class PeerActions
{
    /// <summary>The first edge no configured peer uses, or null when all four are taken.</summary>
    public static ScreenPosition? FirstFreeEdge(AppSettings settings, PeerConfig? except = null) =>
        PeerPositions.All.Cast<ScreenPosition?>().FirstOrDefault(pos => settings.Peers.All(p => p == except || p.Position != pos));

    /// <summary>A new config for a machine found by discovery, on the given edge.</summary>
    public static PeerConfig FromDiscovered(DiscoveredPeer found, ScreenPosition position) => new()
    {
        Id = found.MachineId,
        Name = found.MachineName,
        Address = found.Address.ToString(),
        Port = found.Port,
        Position = position,
        ScreenWidth = found.ScreenWidth,
        ScreenHeight = found.ScreenHeight
    };

    /// <summary>
    /// Connects to a peer that was just added. Returns null when it connected, or when a connect to it
    /// is already under way (the background retry runs every 5 s and got there first); otherwise the
    /// reason it failed.
    /// </summary>
    public static async Task<string?> ConnectNewPeerAsync(IAppBackend backend, PeerConfig peer)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await backend.ConnectToPeerAsync(peer, cts.Token);
            return null;
        }
        catch (Exception ex) when (backend.IsPeerConnected(peer.Id) || IsAlreadyConnecting(ex))
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return "it did not answer in time";
        }
        catch (Exception ex)
        {
            return ex.GetBaseException().Message;
        }
    }

    /// <summary>
    /// The service refuses a second connect to an address that is already being connected to. It has
    /// no dedicated exception type for that, so this recognises its message.
    /// </summary>
    internal static bool IsAlreadyConnecting(Exception ex) =>
        ex is InvalidOperationException && ex.Message.StartsWith("Already connecting", StringComparison.Ordinal);
}
