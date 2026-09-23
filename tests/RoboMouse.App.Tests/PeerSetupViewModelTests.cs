using RoboMouse.App.ViewModels;
using RoboMouse.Core.Configuration;
using Xunit;

namespace RoboMouse.App.Tests;

public class PeerSetupViewModelTests
{
    private static PeerSetupViewModel NewPeer(AppSettings settings, FakeDialogs dialogs, PeerConfig? draft = null) =>
        new(draft, settings, new FakeBackend(), dialogs);

    [Fact]
    public async Task SameAddressAndPort_IsRefused()
    {
        var settings = Samples.Settings();
        var dialogs = new FakeDialogs();
        var vm = NewPeer(settings, dialogs);
        vm.Name = "Laptop again";
        vm.Address = " 192.168.1.20 ";
        vm.Port = 24800;

        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.Null(vm.Result);
        Assert.Contains(dialogs.Messages, m => m.Contains("Laptop already uses 192.168.1.20:24800"));
    }

    [Fact]
    public async Task SameAddressOtherPort_IsAllowed()
    {
        var settings = Samples.Settings();
        var vm = NewPeer(settings, new FakeDialogs());
        vm.Name = "Second instance";
        vm.Address = "192.168.1.20";
        vm.Port = 24900;

        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.NotNull(vm.Result);
    }

    [Fact]
    public async Task SameMachineId_IsRefused()
    {
        var settings = Samples.Settings();
        var dialogs = new FakeDialogs();
        // Discovery found the laptop again under a new address.
        var draft = new PeerConfig { Id = "laptop", Name = "LAPTOP", Address = "10.0.0.5", Position = ScreenPosition.Top };
        var vm = NewPeer(settings, dialogs, draft);

        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.Null(vm.Result);
        Assert.Contains(dialogs.Messages, m => m.Contains("already set up as Laptop"));
    }

    [Fact]
    public async Task ThisPc_IsRefused()
    {
        var settings = Samples.Settings();
        var dialogs = new FakeDialogs();
        var draft = new PeerConfig { Id = settings.MachineId, Name = "DESKTOP", Address = "127.0.0.1", Position = ScreenPosition.Top };
        var vm = NewPeer(settings, dialogs, draft);

        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.Null(vm.Result);
        Assert.Contains(dialogs.Messages, m => m.Contains("this PC"));
    }

    [Fact]
    public async Task EditingAPeer_IsNotItsOwnDuplicate()
    {
        var settings = Samples.Settings();
        var vm = new PeerSetupViewModel(settings.Peers[0], settings, new FakeBackend(), new FakeDialogs());
        vm.Name = "Laptop (renamed)";

        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.Same(settings.Peers[0], vm.Result);
        Assert.Equal("Laptop (renamed)", settings.Peers[0].Name);
    }

    [Fact]
    public void DiscoveredPeer_IsAddedNotEdited()
    {
        var settings = Samples.Settings();
        var draft = new PeerConfig { Id = "studio", Name = "STUDIO", Address = "192.168.1.42", Position = ScreenPosition.Top };

        var vm = NewPeer(settings, new FakeDialogs(), draft);

        Assert.Equal("Add peer", vm.Title);
        Assert.Equal("Add", vm.ConfirmText);
        Assert.Equal(ScreenPosition.Top, vm.SelectedPosition.Position);
    }

    [Fact]
    public void NewPeer_DefaultsToFirstFreeEdge()
    {
        var settings = Samples.Settings(); // left and right are taken

        var vm = NewPeer(settings, new FakeDialogs());

        Assert.Equal(ScreenPosition.Top, vm.SelectedPosition.Position);
    }

    [Fact]
    public async Task ClearedPort_ShowsErrorAndIsRefused()
    {
        var settings = Samples.Settings();
        var dialogs = new FakeDialogs();
        var vm = NewPeer(settings, dialogs);
        vm.Name = "Studio";
        vm.Address = "192.168.1.42";

        vm.Port = null;

        Assert.True(vm.HasErrors);
        Assert.NotEmpty(vm.GetErrors(nameof(vm.Port)).Cast<object>());
        await vm.ConfirmCommand.ExecuteAsync(null);
        Assert.Null(vm.Result);

        vm.Port = 24800;
        Assert.False(vm.HasErrors);
    }

    [Fact]
    public async Task NewPeerOnTakenEdge_SwapMovesTheOtherToAFreeEdge()
    {
        var settings = Samples.Settings();
        var dialogs = new FakeDialogs { Answer = Services.DialogResult.Yes };
        var vm = NewPeer(settings, dialogs);
        vm.Name = "Studio";
        vm.Address = "192.168.1.42";
        vm.SelectedPosition = vm.Positions.First(p => p.Position == ScreenPosition.Left);

        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.NotNull(vm.Result);
        Assert.Equal(ScreenPosition.Left, vm.Result.Position);
        Assert.Equal(ScreenPosition.Top, settings.Peers[0].Position);
    }
}
