using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace RoboMouse.Core.Network;

/// <summary>
/// Authenticated, encrypted stream over a raw socket stream.
///
/// Both machines share a pairing code. On connect they run an ECDH (P-256) key exchange whose
/// public values are authenticated with HMACs keyed from the pairing code, so a machine that does
/// not know the code cannot complete the handshake and cannot sit in the middle. Traffic is then
/// AES-256-GCM, one frame per Write, with separate keys and nonce counters per direction.
///
/// Wire format after the handshake: [4-byte length][8-byte counter][ciphertext][16-byte tag].
/// </summary>
public sealed class SecureChannel : Stream
{
    private const byte HandshakeVersion = 1;
    private const int NonceBytes = 32;
    private const int TagBytes = 16;
    private const int MaxFrameBytes = 64 * 1024 * 1024;

    private readonly Stream _inner;
    private readonly AesGcm _send;
    private readonly AesGcm _receive;
    private ulong _sendCounter;
    private ulong _receiveCounter;

    private readonly object _writeLock = new();
    private byte[] _plainBuffer = new byte[64 * 1024];
    private int _plainStart;
    private int _plainEnd;

    private SecureChannel(Stream inner, byte[] sendKey, byte[] receiveKey)
    {
        _inner = inner;
        _send = new AesGcm(sendKey, TagBytes);
        _receive = new AesGcm(receiveKey, TagBytes);
    }

    /// <summary>
    /// Derives the long-lived pairing key from the human-entered pairing code.
    /// </summary>
    public static byte[] DerivePairingKey(string pairingCode)
    {
        var normalized = pairingCode.Trim().ToUpperInvariant().Replace("-", string.Empty).Replace(" ", string.Empty);
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(normalized),
            Encoding.UTF8.GetBytes("RoboMouse pairing v1"),
            120_000,
            HashAlgorithmName.SHA256,
            32);
    }

    /// <summary>
    /// Generates a fresh, readable pairing code such as "K7QM-4XDP-9RLA".
    /// </summary>
    public static string GeneratePairingCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // No 0/O or 1/I
        var chars = new char[12];
        var bytes = RandomNumberGenerator.GetBytes(chars.Length);
        for (var i = 0; i < chars.Length; i++)
            chars[i] = alphabet[bytes[i] % alphabet.Length];
        return $"{new string(chars, 0, 4)}-{new string(chars, 4, 4)}-{new string(chars, 8, 4)}";
    }

    /// <summary>Runs the client side of the handshake.</summary>
    public static Task<SecureChannel> ConnectAsync(Stream inner, byte[] pairingKey, CancellationToken ct)
        => HandshakeAsync(inner, pairingKey, isClient: true, ct);

    /// <summary>Runs the server side of the handshake.</summary>
    public static Task<SecureChannel> AcceptAsync(Stream inner, byte[] pairingKey, CancellationToken ct)
        => HandshakeAsync(inner, pairingKey, isClient: false, ct);

    private static async Task<SecureChannel> HandshakeAsync(Stream inner, byte[] pairingKey, bool isClient, CancellationToken ct)
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var myPublic = ecdh.PublicKey.ExportSubjectPublicKeyInfo();
        var myNonce = RandomNumberGenerator.GetBytes(NonceBytes);

        // Hello: version, nonce, public key. Client sends first; server replies with its own plus proof.
        byte[] theirNonce, theirPublic;
        if (isClient)
        {
            await WriteBlobAsync(inner, BuildHello(myNonce, myPublic), ct);
            var serverHello = await ReadBlobAsync(inner, ct);
            (theirNonce, theirPublic, var serverProof) = ParseHello(serverHello, expectProof: true);

            var expected = Proof(pairingKey, "server", myNonce, theirNonce, myPublic, theirPublic);
            if (!CryptographicOperations.FixedTimeEquals(expected, serverProof))
                throw new PairingException("The other machine's pairing code does not match this one.");

            var clientProof = Proof(pairingKey, "client", myNonce, theirNonce, myPublic, theirPublic);
            await WriteBlobAsync(inner, clientProof, ct);
        }
        else
        {
            var clientHello = await ReadBlobAsync(inner, ct);
            (theirNonce, theirPublic, _) = ParseHello(clientHello, expectProof: false);

            var serverProof = Proof(pairingKey, "server", theirNonce, myNonce, theirPublic, myPublic);
            await WriteBlobAsync(inner, BuildHello(myNonce, myPublic, serverProof), ct);

            var clientProof = await ReadBlobAsync(inner, ct);
            var expected = Proof(pairingKey, "client", theirNonce, myNonce, theirPublic, myPublic);
            if (!CryptographicOperations.FixedTimeEquals(expected, clientProof))
                throw new PairingException("The connecting machine's pairing code does not match this one.");
        }

        using var theirKey = ECDiffieHellman.Create();
        theirKey.ImportSubjectPublicKeyInfo(theirPublic, out _);
        var shared = ecdh.DeriveRawSecretAgreement(theirKey.PublicKey);

        var clientNonce = isClient ? myNonce : theirNonce;
        var serverNonce = isClient ? theirNonce : myNonce;
        var salt = Concat(clientNonce, serverNonce);
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, Concat(shared, pairingKey), salt);
        var clientToServer = HKDF.Expand(HashAlgorithmName.SHA256, prk, 32, Encoding.ASCII.GetBytes("RoboMouse c2s"));
        var serverToClient = HKDF.Expand(HashAlgorithmName.SHA256, prk, 32, Encoding.ASCII.GetBytes("RoboMouse s2c"));
        CryptographicOperations.ZeroMemory(shared);

        return isClient
            ? new SecureChannel(inner, clientToServer, serverToClient)
            : new SecureChannel(inner, serverToClient, clientToServer);
    }

    private static byte[] BuildHello(byte[] nonce, byte[] publicKey, byte[]? proof = null)
    {
        var buffer = new byte[1 + NonceBytes + 2 + publicKey.Length + (proof?.Length ?? 0)];
        var offset = 0;
        buffer[offset++] = HandshakeVersion;
        nonce.CopyTo(buffer, offset); offset += NonceBytes;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset), (ushort)publicKey.Length); offset += 2;
        publicKey.CopyTo(buffer, offset); offset += publicKey.Length;
        proof?.CopyTo(buffer, offset);
        return buffer;
    }

    private static (byte[] Nonce, byte[] PublicKey, byte[] Proof) ParseHello(byte[] data, bool expectProof)
    {
        if (data.Length < 1 + NonceBytes + 2 || data[0] != HandshakeVersion)
            throw new PairingException("The other machine is running an incompatible RoboMouse version.");

        var offset = 1;
        var nonce = data.AsSpan(offset, NonceBytes).ToArray(); offset += NonceBytes;
        var keyLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset)); offset += 2;
        if (keyLength == 0 || keyLength > 256 || data.Length < offset + keyLength + (expectProof ? 32 : 0))
            throw new PairingException("Malformed handshake from the other machine.");

        var publicKey = data.AsSpan(offset, keyLength).ToArray(); offset += keyLength;
        var proof = expectProof ? data.AsSpan(offset, 32).ToArray() : Array.Empty<byte>();
        return (nonce, publicKey, proof);
    }

    private static byte[] Proof(byte[] pairingKey, string role, byte[] clientNonce, byte[] serverNonce, byte[] clientPublic, byte[] serverPublic)
    {
        using var hmac = new HMACSHA256(pairingKey);
        hmac.TransformBlock(Encoding.ASCII.GetBytes(role), 0, role.Length, null, 0);
        hmac.TransformBlock(clientNonce, 0, clientNonce.Length, null, 0);
        hmac.TransformBlock(serverNonce, 0, serverNonce.Length, null, 0);
        hmac.TransformBlock(clientPublic, 0, clientPublic.Length, null, 0);
        hmac.TransformFinalBlock(serverPublic, 0, serverPublic.Length);
        return hmac.Hash!;
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

    #region Encrypted framing

    public override void Write(byte[] buffer, int offset, int count)
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

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => await Task.Run(() => Read(buffer, offset, count), ct);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        => new(Task.Run(() =>
        {
            var temp = new byte[buffer.Length];
            var read = Read(temp, 0, temp.Length);
            temp.AsSpan(0, read).CopyTo(buffer.Span);
            return read;
        }, ct));

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
