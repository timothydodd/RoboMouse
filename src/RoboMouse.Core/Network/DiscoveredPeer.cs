using System.Net;

namespace RoboMouse.Core.Network;

/// <summary>
/// Represents a peer discovered on the network.
/// </summary>
public class DiscoveredPeer
{
    /// <summary>
    /// Unique identifier of the peer.
    /// </summary>
    public string MachineId { get; set; } = string.Empty;

    /// <summary>
    /// Display name of the peer.
    /// </summary>
    public string MachineName { get; set; } = string.Empty;

    /// <summary>
    /// IP address of the peer.
    /// </summary>
    public IPAddress Address { get; set; } = IPAddress.None;

    /// <summary>
    /// Port the peer is listening on.
    /// </summary>
    public int Port { get; set; }

    /// <summary>
    /// When the peer was last seen.
    /// </summary>
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Peer's screen width.
    /// </summary>
    public int ScreenWidth { get; set; }

    /// <summary>
    /// Peer's screen height.
    /// </summary>
    public int ScreenHeight { get; set; }
}
