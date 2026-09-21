namespace RoboMouse.Core.Network.Protocol;

/// <summary>Whether the sending machine is in use, as far as its display and sleep state tell.</summary>
public enum PeerPowerState : byte
{
    /// <summary>Awake, but the display has turned off.</summary>
    DisplayOff = 0,

    /// <summary>Awake with the display on (or dimmed).</summary>
    DisplayOn = 1,

    /// <summary>About to sleep or hibernate.</summary>
    Suspending = 2
}

/// <summary>
/// Sent by every machine when it connects and whenever its display turns on or off or it is about to
/// sleep. A machine with <c>FollowHostPower</c> on mirrors the state of the peer that last controlled it.
/// Builds that do not know the message skip it, so it needs no protocol bump.
/// </summary>
public class PowerStateMessage : Message
{
    public override MessageType Type => MessageType.PowerState;

    public PeerPowerState State { get; set; }

    protected override byte[] SerializePayload() => new[] { (byte)State };

    public static PowerStateMessage DeserializePayload(ReadOnlySpan<byte> payload) =>
        new() { State = payload.Length > 0 ? (PeerPowerState)payload[0] : PeerPowerState.DisplayOn };
}
