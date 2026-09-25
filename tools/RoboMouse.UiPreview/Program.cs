using RoboMouse.Core.Screen;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RoboMouse.App.Services;
using RoboMouse.App.ViewModels;
using RoboMouse.App.Views;
using RoboMouse.Core;
using RoboMouse.Core.Configuration;

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

            var window = new SettingsWindow(settings, backend, new AppState(), new UpdateChecker());
            window.Show();
            foreach (var page in window.ViewModel.Pages)
            {
                window.ViewModel.SelectedPage = page;
                Capture(window, Path.Combine(outDir, $"settings-{page.Title.ToLowerInvariant()}{suffix}.png"));
            }
            // The layout page again with a second, smaller monitor that has been made the main display.
            backend.LocalLayoutSource = () => new MonitorLayout(new[]
            {
                Monitor(0, 0, 2560, 1440, primary: true),
                Monitor(0, -1080, 1920, 1080, primary: false)
            });
            window.ViewModel.SelectedPage = window.ViewModel.Pages.First(p => p.Title == "Layout");
            window.ViewModel.Layout.Reload();
            Capture(window, Path.Combine(outDir, $"settings-layout-multimonitor{suffix}.png"));

            // Error states: a cleared port box, and startup turned off from Task Manager.
            window.ViewModel.Network.LocalPort = null;
            window.ViewModel.SelectedPage = window.ViewModel.Pages.First(p => p.Title == "Network");
            Capture(window, Path.Combine(outDir, $"settings-network-invalid{suffix}.png"));
            // The General and Advanced pages scrolled down.
            window.ViewModel.ShowPage(SettingsPage.General);
            Dispatcher.UIThread.RunJobs();
            var generalScroll = window.GetVisualDescendants().OfType<GeneralPageView>().Single()
                .GetVisualDescendants().OfType<ScrollViewer>().First();
            generalScroll.Offset = new Vector(0, 600);
            Capture(window, Path.Combine(outDir, $"settings-general-bottom{suffix}.png"));
            generalScroll.Offset = default;
            window.ViewModel.ShowPage(SettingsPage.Advanced);
            Dispatcher.UIThread.RunJobs();
            var advancedScroll = window.GetVisualDescendants().OfType<AdvancedPageView>().Single()
                .GetVisualDescendants().OfType<ScrollViewer>().First();
            advancedScroll.Offset = new Vector(0, 700);
            Capture(window, Path.Combine(outDir, $"settings-advanced-middle{suffix}.png"));
            advancedScroll.Offset = new Vector(0, 1400);
            Capture(window, Path.Combine(outDir, $"settings-advanced-bottom{suffix}.png"));
            advancedScroll.Offset = default;

            window.ViewModel.General.ShowStartupState(RoboMouse.App.StartupState.DisabledByUser);
            window.ViewModel.SelectedPage = window.ViewModel.Pages.First(p => p.Title == "General");
            Capture(window, Path.Combine(outDir, $"settings-general-startup-off{suffix}.png"));
            window.Close();

            // Problems: a hand-typed code, a port another program holds, a machine asking to connect,
            // and a peer whose code does not match.
            var troubled = FakeBackend.SampleSettings();
            troubled.PairingCode = "letmein";
            troubled.Peers[1].Enabled = true;
            troubled.Peers.Add(new RoboMouse.Core.Configuration.PeerConfig { Id = "nas", Name = "NAS-BOX", Address = "192.168.1.50", Position = RoboMouse.Core.Configuration.ScreenPosition.Top, IdentityKey = "cGlubmVkLWtleQ==" });
            var troubledBackend = new FakeBackend
            {
                ListenerError = new NetworkStartError(NetworkErrorKind.ListenPort, 24800, true, "Port 24800 is in use by another program. Change it in Settings > Network.")
            };
            troubledBackend.Pending.Add(Pending);
            var problems = new SettingsWindow(troubled, troubledBackend);
            problems.Show();
            problems.ViewModel.ShowPage(SettingsPage.Network);
            problems.ViewModel.Network.BeginEnterCodeCommand.Execute(null);
            problems.ViewModel.Network.EnteredCode = "hunter2";
            problems.ViewModel.Network.UseEnteredCodeCommand.Execute(null);
            Capture(problems, Path.Combine(outDir, $"settings-network-problems{suffix}.png"));
            problems.ViewModel.ShowPage(SettingsPage.Peers);
            Capture(problems, Path.Combine(outDir, $"settings-peers-problems{suffix}.png"));
            problems.Close();

            // First-run pairing wizard, one capture per step.
            var fresh = new AppSettings { MachineName = "DESKTOP-TIM", PairingCode = "K7PQ-M2XW-9DHR" };
            var wizardBackend = new FakeBackend();
            wizardBackend.Pending.Add(Pending);
            var wizard = new PairingWizardWindow(new PairingWizardViewModel(fresh, wizardBackend, new WindowDialogService(null, wizardBackend)));
            wizard.Show();
            for (var step = 0; step < PairingWizardViewModel.StepCount; step++)
            {
                wizard.ViewModel.Step = step;
                if (step == 1)
                    wizard.ViewModel.SelectedMachine = wizard.ViewModel.Machines.First();
                Capture(wizard, Path.Combine(outDir, $"wizard-{step + 1}{suffix}.png"));
            }
            wizard.Close();

            // Toasts.
            var toasts = new[]
            {
                ("toast-request", Notifications.PendingPeer(Pending, () => { }, () => { })),
                ("toast-port", Notifications.NetworkError(troubledBackend.ListenerError!, () => { })),
                ("toast-update", Notifications.UpdateAvailable(new UpdateInfo(new Version(1, 2, 0), UpdateChecker.ReleasesPage), new Version(1, 1, 4), () => { })),
                ("toast-wake", Notifications.WakeSent(settings.Peers[0]))
            };
            foreach (var (name, notification) in toasts)
            {
                var toast = new ToastWindow(new ToastViewModel(notification));
                toast.Show();
                Capture(toast, Path.Combine(outDir, $"{name}{suffix}.png"));
                toast.Close();
            }

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

    private static readonly PendingPeer Pending = new("studio", "STUDIO-PC", "192.168.1.42", 24800, 3840, 2160, "", new DateTime(2026, 9, 23, 9, 41, 0));

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
