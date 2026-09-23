using System.Buffers.Binary;
using System.Text;

namespace RoboMouse.Core.Network.Protocol;

/// <summary>
/// One entry in a file offer: a file or directory, by path relative to the copy root.
/// </summary>
public sealed class FileOfferEntry
{
    /// <summary>Path relative to the copy root, using backslashes (for example "photos\\a.jpg").</summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>File size in bytes; zero for directories.</summary>
    public long Size { get; set; }

    public bool IsDirectory { get; set; }

    public DateTime LastWriteTimeUtc { get; set; }
}

/// <summary>
/// Announces that files were copied on the sender. Carries names and sizes only; bytes are fetched
/// on demand when the receiver pastes.
/// </summary>
public class FileOfferMessage : Message
{
    public override MessageType Type => MessageType.FileOffer;

    public string OfferId { get; set; } = string.Empty;

    /// <summary>Machine id where the files were copied; see <see cref="ClipboardMessage.OriginId"/>.</summary>
    public string OriginId { get; set; } = string.Empty;

    /// <summary>The origin's clipboard sequence number; see <see cref="ClipboardMessage.Sequence"/>.</summary>
    public ulong Sequence { get; set; }

    public List<FileOfferEntry> Entries { get; set; } = new();

    public long TotalSize => Entries.Sum(e => e.Size);

    protected override byte[] SerializePayload()
    {
        var buffer = new List<byte>();
        MessageHelpers.WriteString(buffer, OfferId);
        MessageHelpers.WriteString(buffer, OriginId);

        Span<byte> num = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(num, Sequence);
        buffer.AddRange(num.ToArray());

        BinaryPrimitives.WriteInt32LittleEndian(num, Entries.Count);
        buffer.AddRange(num[..4].ToArray());

        foreach (var entry in Entries)
        {
            MessageHelpers.WriteString(buffer, entry.RelativePath);
            BinaryPrimitives.WriteInt64LittleEndian(num, entry.Size);
            buffer.AddRange(num.ToArray());
            buffer.Add(entry.IsDirectory ? (byte)1 : (byte)0);
            BinaryPrimitives.WriteInt64LittleEndian(num, entry.LastWriteTimeUtc.Ticks);
            buffer.AddRange(num.ToArray());
        }

        return buffer.ToArray();
    }

    public static FileOfferMessage DeserializePayload(ReadOnlySpan<byte> payload)
    {
        var offset = 0;
        var message = new FileOfferMessage { OfferId = MessageHelpers.ReadString(payload, ref offset) };
        message.OriginId = MessageHelpers.ReadString(payload, ref offset);
        message.Sequence = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(offset));
        offset += 8;
        var count = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset));
        offset += 4;
        if (count < 0)
            throw new InvalidDataException("Negative entry count.");

        for (var i = 0; i < count; i++)
        {
            var entry = new FileOfferEntry { RelativePath = MessageHelpers.ReadString(payload, ref offset) };
            entry.Size = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(offset));
            offset += 8;
            entry.IsDirectory = payload[offset++] == 1;
            entry.LastWriteTimeUtc = new DateTime(BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(offset)), DateTimeKind.Utc);
            offset += 8;
            message.Entries.Add(entry);
        }

        return message;
    }
}

/// <summary>
/// The sender's clipboard changed; the named offer can no longer be fetched.
/// </summary>
public class FileOfferRevokedMessage : Message
{
    public override MessageType Type => MessageType.FileOfferRevoked;

    public string OfferId { get; set; } = string.Empty;

    protected override byte[] SerializePayload()
    {
        var buffer = new List<byte>();
        MessageHelpers.WriteString(buffer, OfferId);
        return buffer.ToArray();
    }

    public static FileOfferRevokedMessage DeserializePayload(ReadOnlySpan<byte> payload)
    {
        var offset = 0;
        return new FileOfferRevokedMessage { OfferId = MessageHelpers.ReadString(payload, ref offset) };
    }
}

/// <summary>
/// Asks for up to <see cref="Length"/> bytes of one offered file starting at <see cref="Offset"/>.
/// </summary>
public class FileRequestMessage : Message
{
    public override MessageType Type => MessageType.FileRequest;

    public string OfferId { get; set; } = string.Empty;
    public int EntryIndex { get; set; }
    public long Offset { get; set; }
    public int Length { get; set; }

    protected override byte[] SerializePayload()
    {
        var id = Encoding.UTF8.GetBytes(OfferId);
        var buffer = new byte[4 + id.Length + 4 + 8 + 4];
        var o = 0;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(o), id.Length); o += 4;
        id.CopyTo(buffer, o); o += id.Length;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(o), EntryIndex); o += 4;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(o), Offset); o += 8;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(o), Length);
        return buffer;
    }

    public static FileRequestMessage DeserializePayload(ReadOnlySpan<byte> payload)
    {
        var o = 0;
        var message = new FileRequestMessage { OfferId = MessageHelpers.ReadString(payload, ref o) };
        message.EntryIndex = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(o)); o += 4;
        message.Offset = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(o)); o += 8;
        message.Length = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(o));
        return message;
    }
}

/// <summary>
/// Bytes answering a <see cref="FileRequestMessage"/>. Fewer bytes than requested means end of file.
/// A non-empty <see cref="Error"/> means the request could not be served.
/// </summary>
public class FileChunkMessage : Message
{
    public override MessageType Type => MessageType.FileChunk;

    public string OfferId { get; set; } = string.Empty;
    public int EntryIndex { get; set; }
    public long Offset { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public string Error { get; set; } = string.Empty;

    protected override byte[] SerializePayload()
    {
        var id = Encoding.UTF8.GetBytes(OfferId);
        var error = Encoding.UTF8.GetBytes(Error);
        var buffer = new byte[4 + id.Length + 4 + 8 + 4 + error.Length + 4 + Data.Length];
        var o = 0;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(o), id.Length); o += 4;
        id.CopyTo(buffer, o); o += id.Length;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(o), EntryIndex); o += 4;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(o), Offset); o += 8;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(o), error.Length); o += 4;
        error.CopyTo(buffer, o); o += error.Length;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(o), Data.Length); o += 4;
        Data.CopyTo(buffer, o);
        return buffer;
    }

    public static FileChunkMessage DeserializePayload(ReadOnlySpan<byte> payload)
    {
        var o = 0;
        var message = new FileChunkMessage { OfferId = MessageHelpers.ReadString(payload, ref o) };
        message.EntryIndex = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(o)); o += 4;
        message.Offset = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(o)); o += 8;
        message.Error = MessageHelpers.ReadString(payload, ref o);
        var length = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(o)); o += 4;
        message.Data = payload.Slice(o, length).ToArray();
        return message;
    }
}
