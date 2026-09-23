using System.Net;
using System.Net.Sockets;
using RoboMouse.Core.Configuration;
using RoboMouse.Core.Network;
using RoboMouse.Core.Network.Protocol;
using Xunit;

namespace RoboMouse.Core.Tests;

public class AcceptPolicyTests
{
    private static readonly IPAddress Somewhere = IPAddress.Parse("10.0.0.9");

    private static AppSettings Settings() => new()
    {
        MachineId = "me",
        MachineName = "DODD-MAIN",
        Peers =
        {
            new PeerConfig { Id = "work", Name = "TIM-WORK", Address = "10.0.0.2", Port = 24800 },
            new PeerConfig { Id = "off", Name = "OLD-PC", Address = "10.0.0.3", Enabled = false },
            new PeerConfig { Id = "not-learned-yet", Name = "10.0.0.4", Address = "10.0.0.4", Port = 24800 }
        },
        BlockedMachineIds = { "removed" }
    };

    private static AcceptDecision Decide(string id, ConnectionKind kind = ConnectionKind.Control, IPAddress? address = null,
        int port = 24800, bool ignored = false) =>
        AcceptPolicy.Decide(Settings(), id, kind, address ?? Somewhere, port, ignored, out _);

    [Fact]
    public void ConfiguredEnabledPeer_IsAccepted() =>
        Assert.Equal(AcceptDecision.Accept, Decide("work"));

    [Fact]
    public void UnknownMachine_IsPending() =>
        Assert.Equal(AcceptDecision.Pending, Decide("stranger"));

    [Fact]
    public void UnknownProbe_IsPending_AndAnswered()
    {
        Assert.Equal(AcceptDecision.Pending, Decide("stranger", ConnectionKind.Probe));
        Assert.Null(AcceptPolicy.RejectReason(AcceptDecision.Pending, ConnectionKind.Probe, "DODD-MAIN"));
        Assert.NotNull(AcceptPolicy.RejectReason(AcceptDecision.Pending, ConnectionKind.Control, "DODD-MAIN"));
    }

    [Fact]
    public void DisabledPeer_IsRejected()
    {
        Assert.Equal(AcceptDecision.RejectDisabled, Decide("off"));
        Assert.Equal(AcceptDecision.RejectDisabled, Decide("off", ConnectionKind.Transfer));
    }

    [Fact]
    public void RemovedPeer_IsBlocked() =>
        Assert.Equal(AcceptDecision.RejectBlocked, Decide("removed"));

    [Fact]
    public void OwnMachineId_IsRejected() =>
        Assert.Equal(AcceptDecision.RejectSelf, Decide("me"));

    [Fact]
    public void IgnoredMachine_IsRejectedQuietly() =>
        Assert.Equal(AcceptDecision.RejectIgnored, Decide("stranger", ignored: true));

    [Fact]
    public void TransferFromUnknownMachine_IsRejected() =>
        Assert.Equal(AcceptDecision.RejectNotPeer, Decide("stranger", ConnectionKind.Transfer));

    [Fact]
    public void ReaddedByHand_WinsOverTheBlockedList()
    {
        var settings = Settings();
        settings.Peers.Add(new PeerConfig { Id = "removed", Name = "BACK-AGAIN" });

        Assert.Equal(AcceptDecision.Accept, AcceptPolicy.Decide(settings, "removed", ConnectionKind.Control, Somewhere, 24800, false, out _));
    }

    [Fact]
    public void PeerConfiguredByAddress_IsRecognisedBeforeItsIdIsKnown()
    {
        var decision = AcceptPolicy.Decide(Settings(), "real-id", ConnectionKind.Control,
            IPAddress.Parse("10.0.0.4").MapToIPv6(), 24800, false, out var matched);

        Assert.Equal(AcceptDecision.Accept, decision);
        Assert.Equal("not-learned-yet", matched!.Id);

        // Same address, different listen port: someone else.
        Assert.Equal(AcceptDecision.Pending, Decide("real-id", address: IPAddress.Parse("10.0.0.4"), port: 25000));
    }

    [Fact]
    public void RejectReasons_CarryTheRightCode()
    {
        Assert.Equal(RejectCode.AwaitingApproval, RejectReasons.Parse(AcceptPolicy.RejectReason(AcceptDecision.Pending, ConnectionKind.Control, "X")).Code);
        Assert.Equal(RejectCode.Blocked, RejectReasons.Parse(AcceptPolicy.RejectReason(AcceptDecision.RejectBlocked, ConnectionKind.Control, "X")).Code);
        Assert.Equal(RejectCode.Disabled, RejectReasons.Parse(AcceptPolicy.RejectReason(AcceptDecision.RejectDisabled, ConnectionKind.Control, "X")).Code);
        Assert.Equal(RejectCode.SameMachine, RejectReasons.Parse(AcceptPolicy.RejectReason(AcceptDecision.RejectSelf, ConnectionKind.Control, "X")).Code);
    }
}

public class PeerConnectFailureTests
{
    [Fact]
    public void Classify_MapsConnectExceptionsToReasons()
    {
        Assert.Equal(PeerFailureKind.PairingCodeMismatch, PeerConnectFailure.Classify(new PairingException("x")).Kind);
        Assert.Equal(PeerFailureKind.VersionMismatch, PeerConnectFailure.Classify(new IncompatibleVersionException("x")).Kind);
        Assert.Equal(PeerFailureKind.TimedOut, PeerConnectFailure.Classify(new OperationCanceledException()).Kind);
        Assert.Equal(PeerFailureKind.Refused, PeerConnectFailure.Classify(new SocketException((int)SocketError.ConnectionRefused)).Kind);
        Assert.Equal(PeerFailureKind.Unreachable, PeerConnectFailure.Classify(new SocketException((int)SocketError.HostUnreachable)).Kind);
        Assert.Equal(PeerFailureKind.AwaitingApproval, PeerConnectFailure.Classify(new ConnectionRejectedException(RejectCode.AwaitingApproval, "x")).Kind);
        Assert.Equal(PeerFailureKind.Blocked, PeerConnectFailure.Classify(new ConnectionRejectedException(RejectCode.Blocked, "x")).Kind);
        Assert.Equal(PeerFailureKind.Other, PeerConnectFailure.Classify(new InvalidOperationException("odd")).Kind);
    }

    [Fact]
    public void PortInUse_SaysSo()
    {
        var error = NetworkStartError.From(NetworkErrorKind.ListenPort, 24800, new SocketException((int)SocketError.AddressAlreadyInUse));

        Assert.True(error.InUse);
        Assert.Contains("24800 is in use", error.Message);
    }
}
