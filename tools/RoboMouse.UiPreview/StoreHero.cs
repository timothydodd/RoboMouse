using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using IOPath = System.IO.Path;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using RoboMouse.App.Services;
using RoboMouse.App.Views;

namespace RoboMouse.UiPreview;

/// <summary>
/// Microsoft Store "Super hero" art: a 16:9 promo banner with a gradient backdrop, the wordmark and
/// tagline on the left and the real settings window floating on the right. Rendered natively at each
/// requested size (layout scales from a 1920x1080 design), so text stays crisp at 4K.
/// </summary>
internal static class StoreHero
{
    private static readonly Color From = Color.Parse("#1E64E6");
    private static readonly Color To = Color.Parse("#6A3FE0");
    private static readonly FontFamily Inter = new("fonts:Inter#Inter");

    public static void Render(string outDir)
    {
        Directory.CreateDirectory(outDir);
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;

        // Capture the app window once at high resolution; it is drawn scaled into each hero size.
        using var window = CaptureAppWindow();

        RenderSize(outDir, window, 1920, 1080);
        RenderSize(outDir, window, 3840, 2160);
    }

    private static Bitmap CaptureAppWindow()
    {
        var backend = new FakeBackend();
        var settings = FakeBackend.SampleSettings();
        var window = new SettingsWindow(settings, backend) { Width = 1000, Height = 720 };
        window.Show();
        window.ViewModel.SelectedPage = window.ViewModel.Pages[0];
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame rendered.");
        window.Close();
        return frame;
    }

    private static void RenderSize(string outDir, Bitmap appWindow, int width, int height)
    {
        var s = width / 1920.0; // design units are 1920x1080

        var root = new Panel
        {
            Width = width,
            Height = height,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(From, 0), new GradientStop(To, 1) }
            }
        };

        // Glow behind the product shot.
        root.Children.Add(new Ellipse
        {
            Width = 1500 * s,
            Height = 1500 * s,
            Fill = new RadialGradientBrush
            {
                GradientStops = { new GradientStop(Color.FromArgb(0x40, 255, 255, 255), 0), new GradientStop(Color.FromArgb(0, 255, 255, 255), 1) }
            },
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, -300 * s, 0)
        });

        // The app window, floating on the right and bleeding slightly off the edge.
        var shotW = 1040 * s;
        var shotH = shotW * appWindow.PixelSize.Height / appWindow.PixelSize.Width;
        root.Children.Add(new Border
        {
            Width = shotW,
            Height = shotH,
            CornerRadius = new CornerRadius(14 * s),
            BoxShadow = BoxShadows.Parse($"0 {40 * s} {90 * s} 0 #66000000, 0 {12 * s} {28 * s} 0 #40000000"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, -110 * s, 0),
            Child = new Border
            {
                CornerRadius = new CornerRadius(14 * s),
                ClipToBounds = true,
                Child = new Image { Source = appWindow, Stretch = Stretch.UniformToFill }
            }
        });

        // Left column: logo, wordmark, tagline, footnote.
        var left = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(120 * s, 0, 0, 0),
            MaxWidth = 820 * s,
            Spacing = 0
        };
        left.Children.Add(new Image
        {
            Source = new Bitmap(AssetLoader.Open(new Uri("avares://RoboMouse.App/Assets/logo.png"))),
            Width = 104 * s,
            Height = 104 * s,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 28 * s)
        });
        left.Children.Add(Text("RoboMouse", 108 * s, FontWeight.Bold, Brushes.White));
        left.Children.Add(Text("One mouse and keyboard\nacross all your PCs.", 46 * s, FontWeight.SemiBold,
            new SolidColorBrush(Colors.White, 0.92), lineHeight: 58 * s, top: 18 * s));
        left.Children.Add(Text("Shared clipboard and files. Encrypted over your own network.\nFree and open source.",
            28 * s, FontWeight.Normal, new SolidColorBrush(Colors.White, 0.8), lineHeight: 40 * s, top: 26 * s));
        root.Children.Add(left);

        var canvas = new Window
        {
            Width = width,
            Height = height,
            WindowDecorations = WindowDecorations.None,
            Content = root
        };
        canvas.Show();
        Dispatcher.UIThread.RunJobs();
        using var rendered = canvas.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame rendered.");
        var path = IOPath.Combine(outDir, $"store-hero-{width}x{height}.png");
        rendered.Save(path, PngBitmapEncoderOptions.Default);
        canvas.Close();
        Console.WriteLine($"  {IOPath.GetFileName(path)}");
    }

    private static TextBlock Text(string text, double size, FontWeight weight, IBrush brush, double lineHeight = 0, double top = 0) => new()
    {
        Text = text,
        FontFamily = Inter,
        FontSize = size,
        FontWeight = weight,
        Foreground = brush,
        LineHeight = lineHeight > 0 ? lineHeight : double.NaN,
        Margin = new Thickness(0, top, 0, 0)
    };
}
