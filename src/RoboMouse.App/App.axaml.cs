using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using RoboMouse.Core.Configuration;
using RoboMouse.Core.Logging;

namespace RoboMouse.App;

/// <summary>
/// The Avalonia application. There is no main window: the app lives in the tray and opens windows on
/// demand, so the lifetime only ends when the user chooses Exit.
/// </summary>
public partial class App : Application
{
    private TrayController? _tray;
    private AppSettings? _settings;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            UnhandledErrors.Install();

            _settings = AppSettings.Load();
            StartupRegistration.SyncOnLaunch(_settings.StartWithWindows);
            try
            {
                _tray = new TrayController(_settings, desktop);
            }
            catch (Exception ex)
            {
                SimpleLogger.Log("Fatal", $"Startup failed: {ex}");
                throw;
            }

            desktop.Exit += (_, _) =>
            {
                _tray?.Dispose();
                _settings?.Save();
            };

            if (ShouldShowSettingsAtLaunch(_settings))
                Avalonia.Threading.Dispatcher.UIThread.Post(() => _tray?.ShowSettings());
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Settings opens at launch when there is nothing to connect to yet (first run, or every peer
    /// removed), since a tray icon alone gives no hint what to do next; otherwise only when the user
    /// turned "Start minimized" off.
    /// </summary>
    internal static bool ShouldShowSettingsAtLaunch(AppSettings settings) =>
        settings.Peers.Count == 0 || !settings.StartMinimized;

}
