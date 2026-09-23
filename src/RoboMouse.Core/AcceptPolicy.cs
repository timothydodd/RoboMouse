using System.Net;
using RoboMouse.Core.Configuration;
using RoboMouse.Core.Network;
using RoboMouse.Core.Network.Protocol;

namespace RoboMouse.Core;

/// <summary>What to do with an incoming connection that has passed the pairing-code check.</summary>
public enum AcceptDecision
{
    /// <summary>A configured, enabled peer.</summary>
    Accept,

    /// <summary>An unknown machine: ask the user. A control connection is refused until they allow it; a probe is answered.</summary>
    Pending,

    /// <summary>A configured peer the user switched off.</summary>
    RejectDisabled,

    /// <summary>A machine the user removed.</summary>
    RejectBlocked,

    /// <summary>An unknown machine the user chose to ignore for this session.</summary>
    RejectIgnored,

    /// <summary>A file transfer from a machine that is not a peer.</summary>
    RejectNotPeer,

    /// <summary>Our own machine id: this PC connecting to itself.</summary>
    RejectSelf,

    /// <summary>A configured peer whose pinned identity key is not the one this connection proved.</summary>
    RejectIdentityMismatch
}

/// <summary>
/// Who may connect. The pairing code only proves a machine is one of the user's; which of those may
/// drive this screen is decided here. Pure, so the rules can be tested without sockets.
/// </summary>
public static class AcceptPolicy
{
    /// <summary>
    /// Decides for a connection from <paramref name="machineId"/> that proved
    /// <paramref name="identityKey"/> (base64). A configured peer whose id has not been learned yet
    /// (added by address and never connected, <see cref="PeerConfig.HasConnected"/> false) is
    /// recognised by its address and listen port and returned in <paramref name="matched"/>, so the
    /// caller can record its id. A peer that has connected is matched by id only: addresses move, and
    /// another machine that later gets its old address is a new request, not an impostor. A configured peer with a pinned
    /// identity key only matches a connection proving that key: knowing the pairing code is not enough
    /// to take over its slot. A connection that skipped the pairing code (<paramref name="pairedWithCode"/>
    /// false, its key was pinned here) must be the peer that pinned it; it never becomes a pending request.
    /// </summary>
    public static AcceptDecision Decide(
        AppSettings settings,
        string machineId,
        ConnectionKind kind,
        IPAddress? address,
        int listenPort,
        bool ignoredThisSession,
        out PeerConfig? matched,
        string? identityKey = null,
        bool pairedWithCode = true)
    {
        matched = null;
        if (machineId == settings.MachineId)
            return AcceptDecision.RejectSelf;

        var peers = settings.Peers.ToList();
        matched = peers.FirstOrDefault(p => p.Id == machineId)
                  ?? peers.FirstOrDefault(p => !p.HasConnected && MatchesAddress(p, address, listenPort));
        if (matched != null)
        {
            var mismatch = string.IsNullOrEmpty(matched.IdentityKey)
                ? !pairedWithCode // pinned by another entry, claiming this one
                : identityKey != null && matched.IdentityKey != identityKey;
            if (mismatch)
                return AcceptDecision.RejectIdentityMismatch;
            return matched.Enabled ? AcceptDecision.Accept : AcceptDecision.RejectDisabled;
        }

        if (!pairedWithCode)
            return AcceptDecision.RejectIdentityMismatch;

        if (settings.BlockedMachineIds.Contains(machineId))
            return AcceptDecision.RejectBlocked;
        if (ignoredThisSession)
            return AcceptDecision.RejectIgnored;
        if (kind == ConnectionKind.Transfer)
            return AcceptDecision.RejectNotPeer;
        return AcceptDecision.Pending;
    }

    /// <summary>
    /// Pins <paramref name="identityKey"/> (base64) on <paramref name="peer"/> when it has none yet, and
    /// says whether the peer's key is now that key. Check and pin happen under <paramref name="gate"/>,
    /// so of two connections racing for one unpinned entry with different keys only the first wins;
    /// the caller must refuse a connection this returns false for. <paramref name="pinned"/> is true
    /// when this call set the key.
    /// </summary>
    public static bool PinOrMatch(PeerConfig peer, string identityKey, object gate, out bool pinned)
    {
        lock (gate)
        {
            pinned = string.IsNullOrEmpty(peer.IdentityKey) && !string.IsNullOrEmpty(identityKey);
            if (pinned)
                peer.IdentityKey = identityKey;
            return peer.IdentityKey == identityKey;
        }
    }

    private static bool MatchesAddress(PeerConfig peer, IPAddress? address, int listenPort)
    {
        if (address == null || peer.Port != listenPort || !IPAddress.TryParse(peer.Address, out var configured))
            return false;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (configured.IsIPv4MappedToIPv6)
            configured = configured.MapToIPv4();
        return configured.Equals(address);
    }

    /// <summary>The reject reason sent back for a decision, or null when the connection is taken.</summary>
    public static string? RejectReason(AcceptDecision decision, ConnectionKind kind, string localName) => decision switch
    {
        AcceptDecision.Accept => null,
        AcceptDecision.Pending when kind == ConnectionKind.Probe => null,
        AcceptDecision.Pending => RejectReasons.Format(RejectCode.AwaitingApproval, $"Waiting for this PC to be allowed on {localName}."),
        AcceptDecision.RejectDisabled => RejectReasons.Format(RejectCode.Disabled, $"This PC is switched off in {localName}'s peer list."),
        AcceptDecision.RejectBlocked => RejectReasons.Format(RejectCode.Blocked, $"This PC was removed on {localName}. Add it there again to connect."),
        AcceptDecision.RejectIgnored => RejectReasons.Format(RejectCode.Blocked, $"{localName} ignored the request from this PC."),
        AcceptDecision.RejectNotPeer => RejectReasons.Format(RejectCode.NotAPeer, $"This PC is not a peer of {localName}."),
        AcceptDecision.RejectSelf => RejectReasons.Format(RejectCode.SameMachine, "That address is this PC."),
        AcceptDecision.RejectIdentityMismatch => RejectReasons.Format(RejectCode.IdentityMismatch,
            $"{localName} has a different identity key on record for this PC (was RoboMouse reinstalled here?). On {localName}, choose to pair with this PC again."),
        _ => RejectReasons.Format(RejectCode.Other, "Refused.")
    };
}

/// <summary>What <see cref="PendingRequests.Register"/> did with a connection request.</summary>
public enum PendingUpdate
{
    /// <summary>First request from this machine: ask the user.</summary>
    Added,

    /// <summary>Another request proving the same identity key: its address and details were refreshed.</summary>
    Refreshed,

    /// <summary>
    /// A request with the same machine id but a different identity key: two machines claim one id.
    /// The pending entry was dropped, so neither can be allowed until a fresh request comes in.
    /// </summary>
    Conflict
}

/// <summary>The rules for the list of machines waiting to be allowed. Pure, so they can be tested without sockets.</summary>
public static class PendingRequests
{
    /// <summary>
    /// Adds or refreshes <paramref name="request"/> in <paramref name="pending"/> (keyed by machine id;
    /// the caller holds its lock). A later request never replaces the identity key of the first one:
    /// Allow pins the key the user saw requested, and a second machine using the same id with its own
    /// key conflicts instead, dropping the entry.
    /// </summary>
    public static PendingUpdate Register(Dictionary<string, PendingPeer> pending, PendingPeer request)
    {
        if (!pending.TryGetValue(request.MachineId, out var first))
        {
            pending[request.MachineId] = request;
            return PendingUpdate.Added;
        }

        if (first.IdentityKey != request.IdentityKey)
        {
            pending.Remove(request.MachineId);
            return PendingUpdate.Conflict;
        }

        // Same machine asking again: keep the first request time, refresh the address in case it moved.
        pending[request.MachineId] = request with { RequestedAt = first.RequestedAt, IdentityKey = first.IdentityKey };
        return PendingUpdate.Refreshed;
    }
}
