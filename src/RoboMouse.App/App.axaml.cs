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
            var appState = Services.AppState.Load();
            StartupRegistration.SyncOnLaunch(_settings.StartWithWindows);
            try
            {
                _tray = new TrayController(_settings, desktop, appState);
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

            switch (WindowAtLaunch(_settings))
            {
                case LaunchWindow.PairingWizard:
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => _tray?.ShowPairingWizard());
                    break;
                case LaunchWindow.Settings:
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => _tray?.ShowSettings());
                    break;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>What opens at launch.</summary>
    internal enum LaunchWindow { None, Settings, PairingWizard }

    /// <summary>
    /// With nothing to connect to yet (first run, or every peer removed) the pairing wizard opens,
    /// since a tray icon alone gives no hint what to do next; otherwise Settings opens only when the
    /// user turned "Start minimized" off.
    /// </summary>
    internal static LaunchWindow WindowAtLaunch(AppSettings settings) =>
        settings.Peers.Count == 0 ? LaunchWindow.PairingWizard
        : !settings.StartMinimized ? LaunchWindow.Settings
        : LaunchWindow.None;

}
