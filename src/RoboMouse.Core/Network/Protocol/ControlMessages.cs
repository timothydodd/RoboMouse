namespace RoboMouse.Core.Network.Protocol;

/// <summary>
/// Keep-alive ping message.
/// </summary>
public class PingMessage : Message
{
    public override MessageType Type => MessageType.Ping;

    protected override byte[] SerializePayload()
    {
        return Array.Empty<byte>();
    }
}

/// <summary>
/// Keep-alive pong response.
/// </summary>
public class PongMessage : Message
{
    public override MessageType Type => MessageType.Pong;

    protected override byte[] SerializePayload()
    {
        return Array.Empty<byte>();
    }
}

/// <summary>
/// Graceful disconnect message.
/// </summary>
public class DisconnectMessage : Message
{
    public override MessageType Type => MessageType.Disconnect;

    protected override byte[] SerializePayload()
    {
        return Array.Empty<byte>();
    }
}

/// <summary>
/// Error message.
/// </summary>
public class ErrorMessage : Message
{
    public override MessageType Type => MessageType.Error;

    /// <summary>
    /// Error code.
    /// </summary>
    public int ErrorCode { get; set; }

    /// <summary>
    /// Error description.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    protected override byte[] SerializePayload()
    {
        var buffer = new List<byte>();

        var codeBytes = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(codeBytes, ErrorCode);
        buffer.AddRange(codeBytes);

        MessageHelpers.WriteString(buffer, Description);

        return buffer.ToArray();
    }

    public static ErrorMessage DeserializePayload(ReadOnlySpan<byte> payload)
    {
        var offset = 0;
        var errorCode = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload);
        offset += 4;

        return new ErrorMessage
        {
            ErrorCode = errorCode,
            Description = MessageHelpers.ReadString(payload, ref offset)
        };
    }
}
