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
    [InlineData(0, true, true)]    // first run / no peers: always open Settings
    [InlineData(0, false, true)]
    [InlineData(1, true, false)]   // peers set up, start minimized: stay in the tray
    [InlineData(1, false, true)]   // peers set up, "start minimized" off
    public void SettingsAtLaunch(int peers, bool startMinimized, bool expected)
    {
        var settings = Samples.Settings();
        settings.Peers.RemoveRange(peers, settings.Peers.Count - peers);
        settings.StartMinimized = startMinimized;

        Assert.Equal(expected, App.ShouldShowSettingsAtLaunch(settings));
    }

    [Fact]
    public void FirewallScript_ScopesRulesToProgramSubnetAndPrivateNetworks()
    {
        var script = NetworkPageViewModel.BuildFirewallScript(@"C:\Program Files\RoboMouse\RoboMouse.App.exe", 24800, 24801);

        Assert.Contains("program=\"C:\\Program Files\\RoboMouse\\RoboMouse.App.exe\"", script);
        Assert.Contains("localport=24800", script);
        Assert.Contains("localport=24801", script);
        Assert.Equal(2, CountOf(script, "remoteip=localsubnet profile=private,domain"));
        Assert.DoesNotContain("profile=any", script);
        Assert.DoesNotContain("remoteip=any", script);
    }

    private static int CountOf(string text, string part)
    {
        var count = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + 1, StringComparison.Ordinal))
            count++;
        return count;
    }
}
