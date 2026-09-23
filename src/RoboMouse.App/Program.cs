using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using RoboMouse.Core.Input;
using RoboMouse.Core.Logging;

namespace RoboMouse.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Recovery switch: if a previous instance was killed while controlling a remote, the system
        // cursors may still be blank. "RoboMouse.exe --restore-cursor" puts them back and exits.
        if (args.Any(a => string.Equals(a, "--restore-cursor", StringComparison.OrdinalIgnoreCase)))
        {
            InputSimulator.RestoreSystemCursor();
            return 0;
        }

        // One instance per session: a second launch just asks the running copy to show Settings.
        using var instance = new SingleInstance();
        if (instance.OtherCopyIsElevated)
        {
            SingleInstance.ShowElevatedCopyMessage();
            return 0;
        }
        if (!instance.IsFirstInstance)
        {
            instance.SignalExistingInstance();
            return 0;
        }
        Instance = instance;

        // Clear log file on startup
        SimpleLogger.ClearLog();

        // The system cursor is hidden while controlling a remote by swapping the system cursors.
        // Make sure they come back even if we crash, otherwise the user is left with no pointer.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => InputSimulator.RestoreSystemCursor();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            InputSimulator.RestoreSystemCursor();
            SimpleLogger.Log("Fatal", e.ExceptionObject?.ToString() ?? "Unknown unhandled exception");
        };

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    /// <summary>The single-instance guard of the running copy; the tray controller listens on it.</summary>
    public static SingleInstance? Instance { get; private set; }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect();
}
