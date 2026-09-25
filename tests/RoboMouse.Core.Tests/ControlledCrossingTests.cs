using System.Drawing;
using RoboMouse.Core.Network.Protocol;
using RoboMouse.Core.Screen;
using Xunit;

namespace RoboMouse.Core.Tests;

/// <summary>
/// The controlled machine has two 1920x1080 monitors side by side (1 left, 2 right, as Windows has
/// them). The controller has one 1920x1080 display at its origin.
/// </summary>
public class ControlledCrossingTests
{
    private static readonly MonitorLayout Local = new(new[]
    {
        new MonitorRect(new Rectangle(0, 0, 1920, 1080), default, true, "1"),
        new MonitorRect(new Rectangle(1920, 0, 1920, 1080), default, false, "2")
    });

    private static VirtualDesktop Layout(params (string? Id, Rectangle Rect)[] screens) =>
        new VirtualLayoutMessage { Screens = screens.Select(s => new VirtualLayoutMessage.LayoutEntry(s.Id, s.Rect)).ToList() }.ToDesktop();

    private static readonly Rectangle Host = new(0, 0, 1920, 1080);

    // Monitor 1 left of the host and monitor 2 right of it: the opposite of how Windows joins them.
    private static readonly VirtualDesktop Split = Layout(
        (null, Host), ("1", new Rectangle(-1920, 0, 1920, 1080)), ("2", new Rectangle(1920, 0, 1920, 1080)));

    [Fact]
    public void WindowsCrossingTheLayoutDoesNotAllow_IsUndone_ThenHandsBackOncePushedEnough()
    {
        var crossing = new ControlledCrossing();
        crossing.Reset("1");

        // Windows carries the cursor from monitor 1 onto monitor 2, but the host is right of monitor 1.
        var first = crossing.Step(Local, Split, wrap: false, x: 1925, y: 500, dx: 10, dy: 0);
        Assert.Equal(new CrossingStep(CrossingAction.MoveTo, 1919, 500), first);

        var second = crossing.Step(Local, Split, wrap: false, x: 1919, y: 500, dx: 10, dy: 0);
        Assert.Equal(CrossingAction.HandBack, second.Action);
        Assert.False(second.Released);
        Assert.Equal((0, 500), (second.X, second.Y)); // the host's left edge
    }

    [Fact]
    public void BriefLean_DoesNotHandBack()
    {
        var crossing = new ControlledCrossing();
        crossing.Reset("1");
        Assert.Equal(CrossingAction.MoveTo, crossing.Step(Local, Split, false, 1925, 500, 5, 0).Action);
        Assert.Equal(CrossingAction.None, crossing.Step(Local, Split, false, 1910, 500, -9, 0).Action);
        Assert.Equal(CrossingAction.None, crossing.Step(Local, Split, false, 1919, 500, 9, 0).Action);
    }

    [Fact]
    public void EdgeWithOwnMonitorBeyondOnTheLayout_JumpsThere()
    {
        // The layout stacks monitor 2 under monitor 1, though Windows has nothing below monitor 1.
        var stacked = Layout((null, Host), ("1", new Rectangle(1920, 0, 1920, 1080)), ("2", new Rectangle(1920, 1080, 1920, 1080)));
        var crossing = new ControlledCrossing();
        crossing.Reset("1");

        var step = crossing.Step(Local, stacked, false, x: 960, y: 1079, dx: 0, dy: 4);

        Assert.Equal(CrossingAction.MoveTo, step.Action);
        Assert.Equal(0, step.Y);                 // top of monitor 2
        Assert.InRange(step.X, 1920 + 955, 1920 + 965);
        Assert.Equal("2", crossing.CurrentMonitor);
    }

    [Fact]
    public void WindowsCrossingTheLayoutAgreesWith_IsLeftAlone()
    {
        var sideBySide = Layout((null, Host), ("1", new Rectangle(1920, 0, 1920, 1080)), ("2", new Rectangle(3840, 0, 1920, 1080)));
        var crossing = new ControlledCrossing();
        crossing.Reset("1");

        Assert.Equal(CrossingStep.None, crossing.Step(Local, sideBySide, false, 1925, 500, 10, 0));
        Assert.Equal("2", crossing.CurrentMonitor);
    }

    [Fact]
    public void EdgeWithNothingBeyond_KeepsTheCursor()
    {
        var crossing = new ControlledCrossing();
        crossing.Reset("1");
        for (var i = 0; i < 10; i++)
            Assert.Equal(CrossingStep.None, crossing.Step(Local, Split, false, 500, 0, 0, -20));
    }

    [Fact]
    public void WithoutALayout_AnOuterEdgeHandsBack_Released()
    {
        var crossing = new ControlledCrossing();
        crossing.Reset("1");

        Assert.Equal(CrossingStep.None, crossing.Step(Local, null, false, 0, 500, -8, 0));
        var step = crossing.Step(Local, null, false, 0, 500, -8, 0);

        Assert.Equal(CrossingAction.HandBack, step.Action);
        Assert.True(step.Released);
    }

    [Fact]
    public void AJumpFarFromTheMonitor_IsFollowed_NotTreatedAsACrossing()
    {
        var crossing = new ControlledCrossing();
        crossing.Reset("1");
        // Someone moved this machine's own mouse over to monitor 2.
        Assert.Equal(CrossingStep.None, crossing.Step(Local, Split, false, 3000, 500, 3, 0));
        Assert.Equal("2", crossing.CurrentMonitor);
    }

    [Theory]
    [InlineData(0f, 0.5f, Configuration.ScreenPosition.Left)]
    [InlineData(1f, 0.5f, Configuration.ScreenPosition.Right)]
    [InlineData(0.4f, 0f, Configuration.ScreenPosition.Top)]
    [InlineData(0.4f, 1f, Configuration.ScreenPosition.Bottom)]
    public void NearestEdge_IsWhereTheCursorCameIn(float fx, float fy, Configuration.ScreenPosition edge) =>
        Assert.Equal(edge, ControlledCrossing.NearestEdge(fx, fy));
}
