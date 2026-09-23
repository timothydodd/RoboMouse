using System.Drawing;
using RoboMouse.Core.Configuration;
using RoboMouse.Core.Input;
using RoboMouse.Core.Screen;
using Xunit;

namespace RoboMouse.Core.Tests;

public class CrossingGuardTests
{
    private static CrossingAttempt At(ScreenPosition edge = ScreenPosition.Right, bool corner = false, bool button = false,
        CrossingModifiers modifiers = CrossingModifiers.None, bool fullScreen = false) =>
        new(edge, corner, button, modifiers, fullScreen);

    [Fact]
    public void Defaults_CrossAtOnce_ButNotWithAButtonHeldOrInTheCorner()
    {
        var settings = new CrossingSettings();
        var guard = new CrossingGuard();

        Assert.Equal(CrossingDecision.ButtonHeld, guard.Evaluate(settings, At(button: true), 0));
        Assert.Equal(CrossingDecision.Corner, guard.Evaluate(settings, At(corner: true), 1));
        Assert.Equal(CrossingDecision.Allow, guard.Evaluate(settings, At(), 2));
    }

    [Fact]
    public void ButtonGuard_CanBeTurnedOff()
    {
        var settings = new CrossingSettings { BlockWhileButtonHeld = false };
        Assert.Equal(CrossingDecision.Allow, new CrossingGuard().Evaluate(settings, At(button: true), 0));
    }

    [Theory]
    [InlineData(CrossingModifier.Ctrl, CrossingModifiers.Ctrl, true)]
    [InlineData(CrossingModifier.Ctrl, CrossingModifiers.Ctrl | CrossingModifiers.Shift, true)]
    [InlineData(CrossingModifier.Ctrl, CrossingModifiers.Alt, false)]
    [InlineData(CrossingModifier.Win, CrossingModifiers.None, false)]
    [InlineData(CrossingModifier.None, CrossingModifiers.None, true)]
    public void RequiredModifier_MustBeHeld(CrossingModifier required, CrossingModifiers held, bool allowed)
    {
        var settings = new CrossingSettings { RequiredModifier = required };
        var decision = new CrossingGuard().Evaluate(settings, At(modifiers: held), 0);
        Assert.Equal(allowed ? CrossingDecision.Allow : CrossingDecision.ModifierMissing, decision);
    }

    [Fact]
    public void FullScreen_BlocksOnlyWhenTheGuardIsOn()
    {
        Assert.Equal(CrossingDecision.Allow, new CrossingGuard().Evaluate(new CrossingSettings(), At(fullScreen: true), 0));
        var settings = new CrossingSettings { BlockWhileFullScreen = true };
        Assert.Equal(CrossingDecision.FullScreen, new CrossingGuard().Evaluate(settings, At(fullScreen: true), 0));
        Assert.Equal(CrossingDecision.Allow, new CrossingGuard().Evaluate(settings, At(), 0));
    }

    [Fact]
    public void PushTwice_NeedsASecondArrivalWithinTheWindow()
    {
        var settings = new CrossingSettings { DoubleTap = true, DoubleTapWindowMs = 500 };
        var guard = new CrossingGuard();

        Assert.Equal(CrossingDecision.NotYet, guard.Evaluate(settings, At(), 1000));
        // Leaning on the edge is still the first push.
        Assert.Equal(CrossingDecision.NotYet, guard.Evaluate(settings, At(), 1100));
        Assert.Equal(CrossingDecision.NotYet, guard.Evaluate(settings, At(), 1300));

        guard.LeftEdge();
        Assert.Equal(CrossingDecision.Allow, guard.Evaluate(settings, At(), 1400));

        // After crossing, the way back starts from scratch.
        Assert.Equal(CrossingDecision.NotYet, guard.Evaluate(settings, At(), 1450));
    }

    [Fact]
    public void PushTwice_TooSlowOrOnAnotherEdge_StartsOver()
    {
        var settings = new CrossingSettings { DoubleTap = true, DoubleTapWindowMs = 500 };
        var guard = new CrossingGuard();

        Assert.Equal(CrossingDecision.NotYet, guard.Evaluate(settings, At(), 0));
        guard.LeftEdge();
        Assert.Equal(CrossingDecision.NotYet, guard.Evaluate(settings, At(), 900)); // too slow: a new first push
        guard.LeftEdge();
        Assert.Equal(CrossingDecision.NotYet, guard.Evaluate(settings, At(ScreenPosition.Left), 1000)); // another edge
        guard.LeftEdge();
        Assert.Equal(CrossingDecision.Allow, guard.Evaluate(settings, At(ScreenPosition.Left), 1200));
    }

    [Fact]
    public void Delay_NeedsPushingForThatLong()
    {
        var settings = new CrossingSettings { DelayMs = 300 };
        var guard = new CrossingGuard();

        Assert.Equal(CrossingDecision.NotYet, guard.Evaluate(settings, At(), 1000));
        Assert.Equal(CrossingDecision.NotYet, guard.Evaluate(settings, At(), 1200));
        Assert.Equal(CrossingDecision.Allow, guard.Evaluate(settings, At(), 1300));

        // Leaving the edge restarts the wait.
        Assert.Equal(CrossingDecision.NotYet, guard.Evaluate(settings, At(), 2000));
        guard.LeftEdge();
        Assert.Equal(CrossingDecision.NotYet, guard.Evaluate(settings, At(), 2250));
    }

    [Fact]
    public void PushTwiceAndDelay_EitherOneIsEnough()
    {
        var settings = new CrossingSettings { DoubleTap = true, DelayMs = 400 };

        var byDelay = new CrossingGuard();
        Assert.Equal(CrossingDecision.NotYet, byDelay.Evaluate(settings, At(), 0));
        Assert.Equal(CrossingDecision.Allow, byDelay.Evaluate(settings, At(), 450));

        var byTap = new CrossingGuard();
        Assert.Equal(CrossingDecision.NotYet, byTap.Evaluate(settings, At(), 0));
        byTap.LeftEdge();
        Assert.Equal(CrossingDecision.Allow, byTap.Evaluate(settings, At(), 200));
    }

    [Fact]
    public void RefusedPush_StillCountsAsTheFirstTap()
    {
        // The first push lands in the corner zone; the second, further along, crosses.
        var settings = new CrossingSettings { DoubleTap = true };
        var guard = new CrossingGuard();
        Assert.Equal(CrossingDecision.Corner, guard.Evaluate(settings, At(corner: true), 0));
        guard.LeftEdge();
        Assert.Equal(CrossingDecision.Allow, guard.Evaluate(settings, At(), 300));
    }

    private static MonitorRect Monitor(int x, int y, int w, int h, bool primary = false)
    {
        var r = new Rectangle(x, y, w, h);
        return new MonitorRect(r, r, primary);
    }

    [Fact]
    public void EdgeEnd_IsTheCornerOfTheDesktop()
    {
        var layout = new MonitorLayout(new[] { Monitor(0, 0, 1920, 1080, primary: true) });

        Assert.True(layout.IsNearEdgeEnd(ScreenPosition.Right, 1919, 5, 20));
        Assert.True(layout.IsNearEdgeEnd(ScreenPosition.Right, 1919, 1070, 20));
        Assert.False(layout.IsNearEdgeEnd(ScreenPosition.Right, 1919, 540, 20));
        Assert.True(layout.IsNearEdgeEnd(ScreenPosition.Top, 10, 0, 20));
        Assert.False(layout.IsNearEdgeEnd(ScreenPosition.Top, 960, 0, 20));
        Assert.False(layout.IsNearEdgeEnd(ScreenPosition.Right, 1919, 5, 0)); // zone off
        Assert.True(layout.IsNearEdgeEnd(ScreenPosition.Right, 1925, 5, 20)); // pushed past the edge
    }

    [Fact]
    public void EdgeEnd_IsNotWhereTheSameEdgeContinuesOnTheNextMonitor()
    {
        // Two 1920x1080 screens stacked: their right edges line up, so the seam is not a corner.
        var stacked = new MonitorLayout(new[] { Monitor(0, 0, 1920, 1080, primary: true), Monitor(0, 1080, 1920, 1080) });
        Assert.False(stacked.IsNearEdgeEnd(ScreenPosition.Right, 1919, 1075, 20));
        Assert.False(stacked.IsNearEdgeEnd(ScreenPosition.Right, 1919, 1085, 20));
        Assert.True(stacked.IsNearEdgeEnd(ScreenPosition.Right, 1919, 2150, 20));

        // A narrower lower screen: the upper one's right edge ends in a step there.
        var step = new MonitorLayout(new[] { Monitor(0, 0, 1920, 1080, primary: true), Monitor(0, 1080, 1280, 1024) });
        Assert.True(step.IsNearEdgeEnd(ScreenPosition.Right, 1919, 1075, 20));
    }
}

public class HotkeySetTests
{
    private static AppSettings Settings(params PeerConfig[] peers)
    {
        var settings = new AppSettings { ToggleHotkey = "Ctrl+Alt+M", LockCursorHotkey = "Scroll", LockAllHotkey = null };
        settings.Peers.AddRange(peers);
        return settings;
    }

    private static ModifierState Held(params Keys[] keys)
    {
        var state = new ModifierState();
        foreach (var key in keys)
            state.Update(key, isDown: true);
        return state;
    }

    [Fact]
    public void ScrollLock_WorksAlone_ButLetterKeysNeedAModifier()
    {
        Assert.NotNull(Hotkey.Parse("Scroll"));
        Assert.NotNull(Hotkey.Parse("Pause"));
        Assert.NotNull(Hotkey.Parse("F13"));
        Assert.Null(Hotkey.Parse("F1"));
        Assert.Null(Hotkey.Parse("L"));
    }

    [Fact]
    public void FirstFourPeers_GetCtrlAltF1ToF4_UnlessTheyChoseOtherwise()
    {
        var peers = Enumerable.Range(0, 6).Select(i => new PeerConfig { Id = $"p{i}", Name = $"P{i}" }).ToArray();
        peers[1].JumpHotkey = "Ctrl+Shift+2";
        peers[2].JumpHotkey = string.Empty; // none
        var set = HotkeySet.From(Settings(peers));

        var jumps = set.Bindings.Where(b => b.Action == HotkeyAction.JumpToPeer).ToDictionary(b => b.PeerId!, b => b.Hotkey.ToString());
        Assert.Equal("Ctrl+Alt+F1", jumps["p0"]);
        Assert.Equal("Ctrl+Shift+D2", jumps["p1"]);
        Assert.False(jumps.ContainsKey("p2"));
        Assert.Equal("Ctrl+Alt+F4", jumps["p3"]);
        Assert.False(jumps.ContainsKey("p4"));
        Assert.False(jumps.ContainsKey("p5"));
    }

    [Fact]
    public void DisabledPeer_HasNoJump()
    {
        var set = HotkeySet.From(Settings(new PeerConfig { Id = "off", Enabled = false }));
        Assert.DoesNotContain(set.Bindings, b => b.Action == HotkeyAction.JumpToPeer);
    }

    [Fact]
    public void Find_MatchesTheExactChord()
    {
        var set = HotkeySet.From(Settings(new PeerConfig { Id = "laptop" }));

        Assert.Equal(HotkeyAction.LockCursor, set.Find(Keys.Scroll, Held())?.Action);
        Assert.Null(set.Find(Keys.Scroll, Held(Keys.LControlKey)));
        Assert.Equal(HotkeyAction.ToggleSharing, set.Find(Keys.M, Held(Keys.LControlKey, Keys.LMenu))?.Action);
        var jump = set.Find(Keys.F1, Held(Keys.RControlKey, Keys.RMenu));
        Assert.Equal(HotkeyAction.JumpToPeer, jump?.Action);
        Assert.Equal("laptop", jump?.PeerId);
        Assert.Null(set.Find(Keys.F1, Held(Keys.LControlKey)));

        Assert.True(set.UsesKey(Keys.F1));
        Assert.False(set.UsesKey(Keys.A));
    }

    [Fact]
    public void ToggleHotkey_WinsAConflict()
    {
        var settings = Settings(new PeerConfig { Id = "laptop", JumpHotkey = "Ctrl+Alt+M" });
        var set = HotkeySet.From(settings);
        Assert.Equal(HotkeyAction.ToggleSharing, set.Find(Keys.M, Held(Keys.LControlKey, Keys.LMenu))?.Action);
    }

    [Fact]
    public void FindConflict_NamesBothHotkeys()
    {
        Assert.Null(HotkeySet.FindConflict(new (string, string?)[] { ("A", "Ctrl+Alt+M"), ("B", "Scroll"), ("C", ""), ("D", null) }));
        var message = HotkeySet.FindConflict(new (string, string?)[] { ("The toggle hotkey", "Ctrl+Alt+M"), ("Jump to Laptop", "alt+ctrl+m") });
        Assert.NotNull(message);
        Assert.Contains("The toggle hotkey", message);
        Assert.Contains("Jump to Laptop", message);
    }

    [Fact]
    public void EffectiveJumpHotkey_ForANewPeer_IsTheNextDefault()
    {
        var settings = Settings(new PeerConfig { Id = "a" });
        Assert.Equal("Ctrl+Alt+F2", HotkeySet.EffectiveJumpHotkey(settings, new PeerConfig()));
        Assert.Equal("Ctrl+Alt+F1", HotkeySet.EffectiveJumpHotkey(settings, settings.Peers[0]));
    }
}
