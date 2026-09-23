namespace RoboMouse.Core.Logging;

/// <summary>
/// Lets a repeated log line through once per interval per key (a remote address, say) and counts what
/// it held back, so a machine retrying every few seconds, or someone hammering the port, cannot fill
/// the log. Thread-safe.
/// </summary>
public sealed class LogThrottle
{
    private readonly object _lock = new();
    private readonly Dictionary<string, (long NextAllowed, int Suppressed)> _keys = new();
    private readonly long _intervalMs;
    private readonly Func<long> _now;

    public LogThrottle(TimeSpan interval, Func<long>? now = null)
    {
        _intervalMs = (long)interval.TotalMilliseconds;
        _now = now ?? (() => Environment.TickCount64);
    }

    /// <summary>
    /// True when a line for <paramref name="key"/> may be written now; <paramref name="suppressed"/> is
    /// how many were held back since the last one that was.
    /// </summary>
    public bool ShouldLog(string key, out int suppressed)
    {
        var now = _now();
        lock (_lock)
        {
            // Keep the table small: forget keys that have been quiet for a whole interval.
            if (_keys.Count > 256)
            {
                foreach (var stale in _keys.Where(k => k.Value.NextAllowed <= now).Select(k => k.Key).ToList())
                    _keys.Remove(stale);
            }

            if (_keys.TryGetValue(key, out var entry) && now < entry.NextAllowed)
            {
                _keys[key] = (entry.NextAllowed, entry.Suppressed + 1);
                suppressed = 0;
                return false;
            }

            suppressed = entry.Suppressed;
            _keys[key] = (now + _intervalMs, 0);
            return true;
        }
    }

    /// <summary>Writes <paramref name="message"/> if the throttle allows it, noting how many were skipped.</summary>
    public void Log(string key, string category, string message)
    {
        if (!ShouldLog(key, out var suppressed))
            return;
        SimpleLogger.Log(category, suppressed > 0 ? $"{message} ({suppressed} similar not logged)" : message);
    }
}
