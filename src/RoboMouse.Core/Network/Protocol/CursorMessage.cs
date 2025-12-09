using System.Buffers.Binary;
using RoboMouse.Core.Configuration;

namespace RoboMouse.Core.Network.Protocol;

/// <summary>
/// Message sent when cursor enters a peer's screen.
/// </summary>
public class CursorEnterMessage : Message
{
    public override MessageType Type => MessageType.CursorEnter;

    /// <summary>
    /// X position where cursor should appear (0-1 normalized).
    /// </summary>
    public float EntryX { get; set; }

    /// <summary>
    /// Y position where cursor should appear (0-1 normalized).
    /// </summary>
    public float EntryY { get; set; }

    /// <summary>
    /// Which edge the cursor entered from (relative to the receiving peer).
    /// </summary>
    public ScreenPosition EntryEdge { get; set; }

    protected override byte[] SerializePayload()
    {
        var buffer = new byte[9]; // 4 + 4 + 1

        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(0), EntryX);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(4), EntryY);
        buffer[8] = (byte)EntryEdge;

        return buffer;
    }

    public static CursorEnterMessage DeserializePayload(ReadOnlySpan<byte> payload)
    {
        return new CursorEnterMessage
        {
            EntryX = BinaryPrimitives.ReadSingleLittleEndian(payload),
            EntryY = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(4)),
            EntryEdge = (ScreenPosition)payload[8]
        };
    }
}

/// <summary>
/// Message sent when cursor leaves a peer's screen (returning control).
/// </summary>
public class CursorLeaveMessage : Message
{
    public override MessageType Type => MessageType.CursorLeave;

    /// <summary>
    /// X position where cursor left (0-1 normalized).
    /// </summary>
    public float ExitX { get; set; }

    /// <summary>
    /// Y position where cursor left (0-1 normalized).
    /// </summary>
    public float ExitY { get; set; }

    /// <summary>
    /// Which edge the cursor left from.
    /// </summary>
    public ScreenPosition ExitEdge { get; set; }

    protected override byte[] SerializePayload()
    {
        var buffer = new byte[9]; // 4 + 4 + 1

        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(0), ExitX);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(4), ExitY);
        buffer[8] = (byte)ExitEdge;

        return buffer;
    }

    public static CursorLeaveMessage DeserializePayload(ReadOnlySpan<byte> payload)
    {
        return new CursorLeaveMessage
        {
            ExitX = BinaryPrimitives.ReadSingleLittleEndian(payload),
            ExitY = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(4)),
            ExitEdge = (ScreenPosition)payload[8]
        };
    }
}
