using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using RoboMouse.App.ViewModels;

namespace RoboMouse.App.Views;

/// <summary>Maps a <see cref="StatusTone"/> to one of the app's status brushes.</summary>
public sealed class StatusToneToBrushConverter : IValueConverter
{
    public static readonly StatusToneToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            StatusTone.Ok => "AppStatusOkBrush",
            StatusTone.Warning => "AppStatusWarnBrush",
            StatusTone.Error => "AppStatusErrorBrush",
            StatusTone.Accent => "SystemControlHighlightAccentBrush",
            _ => "AppStatusIdleBrush"
        };
        return Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var brush) == true
            ? brush as IBrush
            : Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
