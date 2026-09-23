using RoboMouse.Core;
using RoboMouse.Core.Configuration;
using RoboMouse.Core.Network;

namespace RoboMouse.App.Services;

/// <summary>
/// Adding a peer, shared by the Peers page, the tray menu and the pairing wizard so all follow the
/// same flow: the config is added and saved first, then connected.
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
    /// Adds a peer the user chose by hand and saves, then connects when it is enabled. A machine that
    /// was removed before is taken off the blocked list, since adding it again is a clear yes. Returns
    /// null when it connected (or is connecting), otherwise why it did not; the peer is kept either
    /// way and the background retry keeps trying.
    /// </summary>
    public static async Task<string?> AddAndConnectAsync(AppSettings settings, IAppBackend backend, PeerConfig peer)
    {
        if (!settings.Peers.Contains(peer))
            settings.Peers.Add(peer);
        backend.SaveSettings();
        backend.UnblockMachine(peer.Id);

        if (!peer.Enabled)
            return null;

        var error = await ConnectNewPeerAsync(backend, peer);
        if (error == null)
            backend.SaveSettings(); // the connect learned its machine id and screen size
        return error;
    }

    /// <summary>
    /// Connects to a peer that was just added. Returns null when it connected, otherwise the reason it
    /// failed in words for the user. A connect already under way (the background retry got there
    /// first) is shared by the service rather than refused, so it is not a failure here either.
    /// </summary>
    public static async Task<string?> ConnectNewPeerAsync(IAppBackend backend, PeerConfig peer)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await backend.ConnectToPeerAsync(peer, cts.Token);
            return null;
        }
        catch (Exception) when (backend.IsPeerConnected(peer.Id))
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return "it did not answer in time";
        }
        catch (Exception ex)
        {
            return PeerConnectFailure.Classify(ex).Message;
        }
    }
}
