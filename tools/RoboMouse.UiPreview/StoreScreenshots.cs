using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using RoboMouse.App.Services;
using RoboMouse.App.ViewModels;
using RoboMouse.App.Views;

namespace RoboMouse.UiPreview;

/// <summary>
/// Store listing screenshots: 1366x768 PNGs (the Store's minimum for desktop), rendered from the real
/// windows. Smaller windows are composited onto a neutral backdrop so every image meets the size.
/// </summary>
internal static class StoreScreenshots
{
    private const int Width = 1366;
    private const int Height = 768;

    public static void Render(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var app = Application.Current!;
        app.RequestedThemeVariant = ThemeVariant.Light;

        var backend = new FakeBackend();
        var settings = FakeBackend.SampleSettings();

        var window = new SettingsWindow(settings, backend) { Width = Width, Height = Height };
        window.Show();
        var index = 1;
        foreach (var page in window.ViewModel.Pages)
        {
            window.ViewModel.SelectedPage = page;
            Dispatcher.UIThread.RunJobs();
            using var frame = window.CaptureRenderedFrame()!;
            Compose(frame, Path.Combine(outDir, $"{index++:00}-{page.Title.ToLowerInvariant()}.png"));
        }
        window.Close();

        var setup = new PeerSetupWindow(new PeerSetupViewModel(settings.Peers[0], settings, backend, new WindowDialogService(null, backend)));
        setup.Show();
        Dispatcher.UIThread.RunJobs();
        using (var frame = setup.CaptureRenderedFrame()!)
            Compose(frame, Path.Combine(outDir, $"{index++:00}-peer-setup.png"));
        setup.Close();

        app.RequestedThemeVariant = ThemeVariant.Dark;
        var dark = new SettingsWindow(settings, backend) { Width = Width, Height = Height };
        dark.Show();
        dark.ViewModel.SelectedPage = dark.ViewModel.Pages[3];
        Dispatcher.UIThread.RunJobs();
        using (var frame = dark.CaptureRenderedFrame()!)
            Compose(frame, Path.Combine(outDir, $"{index++:00}-layout-dark.png"));
        dark.Close();
        app.RequestedThemeVariant = ThemeVariant.Light;
    }

    /// <summary>Writes the frame as-is when it already fills the canvas, otherwise centres it on a backdrop.</summary>
    private static void Compose(Bitmap frame, string path)
    {
        if (frame.PixelSize.Width >= Width && frame.PixelSize.Height >= Height)
        {
            frame.Save(path, PngBitmapEncoderOptions.Default);
            Console.WriteLine($"  {Path.GetFileName(path)}");
            return;
        }

        using var target = new RenderTargetBitmap(new PixelSize(Width, Height), new Vector(96, 96));
        using (var context = target.CreateDrawingContext())
        {
            context.FillRectangle(new SolidColorBrush(Color.FromRgb(0xE9, 0xEE, 0xF5)), new Rect(0, 0, Width, Height));
            var x = (Width - frame.PixelSize.Width) / 2.0;
            var y = (Height - frame.PixelSize.Height) / 2.0;
            var rect = new Rect(x, y, frame.PixelSize.Width, frame.PixelSize.Height);
            // Soft shadow so the window reads as a window
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x22, 0, 0, 0)), null, rect.Translate(new Vector(0, 6)).Inflate(6), 12, 12);
            context.DrawImage(frame, rect);
        }
        target.Save(path, PngBitmapEncoderOptions.Default);
        Console.WriteLine($"  {Path.GetFileName(path)}");
    }
}
