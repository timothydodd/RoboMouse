using RoboMouse.Core.Input;
using RoboMouse.Core.Network;
using RoboMouse.Core.Network.Protocol;
using Xunit;
using ProtocolMessage = RoboMouse.Core.Network.Protocol.Message;

namespace RoboMouse.Core.Tests;

public class FramingTests
{
    [Fact]
    public void ReadFrames_ParsesMultipleFramesInOneBuffer()
    {
        var a = MouseMessage.Motion(3, -4).Serialize();
        var b = new PingMessage().Serialize();
        var c = new KeyboardMessage { KeyCode = Keys.A, EventType = KeyboardEventType.KeyDown }.Serialize();
        var data = a.Concat(b).Concat(c).ToArray();

        var messages = new List<ProtocolMessage>();
        var consumed = MessageFramer.ReadFrames(data, messages.Add);

        Assert.Equal(data.Length, consumed);
        Assert.Collection(messages,
            m => Assert.Equal((3, -4), (((MouseMessage)m).DeltaX, ((MouseMessage)m).DeltaY)),
            m => Assert.IsType<PingMessage>(m),
            m => Assert.Equal(Keys.A, ((KeyboardMessage)m).KeyCode));
    }

    [Fact]
    public void ReadFrames_LeavesPartialFrameForNextRead()
    {
        var whole = MouseMessage.Motion(1, 1).Serialize();
        var partial = new PingMessage().Serialize();
        var data = whole.Concat(partial.Take(partial.Length - 1)).ToArray();

        var messages = new List<ProtocolMessage>();
        var consumed = MessageFramer.ReadFrames(data, messages.Add);

        Assert.Equal(whole.Length, consumed);
        Assert.Single(messages);

        // Feeding the remainder plus the missing byte completes the frame.
        var rest = data.Skip(consumed).Concat(partial.Skip(partial.Length - 1)).ToArray();
        messages.Clear();
        Assert.Equal(partial.Length, MessageFramer.ReadFrames(rest, messages.Add));
        Assert.IsType<PingMessage>(messages.Single());
    }

    [Fact]
    public void ReadFrames_ThrowsOnCorruptHeader()
    {
        var data = new byte[32];
        Assert.Throws<InvalidDataException>(() => MessageFramer.ReadFrames(data, _ => { }));
    }

    [Fact]
    public void ReadFrames_SkipsUnknownMessageTypeButKeepsFraming()
    {
        var unknown = new PingMessage().Serialize();
        unknown[3] = 0x7E; // Unknown type byte
        var known = new PongMessage().Serialize();

        var messages = new List<ProtocolMessage>();
        var consumed = MessageFramer.ReadFrames(unknown.Concat(known).ToArray(), messages.Add);

        Assert.Equal(unknown.Length + known.Length, consumed);
        Assert.IsType<PongMessage>(messages.Single());
    }

    [Fact]
    public void OutboundQueue_MergesConsecutiveMotionOnly()
    {
        var queue = new OutboundQueue();
        queue.Post(MouseMessage.Motion(1, 2));
        queue.Post(MouseMessage.Motion(3, 4));
        queue.Post(new MouseMessage { EventType = MouseEventType.LeftDown });
        queue.Post(MouseMessage.Motion(-1, 0));
        queue.Post(MouseMessage.Motion(-1, 0));

        var drained = new List<ProtocolMessage>();
        queue.DrainTo(drained);

        Assert.Equal(3, drained.Count);
        Assert.Equal((4, 6), (((MouseMessage)drained[0]).DeltaX, ((MouseMessage)drained[0]).DeltaY));
        Assert.Equal(MouseEventType.LeftDown, ((MouseMessage)drained[1]).EventType);
        Assert.Equal((-2, 0), (((MouseMessage)drained[2]).DeltaX, ((MouseMessage)drained[2]).DeltaY));
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void OutboundQueue_DoesNotMergeAcrossDrain()
    {
        var queue = new OutboundQueue();
        queue.Post(MouseMessage.Motion(5, 5));
        var first = new List<ProtocolMessage>();
        queue.DrainTo(first);

        queue.Post(MouseMessage.Motion(1, 1));
        var second = new List<ProtocolMessage>();
        queue.DrainTo(second);

        Assert.Equal((5, 5), (((MouseMessage)first[0]).DeltaX, ((MouseMessage)first[0]).DeltaY));
        Assert.Equal((1, 1), (((MouseMessage)second[0]).DeltaX, ((MouseMessage)second[0]).DeltaY));
    }

    [Fact]
    public void OutboundQueue_PingsAndPongsJumpTheQueue()
    {
        var queue = new OutboundQueue();
        queue.Post(new ClipboardMessage { Data = new byte[1024] });
        queue.Post(MouseMessage.Motion(1, 1));
        queue.Post(new PongMessage());
        queue.Post(MouseMessage.Motion(1, 1));
        queue.Post(new PingMessage());

        var drained = new List<ProtocolMessage>();
        queue.DrainTo(drained);

        Assert.Collection(drained,
            m => Assert.IsType<PongMessage>(m),
            m => Assert.IsType<PingMessage>(m),
            m => Assert.IsType<ClipboardMessage>(m),
            // The pong in between does not split the motion run.
            m => Assert.Equal(2, ((MouseMessage)m).DeltaX));
    }
}
