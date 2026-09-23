using RoboMouse.App.ViewModels;
using RoboMouse.Core.Configuration;
using Xunit;

namespace RoboMouse.App.Tests;

public class LayoutPageViewModelTests
{
    [Fact]
    public void Drag_WithoutSave_LeavesConfigsAlone()
    {
        var settings = Samples.Settings();
        var layout = new LayoutPageViewModel(settings);

        layout.MoveToEdge(layout.Placements[0], ScreenPosition.Top, 300, 0);

        // Cancel just drops the view model: the configs must still hold what was saved.
        Assert.Equal(ScreenPosition.Left, settings.Peers[0].Position);
        Assert.Equal(120, settings.Peers[0].OffsetY);
        Assert.Equal(0, settings.Peers[0].OffsetX);

        var reopened = new LayoutPageViewModel(settings);
        Assert.Equal(ScreenPosition.Left, reopened.Placements[0].Position);
        Assert.Equal(120, reopened.Placements[0].OffsetY);
    }

    [Fact]
    public void Save_WritesPlacementsIntoConfigs()
    {
        var settings = Samples.Settings();
        var layout = new LayoutPageViewModel(settings);

        layout.MoveToEdge(layout.Placements[0], ScreenPosition.Top, 300, 0);
        layout.Save();

        Assert.Equal(ScreenPosition.Top, settings.Peers[0].Position);
        Assert.Equal(300, settings.Peers[0].OffsetX);
        Assert.Equal(0, settings.Peers[0].OffsetY);
        Assert.False(layout.Placements[0].IsModified);
    }

    [Fact]
    public void DropOnOccupiedEdge_SwapsTheTwo()
    {
        var settings = Samples.Settings();
        var layout = new LayoutPageViewModel(settings);
        var laptop = layout.Placements[0];
        var mac = layout.Placements[1];

        var moved = layout.MoveToEdge(laptop, ScreenPosition.Right, 0, 40);

        Assert.Same(mac, moved);
        Assert.Equal(ScreenPosition.Right, laptop.Position);
        Assert.Equal(40, laptop.OffsetY);
        // The Mac takes the edge and offset the laptop came from.
        Assert.Equal(ScreenPosition.Left, mac.Position);
        Assert.Equal(120, mac.OffsetY);
    }

    [Fact]
    public void DropOnFreeOrOwnEdge_MovesNobodyElse()
    {
        var settings = Samples.Settings();
        var layout = new LayoutPageViewModel(settings);

        Assert.Null(layout.MoveToEdge(layout.Placements[0], ScreenPosition.Bottom, 10, 0));
        Assert.Null(layout.MoveToEdge(layout.Placements[1], ScreenPosition.Right, 0, 50));
        Assert.Equal(ScreenPosition.Right, layout.Placements[1].Position);
    }

    [Fact]
    public void Reload_KeepsUnsavedDrags_ButFollowsEditsMadeElsewhere()
    {
        var settings = Samples.Settings();
        var layout = new LayoutPageViewModel(settings);
        layout.MoveToEdge(layout.Placements[0], ScreenPosition.Bottom, 10, 0);
        layout.MoveToEdge(layout.Placements[1], ScreenPosition.Top, 20, 0);

        // The Peers page edited (and saved) the Mac, and added a peer.
        settings.Peers[1].Position = ScreenPosition.Left;
        settings.Peers.Add(new PeerConfig { Id = "new", Name = "New", Position = ScreenPosition.Right });
        layout.Reload();

        Assert.Equal(3, layout.Placements.Count);
        Assert.Equal(ScreenPosition.Bottom, layout.Placements[0].Position);
        Assert.Equal(ScreenPosition.Left, layout.Placements[1].Position);
        Assert.Equal(ScreenPosition.Right, layout.Placements[2].Position);
    }
}
