using System.Buffers.Binary;
using System.Drawing;
using RoboMouse.Core.Screen;

namespace RoboMouse.Core.Network.Protocol;

/// <summary>
/// The sender's monitors, sent after connecting and whenever they change (a monitor plugged in or
/// removed, a resolution, scaling or main display change), so the peer can place each one on its
/// layout. Rectangles are in the sender's own virtual-screen pixels.
/// </summary>
public class ScreenInfoMessage : Message
{
    public override MessageType Type => MessageType.ScreenInfo;

    /// <summary>Most monitors read from one message; more is not a real desk.</summary>
    public const int MaxMonitors = 32;

    public List<MonitorRect> Monitors { get; set; } = new();

    public static ScreenInfoMessage From(MonitorLayout layout) => new() { Monitors = layout.Monitors.ToList() };

    protected override byte[] SerializePayload()
    {
        var buffer = new List<byte>();
        var monitors = Monitors.Take(MaxMonitors).ToList();
        buffer.Add((byte)monitors.Count);
        foreach (var m in monitors)
        {
            MessageHelpers.WriteString(buffer, m.Id);
            LayoutWire.WriteRect(buffer, m.Bounds);
            LayoutWire.WriteInt(buffer, m.Scale);
            buffer.Add(m.Primary ? (byte)1 : (byte)0);
        }
        return buffer.ToArray();
    }

    public static ScreenInfoMessage DeserializePayload(ReadOnlySpan<byte> payload)
    {
        var offset = 0;
        var count = payload[offset++];
        if (count > MaxMonitors)
            throw new InvalidDataException("Too many monitors.");
        var message = new ScreenInfoMessage();
        for (var i = 0; i < count; i++)
        {
            var id = MessageHelpers.ReadString(payload, ref offset);
            var bounds = LayoutWire.ReadRect(payload, ref offset);
            var scale = LayoutWire.ReadInt(payload, ref offset);
            var primary = payload[offset++] == 1;
            if (bounds.Width <= 0 || bounds.Height <= 0 || scale is < 25 or > 1000)
                throw new InvalidDataException("Monitor out of range.");
            message.Monitors.Add(new MonitorRect(bounds, bounds, primary, id, scale));
        }
        return message;
    }
}

/// <summary>
/// The controller's layout as the receiver needs it to decide crossings itself: the receiver's own
/// monitors (by id) and every other screen, all in the controller's layout coordinates. Sent before
/// the controller can enter the receiver and again whenever its layout changes.
/// </summary>
public class VirtualLayoutMessage : Message
{
    public override MessageType Type => MessageType.VirtualLayout;

    /// <summary>Most screens read from one message.</summary>
    public const int MaxScreens = 128;

    /// <summary>
    /// The screens. An entry with a <see cref="LayoutEntry.MonitorId"/> is one of the receiver's
    /// monitors; one without is someone else's screen (the controller's own, or another peer's).
    /// </summary>
    public List<LayoutEntry> Screens { get; set; } = new();

    public readonly record struct LayoutEntry(string? MonitorId, Rectangle Rect)
    {
        public bool IsYours => MonitorId != null;
    }

    protected override byte[] SerializePayload()
    {
        var buffer = new List<byte>();
        var screens = Screens.Take(MaxScreens).ToList();
        buffer.Add((byte)screens.Count);
        foreach (var s in screens)
        {
            buffer.Add(s.IsYours ? (byte)1 : (byte)0);
            if (s.IsYours)
                MessageHelpers.WriteString(buffer, s.MonitorId!);
            LayoutWire.WriteRect(buffer, s.Rect);
        }
        return buffer.ToArray();
    }

    public static VirtualLayoutMessage DeserializePayload(ReadOnlySpan<byte> payload)
    {
        var offset = 0;
        var count = payload[offset++];
        if (count > MaxScreens)
            throw new InvalidDataException("Too many screens.");
        var message = new VirtualLayoutMessage();
        for (var i = 0; i < count; i++)
        {
            var yours = payload[offset++] == 1;
            var id = yours ? MessageHelpers.ReadString(payload, ref offset) : null;
            var rect = LayoutWire.ReadRect(payload, ref offset);
            if (rect.Width <= 0 || rect.Height <= 0)
                throw new InvalidDataException("Screen out of range.");
            message.Screens.Add(new LayoutEntry(id, rect));
        }
        return message;
    }

    /// <summary>
    /// The layout as a desktop for the receiver: its own monitors owned by <see cref="VirtualDesktop.Local"/>,
    /// every other screen by <see cref="Foreign"/>.
    /// </summary>
    public VirtualDesktop ToDesktop() => new(Screens.Select(s =>
        new LayoutScreen(s.IsYours ? VirtualDesktop.Local : Foreign, s.MonitorId ?? string.Empty, s.Rect)));

    /// <summary>Owner of the screens that are not the receiver's on <see cref="ToDesktop"/>.</summary>
    public const string Foreign = "*";
}

internal static class LayoutWire
{
    public static void WriteInt(List<byte> buffer, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        buffer.AddRange(bytes.ToArray());
    }

    public static int ReadInt(ReadOnlySpan<byte> data, ref int offset)
    {
        var value = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset));
        offset += 4;
        return value;
    }

    public static void WriteRect(List<byte> buffer, Rectangle r)
    {
        WriteInt(buffer, r.X);
        WriteInt(buffer, r.Y);
        WriteInt(buffer, r.Width);
        WriteInt(buffer, r.Height);
    }

    public static Rectangle ReadRect(ReadOnlySpan<byte> data, ref int offset)
    {
        var x = ReadInt(data, ref offset);
        var y = ReadInt(data, ref offset);
        var w = ReadInt(data, ref offset);
        var h = ReadInt(data, ref offset);
        // Keep far inside int range so arithmetic on edges cannot overflow.
        const int Limit = 1 << 24;
        if (Math.Abs(x) > Limit || Math.Abs(y) > Limit || w > Limit || h > Limit)
            throw new InvalidDataException("Rectangle out of range.");
        return new Rectangle(x, y, w, h);
    }
}
