namespace RoboMouse.Service;

/// <summary>
/// Minimal file log for the service, which has no console when run by the SCM. Writes to ProgramData
/// (writable by SYSTEM and readable by the user) rather than a user profile.
/// </summary>
internal static class Log
{
    private static readonly string Path;
    private static readonly object Lock = new();

    static Log()
    {
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RoboMouse");
        try { Directory.CreateDirectory(dir); } catch { }
        Path = System.IO.Path.Combine(dir, "service.log");
    }

    public static void Write(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
        System.Diagnostics.Debug.WriteLine(line);
        lock (Lock)
        {
            try { File.AppendAllText(Path, line + Environment.NewLine); } catch { }
        }
    }
}
