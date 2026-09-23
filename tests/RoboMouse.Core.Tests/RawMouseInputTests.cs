using RoboMouse.Core.Input;
using Xunit;

namespace RoboMouse.Core.Tests;

public class RawMouseInputTests
{
    [Fact]
    public void AbsolutePosition_MapsOverTheGivenArea()
    {
        var primary = (0, 0, 1920, 1080);
        Assert.Equal((0, 0), RawMouseInput.AbsoluteToPixel(0, 0, primary));
        Assert.Equal((1919, 1079), RawMouseInput.AbsoluteToPixel(65535, 65535, primary));

        // A virtual desktop with a monitor left of the primary starts at a negative x.
        var virtualDesktop = (-2560, -200, 4480, 1440);
        Assert.Equal((-2560, -200), RawMouseInput.AbsoluteToPixel(0, 0, virtualDesktop));
        Assert.Equal((1919, 1239), RawMouseInput.AbsoluteToPixel(65535, 65535, virtualDesktop));
    }

    [Fact]
    public void AbsolutePosition_OutOfRangeIsClamped() =>
        Assert.Equal((1919, 0), RawMouseInput.AbsoluteToPixel(70000, -5, (0, 0, 1920, 1080)));
}
