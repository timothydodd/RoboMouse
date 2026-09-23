using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace RoboMouse.App.Views;

/// <summary>
/// One settings row in the WinUI style: a header and optional description on the left, the control
/// on the right. Its look comes from the ControlTheme in Styles/AppStyles.axaml. The control gets the
/// header as its accessible name and the description as its help text, unless it names itself, so a
/// screen reader says "Wrap around, toggle switch" instead of just "toggle switch".
/// </summary>
public class SettingCard : ContentControl
{
    public static readonly StyledProperty<string?> HeaderProperty =
        AvaloniaProperty.Register<SettingCard, string?>(nameof(Header));

    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<SettingCard, string?>(nameof(Description));

    // The control this card named, so a later header change renames it but a name set in XAML is kept.
    private Control? _named;

    public string? Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(SettingCard);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ContentProperty || change.Property == HeaderProperty || change.Property == DescriptionProperty)
            LabelContent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        LabelContent(); // a panel's children are all there by now
    }

    private void LabelContent()
    {
        if (Content is not Control control)
            return;

        // A panel holding one input (a number box and its unit) gets the label on the input; a panel
        // of several buttons is left to name its own buttons.
        var target = control;
        if (control is Panel panel)
        {
            var inputs = panel.Children.Where(c => c.Focusable).ToList();
            if (inputs.Count != 1)
                return;
            target = inputs[0];
        }

        if (target != _named && !string.IsNullOrEmpty(AutomationProperties.GetName(target)))
            return; // named explicitly
        AutomationProperties.SetName(target, Header ?? string.Empty);
        AutomationProperties.SetHelpText(target, Description);
        _named = target;
    }
}
