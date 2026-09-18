using System.IO.Pipes;
using RoboMouse.Contracts;
using RoboMouse.Core.Input;
using Xunit;

namespace RoboMouse.Core.Tests;

/// <summary>The app side of the desktop-service pipe, against a stand-in for the service.</summary>
public class DesktopServiceInjectorTests
{
    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(25);
        Assert.True(condition());
    }

    [Fact]
    public async Task RoutesInputInOrder_AndAnswersCursorQueries_OnceTheHelperIsReady()
    {
        var pipeName = "RoboMouse.Test." + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var injector = new DesktopServiceInjector(pipeName) { CursorQueryTimeoutMs = 10000, Enabled = true };

        await server.WaitForConnectionAsync();
        using var service = new PipeConnection(server);
        Assert.Equal(PipeOpcode.Hello, (await service.ReceiveAsync())!.Value.Opcode);

        await service.SendAsync(PipeMessage.Hello());
        Assert.False(injector.ReachesSecureDesktop);
        await service.SendAsync(new PipeMessage(PipeOpcode.HelperReady));
        await WaitForAsync(() => injector.State == DesktopServiceState.Active);
        Assert.True(injector.ReachesSecureDesktop);

        Assert.True(injector.MoveRelative(5, -3));
        injector.MoveTo(100, 200);
        injector.SimulateKeyboardEvent(Keys.A, 0x1E, KeyboardEventType.KeyDown, false);
        var query = Task.Run(() => injector.GetCursorPosition());

        var motion = (await service.ReceiveAsync())!.Value;
        Assert.Equal(PipeOpcode.InjectMotion, motion.Opcode);
        Assert.Equal((5, -3), motion.ReadMotion());
        var moveTo = (await service.ReceiveAsync())!.Value;
        Assert.Equal(PipeOpcode.MoveTo, moveTo.Opcode);
        Assert.Equal((100, 200), moveTo.ReadMotion());
        Assert.Equal(((int)Keys.A, 0x1Eu, (int)KeyboardEventType.KeyDown, false), (await service.ReceiveAsync())!.Value.ReadKey());
        Assert.Equal(PipeOpcode.QueryCursor, (await service.ReceiveAsync())!.Value.Opcode);

        await service.SendAsync(PipeMessage.Motion(PipeOpcode.CursorPosition, 105, 197));
        Assert.Equal((105, 197), await query);
    }

    [Fact]
    public async Task RefusesAServiceWithADifferentProtocolVersion()
    {
        var pipeName = "RoboMouse.Test." + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var injector = new DesktopServiceInjector(pipeName) { Enabled = true };

        await server.WaitForConnectionAsync();
        using var service = new PipeConnection(server);
        await service.SendAsync(new PipeMessage(PipeOpcode.Hello, BitConverter.GetBytes(PipeNames.ProtocolVersion + 1)));

        await WaitForAsync(() => injector.State == DesktopServiceState.Incompatible);
        Assert.False(injector.ReachesSecureDesktop);
    }
}
