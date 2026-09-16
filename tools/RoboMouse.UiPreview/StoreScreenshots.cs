using Avalonia;
using Avalonia.Controls;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using RoboMouse.App.ViewModels;
using RoboMouse.App.Views;

namespace RoboMouse.UiPreview;

/// <summary>
/// Store listing screenshots: 1920x1080 marketing frames, each with a gradient backdrop, a headline
/// and the real window floating below it with a shadow. The windows are rendered by the app itself;
/// only the frame around them is composed here.
/// </summary>
internal static class StoreScreenshots
{
    private const int Width = 1920;
    private const int Height = 1080;

    // Settings window size inside the frame (rendered 1:1, never scaled, so text stays crisp)
    private const int WindowWidth = 1440;
    private const int WindowHeight = 760;

    private sealed record Slide(string File, string Title, string Subtitle, Color From, Color To, bool Dark = false);

    private static readonly Slide[] Slides =
    {
        new("01-general", "One mouse. Every PC.", "Move to the edge of the screen and keep going. Keyboard included.", Color.Parse("#1E64E6"), Color.Parse("#6A3FE0")),
        new("02-network", "Private by design", "A pairing code you choose. Encrypted end to end. No account, no cloud.", Color.Parse("#0F766E"), Color.Parse("#1D4ED8")),
        new("03-peers", "Finds your other PCs for you", "Machines running RoboMouse on your network show up automatically.", Color.Parse("#7C3AED"), Color.Parse("#DB2777")),
        new("04-layout", "Arrange screens by dragging", "Put each PC where it sits on your desk. Offsets are kept too.", Color.Parse("#0EA5E9"), Color.Parse("#2563EB")),
        new("05-peer-setup", "Set up in seconds", "Name it, pick a side, test the link.", Color.Parse("#F59E0B"), Color.Parse("#DC2626")),
        new("06-layout-dark", "Light or dark, your choice", "Follows your Windows theme, including the accent colour.", Color.Parse("#1F2937"), Color.Parse("#111827"), Dark: true)
    };

    public static void Render(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var app = Application.Current!;
        var backend = new FakeBackend();
        var settings = FakeBackend.SampleSettings();

        foreach (var slide in Slides)
        {
            app.RequestedThemeVariant = slide.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
            using var frame = CaptureWindow(slide, settings, backend);
            Compose(frame, slide, Path.Combine(outDir, slide.File + ".png"));
        }
        app.RequestedThemeVariant = ThemeVariant.Light;
    }

    /// <summary>Renders the window a slide shows and returns its frame.</summary>
    private static Bitmap CaptureWindow(Slide slide, Core.Configuration.AppSettings settings, FakeBackend backend)
    {
        Window window;
        if (slide.File == "05-peer-setup")
        {
            window = new PeerSetupWindow(new PeerSetupViewModel(settings.Peers[0], settings, backend, new WindowDialogService(null, backend)));
            window.Show();
        }
        else
        {
            var page = slide.File switch
            {
                "02-network" => 1,
                "03-peers" => 2,
                "04-layout" or "06-layout-dark" => 3,
                _ => 0
            };
            var settingsWindow = new SettingsWindow(settings, backend) { Width = WindowWidth, Height = WindowHeight };
            settingsWindow.Show();
            settingsWindow.ViewModel.SelectedPage = settingsWindow.ViewModel.Pages[page];
            window = settingsWindow;
        }

        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame rendered.");
        window.Close();
        return frame;
    }

    /// <summary>Lays out headline, subtitle and the floating window on a gradient and renders the whole frame.</summary>
    private static void Compose(Bitmap frame, Slide slide, string path)
    {
        var titleBrush = Brushes.White;
        var subtitleBrush = new SolidColorBrush(Colors.White, 0.85);

        var header = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 64, 0, 0),
            Spacing = 10
        };
        header.Children.Add(new TextBlock
        {
            Text = slide.Title,
            FontFamily = new FontFamily("fonts:Inter#Inter"),
            FontSize = 56,
            FontWeight = FontWeight.Bold,
            Foreground = titleBrush,
            TextAlignment = TextAlignment.Center
        });
        header.Children.Add(new TextBlock
        {
            Text = slide.Subtitle,
            FontFamily = new FontFamily("fonts:Inter#Inter"),
            FontSize = 24,
            Foreground = subtitleBrush,
            TextAlignment = TextAlignment.Center
        });

        // The window: 1:1 pixels, rounded corners, soft shadow, hovering below the headline.
        // Two layers: the outer one carries the shadow (a clipped element would cut its own shadow
        // off), the inner one clips the screenshot to the rounded corners.
        var windowCard = new Border
        {
            Width = frame.PixelSize.Width,
            Height = frame.PixelSize.Height,
            CornerRadius = new CornerRadius(10),
            BoxShadow = BoxShadows.Parse("0 40 90 0 #80000000, 0 12 28 0 #40000000"),
            Background = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = new Border
            {
                CornerRadius = new CornerRadius(10),
                ClipToBounds = true,
                Child = new Image { Source = frame, Stretch = Stretch.None }
            }
        };
        // Centre the window in the space below the headline (which ends around y=210).
        const int headerHeight = 210;
        windowCard.Margin = new Thickness(0, 0, 0, Math.Max(48, (Height - headerHeight - frame.PixelSize.Height) / 2.0 + 10));

        var root = new Panel
        {
            Width = Width,
            Height = Height,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(slide.From, 0), new GradientStop(slide.To, 1) }
            }
        };
        // Subtle glow behind the window so the gradient does not look flat
        root.Children.Add(new Ellipse
        {
            Width = 1400,
            Height = 700,
            Fill = new RadialGradientBrush
            {
                GradientStops = { new GradientStop(Color.FromArgb(0x40, 255, 255, 255), 0), new GradientStop(Color.FromArgb(0, 255, 255, 255), 1) }
            },
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, -200)
        });
        root.Children.Add(windowCard);
        root.Children.Add(header);

        var canvas = new Window
        {
            Width = Width,
            Height = Height,
            WindowDecorations = WindowDecorations.None,
            Content = root
        };
        canvas.Show();
        Dispatcher.UIThread.RunJobs();
        using var rendered = canvas.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame rendered.");
        rendered.Save(path, PngBitmapEncoderOptions.Default);
        canvas.Close();
        Console.WriteLine($"  {Path.GetFileName(path)}");
    }
}
