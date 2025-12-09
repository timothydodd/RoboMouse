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

    /// <summary>
    /// Velocity X in pixels per second (for prediction).
    /// </summary>
    public float VelocityX { get; set; }

    /// <summary>
    /// Velocity Y in pixels per second (for prediction).
    /// </summary>
    public float VelocityY { get; set; }

    protected override byte[] SerializePayload()
    {
        var buffer = new byte[21]; // 4 + 4 + 1 + 4 + 4 + 4

        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0), X);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), Y);
        buffer[8] = (byte)EventType;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(9), WheelDelta);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(13), VelocityX);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(17), VelocityY);

        return buffer;
    }

    public static MouseMessage DeserializePayload(ReadOnlySpan<byte> payload)
    {
        var msg = new MouseMessage
        {
            X = BinaryPrimitives.ReadInt32LittleEndian(payload),
            Y = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4)),
            EventType = (MouseEventType)payload[8],
            WheelDelta = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(9))
        };

        // Velocity fields are optional for backwards compatibility
        if (payload.Length >= 21)
        {
            msg.VelocityX = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(13));
            msg.VelocityY = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(17));
        }

        return msg;
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
