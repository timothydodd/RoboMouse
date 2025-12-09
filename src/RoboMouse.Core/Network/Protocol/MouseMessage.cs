using System.Buffers.Binary;
using RoboMouse.Core.Input;
using InputMouseEventArgs = RoboMouse.Core.Input.MouseEventArgs;

namespace RoboMouse.Core.Network.Protocol;

/// <summary>
/// Mouse input event message.
/// </summary>
public class MouseMessage : Message
{
    public override MessageType Type => MessageType.Mouse;

    /// <summary>
    /// X coordinate (relative to sender's screen).
    /// </summary>
    public int X { get; set; }

    /// <summary>
    /// Y coordinate (relative to sender's screen).
    /// </summary>
    public int Y { get; set; }

    /// <summary>
    /// Type of mouse event.
    /// </summary>
    public MouseEventType EventType { get; set; }

    /// <summary>
    /// Wheel delta for scroll events.
    /// </summary>
    public int WheelDelta { get; set; }

    protected override byte[] SerializePayload()
    {
        var buffer = new byte[13]; // 4 + 4 + 1 + 4

        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0), X);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), Y);
        buffer[8] = (byte)EventType;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(9), WheelDelta);

        return buffer;
    }

    public static MouseMessage DeserializePayload(ReadOnlySpan<byte> payload)
    {
        return new MouseMessage
        {
            X = BinaryPrimitives.ReadInt32LittleEndian(payload),
            Y = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4)),
            EventType = (MouseEventType)payload[8],
            WheelDelta = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(9))
        };
    }

    /// <summary>
    /// Creates a MouseMessage from a mouse event.
    /// </summary>
    public static MouseMessage FromEvent(InputMouseEventArgs e)
    {
        return new MouseMessage
        {
            X = e.X,
            Y = e.Y,
            EventType = e.EventType,
            WheelDelta = e.WheelDelta
        };
    }
}
