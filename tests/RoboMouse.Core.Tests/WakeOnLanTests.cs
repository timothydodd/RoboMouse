using System.Net;
using RoboMouse.Core.Network;
using RoboMouse.Core.Network.Protocol;
using Xunit;

namespace RoboMouse.Core.Tests;

public class WakeOnLanTests
{
    [Theory]
    [InlineData("aa:bb:cc:dd:ee:ff", "AABBCCDDEEFF")]
    [InlineData("AA-BB-CC-DD-EE-FF", "AABBCCDDEEFF")]
    [InlineData("aabbccddeeff", "AABBCCDDEEFF")]
    [InlineData("000000000000", "")]
    [InlineData("aa:bb:cc", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalize_AcceptsCommonForms(string? input, string expected)
    {
        Assert.Equal(expected, WakeOnLan.Normalize(input));
    }

    [Fact]
    public void Format_UsesColons()
    {
        Assert.Equal("AA:BB:CC:DD:EE:FF", WakeOnLan.Format("aabbccddeeff"));
        Assert.Equal("", WakeOnLan.Format("nonsense"));
    }

    [Fact]
    public void MagicPacket_IsSixFFThenMacSixteenTimes()
    {
        var packet = WakeOnLan.BuildMagicPacket("01:23:45:67:89:AB");

        Assert.Equal(102, packet.Length);
        Assert.All(packet.Take(6), b => Assert.Equal(0xFF, b));
        var mac = new byte[] { 0x01, 0x23, 0x45, 0x67, 0x89, 0xAB };
        for (var i = 0; i < 16; i++)
            Assert.Equal(mac, packet.Skip(6 + i * 6).Take(6).ToArray());
    }

    [Fact]
    public void MagicPacket_RejectsABadAddress()
    {
        Assert.Throws<ArgumentException>(() => WakeOnLan.BuildMagicPacket("nope"));
    }

    [Fact]
    public void DirectedBroadcast_SetsTheHostBits()
    {
        Assert.Equal(IPAddress.Parse("192.168.1.255"),
            WakeOnLan.GetDirectedBroadcast(IPAddress.Parse("192.168.1.42"), IPAddress.Parse("255.255.255.0")));
        Assert.Equal(IPAddress.Parse("10.3.255.255"),
            WakeOnLan.GetDirectedBroadcast(IPAddress.Parse("10.2.7.9"), IPAddress.Parse("255.254.0.0")));
        Assert.Equal(IPAddress.Broadcast, WakeOnLan.GetDirectedBroadcast(IPAddress.Parse("10.2.7.9"), null));
    }

    [Fact]
    public void Handshake_CarriesTheMacAddress()
    {
        var sent = new HandshakeMessage { MachineId = "id", MachineName = "PC", ListenPort = 24800, MacAddress = "AABBCCDDEEFF" };
        var read = Assert.IsType<HandshakeMessage>(Message.Deserialize(sent.Serialize()));
        Assert.Equal("AABBCCDDEEFF", read.MacAddress);
        Assert.Equal(24800, read.ListenPort);

        var ack = new HandshakeAckMessage { Accepted = true, MachineId = "id", MachineName = "PC", ListenPort = 1, MacAddress = "AABBCCDDEEFF" };
        Assert.Equal("AABBCCDDEEFF", Assert.IsType<HandshakeAckMessage>(Message.Deserialize(ack.Serialize())).MacAddress);
    }

    [Fact]
    public void Handshake_FromABuildWithoutTheField_StillParses()
    {
        // What 1.1.x sends: the same payload without the trailing MAC string.
        var full = HandshakePayload(new HandshakeMessage { MachineId = "id", MachineName = "PC", ListenPort = 24800, MacAddress = "" });
        var old = full[..^4]; // an empty string is just its 4-byte length prefix
        var read = HandshakeMessage.DeserializePayload(old);
        Assert.Equal(24800, read.ListenPort);
        Assert.Equal("", read.MacAddress);
    }

    private static byte[] HandshakePayload(HandshakeMessage message)
    {
        var bytes = message.Serialize();
        return bytes[16..]; // 16-byte header
    }
}
