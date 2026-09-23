using System.Buffers.Binary;

namespace RoboMouse.Contracts;

/// <summary>Direction and kind of a pipe message. App&lt;-&gt;service and service&lt;-&gt;helper share this set.</summary>
public enum PipeOpcode : byte
{
    // Handshake (either direction). Nothing else is honoured before it.
    Hello = 0x01,

    // App -> service, relayed service -> helper: input to apply on whichever desktop is active.
    // The helper only ever injects; nothing is captured on its side (see plans/uac-service.md).
    InjectMotion = 0x12,       // relative mouse move (dx, dy)
    InjectButton = 0x13,       // mouse button/wheel event (MouseEventType + wheel delta)
    InjectKey = 0x14,          // keyboard event (vk, scan, event type, extended)
    MoveTo = 0x19,             // absolute cursor placement (x, y)
    QueryCursor = 0x1A,        // asks for a CursorPosition reply (sequence id)

    // Helper -> service -> app
    CursorPosition = 0x26,     // reply to QueryCursor (x, y, the query's sequence id)

    // Service -> app: status. 0x24 was DesktopChanged (protocol 2): session 0 cannot see the user's
    // desktops, so it only ever said "Winlogon", and the app ignored it.
    HelperReady = 0x25,        // a helper is attached and injection will land
    HelperLost = 0x27,         // the helper went away; inject in-process until HelperReady again

    Error = 0xF0
}

/// <summary>
/// Numeric values of the Core input enums as they cross the pipe. Contracts stays dependency-free, so
/// these mirror <c>RoboMouse.Core.Input.MouseEventType</c> and <c>KeyboardEventType</c> (a Core test
/// pins them to the enums).
/// </summary>
public static class PipeInput
{
    public const int LeftDown = 1, LeftUp = 2, RightDown = 3, RightUp = 4, MiddleDown = 5, MiddleUp = 6;
    public const int Wheel = 7, HWheel = 8;
    public const int XButton1Down = 9, XButton1Up = 10, XButton2Down = 11, XButton2Up = 12;

    public const int KeyDown = 0, KeyUp = 1, SysKeyDown = 2, SysKeyUp = 3;

    /// <summary>The button release matching a press, or 0 when <paramref name="eventType"/> is not a press.</summary>
    public static int ReleaseOf(int eventType) => eventType switch
    {
        LeftDown => LeftUp,
        RightDown => RightUp,
        MiddleDown => MiddleUp,
        XButton1Down => XButton1Up,
        XButton2Down => XButton2Up,
        _ => 0
    };

    /// <summary>The button press matching a release, or 0 when <paramref name="eventType"/> is not a release.</summary>
    public static int PressOf(int eventType) => eventType switch
    {
        LeftUp => LeftDown,
        RightUp => RightDown,
        MiddleUp => MiddleDown,
        XButton1Up => XButton1Down,
        XButton2Up => XButton2Down,
        _ => 0
    };
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

    /// <summary>
    /// True when the payload has exactly the shape its opcode needs and its values are in range. The
    /// service checks this before relaying and the helper before applying, so a bad frame is dropped
    /// instead of throwing out of a reader or reaching SendInput. Unknown opcodes are not well formed.
    /// </summary>
    public bool IsWellFormed => Opcode switch
    {
        PipeOpcode.Hello => Payload.Length == 4,
        PipeOpcode.InjectMotion or PipeOpcode.MoveTo => Payload.Length == 8,
        PipeOpcode.InjectButton => Payload.Length == 8
            && ReadButton().eventType is >= PipeInput.LeftDown and <= PipeInput.XButton2Up,
        PipeOpcode.InjectKey => Payload.Length == 13
            && ReadKey() is (>= 1 and <= 254, _, >= PipeInput.KeyDown and <= PipeInput.SysKeyUp, _)
            && Payload[12] <= 1,
        PipeOpcode.QueryCursor => Payload.Length == 4,
        PipeOpcode.CursorPosition => Payload.Length == 12,
        PipeOpcode.HelperReady or PipeOpcode.HelperLost => Payload.Length == 0,
        PipeOpcode.Error => true,
        _ => false
    };

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

    /// <summary>A cursor query. The reply echoes <paramref name="sequence"/> so a late answer cannot be taken for the next one.</summary>
    public static PipeMessage QueryCursor(uint sequence)
    {
        var p = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(p, sequence);
        return new PipeMessage(PipeOpcode.QueryCursor, p);
    }

    public uint ReadQueryCursor() => BinaryPrimitives.ReadUInt32LittleEndian(Payload);

    public static PipeMessage CursorPosition(int x, int y, uint sequence)
    {
        var p = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(0), x);
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(4), y);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8), sequence);
        return new PipeMessage(PipeOpcode.CursorPosition, p);
    }

    public (int x, int y, uint sequence) ReadCursorPosition() =>
        (BinaryPrimitives.ReadInt32LittleEndian(Payload),
         BinaryPrimitives.ReadInt32LittleEndian(Payload.AsSpan(4)),
         BinaryPrimitives.ReadUInt32LittleEndian(Payload.AsSpan(8)));
}
