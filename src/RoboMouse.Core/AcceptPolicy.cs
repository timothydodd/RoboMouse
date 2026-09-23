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
    RejectSelf
}

/// <summary>
/// Who may connect. The pairing code only proves a machine is one of the user's; which of those may
/// drive this screen is decided here. Pure, so the rules can be tested without sockets.
/// </summary>
public static class AcceptPolicy
{
    /// <summary>
    /// Decides for a connection from <paramref name="machineId"/>. A configured peer whose id has not
    /// been learned yet (added by address, never connected) is recognised by its address and listen
    /// port and returned in <paramref name="matched"/>, so the caller can record its id.
    /// </summary>
    public static AcceptDecision Decide(
        AppSettings settings,
        string machineId,
        ConnectionKind kind,
        IPAddress? address,
        int listenPort,
        bool ignoredThisSession,
        out PeerConfig? matched)
    {
        matched = null;
        if (machineId == settings.MachineId)
            return AcceptDecision.RejectSelf;

        var peers = settings.Peers.ToList();
        matched = peers.FirstOrDefault(p => p.Id == machineId)
                  ?? peers.FirstOrDefault(p => MatchesAddress(p, address, listenPort));
        if (matched != null)
            return matched.Enabled ? AcceptDecision.Accept : AcceptDecision.RejectDisabled;

        if (settings.BlockedMachineIds.Contains(machineId))
            return AcceptDecision.RejectBlocked;
        if (ignoredThisSession)
            return AcceptDecision.RejectIgnored;
        if (kind == ConnectionKind.Transfer)
            return AcceptDecision.RejectNotPeer;
        return AcceptDecision.Pending;
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
        _ => RejectReasons.Format(RejectCode.Other, "Refused.")
    };
}
