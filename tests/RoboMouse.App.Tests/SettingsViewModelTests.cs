using RoboMouse.App.ViewModels;
using Xunit;

namespace RoboMouse.App.Tests;

public class SettingsViewModelTests
{
    private static (SettingsViewModel Vm, FakeDialogs Dialogs, bool[] Closed) Create(Core.Configuration.AppSettings settings)
    {
        var dialogs = new FakeDialogs();
        var vm = new SettingsViewModel(settings, new FakeBackend(), dialogs, "1.0.0");
        var closed = new bool[1];
        vm.CloseRequested += (_, _) => closed[0] = true;
        return (vm, dialogs, closed);
    }

    [Fact]
    public async Task EmptyHotkey_IsRefused()
    {
        var settings = Samples.Settings();
        var (vm, dialogs, closed) = Create(settings);
        vm.SelectedPage = vm.Pages[2];
        vm.General.ToggleHotkey = "";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(closed[0]);
        Assert.Equal("Ctrl+Alt+M", settings.ToggleHotkey);
        Assert.Contains(dialogs.Messages, m => m.Contains("Choose a toggle hotkey"));
        Assert.Same(vm.General, vm.SelectedPage!.Page); // taken to the field that needs fixing
    }

    [Fact]
    public async Task InvalidHotkey_IsRefused()
    {
        var settings = Samples.Settings();
        var (vm, dialogs, closed) = Create(settings);
        vm.General.ToggleHotkey = "M";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(closed[0]);
        Assert.Contains(dialogs.Messages, m => m.Contains("at least one modifier"));
    }

    [Fact]
    public async Task ClearedPort_IsFlaggedAndRefused()
    {
        var settings = Samples.Settings();
        var (vm, dialogs, closed) = Create(settings);

        vm.Network.LocalPort = null;

        Assert.True(vm.Network.HasErrors);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.False(closed[0]);
        Assert.Equal(24800, settings.LocalPort);
        Assert.Same(vm.Network, vm.SelectedPage!.Page);
    }

    [Theory]
    [InlineData(0, true, App.LaunchWindow.PairingWizard)]   // first run / no peers: the pairing wizard
    [InlineData(0, false, App.LaunchWindow.PairingWizard)]
    [InlineData(1, true, App.LaunchWindow.None)]            // peers set up, start minimized: stay in the tray
    [InlineData(1, false, App.LaunchWindow.Settings)]       // peers set up, "start minimized" off
    internal void WindowAtLaunch(int peers, bool startMinimized, App.LaunchWindow expected)
    {
        var settings = Samples.Settings();
        settings.Peers.RemoveRange(peers, settings.Peers.Count - peers);
        settings.StartMinimized = startMinimized;

        Assert.Equal(expected, App.WindowAtLaunch(settings));
    }

    [Fact]
    public void FirewallScript_ScopesRulesToProgramAndPrivateNetworks_FromAnySubnet()
    {
        var script = NetworkPageViewModel.BuildFirewallScript(@"C:\Program Files\RoboMouse\RoboMouse.App.exe", 24800, 24801);

        Assert.Contains("program=\"C:\\Program Files\\RoboMouse\\RoboMouse.App.exe\"", script);
        Assert.Contains("localport=24800", script);
        Assert.Contains("localport=24801", script);
        Assert.Equal(2, CountOf(script, "profile=private,domain"));
        Assert.DoesNotContain("profile=any", script);
        // The button is for peers on another subnet, so no LocalSubnet restriction.
        Assert.DoesNotContain("remoteip", script);
    }

    [Fact]
    public async Task Save_AppliesEverythingWithoutRestart()
    {
        var settings = Samples.Settings();
        var backend = new FakeBackend();
        var vm = new SettingsViewModel(settings, backend, new FakeDialogs(), "1.0.0");
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;
        vm.Network.LocalPort = 25000;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(closed);
        Assert.Equal(25000, settings.LocalPort);
        Assert.Equal(1, backend.NetworkApplied);
        Assert.Equal(1, backend.ClipboardApplied);
        Assert.Equal(0, backend.PairingApplied); // code unchanged: connections are kept
        Assert.True(backend.Saves > 0);
    }

    [Fact]
    public async Task Save_WithNewPairingCode_DropsConnectionsMadeWithTheOldOne()
    {
        var settings = Samples.Settings();
        var backend = new FakeBackend();
        var vm = new SettingsViewModel(settings, backend, new FakeDialogs(), "1.0.0");
        vm.Network.EnteredCode = "abcd efgh jkmn";
        vm.Network.UseEnteredCodeCommand.Execute(null);
        Assert.True(vm.Network.PairingCodeChanged);

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("ABCD-EFGH-JKMN", settings.PairingCode);
        Assert.Equal(1, backend.PairingApplied);
    }

    [Fact]
    public async Task Save_WritesClipboardControls()
    {
        var settings = Samples.Settings();
        var vm = new SettingsViewModel(settings, new FakeBackend(), new FakeDialogs(), "1.0.0");
        vm.General.SyncImages = false;
        vm.General.SyncText = true;
        vm.General.ShareFiles = false;
        vm.General.ClipboardMaxMegabytes = 25;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(settings.Clipboard.SyncImages);
        Assert.True(settings.Clipboard.SyncText);
        Assert.False(settings.Clipboard.SyncFiles);
        Assert.Equal(25L * 1024 * 1024, settings.Clipboard.MaxSizeBytes);
    }

    [Fact]
    public async Task ClearedClipboardSize_IsRefused()
    {
        var settings = Samples.Settings();
        var (vm, dialogs, closed) = Create(settings);
        vm.SelectedPage = vm.Pages[2];
        vm.General.ClipboardMaxMegabytes = null;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(closed[0]);
        Assert.Same(vm.General, vm.SelectedPage!.Page);
        Assert.Contains(dialogs.Messages, m => m.Contains("General page"));
    }

    [Fact]
    public void AllowingAPendingPeer_OpensTheLayoutPage()
    {
        var settings = Samples.Settings();
        var backend = new FakeBackend { Settings = settings };
        backend.Pending.Add(new Core.PendingPeer("studio", "STUDIO-PC", "192.168.1.42", 24800, 3840, 2160, "", DateTime.Now));
        var vm = new SettingsViewModel(settings, backend, new FakeDialogs(), "1.0.0");
        vm.ShowPage(SettingsPage.Peers);

        vm.Peers.Pending.Single().AllowCommand.Execute(null);

        Assert.Same(vm.Layout, vm.SelectedPage!.Page);
        Assert.Contains(vm.Layout.Placements, p => p.Peer.Id == "studio" && p.Position == Core.Configuration.ScreenPosition.Top);
    }

    private static int CountOf(string text, string part)
    {
        var count = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + 1, StringComparison.Ordinal))
            count++;
        return count;
    }
}
