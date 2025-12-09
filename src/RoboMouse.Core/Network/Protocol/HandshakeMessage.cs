using System.Buffers.Binary;

namespace RoboMouse.Core.Network.Protocol;

/// <summary>
/// Initial handshake message sent when connecting.
/// </summary>
public class HandshakeMessage : Message
{
    public override MessageType Type => MessageType.Handshake;

    /// <summary>
    /// Unique identifier for the sender machine.
    /// </summary>
    public string MachineId { get; set; } = string.Empty;

    /// <summary>
    /// Display name of the sender machine.
    /// </summary>
    public string MachineName { get; set; } = string.Empty;

    /// <summary>
    /// Primary screen width of the sender.
    /// </summary>
    public int ScreenWidth { get; set; }

    /// <summary>
    /// Primary screen height of the sender.
    /// </summary>
    public int ScreenHeight { get; set; }

    /// <summary>
    /// Whether clipboard sharing is supported.
    /// </summary>
    public bool SupportsClipboard { get; set; } = true;

    protected override byte[] SerializePayload()
    {
        var buffer = new List<byte>();

        MessageHelpers.WriteString(buffer, MachineId);
        MessageHelpers.WriteString(buffer, MachineName);

        var intBuffer = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(intBuffer, ScreenWidth);
        buffer.AddRange(intBuffer);

        BinaryPrimitives.WriteInt32LittleEndian(intBuffer, ScreenHeight);
        buffer.AddRange(intBuffer);

        buffer.Add(SupportsClipboard ? (byte)1 : (byte)0);

        return buffer.ToArray();
    }

    public static HandshakeMessage DeserializePayload(ReadOnlySpan<byte> payload)
    {
        var offset = 0;
        var message = new HandshakeMessage
        {
            MachineId = MessageHelpers.ReadString(payload, ref offset),
            MachineName = MessageHelpers.ReadString(payload, ref offset),
            ScreenWidth = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset)),
        };
        offset += 4;
        message.ScreenHeight = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset));
        offset += 4;
        message.SupportsClipboard = payload[offset] == 1;

        return message;
    }
}

/// <summary>
/// Response to a handshake message.
/// </summary>
public class HandshakeAckMessage : Message
{
    public override MessageType Type => MessageType.HandshakeAck;

    /// <summary>
    /// Whether the handshake was accepted.
    /// </summary>
    public bool Accepted { get; set; }

    /// <summary>
    /// Unique identifier for the sender machine.
    /// </summary>
    public string MachineId { get; set; } = string.Empty;

    /// <summary>
    /// Display name of the sender machine.
    /// </summary>
    public string MachineName { get; set; } = string.Empty;

    /// <summary>
    /// Primary screen width of the sender.
    /// </summary>
    public int ScreenWidth { get; set; }

    /// <summary>
    /// Primary screen height of the sender.
    /// </summary>
    public int ScreenHeight { get; set; }

    /// <summary>
    /// Reason if not accepted.
    /// </summary>
    public string? RejectReason { get; set; }

    protected override byte[] SerializePayload()
    {
        var buffer = new List<byte>();

        buffer.Add(Accepted ? (byte)1 : (byte)0);
        MessageHelpers.WriteString(buffer, MachineId);
        MessageHelpers.WriteString(buffer, MachineName);

        var intBuffer = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(intBuffer, ScreenWidth);
        buffer.AddRange(intBuffer);

        BinaryPrimitives.WriteInt32LittleEndian(intBuffer, ScreenHeight);
        buffer.AddRange(intBuffer);

        MessageHelpers.WriteString(buffer, RejectReason ?? string.Empty);

        return buffer.ToArray();
    }

    public static HandshakeAckMessage DeserializePayload(ReadOnlySpan<byte> payload)
    {
        var offset = 0;
        var accepted = payload[offset++] == 1;

        var message = new HandshakeAckMessage
        {
            Accepted = accepted,
            MachineId = MessageHelpers.ReadString(payload, ref offset),
            MachineName = MessageHelpers.ReadString(payload, ref offset),
            ScreenWidth = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset)),
        };
        offset += 4;
        message.ScreenHeight = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset));
        offset += 4;
        message.RejectReason = MessageHelpers.ReadString(payload, ref offset);
        if (string.IsNullOrEmpty(message.RejectReason))
            message.RejectReason = null;

        return message;
    }
}
