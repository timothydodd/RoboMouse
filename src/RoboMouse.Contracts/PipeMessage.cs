using System.Buffers.Binary;
using System.Text;

namespace RoboMouse.Contracts;

/// <summary>Direction and kind of a pipe message. App&lt;-&gt;service and service&lt;-&gt;helper share this set.</summary>
public enum PipeOpcode : byte
{
    // Handshake (either direction)
    Hello = 0x01,

    // App -> service, relayed service -> helper: input to apply on whichever desktop is active.
    // The helper only ever injects; nothing is captured on its side (see plans/uac-service.md).
    InjectMotion = 0x12,       // relative mouse move (dx, dy)
    InjectButton = 0x13,       // mouse button/wheel event (MouseEventType + wheel delta)
    InjectKey = 0x14,          // keyboard event (vk, scan, event type, extended)
    MoveTo = 0x19,             // absolute cursor placement (x, y)
    QueryCursor = 0x1A,        // asks for a CursorPosition reply

    // Helper -> service -> app
    CursorPosition = 0x26,     // reply to QueryCursor (x, y)

    // Service -> app: status
    DesktopChanged = 0x24,     // the active input desktop changed (isSecure, name)
    HelperReady = 0x25,        // a helper is attached and injection will land
    HelperLost = 0x27,         // the helper went away; inject in-process until HelperReady again

    Error = 0xF0
}

/// <summary>
/// A length-prefixed pipe frame: 4-byte little-endian payload length, one opcode byte, then payload.
/// Hand-serialized so it stays reflection-free (Native AOT) and dependency-free.
/// </summary>
public readonly struct PipeMessage
{
    public PipeOpcode Opcode { get; }
    public byte[] Payload { get; }

    public PipeMessage(PipeOpcode opcode, byte[]? payload = null)
    {
        Opcode = opcode;
        Payload = payload ?? Array.Empty<byte>();
    }

    public byte[] ToFrame()
    {
        var frame = new byte[5 + Payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, Payload.Length + 1);
        frame[4] = (byte)Opcode;
        Payload.CopyTo(frame.AsSpan(5));
        return frame;
    }

    // --- typed payload helpers ------------------------------------------------------------------

    public static PipeMessage Hello() =>
        new(PipeOpcode.Hello, BitConverter.GetBytes(PipeNames.ProtocolVersion));

    public int ReadHelloVersion() => Payload.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(Payload) : 0;

    public static PipeMessage Motion(PipeOpcode opcode, int dx, int dy)
    {
        var p = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(0), dx);
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(4), dy);
        return new PipeMessage(opcode, p);
    }

    public (int dx, int dy) ReadMotion() =>
        (BinaryPrimitives.ReadInt32LittleEndian(Payload), BinaryPrimitives.ReadInt32LittleEndian(Payload.AsSpan(4)));

    public static PipeMessage Button(PipeOpcode opcode, int eventType, int wheelDelta)
    {
        var p = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(0), eventType);
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(4), wheelDelta);
        return new PipeMessage(opcode, p);
    }

    public (int eventType, int wheelDelta) ReadButton() =>
        (BinaryPrimitives.ReadInt32LittleEndian(Payload), BinaryPrimitives.ReadInt32LittleEndian(Payload.AsSpan(4)));

    public static PipeMessage Key(int vk, uint scan, int eventType, bool extended)
    {
        var p = new byte[13];
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(0), vk);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), scan);
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(8), eventType);
        p[12] = extended ? (byte)1 : (byte)0;
        return new PipeMessage(PipeOpcode.InjectKey, p);
    }

    public (int vk, uint scan, int eventType, bool extended) ReadKey() =>
        (BinaryPrimitives.ReadInt32LittleEndian(Payload),
         BinaryPrimitives.ReadUInt32LittleEndian(Payload.AsSpan(4)),
         BinaryPrimitives.ReadInt32LittleEndian(Payload.AsSpan(8)),
         Payload[12] != 0);

    public static PipeMessage DesktopChanged(bool isSecure, string name)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var p = new byte[1 + nameBytes.Length];
        p[0] = isSecure ? (byte)1 : (byte)0;
        nameBytes.CopyTo(p.AsSpan(1));
        return new PipeMessage(PipeOpcode.DesktopChanged, p);
    }

    public (bool isSecure, string name) ReadDesktopChanged() =>
        (Payload.Length > 0 && Payload[0] != 0, Payload.Length > 1 ? Encoding.UTF8.GetString(Payload, 1, Payload.Length - 1) : string.Empty);
}
