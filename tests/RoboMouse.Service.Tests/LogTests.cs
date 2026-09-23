using Xunit;

namespace RoboMouse.Service.Tests;

public class LogTests
{
    [Fact]
    public void Throttle_AllowsOneLinePerKeyPerInterval_AndCountsTheRest()
    {
        var now = new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);
        var throttle = new LogThrottle(TimeSpan.FromMinutes(1), () => now);

        Assert.True(throttle.ShouldWrite("rejected", out var skipped));
        Assert.Equal(0, skipped);
        Assert.False(throttle.ShouldWrite("rejected", out _));
        Assert.False(throttle.ShouldWrite("rejected", out _));
        Assert.True(throttle.ShouldWrite("other", out _));

        now = now.AddMinutes(1);
        Assert.True(throttle.ShouldWrite("rejected", out skipped));
        Assert.Equal(2, skipped);
    }

    [Fact]
    public void FileLog_RollsToDotOne_PastTheCap()
    {
        var dir = Directory.CreateTempSubdirectory("robomouse-log-");
        try
        {
            var path = Path.Combine(dir.FullName, "service.log");
            var log = new RollingFileLog(path, maxBytes: 100);

            for (var i = 0; i < 5; i++)
                log.Write($"line {i} " + new string('x', 30));

            Assert.True(File.Exists(path + ".1"));
            Assert.True(new FileInfo(path).Length <= 100);
            Assert.True(new FileInfo(path + ".1").Length <= 100);
            Assert.Contains("line 4", File.ReadAllText(path));
            Assert.DoesNotContain("line 0", File.ReadAllText(path) + File.ReadAllText(path + ".1"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
