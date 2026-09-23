namespace RoboMouse.Core.Network.Protocol;

/// <summary>
/// Sent by a controller to the machine it controls when the cursor is locked to that screen (or
/// unlocked): while locked, pushing through its edges does not hand the cursor back.
/// </summary>
public class CursorLockMessage : Message
{
    public override MessageType Type => MessageType.CursorLock;

    public bool Locked { get; set; }

    protected override byte[] SerializePayload() => new[] { (byte)(Locked ? 1 : 0) };

    public static CursorLockMessage DeserializePayload(ReadOnlySpan<byte> payload) =>
        new() { Locked = payload.Length > 0 && payload[0] != 0 };
}

/// <summary>
/// Sent by every machine on connect and whenever its session locks or unlocks or its screen saver
/// starts or stops. A machine with <c>LockWithHost</c> / <c>ScreensaverWithHost</c> on follows the
/// peer that last controlled it.
/// </summary>
public class SessionStateMessage : Message
{
    public override MessageType Type => MessageType.SessionState;

    public bool Locked { get; set; }

    public bool ScreensaverRunning { get; set; }

    protected override byte[] SerializePayload() =>
        new[] { (byte)((Locked ? 1 : 0) | (ScreensaverRunning ? 2 : 0)) };

    public static SessionStateMessage DeserializePayload(ReadOnlySpan<byte> payload)
    {
        var flags = payload.Length > 0 ? payload[0] : 0;
        return new() { Locked = (flags & 1) != 0, ScreensaverRunning = (flags & 2) != 0 };
    }
}

/// <summary>
/// "Lock all PCs": the sender locked itself and asks the receiver to lock too. Honoured only from a
/// configured, enabled peer whose identity key is pinned (<see cref="LockPolicy"/>).
/// </summary>
public class LockRequestMessage : Message
{
    public override MessageType Type => MessageType.LockRequest;

    protected override byte[] SerializePayload() => Array.Empty<byte>();
}
