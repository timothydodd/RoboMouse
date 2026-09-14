using System.Windows.Forms;

namespace RoboMouse.Core.Input;

/// <summary>
/// A key chord such as "Ctrl+Alt+M", parsed from settings and matched against live key state.
/// </summary>
public sealed record Hotkey(Keys Key, bool Ctrl, bool Alt, bool Shift, bool Win)
{
    /// <summary>
    /// Parses text like "Ctrl+Alt+M" or "Win+Shift+F12". Returns null when the text is empty or invalid.
    /// </summary>
    public static Hotkey? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        bool ctrl = false, alt = false, shift = false, win = false;
        Keys? key = null;

        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": ctrl = true; break;
                case "alt": alt = true; break;
                case "shift": shift = true; break;
                case "win" or "windows" or "meta": win = true; break;
                default:
                    if (key != null)
                        return null;
                    if (Enum.TryParse<Keys>(raw, ignoreCase: true, out var parsed) && parsed != Keys.None)
                        key = parsed;
                    else if (raw.Length == 1 && char.IsDigit(raw[0]))
                        key = Keys.D0 + (raw[0] - '0');
                    else
                        return null;
                    break;
            }
        }

        if (key == null || !(ctrl || alt || shift || win))
            return null; // Require at least one modifier so ordinary typing can never trigger it

        return new Hotkey(key.Value, ctrl, alt, shift, win);
    }

    /// <summary>
    /// True when <paramref name="pressed"/> is this chord's key and the required modifiers are currently held.
    /// </summary>
    public bool Matches(Keys pressed)
    {
        if (pressed != Key)
            return false;

        return IsDown(Keys.ControlKey) == Ctrl
            && IsDown(Keys.Menu) == Alt
            && IsDown(Keys.ShiftKey) == Shift
            && (IsDown(Keys.LWin) || IsDown(Keys.RWin)) == Win;
    }

    private static bool IsDown(Keys key) => (NativeMethods.GetAsyncKeyState((int)key) & 0x8000) != 0;

    public override string ToString()
    {
        var parts = new List<string>();
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        parts.Add(Key.ToString());
        return string.Join("+", parts);
    }
}
