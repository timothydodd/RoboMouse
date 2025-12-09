using RoboMouse.Core.Input;
using RoboMouse.Core.Network.Protocol;
using System.Windows.Forms;
using Xunit;
using ProtocolMessage = RoboMouse.Core.Network.Protocol.Message;

namespace RoboMouse.Core.Tests;

public class MessageSerializationTests
{
    [Fact]
    public void HandshakeMessage_RoundTrip_PreservesData()
    {
        var original = new HandshakeMessage
        {
            MachineId = "test-machine-id",
            MachineName = "Test Machine",
            ScreenWidth = 1920,
            ScreenHeight = 1080,
            SupportsClipboard = true
        };

        var serialized = original.Serialize();
        var deserialized = ProtocolMessage.Deserialize(serialized) as HandshakeMessage;

        Assert.NotNull(deserialized);
        Assert.Equal(original.MachineId, deserialized.MachineId);
        Assert.Equal(original.MachineName, deserialized.MachineName);
        Assert.Equal(original.ScreenWidth, deserialized.ScreenWidth);
        Assert.Equal(original.ScreenHeight, deserialized.ScreenHeight);
        Assert.Equal(original.SupportsClipboard, deserialized.SupportsClipboard);
    }

    [Fact]
    public void MouseMessage_RoundTrip_PreservesData()
    {
        var original = new MouseMessage
        {
            X = 500,
            Y = 300,
            EventType = MouseEventType.LeftDown,
            WheelDelta = 0
        };

        var serialized = original.Serialize();
        var deserialized = ProtocolMessage.Deserialize(serialized) as MouseMessage;

        Assert.NotNull(deserialized);
        Assert.Equal(original.X, deserialized.X);
        Assert.Equal(original.Y, deserialized.Y);
        Assert.Equal(original.EventType, deserialized.EventType);
        Assert.Equal(original.WheelDelta, deserialized.WheelDelta);
    }

    [Fact]
    public void MouseMessage_WheelEvent_PreservesDelta()
    {
        var original = new MouseMessage
        {
            X = 100,
            Y = 200,
            EventType = MouseEventType.Wheel,
            WheelDelta = 120
        };

        var serialized = original.Serialize();
        var deserialized = ProtocolMessage.Deserialize(serialized) as MouseMessage;

        Assert.NotNull(deserialized);
        Assert.Equal(MouseEventType.Wheel, deserialized.EventType);
        Assert.Equal(120, deserialized.WheelDelta);
    }

    [Fact]
    public void KeyboardMessage_RoundTrip_PreservesData()
    {
        var original = new KeyboardMessage
        {
            KeyCode = Keys.A,
            ScanCode = 30,
            EventType = KeyboardEventType.KeyDown,
            IsExtendedKey = false
        };

        var serialized = original.Serialize();
        var deserialized = ProtocolMessage.Deserialize(serialized) as KeyboardMessage;

        Assert.NotNull(deserialized);
        Assert.Equal(original.KeyCode, deserialized.KeyCode);
        Assert.Equal(original.ScanCode, deserialized.ScanCode);
        Assert.Equal(original.EventType, deserialized.EventType);
        Assert.Equal(original.IsExtendedKey, deserialized.IsExtendedKey);
    }

    [Fact]
    public void ClipboardMessage_RoundTrip_PreservesData()
    {
        var testData = System.Text.Encoding.UTF8.GetBytes("Hello, World!");
        var original = new ClipboardMessage
        {
            ContentType = ClipboardContentType.Text,
            Data = testData,
            FormatHint = "text/plain"
        };

        var serialized = original.Serialize();
        var deserialized = ProtocolMessage.Deserialize(serialized) as ClipboardMessage;

        Assert.NotNull(deserialized);
        Assert.Equal(original.ContentType, deserialized.ContentType);
        Assert.Equal(original.FormatHint, deserialized.FormatHint);
        Assert.Equal(original.Data, deserialized.Data);
    }

    [Fact]
    public void CursorEnterMessage_RoundTrip_PreservesData()
    {
        var original = new CursorEnterMessage
        {
            EntryX = 0.75f,
            EntryY = 0.25f,
            EntryEdge = Configuration.ScreenPosition.Left
        };

        var serialized = original.Serialize();
        var deserialized = ProtocolMessage.Deserialize(serialized) as CursorEnterMessage;

        Assert.NotNull(deserialized);
        Assert.Equal(original.EntryX, deserialized.EntryX, precision: 5);
        Assert.Equal(original.EntryY, deserialized.EntryY, precision: 5);
        Assert.Equal(original.EntryEdge, deserialized.EntryEdge);
    }

    [Fact]
    public void GetMessageSize_ReturnsCorrectSize()
    {
        var message = new PingMessage();
        var serialized = message.Serialize();

        var size = ProtocolMessage.GetMessageSize(serialized);

        Assert.Equal(serialized.Length, size);
    }

    [Fact]
    public void InvalidMagicBytes_ReturnsNull()
    {
        var badData = new byte[] { 0x00, 0x00, 0x01, 0x40, 0x00, 0x00, 0x00, 0x00 };

        var result = ProtocolMessage.Deserialize(badData);

        Assert.Null(result);
    }
}
