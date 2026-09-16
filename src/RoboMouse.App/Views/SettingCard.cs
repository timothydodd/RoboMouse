using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace RoboMouse.App.Views;

/// <summary>
/// One settings row in the WinUI style: a header and optional description on the left, the control
/// on the right. Its look comes from the ControlTheme in Styles/AppStyles.axaml.
/// </summary>
public class SettingCard : ContentControl
{
    public static readonly StyledProperty<string?> HeaderProperty =
        AvaloniaProperty.Register<SettingCard, string?>(nameof(Header));

    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<SettingCard, string?>(nameof(Description));

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
}
