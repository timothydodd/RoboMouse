using System.IO.Pipes;
using Xunit;

namespace RoboMouse.Service.Tests;

/// <summary>The control pipe's name must stay the service's for as long as it runs.</summary>
public class ControlPipeServerTests
{
    [Fact]
    public async Task PipeName_IsNeverFree_BetweenConnections()
    {
        var name = "RoboMouse.Tests." + Guid.NewGuid().ToString("N");
        NamedPipeServerStream Create(bool first) => new(name, PipeDirection.InOut, 2, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None));

        // Every client is refused (this test process is not the app), so each connection ends in a handoff.
        var policy = new CallerPolicy(@"C:\Nowhere\RoboMouse.App.exe", null, (SignerIdentity?)null,
            new Win32ProcessInspector(), new AuthenticodeReader());
        using var server = new ControlPipeServer(policy, Create);
        server.Start();

        Assert.True(await ConnectAndWaitForDropAsync(name, TimeSpan.FromSeconds(5)), "the server never listened");

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var squatted = 0;
        var squatter = Task.Run(() =>
        {
            // Another process trying to claim the name the moment it is free.
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var claim = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.FirstPipeInstance);
                    Interlocked.Increment(ref squatted);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }, TestContext.Current.CancellationToken);

        var connections = 0;
        while (!stop.IsCancellationRequested)
        {
            if (await ConnectAndWaitForDropAsync(name, TimeSpan.FromSeconds(1)))
                connections++;
        }
        await squatter;

        Assert.True(connections >= 5, $"only {connections} connections were served");
        Assert.Equal(0, squatted);
    }

    /// <summary>Connects a client and waits until the server hangs up on it; false when it could not connect.</summary>
    private static async Task<bool> ConnectAndWaitForDropAsync(string name, TimeSpan timeout)
    {
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await client.ConnectAsync(cts.Token);
            var buffer = new byte[64];
            while (await client.ReadAsync(buffer, cts.Token) > 0) { }
            return true;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException)
        {
            return client.IsConnected;
        }
    }
}
