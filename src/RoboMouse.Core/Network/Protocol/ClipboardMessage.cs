using System.Buffers.Binary;

namespace RoboMouse.Core.Network.Protocol;

/// <summary>
/// Type of clipboard content.
/// </summary>
public enum ClipboardContentType : byte
{
    Text = 0,
    Image = 1,
    Files = 2,
    Html = 3,
    Rtf = 4
}

/// <summary>
/// Clipboard data message. Content over <see cref="ClipboardChunkMessage.ChunkThreshold"/> travels as
/// <see cref="ClipboardChunkMessage"/>s instead and is put back together by the receiver.
/// </summary>
public class ClipboardMessage : Message
{
    public override MessageType Type => MessageType.Clipboard;

    /// <summary>
    /// Type of clipboard content.
    /// </summary>
    public ClipboardContentType ContentType { get; set; }

    /// <summary>
    /// The clipboard data.
    /// </summary>
    public byte[] Data { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Optional format hint (e.g., "text/plain", "image/png").
    /// </summary>
    public string FormatHint { get; set; } = string.Empty;

    /// <summary>
    /// Machine id of the machine the content was copied on. Kept unchanged when a peer relays it, so
    /// every machine can tell a copy it has already applied from a new one (see <see cref="Sequence"/>).
    /// </summary>
    public string OriginId { get; set; } = string.Empty;

    /// <summary>
    /// Increases with every clipboard change announced by <see cref="OriginId"/> (text, image or files),
    /// so a stale or repeated message arriving round a ring is recognised and dropped.
    /// </summary>
    public ulong Sequence { get; set; }

    protected override byte[] SerializePayload()
    {
        var buffer = new List<byte>(Data.Length + 64);

        buffer.Add((byte)ContentType);
        MessageHelpers.WriteString(buffer, FormatHint);
        MessageHelpers.WriteString(buffer, OriginId);

        var number = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(number, Sequence);
        buffer.AddRange(number);

        BinaryPrimitives.WriteInt32LittleEndian(number, Data.Length);
        buffer.AddRange(number.AsSpan(0, 4).ToArray());
        buffer.AddRange(Data);

        return buffer.ToArray();
    }

    public static ClipboardMessage DeserializePayload(ReadOnlySpan<byte> payload)
    {
        var offset = 0;
        var contentType = (ClipboardContentType)payload[offset++];
        var formatHint = MessageHelpers.ReadString(payload, ref offset);
        var originId = MessageHelpers.ReadString(payload, ref offset);
        var sequence = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(offset));
        offset += 8;

        var dataLength = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset));
        offset += 4;

        var data = payload.Slice(offset, dataLength).ToArray();

        return new ClipboardMessage
        {
            ContentType = contentType,
            FormatHint = formatHint,
            OriginId = originId,
            Sequence = sequence,
            Data = data
        };
    }
}

/// <summary>
/// One piece of clipboard content too big for a single message. The pieces of one copy share
/// <see cref="OriginId"/> and <see cref="Sequence"/> and arrive in order, each at the <see cref="Offset"/>
/// where the previous one ended. They go out on the connection's bulk lane, one at a time between
/// input and pings, so a big copy never holds up the cursor or the liveness check.
/// </summary>
public class ClipboardChunkMessage : Message
{
    /// <summary>Content bigger than this is sent in chunks.</summary>
    public const int ChunkThreshold = 256 * 1024;

    /// <summary>Largest piece in one chunk.</summary>
    public const int ChunkSize = 256 * 1024;

    public override MessageType Type => MessageType.ClipboardChunk;

    public ClipboardContentType ContentType { get; set; }
    public string FormatHint { get; set; } = string.Empty;
    public string OriginId { get; set; } = string.Empty;
    public ulong Sequence { get; set; }

    /// <summary>Size of the whole content.</summary>
    public int TotalLength { get; set; }

    /// <summary>Where <see cref="Data"/> starts in the whole content.</summary>
    public int Offset { get; set; }

    public byte[] Data { get; set; } = Array.Empty<byte>();

    /// <summary>Splits a clipboard message into chunks; returns the message itself when it is small enough.</summary>
    public static List<Message> Split(ClipboardMessage message)
    {
        if (message.Data.Length <= ChunkThreshold)
            return new List<Message> { message };

        var chunks = new List<Message>();
        for (var offset = 0; offset < message.Data.Length; offset += ChunkSize)
        {
            var length = Math.Min(ChunkSize, message.Data.Length - offset);
            chunks.Add(new ClipboardChunkMessage
            {
                ContentType = message.ContentType,
                FormatHint = message.FormatHint,
                OriginId = message.OriginId,
                Sequence = message.Sequence,
                TotalLength = message.Data.Length,
                Offset = offset,
                Data = message.Data.AsSpan(offset, length).ToArray()
            });
        }
        return chunks;
    }

    protected override byte[] SerializePayload()
    {
        var buffer = new List<byte>(Data.Length + 64);
        buffer.Add((byte)ContentType);
        MessageHelpers.WriteString(buffer, FormatHint);
        MessageHelpers.WriteString(buffer, OriginId);

        var number = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(number, Sequence);
        buffer.AddRange(number);
        BinaryPrimitives.WriteInt32LittleEndian(number, TotalLength);
        buffer.AddRange(number.AsSpan(0, 4).ToArray());
        BinaryPrimitives.WriteInt32LittleEndian(number, Offset);
        buffer.AddRange(number.AsSpan(0, 4).ToArray());
        BinaryPrimitives.WriteInt32LittleEndian(number, Data.Length);
        buffer.AddRange(number.AsSpan(0, 4).ToArray());
        buffer.AddRange(Data);
        return buffer.ToArray();
    }

    public static ClipboardChunkMessage DeserializePayload(ReadOnlySpan<byte> payload)
    {
        var offset = 0;
        var message = new ClipboardChunkMessage
        {
            ContentType = (ClipboardContentType)payload[offset++],
            FormatHint = MessageHelpers.ReadString(payload, ref offset),
            OriginId = MessageHelpers.ReadString(payload, ref offset)
        };
        message.Sequence = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(offset)); offset += 8;
        message.TotalLength = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset)); offset += 4;
        message.Offset = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset)); offset += 4;
        var length = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset)); offset += 4;
        message.Data = payload.Slice(offset, length).ToArray();
        return message;
    }
}

/// <summary>
/// Request for clipboard data.
/// </summary>
public class ClipboardRequestMessage : Message
{
    public override MessageType Type => MessageType.ClipboardRequest;

    protected override byte[] SerializePayload()
    {
        return Array.Empty<byte>();
    }
}
