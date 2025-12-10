using RoboMouse.Core.Configuration;
using RoboMouse.Core.Logging;

namespace RoboMouse.App;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        // Ensure single instance
        using var mutex = new Mutex(true, "RoboMouse_SingleInstance", out var isNew);
        if (!isNew)
        {
            MessageBox.Show("RoboMouse is already running.", "RoboMouse",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Clear log file on startup
        SimpleLogger.ClearLog();

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        // Load settings
        var settings = AppSettings.Load();

        // Run the application
        var context = new TrayApplicationContext(settings);
        Application.Run(context);

        // Save settings on exit
        settings.Save();
    }
}
