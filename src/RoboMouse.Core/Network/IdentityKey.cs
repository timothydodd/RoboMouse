using System.ComponentModel;
using System.Security.Cryptography;
using RoboMouse.Core.Input;
using RoboMouse.Core.Logging;

namespace RoboMouse.Core.Network;

/// <summary>
/// This install's long-lived identity: an ECDSA P-256 key pair. Peers pin the public key the first
/// time they pair (inside a channel the pairing code authenticated), and every later handshake must
/// be signed with the private key, so a machine that merely knows the pairing code can no longer
/// claim another peer's machine id. It also signs discovery broadcasts.
///
/// A signing key rather than a static Diffie-Hellman key: a signature over the handshake transcript is
/// an explicit, separate proof of possession that leaves the ephemeral key exchange untouched, where
/// static-ephemeral DH would need a more delicate key schedule; and a DH key could not sign broadcasts.
///
/// The private key is kept in <c>%AppData%\RoboMouse\identity.key</c>, encrypted with DPAPI for the
/// current user.
/// </summary>
public sealed class IdentityKey : IDisposable
{
    private readonly ECDsa _key;

    private IdentityKey(ECDsa key)
    {
        _key = key;
        PublicKey = key.ExportSubjectPublicKeyInfo();
        PublicKeyText = Convert.ToBase64String(PublicKey);
    }

    /// <summary>Default location of the protected private key.</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RoboMouse", "identity.key");

    /// <summary>The public key (SubjectPublicKeyInfo DER), as sent in the handshake.</summary>
    public byte[] PublicKey { get; }

    /// <summary>The public key as base64, the form pinned in <see cref="Configuration.PeerConfig.IdentityKey"/>.</summary>
    public string PublicKeyText { get; }

    /// <summary>A short, readable fingerprint of the public key (for showing to the user).</summary>
    public string Fingerprint => FingerprintOf(PublicKey);

    /// <summary>Creates a new random identity (not saved).</summary>
    public static IdentityKey Create() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>Signs <paramref name="data"/> (SHA-256, IEEE P1363 signature).</summary>
    public byte[] Sign(ReadOnlySpan<byte> data) => _key.SignData(data, HashAlgorithmName.SHA256);

    /// <summary>
    /// Checks a signature made by the holder of <paramref name="publicKey"/>. False for a malformed
    /// key, a key that is not P-256, or a bad signature; never throws.
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        if (!IsP256(publicKey))
            return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKey, out var read);
            if (read != publicKey.Length || key.KeySize != 256)
                return false;
            return key.VerifyData(data, signature, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Whether <paramref name="publicKey"/> is a well-formed P-256 public key.</summary>
    public static bool IsValidPublicKey(ReadOnlySpan<byte> publicKey)
    {
        if (!IsP256(publicKey))
            return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKey, out var read);
            return read == publicKey.Length && key.KeySize == 256;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    // SubjectPublicKeyInfo of a P-256 key: SEQUENCE { SEQUENCE { id-ecPublicKey, prime256v1
    // (1.2.840.10045.3.1.7) }, BIT STRING { uncompressed point } }. Anything else, including the
    // same curve spelled out as explicit parameters, is refused before it reaches the parser.
    private static ReadOnlySpan<byte> P256SpkiPrefix =>
    [
        0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x02, 0x01,
        0x06, 0x08, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x03, 0x01, 0x07, 0x03, 0x42, 0x00, 0x04
    ];

    /// <summary>Whether a SubjectPublicKeyInfo names the P-256 curve (nistP256) with an uncompressed point.</summary>
    internal static bool IsP256(ReadOnlySpan<byte> spki) =>
        spki.Length == P256SpkiPrefix.Length + 64 && spki.StartsWith(P256SpkiPrefix);

    /// <summary>Fingerprint of a public key: the first 10 bytes of its SHA-256, as five groups of four hex digits.</summary>
    public static string FingerprintOf(ReadOnlySpan<byte> publicKey)
    {
        var hex = Convert.ToHexString(SHA256.HashData(publicKey).AsSpan(0, 10));
        return string.Join('-', Enumerable.Range(0, 5).Select(i => hex.Substring(i * 4, 4)));
    }

    /// <summary>Fingerprint of a pinned key in its base64 form; empty when it is empty or not base64.</summary>
    public static string FingerprintOf(string? publicKeyText)
    {
        var key = Decode(publicKeyText);
        return key == null ? string.Empty : FingerprintOf(key);
    }

    /// <summary>Decodes a pinned key's base64 form; null when it is empty or not base64.</summary>
    public static byte[]? Decode(string? publicKeyText)
    {
        if (string.IsNullOrWhiteSpace(publicKeyText))
            return null;
        try
        {
            return Convert.FromBase64String(publicKeyText);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Loads the identity from <paramref name="path"/>, or creates and saves a new one when there is
    /// none. Only a file whose content is unusable (DPAPI cannot decrypt it because it was copied from
    /// another user or machine, it is corrupt, or it is not a P-256 key) is moved aside and replaced;
    /// peers that pinned the old key then refuse this machine until they forget it and pair again,
    /// which is the point. A file that cannot be opened (a sharing violation from a backup or
    /// antivirus scan, say) is retried a few times and then left untouched: this session uses a
    /// temporary key, and the saved identity is back on the next start.
    /// </summary>
    public static IdentityKey LoadOrCreate(string path, IKeyProtector protector) =>
        LoadOrCreate(path, protector, File.ReadAllBytes, Thread.Sleep);

    /// <summary>How often a key file that cannot be opened is tried before this session gives up on it.</summary>
    internal const int ReadAttempts = 4;

    /// <summary><see cref="LoadOrCreate(string, IKeyProtector)"/> with the file read and the wait between attempts injected, for tests.</summary>
    internal static IdentityKey LoadOrCreate(string path, IKeyProtector protector, Func<string, byte[]> readFile, Action<TimeSpan> wait)
    {
        if (File.Exists(path))
        {
            byte[]? blob = null;
            for (var attempt = 1; blob == null; attempt++)
            {
                try
                {
                    blob = readFile(path);
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    break; // deleted since the check: make a new one
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt >= ReadAttempts)
                    {
                        var temporary = Create();
                        SimpleLogger.Log("Identity", $"Identity key file could not be opened ({ex.Message}); using a temporary identity " +
                            $"{temporary.Fingerprint} for this session and leaving the file alone. Paired peers refuse this PC until it restarts.");
                        return temporary;
                    }
                    wait(TimeSpan.FromMilliseconds(100 * attempt));
                }
            }

            if (blob != null)
            {
                if (TryDecode(blob, protector, out var problem) is { } loaded)
                    return loaded;

                SimpleLogger.Log("Identity", $"Identity key is unusable ({problem}); creating a new one. Peers will need to pair again.");
                try
                {
                    File.Move(path, $"{path}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        var identity = Create();
        identity.Save(path, protector);
        SimpleLogger.Log("Identity", $"Created identity key {identity.Fingerprint}");
        return identity;
    }

    /// <summary>Decrypts and imports a saved key; null with the reason when the content is unusable.</summary>
    private static IdentityKey? TryDecode(byte[] blob, IKeyProtector protector, out string problem)
    {
        byte[] pkcs8;
        try
        {
            pkcs8 = protector.Unprotect(blob);
        }
        catch (CryptographicException ex)
        {
            problem = ex.Message;
            return null;
        }

        var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(pkcs8, out _);
            if (IsP256(key.ExportSubjectPublicKeyInfo()))
            {
                problem = string.Empty;
                return new IdentityKey(key);
            }
            problem = "not a P-256 key";
        }
        catch (CryptographicException ex)
        {
            problem = ex.Message;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
        key.Dispose();
        return null;
    }

    /// <summary>Writes the private key, protected by <paramref name="protector"/>.</summary>
    public void Save(string path, IKeyProtector protector)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var pkcs8 = _key.ExportPkcs8PrivateKey();
        try
        {
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, protector.Protect(pkcs8));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    public void Dispose() => _key.Dispose();
}

/// <summary>Encrypts the identity key at rest.</summary>
public interface IKeyProtector
{
    byte[] Protect(byte[] data);

    /// <summary>Decrypts; throws <see cref="CryptographicException"/> when the data cannot be decrypted.</summary>
    byte[] Unprotect(byte[] data);
}

/// <summary>
/// DPAPI for the current user (<c>CryptProtectData</c>), called directly so the assembly stays
/// AOT-clean without the Windows-only ProtectedData package. Only this Windows account can decrypt.
/// </summary>
public sealed unsafe class DpapiKeyProtector : IKeyProtector
{
    // Ties the blob to this purpose, so another program's DPAPI blob can't be swapped in unnoticed.
    private static readonly byte[] Entropy = "RoboMouse identity key v1"u8.ToArray();

    public byte[] Protect(byte[] data) => Transform(data, protect: true);

    public byte[] Unprotect(byte[] data) => Transform(data, protect: false);

    private static byte[] Transform(byte[] data, bool protect)
    {
        fixed (byte* input = data)
        fixed (byte* entropy = Entropy)
        {
            var inBlob = new NativeMethods.DATA_BLOB { cbData = (uint)data.Length, pbData = (nint)input };
            var entropyBlob = new NativeMethods.DATA_BLOB { cbData = (uint)Entropy.Length, pbData = (nint)entropy };
            var outBlob = default(NativeMethods.DATA_BLOB);

            var ok = protect
                ? NativeMethods.CryptProtectData(&inBlob, "RoboMouse identity", &entropyBlob, 0, 0, NativeMethods.CRYPTPROTECT_UI_FORBIDDEN, &outBlob)
                : NativeMethods.CryptUnprotectData(&inBlob, 0, &entropyBlob, 0, 0, NativeMethods.CRYPTPROTECT_UI_FORBIDDEN, &outBlob);
            if (!ok)
                throw new CryptographicException($"DPAPI {(protect ? "protect" : "unprotect")} failed: {new Win32Exception().Message}");

            try
            {
                return new ReadOnlySpan<byte>((void*)outBlob.pbData, (int)outBlob.cbData).ToArray();
            }
            finally
            {
                new Span<byte>((void*)outBlob.pbData, (int)outBlob.cbData).Clear();
                NativeMethods.LocalFree(outBlob.pbData);
            }
        }
    }
}
