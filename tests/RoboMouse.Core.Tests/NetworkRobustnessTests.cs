using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RoboMouse.Core.Logging;
using RoboMouse.Core.Network;
using RoboMouse.Core.Network.Protocol;
using Xunit;

namespace RoboMouse.Core.Tests;

public class NetworkRobustnessTests
{
    private static readonly byte[] Key = SecureChannel.DerivePairingKey("K7QM-4XDP-9RLA");

    /// <summary>A listener on a free loopback port.</summary>
    private static (TcpListener Listener, int Port) Listen()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return (listener, ((IPEndPoint)listener.LocalEndpoint).Port);
    }

    [Fact]
    public async Task SecureChannel_ConnectToSilentServer_ReturnsWhenCancelled()
    {
        var (listener, port) = Listen();
        using var _ = listener;
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
        using var server = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SecureChannel.ConnectAsync(client.GetStream(), Key, cts.Token));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task PeerConnection_ServerSilentAfterSecureHandshake_TimesOut()
    {
        var (listener, port) = Listen();
        using var _ = listener;

        // The server completes the encrypted handshake and then never answers the handshake message:
        // the read that waits for the ack blocks inside the encrypted stream.
        var serverTask = Task.Run(async () =>
        {
            var tcp = await listener.AcceptTcpClientAsync();
            var channel = await SecureChannel.AcceptAsync(tcp.GetStream(), Key, CancellationToken.None);
            return (tcp, channel);
        }, TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PeerConnection.ConnectAsync(
            "127.0.0.1", port, Key, "client", "CLIENT", 1920, 1080, 0, cts.Token));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"took {sw.Elapsed}");

        var (tcp, channel) = await serverTask;
        channel.Dispose();
        tcp.Dispose();
    }

    private static async Task<Exception?> ConnectThroughPolicyAsync(string clientId, string serverId, Func<HandshakeMessage, IPEndPoint?, string?>? decide)
    {
        var (listener, port) = Listen();
        using var _ = listener;

        var serverTask = Task.Run(async () =>
        {
            var tcp = await listener.AcceptTcpClientAsync();
            try
            {
                using var accepted = await PeerConnection.AcceptAsync(tcp, Key, serverId, "SERVER", 1920, 1080, port, CancellationToken.None, decide);
            }
            catch (ConnectionRejectedException)
            {
            }
        });

        Exception? failure = null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var connection = await PeerConnection.ConnectAsync("127.0.0.1", port, Key, clientId, "CLIENT", 1920, 1080, 0, cts.Token);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        await serverTask;
        return failure;
    }

    [Fact]
    public async Task AcceptPolicy_Rejection_ReachesTheConnectingSideWithItsCode()
    {
        var failure = await ConnectThroughPolicyAsync("client", "server",
            (hs, ep) => hs.MachineId == "client" ? RejectReasons.Format(RejectCode.AwaitingApproval, "Waiting for approval on SERVER.") : null);

        var rejected = Assert.IsType<ConnectionRejectedException>(failure);
        Assert.Equal(RejectCode.AwaitingApproval, rejected.Code);
        Assert.Equal("Waiting for approval on SERVER.", rejected.Message);
    }

    [Fact]
    public async Task AcceptPolicy_Accepts_WhenItReturnsNull()
    {
        Assert.Null(await ConnectThroughPolicyAsync("client", "server", (hs, ep) => null));
    }

    [Fact]
    public async Task ConnectingToOwnMachineId_IsRejected()
    {
        var failure = await ConnectThroughPolicyAsync("same", "same", null);

        Assert.Equal(RejectCode.SameMachine, Assert.IsType<ConnectionRejectedException>(failure).Code);
    }

    [Fact]
    public void RejectReasons_RoundTripAndPassUnknownTextThrough()
    {
        foreach (var code in Enum.GetValues<RejectCode>().Where(c => c != RejectCode.Other))
            Assert.Equal((code, "why"), RejectReasons.Parse(RejectReasons.Format(code, "why")));

        Assert.Equal((RejectCode.Other, "an old: reason"), RejectReasons.Parse("an old: reason"));
        Assert.Equal(RejectCode.Other, RejectReasons.Parse(null).Code);
    }

    [Fact]
    public async Task SecureChannel_LargeWrite_IsSplitAndReassembled()
    {
        var (listener, port) = Listen();
        using var _ = listener;
        using var clientTcp = new TcpClient();
        await clientTcp.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
        using var serverTcp = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverSide = SecureChannel.AcceptAsync(serverTcp.GetStream(), Key, cts.Token);
        using var client = await SecureChannel.ConnectAsync(clientTcp.GetStream(), Key, cts.Token);
        using var server = await serverSide;

        var payload = new byte[SecureChannel.MaxWriteBytes * 3 + 123];
        new Random(7).NextBytes(payload);
        var writer = Task.Run(() => client.Write(payload, 0, payload.Length), TestContext.Current.CancellationToken);

        var received = new byte[payload.Length];
        var got = 0;
        var reads = 0;
        while (got < received.Length)
        {
            var read = server.Read(received, got, received.Length - got);
            Assert.True(read > 0);
            // One frame per read at most: never more than a frame's worth at once.
            Assert.True(read <= SecureChannel.MaxWriteBytes);
            got += read;
            reads++;
        }
        await writer;

        Assert.Equal(payload, received);
        Assert.True(reads >= 4);
    }

    [Fact]
    public void HandshakeGate_LimitsPerAddressAndInTotal()
    {
        var gate = new HandshakeGate(maxTotal: 3, maxPerAddress: 2);
        var a = IPAddress.Parse("10.0.0.1");
        var b = IPAddress.Parse("10.0.0.2");

        Assert.True(gate.TryEnter(a));
        Assert.True(gate.TryEnter(a));
        Assert.False(gate.TryEnter(a));
        Assert.True(gate.TryEnter(b));
        Assert.False(gate.TryEnter(b)); // total is full

        gate.Exit(a);
        Assert.True(gate.TryEnter(b));
        Assert.Equal(3, gate.InFlight);

        gate.Exit(IPAddress.Parse("10.0.0.9")); // never entered: ignored
        Assert.Equal(3, gate.InFlight);
    }

    [Fact]
    public void LogThrottle_LetsOneThroughPerIntervalAndCountsTheRest()
    {
        long now = 0;
        var throttle = new LogThrottle(TimeSpan.FromSeconds(60), () => now);

        Assert.True(throttle.ShouldLog("x", out var skipped));
        Assert.Equal(0, skipped);
        Assert.False(throttle.ShouldLog("x", out _));
        Assert.False(throttle.ShouldLog("x", out _));
        Assert.True(throttle.ShouldLog("y", out _));

        now = 60_000;
        Assert.True(throttle.ShouldLog("x", out skipped));
        Assert.Equal(2, skipped);
    }

    [Fact]
    public void Discovery_KeepsAtMostMaxPeers()
    {
        using var discovery = new PeerDiscovery(0, 24800, "me", "ME", 1920, 1080);
        for (var i = 0; i < PeerDiscovery.MaxPeers + 20; i++)
        {
            using var other = new PeerDiscovery(0, 24800, $"id-{i}", $"PC-{i}", 1920, 1080);
            discovery.ProcessDiscoveryMessage(other.CreateDiscoveryMessage(), new IPEndPoint(IPAddress.Loopback, 24801));
        }

        Assert.Equal(PeerDiscovery.MaxPeers, discovery.Peers.Count);
        Assert.Contains(discovery.Peers, p => p.MachineId == $"id-{PeerDiscovery.MaxPeers + 19}");
    }
}
