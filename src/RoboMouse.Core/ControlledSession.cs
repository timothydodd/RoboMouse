using RoboMouse.Core.Configuration;
using RoboMouse.Core.Input;
using RoboMouse.Core.Network;
using RoboMouse.Core.Network.Protocol;

namespace RoboMouse.Core;

/// <summary>What happened to a <see cref="CursorEnterMessage"/>.</summary>
public enum EnterOutcome
{
    /// <summary>This machine is now controlled by the sender.</summary>
    Entered,

    /// <summary>Refused (sharing is off here, or this machine is controlling someone else). Tell the sender to take its cursor back.</summary>
    Refused
}

/// <summary>
/// The controlled side of a session: which peer is driving this screen, where its cursor came in, and
/// which keys and buttons it holds down. Everything that changes who is in control, and every injected
/// key or button, happens under one lock, so a key-down can never be injected after control ended and
/// its release already ran. Motion is not tracked here; it leaves nothing behind.
/// </summary>
public sealed class ControlledSession
{
    private readonly object _lock = new();
    private readonly IInputInjector _injector;
    private readonly HeldInput _held = new();
    private IPeerLink? _controller;
    private volatile bool _active;

    public ControlledSession(IInputInjector injector)
    {
        _injector = injector;
    }

    /// <summary>True while a peer controls this machine. Cheap; read from the input hooks.</summary>
    public bool IsActive => _active;

    /// <summary>The peer in control, or null.</summary>
    public IPeerLink? Controller
    {
        get { lock (_lock) return _controller; }
    }

    /// <summary>The local edge the controller's cursor came in on.</summary>
    public ScreenPosition EntryEdge { get; private set; }

    /// <summary>Whether the controller has wrap-around on (any edge hands control back).</summary>
    public bool WrapsAround { get; private set; }

    /// <summary>True when <paramref name="link"/> is the peer in control.</summary>
    public bool IsControlledBy(IPeerLink link)
    {
        lock (_lock) return _active && ReferenceEquals(_controller, link);
    }

    /// <summary>
    /// A peer's cursor arrives. Refused while sharing is off here or while this machine is controlling
    /// another one (two machines entering each other at once both refuse, and both get their cursor
    /// back). When a different peer was in control it is replaced: its held input is released first and
    /// it is returned in <paramref name="replaced"/> so the caller can tell it to take its cursor back.
    /// </summary>
    public EnterOutcome Enter(IPeerLink from, ScreenPosition entryEdge, bool wrapAround, bool sharingEnabled, bool controllingAnother, out IPeerLink? replaced)
    {
        replaced = null;
        if (!sharingEnabled || controllingAnother)
            return EnterOutcome.Refused;

        lock (_lock)
        {
            if (_active && _controller != null && !ReferenceEquals(_controller, from))
            {
                replaced = _controller;
                ReleaseLocked();
            }
            _controller = from;
            EntryEdge = entryEdge;
            WrapsAround = wrapAround;
            _active = true;
            return EnterOutcome.Entered;
        }
    }

    /// <summary>Applies a button or wheel event from <paramref name="from"/>. Returns false (and does nothing) unless it is in control.</summary>
    public bool ApplyMouseButton(IPeerLink from, MouseEventType eventType, int wheelDelta)
    {
        lock (_lock)
        {
            if (!_active || !ReferenceEquals(_controller, from))
                return false;
            _held.TrackButton(eventType);
            _injector.SimulateMouseEvent(eventType, wheelDelta);
            return true;
        }
    }

    /// <summary>Applies a key event from <paramref name="from"/>. Returns false (and does nothing) unless it is in control.</summary>
    public bool ApplyKey(IPeerLink from, KeyboardMessage message)
    {
        lock (_lock)
        {
            if (!_active || !ReferenceEquals(_controller, from))
                return false;
            var isDown = message.EventType is KeyboardEventType.KeyDown or KeyboardEventType.SysKeyDown;
            _held.TrackKey(message.KeyCode, message.ScanCode, message.IsExtendedKey, isDown);
            _injector.SimulateKeyboardEvent(message.KeyCode, message.ScanCode, message.EventType, message.IsExtendedKey);
            return true;
        }
    }

    /// <summary>
    /// Ends the session, releasing every key and button the controller left held. With
    /// <paramref name="onlyIf"/> set, only ends it when that peer is the one in control. Returns false
    /// when there was nothing to end; <paramref name="former"/> is the peer that was in control.
    /// </summary>
    public bool TryEnd(IPeerLink? onlyIf, out IPeerLink? former)
    {
        lock (_lock)
        {
            former = _controller;
            if (!_active || (onlyIf != null && !ReferenceEquals(_controller, onlyIf)))
            {
                former = null;
                return false;
            }
            ReleaseLocked();
            _active = false;
            _controller = null;
            return true;
        }
    }

    private void ReleaseLocked()
    {
        var (keys, buttons) = _held.TakeReleases();
        foreach (var (key, scan, extended) in keys)
            _injector.SimulateKeyboardEvent(key, scan, KeyboardEventType.KeyUp, extended);
        foreach (var up in buttons)
            _injector.SimulateMouseEvent(up);
    }
}
