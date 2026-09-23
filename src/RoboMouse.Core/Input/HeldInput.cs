namespace RoboMouse.Core.Input;

/// <summary>
/// Keys and mouse buttons currently held down, with what is needed to release them. Used on both ends:
/// the controlled machine tracks what it injected so nothing sticks when control ends, the controller
/// tracks what it forwarded so it can send the ups when the session locks, and the physical state from
/// the hooks decides whether the cursor may cross. Thread-safe.
/// </summary>
public sealed class HeldInput
{
    private readonly object _lock = new();
    private readonly Dictionary<Keys, (uint ScanCode, bool Extended)> _keys = new();
    private readonly HashSet<MouseEventType> _buttons = new();

    /// <summary>True while any mouse button is down.</summary>
    public bool AnyButtonDown
    {
        get { lock (_lock) return _buttons.Count > 0; }
    }

    /// <summary>True when nothing is held.</summary>
    public bool IsEmpty
    {
        get { lock (_lock) return _keys.Count == 0 && _buttons.Count == 0; }
    }

    /// <summary>Records a key going down or up.</summary>
    public void TrackKey(Keys key, uint scanCode, bool extended, bool isDown)
    {
        lock (_lock)
        {
            if (isDown)
                _keys[key] = (scanCode, extended);
            else
                _keys.Remove(key);
        }
    }

    /// <summary>Records a button down or up. Motion and wheel events are ignored.</summary>
    public void TrackButton(MouseEventType eventType)
    {
        lock (_lock)
        {
            if (UpFor(eventType) != null)
                _buttons.Add(eventType);
            else if (DownFor(eventType) is { } down)
                _buttons.Remove(down);
        }
    }

    /// <summary>The held modifier keys (Ctrl, Alt, Shift, Win; left and right kept apart).</summary>
    public List<(Keys Key, uint ScanCode, bool Extended)> HeldModifiers()
    {
        lock (_lock)
            return _keys.Where(k => IsModifier(k.Key)).Select(k => (k.Key, k.Value.ScanCode, k.Value.Extended)).ToList();
    }

    /// <summary>Returns a key-up for every held key and an up for every held button, and forgets them all.</summary>
    public (List<(Keys Key, uint ScanCode, bool Extended)> Keys, List<MouseEventType> ButtonUps) TakeReleases()
    {
        lock (_lock)
        {
            var keys = _keys.Select(k => (k.Key, k.Value.ScanCode, k.Value.Extended)).ToList();
            var buttons = _buttons.Select(b => UpFor(b)!.Value).ToList();
            _keys.Clear();
            _buttons.Clear();
            return (keys, buttons);
        }
    }

    /// <summary>Returns an up for every held button and forgets the buttons; held keys are kept.</summary>
    public List<MouseEventType> TakeButtonReleases()
    {
        lock (_lock)
        {
            var buttons = _buttons.Select(b => UpFor(b)!.Value).ToList();
            _buttons.Clear();
            return buttons;
        }
    }

    /// <summary>Forgets everything without releasing it.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _keys.Clear();
            _buttons.Clear();
        }
    }

    public static bool IsModifier(Keys key) => key is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey
        or Keys.Menu or Keys.LMenu or Keys.RMenu
        or Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey
        or Keys.LWin or Keys.RWin;

    /// <summary>The up event for a button-down event, or null when it is not one.</summary>
    public static MouseEventType? UpFor(MouseEventType down) => down switch
    {
        MouseEventType.LeftDown => MouseEventType.LeftUp,
        MouseEventType.RightDown => MouseEventType.RightUp,
        MouseEventType.MiddleDown => MouseEventType.MiddleUp,
        MouseEventType.XButton1Down => MouseEventType.XButton1Up,
        MouseEventType.XButton2Down => MouseEventType.XButton2Up,
        _ => null
    };

    /// <summary>The down event for a button-up event, or null when it is not one.</summary>
    public static MouseEventType? DownFor(MouseEventType up) => up switch
    {
        MouseEventType.LeftUp => MouseEventType.LeftDown,
        MouseEventType.RightUp => MouseEventType.RightDown,
        MouseEventType.MiddleUp => MouseEventType.MiddleDown,
        MouseEventType.XButton1Up => MouseEventType.XButton1Down,
        MouseEventType.XButton2Up => MouseEventType.XButton2Down,
        _ => null
    };
}
