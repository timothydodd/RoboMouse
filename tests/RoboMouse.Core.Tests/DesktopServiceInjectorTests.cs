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

    // Buffered like the real servers: on Windows an unbuffered pipe completes a write only when the
    // other end reads, and both ends open by writing Hello.
    private static NamedPipeServerStream CreateServer(string pipeName) =>
        new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            PipeNames.BufferSize, PipeNames.BufferSize);

    // Nothing here may wait forever: a hung pipe must fail the test, not the whole CI run.
    private static async Task<PipeMessage> ReceiveAsync(PipeConnection pipe) =>
        (await pipe.ReceiveAsync(Ct).WaitAsync(Timeout, Ct))!.Value;

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(25, Ct);
        Assert.True(condition());
    }

    [Fact]
    public async Task RoutesInputInOrder_AndAnswersCursorQueries_OnceTheHelperIsReady()
    {
        var pipeName = "RoboMouse.Test." + Guid.NewGuid().ToString("N");
        using var server = CreateServer(pipeName);
        using var injector = new DesktopServiceInjector(pipeName) { CursorQueryTimeoutMs = 10000, Enabled = true };

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
        Assert.Equal(PipeOpcode.QueryCursor, (await ReceiveAsync(service)).Opcode);

        await service.SendAsync(PipeMessage.Motion(PipeOpcode.CursorPosition, 105, 197), Ct);
        Assert.Equal((105, 197), await query.WaitAsync(Timeout, Ct));
    }

    [Fact]
    public async Task RefusesAServiceWithADifferentProtocolVersion()
    {
        var pipeName = "RoboMouse.Test." + Guid.NewGuid().ToString("N");
        using var server = CreateServer(pipeName);
        using var injector = new DesktopServiceInjector(pipeName) { Enabled = true };

        await server.WaitForConnectionAsync(Ct).WaitAsync(Timeout, Ct);
        using var service = new PipeConnection(server);
        Assert.Equal(PipeOpcode.Hello, (await ReceiveAsync(service)).Opcode);
        await service.SendAsync(new PipeMessage(PipeOpcode.Hello, BitConverter.GetBytes(PipeNames.ProtocolVersion + 1)), Ct);

        await WaitForAsync(() => injector.State == DesktopServiceState.Incompatible);
        Assert.False(injector.ReachesSecureDesktop);
    }
}
