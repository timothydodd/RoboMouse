using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;

namespace RoboMouse.App;

/// <summary>
/// Shared look for the app's windows: one font, one palette (taken from the app icon) and small
/// factory helpers so every window builds the same kinds of controls the same way.
/// </summary>
internal static class Ui
{
    // Palette
    public static readonly Color Accent = Color.FromRgb(30, 100, 230);
    public static readonly Color AccentDark = Color.FromRgb(22, 78, 190);
    public static readonly Color AccentSoft = Color.FromRgb(232, 240, 255);
    public static readonly Color AccentPressed = Color.FromRgb(214, 226, 250);
    public static readonly Color Window = Color.FromRgb(249, 250, 252);
    public static readonly Color Card = Colors.White;
    public static readonly Color Border = Color.FromRgb(226, 230, 236);
    public static readonly Color Text = Color.FromRgb(28, 32, 40);
    public static readonly Color Muted = Color.FromRgb(108, 116, 130);
    public static readonly Color Sidebar = Color.FromRgb(240, 243, 248);
    public static readonly Color SidebarHover = Color.FromRgb(230, 235, 243);

    public static readonly Color Green = Color.FromRgb(34, 170, 90);
    public static readonly Color Orange = Color.FromRgb(235, 130, 30);
    public static readonly Color Red = Color.FromRgb(214, 60, 60);
    public static readonly Color RedSoft = Color.FromRgb(253, 235, 235);
    public static readonly Color Grey = Color.FromRgb(150, 158, 170);

    public static readonly IBrush AccentBrush = new ImmutableSolidColorBrushWrapper(Accent);
    public static readonly IBrush WindowBrush = new ImmutableSolidColorBrushWrapper(Window);
    public static readonly IBrush CardBrush = new ImmutableSolidColorBrushWrapper(Card);
    public static readonly IBrush BorderBrush = new ImmutableSolidColorBrushWrapper(Border);
    public static readonly IBrush TextBrush = new ImmutableSolidColorBrushWrapper(Text);
    public static readonly IBrush MutedBrush = new ImmutableSolidColorBrushWrapper(Muted);
    public static readonly IBrush SidebarBrush = new ImmutableSolidColorBrushWrapper(Sidebar);

    // Type
    public static readonly FontFamily BodyFont = new("Segoe UI, Inter, sans-serif");
    public static readonly FontFamily MonoFont = new("Consolas, Cascadia Mono, monospace");
    public const double BodySize = 13;
    public const double SmallSize = 11.5;
    public const double TitleSize = 20;
    public const double HeadingSize = 17;

    public const double Gap = 8;
    public const double Pad = 20;

    /// <summary>App-wide control defaults that the theme does not set the way we want.</summary>
    public static void RegisterGlobalStyles(Application app)
    {
        var textBox = new Style(x => x.OfType<TextBox>());
        textBox.Setters.Add(new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(3)));
        textBox.Setters.Add(new Setter(Layoutable.MinHeightProperty, 30.0));
        app.Styles.Add(textBox);

        var button = new Style(x => x.OfType<Button>());
        button.Setters.Add(new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(4)));
        app.Styles.Add(button);
    }

    /// <summary>Applies the shared font and colours to a window.</summary>
    public static void Style(Avalonia.Controls.Window window)
    {
        window.FontFamily = BodyFont;
        window.FontSize = BodySize;
        window.Background = WindowBrush;
        window.Foreground = TextBrush;
        window.Icon = AppIcon();
    }

    public static TextBlock Label(string text, double top = 0) => new()
    {
        Text = text,
        Foreground = TextBrush,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, top, Gap, 0)
    };

    /// <summary>Muted explanatory text under a control.</summary>
    public static TextBlock Hint(string text, double maxWidth = 0)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = SmallSize,
            Foreground = MutedBrush,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 2, 0, Gap)
        };
        if (maxWidth > 0)
            block.MaxWidth = maxWidth;
        return block;
    }

    /// <summary>Section title.</summary>
    public static TextBlock SectionHeading(string text, bool first = false) => new()
    {
        Text = text,
        FontSize = HeadingSize,
        FontWeight = FontWeight.SemiBold,
        Foreground = TextBrush,
        Margin = new Thickness(0, first ? 0 : Pad, 0, Gap)
    };

    public static CheckBox Check(string text) => new()
    {
        Content = text,
        Foreground = TextBrush,
        Margin = new Thickness(0, 3, 0, 3)
    };

    public static TextBox TextBox(double width = 0)
    {
        var box = new TextBox { Margin = new Thickness(0, 3, 0, 3) };
        if (width > 0)
        {
            box.Width = width;
            box.HorizontalAlignment = HorizontalAlignment.Left;
        }
        else
            box.HorizontalAlignment = HorizontalAlignment.Stretch;
        return box;
    }

    public static NumericUpDown Number(int min, int max, double width = 110) => new()
    {
        Minimum = min,
        Maximum = max,
        Increment = 1,
        FormatString = "0",
        Width = width,
        HorizontalAlignment = HorizontalAlignment.Left,
        Margin = new Thickness(0, 3, 0, 3)
    };

    public static ComboBox Combo(double width = 160) => new()
    {
        Width = width,
        HorizontalAlignment = HorizontalAlignment.Left,
        Margin = new Thickness(0, 3, 0, 3)
    };

    /// <summary>A flat secondary button.</summary>
    public static Button Button(string text, double width = 0)
    {
        var b = new Button
        {
            Content = text,
            Background = CardBrush,
            Foreground = TextBrush,
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(1),
            Height = 32,
            Padding = new Thickness(12, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 3, Gap, 3),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        if (width > 0)
            b.Width = width;
        SetButtonStates(b, Card, AccentSoft, AccentPressed, Text, Text, Border);
        return b;
    }

    /// <summary>The one filled call-to-action button in a window.</summary>
    public static Button PrimaryButton(string text, double width = 0)
    {
        var b = Button(text, width);
        b.Background = AccentBrush;
        b.Foreground = Brushes.White;
        b.FontWeight = FontWeight.SemiBold;
        b.BorderThickness = new Thickness(0);
        SetButtonStates(b, Accent, AccentDark, AccentDark, Colors.White, Colors.White, Accent);
        return b;
    }

    /// <summary>A "danger" text button for destructive actions such as Remove.</summary>
    public static Button DangerButton(string text, double width = 0)
    {
        var b = Button(text, width);
        b.Foreground = new SolidColorBrush(Red);
        SetButtonStates(b, Card, RedSoft, RedSoft, Red, Red, Border);
        return b;
    }

    /// <summary>
    /// The Fluent button template reads its hover/pressed colours from these resources; setting them on
    /// the button itself overrides the theme for just that button.
    /// </summary>
    private static void SetButtonStates(Button b, Color normal, Color hover, Color pressed, Color foreground, Color foregroundHover, Color border)
    {
        b.Resources["ButtonBackground"] = new SolidColorBrush(normal);
        b.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(hover);
        b.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(pressed);
        b.Resources["ButtonForeground"] = new SolidColorBrush(foreground);
        b.Resources["ButtonForegroundPointerOver"] = new SolidColorBrush(foregroundHover);
        b.Resources["ButtonForegroundPressed"] = new SolidColorBrush(foregroundHover);
        b.Resources["ButtonBorderBrush"] = new SolidColorBrush(border);
        b.Resources["ButtonBorderBrushPointerOver"] = new SolidColorBrush(border);
        b.Resources["ButtonBorderBrushPressed"] = new SolidColorBrush(border);
    }

    /// <summary>Two-column label/control form layout with a fixed label gutter.</summary>
    public static Grid FormGrid(double labelWidth = 140)
    {
        var grid = new Grid { Margin = new Thickness(0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition(labelWidth, GridUnitType.Pixel));
        grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        return grid;
    }

    /// <summary>Adds a labelled row to a <see cref="FormGrid"/> and returns the row index.</summary>
    public static int Row(Grid grid, string label, Control control)
    {
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        if (!string.IsNullOrEmpty(label))
        {
            var text = Label(label);
            text.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(text, row);
            Grid.SetColumn(text, 0);
            grid.Children.Add(text);
        }
        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return row;
    }

    /// <summary>A vertical stack of controls.</summary>
    public static StackPanel Stack() => new()
    {
        Orientation = Orientation.Vertical,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    /// <summary>A horizontal row of controls.</summary>
    public static StackPanel Inline() => new()
    {
        Orientation = Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Left
    };

    /// <summary>A 1px horizontal rule.</summary>
    public static Control Rule() => new Border { Height = 1, Background = BorderBrush };

    /// <summary>Bottom action bar with buttons right-aligned. The first button is the default action.</summary>
    public static Control ActionBar(params Button[] buttons)
    {
        var flow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = Gap
        };
        foreach (var b in buttons)
        {
            b.Margin = new Thickness(0);
            flow.Children.Add(b);
        }

        var bar = new Border
        {
            Height = 60,
            Background = CardBrush,
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(Pad, 0),
            Child = flow
        };
        DockPanel.SetDock(bar, Dock.Bottom);
        return bar;
    }

    /// <summary>Small filled circle used as a status/legend swatch.</summary>
    public static Ellipse Dot(Color color, double size = 10) => new()
    {
        Width = size,
        Height = size,
        Fill = new SolidColorBrush(color),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 6, 0)
    };

    private static WindowIcon? s_appIcon;

    /// <summary>The app icon for window title bars.</summary>
    public static WindowIcon AppIcon()
    {
        if (s_appIcon == null)
        {
            using var stream = OpenAsset("app.ico");
            s_appIcon = new WindowIcon(stream);
        }
        return s_appIcon;
    }

    /// <summary>Loads the app logo bitmap (for in-window use).</summary>
    public static Bitmap AppImage()
    {
        using var stream = OpenAsset("logo.png");
        return new Bitmap(stream);
    }

    public static Stream OpenAsset(string name) =>
        typeof(Ui).Assembly.GetManifestResourceStream($"RoboMouse.App.Assets.{name}")
        ?? throw new InvalidOperationException($"Missing embedded asset '{name}'.");

    /// <summary>A brush that can be shared between windows without being frozen by the first one.</summary>
    private sealed class ImmutableSolidColorBrushWrapper : Avalonia.Media.Immutable.ImmutableSolidColorBrush
    {
        public ImmutableSolidColorBrushWrapper(Color color) : base(color) { }
    }
}
