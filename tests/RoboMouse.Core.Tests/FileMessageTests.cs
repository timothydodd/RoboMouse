using RoboMouse.Core.Network.Protocol;
using Xunit;
using ProtocolMessage = RoboMouse.Core.Network.Protocol.Message;

namespace RoboMouse.Core.Tests;

public class FileMessageTests
{
    [Fact]
    public void FileOffer_RoundTrip_PreservesEntries()
    {
        var stamp = new DateTime(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
        var original = new FileOfferMessage
        {
            OfferId = "abc123",
            Entries =
            {
                new FileOfferEntry { RelativePath = "photos", IsDirectory = true, LastWriteTimeUtc = stamp },
                new FileOfferEntry { RelativePath = "photos\\a.jpg", Size = 123_456_789_012, LastWriteTimeUtc = stamp },
                new FileOfferEntry { RelativePath = "notes.txt", Size = 42, LastWriteTimeUtc = stamp }
            }
        };

        var deserialized = ProtocolMessage.Deserialize(original.Serialize()) as FileOfferMessage;

        Assert.NotNull(deserialized);
        Assert.Equal("abc123", deserialized.OfferId);
        Assert.Equal(3, deserialized.Entries.Count);
        Assert.True(deserialized.Entries[0].IsDirectory);
        Assert.Equal("photos\\a.jpg", deserialized.Entries[1].RelativePath);
        Assert.Equal(123_456_789_012, deserialized.Entries[1].Size);
        Assert.Equal(stamp, deserialized.Entries[2].LastWriteTimeUtc);
        Assert.Equal(123_456_789_054, deserialized.TotalSize);
    }

    [Fact]
    public void FileRequest_RoundTrip()
    {
        var original = new FileRequestMessage { OfferId = "o1", EntryIndex = 7, Offset = 5_000_000_000, Length = 1 << 20 };
        var deserialized = ProtocolMessage.Deserialize(original.Serialize()) as FileRequestMessage;

        Assert.NotNull(deserialized);
        Assert.Equal(("o1", 7, 5_000_000_000L, 1 << 20), (deserialized.OfferId, deserialized.EntryIndex, deserialized.Offset, deserialized.Length));
    }

    [Fact]
    public void FileChunk_RoundTrip_WithDataAndWithError()
    {
        var data = new byte[70_000];
        new Random(3).NextBytes(data);
        var ok = new FileChunkMessage { OfferId = "o1", EntryIndex = 2, Offset = 4096, Data = data };
        var okBack = ProtocolMessage.Deserialize(ok.Serialize()) as FileChunkMessage;
        Assert.NotNull(okBack);
        Assert.Equal(data, okBack.Data);
        Assert.Equal(string.Empty, okBack.Error);
        Assert.Equal(4096, okBack.Offset);

        var failed = new FileChunkMessage { OfferId = "o1", EntryIndex = 2, Offset = 0, Error = "gone" };
        var failedBack = ProtocolMessage.Deserialize(failed.Serialize()) as FileChunkMessage;
        Assert.NotNull(failedBack);
        Assert.Equal("gone", failedBack.Error);
        Assert.Empty(failedBack.Data);
    }

    [Fact]
    public void FileOfferRevoked_RoundTrip()
    {
        var deserialized = ProtocolMessage.Deserialize(new FileOfferRevokedMessage { OfferId = "zzz" }.Serialize()) as FileOfferRevokedMessage;
        Assert.NotNull(deserialized);
        Assert.Equal("zzz", deserialized.OfferId);
    }

    [Fact]
    public void Handshake_CarriesKindAndListenPort()
    {
        var handshake = new HandshakeMessage { MachineId = "m", MachineName = "n", Kind = ConnectionKind.Transfer, ListenPort = 24800 };
        var back = ProtocolMessage.Deserialize(handshake.Serialize()) as HandshakeMessage;
        Assert.NotNull(back);
        Assert.Equal(ConnectionKind.Transfer, back.Kind);
        Assert.Equal(24800, back.ListenPort);

        var ack = new HandshakeAckMessage { Accepted = true, MachineId = "m", MachineName = "n", ListenPort = 24801 };
        var ackBack = ProtocolMessage.Deserialize(ack.Serialize()) as HandshakeAckMessage;
        Assert.NotNull(ackBack);
        Assert.Equal(24801, ackBack.ListenPort);
        Assert.Null(ackBack.RejectReason);
    }
}
