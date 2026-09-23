using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using RoboMouse.Core.Network.Protocol;

namespace RoboMouse.Core.Network;

/// <summary>
/// What this machine brings to a secure handshake: the key derived from the pairing code, its own
/// identity key, and (listening side) a way to tell whether a connecting machine's identity key is
/// pinned by one of its peers.
/// </summary>
public sealed class ChannelCredentials
{
    public ChannelCredentials(byte[] pairingKey, IdentityKey identity, Func<byte[], bool>? isPinned = null)
    {
        PairingKey = pairingKey;
        Identity = identity;
        IsPinned = isPinned;
    }

    /// <summary>Derived from the pairing code by <see cref="SecureChannel.DerivePairingKey"/>.</summary>
    public byte[] PairingKey { get; }

    /// <summary>This install's identity.</summary>
    public IdentityKey Identity { get; }

    /// <summary>
    /// Listening side: true when the given identity public key is pinned by a configured peer, so that
    /// machine can connect without the pairing code. Null: every connection pairs with the code.
    /// </summary>
    public Func<byte[], bool>? IsPinned { get; }

    /// <summary>Credentials with a throwaway identity, for one-off connections and tests.</summary>
    public static ChannelCredentials Ephemeral(byte[] pairingKey) => new(pairingKey, IdentityKey.Create());
}

/// <summary>
/// Authenticated, encrypted stream over a raw socket stream.
///
/// Every install has a long-lived identity key (<see cref="IdentityKey"/>). On connect the two machines
/// run an ECDH (P-256) key exchange and each signs the handshake transcript with its identity key, so
/// each learns the other's identity public key and knows the other holds it. Then one of two modes:
/// <list type="bullet">
/// <item><b>Pinned</b>: both already pinned each other's identity key (the connecting side asks for it,
/// the listening side agrees when it finds the key pinned). The signatures are the whole
/// authentication; the pairing code plays no part, so changing it does not disturb paired machines.</item>
/// <item><b>Pairing</b>: anything else. Both also prove they know the pairing code (HMACs keyed from it,
/// the connecting side first), and the code is mixed into the session keys. The caller then pins the
/// identity key it learned, trust-on-first-pair inside a channel the code authenticated.</item>
/// </list>
/// The connecting side names the identity it expects when it has one pinned and refuses any other
/// (<see cref="IdentityMismatchException"/>). The listening side reports the connecting machine's key in
/// <see cref="PeerIdentityKey"/>; matching it against the machine id the peer then claims is the caller's job.
///
/// No PAKE: someone who records a pairing-mode handshake can test pairing-code guesses offline, which is
/// why only generated codes (60 random bits, see <see cref="PairingCode"/>) are strong enough. The
/// PBKDF2 salt includes the protocol version, so work done against an older version does not carry over.
///
/// Traffic is AES-256-GCM, one frame per Write (split at 1 MB), with separate keys and nonce counters
/// per direction. Wire format after the handshake: [4-byte length][8-byte counter][ciphertext][16-byte tag].
/// </summary>
public sealed class SecureChannel : Stream
{
    /// <summary>Handshake format version: 1 up to protocol 4, 2 since protocol 5.</summary>
    internal const byte HandshakeVersion = 2;

    private const int NonceBytes = 32;
    private const int TagBytes = 16;
    private const int ProofBytes = 32;
    private const int MaxFrameBytes = 64 * 1024 * 1024;
    private const int MaxKeyBytes = 256;
    private const int MaxSignatureBytes = 256;

    private const byte FlagWantsPinned = 0x01;
    private const byte ModePairing = 0;
    private const byte ModePinned = 1;

    private const byte StatusOk = 0;
    private const byte StatusCodeMismatch = 1;
    private const byte StatusBadIdentity = 2;

    internal const string UpdateBothMachines = "Update RoboMouse on both machines to the same version.";

    /// <summary>Largest plaintext sent in one frame; bigger writes are split.</summary>
    internal const int MaxWriteBytes = 1024 * 1024;

    private readonly Stream _inner;
    private readonly AesGcm _send;
    private readonly AesGcm _receive;
    private ulong _sendCounter;
    private ulong _receiveCounter;

    private readonly object _writeLock = new();
    private byte[] _plainBuffer = new byte[64 * 1024];
    private int _plainStart;
    private int _plainEnd;

    private SecureChannel(Stream inner, byte[] sendKey, byte[] receiveKey, byte[] peerIdentityKey, bool pairedWithCode)
    {
        _inner = inner;
        _send = new AesGcm(sendKey, TagBytes);
        _receive = new AesGcm(receiveKey, TagBytes);
        PeerIdentityKey = peerIdentityKey;
        PairedWithCode = pairedWithCode;
    }

    /// <summary>The other machine's identity public key (SubjectPublicKeyInfo DER); it proved it holds the private key.</summary>
    public byte[] PeerIdentityKey { get; }

    /// <summary>True when the handshake used the pairing code (pairing mode); false when both identities were pinned.</summary>
    public bool PairedWithCode { get; }

    /// <summary>
    /// Derives the pairing key from the human-entered pairing code. Deliberately slow (PBKDF2, 120 000
    /// rounds); the salt names the protocol version.
    /// </summary>
    public static byte[] DerivePairingKey(string pairingCode)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(PairingCode.Normalize(pairingCode)),
            Encoding.UTF8.GetBytes($"RoboMouse pairing, protocol {Message.ProtocolVersion}"),
            120_000,
            HashAlgorithmName.SHA256,
            32);
    }

    /// <summary>
    /// Generates a fresh, readable pairing code such as "K7QM-4XDP-9RLA".
    /// </summary>
    public static string GeneratePairingCode() => PairingCode.Generate();

    /// <summary>
    /// Runs the connecting side of the handshake. With <paramref name="expectedPeerKey"/> set (the
    /// peer's pinned identity), asks for pinned mode and refuses any other identity.
    /// </summary>
    public static Task<SecureChannel> ConnectAsync(Stream inner, ChannelCredentials credentials, byte[]? expectedPeerKey, CancellationToken ct)
        => ClientHandshakeAsync(inner, credentials, expectedPeerKey, ct);

    /// <summary>Runs the listening side of the handshake.</summary>
    public static Task<SecureChannel> AcceptAsync(Stream inner, ChannelCredentials credentials, CancellationToken ct)
        => ServerHandshakeAsync(inner, credentials, ct);

    /// <summary>Connecting side with a throwaway identity, pairing with the code.</summary>
    public static Task<SecureChannel> ConnectAsync(Stream inner, byte[] pairingKey, CancellationToken ct)
        => ConnectAsync(inner, ChannelCredentials.Ephemeral(pairingKey), null, ct);

    /// <summary>Listening side with a throwaway identity, pairing with the code.</summary>
    public static Task<SecureChannel> AcceptAsync(Stream inner, byte[] pairingKey, CancellationToken ct)
        => AcceptAsync(inner, ChannelCredentials.Ephemeral(pairingKey), ct);

    #region Handshake

    // Client hello:  [version][flags][nonce][u16 len][ephemeral key][u16 len][identity key]
    // Server hello:  [version][mode][nonce][u16 len][ephemeral key][u16 len][identity key] + [u16 len][signature]
    // Client finish: [u16 len][signature] + ([pairing proof] in pairing mode)
    // Server finish: [status] + ([pairing proof] in pairing mode when the status is OK)
    //
    // Both signatures and both proofs cover the transcript hash of the two hellos (without signatures),
    // which the session keys are also salted with. The connecting side proves the pairing code first,
    // so a machine that only listens gives nothing away to a prober.

    private static async Task<SecureChannel> ClientHandshakeAsync(Stream inner, ChannelCredentials credentials, byte[]? expectedPeerKey, CancellationToken ct)
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ephemeral = ecdh.PublicKey.ExportSubjectPublicKeyInfo();
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var identity = credentials.Identity;

        var clientHello = BuildHello(expectedPeerKey != null ? FlagWantsPinned : (byte)0, nonce, ephemeral, identity.PublicKey);
        await WriteBlobAsync(inner, clientHello, ct);

        byte[] serverBlob;
        try
        {
            serverBlob = await ReadBlobAsync(inner, ct);
        }
        catch (IOException) when (!ct.IsCancellationRequested)
        {
            // A protocol 4 build cannot read our hello and hangs up without a word.
            throw new IncompatibleVersionException(
                "The other machine closed the connection during the secure handshake; it is probably running an older RoboMouse. " + UpdateBothMachines);
        }

        CheckVersion(serverBlob);
        var serverHello = ParseHello(serverBlob, withSignature: true);
        if (serverHello.Flags is not (ModePairing or ModePinned) || (serverHello.Flags == ModePinned && expectedPeerKey == null))
            throw new PairingException("Malformed handshake from the other machine.");
        var pairing = serverHello.Flags == ModePairing;

        if (expectedPeerKey != null && !CryptographicOperations.FixedTimeEquals(expectedPeerKey, serverHello.IdentityKey))
            throw new IdentityMismatchException("The other machine's identity key is not the one this PC paired with. It may have been reinstalled, or another machine is using its address.");

        var transcript = Transcript(clientHello, serverBlob.AsSpan(0, serverHello.CoreLength));
        if (!IdentityKey.Verify(serverHello.IdentityKey, Signed("server", transcript), serverHello.Signature))
            throw new PairingException("The other machine did not prove its identity.");

        var signature = identity.Sign(Signed("client", transcript));
        var finish = new List<byte>();
        AppendField(finish, signature);
        if (pairing)
            finish.AddRange(Proof(credentials.PairingKey, "client", transcript));
        await WriteBlobAsync(inner, finish.ToArray(), ct);

        var result = await ReadBlobAsync(inner, ct);
        switch (result[0])
        {
            case StatusOk:
                break;
            case StatusCodeMismatch:
                throw new PairingException("The other machine's pairing code does not match this one.");
            default:
                throw new PairingException("The other machine did not accept this PC's identity proof.");
        }

        if (pairing)
        {
            var expected = Proof(credentials.PairingKey, "server", transcript);
            if (result.Length != 1 + ProofBytes || !CryptographicOperations.FixedTimeEquals(expected, result.AsSpan(1)))
                throw new PairingException("The other machine's pairing code does not match this one.");
        }

        var (c2s, s2c) = DeriveKeys(ecdh, serverHello.EphemeralKey, pairing ? credentials.PairingKey : null, transcript);
        return new SecureChannel(inner, c2s, s2c, serverHello.IdentityKey, pairing);
    }

    private static async Task<SecureChannel> ServerHandshakeAsync(Stream inner, ChannelCredentials credentials, CancellationToken ct)
    {
        var clientBlob = await ReadBlobAsync(inner, ct);
        if (clientBlob[0] != HandshakeVersion)
        {
            // Answer with our version so the other side reports a version problem, not a broken
            // connection: a protocol 4 build says "incompatible RoboMouse version". Nothing is proved to
            // it, so it learns nothing it could test pairing-code guesses against.
            await WriteBlobAsync(inner, new[] { HandshakeVersion }, ct);
            CheckVersion(clientBlob);
        }
        var clientHello = ParseHello(clientBlob, withSignature: false);

        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ephemeral = ecdh.PublicKey.ExportSubjectPublicKeyInfo();
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var identity = credentials.Identity;

        var pinned = (clientHello.Flags & FlagWantsPinned) != 0 && credentials.IsPinned?.Invoke(clientHello.IdentityKey) == true;
        var pairing = !pinned;

        var serverCore = BuildHello(pinned ? ModePinned : ModePairing, nonce, ephemeral, identity.PublicKey);
        var transcript = Transcript(clientBlob, serverCore);
        var serverHello = new List<byte>(serverCore);
        AppendField(serverHello, identity.Sign(Signed("server", transcript)));
        await WriteBlobAsync(inner, serverHello.ToArray(), ct);

        var finish = await ReadBlobAsync(inner, ct);
        var offset = 0;
        var signature = ReadField(finish, ref offset, MaxSignatureBytes);
        if (!IdentityKey.Verify(clientHello.IdentityKey, Signed("client", transcript), signature))
        {
            await WriteBlobAsync(inner, new[] { StatusBadIdentity }, ct);
            throw new PairingException("The connecting machine did not prove its identity.");
        }

        if (pairing)
        {
            var expected = Proof(credentials.PairingKey, "client", transcript);
            if (finish.Length != offset + ProofBytes || !CryptographicOperations.FixedTimeEquals(expected, finish.AsSpan(offset)))
            {
                await WriteBlobAsync(inner, new[] { StatusCodeMismatch }, ct);
                throw new PairingException("The connecting machine's pairing code does not match this one.");
            }
        }

        var reply = new List<byte> { StatusOk };
        if (pairing)
            reply.AddRange(Proof(credentials.PairingKey, "server", transcript));
        await WriteBlobAsync(inner, reply.ToArray(), ct);

        var (c2s, s2c) = DeriveKeys(ecdh, clientHello.EphemeralKey, pairing ? credentials.PairingKey : null, transcript);
        return new SecureChannel(inner, s2c, c2s, clientHello.IdentityKey, pairing);
    }

    /// <summary>Throws <see cref="IncompatibleVersionException"/> unless the blob starts with our handshake version.</summary>
    private static void CheckVersion(byte[] blob)
    {
        var version = blob[0];
        if (version == HandshakeVersion)
            return;
        throw new IncompatibleVersionException(version < HandshakeVersion
            ? "The other machine is running an older RoboMouse. " + UpdateBothMachines
            : "The other machine is running a newer RoboMouse. " + UpdateBothMachines);
    }

    private sealed record Hello(byte Flags, byte[] Nonce, byte[] EphemeralKey, byte[] IdentityKey, byte[] Signature, int CoreLength);

    private static byte[] BuildHello(byte flags, byte[] nonce, byte[] ephemeral, byte[] identity)
    {
        var buffer = new List<byte>(2 + NonceBytes + 4 + ephemeral.Length + identity.Length) { HandshakeVersion, flags };
        buffer.AddRange(nonce);
        AppendField(buffer, ephemeral);
        AppendField(buffer, identity);
        return buffer.ToArray();
    }

    private static Hello ParseHello(byte[] data, bool withSignature)
    {
        if (data.Length < 2 + NonceBytes)
            throw new PairingException("Malformed handshake from the other machine.");

        var offset = 2;
        var nonce = data.AsSpan(offset, NonceBytes).ToArray();
        offset += NonceBytes;
        var ephemeral = ReadField(data, ref offset, MaxKeyBytes);
        var identity = ReadField(data, ref offset, MaxKeyBytes);
        var coreLength = offset;
        var signature = withSignature ? ReadField(data, ref offset, MaxSignatureBytes) : Array.Empty<byte>();
        if (offset != data.Length || !IdentityKey.IsValidPublicKey(identity))
            throw new PairingException("Malformed handshake from the other machine.");
        return new Hello(data[1], nonce, ephemeral, identity, signature, coreLength);
    }

    private static void AppendField(List<byte> buffer, byte[] field)
    {
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(length, (ushort)field.Length);
        buffer.AddRange(length.ToArray());
        buffer.AddRange(field);
    }

    private static byte[] ReadField(byte[] data, ref int offset, int maxLength)
    {
        if (data.Length - offset < 2)
            throw new PairingException("Malformed handshake from the other machine.");
        var length = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
        offset += 2;
        if (length == 0 || length > maxLength || data.Length - offset < length)
            throw new PairingException("Malformed handshake from the other machine.");
        var field = data.AsSpan(offset, length).ToArray();
        offset += length;
        return field;
    }

    /// <summary>Hash of both hellos (without the server's signature), prefixed with the protocol version.</summary>
    private static byte[] Transcript(ReadOnlySpan<byte> clientHello, ReadOnlySpan<byte> serverCore)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("RoboMouse handshake"u8);
        hash.AppendData(new[] { Message.ProtocolVersion });
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, clientHello.Length);
        hash.AppendData(length);
        hash.AppendData(clientHello);
        BinaryPrimitives.WriteInt32LittleEndian(length, serverCore.Length);
        hash.AppendData(length);
        hash.AppendData(serverCore);
        return hash.GetHashAndReset();
    }

    private static byte[] Signed(string role, byte[] transcript) => Concat(Encoding.ASCII.GetBytes($"RoboMouse {role} identity"), transcript);

    private static byte[] Proof(byte[] pairingKey, string role, byte[] transcript) =>
        HMACSHA256.HashData(pairingKey, Concat(Encoding.ASCII.GetBytes($"RoboMouse {role} code"), transcript));

    private static (byte[] ClientToServer, byte[] ServerToClient) DeriveKeys(ECDiffieHellman ecdh, byte[] theirEphemeral, byte[]? pairingKey, byte[] transcript)
    {
        byte[] shared;
        using (var theirKey = ECDiffieHellman.Create())
        {
            try
            {
                theirKey.ImportSubjectPublicKeyInfo(theirEphemeral, out _);
                shared = ecdh.DeriveRawSecretAgreement(theirKey.PublicKey);
            }
            catch (CryptographicException)
            {
                throw new PairingException("Malformed handshake from the other machine.");
            }
        }

        var ikm = pairingKey == null ? shared : Concat(shared, pairingKey);
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, transcript);
        var clientToServer = HKDF.Expand(HashAlgorithmName.SHA256, prk, 32, "RoboMouse c2s"u8.ToArray());
        var serverToClient = HKDF.Expand(HashAlgorithmName.SHA256, prk, 32, "RoboMouse s2c"u8.ToArray());
        CryptographicOperations.ZeroMemory(shared);
        CryptographicOperations.ZeroMemory(ikm);
        CryptographicOperations.ZeroMemory(prk);
        return (clientToServer, serverToClient);
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var result = new byte[a.Length + b.Length];
        a.CopyTo(result, 0);
        b.CopyTo(result, a.Length);
        return result;
    }

    private static async Task WriteBlobAsync(Stream stream, byte[] data, CancellationToken ct)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(data, ct);
    }

    private static async Task<byte[]> ReadBlobAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await ReadExactlyAsync(stream, header, ct);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > 4096)
            throw new PairingException("Malformed handshake from the other machine.");
        var data = new byte[length];
        await ReadExactlyAsync(stream, data, ct);
        return data;
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> target, CancellationToken ct)
    {
        var got = 0;
        while (got < target.Length)
        {
            var read = await stream.ReadAsync(target.Slice(got), ct);
            if (read == 0)
                throw new IOException("Connection closed during handshake.");
            got += read;
        }
    }

    #endregion

    #region Encrypted framing

    public override void Write(byte[] buffer, int offset, int count)
    {
        // A frame is only decrypted once all of it has arrived, so a big write goes out as several
        // frames: the reader sees progress (and counts it as liveness) instead of one long silence.
        while (count > MaxWriteBytes)
        {
            WriteFrame(buffer, offset, MaxWriteBytes);
            offset += MaxWriteBytes;
            count -= MaxWriteBytes;
        }
        WriteFrame(buffer, offset, count);
    }

    private void WriteFrame(byte[] buffer, int offset, int count)
    {
        if (count == 0)
            return;

        var frame = new byte[4 + 8 + count + TagBytes];
        lock (_writeLock)
        {
            var counter = _sendCounter++;
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0), 8 + count + TagBytes);
            BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(4), counter);

            Span<byte> nonce = stackalloc byte[12];
            BinaryPrimitives.WriteUInt64LittleEndian(nonce, counter);

            _send.Encrypt(nonce, buffer.AsSpan(offset, count), frame.AsSpan(12, count), frame.AsSpan(12 + count, TagBytes), frame.AsSpan(4, 8));
            _inner.Write(frame, 0, frame.Length);
        }
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        // Handshake-phase writers use this; it is rare, so a synchronous call on the pool is acceptable.
        await Task.Run(() => Write(buffer, offset, count), ct);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        => new(WriteAsync(buffer.ToArray(), 0, buffer.Length, ct));

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_plainStart == _plainEnd)
        {
            if (!ReadFrame())
                return 0;
        }

        var available = Math.Min(count, _plainEnd - _plainStart);
        Buffer.BlockCopy(_plainBuffer, _plainStart, buffer, offset, available);
        _plainStart += available;
        return available;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    /// <summary>
    /// Handshake-phase readers use this. The read itself is synchronous on the pool, which a token
    /// cannot interrupt, so cancelling closes the underlying stream: the blocked read then fails and the
    /// caller sees the cancellation. The channel is unusable afterwards, which is what a cancelled
    /// handshake wants anyway.
    /// </summary>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var abort = ct.Register(static state => { try { ((Stream)state!).Dispose(); } catch { } }, _inner);
        try
        {
            return await Task.Run(() =>
            {
                var temp = new byte[buffer.Length];
                var read = Read(temp, 0, temp.Length);
                temp.AsSpan(0, read).CopyTo(buffer.Span);
                return read;
            }, CancellationToken.None);
        }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is not OperationCanceledException)
        {
            throw new OperationCanceledException("The read was cancelled.", ex, ct);
        }
    }

    /// <summary>Reads and decrypts one frame into the plaintext buffer. Returns false at end of stream.</summary>
    private bool ReadFrame()
    {
        Span<byte> header = stackalloc byte[4];
        if (!ReadExactlyFromInner(header))
            return false;

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 8 + TagBytes || length > MaxFrameBytes)
            throw new InvalidDataException("Invalid encrypted frame length.");

        var frame = new byte[length];
        if (!ReadExactlyFromInner(frame))
            throw new IOException("Connection closed mid-frame.");

        var counter = BinaryPrimitives.ReadUInt64LittleEndian(frame.AsSpan(0, 8));
        if (counter != _receiveCounter)
            throw new InvalidDataException("Encrypted frame out of sequence (replayed or dropped).");
        _receiveCounter++;

        var plainLength = length - 8 - TagBytes;
        if (_plainBuffer.Length < plainLength)
            _plainBuffer = new byte[plainLength];

        Span<byte> nonce = stackalloc byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(nonce, counter);

        try
        {
            _receive.Decrypt(nonce, frame.AsSpan(8, plainLength), frame.AsSpan(8 + plainLength, TagBytes), _plainBuffer.AsSpan(0, plainLength), frame.AsSpan(0, 8));
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new InvalidDataException("Encrypted frame failed authentication.");
        }

        _plainStart = 0;
        _plainEnd = plainLength;
        return true;
    }

    private bool ReadExactlyFromInner(Span<byte> target)
    {
        var got = 0;
        while (got < target.Length)
        {
            var read = _inner.Read(target.Slice(got));
            if (read == 0)
                return got == 0 ? false : throw new IOException("Connection closed mid-frame.");
            got += read;
        }
        return true;
    }

    #endregion

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => _inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _send.Dispose();
            _receive.Dispose();
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Thrown when the secure handshake fails because the two machines do not share a pairing code.
/// </summary>
public class PairingException : Exception
{
    public PairingException(string message) : base(message) { }
}

/// <summary>
/// Thrown when the other machine speaks a different version of the RoboMouse protocol.
/// </summary>
public class IncompatibleVersionException : Exception
{
    public IncompatibleVersionException(string message) : base(message) { }
}

/// <summary>
/// Thrown when the other machine's identity key is not the one pinned for it: it was reinstalled (new
/// key), or a different machine answered at its address or under its id. Pairing again means
/// forgetting the pinned key (<see cref="RoboMouseService.ForgetPeerIdentity"/>).
/// </summary>
public class IdentityMismatchException : Exception
{
    public IdentityMismatchException(string message) : base(message) { }
}
