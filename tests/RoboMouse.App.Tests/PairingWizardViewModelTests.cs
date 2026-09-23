using System.Net;
using RoboMouse.App.ViewModels;
using RoboMouse.Core;
using RoboMouse.Core.Configuration;
using RoboMouse.Core.Network;
using Xunit;

namespace RoboMouse.App.Tests;

public class PairingWizardViewModelTests
{
    private static AppSettings Empty() => new() { MachineId = "this-pc", MachineName = "DESKTOP", PairingCode = "K7PQ-M2XW-9DHR" };

    private static async Task Finish(PairingWizardViewModel wizard)
    {
        while (wizard.Step < PairingWizardViewModel.StepCount - 1)
            await wizard.NextCommand.ExecuteAsync(null);
        await wizard.NextCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task DiscoveredMachine_IsAddedOnTheChosenEdge_AndConnected()
    {
        var settings = Empty();
        var backend = new FakeBackend();
        backend.Discovered.Add(new DiscoveredPeer { MachineId = "studio", MachineName = "STUDIO-PC", Address = IPAddress.Parse("192.168.1.42"), Port = 24800, ScreenWidth = 3840, ScreenHeight = 2160 });
        var wizard = new PairingWizardViewModel(settings, backend, new FakeDialogs());
        var closed = false;
        wizard.CloseRequested += (_, _) => closed = true;

        await wizard.NextCommand.ExecuteAsync(null);
        Assert.False(wizard.NextCommand.CanExecute(null)); // nothing picked yet
        wizard.SelectedMachine = wizard.Machines.Single();
        await wizard.NextCommand.ExecuteAsync(null);
        wizard.SelectedPosition = wizard.Positions.First(p => p.Position == ScreenPosition.Left);
        await wizard.NextCommand.ExecuteAsync(null);

        Assert.True(closed);
        var peer = Assert.Single(settings.Peers);
        Assert.Equal("studio", peer.Id);
        Assert.Equal(ScreenPosition.Left, peer.Position);
        Assert.Contains(peer, backend.Connected);
        Assert.Same(peer, wizard.Result);
    }

    [Fact]
    public async Task MachineAskingToConnect_IsAllowed()
    {
        var settings = Empty();
        var backend = new FakeBackend { Settings = settings };
        backend.Pending.Add(new PendingPeer("studio", "STUDIO-PC", "192.168.1.42", 24800, 1920, 1080, "", DateTime.Now));
        var wizard = new PairingWizardViewModel(settings, backend, new FakeDialogs());

        await wizard.NextCommand.ExecuteAsync(null);
        var request = wizard.Machines.Single();
        Assert.True(request.IsRequest);
        wizard.SelectedMachine = request;
        await wizard.NextCommand.ExecuteAsync(null);
        wizard.SelectedPosition = wizard.Positions.First(p => p.Position == ScreenPosition.Bottom);
        await wizard.NextCommand.ExecuteAsync(null);

        Assert.Equal(ScreenPosition.Bottom, settings.Peers.Single().Position);
        Assert.Empty(backend.Pending);
        Assert.Empty(backend.Connected); // the service connects allowed peers itself
    }

    [Fact]
    public async Task TypedAddress_IsAdded_EvenWhenItCannotConnectYet()
    {
        var settings = Empty();
        var backend = new FakeBackend { ConnectError = new ConnectionRejectedException(RejectCode.AwaitingApproval, "Waiting for LAPTOP to allow this PC") };
        var dialogs = new FakeDialogs();
        var wizard = new PairingWizardViewModel(settings, backend, dialogs);

        await wizard.NextCommand.ExecuteAsync(null);
        wizard.ManualAddress = "192.168.1.20";
        await Finish(wizard);

        Assert.Equal("192.168.1.20", settings.Peers.Single().Address);
        Assert.Contains(dialogs.Messages, m => m.Contains("Waiting for LAPTOP to allow this PC") && m.Contains("Allow"));
    }

    [Fact]
    public void OtherPcsCode_IsSavedAndApplied()
    {
        var settings = Empty();
        var backend = new FakeBackend();
        var wizard = new PairingWizardViewModel(settings, backend, new FakeDialogs());

        wizard.EnteredCode = "nope";
        wizard.UseEnteredCodeCommand.Execute(null);
        Assert.NotNull(wizard.EnteredCodeError);
        Assert.Equal("K7PQ-M2XW-9DHR", settings.PairingCode);

        wizard.EnteredCode = "2345 6789 ABCD";
        wizard.UseEnteredCodeCommand.Execute(null);
        Assert.Equal("2345-6789-ABCD", settings.PairingCode);
        Assert.Equal("2345-6789-ABCD", wizard.PairingCode);
        Assert.Equal(1, backend.PairingApplied);
        Assert.Equal(1, backend.Saves);
    }

    [Fact]
    public void ThisPcAndConfiguredPeers_AreNotOffered()
    {
        var settings = Samples.Settings();
        var backend = new FakeBackend();
        backend.Discovered.Add(new DiscoveredPeer { MachineId = "this-pc", MachineName = "DESKTOP", Address = IPAddress.Loopback });
        backend.Discovered.Add(new DiscoveredPeer { MachineId = "laptop", MachineName = "Laptop", Address = IPAddress.Parse("192.168.1.20") });

        var wizard = new PairingWizardViewModel(settings, backend, new FakeDialogs());

        Assert.Empty(wizard.Machines);
    }
}
