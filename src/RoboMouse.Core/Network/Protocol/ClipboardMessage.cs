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
/// Clipboard data message.
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

    protected override byte[] SerializePayload()
    {
        var buffer = new List<byte>();

        buffer.Add((byte)ContentType);
        MessageHelpers.WriteString(buffer, FormatHint);

        var lengthBytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, Data.Length);
        buffer.AddRange(lengthBytes);
        buffer.AddRange(Data);

        return buffer.ToArray();
    }

    public static ClipboardMessage DeserializePayload(ReadOnlySpan<byte> payload)
    {
        var offset = 0;
        var contentType = (ClipboardContentType)payload[offset++];
        var formatHint = MessageHelpers.ReadString(payload, ref offset);

        var dataLength = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset));
        offset += 4;

        var data = payload.Slice(offset, dataLength).ToArray();

        return new ClipboardMessage
        {
            ContentType = contentType,
            FormatHint = formatHint,
            Data = data
        };
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
