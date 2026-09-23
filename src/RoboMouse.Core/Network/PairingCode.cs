using System.Security.Cryptography;

namespace RoboMouse.Core.Network;

/// <summary>
/// The shared code that lets two machines pair. Only generated codes are strong enough: the handshake
/// is not a PAKE, so someone who records or probes a handshake can test guesses offline, and only a
/// random code (12 characters from 32, 60 bits) puts that out of reach. A code typed by hand is still
/// used, but <see cref="IsStrong"/> flags it so the UI can ask for a new one.
/// </summary>
public static class PairingCode
{
    /// <summary>The characters a generated code uses: no 0/O or 1/I.</summary>
    public const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>Characters in a code, not counting separators.</summary>
    public const int Length = 12;

    /// <summary>Generates a fresh, readable code such as "K7QM-4XDP-9RLA".</summary>
    public static string Generate()
    {
        var chars = new char[Length];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return $"{new string(chars, 0, 4)}-{new string(chars, 4, 4)}-{new string(chars, 8, 4)}";
    }

    /// <summary>Upper case, without separators and spaces: the form the key is derived from.</summary>
    public static string Normalize(string? code) =>
        (code ?? string.Empty).Trim().ToUpperInvariant().Replace("-", string.Empty).Replace(" ", string.Empty);

    /// <summary>
    /// True when <paramref name="code"/> has the generated format (12 characters from
    /// <see cref="Alphabet"/>, separators and case ignored), so it carries the full 60 bits.
    /// </summary>
    public static bool IsStrong(string? code)
    {
        var normalized = Normalize(code);
        return normalized.Length == Length && normalized.All(c => Alphabet.Contains(c));
    }

    /// <summary>True when two codes are the same once normalized.</summary>
    public static bool AreEqual(string? a, string? b) => Normalize(a) == Normalize(b);
}
