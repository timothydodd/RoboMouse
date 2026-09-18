using System.Drawing;
using RoboMouse.Core.Configuration;
using RoboMouse.Core.Screen;
using Xunit;

namespace RoboMouse.Core.Tests;

public class MonitorLayoutTests
{
    private static MonitorRect Monitor(int x, int y, int w, int h, bool primary = false)
    {
        var r = new Rectangle(x, y, w, h);
        return new MonitorRect(r, r, primary);
    }

    // A 1920x1080 main display with a larger 2560x1440 monitor to its left, 200 px higher.
    private static MonitorLayout MainOnRight() => new(new[]
    {
        Monitor(-2560, -200, 2560, 1440),
        Monitor(0, 0, 1920, 1080, primary: true)
    });

    [Fact]
    public void Primary_IsTheFlaggedMonitor_NotTheFirst()
    {
        var layout = MainOnRight();
        Assert.Equal(new Rectangle(0, 0, 1920, 1080), layout.PrimaryBounds);
        Assert.Equal(new Rectangle(-2560, -200, 4480, 1440), layout.VirtualBounds);
    }

    [Fact]
    public void SharedEdgeBetweenMonitors_IsNotAnEdge()
    {
        var layout = MainOnRight();
        Assert.Null(layout.GetEdgeAt(0, 500));
        Assert.Null(layout.GetEdgeAt(-1, 500));
    }

    [Fact]
    public void OuterEdges_AreDetectedOnEitherMonitor()
    {
        var layout = MainOnRight();
        Assert.Equal(ScreenPosition.Left, layout.GetEdgeAt(-2560, 500)!.Edge);
        Assert.Equal(ScreenPosition.Right, layout.GetEdgeAt(1919, 500)!.Edge);
    }

    [Fact]
    public void TopOfShorterMonitor_IsAnEdge_ThoughInsideTheBoundingBox()
    {
        var layout = MainOnRight();
        var edge = layout.GetEdgeAt(960, 0);
        Assert.NotNull(edge);
        Assert.Equal(ScreenPosition.Top, edge!.Edge);
        Assert.Equal(ScreenPosition.Bottom, layout.GetEdgeAt(960, 1079)!.Edge);
    }

    [Fact]
    public void PartOfAMonitorEdgeBesideEmptySpace_IsAnEdge()
    {
        var layout = MainOnRight();
        // Right side of the left monitor, above where the main display starts.
        Assert.Equal(ScreenPosition.Right, layout.GetEdgeAt(-1, -100)!.Edge);
    }

    [Fact]
    public void Threshold_DoesNotTurnAnInnerEdgeIntoAnOuterOne()
    {
        var layout = MainOnRight();
        Assert.Null(layout.GetEdgeAt(2, 500, threshold: 3));
        Assert.NotNull(layout.GetEdgeAt(1917, 500, threshold: 3));
    }

    [Fact]
    public void EdgePoint_LandsOnAMonitor_NotInEmptySpace()
    {
        var layout = MainOnRight();

        // Top of the right edge is empty space beside the main display: use the left monitor's edge there.
        Assert.Equal((-1, -200), layout.GetEdgePoint(ScreenPosition.Right, 0f));
        // Lower down, the main display is the outermost.
        Assert.Equal(1919, layout.GetEdgePoint(ScreenPosition.Right, 0.5f).X);

        foreach (var edge in new[] { ScreenPosition.Left, ScreenPosition.Right, ScreenPosition.Top, ScreenPosition.Bottom })
        {
            for (var n = 0f; n <= 1f; n += 0.05f)
            {
                var (x, y) = layout.GetEdgePoint(edge, n);
                Assert.True(layout.IsAtOuterEdge(edge, x, y), $"{edge} {n}: ({x},{y})");
            }
        }
    }

    [Fact]
    public void SingleMonitor_BehavesAsBefore()
    {
        var layout = new MonitorLayout(new[] { Monitor(0, 0, 1920, 1080, primary: true) });
        Assert.Equal(ScreenPosition.Left, layout.GetEdgeAt(0, 540)!.Edge);
        Assert.Equal(0.5f, layout.GetEdgeAt(0, 540)!.NormalizedPosition, 2);
        Assert.Equal((1919, 0), layout.GetEdgePoint(ScreenPosition.Right, 0f));
        Assert.Null(layout.GetEdgeAt(960, 540));
    }

    [Fact]
    public void NoMonitors_FallsBackToADefaultScreen()
    {
        var layout = new MonitorLayout(Array.Empty<MonitorRect>());
        Assert.Equal(new Rectangle(0, 0, 1920, 1080), layout.PrimaryBounds);
    }
}
