using System.Buffers.Binary;
using System.Text;

namespace RoboMouse.Core.Network.Protocol;

/// <summary>
/// Base class for all protocol messages.
/// </summary>
public abstract class Message
{
    /// <summary>
    /// Protocol version number.
    /// </summary>
    public const byte ProtocolVersion = 1;

    /// <summary>
    /// Magic bytes to identify RoboMouse protocol.
    /// </summary>
    public static readonly byte[] MagicBytes = "MS"u8.ToArray();

    /// <summary>
    /// Type of this message.
    /// </summary>
    public abstract MessageType Type { get; }

    /// <summary>
    /// Timestamp when the message was created (milliseconds since epoch).
    /// </summary>
    public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>
    /// Serializes the message to bytes.
    /// </summary>
    public byte[] Serialize()
    {
        var payload = SerializePayload();
        var totalLength = 2 + 1 + 1 + 4 + 8 + payload.Length; // Magic(2) + Version(1) + Type(1) + Length(4) + Timestamp(8) + Payload

        var buffer = new byte[totalLength];
        var offset = 0;

        // Magic bytes
        buffer[offset++] = MagicBytes[0];
        buffer[offset++] = MagicBytes[1];

        // Version
        buffer[offset++] = ProtocolVersion;

        // Type
        buffer[offset++] = (byte)Type;

        // Payload length
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), payload.Length);
        offset += 4;

        // Timestamp
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset), Timestamp);
        offset += 8;

        // Payload
        payload.CopyTo(buffer.AsSpan(offset));

        return buffer;
    }

    /// <summary>
    /// Deserializes a message from bytes.
    /// </summary>
    public static Message? Deserialize(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16) // Minimum header size
            return null;

        var offset = 0;

        // Verify magic bytes
        if (data[offset++] != MagicBytes[0] || data[offset++] != MagicBytes[1])
            return null;

        // Version
        var version = data[offset++];
        if (version != ProtocolVersion)
            return null;

        // Type
        var type = (MessageType)data[offset++];

        // Payload length
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset));
        offset += 4;

        // Timestamp
        var timestamp = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(offset));
        offset += 8;

        // Verify we have enough data
        if (data.Length < offset + payloadLength)
            return null;

        var payload = data.Slice(offset, payloadLength);

        Message? message = type switch
        {
            MessageType.Handshake => HandshakeMessage.DeserializePayload(payload),
            MessageType.HandshakeAck => HandshakeAckMessage.DeserializePayload(payload),
            MessageType.Mouse => MouseMessage.DeserializePayload(payload),
            MessageType.Keyboard => KeyboardMessage.DeserializePayload(payload),
            MessageType.CursorEnter => CursorEnterMessage.DeserializePayload(payload),
            MessageType.CursorLeave => CursorLeaveMessage.DeserializePayload(payload),
            MessageType.Clipboard => ClipboardMessage.DeserializePayload(payload),
            MessageType.Ping => new PingMessage(),
            MessageType.Pong => new PongMessage(),
            MessageType.Disconnect => new DisconnectMessage(),
            _ => null
        };

        if (message != null)
        {
            message.Timestamp = timestamp;
        }

        return message;
    }

    /// <summary>
    /// Gets the expected message size from a header.
    /// Returns -1 if the header is incomplete or invalid.
    /// </summary>
    public static int GetMessageSize(ReadOnlySpan<byte> headerData)
    {
        if (headerData.Length < 8) // Need at least up to length field
            return -1;

        if (headerData[0] != MagicBytes[0] || headerData[1] != MagicBytes[1])
            return -1;

        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(headerData.Slice(4));
        return 16 + payloadLength; // Header(16) + Payload
    }

    /// <summary>
    /// Serializes the message-specific payload.
    /// </summary>
    protected abstract byte[] SerializePayload();
}

/// <summary>
/// Helper methods for message serialization.
/// </summary>
internal static class MessageHelpers
{
    public static void WriteString(List<byte> buffer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var lengthBytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, bytes.Length);
        buffer.AddRange(lengthBytes);
        buffer.AddRange(bytes);
    }

    public static string ReadString(ReadOnlySpan<byte> data, ref int offset)
    {
        var length = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset));
        offset += 4;
        var value = Encoding.UTF8.GetString(data.Slice(offset, length));
        offset += length;
        return value;
    }
}
