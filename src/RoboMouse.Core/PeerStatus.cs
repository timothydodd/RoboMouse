using System.Net.Sockets;
using RoboMouse.Core.Network;

namespace RoboMouse.Core;

/// <summary>Why the last attempt to connect to a peer failed.</summary>
public enum PeerFailureKind
{
    /// <summary>No address answered (off, asleep, wrong address, or a firewall dropping packets).</summary>
    Unreachable,

    /// <summary>The machine answered but nothing listens on that port (RoboMouse not running, or another port).</summary>
    Refused,

    /// <summary>It connected but did not finish the handshake in time.</summary>
    TimedOut,

    /// <summary>The two machines have different pairing codes.</summary>
    PairingCodeMismatch,

    /// <summary>The other machine runs an incompatible RoboMouse version.</summary>
    VersionMismatch,

    /// <summary>The other machine has not allowed this one yet (it shows a request there).</summary>
    AwaitingApproval,

    /// <summary>The other machine removed or ignored this one.</summary>
    Blocked,

    /// <summary>The other machine switched this peer off.</summary>
    DisabledThere,

    /// <summary>The address is this PC.</summary>
    SameMachine,

    /// <summary>Anything else; see the message.</summary>
    Other
}

/// <summary>The last failed connection attempt to a peer.</summary>
public sealed record PeerConnectFailure(PeerFailureKind Kind, string Message, DateTime When)
{
    /// <summary>Turns a connect exception into a reason the UI can show.</summary>
    public static PeerConnectFailure From(Exception ex)
    {
        var (kind, message) = Classify(ex);
        return new PeerConnectFailure(kind, message, DateTime.Now);
    }

    public static (PeerFailureKind Kind, string Message) Classify(Exception ex)
    {
        var inner = ex is AggregateException agg ? agg.GetBaseException() : ex;
        return inner switch
        {
            ConnectionRejectedException r => r.Code switch
            {
                RejectCode.AwaitingApproval => (PeerFailureKind.AwaitingApproval, r.Message),
                RejectCode.Blocked => (PeerFailureKind.Blocked, r.Message),
                RejectCode.Disabled => (PeerFailureKind.DisabledThere, r.Message),
                RejectCode.SameMachine => (PeerFailureKind.SameMachine, r.Message),
                _ => (PeerFailureKind.Other, r.Message)
            },
            IncompatibleVersionException v => (PeerFailureKind.VersionMismatch, v.Message),
            PairingException => (PeerFailureKind.PairingCodeMismatch, "The pairing code doesn't match. Enter the same code on both machines (Settings > Network)."),
            OperationCanceledException => (PeerFailureKind.TimedOut, "No answer in time. Is RoboMouse running there, and is the port open in its firewall?"),
            SocketException s => s.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => (PeerFailureKind.Refused, "Connection refused: RoboMouse is not listening on that port."),
                SocketError.TimedOut or SocketError.HostUnreachable or SocketError.NetworkUnreachable
                    or SocketError.HostDown or SocketError.NetworkDown => (PeerFailureKind.Unreachable, "Unreachable. The machine may be off or asleep, or the address is wrong."),
                SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => (PeerFailureKind.Unreachable, "The host name could not be resolved."),
                _ => (PeerFailureKind.Other, s.Message)
            },
            _ => (PeerFailureKind.Other, inner.Message)
        };
    }
}

/// <summary>Which network port could not be opened.</summary>
public enum NetworkErrorKind
{
    /// <summary>The TCP port peers connect to.</summary>
    ListenPort,

    /// <summary>The UDP port used to find machines on the network.</summary>
    DiscoveryPort
}

/// <summary>A port that could not be opened at start or after a settings change. The app keeps running without it.</summary>
public sealed record NetworkStartError(NetworkErrorKind Kind, int Port, bool InUse, string Message)
{
    public static NetworkStartError From(NetworkErrorKind kind, int port, Exception ex)
    {
        var inUse = ex is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse or SocketError.AccessDenied };
        var what = kind == NetworkErrorKind.ListenPort ? "Port" : "Discovery port";
        var message = inUse
            ? $"{what} {port} is in use by another program. Change it in Settings > Network."
            : $"{what} {port} could not be opened: {ex.Message}";
        return new NetworkStartError(kind, port, inUse, message);
    }
}

/// <summary>A machine that has the pairing code but is not a configured peer, waiting for the user to allow or ignore it.</summary>
public sealed record PendingPeer(
    string MachineId,
    string MachineName,
    string Address,
    int Port,
    int ScreenWidth,
    int ScreenHeight,
    string MacAddress,
    DateTime RequestedAt);
