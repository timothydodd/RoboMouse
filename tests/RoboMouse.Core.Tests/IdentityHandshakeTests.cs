using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using RoboMouse.Core.Configuration;
using RoboMouse.Core.Network;
using RoboMouse.Core.Network.Protocol;
using Xunit;

namespace RoboMouse.Core.Tests;

/// <summary>The protocol 5 secure handshake: pairing mode, pinned identities, tampering and version mismatches.</summary>
public class IdentityHandshakeTests
{
    private static readonly byte[] Code = SecureChannel.DerivePairingKey("K7QM-4XDP-9RLA");
    private static readonly byte[] OtherCode = SecureChannel.DerivePairingKey("ZZZZ-4XDP-9RLA");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(Stream Client, Stream Server, IDisposable Cleanup)> SocketPairAsync()
    {
        var (client, server) = await TcpPairAsync();
        return (client.GetStream(), server.GetStream(), new Both(client, server));
    }

    private static async Task<(TcpClient Client, TcpClient Server)> TcpPairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var client = new TcpClient();
        var connect = client.ConnectAsync(IPAddress.Loopback, port, Ct);
        var server = await listener.AcceptTcpClientAsync(Ct);
        await connect;
        listener.Stop();
        return (client, server);
    }

    private sealed class Both(IDisposable a, IDisposable b) : IDisposable
    {
        public void Dispose() { a.Dispose(); b.Dispose(); }
    }

    /// <summary>
    /// Runs both sides; returns each side's channel or exception, and the sockets to dispose. A side
    /// that fails closes its own socket, as the real caller does, so the other side fails too instead
    /// of waiting.
    /// </summary>
    private static async Task<(object Client, object Server, IDisposable Cleanup)> HandshakeAsync(
        ChannelCredentials client, byte[]? expected, ChannelCredentials server,
        Func<Stream, Stream>? wrapClient = null, Func<Stream, Stream>? wrapServer = null)
    {
        var (c, s) = await TcpPairAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Capture(() => SecureChannel.AcceptAsync(wrapServer?.Invoke(s.GetStream()) ?? s.GetStream(), server, cts.Token), s);
        var clientTask = Capture(() => SecureChannel.ConnectAsync(wrapClient?.Invoke(c.GetStream()) ?? c.GetStream(), client, expected, cts.Token), c);
        return (await clientTask, await serverTask, new Both(c, s));
    }

    private static async Task<object> CaptureAsync(Func<Task<SecureChannel>> run)
    {
        try { return await run(); }
        catch (Exception ex) { return ex; }
    }

    private static async Task<object> Capture(Func<Task<SecureChannel>> run, TcpClient own)
    {
        try { return await run(); }
        catch (Exception ex) { own.Dispose(); return ex; }
    }

    [Fact]
    public async Task Pairing_BothSidesLearnEachOthersIdentity()
    {
        using var a = IdentityKey.Create();
        using var b = IdentityKey.Create();

        var (client, server, cleanup) = await HandshakeAsync(new ChannelCredentials(Code, a), null, new ChannelCredentials(Code, b));
        using var _cleanup = cleanup;

        var c = Assert.IsType<SecureChannel>(client);
        var s = Assert.IsType<SecureChannel>(server);
        Assert.Equal(b.PublicKey, c.PeerIdentityKey);
        Assert.Equal(a.PublicKey, s.PeerIdentityKey);
        Assert.True(c.PairedWithCode);
        Assert.True(s.PairedWithCode);

        // And the channel works.
        c.Write(new byte[] { 1, 2, 3 }, 0, 3);
        var buffer = new byte[3];
        Assert.Equal(3, s.Read(buffer, 0, 3));
        Assert.Equal(new byte[] { 1, 2, 3 }, buffer);
    }

    [Fact]
    public async Task Pairing_WrongCode_FailsOnBothSides()
    {
        var (client, server, cleanup) = await HandshakeAsync(ChannelCredentials.Ephemeral(Code), null, ChannelCredentials.Ephemeral(OtherCode));
        using var _cleanup = cleanup;

        Assert.IsType<PairingException>(client);
        Assert.IsType<PairingException>(server);
    }

    [Fact]
    public async Task Pinned_NeedsNoPairingCode()
    {
        using var a = IdentityKey.Create();
        using var b = IdentityKey.Create();
        var server = new ChannelCredentials(OtherCode, b, key => key.AsSpan().SequenceEqual(a.PublicKey));

        var (client, accepted, cleanup) = await HandshakeAsync(new ChannelCredentials(Code, a), b.PublicKey, server);
        using var _cleanup = cleanup;

        Assert.False(Assert.IsType<SecureChannel>(client).PairedWithCode);
        Assert.False(Assert.IsType<SecureChannel>(accepted).PairedWithCode);
    }

    [Fact]
    public async Task Pinned_ServerWithAnotherIdentity_IsRefusedByTheClient()
    {
        using var a = IdentityKey.Create();
        using var pinned = IdentityKey.Create();
        using var impostor = IdentityKey.Create();

        // The impostor knows the code and would even accept pinned mode; the client still refuses it.
        var (client, _, cleanup) = await HandshakeAsync(new ChannelCredentials(Code, a), pinned.PublicKey,
            new ChannelCredentials(Code, impostor, _ => true));
        using var _cleanup = cleanup;

        Assert.IsType<IdentityMismatchException>(client);
    }

    [Fact]
    public async Task Pinned_ServerThatForgotUs_FallsBackToTheCode()
    {
        using var a = IdentityKey.Create();
        using var b = IdentityKey.Create();

        var (client, server, cleanup) = await HandshakeAsync(new ChannelCredentials(Code, a), b.PublicKey,
            new ChannelCredentials(Code, b, _ => false));
        using var _cleanup = cleanup;
        Assert.True(Assert.IsType<SecureChannel>(client).PairedWithCode);
        Assert.IsType<SecureChannel>(server);

        var (client2, _, cleanup2) = await HandshakeAsync(new ChannelCredentials(Code, a), b.PublicKey,
            new ChannelCredentials(OtherCode, b, _ => false));
        using var _cleanup2 = cleanup2;
        Assert.IsType<PairingException>(client2);
    }

    [Fact]
    public async Task ClientNotAskingForPinned_StillPairsWithTheCode()
    {
        using var b = IdentityKey.Create();
        var (client, _, cleanup) = await HandshakeAsync(ChannelCredentials.Ephemeral(Code), null, new ChannelCredentials(Code, b, _ => true));
        using var _cleanup = cleanup;

        Assert.True(Assert.IsType<SecureChannel>(client).PairedWithCode);
    }

    [Theory]
    [InlineData(0, 10)]  // client hello: the nonce
    [InlineData(1, 10)]  // server hello: the nonce
    [InlineData(1, 60)]  // server hello: the ephemeral key
    public async Task TamperedHandshake_Fails(int blob, int offset)
    {
        var (client, server, cleanup) = await HandshakeAsync(ChannelCredentials.Ephemeral(Code), null, ChannelCredentials.Ephemeral(Code),
            wrapClient: blob == 0 ? inner => new BlobTamperStream(inner, 0, offset) : null,
            wrapServer: blob == 1 ? inner => new BlobTamperStream(inner, 0, offset) : null);
        using var _cleanup = cleanup;

        Assert.True(client is Exception || server is Exception, "a tampered handshake must not succeed on both sides");
        Assert.False(client is SecureChannel && server is SecureChannel);
    }

    [Fact]
    public async Task OldVersionHello_GetsAVersionReply_AndAClearError()
    {
        var (c, s, cleanup) = await SocketPairAsync();
        using var _ = cleanup;

        // What a protocol 4 client sends first: version 1, nonce, public key.
        var hello = new byte[1 + 32 + 2 + 91];
        hello[0] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(hello.AsSpan(33), 91);
        var serverTask = CaptureAsync(() => SecureChannel.AcceptAsync(s, ChannelCredentials.Ephemeral(Code), Ct));
        await WriteBlobAsync(c, hello);

        var reply = await ReadBlobAsync(c);
        Assert.Equal(new[] { SecureChannel.HandshakeVersion }, reply);
        var error = Assert.IsType<IncompatibleVersionException>(await serverTask);
        Assert.Contains("Update RoboMouse on both machines", error.Message);
    }

    [Fact]
    public async Task ServerThatHangsUpAfterOurHello_IsReportedAsAVersionProblem()
    {
        var (c, s, cleanup) = await SocketPairAsync();
        using var _ = cleanup;

        var clientTask = CaptureAsync(() => SecureChannel.ConnectAsync(c, ChannelCredentials.Ephemeral(Code), null, Ct));
        await ReadBlobAsync(s);
        s.Dispose(); // what a protocol 4 listener does with a hello it cannot read

        var error = Assert.IsType<IncompatibleVersionException>(await clientTask);
        Assert.Contains("Update RoboMouse on both machines", error.Message);
        Assert.Equal(PeerFailureKind.VersionMismatch, PeerConnectFailure.Classify(error).Kind);
    }

    [Fact]
    public void PairingKey_SaltNamesTheProtocolVersion()
    {
        var v4 = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes("K7QM4XDP9RLA"), Encoding.UTF8.GetBytes("RoboMouse pairing v1"),
            120_000, HashAlgorithmName.SHA256, 32);
        Assert.NotEqual(v4, Code);
        Assert.Equal(Code, SecureChannel.DerivePairingKey(" k7qm-4xdp 9rla "));
    }

    [Fact]
    public void IdentityMismatch_IsClassified()
    {
        Assert.Equal(PeerFailureKind.IdentityMismatch, PeerConnectFailure.Classify(new IdentityMismatchException("x")).Kind);
        Assert.Equal(PeerFailureKind.IdentityMismatch,
            PeerConnectFailure.Classify(new ConnectionRejectedException(RejectCode.IdentityMismatch, "x")).Kind);
    }

    [Fact]
    public async Task PeerConnection_PolicyIdentityRejection_ReachesTheClientAsIdentityMismatch()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var serverKey = IdentityKey.Create();
        using var clientKey = IdentityKey.Create();
        byte[]? seen = null;

        var serverTask = Task.Run(async () =>
        {
            var tcp = await listener.AcceptTcpClientAsync();
            try
            {
                using var accepted = await PeerConnection.AcceptAsync(tcp, new ChannelCredentials(Code, serverKey), "server", "SERVER", 1, 1, port,
                    CancellationToken.None, incoming =>
                    {
                        seen = incoming.IdentityKey;
                        return AcceptPolicy.RejectReason(AcceptDecision.RejectIdentityMismatch, incoming.Handshake.Kind, "SERVER");
                    });
            }
            catch (ConnectionRejectedException)
            {
            }
        }, Ct);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<IdentityMismatchException>(() => PeerConnection.ConnectAsync(
            "127.0.0.1", port, new ChannelCredentials(Code, clientKey), "client", "CLIENT", 1, 1, 0, cts.Token));
        await serverTask;
        listener.Stop();

        Assert.Equal(clientKey.PublicKey, seen);
    }

    [Fact]
    public async Task PeerConnection_ExposesTheProvedIdentity()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var serverKey = IdentityKey.Create();

        var serverTask = Task.Run(async () =>
        {
            var tcp = await listener.AcceptTcpClientAsync();
            return await PeerConnection.AcceptAsync(tcp, new ChannelCredentials(Code, serverKey), "server", "SERVER", 1, 1, port);
        }, Ct);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var connection = await PeerConnection.ConnectAsync("127.0.0.1", port, ChannelCredentials.Ephemeral(Code), "client", "CLIENT", 1, 1, 0, cts.Token);
        using var server = await serverTask;
        listener.Stop();

        Assert.Equal(serverKey.PublicKey, connection.PeerIdentityKey);
        Assert.True(connection.PairedWithCode);
    }

    private static async Task WriteBlobAsync(Stream stream, byte[] data)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
        await stream.WriteAsync(header, Ct);
        await stream.WriteAsync(data, Ct);
    }

    private static async Task<byte[]> ReadBlobAsync(Stream stream)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, Ct);
        var data = new byte[BinaryPrimitives.ReadInt32LittleEndian(header)];
        await stream.ReadExactlyAsync(data, Ct);
        return data;
    }

    /// <summary>Flips one byte of the n-th length-prefixed blob this side writes: a man in the middle.</summary>
    private sealed class BlobTamperStream(Stream inner, int blob, int offset) : Stream
    {
        private int _written; // blobs fully written so far (each is a 4-byte header write and a body write)
        private bool _nextIsBody;

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (_nextIsBody && _written == blob && buffer.Length > offset)
            {
                var copy = buffer.ToArray();
                copy[offset] ^= 0x40;
                buffer = copy;
            }
            if (_nextIsBody)
                _written++;
            _nextIsBody = !_nextIsBody;
            return inner.WriteAsync(buffer, ct);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}

/// <summary>Pairing codes, identity keys and the identity rules of the accept policy.</summary>
public class IdentityKeyTests
{
    [Fact]
    public void GeneratedCodes_AreStrong_HandTypedOnesAreNot()
    {
        for (var i = 0; i < 50; i++)
            Assert.True(PairingCode.IsStrong(PairingCode.Generate()));

        Assert.True(PairingCode.IsStrong("k7qm 4xdp 9rla"));
        Assert.False(PairingCode.IsStrong("hello"));
        Assert.False(PairingCode.IsStrong("1234-5678-9012")); // 0 and 1 are never generated
        Assert.False(PairingCode.IsStrong("K7QM-4XDP-9RL"));
        Assert.False(PairingCode.IsStrong(null));
        Assert.True(PairingCode.AreEqual("K7QM-4XDP-9RLA", "k7qm4xdp9rla"));
    }

    [Fact]
    public void Signature_VerifiesOnlyForTheKeyAndData()
    {
        using var key = IdentityKey.Create();
        using var other = IdentityKey.Create();
        var data = "transcript"u8.ToArray();
        var signature = key.Sign(data);

        Assert.True(IdentityKey.Verify(key.PublicKey, data, signature));
        Assert.False(IdentityKey.Verify(other.PublicKey, data, signature));
        Assert.False(IdentityKey.Verify(key.PublicKey, "transcripT"u8, signature));
        Assert.False(IdentityKey.Verify(new byte[] { 1, 2, 3 }, data, signature));
        Assert.Matches("^[0-9A-F]{4}(-[0-9A-F]{4}){4}$", key.Fingerprint);
    }

    /// <summary>Stands in for DPAPI (Windows only).</summary>
    private sealed class XorProtector : IKeyProtector
    {
        public byte[] Protect(byte[] data) => data.Select(b => (byte)(b ^ 0x5A)).Prepend((byte)0xA5).ToArray();

        public byte[] Unprotect(byte[] data) => data.Length > 0 && data[0] == 0xA5
            ? data.Skip(1).Select(b => (byte)(b ^ 0x5A)).ToArray()
            : throw new CryptographicException("not ours");
    }

    [Fact]
    public void LoadOrCreate_KeepsTheSameKey_AndReplacesAnUnreadableOne()
    {
        var dir = Path.Combine(Path.GetTempPath(), "robomouse-identity-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "identity.key");
        try
        {
            using var first = IdentityKey.LoadOrCreate(path, new XorProtector());
            using var again = IdentityKey.LoadOrCreate(path, new XorProtector());
            Assert.Equal(first.PublicKey, again.PublicKey);

            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
            using var replaced = IdentityKey.LoadOrCreate(path, new XorProtector());
            Assert.NotEqual(first.PublicKey, replaced.PublicKey);
            Assert.Single(Directory.GetFiles(dir, "identity.key.unreadable-*"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static AppSettings Settings() => new()
    {
        MachineId = "me",
        Peers =
        {
            new PeerConfig { Id = "work", Name = "TIM-WORK", Address = "10.0.0.2", IdentityKey = "S0VZLVdPUks=" },
            new PeerConfig { Id = "new", Name = "NEW-PC", Address = "10.0.0.5" }
        }
    };

    private static AcceptDecision Decide(string id, string key, bool pairedWithCode = true) =>
        AcceptPolicy.Decide(Settings(), id, ConnectionKind.Control, IPAddress.Parse("10.0.0.9"), 24800, false, out _, key, pairedWithCode);

    [Fact]
    public void PinnedPeer_MustProveItsKey()
    {
        Assert.Equal(AcceptDecision.Accept, Decide("work", "S0VZLVdPUks=", pairedWithCode: false));
        Assert.Equal(AcceptDecision.RejectIdentityMismatch, Decide("work", "T1RIRVI="));
    }

    [Fact]
    public void UnpinnedPeer_IsAcceptedWithTheCode_ButNotOnAnotherPeersPin()
    {
        Assert.Equal(AcceptDecision.Accept, Decide("new", "TkVX"));
        Assert.Equal(AcceptDecision.RejectIdentityMismatch, Decide("new", "S0VZLVdPUks=", pairedWithCode: false));
    }

    [Fact]
    public void UnknownMachine_WithoutTheCode_NeverBecomesPending()
    {
        Assert.Equal(AcceptDecision.Pending, Decide("stranger", "U1RS"));
        Assert.Equal(AcceptDecision.RejectIdentityMismatch, Decide("stranger", "S0VZLVdPUks=", pairedWithCode: false));
    }

    [Fact]
    public void IdentityMismatch_RejectReason_RoundTrips()
    {
        var reason = AcceptPolicy.RejectReason(AcceptDecision.RejectIdentityMismatch, ConnectionKind.Control, "DODD-MAIN");
        Assert.Equal(RejectCode.IdentityMismatch, RejectReasons.Parse(reason).Code);
    }
}
