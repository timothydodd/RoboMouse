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
            new PeerConfig { Id = "not-learned-yet", Name = "10.0.0.4", Address = "10.0.0.4", Port = 24800, HasConnected = false }
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
    public void PeerThatHasConnected_IsMatchedByIdOnly()
    {
        // A new machine that got a known peer's old address is a new request, not that peer.
        var settings = Settings();
        settings.Peers[0].IdentityKey = "S0VZLVdPUks=";

        var decision = AcceptPolicy.Decide(settings, "newcomer", ConnectionKind.Control, IPAddress.Parse("10.0.0.2"), 24800,
            false, out var matched, identityKey: "TkVX");

        Assert.Equal(AcceptDecision.Pending, decision);
        Assert.Null(matched);
    }

    [Fact]
    public void PeerThatHasNeverConnected_IsStillMatchedByAddress_WithAnyKey()
    {
        var decision = AcceptPolicy.Decide(Settings(), "real-id", ConnectionKind.Control, IPAddress.Parse("10.0.0.4"), 24800,
            false, out var matched, identityKey: "TkVX");

        Assert.Equal(AcceptDecision.Accept, decision);
        Assert.Equal("not-learned-yet", matched!.Id);
    }

    [Fact]
    public void ConfigFromAnOlderVersion_WithoutTheFlag_CountsAsConnected()
    {
        // 1.1.x settings have no HasConnected; their ids were learned by connecting.
        var json = """{ "peers": [ { "id": "work", "name": "TIM-WORK", "address": "10.0.0.2", "port": 24800 } ] }""";
        var settings = System.Text.Json.JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings)!;
        settings.MachineId = "me";

        Assert.True(settings.Peers[0].HasConnected);
        Assert.Equal(AcceptDecision.Pending, AcceptPolicy.Decide(settings, "newcomer", ConnectionKind.Control,
            IPAddress.Parse("10.0.0.2"), 24800, false, out _));
        Assert.Equal(AcceptDecision.Accept, AcceptPolicy.Decide(settings, "work", ConnectionKind.Control,
            IPAddress.Parse("10.0.0.7"), 24800, false, out _));
    }

    [Fact]
    public void PinOrMatch_PinsAnEmptyEntry_AndThenOnlyMatchesThatKey()
    {
        var peer = new PeerConfig { Id = "work" };
        var gate = new object();

        Assert.True(AcceptPolicy.PinOrMatch(peer, "QUFB", gate, out var pinned));
        Assert.True(pinned);
        Assert.True(AcceptPolicy.PinOrMatch(peer, "QUFB", gate, out pinned));
        Assert.False(pinned);
        Assert.False(AcceptPolicy.PinOrMatch(peer, "QkJC", gate, out pinned));
        Assert.False(pinned);
        Assert.Equal("QUFB", peer.IdentityKey);
    }

    [Fact]
    public void PinOrMatch_ConcurrentConnectionsWithDifferentKeys_OnlyOneIsAccepted()
    {
        for (var round = 0; round < 200; round++)
        {
            var peer = new PeerConfig { Id = "work" };
            var gate = new object();
            using var start = new Barrier(2);
            var results = new bool[2];
            var threads = Enumerable.Range(0, 2).Select(i => new Thread(() =>
            {
                start.SignalAndWait();
                results[i] = AcceptPolicy.PinOrMatch(peer, i == 0 ? "QUFB" : "QkJC", gate, out _);
            })).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());

            Assert.Single(results, r => r);
            Assert.Equal(results[0] ? "QUFB" : "QkJC", peer.IdentityKey);
        }
    }

    private static PendingPeer Request(string key, string address = "10.0.0.9", int minutes = 0) =>
        new("id", "LAPTOP", address, 24800, 1920, 1080, string.Empty, new DateTime(2026, 9, 1, 12, minutes, 0)) { IdentityKey = key };

    [Fact]
    public void PendingRequest_FromTheSameKey_RefreshesTheAddress_AndKeepsTheFirstTime()
    {
        var pending = new Dictionary<string, PendingPeer>();

        Assert.Equal(PendingUpdate.Added, PendingRequests.Register(pending, Request("QUFB")));
        Assert.Equal(PendingUpdate.Refreshed, PendingRequests.Register(pending, Request("QUFB", "10.0.0.10", minutes: 5)));

        var entry = pending["id"];
        Assert.Equal("10.0.0.10", entry.Address);
        Assert.Equal(0, entry.RequestedAt.Minute);
        Assert.Equal("QUFB", entry.IdentityKey);
    }

    [Fact]
    public void PendingRequest_WithTheSameIdButAnotherKey_NeverReplacesTheFirstKey()
    {
        var pending = new Dictionary<string, PendingPeer>();
        PendingRequests.Register(pending, Request("QUFB"));

        // An impostor using the same machine id: the entry is dropped rather than taking its key.
        Assert.Equal(PendingUpdate.Conflict, PendingRequests.Register(pending, Request("QkJC", "10.0.0.66")));
        Assert.Empty(pending);

        // Only a fresh request brings it back, and then with that request's key as the first.
        Assert.Equal(PendingUpdate.Added, PendingRequests.Register(pending, Request("QUFB")));
        Assert.Equal("QUFB", pending["id"].IdentityKey);
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
