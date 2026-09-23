using RoboMouse.Core.Configuration;
using Xunit;

namespace RoboMouse.Core.Tests;

public sealed class AppSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "robomouse-settings-" + Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_dir, "settings.json");

    public AppSettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static AppSettings Sample() => new()
    {
        MachineName = "DODD-MAIN",
        PairingCode = "K7QM-4XDP-9RLA",
        LocalPort = 25000,
        Peers = { new PeerConfig { Name = "TIM-WORK", Address = "10.0.0.2", Position = ScreenPosition.Left } },
        BlockedMachineIds = { "abc" }
    };

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var saved = Sample();
        saved.Save(ConfigPath);

        var loaded = AppSettings.Load(ConfigPath);

        Assert.Equal(SettingsLoadNotice.None, loaded.LoadNotice);
        Assert.Equal(saved.MachineId, loaded.MachineId);
        Assert.Equal("K7QM-4XDP-9RLA", loaded.PairingCode);
        Assert.Equal(25000, loaded.LocalPort);
        Assert.Equal("TIM-WORK", Assert.Single(loaded.Peers).Name);
        Assert.Equal(ScreenPosition.Left, loaded.Peers[0].Position);
        Assert.Equal(new[] { "abc" }, loaded.BlockedMachineIds);
        Assert.False(File.Exists(ConfigPath + ".tmp"));
    }

    [Fact]
    public void FirstLoad_GeneratesPairingCodeAndWritesFile()
    {
        var loaded = AppSettings.Load(ConfigPath);

        Assert.Equal(SettingsLoadNotice.None, loaded.LoadNotice);
        Assert.False(string.IsNullOrWhiteSpace(loaded.PairingCode));
        Assert.True(File.Exists(ConfigPath));
        Assert.Equal(loaded.PairingCode, AppSettings.Load(ConfigPath).PairingCode);
    }

    [Fact]
    public void SecondSave_KeepsPreviousVersionAsBackup()
    {
        var settings = Sample();
        settings.Save(ConfigPath);
        settings.LocalPort = 26000;
        settings.Save(ConfigPath);

        Assert.True(File.Exists(ConfigPath + ".bak"));
        Assert.Contains("25000", File.ReadAllText(ConfigPath + ".bak"));
        Assert.Contains("26000", File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void TruncatedFile_IsRestoredFromBackup_AndKeptAside()
    {
        var settings = Sample();
        settings.Save(ConfigPath);
        settings.Save(ConfigPath); // the first version is now the backup

        var text = File.ReadAllText(ConfigPath);
        File.WriteAllText(ConfigPath, text[..(text.Length / 2)]);

        var loaded = AppSettings.Load(ConfigPath);

        Assert.Equal(SettingsLoadNotice.RestoredFromBackup, loaded.LoadNotice);
        Assert.Equal(settings.MachineId, loaded.MachineId);
        Assert.Equal("K7QM-4XDP-9RLA", loaded.PairingCode);
        Assert.Single(loaded.Peers);

        Assert.NotNull(loaded.CorruptFilePath);
        Assert.True(File.Exists(loaded.CorruptFilePath));
        Assert.Contains("corrupt-", Path.GetFileName(loaded.CorruptFilePath));

        // The restored settings were written back as a readable file.
        Assert.Equal(settings.MachineId, AppSettings.Load(ConfigPath).MachineId);
    }

    [Fact]
    public void CorruptFileWithoutBackup_ResetsButKeepsTheFile()
    {
        File.WriteAllText(ConfigPath, "{ this is not json");

        var loaded = AppSettings.Load(ConfigPath);

        Assert.Equal(SettingsLoadNotice.Reset, loaded.LoadNotice);
        Assert.NotNull(loaded.CorruptFilePath);
        Assert.Equal("{ this is not json", File.ReadAllText(loaded.CorruptFilePath!));
        Assert.False(string.IsNullOrWhiteSpace(loaded.PairingCode));
    }

    [Fact]
    public void JsonNull_CountsAsUnreadable()
    {
        File.WriteAllText(ConfigPath, "null");

        Assert.Equal(SettingsLoadNotice.Reset, AppSettings.Load(ConfigPath).LoadNotice);
    }

    [Fact]
    public async Task ConcurrentSaves_AlwaysLeaveAReadableFile()
    {
        var settings = Sample();
        settings.Save(ConfigPath);

        var writers = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            for (var n = 0; n < 25; n++)
            {
                // One writer keeps changing the peer list while the others save.
                if (i == 0)
                {
                    settings.Peers.Add(new PeerConfig { Name = $"P{n}" });
                    if (settings.Peers.Count > 4)
                        settings.Peers.RemoveAt(1);
                }
                settings.Save(ConfigPath);
            }
        }));
        await Task.WhenAll(writers);

        var loaded = AppSettings.Load(ConfigPath);
        Assert.Equal(SettingsLoadNotice.None, loaded.LoadNotice);
        Assert.Equal(settings.MachineId, loaded.MachineId);
        Assert.Empty(Directory.GetFiles(_dir, "*.corrupt-*"));
    }
}
