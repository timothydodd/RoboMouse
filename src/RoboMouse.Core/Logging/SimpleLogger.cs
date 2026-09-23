namespace RoboMouse.Core.Logging;

/// <summary>
/// Simple file logger for debugging connection issues. Each run of the app starts a new file and the
/// previous ones are kept (<c>debug.1.log</c>, <c>debug.2.log</c>), so the log of a run that went
/// wrong survives the restart that follows it. A run that logs more than <see cref="MaxFileBytes"/>
/// rolls over the same way.
/// </summary>
public static class SimpleLogger
{
    /// <summary>Files kept: the current one plus the previous runs.</summary>
    public const int KeptFiles = 3;

    /// <summary>Size at which the current file is rolled over.</summary>
    public const long MaxFileBytes = 5 * 1024 * 1024;

    private static readonly string LogPath;
    private static readonly object Lock = new();
    private static long _written;

    static SimpleLogger()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var logDir = Path.Combine(appData, "RoboMouse");
        Directory.CreateDirectory(logDir);
        LogPath = Path.Combine(logDir, "debug.log");
        try { _written = File.Exists(LogPath) ? new FileInfo(LogPath).Length : 0; }
        catch { }
    }

    /// <summary>The current log file.</summary>
    public static string LogFilePath => LogPath;

    /// <summary>
    /// Starts a new log file for this run, keeping the previous runs' files. Call once at app startup.
    /// </summary>
    public static void StartNewRun()
    {
        lock (Lock)
        {
            RollLocked($"=== RoboMouse Log Started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n");
        }
    }

    public static void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n";
        System.Diagnostics.Debug.Write(line);

        lock (Lock)
        {
            try
            {
                if (_written > MaxFileBytes)
                    RollLocked($"=== Log continued {DateTime.Now:yyyy-MM-dd HH:mm:ss} (previous file reached {MaxFileBytes / (1024 * 1024)} MB) ===\n");
                File.AppendAllText(LogPath, line);
                _written += line.Length;
            }
            catch { }
        }
    }

    public static void Log(string category, string message) => Log($"[{category}] {message}");

    private static void RollLocked(string header)
    {
        try
        {
            Roll(LogPath, KeptFiles);
            File.WriteAllText(LogPath, header);
            _written = header.Length;
        }
        catch { }
    }

    /// <summary>
    /// Shifts <c>name.log</c> to <c>name.1.log</c>, <c>name.1.log</c> to <c>name.2.log</c> and so on,
    /// deleting the oldest so that at most <paramref name="keep"/> files including a new
    /// <c>name.log</c> remain. Failures on individual files are ignored.
    /// </summary>
    internal static void Roll(string path, int keep)
    {
        string Numbered(int n) => n == 0 ? path : Path.ChangeExtension(path, $"{n}{Path.GetExtension(path)}");

        for (var n = keep - 1; n >= 1; n--)
        {
            try
            {
                var from = Numbered(n - 1);
                if (!File.Exists(from))
                    continue;
                File.Move(from, Numbered(n), overwrite: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // keep == 1: nothing is kept, the current file is simply started again.
        if (keep <= 1)
        {
            try { File.Delete(path); } catch { }
        }
    }
}
