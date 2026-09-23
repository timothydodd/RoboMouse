using RoboMouse.Core.Logging;
using Xunit;

namespace RoboMouse.Core.Tests;

public sealed class SimpleLoggerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "robomouse-log-" + Guid.NewGuid().ToString("N"));
    private string LogPath => Path.Combine(_dir, "debug.log");

    public SimpleLoggerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Roll_KeepsTheLastRuns()
    {
        for (var run = 1; run <= 5; run++)
        {
            SimpleLogger.Roll(LogPath, keep: 3);
            File.WriteAllText(LogPath, $"run {run}");
        }

        Assert.Equal("run 5", File.ReadAllText(LogPath));
        Assert.Equal("run 4", File.ReadAllText(Path.Combine(_dir, "debug.1.log")));
        Assert.Equal("run 3", File.ReadAllText(Path.Combine(_dir, "debug.2.log")));
        Assert.Equal(3, Directory.GetFiles(_dir).Length);
    }

    [Fact]
    public void Roll_WithNothingThere_DoesNothing()
    {
        SimpleLogger.Roll(LogPath, keep: 3);
        Assert.Empty(Directory.GetFiles(_dir));
    }
}
