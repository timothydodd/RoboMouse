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
    public void EdgeHit_RoundTrips()
    {
        var m = RoundTrip(PipeMessage.EdgeHit(3, 0.42f));
        var (edge, normalized) = m.ReadEdgeHit();
        Assert.Equal(3, edge);
        Assert.Equal(0.42f, normalized, precision: 5);
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
