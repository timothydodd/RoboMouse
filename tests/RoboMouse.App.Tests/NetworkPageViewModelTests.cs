using RoboMouse.App.ViewModels;
using RoboMouse.Core;
using RoboMouse.Core.Network;
using Xunit;

namespace RoboMouse.App.Tests;

public class NetworkPageViewModelTests
{
    [Fact]
    public void GeneratedCodes_AreStrong()
    {
        for (var i = 0; i < 50; i++)
            Assert.True(PairingCodeFormat.IsStrong(SecureChannel.GeneratePairingCode()));
    }

    [Theory]
    [InlineData("K7PQ-M2XW-9DHR", "K7PQ-M2XW-9DHR")]
    [InlineData("k7pq m2xw 9dhr", "K7PQ-M2XW-9DHR")]
    [InlineData(" K7PQM2XW9DHR ", "K7PQ-M2XW-9DHR")]
    public void Normalize_AcceptsLooselyTypedCodes(string input, string expected)
    {
        Assert.True(PairingCodeFormat.TryNormalize(input, out var code));
        Assert.Equal(expected, code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("password1")]            // an old hand-typed code
    [InlineData("K7PQ-M2XW-9DH")]        // too short
    [InlineData("K7PQ-M2XW-9DHRX")]      // too long
    [InlineData("K7PQ-M2XW-9DH0")]       // 0 is never generated
    [InlineData("OOOO-IIII-1111")]
    public void Normalize_RefusesAnythingElse(string input)
    {
        Assert.False(PairingCodeFormat.TryNormalize(input, out _));
        Assert.False(PairingCodeFormat.IsStrong(input));
    }

    [Fact]
    public void WeakExistingCode_ShowsAWarning_UntilRegenerated()
    {
        var settings = Samples.Settings();
        settings.PairingCode = "letmein123";
        var dialogs = new FakeDialogs { Answer = Services.DialogResult.Yes };
        var page = new NetworkPageViewModel(settings, dialogs);

        Assert.True(page.IsPairingCodeWeak);
        page.GeneratePairingCodeCommand.Execute(null);

        Assert.False(page.IsPairingCodeWeak);
        Assert.True(page.PairingCodeChanged);
    }

    [Fact]
    public void EnteredCode_MustBeAGeneratedCode()
    {
        var page = new NetworkPageViewModel(Samples.Settings(), new FakeDialogs());
        page.BeginEnterCodeCommand.Execute(null);

        page.EnteredCode = "hunter2";
        page.UseEnteredCodeCommand.Execute(null);
        Assert.NotNull(page.EnteredCodeError);
        Assert.True(page.IsEnteringCode);
        Assert.False(page.PairingCodeChanged);

        page.EnteredCode = "abcd-efgh-2345";
        page.UseEnteredCodeCommand.Execute(null);
        Assert.Null(page.EnteredCodeError);
        Assert.False(page.IsEnteringCode);
        Assert.Equal("ABCD-EFGH-2345", page.PairingCode);
    }

    [Fact]
    public void PortInUse_IsShownOnThePage()
    {
        var backend = new FakeBackend
        {
            ListenerError = new NetworkStartError(NetworkErrorKind.ListenPort, 24800, true, "Port 24800 is in use by another program.")
        };
        var page = new NetworkPageViewModel(Samples.Settings(), new FakeDialogs(), backend);

        Assert.Equal("Port 24800 is in use by another program.", page.NetworkError);
        backend.ListenerError = null;
        page.Refresh();
        Assert.Null(page.NetworkError);
    }
}
