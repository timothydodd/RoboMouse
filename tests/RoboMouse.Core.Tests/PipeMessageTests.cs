using System.Buffers.Binary;
using RoboMouse.Contracts;
using RoboMouse.Core.Input;
using Xunit;

namespace RoboMouse.Core.Tests;

/// <summary>The pipe framing carries app/service/helper messages, so its round-trips are pinned here.</summary>
public class PipeMessageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PipeMessage RoundTrip(PipeMessage m)
    {
        var frame = m.ToFrame();
        var length = BinaryPrimitives.ReadInt32LittleEndian(frame);
        Assert.Equal(frame.Length - 4, length);
        return new PipeMessage((PipeOpcode)frame[4], frame[5..]);
    }

    [Fact]
    public void Hello_CarriesProtocolVersion()
    {
        var m = RoundTrip(PipeMessage.Hello());
        Assert.Equal(PipeOpcode.Hello, m.Opcode);
        Assert.Equal(PipeNames.ProtocolVersion, m.ReadHelloVersion());
        Assert.True(m.IsWellFormed);
    }

    [Fact]
    public void Motion_RoundTrips()
    {
        var m = RoundTrip(PipeMessage.Motion(PipeOpcode.InjectMotion, -7, 13));
        Assert.Equal(PipeOpcode.InjectMotion, m.Opcode);
        Assert.Equal((-7, 13), m.ReadMotion());
        Assert.True(m.IsWellFormed);
    }

    [Fact]
    public void Key_RoundTrips()
    {
        var m = RoundTrip(PipeMessage.Key(0x41, 0x1E, 2, extended: true));
        Assert.Equal(PipeOpcode.InjectKey, m.Opcode);
        Assert.Equal((0x41, 0x1Eu, 2, true), m.ReadKey());
        Assert.True(m.IsWellFormed);
    }

    [Fact]
    public void CursorQueryAndReply_CarryTheSequenceId()
    {
        var query = RoundTrip(PipeMessage.QueryCursor(0xDEADBEEF));
        Assert.Equal(PipeOpcode.QueryCursor, query.Opcode);
        Assert.Equal(0xDEADBEEFu, query.ReadQueryCursor());

        var reply = RoundTrip(PipeMessage.CursorPosition(-1920, 1079, 7));
        Assert.Equal(PipeOpcode.CursorPosition, reply.Opcode);
        Assert.Equal((-1920, 1079, 7u), reply.ReadCursorPosition());
        Assert.True(query.IsWellFormed && reply.IsWellFormed);
    }

    [Fact]
    public void InputConstants_MatchTheCoreEnums()
    {
        Assert.Equal((int)MouseEventType.LeftDown, PipeInput.LeftDown);
        Assert.Equal((int)MouseEventType.LeftUp, PipeInput.LeftUp);
        Assert.Equal((int)MouseEventType.RightDown, PipeInput.RightDown);
        Assert.Equal((int)MouseEventType.RightUp, PipeInput.RightUp);
        Assert.Equal((int)MouseEventType.MiddleDown, PipeInput.MiddleDown);
        Assert.Equal((int)MouseEventType.MiddleUp, PipeInput.MiddleUp);
        Assert.Equal((int)MouseEventType.Wheel, PipeInput.Wheel);
        Assert.Equal((int)MouseEventType.HWheel, PipeInput.HWheel);
        Assert.Equal((int)MouseEventType.XButton1Down, PipeInput.XButton1Down);
        Assert.Equal((int)MouseEventType.XButton1Up, PipeInput.XButton1Up);
        Assert.Equal((int)MouseEventType.XButton2Down, PipeInput.XButton2Down);
        Assert.Equal((int)MouseEventType.XButton2Up, PipeInput.XButton2Up);
        Assert.Equal((int)KeyboardEventType.KeyDown, PipeInput.KeyDown);
        Assert.Equal((int)KeyboardEventType.KeyUp, PipeInput.KeyUp);
        Assert.Equal((int)KeyboardEventType.SysKeyDown, PipeInput.SysKeyDown);
        Assert.Equal((int)KeyboardEventType.SysKeyUp, PipeInput.SysKeyUp);
    }

    // --- validation: every opcode rejects a payload one byte short, one byte long, and empty ---------

    public static TheoryData<PipeMessage> ValidMessages => new()
    {
        PipeMessage.Hello(),
        PipeMessage.Motion(PipeOpcode.InjectMotion, 1, 2),
        PipeMessage.Motion(PipeOpcode.MoveTo, 1, 2),
        PipeMessage.Button(PipeOpcode.InjectButton, PipeInput.LeftDown, 0),
        PipeMessage.Key(0x41, 0x1E, PipeInput.KeyDown, false),
        PipeMessage.QueryCursor(1),
        PipeMessage.CursorPosition(1, 2, 3)
    };

    [Theory]
    [MemberData(nameof(ValidMessages))]
    public void Validation_RejectsTruncatedAndPaddedPayloads(PipeMessage valid)
    {
        Assert.True(valid.IsWellFormed);
        Assert.False(new PipeMessage(valid.Opcode, valid.Payload[..^1]).IsWellFormed);
        Assert.False(new PipeMessage(valid.Opcode, [.. valid.Payload, 0]).IsWellFormed);
        Assert.False(new PipeMessage(valid.Opcode).IsWellFormed);
    }

    [Theory]
    [InlineData(0)]   // Move is not a button event
    [InlineData(13)]
    [InlineData(-1)]
    public void Validation_RejectsButtonEventsOutOfRange(int eventType) =>
        Assert.False(PipeMessage.Button(PipeOpcode.InjectButton, eventType, 0).IsWellFormed);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(255, 0)]
    [InlineData(0x41, 4)]
    [InlineData(0x41, -1)]
    public void Validation_RejectsKeysOutOfRange(int vk, int eventType) =>
        Assert.False(PipeMessage.Key(vk, 0, eventType, false).IsWellFormed);

    [Fact]
    public void Validation_RejectsUnknownOpcodes()
    {
        Assert.False(new PipeMessage((PipeOpcode)0x24, [1]).IsWellFormed); // retired DesktopChanged
        Assert.False(new PipeMessage((PipeOpcode)0x77).IsWellFormed);
        Assert.True(new PipeMessage(PipeOpcode.HelperReady).IsWellFormed);
        Assert.False(new PipeMessage(PipeOpcode.HelperReady, [0]).IsWellFormed);
    }

    // --- framing on a stream -----------------------------------------------------------------------

    private static byte[] LengthPrefix(int length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        return header;
    }

    private static PipeConnection Reader(params byte[][] chunks) =>
        new(new MemoryStream(chunks.SelectMany(c => c).ToArray()));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(PipeNames.MaxFrameLength + 1)]
    public async Task Receive_RejectsFrameLengthsOutOfRange(int length)
    {
        using var pipe = Reader(LengthPrefix(length), new byte[16]);
        await Assert.ThrowsAsync<InvalidDataException>(() => pipe.ReceiveAsync(Ct));
    }

    [Fact]
    public async Task Receive_AcceptsAFrameAtTheCap()
    {
        using var pipe = Reader(LengthPrefix(PipeNames.MaxFrameLength), [(byte)PipeOpcode.Error], new byte[PipeNames.MaxFrameLength - 1]);
        var message = await pipe.ReceiveAsync(Ct);
        Assert.Equal(PipeOpcode.Error, message!.Value.Opcode);
    }

    [Fact]
    public async Task Receive_ReturnsNull_WhenTheStreamEndsInsideAFrame()
    {
        var frame = PipeMessage.Key(0x41, 0x1E, PipeInput.KeyDown, false).ToFrame();
        using (var partialHeader = Reader(frame[..2]))
            Assert.Null(await partialHeader.ReceiveAsync(Ct));

        using var partialBody = Reader(frame[..^3]);
        Assert.Null(await partialBody.ReceiveAsync(Ct));
        Assert.False(partialBody.IsConnected);
    }

    [Fact]
    public async Task Receive_ReadsConsecutiveFrames()
    {
        using var pipe = Reader(PipeMessage.Hello().ToFrame(), PipeMessage.QueryCursor(9).ToFrame());
        Assert.Equal(PipeOpcode.Hello, (await pipe.ReceiveAsync(Ct))!.Value.Opcode);
        Assert.Equal(9u, (await pipe.ReceiveAsync(Ct))!.Value.ReadQueryCursor());
        Assert.Null(await pipe.ReceiveAsync(Ct));
    }

    [Fact]
    public async Task Send_RefusesAMessageOverTheCap()
    {
        using var pipe = new PipeConnection(new MemoryStream());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            pipe.SendAsync(new PipeMessage(PipeOpcode.Error, new byte[PipeNames.MaxFrameLength]), Ct));
    }

    // --- held input ------------------------------------------------------------------------------

    [Fact]
    public void HeldInput_ReleasesWhatIsStillDown()
    {
        var held = new HeldInput();
        held.Track(PipeMessage.Key(0x10, 0x2A, PipeInput.KeyDown, false));
        held.Track(PipeMessage.Key(0x41, 0x1E, PipeInput.KeyDown, false));
        held.Track(PipeMessage.Key(0x41, 0x1E, PipeInput.KeyUp, false));
        held.Track(PipeMessage.Key(0xA5, 0x38, PipeInput.SysKeyDown, true));
        held.Track(PipeMessage.Button(PipeOpcode.InjectButton, PipeInput.LeftDown, 0));
        held.Track(PipeMessage.Button(PipeOpcode.InjectButton, PipeInput.RightDown, 0));
        held.Track(PipeMessage.Button(PipeOpcode.InjectButton, PipeInput.RightUp, 0));
        held.Track(PipeMessage.Button(PipeOpcode.InjectButton, PipeInput.Wheel, 120));
        held.Track(PipeMessage.Motion(PipeOpcode.InjectMotion, 5, 5));

        var releases = held.TakeReleases();
        Assert.True(held.IsEmpty);
        Assert.Equal(3, releases.Count);
        Assert.Contains(releases, r => r.Opcode == PipeOpcode.InjectKey && r.ReadKey() == (0x10, 0x2Au, PipeInput.KeyUp, false));
        Assert.Contains(releases, r => r.Opcode == PipeOpcode.InjectKey && r.ReadKey() == (0xA5, 0x38u, PipeInput.KeyUp, true));
        Assert.Contains(releases, r => r.Opcode == PipeOpcode.InjectButton && r.ReadButton().eventType == PipeInput.LeftUp);
        Assert.Empty(held.TakeReleases());
    }
}
