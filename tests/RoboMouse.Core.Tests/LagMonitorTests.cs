using Xunit;

namespace RoboMouse.Core.Tests;

public class LagMonitorTests
{
    [Fact]
    public void SteadyMotion_ReportsNothing_WhateverTheClockDifference()
    {
        var lag = new LagMonitor();
        // The sender's clock is 5 s behind; every message takes 2 ms.
        for (long t = 0; t < 10_000; t += 4)
            Assert.Null(lag.Record(1_000_000 + t, 1_000_000 + t - 2 - 5000, 0.1));
    }

    [Fact]
    public void ABacklog_IsReported_WithTheWorstDelay()
    {
        var lag = new LagMonitor();
        string? report = null;
        for (long t = 0; t <= 2100; t += 4)
        {
            // Between 1 s and 1.2 s messages arrive up to 150 ms late, as a backed-up queue drains.
            var late = t is >= 1000 and < 1200 ? 150 - (t - 1000) / 2 : 0;
            report ??= lag.Record(t, t - 3 - late, t == 1100 ? 9 : 0.1);
        }

        Assert.NotNull(report);
        Assert.Contains("(worst 150 ms)", report);
        Assert.Contains("1 slow to apply (worst 9.0 ms)", report);
    }
}
