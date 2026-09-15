using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using RoboMouse.Core.Configuration;
using RoboMouse.Core.Logging;

namespace RoboMouse.App;

/// <summary>
/// The Avalonia application. There is no main window: the app lives in the tray and opens windows on
/// demand, so the lifetime only ends when the user chooses Exit.
/// </summary>
public sealed class App : Application
{
    private TrayController? _tray;
    private AppSettings? _settings;

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Light;
        Ui.RegisterGlobalStyles(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _settings = AppSettings.Load();
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
        }

        base.OnFrameworkInitializationCompleted();
    }
}
