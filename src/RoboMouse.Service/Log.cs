using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace RoboMouse.Service;

/// <summary>A destination for service log lines.</summary>
internal interface ILogSink
{
    void Write(string line);
}

/// <summary>
/// Minimal log for the service, which has no console when run by the SCM. Lines go to
/// <c>%ProgramData%\RoboMouse\service.log</c> once <see cref="Initialize"/> has checked that folder is
/// safe for SYSTEM to write in, to the Application event log when it is not, and only to the debugger
/// before that (and in tests).
/// </summary>
internal static class Log
{
    private static readonly LogThrottle Throttle = new(TimeSpan.FromMinutes(1));
    private static volatile ILogSink? s_sink;

    /// <summary>Where lines go; replaceable for tests.</summary>
    internal static ILogSink? Sink
    {
        get => s_sink;
        set => s_sink = value;
    }

    /// <summary>Picks the file log when its folder is safe, the event log otherwise.</summary>
    [SupportedOSPlatform("windows")]
    public static void Initialize()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RoboMouse");
        if (LogFolder.TryPrepare(dir, out var problem))
        {
            s_sink = new RollingFileLog(Path.Combine(dir, "service.log"), RollingFileLog.DefaultMaxBytes);
            return;
        }
        s_sink = new EventLogSink();
        Write($"Not logging to '{dir}': {problem}. Logging to the Application event log instead.");
    }

    public static void Write(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
        System.Diagnostics.Debug.WriteLine(line);
        s_sink?.Write(line);
    }

    /// <summary>
    /// Writes at most one line per <paramref name="key"/> a minute, noting how many were skipped. For
    /// anything a local process can trigger at will (a rejected pipe client retrying every few seconds).
    /// </summary>
    public static void WriteLimited(string key, string message)
    {
        if (Throttle.ShouldWrite(key, out var suppressed))
            Write(suppressed > 0 ? $"{message} ({suppressed} similar skipped)" : message);
    }
}

/// <summary>Allows one line per key per interval and counts the ones it holds back.</summary>
internal sealed class LogThrottle
{
    private readonly TimeSpan _interval;
    private readonly Func<DateTime> _now;
    private readonly Dictionary<string, (DateTime Last, int Suppressed)> _keys = new();

    public LogThrottle(TimeSpan interval, Func<DateTime>? now = null)
    {
        _interval = interval;
        _now = now ?? (() => DateTime.UtcNow);
    }

    public bool ShouldWrite(string key, out int suppressed)
    {
        lock (_keys)
        {
            var now = _now();
            if (_keys.TryGetValue(key, out var entry) && now - entry.Last < _interval)
            {
                _keys[key] = (entry.Last, entry.Suppressed + 1);
                suppressed = 0;
                return false;
            }
            suppressed = entry.Suppressed;
            _keys[key] = (now, 0);
            // Keys are a handful of fixed strings; this only guards against a caller using free text.
            if (_keys.Count > 256)
                _keys.Clear();
            return true;
        }
    }
}

/// <summary>Appends to one file, moving it to <c>.1</c> (replacing the previous one) once it passes the cap.</summary>
internal sealed class RollingFileLog : ILogSink
{
    public const long DefaultMaxBytes = 1024 * 1024;

    private readonly string _path;
    private readonly long _maxBytes;
    private readonly object _lock = new();
    private long _size = -1;

    public RollingFileLog(string path, long maxBytes)
    {
        _path = path;
        _maxBytes = maxBytes;
    }

    public void Write(string line)
    {
        var text = line + Environment.NewLine;
        lock (_lock)
        {
            try
            {
                if (_size < 0)
                {
                    RemoveIfLink(_path);
                    RemoveIfLink(_path + ".1");
                    _size = File.Exists(_path) ? new FileInfo(_path).Length : 0;
                }
                if (_size > 0 && _size + text.Length > _maxBytes)
                {
                    File.Move(_path, _path + ".1", overwrite: true);
                    _size = 0;
                }
                File.AppendAllText(_path, text);
                _size += System.Text.Encoding.UTF8.GetByteCount(text);
            }
            catch
            {
                // Logging must never take the service down.
            }
        }
    }

    // A link planted before the folder was locked down would redirect SYSTEM's writes; drop the link
    // itself (never its target) and start a fresh file.
    private static void RemoveIfLink(string path)
    {
        var info = new FileInfo(path);
        if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            info.Delete();
    }
}

/// <summary>Checks, or creates, the ProgramData folder the service logs to.</summary>
[SupportedOSPlatform("windows")]
internal static class LogFolder
{
    /// <summary>
    /// True when SYSTEM can safely write in <paramref name="dir"/>: it is a real folder (not a junction
    /// a user planted to redirect the writes) owned by SYSTEM or Administrators. A missing folder is
    /// created with the same ACL the installer sets: SYSTEM and Administrators full, Users read.
    /// </summary>
    public static bool TryPrepare(string dir, out string problem)
    {
        problem = string.Empty;
        try
        {
            var info = new DirectoryInfo(dir);
            if (!info.Exists)
            {
                info.Create(CreateSecurity());
                return true;
            }
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                problem = "the folder is a junction or link";
                return false;
            }

            var owner = info.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner == null
                || !(owner.IsWellKnown(WellKnownSidType.LocalSystemSid) || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)))
            {
                problem = $"the folder is owned by {owner?.Value ?? "nobody"}, not SYSTEM or Administrators";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            problem = ex.Message;
            return false;
        }
    }

    private static DirectorySecurity CreateSecurity()
    {
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }
}

/// <summary>Writes to the Application event log under the service's name (registered by the installer).</summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class EventLogSink : ILogSink
{
    private const ushort EVENTLOG_INFORMATION_TYPE = 0x0004;

    private readonly nint _source = RegisterEventSourceW(null, "RoboMouseService");

    public void Write(string line)
    {
        if (_source == 0)
            return;
        fixed (char* text = line)
        {
            var strings = stackalloc char*[1];
            strings[0] = text;
            ReportEventW(_source, EVENTLOG_INFORMATION_TYPE, 0, 0, 0, 1, 0, strings, null);
        }
    }

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint RegisterEventSourceW(string? uncServerName, string sourceName);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReportEventW(nint eventLog, ushort type, ushort category, uint eventId, nint userSid,
        ushort numStrings, uint dataSize, char** strings, void* rawData);
}
