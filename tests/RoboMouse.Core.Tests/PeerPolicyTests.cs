using RoboMouse.Core.Configuration;
using RoboMouse.Core.Network.Protocol;
using Xunit;
using ProtocolMessage = RoboMouse.Core.Network.Protocol.Message;

namespace RoboMouse.Core.Tests;

public class ClipboardSharingTests
{
    [Fact]
    public void SharesOnlyWithConfiguredPeersThatHaveItOn()
    {
        var settings = new AppSettings
        {
            Peers =
            {
                new PeerConfig { Id = "on" },
                new PeerConfig { Id = "off", ShareClipboard = false }
            }
        };

        Assert.True(ClipboardSharing.SharesWith(settings, "on"));
        Assert.False(ClipboardSharing.SharesWith(settings, "off"));
        Assert.False(ClipboardSharing.SharesWith(settings, "stranger"));
    }

    [Fact]
    public void ShareClipboard_DefaultsOn_ForOldSettingsFiles()
    {
        Assert.True(new PeerConfig().ShareClipboard);
    }
}

public class LockPolicyTests
{
    private const string Key = "cGlubmVkLWtleQ==";

    private static AppSettings Settings(bool lockWithHost = true, bool screensaverWithHost = true) => new()
    {
        LockWithHost = lockWithHost,
        ScreensaverWithHost = screensaverWithHost,
        Peers =
        {
            new PeerConfig { Id = "host", IdentityKey = Key },
            new PeerConfig { Id = "unpaired" },
            new PeerConfig { Id = "disabled", IdentityKey = Key, Enabled = false }
        }
    };

    [Theory]
    [InlineData("host", Key, true)]
    [InlineData("host", "b3RoZXIta2V5", false)]   // another key under the paired peer's id
    [InlineData("host", "", false)]
    [InlineData("unpaired", Key, false)]          // knows the code, but no pinned key
    [InlineData("disabled", Key, false)]
    [InlineData("stranger", Key, false)]
    public void LockRequests_OnlyFromPairedEnabledPeers(string peerId, string key, bool accepted)
    {
        Assert.Equal(accepted, LockPolicy.AcceptsLockRequest(Settings(), peerId, key));
    }

    [Fact]
    public void FollowsTheHostLocking_Once()
    {
        var settings = Settings();
        Assert.Equal(SessionFollowAction.Lock,
            LockPolicy.Follow(settings, "host", Key, "host", (false, false), (true, false), uint.MaxValue));
        // Already known to be locked: nothing new.
        Assert.Equal(SessionFollowAction.None,
            LockPolicy.Follow(settings, "host", Key, "host", (true, false), (true, false), uint.MaxValue));
        // Unlocking is never mirrored.
        Assert.Equal(SessionFollowAction.None,
            LockPolicy.Follow(settings, "host", Key, "host", (true, false), (false, false), uint.MaxValue));
    }

    [Fact]
    public void OnlyTheHost_WithTheSettingOn_IsFollowed()
    {
        Assert.Equal(SessionFollowAction.None,
            LockPolicy.Follow(Settings(lockWithHost: false), "host", Key, "host", (false, false), (true, false), uint.MaxValue));
        Assert.Equal(SessionFollowAction.None,
            LockPolicy.Follow(Settings(), "host", Key, hostId: null, (false, false), (true, false), uint.MaxValue));
        Assert.Equal(SessionFollowAction.None,
            LockPolicy.Follow(Settings(), "host", Key, hostId: "someone-else", (false, false), (true, false), uint.MaxValue));
        Assert.Equal(SessionFollowAction.None,
            LockPolicy.Follow(Settings(), "unpaired", Key, "unpaired", (false, false), (true, false), uint.MaxValue));
    }

    [Fact]
    public void Screensaver_FollowsTheHost_UnlessThisPcWasJustUsed()
    {
        var settings = Settings();
        Assert.Equal(SessionFollowAction.StartScreensaver,
            LockPolicy.Follow(settings, "host", Key, "host", (false, false), (false, true), LockPolicy.LocalUseWindowMs));
        Assert.Equal(SessionFollowAction.None,
            LockPolicy.Follow(settings, "host", Key, "host", (false, false), (false, true), 5_000));
        Assert.Equal(SessionFollowAction.None,
            LockPolicy.Follow(Settings(screensaverWithHost: false), "host", Key, "host", (false, false), (false, true), uint.MaxValue));
    }

    [Fact]
    public void SessionMessages_RoundTrip()
    {
        var state = ProtocolMessage.Deserialize(new SessionStateMessage { Locked = true, ScreensaverRunning = true }.Serialize()) as SessionStateMessage;
        Assert.NotNull(state);
        Assert.True(state.Locked);
        Assert.True(state.ScreensaverRunning);

        var cursorLock = ProtocolMessage.Deserialize(new CursorLockMessage { Locked = true }.Serialize()) as CursorLockMessage;
        Assert.True(cursorLock?.Locked);

        Assert.IsType<LockRequestMessage>(ProtocolMessage.Deserialize(new LockRequestMessage().Serialize()));
    }
}
