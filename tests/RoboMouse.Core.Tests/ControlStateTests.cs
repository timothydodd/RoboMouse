using RoboMouse.Core.Configuration;
using RoboMouse.Core.Input;
using RoboMouse.Core.Network;
using RoboMouse.Core.Network.Protocol;
using Xunit;
using ProtocolMessage = RoboMouse.Core.Network.Protocol.Message;

namespace RoboMouse.Core.Tests;

/// <summary>A peer link that records what was posted to it.</summary>
internal sealed class FakeLink : IPeerLink
{
    public FakeLink(string peerId, bool outbound = false)
    {
        PeerId = peerId;
        PeerName = peerId.ToUpperInvariant();
        IsOutbound = outbound;
    }

    public string PeerId { get; }
    public string PeerName { get; }
    public bool IsConnected { get; set; } = true;
    public bool IsOutbound { get; }
    public List<ProtocolMessage> Posted { get; } = new();

    public void Post(ProtocolMessage message) => Posted.Add(message);
}

/// <summary>An injector that records what it was asked to inject.</summary>
internal sealed class RecordingInjector : IInputInjector
{
    public List<string> Log { get; } = new();

    public bool ReachesSecureDesktop => false;
    public bool MoveRelative(int deltaX, int deltaY) { Log.Add($"move {deltaX},{deltaY}"); return true; }
    public void MoveTo(int x, int y) => Log.Add($"to {x},{y}");
    public (int X, int Y) GetCursorPosition() => (0, 0);
    public bool SimulateMouseEvent(MouseEventType eventType, int wheelDelta = 0) { Log.Add(eventType.ToString()); return true; }

    public bool SimulateKeyboardEvent(Keys keyCode, uint scanCode, KeyboardEventType eventType, bool isExtended)
    {
        Log.Add($"{keyCode} {eventType}");
        return true;
    }
}

public class ControlledSessionTests
{
    private static KeyboardMessage Key(Keys key, bool down) => new()
    {
        KeyCode = key,
        ScanCode = 0x1D,
        EventType = down ? KeyboardEventType.KeyDown : KeyboardEventType.KeyUp
    };

    [Fact]
    public void CursorEnter_IsRefused_WhileSharingIsOff()
    {
        var session = new ControlledSession(new RecordingInjector());
        var outcome = session.Enter(new FakeLink("a"), ScreenPosition.Left, false, sharingEnabled: false, controllingAnother: false, out _);

        Assert.Equal(EnterOutcome.Refused, outcome);
        Assert.False(session.IsActive);
    }

    [Fact]
    public void CursorEnter_IsRefused_WhileControllingAnotherMachine()
    {
        var session = new ControlledSession(new RecordingInjector());
        var outcome = session.Enter(new FakeLink("a"), ScreenPosition.Left, false, sharingEnabled: true, controllingAnother: true, out _);

        Assert.Equal(EnterOutcome.Refused, outcome);
        Assert.False(session.IsActive);
    }

    [Fact]
    public void InputFromANonController_IsIgnored()
    {
        var injector = new RecordingInjector();
        var session = new ControlledSession(injector);
        var a = new FakeLink("a");
        var b = new FakeLink("b");

        Assert.False(session.ApplyKey(a, Key(Keys.A, true)));
        session.Enter(a, ScreenPosition.Left, false, true, false, out _);
        Assert.False(session.ApplyKey(b, Key(Keys.A, true)));
        Assert.False(session.ApplyMouseButton(b, MouseEventType.LeftDown, 0));

        Assert.Empty(injector.Log);
    }

    [Fact]
    public void Ending_ReleasesEverythingStillHeld()
    {
        var injector = new RecordingInjector();
        var session = new ControlledSession(injector);
        var a = new FakeLink("a");
        session.Enter(a, ScreenPosition.Left, false, true, false, out _);

        session.ApplyKey(a, Key(Keys.LControlKey, true));
        session.ApplyKey(a, Key(Keys.C, true));
        session.ApplyKey(a, Key(Keys.C, false));
        session.ApplyMouseButton(a, MouseEventType.LeftDown, 0);
        injector.Log.Clear();

        Assert.True(session.TryEnd(null, out var former));
        Assert.Same(a, former);
        Assert.Equal(new[] { "LControlKey KeyUp", "LeftUp" }, injector.Log);
        Assert.False(session.IsActive);

        // Nothing left to release a second time, and input after the end is not applied.
        injector.Log.Clear();
        Assert.False(session.TryEnd(null, out _));
        Assert.False(session.ApplyKey(a, Key(Keys.A, true)));
        Assert.Empty(injector.Log);
    }

    [Fact]
    public void AnotherPeerEntering_ReplacesTheControllerAndReleasesItsInput()
    {
        var injector = new RecordingInjector();
        var session = new ControlledSession(injector);
        var a = new FakeLink("a");
        var b = new FakeLink("b");
        session.Enter(a, ScreenPosition.Left, false, true, false, out _);
        session.ApplyKey(a, Key(Keys.LShiftKey, true));
        injector.Log.Clear();

        var outcome = session.Enter(b, ScreenPosition.Right, true, true, false, out var replaced);

        Assert.Equal(EnterOutcome.Entered, outcome);
        Assert.Same(a, replaced);
        Assert.Equal(new[] { "LShiftKey KeyUp" }, injector.Log);
        Assert.True(session.IsControlledBy(b));
        Assert.Equal(ScreenPosition.Right, session.EntryEdge);
        Assert.True(session.WrapsAround);
    }

    [Fact]
    public void TryEnd_WithOnlyIf_LeavesAnotherControllerAlone()
    {
        var session = new ControlledSession(new RecordingInjector());
        var a = new FakeLink("a");
        session.Enter(a, ScreenPosition.Left, false, true, false, out _);

        Assert.False(session.TryEnd(new FakeLink("b"), out _));
        Assert.True(session.IsActive);
        Assert.True(session.TryEnd(a, out _));
    }
}

public class ConnectionRegistryTests
{
    [Fact]
    public void ReplacingTheActiveConnection_ClearsIt()
    {
        // Local id "b" is larger than peer "a", so the peer's outbound connection (our inbound) wins.
        var registry = new ConnectionRegistry<FakeLink>();
        var outbound = new FakeLink("a", outbound: true);
        registry.Add(outbound, "b");
        Assert.Same(outbound, registry.TryActivate("a"));

        var inbound = new FakeLink("a", outbound: false);
        var result = registry.Add(inbound, "b");

        Assert.True(result.Added);
        Assert.Same(outbound, result.Replaced);
        Assert.True(result.ReplacedWasActive);
        Assert.Null(registry.Active);
        Assert.Same(inbound, registry.Get("a"));
    }

    [Fact]
    public void DuplicateThatLoses_IsNotAdded()
    {
        var registry = new ConnectionRegistry<FakeLink>();
        var inbound = new FakeLink("a", outbound: false);
        registry.Add(inbound, "b");
        registry.TryActivate("a");

        var result = registry.Add(new FakeLink("a", outbound: true), "b");

        Assert.False(result.Added);
        Assert.Same(inbound, registry.Active);
    }

    [Fact]
    public void ActivatingARemovedConnection_Fails()
    {
        var registry = new ConnectionRegistry<FakeLink>();
        var link = new FakeLink("a");
        registry.Add(link, "b");
        Assert.True(registry.Remove(link, out var wasActive));
        Assert.False(wasActive);

        Assert.Null(registry.TryActivate("a"));
        Assert.Null(registry.Active);
    }

    [Fact]
    public void ActivatingADeadConnection_Fails()
    {
        var registry = new ConnectionRegistry<FakeLink>();
        registry.Add(new FakeLink("a") { IsConnected = false }, "b");

        Assert.Null(registry.TryActivate("a"));
    }

    [Fact]
    public void RemovingTheActiveConnection_ReportsItAndClearsIt()
    {
        var registry = new ConnectionRegistry<FakeLink>();
        var link = new FakeLink("a");
        registry.Add(link, "b");
        registry.TryActivate("a");

        Assert.True(registry.Remove(link, out var wasActive));
        Assert.True(wasActive);
        Assert.Null(registry.Active);

        // A stale Remove for a connection that was already replaced does nothing.
        Assert.False(registry.Remove(link, out _));
    }
}

public class HeldInputTests
{
    [Fact]
    public void TracksButtonsAndKeys_AndReleasesThemOnce()
    {
        var held = new HeldInput();
        held.TrackButton(MouseEventType.LeftDown);
        held.TrackButton(MouseEventType.XButton1Down);
        held.TrackButton(MouseEventType.XButton1Up);
        held.TrackButton(MouseEventType.Move);
        held.TrackKey(Keys.LControlKey, 0x1D, false, true);
        held.TrackKey(Keys.A, 0x1E, false, true);
        held.TrackKey(Keys.A, 0x1E, false, false);

        Assert.True(held.AnyButtonDown);
        Assert.Equal(new[] { Keys.LControlKey }, held.HeldModifiers().Select(m => m.Key));

        var (keys, buttons) = held.TakeReleases();
        Assert.Equal(new[] { Keys.LControlKey }, keys.Select(k => k.Key));
        Assert.Equal(new[] { MouseEventType.LeftUp }, buttons);
        Assert.True(held.IsEmpty);
    }

    [Fact]
    public void HeldModifiers_ExcludesOrdinaryKeys()
    {
        var held = new HeldInput();
        held.TrackKey(Keys.RShiftKey, 0x36, false, true);
        held.TrackKey(Keys.LWin, 0x5B, true, true);
        held.TrackKey(Keys.Space, 0x39, false, true);

        Assert.Equal(new[] { Keys.LWin, Keys.RShiftKey }, held.HeldModifiers().Select(m => m.Key).OrderBy(k => k.ToString()));
    }
}

public class ModifierStateTests
{
    [Fact]
    public void Confirm_DropsModifiersWindowsSaysAreUp()
    {
        var state = new ModifierState();
        state.Update(Keys.LControlKey, true);
        state.Update(Keys.LMenu, true);

        // Windows says Ctrl is still down but Alt was released (on the secure desktop, say).
        state.Confirm(key => key == Keys.ControlKey);

        Assert.True(state.Ctrl);
        Assert.False(state.Alt);
        Assert.False(Hotkey.Parse("Ctrl+Alt+M")!.Matches(Keys.M, state));
    }
}
