using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using RoboMouse.Core.Input;
using RoboMouse.Core.Logging;

namespace RoboMouse.App;

internal static partial class Program
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

        // Ensure single instance
        using var mutex = new Mutex(true, "RoboMouse_SingleInstance", out var isNew);
        if (!isNew)
        {
            MessageBoxW(0, "RoboMouse is already running.", "RoboMouse", MB_OK | MB_ICONINFORMATION);
            return 0;
        }

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

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect();

    private const uint MB_OK = 0x0;
    private const uint MB_ICONINFORMATION = 0x40;

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(nint hWnd, string text, string caption, uint type);
}
