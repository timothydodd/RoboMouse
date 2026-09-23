using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using RoboMouse.Core.Input;

namespace RoboMouse.App.Views;

/// <summary>
/// A text box that records a key chord instead of taking typed text: focus it and press the
/// combination (for example Ctrl+Alt+M). Only chords <see cref="RoboMouse.Core.Input.Hotkey.Parse"/>
/// accepts are taken, so it can never hold a plain key or an unknown name. Tab still moves focus.
/// </summary>
public class HotkeyBox : TextBox
{
    public static readonly StyledProperty<string?> HotkeyProperty =
        AvaloniaProperty.Register<HotkeyBox, string?>(nameof(Hotkey), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>The chord in settings form ("Ctrl+Alt+M"), or null/empty when none is set.</summary>
    public string? Hotkey
    {
        get => GetValue(HotkeyProperty);
        set => SetValue(HotkeyProperty, value);
    }

    public HotkeyBox()
    {
        IsReadOnly = true;
        IsUndoEnabled = false;
        PlaceholderText = "Press a key combination";
    }

    protected override Type StyleKeyOverride => typeof(TextBox);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HotkeyProperty)
            Text = change.GetNewValue<string?>();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Plain Tab / Shift+Tab keep their focus-moving job.
        if (e.Key == Key.Tab && (e.KeyModifiers & ~KeyModifiers.Shift) == KeyModifiers.None)
        {
            base.OnKeyDown(e);
            return;
        }

        e.Handled = true;
        var chord = Format(e.Key, e.KeyModifiers);
        if (chord != null)
            Hotkey = chord;
    }

    /// <summary>
    /// The settings text for a key press, or null when it is not a usable hotkey: a modifier on its
    /// own, no modifier at all, or a key the core has no name for. Avalonia's key names match the
    /// core's (Windows Forms) names for every key a hotkey would use.
    /// </summary>
    public static string? Format(Key key, KeyModifiers modifiers)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.None)
            return null;

        var parts = new List<string>(5);
        if (modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Win");
        if (!Enum.TryParse<Keys>(key.ToString(), ignoreCase: true, out var coreKey) || coreKey == Keys.None)
            return null;
        parts.Add(coreKey.ToString());

        var text = string.Join("+", parts);
        return RoboMouse.Core.Input.Hotkey.Parse(text)?.ToString();
    }
}
