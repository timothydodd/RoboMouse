using ProtocolMessage = RoboMouse.Core.Network.Protocol.Message;

namespace RoboMouse.Core.Network;

/// <summary>
/// The part of a peer connection the control logic needs. <see cref="PeerConnection"/> is the real
/// one; tests supply a fake so the control state can be exercised without sockets.
/// </summary>
public interface IPeerLink
{
    string PeerId { get; }
    string PeerName { get; }
    bool IsConnected { get; }

    /// <summary>True when this machine initiated the connection.</summary>
    bool IsOutbound { get; }

    /// <summary>Queues a message for sending. Never blocks and never throws.</summary>
    void Post(ProtocolMessage message);
}
