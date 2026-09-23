using System.Buffers.Binary;
using System.Net;
using RoboMouse.Core.Input;
using RoboMouse.Core.Network;
using RoboMouse.Core.Network.Protocol;
using Xunit;
using ProtocolMessage = RoboMouse.Core.Network.Protocol.Message;

namespace RoboMouse.Core.Tests;

/// <summary>Protocol 5 message fields, malformed payloads and signed discovery.</summary>
public class ProtocolV5Tests
{
    [Fact]
    public void ClipboardAndFileOffer_CarryOriginAndSequence()
    {
        var clip = new ClipboardMessage { ContentType = ClipboardContentType.Text, Data = "hi"u8.ToArray(), FormatHint = "text/plain", OriginId = "origin", Sequence = 12345678901234UL };
        var back = Assert.IsType<ClipboardMessage>(ProtocolMessage.Deserialize(clip.Serialize()));
        Assert.Equal("origin", back.OriginId);
        Assert.Equal(12345678901234UL, back.Sequence);
        Assert.Equal("hi"u8.ToArray(), back.Data);

        var offer = new FileOfferMessage
        {
            OfferId = "o1",
            OriginId = "origin",
            Sequence = 42,
            Entries = { new FileOfferEntry { RelativePath = "a.txt", Size = 3, LastWriteTimeUtc = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc) } }
        };
        var offerBack = Assert.IsType<FileOfferMessage>(ProtocolMessage.Deserialize(offer.Serialize()));
        Assert.Equal("origin", offerBack.OriginId);
        Assert.Equal(42UL, offerBack.Sequence);
        Assert.Equal("a.txt", Assert.Single(offerBack.Entries).RelativePath);
    }

    [Fact]
    public void Deserialize_RejectsProtocol4Messages()
    {
        var bytes = new PingMessage().Serialize();
        bytes[2] = 4;
        Assert.Null(ProtocolMessage.Deserialize(bytes));
    }

    private static IEnumerable<ProtocolMessage> Samples() => new ProtocolMessage[]
    {
        new HandshakeMessage { MachineId = "id", MachineName = "PC", ScreenWidth = 1, ScreenHeight = 2, ListenPort = 3, MacAddress = "001122334455" },
        new HandshakeAckMessage { Accepted = false, MachineId = "id", MachineName = "PC", RejectReason = "pending: x", MacAddress = "aa" },
        new MouseMessage { DeltaX = 1, DeltaY = -1, EventType = MouseEventType.LeftDown, WheelDelta = 120 },
        new KeyboardMessage { KeyCode = Keys.A, ScanCode = 0x1E, EventType = KeyboardEventType.KeyDown },
        new CursorEnterMessage { EntryEdge = Configuration.ScreenPosition.Left, EntryY = 0.5f },
        new CursorLeaveMessage(),
        new InputStatusMessage { Reason = InputBlockReason.SecureDesktop },
        new PowerStateMessage(),
        new ClipboardMessage { ContentType = ClipboardContentType.Text, Data = new byte[40], FormatHint = "text/plain", OriginId = "o", Sequence = 1 },
        new ClipboardChunkMessage { ContentType = ClipboardContentType.Image, OriginId = "o", Sequence = 2, TotalLength = 100, Offset = 0, Data = new byte[50] },
        new FileOfferMessage { OfferId = "o", OriginId = "x", Entries = { new FileOfferEntry { RelativePath = "a" }, new FileOfferEntry { RelativePath = "b", IsDirectory = true } } },
        new FileOfferRevokedMessage { OfferId = "o" },
        new FileRequestMessage { OfferId = "o", EntryIndex = 1, Offset = 2, Length = 3 },
        new FileChunkMessage { OfferId = "o", Data = new byte[20], Error = "e" }
    };

    [Fact]
    public void MalformedPayloads_NeverThrow()
    {
        var random = new Random(5);
        foreach (var sample in Samples())
        {
            var good = sample.Serialize();
            Assert.NotNull(ProtocolMessage.Deserialize(good));

            // Every truncation, with the length field fixed up so the frame itself looks whole.
            for (var cut = 16; cut < good.Length; cut++)
            {
                var truncated = good[..cut];
                BinaryPrimitives.WriteInt32LittleEndian(truncated.AsSpan(4), cut - 16);
                _ = ProtocolMessage.Deserialize(truncated);
            }

            // Random payloads, and random corruption of the real one.
            for (var i = 0; i < 300; i++)
            {
                var junk = new byte[random.Next(0, 96)];
                random.NextBytes(junk);
                var frame = new byte[16 + junk.Length];
                good.AsSpan(0, 16).CopyTo(frame);
                BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), junk.Length);
                junk.CopyTo(frame, 16);
                _ = ProtocolMessage.Deserialize(frame);

                var corrupt = (byte[])good.Clone();
                for (var flips = 0; flips < 3; flips++)
                    corrupt[16 + random.Next(Math.Max(1, corrupt.Length - 16))] = (byte)random.Next(256);
                _ = ProtocolMessage.Deserialize(corrupt);
            }
        }
    }

    [Fact]
    public void NegativePayloadLength_IsNotAFrame()
    {
        var bytes = new PingMessage().Serialize();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), -5);
        Assert.Equal(-1, ProtocolMessage.GetMessageSize(bytes));
        Assert.Null(ProtocolMessage.Deserialize(bytes));
    }

    [Fact]
    public void Discovery_ListsSignedBroadcasts_WithTheirKey()
    {
        using var mine = IdentityKey.Create();
        using var theirs = IdentityKey.Create();
        using var discovery = new PeerDiscovery(0, 24800, "me", "ME", 1920, 1080, mine);
        using var other = new PeerDiscovery(0, 24800, "them", "THEM", 1920, 1080, theirs);

        discovery.ProcessDiscoveryMessage(other.CreateDiscoveryMessage(), new IPEndPoint(IPAddress.Loopback, 24801));

        var peer = Assert.Single(discovery.Peers);
        Assert.Equal("them", peer.MachineId);
        Assert.Equal(theirs.PublicKeyText, peer.IdentityKey);
    }

    [Fact]
    public void Discovery_DropsTamperedAndRefusedBroadcasts()
    {
        using var discovery = new PeerDiscovery(0, 24800, "me", "ME", 1920, 1080, IdentityKey.Create());
        using var other = new PeerDiscovery(0, 24800, "them", "THEM", 1920, 1080, IdentityKey.Create());
        var message = other.CreateDiscoveryMessage();

        var renamed = (byte[])message.Clone();
        renamed[7 + 4 + 4 + 4] ^= 0x01; // a letter of the name
        discovery.ProcessDiscoveryMessage(renamed, new IPEndPoint(IPAddress.Loopback, 24801));
        discovery.ProcessDiscoveryMessage(message[..^10], new IPEndPoint(IPAddress.Loopback, 24801));
        Assert.Empty(discovery.Peers);

        discovery.AcceptPeer = _ => false;
        discovery.ProcessDiscoveryMessage(message, new IPEndPoint(IPAddress.Loopback, 24801));
        Assert.Empty(discovery.Peers);
    }
}
