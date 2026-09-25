using System.Drawing;
using RoboMouse.Core.Configuration;
using RoboMouse.Core.Screen;
using Xunit;

namespace RoboMouse.Core.Tests;

public class VirtualDesktopTests
{
    private static LayoutScreen Local(string id, int x, int y, int w, int h) => new(VirtualDesktop.Local, id, new Rectangle(x, y, w, h));
    private static LayoutScreen Peer(string peer, string id, int x, int y, int w, int h) => new(peer, id, new Rectangle(x, y, w, h));

    // This PC: one 1920x1080 display. Two laptops side by side under it, each half as wide.
    private static readonly LayoutScreen Main = Local("main", 0, 0, 1920, 1080);
    private static readonly LayoutScreen LaptopA = Peer("a", "1", 0, 1080, 960, 600);
    private static readonly LayoutScreen LaptopB = Peer("b", "1", 960, 1080, 960, 600);

    [Fact]
    public void TwoPeersOnOneEdge_EachTakesItsOwnStretch()
    {
        var desktop = new VirtualDesktop(new[] { Main, LaptopA, LaptopB });

        var left = desktop.Resolve(Main, ScreenPosition.Bottom, 400, wrap: false);
        var right = desktop.Resolve(Main, ScreenPosition.Bottom, 1500, wrap: false);

        Assert.Equal("a", left!.Value.Screen.Owner);
        Assert.Equal((400, 1080), (left.Value.X, left.Value.Y));
        Assert.Equal(ScreenPosition.Top, left.Value.EntryEdge);
        Assert.Equal("b", right!.Value.Screen.Owner);
        Assert.Equal((1500, 1080), (right.Value.X, right.Value.Y));
    }

    [Fact]
    public void PartOfAnEdgeWithNothingBeyond_DoesNotCross()
    {
        var desktop = new VirtualDesktop(new[] { Main, LaptopA });
        Assert.Null(desktop.Resolve(Main, ScreenPosition.Bottom, 1500, wrap: false));
    }

    [Fact]
    public void ScreenBeyondAGap_IsNotReached()
    {
        var far = Peer("a", "1", 2000, 0, 1920, 1080);
        var desktop = new VirtualDesktop(new[] { Main, far });
        Assert.Null(desktop.Resolve(Main, ScreenPosition.Right, 500, wrap: false));
    }

    [Fact]
    public void RemoteMonitors_CanBePlacedInAnyOrder()
    {
        // The peer has monitors 1 and 2 side by side; here 1 is left of this PC and 2 right of it.
        var one = Peer("a", "1", -1920, 0, 1920, 1080);
        var two = Peer("a", "2", 1920, 0, 1920, 1080);
        var desktop = new VirtualDesktop(new[] { Main, one, two });

        Assert.Equal("1", desktop.Resolve(Main, ScreenPosition.Left, 500, false)!.Value.Screen.MonitorId);
        Assert.Equal("2", desktop.Resolve(Main, ScreenPosition.Right, 500, false)!.Value.Screen.MonitorId);
        // From the peer's monitor 2, left leads back here, not to its own monitor 1.
        Assert.True(desktop.Resolve(two, ScreenPosition.Left, 500, false)!.Value.Screen.IsLocal);
    }

    [Fact]
    public void Wrap_GoesToTheFarthestScreenOnTheRow_EnteringFromItsFarSide()
    {
        var left = Peer("a", "1", -1920, 0, 1920, 1080);
        var desktop = new VirtualDesktop(new[] { Main, left });

        var target = desktop.Resolve(Main, ScreenPosition.Right, 300, wrap: true);

        Assert.Equal("a", target!.Value.Screen.Owner);
        Assert.Equal(ScreenPosition.Left, target.Value.EntryEdge);
        Assert.Equal((-1920, 300), (target.Value.X, target.Value.Y));
        Assert.Null(desktop.Resolve(Main, ScreenPosition.Right, 300, wrap: false));
    }

    [Fact]
    public void Wrap_WithNothingElseOnTheRow_GoesNowhere()
    {
        var below = Peer("a", "1", 0, 1080, 1920, 1080);
        var desktop = new VirtualDesktop(new[] { Main, below });
        Assert.Null(desktop.Resolve(Main, ScreenPosition.Right, 300, wrap: true));
    }

    [Fact]
    public void Normalize_And_Map_ArePropotional()
    {
        var from = new Rectangle(0, 0, 1921, 1081);
        var to = new Rectangle(100, 100, 3841, 2161);
        Assert.Equal((100 + 1920, 100 + 1080), VirtualDesktop.Map(from, to, 960, 540));
        Assert.Equal((0f, 1f), VirtualDesktop.Normalize(from, -50, 5000));
    }

    [Fact]
    public void FindFree_LeavesAPositionThatTouchesAndOverlapsNothing()
    {
        var spot = new Rectangle(1920, 100, 800, 600);
        Assert.Equal(spot, VirtualDesktop.FindFree(spot, new[] { Main.Rect }));
    }

    [Fact]
    public void FindFree_MovesAnOverlappingScreenToTheNearestEdge()
    {
        var overlapping = new Rectangle(1800, 100, 800, 600);
        var moved = VirtualDesktop.FindFree(overlapping, new[] { Main.Rect });
        Assert.Equal(new Rectangle(1920, 100, 800, 600), moved);
    }

    [Fact]
    public void FindFree_MovesAFloatingScreenToTouchSomething()
    {
        var floating = new Rectangle(3000, 200, 800, 600);
        var moved = VirtualDesktop.FindFree(floating, new[] { Main.Rect });
        Assert.Equal(new Rectangle(1920, 200, 800, 600), moved);
    }

    [Fact]
    public void Snap_NeverOverlaps_AndLinesUpWhenClose()
    {
        var others = new[] { Main.Rect, new Rectangle(1920, 0, 1000, 500) };
        // Dropped overlapping the peer screen right of Main, a little below its bottom.
        var snapped = VirtualDesktop.Snap(new Rectangle(1950, 520, 1000, 500), others, alignThreshold: 40);

        Assert.False(VirtualDesktop.OverlapsAny(snapped, others));
        Assert.Equal(new Rectangle(1920, 500, 1000, 500), snapped);
    }

    [Fact]
    public void Touches_NeedsASharedStretchOfEdge_NotJustACorner()
    {
        Assert.True(VirtualDesktop.Touches(Main.Rect, new Rectangle(1920, 1000, 100, 100)));
        Assert.False(VirtualDesktop.Touches(Main.Rect, new Rectangle(1920, 1080, 100, 100)));
    }
}

public class PlacementPlannerTests
{
    private static MonitorRect Remote(string id, int x, int y, int w, int h, bool primary = false, int scale = 100) =>
        new(new Rectangle(x, y, w, h), new Rectangle(x, y, w, h), primary, id, scale);

    private static readonly Rectangle Main = new(0, 0, 1920, 1080);

    [Fact]
    public void LayoutSize_ScalesByTheRatioOfScaling()
    {
        Assert.Equal(new Size(1920, 1080), PlacementPlanner.LayoutSize(new Size(3840, 2160), 200, 100));
        Assert.Equal(new Size(2880, 1620), PlacementPlanner.LayoutSize(new Size(3840, 2160), 200, 150));
    }

    [Fact]
    public void NewPeer_IsPlacedAsAGroupOnItsSide_KeepingItsOwnArrangement()
    {
        var reported = new[] { Remote("\\\\.\\DISPLAY1", 0, 0, 1920, 1080, primary: true), Remote("\\\\.\\DISPLAY2", 1920, 0, 1280, 1024) };

        var placed = PlacementPlanner.Reconcile(Array.Empty<MonitorPlacement>(), reported, 100,
            new[] { Main }, Main, ScreenPosition.Left, 0, 50);

        var one = placed.Single(p => p.Id.EndsWith("1"));
        var two = placed.Single(p => p.Id.EndsWith("2"));
        // The pair ends at this PC's left edge, monitor 2 still right of monitor 1.
        Assert.Equal(new Rectangle(-3200, 50, 1920, 1080), one.Rect);
        Assert.Equal(new Rectangle(-1280, 50, 1280, 1024), two.Rect);
    }

    [Fact]
    public void KnownMonitor_KeepsItsPlace_AndTakesItsNewSize()
    {
        var saved = new List<MonitorPlacement> { new() { Id = "1", X = 1920, Y = 0, Width = 1920, Height = 1080, RemoteWidth = 1920, RemoteHeight = 1080 } };
        var placed = PlacementPlanner.Reconcile(saved, new[] { Remote("1", 0, 0, 2560, 1440, primary: true) }, 100,
            new[] { Main }, Main, ScreenPosition.Right, 0, 0);

        Assert.Equal(new Rectangle(1920, 0, 2560, 1440), placed.Single().Rect);
        Assert.Equal(1080, saved[0].Height); // the input is not changed
    }

    [Fact]
    public void MonitorThatIsGone_KeepsItsPlacement()
    {
        var saved = new List<MonitorPlacement>
        {
            new() { Id = "1", X = 1920, Y = 0, Width = 1920, Height = 1080, RemoteWidth = 1920, RemoteHeight = 1080, Primary = true },
            new() { Id = "2", X = 3840, Y = 0, Width = 1920, Height = 1080, RemoteX = 1920, RemoteWidth = 1920, RemoteHeight = 1080 }
        };
        var placed = PlacementPlanner.Reconcile(saved, new[] { Remote("1", 0, 0, 1920, 1080, primary: true) }, 100,
            new[] { Main }, Main, ScreenPosition.Right, 0, 0);

        Assert.Equal(2, placed.Count);
        Assert.Equal(new Rectangle(3840, 0, 1920, 1080), placed.Single(p => p.Id == "2").Rect);
    }

    [Fact]
    public void NewMonitor_GoesBesideItsNeighbour_AsThePeerHasIt()
    {
        // Monitor 1 was placed below this PC; monitor 2 appears above monitor 1 on the peer.
        var saved = new List<MonitorPlacement> { new() { Id = "1", X = 0, Y = 1080, Width = 1920, Height = 1080, RemoteWidth = 1920, RemoteHeight = 1080, Primary = true } };
        var reported = new[] { Remote("1", 0, 0, 1920, 1080, primary: true), Remote("2", 0, -1080, 1920, 1080) };

        var placed = PlacementPlanner.Reconcile(saved, reported, 100, new[] { Main }, Main, ScreenPosition.Right, 0, 0);

        // Above monitor 1 is this PC, so it goes to the nearest free place instead: touching, no overlap.
        var two = placed.Single(p => p.Id == "2").Rect;
        Assert.False(VirtualDesktop.OverlapsAny(two, new[] { Main, placed[0].Rect }));
        Assert.True(VirtualDesktop.Touches(two, Main) || VirtualDesktop.Touches(two, placed[0].Rect));
    }

    [Fact]
    public void SecondPeerOnTheSameSide_DoesNotLandOnTheFirst()
    {
        var first = new Rectangle(1920, 0, 1920, 1080);
        var placed = PlacementPlanner.Reconcile(Array.Empty<MonitorPlacement>(), new[] { Remote("1", 0, 0, 1920, 1080, primary: true) }, 100,
            new[] { Main, first }, Main, ScreenPosition.Right, 0, 0);

        Assert.False(VirtualDesktop.OverlapsAny(placed[0].Rect, new[] { Main, first }));
    }

    [Fact]
    public void PushOut_MovesOnlyWhatOverlaps()
    {
        var placements = new List<MonitorPlacement>
        {
            new() { Id = "1", X = 1920, Y = 0, Width = 1920, Height = 1080 },
            new() { Id = "2", X = -1920, Y = 0, Width = 1920, Height = 1080 }
        };
        // This PC gained a monitor on its right, where the peer's monitor 1 was.
        var local = new[] { Main, new Rectangle(1920, 0, 1920, 1080) };

        var moved = PlacementPlanner.PushOut(placements, local);

        Assert.NotNull(moved);
        Assert.Equal(new Rectangle(-1920, 0, 1920, 1080), moved!.Single(p => p.Id == "2").Rect);
        Assert.False(VirtualDesktop.OverlapsAny(moved.Single(p => p.Id == "1").Rect, local));
        Assert.Null(PlacementPlanner.PushOut(moved, local));
    }
}
