using RoboMouse.Core.Configuration;
using RoboMouse.Core.Input;
using RoboMouse.Core.Network;
using RoboMouse.Core.Network.Protocol;
using Xunit;
using ProtocolMessage = RoboMouse.Core.Network.Protocol.Message;

namespace RoboMouse.Core.Tests;

/// <summary>Chunked clipboard transfers, clipboard ordering across a ring, and the clipboard settings.</summary>
public class ClipboardSyncTests
{
    private static ClipboardMessage Big(int size, string origin = "a", ulong sequence = 7)
    {
        var data = new byte[size];
        new Random(size).NextBytes(data);
        return new ClipboardMessage { ContentType = ClipboardContentType.Image, FormatHint = "image/png", Data = data, OriginId = origin, Sequence = sequence };
    }

    [Fact]
    public void SmallContent_IsNotChunked()
    {
        var message = Big(ClipboardChunkMessage.ChunkThreshold);
        Assert.Same(message, Assert.Single(ClipboardChunkMessage.Split(message)));
    }

    [Fact]
    public void ChunkedContent_SurvivesTheWireAndIsReassembled()
    {
        var original = Big(ClipboardChunkMessage.ChunkSize * 3 + 1234);
        var parts = ClipboardChunkMessage.Split(original);
        Assert.Equal(4, parts.Count);

        var assembler = new ClipboardAssembler();
        ClipboardMessage? result = null;
        foreach (var part in parts)
        {
            var bytes = part.Serialize();
            Assert.True(bytes.Length <= ClipboardChunkMessage.ChunkSize + 1024);
            var chunk = Assert.IsType<ClipboardChunkMessage>(ProtocolMessage.Deserialize(bytes));
            Assert.Null(result);
            result = assembler.Add(chunk, long.MaxValue);
        }

        Assert.NotNull(result);
        Assert.Equal(original.Data, result.Data);
        Assert.Equal(original.ContentType, result.ContentType);
        Assert.Equal(original.FormatHint, result.FormatHint);
        Assert.Equal(original.OriginId, result.OriginId);
        Assert.Equal(original.Sequence, result.Sequence);
        Assert.False(assembler.InProgress);
    }

    [Fact]
    public void Assembler_DropsOversizedAndOutOfOrderTransfers()
    {
        var parts = ClipboardChunkMessage.Split(Big(ClipboardChunkMessage.ChunkSize * 2 + 10)).Cast<ClipboardChunkMessage>().ToList();

        var tooSmallLimit = new ClipboardAssembler();
        Assert.All(parts, p => Assert.Null(tooSmallLimit.Add(p, 1000)));

        var skipped = new ClipboardAssembler();
        Assert.Null(skipped.Add(parts[0], long.MaxValue));
        Assert.Null(skipped.Add(parts[2], long.MaxValue));
        Assert.False(skipped.InProgress);

        // A new transfer starting mid-way abandons the old one and completes on its own.
        var restarted = new ClipboardAssembler();
        Assert.Null(restarted.Add(parts[0], long.MaxValue));
        var other = ClipboardChunkMessage.Split(Big(ClipboardChunkMessage.ChunkSize + 5, "b", 9)).Cast<ClipboardChunkMessage>().ToList();
        Assert.Null(restarted.Add(other[0], long.MaxValue));
        Assert.Equal("b", restarted.Add(other[1], long.MaxValue)?.OriginId);
    }

    [Fact]
    public void Chunks_AreInterleavedWithInputAndPings()
    {
        var queue = new OutboundQueue();
        foreach (var part in ClipboardChunkMessage.Split(Big(ClipboardChunkMessage.ChunkSize * 4)))
            queue.Post(part);

        var round = new List<ProtocolMessage>();
        queue.DrainTo(round);
        Assert.IsType<ClipboardChunkMessage>(Assert.Single(round));

        // Input and a ping posted while the copy is going out are sent before the rest of it.
        queue.Post(new KeyboardMessage { KeyCode = Keys.A });
        queue.Post(new PingMessage());
        round.Clear();
        queue.DrainTo(round);
        Assert.IsType<PingMessage>(round[0]);
        Assert.IsType<KeyboardMessage>(round[1]);
        Assert.IsType<ClipboardChunkMessage>(round[2]);
        Assert.Equal(3, round.Count);

        Assert.Equal(2, queue.Count);
    }

    /// <summary>A machine in the relay simulation: its stamps, its clipboard, and its neighbours.</summary>
    private sealed class Node
    {
        public Node(string id, ulong clock)
        {
            Id = id;
            Clock = clock;
            S = new ClipboardStamps(id, () => Clock);
        }

        public string Id { get; }
        public ulong Clock;
        public ClipboardStamps S { get; }
        public string Content = string.Empty;
        public List<Node> Neighbours { get; } = new();
        public int Applied;
    }

    /// <summary>Delivers messages in the order given, like the service does: apply and pass on only what is newer.</summary>
    private static void Run(Queue<(Node To, Node From, string Content, string Origin, ulong Sequence)> wire)
    {
        var hops = 0;
        while (wire.Count > 0)
        {
            Assert.True(++hops < 1000, "clipboard messages kept circulating");
            var (to, from, content, origin, sequence) = wire.Dequeue();
            if (!to.S.TryAccept(origin, sequence))
                continue;
            to.Content = content;
            to.Applied++;
            foreach (var next in to.Neighbours.Where(n => n != from))
                wire.Enqueue((next, to, content, origin, sequence));
        }
    }

    private static (Node A, Node B, Node C) Ring()
    {
        var a = new Node("a", 1_000);
        var b = new Node("b", 1_000);
        var c = new Node("c", 1_000);
        a.Neighbours.AddRange(new[] { b, c });
        b.Neighbours.AddRange(new[] { a, c });
        c.Neighbours.AddRange(new[] { a, b });
        return (a, b, c);
    }

    private static void Copy(Node node, string content, Queue<(Node, Node, string, string, ulong)> wire)
    {
        var sequence = node.S.StampLocal();
        node.Content = content;
        foreach (var next in node.Neighbours)
            wire.Enqueue((next, node, content, node.Id, sequence));
    }

    [Fact]
    public void RingOfThree_OneCopy_ReachesEveryoneOnceAndStops()
    {
        var (a, b, c) = Ring();
        var wire = new Queue<(Node, Node, string, string, ulong)>();
        Copy(a, "hello", wire);
        Run(wire);

        Assert.All(new[] { a, b, c }, n => Assert.Equal("hello", n.Content));
        Assert.Equal(0, a.Applied); // its own copy coming back round the ring is not applied again
        Assert.Equal(1, b.Applied);
        Assert.Equal(1, c.Applied);
    }

    [Fact]
    public void RingOfThree_SimultaneousCopies_Converge()
    {
        var (a, b, c) = Ring();
        a.Clock = 5_000;
        c.Clock = 5_000;
        var wire = new Queue<(Node, Node, string, string, ulong)>();
        Copy(a, "from a", wire);
        Copy(c, "from c", wire); // same moment, crossing on the wire
        Run(wire);

        Assert.Equal(a.Content, b.Content);
        Assert.Equal(b.Content, c.Content);
    }

    [Fact]
    public void LocalCopy_AlwaysBeatsWhatItReplaces_EvenWithASlowClock()
    {
        var (a, b, c) = Ring();
        a.Clock = 9_000_000; // a's clock is far ahead
        var wire = new Queue<(Node, Node, string, string, ulong)>();
        Copy(a, "from a", wire);
        Run(wire);

        Copy(b, "from b", wire); // b copies afterwards with its slow clock
        Run(wire);

        Assert.All(new[] { a, b, c }, n => Assert.Equal("from b", n.Content));
    }

    [Theory]
    [InlineData(ClipboardContentType.Text, 100, true)]
    [InlineData(ClipboardContentType.Html, 100, true)]
    [InlineData(ClipboardContentType.Image, 100, true)]
    [InlineData(ClipboardContentType.Image, 2000, false)]
    [InlineData(ClipboardContentType.Files, 5000, true)]
    public void Settings_AllowByTypeAndSize(ClipboardContentType type, long length, bool expected)
    {
        var settings = new ClipboardSettings { MaxSizeBytes = 1000 };
        Assert.Equal(expected, settings.Allows(type, length));
    }

    [Fact]
    public void Settings_SwitchesTurnTypesOff()
    {
        var settings = new ClipboardSettings { SyncText = false, SyncImages = false, SyncFiles = false };
        Assert.False(settings.Allows(ClipboardContentType.Text, 1));
        Assert.False(settings.Allows(ClipboardContentType.Rtf, 1));
        Assert.False(settings.Allows(ClipboardContentType.Image, 1));
        Assert.False(settings.Allows(ClipboardContentType.Files, 1));

        Assert.False(new ClipboardSettings { Enabled = false }.Allows(ClipboardContentType.Text, 1));
    }
}
