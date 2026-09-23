using RoboMouse.App.ViewModels;
using RoboMouse.Core;
using RoboMouse.Core.Configuration;
using RoboMouse.Core.Network;
using Xunit;

namespace RoboMouse.App.Tests;

/// <summary>Settings for the Phase 6 features: crossing guards, hotkeys, per-peer clipboard, locking, re-pairing.</summary>
public class Phase6Tests
{
    [Fact]
    public async Task Save_WritesCrossingHotkeysAndLocking_AndAppliesThemLive()
    {
        var settings = Samples.Settings();
        var backend = new FakeBackend();
        var vm = new SettingsViewModel(settings, backend, new FakeDialogs(), "1.0.0");
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        vm.General.BlockWhileButtonHeld = false;
        vm.General.CornerDeadZone = 40;
        vm.General.DoubleTap = true;
        vm.General.CrossingDelayMs = 250;
        vm.General.SelectedCrossingModifier = vm.General.ModifierChoices.Single(c => c.Modifier == CrossingModifier.Shift);
        vm.General.BlockWhileFullScreen = true;
        vm.General.LockCursorHotkey = "Pause";
        vm.General.LockAllHotkey = "Ctrl+Win+L";
        vm.General.LockWithHost = true;
        vm.General.ScreensaverWithHost = true;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(closed);
        var crossing = settings.Crossing;
        Assert.False(crossing.BlockWhileButtonHeld);
        Assert.Equal(40, crossing.CornerDeadZone);
        Assert.True(crossing.DoubleTap);
        Assert.Equal(250, crossing.DelayMs);
        Assert.Equal(CrossingModifier.Shift, crossing.RequiredModifier);
        Assert.True(crossing.BlockWhileFullScreen);
        Assert.Equal("Pause", settings.LockCursorHotkey);
        Assert.Equal("Ctrl+Win+L", settings.LockAllHotkey);
        Assert.True(settings.LockWithHost);
        Assert.True(settings.ScreensaverWithHost);
        Assert.Equal(1, backend.ClipboardApplied);
        Assert.Equal(1, backend.CrossingApplied);
        Assert.Equal(1, backend.HotkeysApplied);
    }

    [Fact]
    public async Task Save_RefusesTwoActionsOnOneChord()
    {
        var settings = Samples.Settings();
        var dialogs = new FakeDialogs();
        var vm = new SettingsViewModel(settings, new FakeBackend(), dialogs, "1.0.0");

        // Laptop is the first peer, so it jumps with Ctrl+Alt+F1 by default.
        vm.General.LockAllHotkey = "Ctrl+Alt+F1";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Null(settings.LockAllHotkey);
        Assert.Contains(dialogs.Messages, m => m.Contains("Jump to Laptop") && m.Contains("Lock all PCs"));
    }

    [Fact]
    public void ClearedCrossingNumber_IsFlagged()
    {
        var page = new GeneralPageViewModel(Samples.Settings());
        page.CornerDeadZone = null;
        Assert.True(page.HasErrors);
    }

    [Fact]
    public async Task PeerDialog_SavesClipboardSharingAndJumpHotkey()
    {
        var settings = Samples.Settings();
        var laptop = settings.Peers[0];
        var dialog = new PeerSetupViewModel(laptop, settings, null, new FakeDialogs());

        Assert.Equal("Ctrl+Alt+F1", dialog.JumpHotkey);
        Assert.True(dialog.ShareClipboard);

        dialog.ShareClipboard = false;
        dialog.JumpHotkey = "Ctrl+Shift+F9";
        await dialog.ConfirmCommand.ExecuteAsync(null);

        Assert.Same(laptop, dialog.Result);
        Assert.False(laptop.ShareClipboard);
        Assert.Equal("Ctrl+Shift+F9", laptop.JumpHotkey);
    }

    [Fact]
    public async Task PeerDialog_LeavesADefaultJumpHotkeyFollowingTheList()
    {
        var settings = Samples.Settings();
        var dialog = new PeerSetupViewModel(settings.Peers[1], settings, null, new FakeDialogs());
        Assert.Equal("Ctrl+Alt+F2", dialog.JumpHotkey);

        await dialog.ConfirmCommand.ExecuteAsync(null);
        Assert.Null(settings.Peers[1].JumpHotkey);

        dialog = new PeerSetupViewModel(settings.Peers[1], settings, null, new FakeDialogs());
        dialog.JumpHotkey = string.Empty; // cleared: no jump hotkey
        await dialog.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(string.Empty, settings.Peers[1].JumpHotkey);
    }

    [Fact]
    public async Task PeerDialog_RefusesAJumpHotkeyAnotherActionUses()
    {
        var settings = Samples.Settings();
        var dialogs = new FakeDialogs();
        var dialog = new PeerSetupViewModel(settings.Peers[1], settings, null, dialogs);

        dialog.JumpHotkey = "Ctrl+Alt+F1"; // Laptop's
        await dialog.ConfirmCommand.ExecuteAsync(null);

        Assert.Null(dialog.Result);
        Assert.Contains(dialogs.Messages, m => m.Contains("Jump to Laptop"));
    }

    [Fact]
    public async Task IdentityMismatch_OffersPairAgain_WhichForgetsTheKey()
    {
        var settings = Samples.Settings();
        var laptop = settings.Peers[0];
        laptop.IdentityKey = "cGlubmVkLWtleQ==";
        var backend = new FakeBackend { Settings = settings };
        backend.Failures["laptop"] = new PeerConnectFailure(PeerFailureKind.IdentityMismatch, "The machine claims to be Laptop but does not have its identity key.", DateTime.Now);
        var page = new PeersPageViewModel(settings, backend, new FakeDialogs { Answer = Services.DialogResult.Yes });

        var row = page.Peers.Single(p => p.Peer.Id == "laptop");
        Assert.True(row.CanPairAgain);
        Assert.Contains("pair again", row.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("Identity ", row.IdentityText);
        Assert.False(page.Peers.Single(p => p.Peer.Id == "mac").CanPairAgain);

        await row.PairAgainCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "laptop" }, backend.Forgotten);
        Assert.Equal(string.Empty, laptop.IdentityKey);
        Assert.False(row.CanPairAgain);
        Assert.Equal("Not paired yet", row.IdentityText);
    }

    [Fact]
    public async Task PairAgain_NeedsConfirmation()
    {
        var settings = Samples.Settings();
        var backend = new FakeBackend { Settings = settings };
        backend.Failures["laptop"] = new PeerConnectFailure(PeerFailureKind.IdentityMismatch, "changed", DateTime.Now);
        var page = new PeersPageViewModel(settings, backend, new FakeDialogs { Answer = Services.DialogResult.No });

        await page.Peers.Single(p => p.Peer.Id == "laptop").PairAgainCommand.ExecuteAsync(null);

        Assert.Empty(backend.Forgotten);
    }

    [Fact]
    public void NetworkPage_UsesTheCoreStrengthRule_AndShowsThisPcsFingerprint()
    {
        var settings = Samples.Settings();
        var page = new NetworkPageViewModel(settings, new FakeDialogs(), new FakeBackend());

        Assert.Equal(!PairingCode.IsStrong(settings.PairingCode), page.IsPairingCodeWeak);
        Assert.Equal("1A2B-3C4D-5E6F-7A8B-9C0D", page.IdentityFingerprint);
    }
}
