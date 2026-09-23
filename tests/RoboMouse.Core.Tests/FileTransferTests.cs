using System.Net;
using System.Net.Sockets;
using RoboMouse.Core.Network;
using RoboMouse.Core.Network.Protocol;
using Xunit;

namespace RoboMouse.Core.Tests;

public class FileTransferTests
{
    private static readonly byte[] Key = SecureChannel.DerivePairingKey("K7QM-4XDP-9RLA");

    [Fact]
    public async Task Fetch_IgnoresAChunkThatAnswersAnotherRequest()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        // The server first sends a late reply to some earlier request, then the real one.
        var serverTask = Task.Run(async () =>
        {
            var tcp = await listener.AcceptTcpClientAsync();
            var server = await PeerConnection.AcceptAsync(tcp, Key, "server", "SERVER", 1, 1, port);
            server.MessageReceived += (s, m) =>
            {
                if (m is not FileRequestMessage request)
                    return;
                server.Post(new FileChunkMessage { OfferId = request.OfferId, EntryIndex = request.EntryIndex, Offset = request.Offset - 4, Data = new byte[] { 9, 9, 9, 9 } });
                server.Post(new FileChunkMessage { OfferId = "other", EntryIndex = request.EntryIndex, Offset = request.Offset, Data = new byte[] { 8 } });
                server.Post(new FileChunkMessage { OfferId = request.OfferId, EntryIndex = request.EntryIndex, Offset = request.Offset, Data = new byte[] { 1, 2, 3 } });
            };
            server.Start();
            return server;
        }, TestContext.Current.CancellationToken);

        using var client = new FileTransferClient("SERVER", ct => PeerConnection.ConnectAsync(
            "127.0.0.1", port, Key, "client", "CLIENT", 1, 1, 0, ct, ConnectionKind.Transfer));

        var data = await Task.Run(() => client.Fetch("offer", 0, 4, 100), TestContext.Current.CancellationToken);

        Assert.Equal(new byte[] { 1, 2, 3 }, data);
        using var server = await serverTask;
        listener.Stop();
    }

    [Fact]
    public async Task SerialWorkQueue_RunsInOrderOneAtATime_AndCapsTheBacklog()
    {
        var queue = new SerialWorkQueue(maxPending: 3);
        var gate = new ManualResetEventSlim(false);
        var order = new List<int>();
        var running = 0;
        var maxRunning = 0;
        var done = new TaskCompletionSource();

        void Work(int n)
        {
            var now = Interlocked.Increment(ref running);
            lock (order)
            {
                maxRunning = Math.Max(maxRunning, now);
                order.Add(n);
            }
            if (n == 0)
                gate.Wait(TimeSpan.FromSeconds(5));
            Interlocked.Decrement(ref running);
            if (n == 3)
                done.TrySetResult();
        }

        Assert.True(queue.TryEnqueue(() => Work(0)));
        // Give the first item time to start so the next three wait behind it.
        for (var i = 0; i < 100 && Volatile.Read(ref running) == 0; i++)
            await Task.Delay(10, TestContext.Current.CancellationToken);

        Assert.True(queue.TryEnqueue(() => Work(1)));
        Assert.True(queue.TryEnqueue(() => Work(2)));
        Assert.True(queue.TryEnqueue(() => Work(3)));
        Assert.False(queue.TryEnqueue(() => Work(4)));

        gate.Set();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(new[] { 0, 1, 2, 3 }, order);
        Assert.Equal(1, maxRunning);
    }
}
