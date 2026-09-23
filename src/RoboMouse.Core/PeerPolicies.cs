using RoboMouse.Core.Configuration;

namespace RoboMouse.Core;

/// <summary>
/// Which peers the clipboard is shared with (<see cref="PeerConfig.ShareClipboard"/>). Applied in both
/// directions and to relays: a peer with sharing off is sent nothing, and nothing it sends is applied or
/// passed on, so it neither receives nor contributes clipboard content or file offers.
/// </summary>
public static class ClipboardSharing
{
    /// <summary>
    /// True when clipboard content may go to, or come from, this peer (by machine id). A machine that is
    /// not a configured peer never shares.
    /// </summary>
    public static bool SharesWith(AppSettings settings, string peerId)
    {
        foreach (var peer in settings.Peers.ToList())
        {
            if (peer.Id == peerId)
                return peer.ShareClipboard;
        }
        return false;
    }
}

/// <summary>What following the host's session state asks this machine to do.</summary>
[Flags]
public enum SessionFollowAction
{
    None = 0,
    Lock = 1,
    StartScreensaver = 2
}

/// <summary>
/// Who may lock this PC: "Lock all PCs" requests, and following the controlling PC's lock and screen
/// saver. Locking is harmless to the data but disruptive, so only a peer that has proved its pinned
/// identity key counts, never one that merely knows the pairing code.
/// </summary>
public static class LockPolicy
{
    /// <summary>
    /// Input here this recently means somebody is using this PC, so the host's screen saver is not
    /// copied. (The host's own input, injected here while it controlled this PC, is older than this by
    /// the time its screen saver starts.)
    /// </summary>
    public const uint LocalUseWindowMs = 60_000;

    /// <summary>
    /// True when <paramref name="peerId"/> is a configured, enabled peer whose pinned identity key is
    /// <paramref name="identityKey"/> (base64, as the connection proved it).
    /// </summary>
    public static bool IsTrusted(AppSettings settings, string peerId, string identityKey)
    {
        if (string.IsNullOrEmpty(identityKey))
            return false;
        foreach (var peer in settings.Peers.ToList())
        {
            if (peer.Id == peerId)
                return peer.Enabled && !string.IsNullOrEmpty(peer.IdentityKey) && peer.IdentityKey == identityKey;
        }
        return false;
    }

    /// <summary>Whether a "Lock all PCs" request from this peer is honoured.</summary>
    public static bool AcceptsLockRequest(AppSettings settings, string peerId, string identityKey) =>
        IsTrusted(settings, peerId, identityKey);

    /// <summary>
    /// What to do when a peer's session state changes from <paramref name="previous"/> (null when not
    /// known yet) to <paramref name="current"/>: follow the host's lock with <see cref="AppSettings.LockWithHost"/>,
    /// its screen saver with <see cref="AppSettings.ScreensaverWithHost"/>. Only the host (the peer that
    /// last controlled this PC) is followed, only on a change, and only when it is trusted.
    /// </summary>
    public static SessionFollowAction Follow(AppSettings settings, string peerId, string identityKey, string? hostId,
        (bool Locked, bool Screensaver)? previous, (bool Locked, bool Screensaver) current, uint msSinceLocalInput)
    {
        if (hostId == null || hostId != peerId || !IsTrusted(settings, peerId, identityKey))
            return SessionFollowAction.None;

        var action = SessionFollowAction.None;
        if (settings.LockWithHost && current.Locked && previous?.Locked != true)
            action |= SessionFollowAction.Lock;
        if (settings.ScreensaverWithHost && current.Screensaver && previous?.Screensaver != true
            && !current.Locked && msSinceLocalInput >= LocalUseWindowMs)
            action |= SessionFollowAction.StartScreensaver;
        return action;
    }
}
