namespace RoboMouse.Core.Input;

/// <summary>
/// How a forwarded key event is injected on the controlled machine, so that the controlled machine's
/// own keyboard layout decides what it types, as if the keyboard were plugged into it.
///
/// <list type="bullet">
/// <item>Normal keys go by scan code (<c>KEYEVENTF_SCANCODE</c>, plus <c>KEYEVENTF_EXTENDEDKEY</c> for
/// the extended ones): the physical key position, which the receiving layout turns into a character.
/// Injecting the controller's virtual-key code instead would type the controller's layout (a German
/// controller's Z arriving as Y on a US machine, or the other way round).</item>
/// <item>A key event with no scan code, or a key whose meaning never depends on the layout and whose scan
/// code is ambiguous (Pause and Num Lock share 0x45; media, browser and launch keys often come from
/// vendor software with made-up scan codes), goes by virtual-key code. So do the modifiers (Shift, Ctrl,
/// Alt, Win, Caps Lock): they mean the same in every layout, and the hook reports Right Shift with the
/// extended flag, which as a scan code (E0 36) is no key at all.</item>
/// <item>A Unicode character (<see cref="Keys.Packet"/>: an on-screen keyboard, IME or password manager
/// typing text) carries the UTF-16 code unit in the scan code field and is injected with
/// <c>KEYEVENTF_UNICODE</c>, so it arrives as that character whatever the layout.</item>
/// </list>
/// </summary>
public static class KeyInjection
{
    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_UNICODE = 0x0004;
    public const uint KEYEVENTF_SCANCODE = 0x0008;

    /// <summary>What goes into the <c>KEYBDINPUT</c> fields.</summary>
    public readonly record struct Plan(ushort VirtualKey, ushort ScanCode, uint Flags);

    /// <summary>Works out the <c>KEYBDINPUT</c> for one key event.</summary>
    public static Plan For(Keys keyCode, uint scanCode, KeyboardEventType eventType, bool isExtended)
    {
        var up = eventType is KeyboardEventType.KeyUp or KeyboardEventType.SysKeyUp ? KEYEVENTF_KEYUP : 0u;

        if (keyCode == Keys.Packet)
            return new Plan(0, (ushort)scanCode, KEYEVENTF_UNICODE | up);

        var extended = isExtended ? KEYEVENTF_EXTENDEDKEY : 0u;
        if (scanCode == 0 || scanCode > 0xFF || InjectByVirtualKey(keyCode))
            return new Plan((ushort)keyCode, (ushort)scanCode, extended | up);

        return new Plan(0, (ushort)scanCode, KEYEVENTF_SCANCODE | extended | up);
    }

    /// <summary>Keys injected by virtual-key code even when they have a scan code: layout-independent, and their scan codes are unreliable.</summary>
    public static bool InjectByVirtualKey(Keys key) =>
        key is Keys.Pause or Keys.NumLock or Keys.Cancel or Keys.Snapshot or Keys.Sleep
            or >= Keys.BrowserBack and <= Keys.LaunchApplication2
            or Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey
            or Keys.ControlKey or Keys.LControlKey or Keys.RControlKey
            or Keys.Menu or Keys.LMenu or Keys.RMenu
            or Keys.LWin or Keys.RWin or Keys.CapsLock;
}
