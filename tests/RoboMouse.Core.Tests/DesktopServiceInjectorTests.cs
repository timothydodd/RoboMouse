using System.Collections.Concurrent;
using System.IO.Pipes;
using RoboMouse.Contracts;
using RoboMouse.Core.Input;
using Xunit;

namespace RoboMouse.Core.Tests;

/// <summary>The app side of the desktop-service pipe, against a stand-in for the service.</summary>
public class DesktopServiceInjectorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Records what fell back to in-process injection instead of calling SendInput.</summary>
    private sealed class RecordingInjector : IInputInjector
    {
        public ConcurrentQueue<string> Calls { get; } = new();
        public (int X, int Y) Position { get; set; } = (-1, -1);
        public bool ReachesSecureDesktop => false;
        public bool MoveRelative(int deltaX, int deltaY) { Calls.Enqueue($"move {deltaX},{deltaY}"); return true; }
        public void MoveTo(int x, int y) => Calls.Enqueue($"moveto {x},{y}");
        public (int X, int Y) GetCursorPosition() => Position;
        public bool SimulateMouseEvent(MouseEventType eventType, int wheelDelta = 0) { Calls.Enqueue($"mouse {eventType}"); return true; }
        public bool SimulateKeyboardEvent(Keys keyCode, uint scanCode, KeyboardEventType eventType, bool isExtended) { Calls.Enqueue($"key {keyCode} {eventType}"); return true; }
    }

    // Buffered like the real servers: on Windows an unbuffered pipe completes a write only when the
    // other end reads, and both ends open by writing Hello.
    private static NamedPipeServerStream CreateServer(string pipeName) =>
        new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            PipeNames.BufferSize, PipeNames.BufferSize);

    private static string NewPipeName() => "RoboMouse.Test." + Guid.NewGuid().ToString("N");

    // Nothing here may wait forever: a hung pipe must fail the test, not the whole CI run.
    private static async Task<PipeMessage> ReceiveAsync(PipeConnection pipe) =>
        (await pipe.ReceiveAsync(Ct).WaitAsync(Timeout, Ct))!.Value;

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(25, Ct);
        Assert.True(condition());
    }

    /// <summary>Accepts the injector, exchanges Hello and (optionally) reports the helper ready.</summary>
    private static async Task<PipeConnection> AcceptAsync(NamedPipeServerStream server, bool ready = true)
    {
        await server.WaitForConnectionAsync(Ct).WaitAsync(Timeout, Ct);
        var service = new PipeConnection(server);
        Assert.Equal(PipeOpcode.Hello, (await ReceiveAsync(service)).Opcode);
        await service.SendAsync(PipeMessage.Hello(), Ct);
        if (ready)
            await service.SendAsync(new PipeMessage(PipeOpcode.HelperReady), Ct);
        return service;
    }

    [Fact]
    public async Task RoutesInputInOrder_AndAnswersCursorQueries_OnceTheHelperIsReady()
    {
        var pipeName = NewPipeName();
        using var server = CreateServer(pipeName);
        var local = new RecordingInjector();
        using var injector = new DesktopServiceInjector(pipeName, local, null) { CursorQueryTimeoutMs = 10000, Enabled = true };

        await server.WaitForConnectionAsync(Ct).WaitAsync(Timeout, Ct);
        using var service = new PipeConnection(server);
        Assert.Equal(PipeOpcode.Hello, (await ReceiveAsync(service)).Opcode);

        await service.SendAsync(PipeMessage.Hello(), Ct);
        Assert.False(injector.ReachesSecureDesktop);
        await service.SendAsync(new PipeMessage(PipeOpcode.HelperReady), Ct);
        await WaitForAsync(() => injector.State == DesktopServiceState.Active);
        Assert.True(injector.ReachesSecureDesktop);

        Assert.True(injector.MoveRelative(5, -3));
        injector.MoveTo(100, 200);
        injector.SimulateKeyboardEvent(Keys.A, 0x1E, KeyboardEventType.KeyDown, false);
        var query = Task.Run(() => injector.GetCursorPosition(), Ct);

        var motion = (await ReceiveAsync(service));
        Assert.Equal(PipeOpcode.InjectMotion, motion.Opcode);
        Assert.Equal((5, -3), motion.ReadMotion());
        var moveTo = (await ReceiveAsync(service));
        Assert.Equal(PipeOpcode.MoveTo, moveTo.Opcode);
        Assert.Equal((100, 200), moveTo.ReadMotion());
        Assert.Equal(((int)Keys.A, 0x1Eu, (int)KeyboardEventType.KeyDown, false), (await ReceiveAsync(service)).ReadKey());
        var request = await ReceiveAsync(service);
        Assert.Equal(PipeOpcode.QueryCursor, request.Opcode);

        await service.SendAsync(PipeMessage.CursorPosition(105, 197, request.ReadQueryCursor()), Ct);
        Assert.Equal((105, 197), await query.WaitAsync(Timeout, Ct));
        Assert.Empty(local.Calls);
    }

    [Fact]
    public async Task LateCursorReply_DoesNotAnswerTheNextQuery()
    {
        var pipeName = NewPipeName();
        using var server = CreateServer(pipeName);
        var local = new RecordingInjector { Position = (1, 1) };
        using var injector = new DesktopServiceInjector(pipeName, local, null) { CursorQueryTimeoutMs = 200, Enabled = true };
        using var service = await AcceptAsync(server);
        await WaitForAsync(() => injector.State == DesktopServiceState.Active);

        // First query times out (no reply), falling back to the local position.
        Assert.Equal((1, 1), await Task.Run(() => injector.GetCursorPosition(), Ct).WaitAsync(Timeout, Ct));
        var first = await ReceiveAsync(service);

        injector.CursorQueryTimeoutMs = 10000;
        var second = Task.Run(() => injector.GetCursorPosition(), Ct);
        var secondRequest = await ReceiveAsync(service);
        Assert.NotEqual(first.ReadQueryCursor(), secondRequest.ReadQueryCursor());

        // The stale reply arrives first and must be ignored; only the matching one answers.
        await service.SendAsync(PipeMessage.CursorPosition(10, 10, first.ReadQueryCursor()), Ct);
        await service.SendAsync(PipeMessage.CursorPosition(20, 20, secondRequest.ReadQueryCursor()), Ct);
        Assert.Equal((20, 20), await second.WaitAsync(Timeout, Ct));
    }

    [Fact]
    public async Task RefusesAServiceWithADifferentProtocolVersion()
    {
        var pipeName = NewPipeName();
        using var server = CreateServer(pipeName);
        using var injector = new DesktopServiceInjector(pipeName, new RecordingInjector(), null) { Enabled = true };

        await server.WaitForConnectionAsync(Ct).WaitAsync(Timeout, Ct);
        using var service = new PipeConnection(server);
        Assert.Equal(PipeOpcode.Hello, (await ReceiveAsync(service)).Opcode);
        await service.SendAsync(new PipeMessage(PipeOpcode.Hello, BitConverter.GetBytes(PipeNames.ProtocolVersion + 1)), Ct);

        await WaitForAsync(() => injector.State == DesktopServiceState.Incompatible);
        Assert.False(injector.ReachesSecureDesktop);
    }

    [Fact]
    public async Task RejectsAPipeServerThatIsNotTheService_WithoutSendingAnything()
    {
        var pipeName = NewPipeName();
        using var server = CreateServer(pipeName);
        var checkedServer = new TaskCompletionSource();
        var local = new RecordingInjector();
        using var injector = new DesktopServiceInjector(pipeName, local, _ =>
        {
            checkedServer.TrySetResult();
            return "the pipe server is pid 1234, but the desktop service is pid 42";
        }) { Enabled = true };

        await server.WaitForConnectionAsync(Ct).WaitAsync(Timeout, Ct);
        await checkedServer.Task.WaitAsync(Timeout, Ct);
        using var squatter = new PipeConnection(server);

        // The injector hangs up without a Hello; its input keeps going in-process.
        Assert.Null(await squatter.ReceiveAsync(Ct).WaitAsync(Timeout, Ct));
        Assert.Equal(DesktopServiceState.Connecting, injector.State);
        Assert.False(injector.ReachesSecureDesktop);
        injector.SimulateKeyboardEvent(Keys.A, 0x1E, KeyboardEventType.KeyDown, false);
        Assert.Equal(new[] { "key A KeyDown" }, local.Calls);
    }

    [Fact]
    public async Task FallsBackInProcess_WhenTheHelperIsLost()
    {
        var pipeName = NewPipeName();
        using var server = CreateServer(pipeName);
        var local = new RecordingInjector();
        using var injector = new DesktopServiceInjector(pipeName, local, null) { Enabled = true };
        using var service = await AcceptAsync(server);
        await WaitForAsync(() => injector.State == DesktopServiceState.Active);

        await service.SendAsync(new PipeMessage(PipeOpcode.HelperLost), Ct);
        await WaitForAsync(() => !injector.ReachesSecureDesktop);
        Assert.Equal(DesktopServiceState.Connecting, injector.State);

        injector.SimulateMouseEvent(MouseEventType.LeftDown);
        Assert.Equal(new[] { "mouse LeftDown" }, local.Calls);

        // And back through the service once a new helper is ready.
        await service.SendAsync(new PipeMessage(PipeOpcode.HelperReady), Ct);
        await WaitForAsync(() => injector.State == DesktopServiceState.Active);
        injector.SimulateMouseEvent(MouseEventType.LeftUp);
        Assert.Equal(PipeOpcode.InjectButton, (await ReceiveAsync(service)).Opcode);
        Assert.Single(local.Calls);
    }

    [Fact]
    public async Task ReconnectsAfterTheServiceRestarts()
    {
        var pipeName = NewPipeName();
        using var injector = new DesktopServiceInjector(pipeName, new RecordingInjector(), null) { Enabled = true };

        using (var first = CreateServer(pipeName))
        {
            using var service = await AcceptAsync(first);
            await WaitForAsync(() => injector.State == DesktopServiceState.Active);
        }
        await WaitForAsync(() => injector.State == DesktopServiceState.Connecting);

        // The retry runs every few seconds; the restarted service gets a fresh Hello.
        using var second = CreateServer(pipeName);
        using var restarted = await AcceptAsync(second);
        await WaitForAsync(() => injector.State == DesktopServiceState.Active);
        Assert.True(injector.MoveRelative(1, 2));
        Assert.Equal((1, 2), (await ReceiveAsync(restarted)).ReadMotion());
    }
}
