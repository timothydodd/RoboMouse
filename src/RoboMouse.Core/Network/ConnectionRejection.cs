using RoboMouse.Core.Network.Protocol;

namespace RoboMouse.Core.Network;

/// <summary>Why a machine refused a connection after the secure handshake succeeded.</summary>
public enum RejectCode
{
    /// <summary>A reason this build does not recognise (or none given).</summary>
    Other,

    /// <summary>This machine is not one of its peers yet; the user there has been asked to allow it.</summary>
    AwaitingApproval,

    /// <summary>The user there removed or ignored this machine.</summary>
    Blocked,

    /// <summary>The user there switched this peer off.</summary>
    Disabled,

    /// <summary>The address leads back to this same machine.</summary>
    SameMachine,

    /// <summary>The connection kind needs a configured peer (a file transfer from an unknown machine).</summary>
    NotAPeer,

    /// <summary>The machine there pinned a different identity key for this PC (it was reinstalled, or is impersonated).</summary>
    IdentityMismatch
}

/// <summary>A connecting machine as the accept policy sees it, after the secure handshake.</summary>
/// <param name="Handshake">What it says about itself.</param>
/// <param name="Remote">Where it connects from.</param>
/// <param name="IdentityKey">The identity public key it proved it holds.</param>
/// <param name="PairedWithCode">True when the handshake used the pairing code; false when its key was already pinned here.</param>
public sealed record IncomingPeer(HandshakeMessage Handshake, System.Net.IPEndPoint? Remote, byte[] IdentityKey, bool PairedWithCode);

/// <summary>Thrown by <see cref="PeerConnection.ConnectAsync"/> when the other machine refused the connection.</summary>
public sealed class ConnectionRejectedException : Exception
{
    public RejectCode Code { get; }

    public ConnectionRejectedException(RejectCode code, string message) : base(message)
    {
        Code = code;
    }
}

/// <summary>
/// Carries a <see cref="RejectCode"/> in the handshake acknowledgement's free-text reject reason as
/// "code: text", so the connecting side can tell the user why without a protocol change. Older builds
/// never reject after the handshake, and show the text as it is.
/// </summary>
public static class RejectReasons
{
    private static readonly (RejectCode Code, string Tag)[] Tags =
    {
        (RejectCode.AwaitingApproval, "pending"),
        (RejectCode.Blocked, "blocked"),
        (RejectCode.Disabled, "disabled"),
        (RejectCode.SameMachine, "self"),
        (RejectCode.NotAPeer, "not-peer"),
        (RejectCode.IdentityMismatch, "identity")
    };

    public static string Format(RejectCode code, string text)
    {
        foreach (var (c, tag) in Tags)
        {
            if (c == code)
                return $"{tag}: {text}";
        }
        return text;
    }

    public static (RejectCode Code, string Text) Parse(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return (RejectCode.Other, "The other machine refused the connection.");

        var colon = reason.IndexOf(':');
        if (colon > 0)
        {
            var tag = reason[..colon];
            foreach (var (code, t) in Tags)
            {
                if (t == tag)
                    return (code, reason[(colon + 1)..].Trim());
            }
        }
        return (RejectCode.Other, reason);
    }
}
