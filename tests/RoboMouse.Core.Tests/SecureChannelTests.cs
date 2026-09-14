using System.Net;
using System.Net.Sockets;
using RoboMouse.Core.Network;
using Xunit;

namespace RoboMouse.Core.Tests;

public class SecureChannelTests
{
    private static readonly byte[] Key = SecureChannel.DerivePairingKey("K7QM-4XDP-9RLA");

    private static async Task<(SecureChannel Client, SecureChannel Server)> PairAsync(byte[] clientKey, byte[] serverKey)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var clientTask = Task.Run(async () =>
        {
            var c = new TcpClient();
            await c.ConnectAsync(IPAddress.Loopback, port);
            return c;
        });
        var server = await listener.AcceptTcpClientAsync();
        var client = await clientTask;
        listener.Stop();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverSide = SecureChannel.AcceptAsync(server.GetStream(), serverKey, cts.Token);
        var clientSide = SecureChannel.ConnectAsync(client.GetStream(), clientKey, cts.Token);
        await Task.WhenAll(serverSide, clientSide);
        return (clientSide.Result, serverSide.Result);
    }

    private static byte[] ReadExactly(Stream stream, int count)
    {
        var buffer = new byte[count];
        var got = 0;
        while (got < count)
        {
            var read = stream.Read(buffer, got, count - got);
            if (read == 0) break;
            got += read;
        }
        return buffer;
    }

    [Fact]
    public void PairingKey_IgnoresCaseSeparatorsAndSpaces()
    {
        Assert.Equal(Key, SecureChannel.DerivePairingKey("k7qm 4xdp 9rla"));
        Assert.NotEqual(Key, SecureChannel.DerivePairingKey("K7QM-4XDP-9RLB"));
    }

    [Fact]
    public void GeneratePairingCode_HasExpectedShape()
    {
        var code = SecureChannel.GeneratePairingCode();
        Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}$", code);
        Assert.NotEqual(code, SecureChannel.GeneratePairingCode());
    }

    [Fact]
    public async Task RoundTrip_BothDirections_PreservesData()
    {
        var (client, server) = await PairAsync(Key, Key);
        using (client)
        using (server)
        {
            var payload = new byte[100_000];
            new Random(1).NextBytes(payload);

            var writer = Task.Run(() =>
            {
                client.Write(payload, 0, payload.Length);
                client.Write(payload, 0, 10);
            }, TestContext.Current.CancellationToken);
            var received = ReadExactly(server, payload.Length + 10);
            await writer;

            Assert.Equal(payload, received[..payload.Length]);
            Assert.Equal(payload[..10], received[payload.Length..]);

            server.Write(new byte[] { 1, 2, 3 }, 0, 3);
            Assert.Equal(new byte[] { 1, 2, 3 }, ReadExactly(client, 3));
        }
    }

    [Fact]
    public async Task Handshake_RejectsMismatchedPairingCode()
    {
        var wrong = SecureChannel.DerivePairingKey("WRONG-CODE-1234");
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => PairAsync(Key, wrong));
        Assert.IsType<PairingException>(ex.GetBaseException());
    }

    [Fact]
    public async Task TamperedFrame_FailsAuthentication()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var clientTask = Task.Run(async () =>
        {
            var c = new TcpClient();
            await c.ConnectAsync(IPAddress.Loopback, port);
            return c;
        });
        var serverTcp = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
        var clientTcp = await clientTask;
        listener.Stop();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var tamper = new TamperStream(serverTcp.GetStream());
        var serverTask = SecureChannel.AcceptAsync(tamper, Key, cts.Token);
        using var client = await SecureChannel.ConnectAsync(clientTcp.GetStream(), Key, cts.Token);
        using var server = await serverTask;

        tamper.Tamper = true;
        client.Write(new byte[] { 9, 9, 9, 9 }, 0, 4);

        Assert.Throws<InvalidDataException>(() => server.Read(new byte[4], 0, 4));
    }

    /// <summary>Flips the last byte of every read once enabled, simulating a man in the middle.</summary>
    private sealed class TamperStream : Stream
    {
        private readonly Stream _inner;
        public bool Tamper;
        public TamperStream(Stream inner) => _inner = inner;

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            if (Tamper && read > 12) buffer[offset + read - 1] ^= 1;
            return read;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _inner.ReadAsync(buffer, ct);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => _inner.WriteAsync(buffer, ct);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
