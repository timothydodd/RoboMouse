using RoboMouse.App.Services;
using RoboMouse.App.ViewModels;
using RoboMouse.Core;
using RoboMouse.Core.Configuration;
using Xunit;

namespace RoboMouse.App.Tests;

public class NotificationTests
{
    [Fact]
    public void PendingPeer_OffersAllowAndIgnore_AndStaysUp()
    {
        var allowed = false;
        var ignored = false;
        var toast = Notifications.PendingPeer(new PendingPeer("studio", "STUDIO-PC", "192.168.1.42", 24800, 1, 1, "", DateTime.Now),
            () => allowed = true, () => ignored = true);

        Assert.Equal("STUDIO-PC wants to connect", toast.Title);
        Assert.Equal(Notifications.PendingKey("studio"), toast.Key);
        Assert.Null(toast.Duration);
        Assert.Equal(new[] { "Allow", "Ignore" }, toast.Actions.Select(a => a.Label));

        var vm = new ToastViewModel(toast);
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;
        vm.Actions[0].RunCommand.Execute(null);

        Assert.True(allowed);
        Assert.False(ignored);
        Assert.True(closed);
    }

    [Fact]
    public void PortInUse_PointsToNetworkSettings()
    {
        var opened = false;
        var toast = Notifications.NetworkError(new NetworkStartError(NetworkErrorKind.ListenPort, 24800, true, "Port 24800 is in use by another program."), () => opened = true);

        Assert.Equal("Port 24800 is in use", toast.Title);
        Assert.Contains("cannot connect to this PC", toast.Message);
        toast.Actions.Single().Invoke();
        Assert.True(opened);
    }

    [Theory]
    [InlineData(SettingsLoadNotice.None, null)]
    [InlineData(SettingsLoadNotice.RestoredFromBackup, "Settings restored from backup")]
    [InlineData(SettingsLoadNotice.Reset, "Settings were reset")]
    public void SettingsLoadNotice_ShownOnlyWhenSomethingHappened(SettingsLoadNotice notice, string? title)
    {
        var toast = Notifications.SettingsLoad(notice, @"C:\Users\me\AppData\Roaming\RoboMouse\settings.corrupt-20260101-120000.json", () => { });

        Assert.Equal(title, toast?.Title);
        if (toast != null)
            Assert.Contains("settings.corrupt-20260101-120000.json", toast.Message);
    }

    [Fact]
    public void Update_NamesTheVersions()
    {
        var toast = Notifications.UpdateAvailable(new UpdateInfo(new Version(1, 2, 0), UpdateChecker.ReleasesPage), new Version(1, 1, 4), () => { });

        Assert.Equal("RoboMouse 1.2.0 is available", toast.Title);
        Assert.Contains("1.1.4", toast.Message);
        Assert.Equal("Download", toast.Actions.Single().Label);
    }

    [Fact]
    public void Throttle_LetsOneThroughPerInterval()
    {
        var throttle = new NotificationThrottle(TimeSpan.FromMinutes(1));
        var start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(throttle.ShouldShow("a", start));
        Assert.False(throttle.ShouldShow("a", start.AddSeconds(30)));
        Assert.True(throttle.ShouldShow("b", start.AddSeconds(30)));
        Assert.True(throttle.ShouldShow("a", start.AddSeconds(61)));
    }
}
