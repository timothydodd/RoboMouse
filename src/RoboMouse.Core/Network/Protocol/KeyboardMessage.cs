using System.Buffers.Binary;
using System.Windows.Forms;
using RoboMouse.Core.Input;

namespace RoboMouse.Core.Network.Protocol;

/// <summary>
/// Keyboard input event message.
/// </summary>
public class KeyboardMessage : Message
{
    public override MessageType Type => MessageType.Keyboard;

    /// <summary>
    /// Virtual key code.
    /// </summary>
    public Keys KeyCode { get; set; }

    /// <summary>
    /// Scan code.
    /// </summary>
    public uint ScanCode { get; set; }

    /// <summary>
    /// Type of keyboard event.
    /// </summary>
    public KeyboardEventType EventType { get; set; }

    /// <summary>
    /// Whether this is an extended key.
    /// </summary>
    public bool IsExtendedKey { get; set; }

    protected override byte[] SerializePayload()
    {
        var buffer = new byte[10]; // 4 + 4 + 1 + 1

        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0), (int)KeyCode);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), ScanCode);
        buffer[8] = (byte)EventType;
        buffer[9] = IsExtendedKey ? (byte)1 : (byte)0;

        return buffer;
    }

    public static KeyboardMessage DeserializePayload(ReadOnlySpan<byte> payload)
    {
        return new KeyboardMessage
        {
            KeyCode = (Keys)BinaryPrimitives.ReadInt32LittleEndian(payload),
            ScanCode = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4)),
            EventType = (KeyboardEventType)payload[8],
            IsExtendedKey = payload[9] == 1
        };
    }

    /// <summary>
    /// Creates a KeyboardMessage from a keyboard event.
    /// </summary>
    public static KeyboardMessage FromEvent(KeyboardEventArgs e)
    {
        return new KeyboardMessage
        {
            KeyCode = e.KeyCode,
            ScanCode = e.ScanCode,
            EventType = e.EventType,
            IsExtendedKey = e.IsExtendedKey
        };
    }
}
