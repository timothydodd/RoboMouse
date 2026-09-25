using RoboMouse.Core.Configuration;
using Xunit;

namespace RoboMouse.Core.Tests;

public class EdgePushTests
{
    [Fact]
    public void CrossesOncePushedFarEnough_IntoTheEdge()
    {
        var push = new EdgePush();
        push.Arm(ScreenPosition.Right);

        Assert.False(push.Add(10, 0, 30));
        Assert.False(push.Add(0, 50, 30)); // along the edge does not count
        Assert.False(push.Add(15, 0, 30));
        Assert.True(push.Add(5, 0, 30));
        Assert.False(push.IsArmed);
    }

    [Fact]
    public void MovingBackOut_CountsDown_NeverBelowZero()
    {
        var push = new EdgePush();
        push.Arm(ScreenPosition.Left);

        Assert.False(push.Add(-20, 0, 30));
        Assert.False(push.Add(100, 0, 30)); // back out: count to 0, not negative
        Assert.False(push.Add(-29, 0, 30));
        Assert.True(push.Add(-1, 0, 30));
    }

    [Fact]
    public void ArmingTheSameEdge_KeepsTheCount_AnotherEdgeStartsOver()
    {
        var push = new EdgePush();
        push.Arm(ScreenPosition.Top);
        push.Add(0, -20, 30);
        push.Arm(ScreenPosition.Top);
        Assert.True(push.Add(0, -10, 30));

        push.Arm(ScreenPosition.Top);
        push.Add(0, -20, 30);
        push.Arm(ScreenPosition.Bottom);
        Assert.False(push.Add(0, 20, 30));
    }

    [Fact]
    public void Cancelled_CountsNothing()
    {
        var push = new EdgePush();
        push.Arm(ScreenPosition.Bottom);
        push.Cancel();
        Assert.False(push.Add(0, 1000, 30));
    }
}
