using System.Buffers.Binary;
using RoboMouse.Core.Input;

namespace RoboMouse.Core.Network.Protocol;

/// <summary>
/// Mouse input event message. Motion is relative, in raw hardware counts, so the
/// receiving machine applies its own pointer speed and acceleration exactly as it
/// would for a locally attached mouse.
/// </summary>
public class MouseMessage : Message
{
    public override MessageType Type => MessageType.Mouse;

    /// <summary>
    /// Horizontal motion since the previous message (raw counts). Only meaningful for Move.
    /// </summary>
    public int DeltaX { get; set; }

    /// <summary>
    /// Vertical motion since the previous message (raw counts). Only meaningful for Move.
    /// </summary>
    public int DeltaY { get; set; }

    /// <summary>
    /// Type of mouse event.
    /// </summary>
    public MouseEventType EventType { get; set; }

    /// <summary>
    /// Wheel delta for scroll events.
    /// </summary>
    public int WheelDelta { get; set; }

    /// <summary>
    /// True for pure motion messages, which may be merged with adjacent motion messages.
    /// </summary>
    public bool IsMotion => EventType == MouseEventType.Move;

    protected override byte[] SerializePayload()
    {
        var buffer = new byte[13]; // 4 + 4 + 1 + 4

        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0), DeltaX);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), DeltaY);
        buffer[8] = (byte)EventType;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(9), WheelDelta);

        return buffer;
    }

    public static MouseMessage DeserializePayload(ReadOnlySpan<byte> payload)
    {
        return new MouseMessage
        {
            DeltaX = BinaryPrimitives.ReadInt32LittleEndian(payload),
            DeltaY = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4)),
            EventType = (MouseEventType)payload[8],
            WheelDelta = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(9))
        };
    }

    public static MouseMessage Motion(int dx, int dy) => new()
    {
        DeltaX = dx,
        DeltaY = dy,
        EventType = MouseEventType.Move
    };
}
