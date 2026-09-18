using RoboMouse.Contracts;
using Xunit;

namespace RoboMouse.Core.Tests;

/// <summary>The pipe framing carries app/service/helper messages, so its round-trips are pinned here.</summary>
public class PipeMessageTests
{
    private static PipeMessage RoundTrip(PipeMessage m)
    {
        var frame = m.ToFrame();
        var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(frame);
        Assert.Equal(frame.Length - 4, length);
        return new PipeMessage((PipeOpcode)frame[4], frame[5..]);
    }

    [Fact]
    public void Hello_CarriesProtocolVersion()
    {
        var m = RoundTrip(PipeMessage.Hello());
        Assert.Equal(PipeOpcode.Hello, m.Opcode);
        Assert.Equal(PipeNames.ProtocolVersion, m.ReadHelloVersion());
    }

    [Fact]
    public void Motion_RoundTrips()
    {
        var m = RoundTrip(PipeMessage.Motion(PipeOpcode.InjectMotion, -7, 13));
        Assert.Equal(PipeOpcode.InjectMotion, m.Opcode);
        Assert.Equal((-7, 13), m.ReadMotion());
    }

    [Fact]
    public void Key_RoundTrips()
    {
        var m = RoundTrip(PipeMessage.Key(0x41, 0x1E, 2, extended: true));
        Assert.Equal(PipeOpcode.InjectKey, m.Opcode);
        Assert.Equal((0x41, 0x1Eu, 2, true), m.ReadKey());
    }

    [Fact]
    public void CursorPosition_RoundTrips()
    {
        var m = RoundTrip(PipeMessage.Motion(PipeOpcode.CursorPosition, -1920, 1079));
        Assert.Equal(PipeOpcode.CursorPosition, m.Opcode);
        Assert.Equal((-1920, 1079), m.ReadMotion());
    }

    [Theory]
    [InlineData(true, "Winlogon")]
    [InlineData(false, "Default")]
    public void DesktopChanged_RoundTrips(bool secure, string name)
    {
        var m = RoundTrip(PipeMessage.DesktopChanged(secure, name));
        Assert.Equal((secure, name), m.ReadDesktopChanged());
    }
}
