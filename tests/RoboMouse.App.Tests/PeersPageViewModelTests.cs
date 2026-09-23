using RoboMouse.App.ViewModels;
using RoboMouse.Core;
using RoboMouse.Core.Configuration;
using Xunit;

namespace RoboMouse.App.Tests;

public class PeersPageViewModelTests
{
    private static PendingPeer Studio() => new("studio", "STUDIO-PC", "192.168.1.42", 24800, 3840, 2160, "", DateTime.Now);

    [Fact]
    public void PendingRequests_AreListed_AndAllowAddsThePeer()
    {
        var settings = Samples.Settings();
        var backend = new FakeBackend { Settings = settings };
        backend.Pending.Add(Studio());
        var page = new PeersPageViewModel(settings, backend, new FakeDialogs());
        PeerConfig? allowed = null;
        page.PeerAllowed += (_, peer) => allowed = peer;

        Assert.True(page.HasPending);
        page.Pending.Single().AllowCommand.Execute(null);

        Assert.NotNull(allowed);
        Assert.Equal(ScreenPosition.Top, allowed!.Position); // first free edge
        Assert.False(page.HasPending);
        Assert.Contains(page.Peers, p => p.Peer.Id == "studio");
    }

    [Fact]
    public void Ignore_DropsTheRequest()
    {
        var settings = Samples.Settings();
        var backend = new FakeBackend { Settings = settings };
        backend.Pending.Add(Studio());
        var page = new PeersPageViewModel(settings, backend, new FakeDialogs());

        page.Pending.Single().IgnoreCommand.Execute(null);

        Assert.Contains("studio", backend.Ignored);
        Assert.Empty(page.Pending);
        Assert.DoesNotContain(settings.Peers, p => p.Id == "studio");
    }

    [Fact]
    public async Task Remove_GoesThroughTheService_SoTheMachineIsBlocked()
    {
        var settings = Samples.Settings();
        var backend = new FakeBackend { Settings = settings };
        var page = new PeersPageViewModel(settings, backend, new FakeDialogs());
        page.SelectedPeer = page.Peers[0];

        await page.RemovePeerCommand.ExecuteAsync(null);

        Assert.Single(backend.Removed);
        Assert.Equal("laptop", backend.Removed[0].Id);
        Assert.Contains("laptop", settings.BlockedMachineIds);
        Assert.Single(page.Peers);
    }

    [Fact]
    public void Refresh_FollowsPeersTheServiceAdded()
    {
        var settings = Samples.Settings();
        var page = new PeersPageViewModel(settings, new FakeBackend(), new FakeDialogs());
        var changed = 0;
        page.PeersChanged += (_, _) => changed++;

        settings.Peers.Add(new PeerConfig { Id = "studio", Name = "STUDIO-PC", Position = ScreenPosition.Top });
        page.Refresh();

        Assert.Equal(3, page.Peers.Count);
        Assert.Equal(1, changed);
    }

    [Theory]
    [InlineData(PeerFailureKind.PairingCodeMismatch, "Pairing code doesn't match", StatusTone.Error)]
    [InlineData(PeerFailureKind.AwaitingApproval, "Waiting for approval on Laptop", StatusTone.Warning)]
    [InlineData(PeerFailureKind.Blocked, "Blocked on Laptop", StatusTone.Error)]
    [InlineData(PeerFailureKind.VersionMismatch, "Different RoboMouse version", StatusTone.Error)]
    [InlineData(PeerFailureKind.Unreachable, "Unreachable", StatusTone.Warning)]
    [InlineData(PeerFailureKind.Refused, "RoboMouse not running there", StatusTone.Warning)]
    internal void Status_SaysWhyAPeerIsNotConnected(PeerFailureKind kind, string text, StatusTone tone)
    {
        var settings = Samples.Settings();
        var backend = new FakeBackend();
        backend.Failures["laptop"] = new PeerConnectFailure(kind, "details", DateTime.Now);
        var page = new PeersPageViewModel(settings, backend, new FakeDialogs());

        var row = page.Peers.Single(p => p.Peer.Id == "laptop");
        Assert.Equal(text, row.StatusText);
        Assert.Equal(tone, row.StatusTone);
        Assert.Equal("details", row.StatusDetail);
    }

    [Fact]
    public void Status_ConnectedOrDisabled_IgnoresOldFailures()
    {
        var peer = new PeerConfig { Name = "Laptop" };
        var failure = new PeerConnectFailure(PeerFailureKind.Unreachable, "x", DateTime.Now);

        Assert.Equal("Connected · 4 ms", PeerItemViewModel.Describe(peer, new Services.ConnectedPeerInfo("laptop", "Laptop", 4), failure).Text);
        peer.Enabled = false;
        Assert.Equal("Disabled", PeerItemViewModel.Describe(peer, null, failure).Text);
    }
}
