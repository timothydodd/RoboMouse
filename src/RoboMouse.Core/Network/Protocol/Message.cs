using System.Buffers.Binary;
using System.Text;

namespace RoboMouse.Core.Network.Protocol;

/// <summary>
/// Base class for all protocol messages.
/// </summary>
public abstract class Message
{
    /// <summary>
    /// Protocol version number. Both machines must run the same one. Version 5 added pinned identity
    /// keys to the secure handshake, chunked clipboard transfers and clipboard origin/sequence stamps.
    /// </summary>
    public const byte ProtocolVersion = 5;

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
    /// Deserializes a message from bytes. Returns null for anything it cannot read: a different
    /// version, an unknown type, or a malformed payload. Never throws, so one bad message from a peer
    /// is skipped instead of taking the connection down.
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
        if (payloadLength < 0 || data.Length - offset < payloadLength)
            return null;

        var payload = data.Slice(offset, payloadLength);

        Message? message;
        try
        {
            message = DeserializePayload(type, payload);
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidDataException
                                       or OverflowException or FormatException or DecoderFallbackException)
        {
            // Payload shorter than its fields claim, a negative length, an out-of-range date: skip it.
            return null;
        }

        if (message != null)
        {
            message.Timestamp = timestamp;
        }

        return message;
    }

    private static Message? DeserializePayload(MessageType type, ReadOnlySpan<byte> payload)
    {
        return type switch
        {
            MessageType.Handshake => HandshakeMessage.DeserializePayload(payload),
            MessageType.HandshakeAck => HandshakeAckMessage.DeserializePayload(payload),
            MessageType.Mouse => MouseMessage.DeserializePayload(payload),
            MessageType.Keyboard => KeyboardMessage.DeserializePayload(payload),
            MessageType.CursorEnter => CursorEnterMessage.DeserializePayload(payload),
            MessageType.CursorLeave => CursorLeaveMessage.DeserializePayload(payload),
            MessageType.InputStatus => InputStatusMessage.DeserializePayload(payload),
            MessageType.PowerState => PowerStateMessage.DeserializePayload(payload),
            MessageType.CursorLock => CursorLockMessage.DeserializePayload(payload),
            MessageType.SessionState => SessionStateMessage.DeserializePayload(payload),
            MessageType.LockRequest => new LockRequestMessage(),
            MessageType.Clipboard => ClipboardMessage.DeserializePayload(payload),
            MessageType.FileOffer => FileOfferMessage.DeserializePayload(payload),
            MessageType.FileOfferRevoked => FileOfferRevokedMessage.DeserializePayload(payload),
            MessageType.FileRequest => FileRequestMessage.DeserializePayload(payload),
            MessageType.FileChunk => FileChunkMessage.DeserializePayload(payload),
            MessageType.ClipboardChunk => ClipboardChunkMessage.DeserializePayload(payload),
            MessageType.Ping => new PingMessage(),
            MessageType.Pong => new PongMessage(),
            MessageType.Disconnect => new DisconnectMessage(),
            _ => null
        };
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
        if (payloadLength < 0 || payloadLength > int.MaxValue - 16)
            return -1;
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

    /// <summary>Reads a length-prefixed UTF-8 string. Throws <see cref="InvalidDataException"/> when the length is out of range.</summary>
    public static string ReadString(ReadOnlySpan<byte> data, ref int offset)
    {
        var length = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset));
        offset += 4;
        if (length < 0 || length > data.Length - offset)
            throw new InvalidDataException("String length out of range.");
        var value = Encoding.UTF8.GetString(data.Slice(offset, length));
        offset += length;
        return value;
    }
}
