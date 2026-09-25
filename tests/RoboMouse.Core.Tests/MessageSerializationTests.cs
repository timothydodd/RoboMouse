using RoboMouse.Core.Input;
using RoboMouse.Core.Network.Protocol;
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
            DeltaX = -17,
            DeltaY = 42,
            EventType = MouseEventType.Move,
            WheelDelta = 0
        };

        var serialized = original.Serialize();
        var deserialized = ProtocolMessage.Deserialize(serialized) as MouseMessage;

        Assert.NotNull(deserialized);
        Assert.Equal(original.DeltaX, deserialized.DeltaX);
        Assert.Equal(original.DeltaY, deserialized.DeltaY);
        Assert.Equal(original.EventType, deserialized.EventType);
        Assert.Equal(original.WheelDelta, deserialized.WheelDelta);
    }

    [Fact]
    public void MouseMessage_WheelEvent_PreservesDelta()
    {
        var original = new MouseMessage
        {
            EventType = MouseEventType.Wheel,
            WheelDelta = -120
        };

        var serialized = original.Serialize();
        var deserialized = ProtocolMessage.Deserialize(serialized) as MouseMessage;

        Assert.NotNull(deserialized);
        Assert.Equal(MouseEventType.Wheel, deserialized.EventType);
        Assert.Equal(-120, deserialized.WheelDelta);
    }

    [Fact]
    public void PongMessage_EchoesPingTimestamp()
    {
        var ping = new PingMessage { Timestamp = 1234567890123 };
        var pong = new PongMessage { Timestamp = ping.Timestamp };

        var deserialized = ProtocolMessage.Deserialize(pong.Serialize());

        Assert.IsType<PongMessage>(deserialized);
        Assert.Equal(ping.Timestamp, deserialized!.Timestamp);
    }

    [Fact]
    public void Deserialize_RejectsOldProtocolVersion()
    {
        var data = new MouseMessage().Serialize();
        data[2] = 1; // Version byte

        Assert.Null(ProtocolMessage.Deserialize(data));
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
            MonitorId = "\\\\.\\DISPLAY2",
            EntryX = 0.75f,
            EntryY = 0.25f,
            WrapAround = true,
            HandBackPush = 30
        };

        var serialized = original.Serialize();
        var deserialized = ProtocolMessage.Deserialize(serialized) as CursorEnterMessage;

        Assert.NotNull(deserialized);
        Assert.Equal(original.MonitorId, deserialized.MonitorId);
        Assert.Equal(original.EntryX, deserialized.EntryX, precision: 5);
        Assert.Equal(original.EntryY, deserialized.EntryY, precision: 5);
        Assert.True(deserialized.WrapAround);
        Assert.Equal(30, deserialized.HandBackPush);
        Assert.False((ProtocolMessage.Deserialize(new CursorEnterMessage().Serialize()) as CursorEnterMessage)!.WrapAround);
    }

    [Fact]
    public void CursorLeaveMessage_RoundTrip_CarriesTheTargetOrReleased()
    {
        var target = ProtocolMessage.Deserialize(CursorLeaveMessage.To(-1920, 540).Serialize()) as CursorLeaveMessage;
        Assert.False(target!.Released);
        Assert.Equal((-1920, 540), (target.TargetX, target.TargetY));

        var released = ProtocolMessage.Deserialize(new CursorLeaveMessage().Serialize()) as CursorLeaveMessage;
        Assert.True(released!.Released);
    }

    [Fact]
    public void ScreenInfoMessage_RoundTrip_PreservesMonitors()
    {
        var original = new ScreenInfoMessage
        {
            Monitors =
            {
                new(new System.Drawing.Rectangle(0, 0, 3840, 2160), default, true, "A", 150),
                new(new System.Drawing.Rectangle(-1920, 200, 1920, 1080), default, false, "B", 100)
            }
        };

        var deserialized = ProtocolMessage.Deserialize(original.Serialize()) as ScreenInfoMessage;

        Assert.NotNull(deserialized);
        Assert.Equal(2, deserialized.Monitors.Count);
        Assert.Equal(("A", 150, true), (deserialized.Monitors[0].Id, deserialized.Monitors[0].Scale, deserialized.Monitors[0].Primary));
        Assert.Equal(new System.Drawing.Rectangle(-1920, 200, 1920, 1080), deserialized.Monitors[1].Bounds);
    }

    [Fact]
    public void VirtualLayoutMessage_RoundTrip_KeepsWhichScreensAreTheReceivers()
    {
        var original = new VirtualLayoutMessage
        {
            Screens =
            {
                new("B", new System.Drawing.Rectangle(1920, 0, 1920, 1080)),
                new(null, new System.Drawing.Rectangle(0, 0, 1920, 1080))
            }
        };

        var deserialized = ProtocolMessage.Deserialize(original.Serialize()) as VirtualLayoutMessage;
        var desktop = deserialized!.ToDesktop();

        Assert.Equal(2, desktop.Screens.Count);
        Assert.True(desktop.Find(Screen.VirtualDesktop.Local, "B").HasValue);
        Assert.Equal(VirtualLayoutMessage.Foreign, desktop.ScreenAt(10, 10)!.Value.Owner);
    }

    [Fact]
    public void ScreenInfoMessage_WithAnEmptyMonitor_IsSkipped()
    {
        var bad = new ScreenInfoMessage { Monitors = { new(System.Drawing.Rectangle.Empty, default, true, "A", 100) } };
        Assert.Null(ProtocolMessage.Deserialize(bad.Serialize()));
    }

    [Theory]
    [InlineData(PeerPowerState.DisplayOff)]
    [InlineData(PeerPowerState.DisplayOn)]
    [InlineData(PeerPowerState.Suspending)]
    public void PowerStateMessage_RoundTrip_PreservesState(PeerPowerState state)
    {
        var deserialized = ProtocolMessage.Deserialize(new PowerStateMessage { State = state }.Serialize()) as PowerStateMessage;

        Assert.NotNull(deserialized);
        Assert.Equal(state, deserialized.State);
    }

    [Theory]
    [InlineData(InputBlockReason.None)]
    [InlineData(InputBlockReason.SecureDesktop)]
    [InlineData(InputBlockReason.ElevatedWindow)]
    public void InputStatusMessage_RoundTrip_PreservesReason(InputBlockReason reason)
    {
        var deserialized = ProtocolMessage.Deserialize(new InputStatusMessage { Reason = reason }.Serialize()) as InputStatusMessage;

        Assert.NotNull(deserialized);
        Assert.Equal(reason, deserialized.Reason);
        Assert.Equal(reason != InputBlockReason.None, deserialized.IsBlocked);
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
