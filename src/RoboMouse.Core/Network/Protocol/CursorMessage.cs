using System.Buffers.Binary;

namespace RoboMouse.Core.Network.Protocol;

/// <summary>
/// Message sent when cursor enters a peer's screen: which of its monitors, and where on it.
/// </summary>
public class CursorEnterMessage : Message
{
    public override MessageType Type => MessageType.CursorEnter;

    /// <summary>The receiver's name for the monitor the cursor enters (from its <see cref="ScreenInfoMessage"/>).</summary>
    public string MonitorId { get; set; } = string.Empty;

    /// <summary>X position on that monitor (0-1 normalized).</summary>
    public float EntryX { get; set; }

    /// <summary>Y position on that monitor (0-1 normalized).</summary>
    public float EntryY { get; set; }

    /// <summary>
    /// When set, an edge of the receiver's screens with nothing beyond it on the layout leads to the
    /// farthest screen the other way, so screens in a row form a loop.
    /// </summary>
    public bool WrapAround { get; set; }

    /// <summary>
    /// How far the controller's user wants to push past an edge before the cursor comes back (the
    /// controller's <see cref="Configuration.CrossingSettings.PushDistance"/>), in raw counts; 0 for the default.
    /// </summary>
    public ushort HandBackPush { get; set; }

    private const byte FlagWrapAround = 0x01;

    protected override byte[] SerializePayload()
    {
        var buffer = new List<byte>();
        MessageHelpers.WriteString(buffer, MonitorId);
        var number = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(number, EntryX);
        buffer.AddRange(number);
        BinaryPrimitives.WriteSingleLittleEndian(number, EntryY);
        buffer.AddRange(number);
        buffer.Add(WrapAround ? FlagWrapAround : (byte)0);
        var push = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(push, HandBackPush);
        buffer.AddRange(push);
        return buffer.ToArray();
    }

    public static CursorEnterMessage DeserializePayload(ReadOnlySpan<byte> payload)
    {
        var offset = 0;
        var message = new CursorEnterMessage { MonitorId = MessageHelpers.ReadString(payload, ref offset) };
        message.EntryX = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(offset));
        message.EntryY = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(offset + 4));
        message.WrapAround = (payload[offset + 8] & FlagWrapAround) != 0;
        message.HandBackPush = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(offset + 9));
        return message;
    }
}

/// <summary>
/// Message sent when the cursor leaves the controlled machine, either way. From the controlled machine
/// it carries where the cursor went, as a point on the controller's layout (this PC's screen, or
/// another peer's); <see cref="Released"/> means no particular place (control was refused or ended).
/// From the controller it just ends control, and is always <see cref="Released"/>.
/// </summary>
public class CursorLeaveMessage : Message
{
    public override MessageType Type => MessageType.CursorLeave;

    /// <summary>No target point: the controller puts its cursor back where it left.</summary>
    public bool Released { get; set; } = true;

    /// <summary>The point the cursor moves to, in the controller's layout coordinates.</summary>
    public int TargetX { get; set; }
    public int TargetY { get; set; }

    private const byte FlagReleased = 0x01;

    /// <summary>A hand-back to a point on the controller's layout.</summary>
    public static CursorLeaveMessage To(int x, int y) => new() { Released = false, TargetX = x, TargetY = y };

    protected override byte[] SerializePayload()
    {
        var buffer = new byte[9];
        buffer[0] = Released ? FlagReleased : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(1), TargetX);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(5), TargetY);
        return buffer;
    }

    public static CursorLeaveMessage DeserializePayload(ReadOnlySpan<byte> payload) => new()
    {
        Released = (payload[0] & FlagReleased) != 0,
        TargetX = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(1)),
        TargetY = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(5))
    };
}
