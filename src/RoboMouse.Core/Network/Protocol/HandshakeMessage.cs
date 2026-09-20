using System.Buffers.Binary;

namespace RoboMouse.Core.Network.Protocol;

/// <summary>
/// What a connection is for.
/// </summary>
public enum ConnectionKind : byte
{
    /// <summary>Normal peer link carrying input, clipboard and control messages.</summary>
    Control = 0,

    /// <summary>Reachability test; the receiver answers pings but does not treat the sender as a peer.</summary>
    Probe = 1,

    /// <summary>Bulk file transfer link, kept separate so large copies never delay input.</summary>
    Transfer = 2
}

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

    /// <summary>What this connection is for.</summary>
    public ConnectionKind Kind { get; set; } = ConnectionKind.Control;

    /// <summary>The port the sender listens on, so the receiver can open further connections back to it.</summary>
    public int ListenPort { get; set; }

    /// <summary>
    /// MAC address (12 hex digits) of the adapter this connection uses, so the receiver can wake the
    /// sender later. Trails the payload and may be absent: older builds neither send nor read it.
    /// </summary>
    public string MacAddress { get; set; } = string.Empty;

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
        buffer.Add((byte)Kind);

        BinaryPrimitives.WriteInt32LittleEndian(intBuffer, ListenPort);
        buffer.AddRange(intBuffer);

        MessageHelpers.WriteString(buffer, MacAddress);

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
        message.SupportsClipboard = payload[offset++] == 1;
        message.Kind = (ConnectionKind)payload[offset++];
        message.ListenPort = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset));
        offset += 4;
        if (offset < payload.Length)
            message.MacAddress = MessageHelpers.ReadString(payload, ref offset);

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

    /// <summary>The port the acknowledging machine listens on.</summary>
    public int ListenPort { get; set; }

    /// <summary>MAC address of the acknowledging machine's adapter; optional, as in the handshake.</summary>
    public string MacAddress { get; set; } = string.Empty;

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

        BinaryPrimitives.WriteInt32LittleEndian(intBuffer, ListenPort);
        buffer.AddRange(intBuffer);

        MessageHelpers.WriteString(buffer, MacAddress);

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
        message.ListenPort = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset));
        offset += 4;
        if (offset < payload.Length)
            message.MacAddress = MessageHelpers.ReadString(payload, ref offset);

        return message;
    }
}
