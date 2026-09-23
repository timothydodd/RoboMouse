using System.IO.Compression;
using System.Net;
using System.Text;
using RoboMouse.App.Services;
using RoboMouse.App.ViewModels;
using Xunit;

namespace RoboMouse.App.Tests;

public class UpdateCheckerTests
{
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("v2.0.0-beta.1", "2.0.0")]
    [InlineData("nightly", null)]
    [InlineData("", null)]
    public void ParseTag(string tag, string? expected)
    {
        Assert.Equal(expected, UpdateChecker.ParseTag(tag)?.ToString());
    }

    [Fact]
    public void Evaluate_OnlyNewerFinalReleases()
    {
        var current = new Version(1, 1, 4);
        GitHubRelease Release(string tag, bool pre = false) => new() { TagName = tag, HtmlUrl = "https://github.com/timothydodd/RoboMouse/releases/tag/" + tag, Prerelease = pre };

        Assert.Null(UpdateChecker.Evaluate(Release("v1.1.4"), current));
        Assert.Null(UpdateChecker.Evaluate(Release("v1.1.3"), current));
        Assert.Null(UpdateChecker.Evaluate(Release("v1.2.0", pre: true), current));
        var update = UpdateChecker.Evaluate(Release("v1.2.0"), current);
        Assert.Equal(new Version(1, 2, 0), update!.Version);
        Assert.Equal("https://github.com/timothydodd/RoboMouse/releases/tag/v1.2.0", update.PageUrl);
    }

    [Fact]
    public void Evaluate_NeverOpensAPageOffGitHub()
    {
        var update = UpdateChecker.Evaluate(new GitHubRelease { TagName = "v9.0.0", HtmlUrl = "https://evil.example/installer.exe" }, new Version(1, 0, 0));

        Assert.Equal(UpdateChecker.ReleasesPage, update!.PageUrl);
    }

    [Fact]
    public void IsDue_OncePerDay()
    {
        var now = new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(UpdateChecker.IsDue(null, now));
        Assert.False(UpdateChecker.IsDue(now.AddHours(-23), now));
        Assert.True(UpdateChecker.IsDue(now.AddHours(-24), now));
        Assert.True(UpdateChecker.IsDue(now.AddDays(3), now)); // clock went backwards
    }

    [Fact]
    public async Task Check_ReadsGitHubsReleaseJson()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"tag_name":"v1.3.0","html_url":"https://github.com/timothydodd/RoboMouse/releases/tag/v1.3.0","draft":false,"prerelease":false,"assets":[]}""");

        var update = await new UpdateChecker(handler).CheckAsync(new Version(1, 1, 4), CancellationToken.None);

        Assert.Equal(new Version(1, 3, 0), update!.Version);
        Assert.Equal(UpdateChecker.LatestReleaseApi, handler.Request!.RequestUri!.ToString());
        Assert.NotEmpty(handler.Request.Headers.UserAgent);
    }

    [Fact]
    public async Task AboutPage_CheckNow_ShowsTheNewVersion()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"tag_name":"v1.3.0","html_url":"https://github.com/timothydodd/RoboMouse/releases/tag/v1.3.0"}""");
        var dialogs = new FakeDialogs();
        var state = new AppState();
        var page = new AboutPageViewModel(dialogs, state, new UpdateChecker(handler), () => new DiagnosticsSources("", null, ""), new Version(1, 1, 4));

        await page.CheckNowCommand.ExecuteAsync(null);

        Assert.True(page.HasUpdate);
        Assert.Equal("RoboMouse 1.3.0 is available.", page.UpdateStatus);
        Assert.NotNull(state.LastUpdateCheckUtc);
        page.OpenReleasePageCommand.Execute(null);
        Assert.Equal("https://github.com/timothydodd/RoboMouse/releases/tag/v1.3.0", dialogs.Opened.Single());
    }

    [Fact]
    public async Task AboutPage_CheckNow_SurvivesNetworkErrors()
    {
        var page = new AboutPageViewModel(new FakeDialogs(), new AppState(), new UpdateChecker(new StubHandler(HttpStatusCode.Forbidden, "")),
            () => new DiagnosticsSources("", null, ""), new Version(1, 1, 4));

        await page.CheckNowCommand.ExecuteAsync(null);

        Assert.False(page.HasUpdate);
        Assert.Contains("Could not reach GitHub", page.UpdateStatus);
    }

    [Fact]
    public void AppState_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"robomouse-appstate-{Guid.NewGuid():N}.json");
        try
        {
            var state = AppState.Load(path);
            Assert.True(state.CheckForUpdates);
            state.CheckForUpdates = false;
            state.LastNotifiedVersion = "1.2.0";
            state.Save();

            var again = AppState.Load(path);
            Assert.False(again.CheckForUpdates);
            Assert.Equal("1.2.0", again.LastNotifiedVersion);

            File.WriteAllText(path, "{ not json");
            Assert.True(AppState.Load(path).CheckForUpdates); // unreadable: defaults
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class DiagnosticsTests
{
    private const string SettingsJson = """
        {
          "machineId": "abc",
          "pairingCode": "K7PQ-M2XW-9DHR",
          "toggleHotkey": "Ctrl+Alt+M",
          "identityPrivateKey": "MHcCAQEE",
          "peers": [ { "name": "Laptop", "pinnedPublicKey": "MFkwEw", "address": "192.168.1.20" } ]
        }
        """;

    [Fact]
    public void Redact_RemovesThePairingCodeAndKeys_KeepsTheRest()
    {
        var redacted = Diagnostics.RedactSettings(SettingsJson)!;

        Assert.DoesNotContain("K7PQ-M2XW-9DHR", redacted);
        Assert.DoesNotContain("MHcCAQEE", redacted);
        Assert.DoesNotContain("MFkwEw", redacted);
        Assert.Contains("Ctrl+Alt+M", redacted);
        Assert.Contains("192.168.1.20", redacted);
        Assert.Contains("(redacted)", redacted);
    }

    [Fact]
    public void Redact_UnparsableSettings_GiveNull()
    {
        Assert.Null(Diagnostics.RedactSettings("{ \"pairingCode\": \"K7PQ"));
    }

    [Fact]
    public void Export_ZipsLogsAndRedactedSettings()
    {
        var dir = Directory.CreateTempSubdirectory("robomouse-diag-");
        var zipPath = Path.Combine(dir.FullName, "out.zip");
        try
        {
            var data = Directory.CreateDirectory(Path.Combine(dir.FullName, "data")).FullName;
            File.WriteAllText(Path.Combine(data, "debug.log"), "run 3");
            File.WriteAllText(Path.Combine(data, "debug.1.log"), "run 2");
            File.WriteAllText(Path.Combine(data, "crash.txt"), "boom");
            File.WriteAllText(Path.Combine(data, "settings.json"), SettingsJson);
            File.WriteAllText(Path.Combine(data, "unrelated.txt"), "not collected");
            var service = Directory.CreateDirectory(Path.Combine(dir.FullName, "service")).FullName;
            File.WriteAllText(Path.Combine(service, "service.log"), "service ran");

            Diagnostics.Export(zipPath, new DiagnosticsSources(data, service, "RoboMouse 1.1.4"));

            using var zip = ZipFile.OpenRead(zipPath);
            var names = zip.Entries.Select(e => e.FullName).OrderBy(n => n).ToList();
            Assert.Equal(new[] { "crash.txt", "debug.1.log", "debug.log", "service/service.log", "settings.json", "system.txt" }, names);
            using var reader = new StreamReader(zip.GetEntry("settings.json")!.Open());
            Assert.DoesNotContain("K7PQ-M2XW-9DHR", reader.ReadToEnd());
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void SystemSummary_ListsMonitors()
    {
        var main = new System.Drawing.Rectangle(0, 0, 2560, 1440);
        var side = new System.Drawing.Rectangle(-1920, 0, 1920, 1080);
        var text = Diagnostics.DescribeSystem(new Version(1, 1, 4), packaged: false,
            new Core.Screen.MonitorLayout(new[] { new Core.Screen.MonitorRect(main, main, true), new Core.Screen.MonitorRect(side, side, false) }));

        Assert.Contains("RoboMouse 1.1.4 (direct download)", text);
        Assert.Contains("Monitor 1 (main): 2560x1440 at (0, 0)", text);
        Assert.Contains("Monitor 2: 1920x1080 at (-1920, 0)", text);
    }

    [Fact]
    public void CrashFile_HoldsTheLastException()
    {
        var dir = Directory.CreateTempSubdirectory("robomouse-crash-");
        try
        {
            Diagnostics.WriteCrash("UI", new InvalidOperationException("first"), dir.FullName);
            Diagnostics.WriteCrash("Fatal", new InvalidOperationException("second"), dir.FullName);

            var text = File.ReadAllText(Path.Combine(dir.FullName, "crash.txt"));
            Assert.Contains("second", text);
            Assert.DoesNotContain("first", text);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
