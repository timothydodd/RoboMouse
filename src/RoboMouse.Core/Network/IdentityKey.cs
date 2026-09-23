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
    /// none. A file that cannot be read or decrypted (copied from another user or machine, corrupt) is
    /// moved aside and replaced; peers that pinned the old key then refuse this machine until they
    /// forget it and pair again, which is the point.
    /// </summary>
    public static IdentityKey LoadOrCreate(string path, IKeyProtector protector)
    {
        if (File.Exists(path))
        {
            try
            {
                var pkcs8 = protector.Unprotect(File.ReadAllBytes(path));
                try
                {
                    var key = ECDsa.Create();
                    key.ImportPkcs8PrivateKey(pkcs8, out _);
                    if (key.KeySize == 256)
                        return new IdentityKey(key);
                    key.Dispose();
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(pkcs8);
                }
            }
            catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or Win32Exception)
            {
                SimpleLogger.Log("Identity", $"Identity key could not be read ({ex.Message}); creating a new one. Peers will need to pair again.");
            }

            try
            {
                File.Move(path, $"{path}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        var identity = Create();
        identity.Save(path, protector);
        SimpleLogger.Log("Identity", $"Created identity key {identity.Fingerprint}");
        return identity;
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
