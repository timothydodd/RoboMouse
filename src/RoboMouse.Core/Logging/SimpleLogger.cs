namespace RoboMouse.Core.Logging;

/// <summary>
/// Simple file logger for debugging connection issues.
/// </summary>
public static class SimpleLogger
{
    private static readonly string LogPath;
    private static readonly object Lock = new();

    static SimpleLogger()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var logDir = Path.Combine(appData, "RoboMouse");
        Directory.CreateDirectory(logDir);
        LogPath = Path.Combine(logDir, "debug.log");
    }

    /// <summary>
    /// Deletes the log file and starts fresh. Call this at app startup.
    /// </summary>
    public static void ClearLog()
    {
        lock (Lock)
        {
            try
            {
                if (File.Exists(LogPath))
                    File.Delete(LogPath);
                File.WriteAllText(LogPath, $"=== RoboMouse Log Started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n");
            }
            catch { }
        }
    }

    public static void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        System.Diagnostics.Debug.WriteLine(line);

        lock (Lock)
        {
            try { File.AppendAllText(LogPath, line + "\n"); }
            catch { }
        }
    }

    public static void Log(string category, string message) => Log($"[{category}] {message}");
}
