using System.Drawing;
using RoboMouse.App.ViewModels;
using RoboMouse.Core.Configuration;
using RoboMouse.Core.Screen;
using Xunit;

namespace RoboMouse.App.Tests;

public class LayoutPageViewModelTests
{
    // This PC: one 1920x1080 display at the origin (the fake backend's default).
    private static MonitorRect Remote(string id, int x, int y, int w, int h, bool primary = false) =>
        new(new Rectangle(x, y, w, h), default, primary, id);

    private static MonitorPlacement Placed(string id, int x, int y, int w = 1920, int h = 1080, int remoteX = 0) =>
        new() { Id = id, X = x, Y = y, Width = w, Height = h, RemoteX = remoteX, RemoteWidth = w, RemoteHeight = h, Primary = remoteX == 0 };

    /// <summary>The laptop is connected with two monitors, one each side of this PC; the Mac is offline, below.</summary>
    private static (AppSettings Settings, FakeBackend Backend) Arranged()
    {
        var settings = Samples.Settings();
        settings.Peers[0].Monitors = new() { Placed("1", -1920, 0), Placed("2", 1920, 0, remoteX: 1920) };
        settings.Peers[1].Monitors = new() { Placed("1", 0, 1080) };
        var backend = new FakeBackend { Settings = settings };
        backend.PeerMonitors["laptop"] = new() { Remote("1", 0, 0, 1920, 1080, primary: true), Remote("2", 1920, 0, 1920, 1080) };
        return (settings, backend);
    }

    private static LayoutItem Item(LayoutPageViewModel layout, string peerId, string monitorId) =>
        layout.Items.Single(i => i.Peer?.Id == peerId && i.MonitorId == monitorId);

    [Fact]
    public void EveryMonitor_IsItsOwnScreen()
    {
        var (settings, backend) = Arranged();
        var layout = new LayoutPageViewModel(settings, backend);

        Assert.Single(layout.Items, i => i.IsLocal);
        var one = Item(layout, "laptop", "1");
        var two = Item(layout, "laptop", "2");
        Assert.Equal(new Rectangle(-1920, 0, 1920, 1080), one.Rect);
        Assert.Equal(new Rectangle(1920, 0, 1920, 1080), two.Rect);
        Assert.Equal("Laptop 2", two.Label);
        Assert.True(one.IsConnected);
        Assert.False(Item(layout, "mac", "1").IsConnected);
    }

    [Fact]
    public void Move_SnapsAgainstAScreen_AndNeverOverlaps()
    {
        var (settings, backend) = Arranged();
        var layout = new LayoutPageViewModel(settings, backend);
        var two = Item(layout, "laptop", "2");

        // Dropped on top of this PC, a little up and left of its top edge.
        var placed = layout.Move(two, new Point(-50, -900));

        Assert.Equal(new Rectangle(0, -1080, 1920, 1080), placed); // above this PC, lined up
        Assert.False(VirtualDesktop.OverlapsAny(placed, layout.Items.Where(i => i != two).Select(i => i.Rect)));
    }

    [Fact]
    public void Move_WithoutSave_LeavesConfigsAlone_AndSaveWritesIt()
    {
        var (settings, backend) = Arranged();
        var layout = new LayoutPageViewModel(settings, backend);
        layout.Move(Item(layout, "laptop", "2"), new Point(0, -1080));

        Assert.True(layout.IsModified);
        Assert.Equal(1920, settings.Peers[0].Monitors[1].X);
        Assert.Equal(1920, Item(new LayoutPageViewModel(settings, backend), "laptop", "2").X);

        var before = settings.Peers[0].Monitors;
        layout.Save();

        Assert.False(layout.IsModified);
        Assert.Equal((0, -1080), (settings.Peers[0].Monitors[1].X, settings.Peers[0].Monitors[1].Y));
        Assert.NotSame(before, settings.Peers[0].Monitors); // replaced, never changed in place
        Assert.Equal(1920, before[1].X);
    }

    [Fact]
    public void Reload_KeepsUnsavedMoves_ButFollowsPlacementsChangedElsewhere()
    {
        var (settings, backend) = Arranged();
        var layout = new LayoutPageViewModel(settings, backend);
        layout.Move(Item(layout, "laptop", "2"), new Point(0, -1080));
        layout.Move(Item(layout, "laptop", "1"), new Point(0, 2160));

        // The service placed monitor 1 again (the laptop's display changed), and a peer was added.
        settings.Peers[0].Monitors = new() { Placed("1", -1920, 500), settings.Peers[0].Monitors[1] };
        settings.Peers.Add(new PeerConfig { Id = "new", Name = "New", Position = ScreenPosition.Top });
        layout.Reload();

        Assert.Equal((0, -1080), (Item(layout, "laptop", "2").X, Item(layout, "laptop", "2").Y));
        Assert.Equal((-1920, 500), (Item(layout, "laptop", "1").X, Item(layout, "laptop", "1").Y));
        Assert.True(layout.Items.Single(i => i.Peer?.Id == "new").IsPlaceholder);
    }

    [Fact]
    public void ConnectedPeer_ShowsOnlyTheMonitorsItHasNow()
    {
        var (settings, backend) = Arranged();
        backend.PeerMonitors["laptop"].RemoveAt(1); // monitor 2 unplugged
        var layout = new LayoutPageViewModel(settings, backend);

        Assert.Single(layout.Items, i => i.Peer?.Id == "laptop");
        Assert.Equal(2, settings.Peers[0].Monitors.Count); // its place is kept for when it comes back
    }

    [Fact]
    public void Placeholder_CannotBeMoved()
    {
        var settings = Samples.Settings();
        var layout = new LayoutPageViewModel(settings, new FakeBackend { Settings = settings });
        var laptop = layout.Items.Single(i => i.Peer?.Id == "laptop");

        Assert.True(laptop.IsPlaceholder);
        Assert.Equal(laptop.Rect, layout.Move(laptop, new Point(5000, 5000)));
        Assert.False(layout.Nudge(laptop, 100, 0));
        Assert.False(layout.IsModified);
    }

    [Fact]
    public void Nudge_SlidesAlongAnEdge_AndJumpsPastAScreenInTheWay()
    {
        var (settings, backend) = Arranged();
        var layout = new LayoutPageViewModel(settings, backend);
        var one = Item(layout, "laptop", "1");

        Assert.True(layout.Nudge(one, 0, 100));
        Assert.Equal(new Point(-1920, 100), one.Rect.Location);

        // Right is this PC: it ends up somewhere past the left edge, overlapping nothing.
        Assert.True(layout.Nudge(one, 100, 0));
        Assert.True(one.X > -1920);
        Assert.False(VirtualDesktop.OverlapsAny(one.Rect, layout.Items.Where(i => i != one).Select(i => i.Rect)));

        // Left of everything there is nothing to touch.
        var two = Item(layout, "laptop", "2");
        Assert.False(layout.Nudge(two, 100, 0));
    }

    [Fact]
    public void Describe_SaysWhatAScreenTouches()
    {
        var (settings, backend) = Arranged();
        var layout = new LayoutPageViewModel(settings, backend);

        Assert.Equal("Laptop 1, 1920 by 1080, left of This PC", layout.Describe(Item(layout, "laptop", "1")));
        Assert.Equal("Mac mini, 1920 by 1080, below This PC, offline", layout.Describe(Item(layout, "mac", "1")));
    }
}
