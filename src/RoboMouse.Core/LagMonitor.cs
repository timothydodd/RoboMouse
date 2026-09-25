namespace RoboMouse.Core;

/// <summary>
/// Watches motion arriving on the controlled side for lag, so "the mouse has to catch up" shows up in
/// the log with a cause. Two things are measured per motion message: how late it arrived (receive time
/// minus the sender's timestamp, less the smallest such gap lately, which absorbs the difference between
/// the two clocks) and how long applying it took here. Nothing is logged per message: <see cref="Record"/>
/// returns one summary line at most every <see cref="ReportEveryMs"/>, and only when something was late
/// or slow. Pure; one thread at a time.
/// </summary>
public sealed class LagMonitor
{
    /// <summary>A message this much later than the best lately counts as late.</summary>
    public const int LateMs = 40;

    /// <summary>Applying one message taking this long counts as slow.</summary>
    public const double SlowApplyMs = 4;

    public const int ReportEveryMs = 2000;

    // The clock offset baseline is the smallest gap over the last window or two, so a clock adjustment
    // on either machine is forgotten within a minute.
    private const int BaselineWindowMs = 30000;

    private long _baseline = long.MaxValue;
    private long _nextBaseline = long.MaxValue;
    private long _baselineSince = long.MinValue;

    private long _reportSince = long.MinValue;
    private int _count;
    private int _late;
    private long _worstLateMs;
    private int _slow;
    private double _worstApplyMs;

    /// <summary>Starts over (control began).</summary>
    public void Reset()
    {
        _reportSince = long.MinValue;
        _count = _late = _slow = 0;
        _worstLateMs = 0;
        _worstApplyMs = 0;
    }

    /// <param name="nowMs">Receive time, Unix milliseconds.</param>
    /// <param name="sentMs">The message's timestamp from the sender, Unix milliseconds.</param>
    /// <param name="applyMs">How long applying it took here.</param>
    /// <returns>A summary to log, or null.</returns>
    public string? Record(long nowMs, long sentMs, double applyMs)
    {
        var gap = nowMs - sentMs;
        if (nowMs - _baselineSince >= BaselineWindowMs)
        {
            _baseline = Math.Min(_nextBaseline, gap);
            _nextBaseline = gap;
            _baselineSince = nowMs;
        }
        _nextBaseline = Math.Min(_nextBaseline, gap);
        _baseline = Math.Min(_baseline, gap);

        var lateBy = gap - _baseline;
        _count++;
        if (lateBy >= LateMs)
        {
            _late++;
            _worstLateMs = Math.Max(_worstLateMs, lateBy);
        }
        if (applyMs >= SlowApplyMs)
        {
            _slow++;
            _worstApplyMs = Math.Max(_worstApplyMs, applyMs);
        }

        if (_reportSince == long.MinValue)
            _reportSince = nowMs;
        if (nowMs - _reportSince < ReportEveryMs)
            return null;

        string? report = null;
        if (_late > 0 || _slow > 0)
        {
            report = $"{_count} motion messages in {(nowMs - _reportSince) / 1000.0:F1} s: " +
                     $"{_late} arrived late (worst {_worstLateMs} ms), {_slow} slow to apply (worst {_worstApplyMs:F1} ms)";
        }
        _reportSince = nowMs;
        _count = _late = _slow = 0;
        _worstLateMs = 0;
        _worstApplyMs = 0;
        return report;
    }
}
