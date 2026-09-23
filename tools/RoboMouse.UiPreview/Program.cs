using RoboMouse.Core.Screen;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using RoboMouse.App.Services;
using RoboMouse.App.ViewModels;
using RoboMouse.App.Views;

namespace RoboMouse.UiPreview;

/// <summary>
/// Renders the app's windows headlessly and saves PNGs: <c>RoboMouse.UiPreview [outDir]</c>.
/// <c>--store &lt;outDir&gt;</c> writes the 1366x768 Store listing screenshots instead.
/// Pass resource keys after the directory to print whether the theme defines them instead.
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var store = args.Length > 0 && args[0] == "--store";
        var hero = args.Length > 0 && args[0] == "--hero";
        if (store || hero)
            args = args.Skip(1).ToArray();
        var outDir = args.Length > 0 ? args[0] : "ui-preview";
        Directory.CreateDirectory(outDir);

        AppBuilder.Configure<RoboMouse.App.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .WithInterFont()
            .SetupWithoutStarting();

        var app = Application.Current!;
        if (store)
        {
            StoreScreenshots.Render(outDir);
            Console.WriteLine($"Store screenshots written to {Path.GetFullPath(outDir)}");
            return 0;
        }
        if (hero)
        {
            StoreHero.Render(outDir);
            Console.WriteLine($"Hero art written to {Path.GetFullPath(outDir)}");
            return 0;
        }
        if (args.Length > 1)
        {
            foreach (var key in args.Skip(1))
            {
                var found = app.TryGetResource(key, ThemeVariant.Light, out var value);
                Console.WriteLine($"{key}: {(found ? value?.GetType().Name + " " + value : "MISSING")}");
            }
            return 0;
        }

        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            app.RequestedThemeVariant = variant;
            var suffix = variant == ThemeVariant.Dark ? "-dark" : "";
            var backend = new FakeBackend();
            var settings = FakeBackend.SampleSettings();

            var window = new SettingsWindow(settings, backend);
            window.Show();
            foreach (var page in window.ViewModel.Pages)
            {
                window.ViewModel.SelectedPage = page;
                Capture(window, Path.Combine(outDir, $"settings-{page.Title.ToLowerInvariant()}{suffix}.png"));
            }
            // The layout page again with a second, smaller monitor that has been made the main display.
            ScreenLayoutControl.LayoutSource = () => new MonitorLayout(new[]
            {
                Monitor(0, 0, 1920, 1080, primary: true),
                Monitor(-2560, -200, 2560, 1440, primary: false)
            });
            window.ViewModel.SelectedPage = window.ViewModel.Pages.First(p => p.Title == "Layout");
            window.ViewModel.Layout.Reload();
            Capture(window, Path.Combine(outDir, $"settings-layout-multimonitor{suffix}.png"));
            ScreenLayoutControl.LayoutSource = null;

            // Error states: a cleared port box, and startup turned off from Task Manager.
            window.ViewModel.Network.LocalPort = null;
            window.ViewModel.SelectedPage = window.ViewModel.Pages.First(p => p.Title == "Network");
            Capture(window, Path.Combine(outDir, $"settings-network-invalid{suffix}.png"));
            window.ViewModel.General.ShowStartupState(RoboMouse.App.StartupState.DisabledByUser);
            window.ViewModel.SelectedPage = window.ViewModel.Pages.First(p => p.Title == "General");
            Capture(window, Path.Combine(outDir, $"settings-general-startup-off{suffix}.png"));
            window.Close();

            var setup = new PeerSetupWindow(new PeerSetupViewModel(settings.Peers[0], settings, backend, new WindowDialogService(null, backend)));
            setup.Show();
            Capture(setup, Path.Combine(outDir, $"peer-setup{suffix}.png"));
            setup.Close();

            // A machine picked from discovery is a new peer: "Add", on the first free edge.
            var found = backend.DiscoveredPeers[0];
            var draft = PeerActions.FromDiscovered(found, PeerActions.FirstFreeEdge(settings) ?? RoboMouse.Core.Configuration.ScreenPosition.Right);
            var add = new PeerSetupWindow(new PeerSetupViewModel(draft, settings, backend, new WindowDialogService(null, backend)));
            add.Show();
            Capture(add, Path.Combine(outDir, $"peer-setup-add{suffix}.png"));
            add.Close();


            var message = new MessageDialog("Laptop is already left of this screen.\n\nSwap them so Laptop moves right?", "Edge already in use", DialogButtons.YesNoCancel, DialogIcon.Question);
            message.Show();
            Capture(message, Path.Combine(outDir, $"message{suffix}.png"));
            message.Close();

            var debugVm = new DebugPanelViewModel();
            for (var i = 0; i < 8; i++)
                debugVm.Record(new MouseDebugData { IsControlling = true, PeerName = "Laptop", PeerPosition = "Left", DeltaX = 12 - i, DeltaY = -4 + i, RoundTripMs = 3 });
            var debug = new DebugPanelWindow(debugVm);
            debug.Show();
            debugVm.Flush();
            Capture(debug, Path.Combine(outDir, $"debug{suffix}.png"));
            debug.Close();
        }

        Console.WriteLine($"Screenshots written to {Path.GetFullPath(outDir)}");
        return 0;
    }

    private static MonitorRect Monitor(int x, int y, int w, int h, bool primary)
    {
        var r = new System.Drawing.Rectangle(x, y, w, h);
        return new MonitorRect(r, r, primary);
    }

    private static void Capture(Window window, string path)
    {
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame rendered.");
        frame.Save(path, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        Console.WriteLine($"  {Path.GetFileName(path)}");
    }
}
