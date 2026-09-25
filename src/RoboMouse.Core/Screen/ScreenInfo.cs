using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RoboMouse.Core.Input;

namespace RoboMouse.Core.Screen;

/// <summary>
/// The local monitor arrangement, read from the monitor APIs. Monitors come and go and the primary
/// can change while the app runs (which moves the coordinate origin), so the arrangement is re-read
/// when the cached copy is more than a second old rather than captured once.
/// </summary>
public unsafe class ScreenInfo
{
    private const long MaxAgeMs = 1000;

    private volatile MonitorLayout _layout = ReadLayout();
    private long _readAt = Environment.TickCount64;

    /// <summary>The current arrangement. Cheap enough for the mouse hook: a tick compare when fresh.</summary>
    public MonitorLayout Layout
    {
        get
        {
            var now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _readAt) > MaxAgeMs)
            {
                Interlocked.Exchange(ref _readAt, now);
                _layout = ReadLayout();
            }
            return _layout;
        }
    }

    /// <summary>
    /// Primary screen bounds.
    /// </summary>
    public Rectangle PrimaryBounds => Layout.PrimaryBounds;

    /// <summary>
    /// Virtual screen bounds (all monitors combined).
    /// </summary>
    public Rectangle VirtualBounds => Layout.VirtualBounds;

    /// <summary>Reads the monitor arrangement as it is right now.</summary>
    public static MonitorLayout ReadLayout() =>
        new(EnumerateMonitors().Select(m => new MonitorRect(m.Bounds, m.WorkingArea, m.Primary, m.Id, m.Scale)));

    /// <summary>The bounding rectangle of all monitors, from the system metrics.</summary>
    public static Rectangle GetVirtualScreen()
    {
        var (x, y, w, h) = InputSimulator.GetVirtualScreenBounds();
        if (w <= 0 || h <= 0)
            return new Rectangle(0, 0, 1920, 1080);
        return new Rectangle(x, y, w, h);
    }

    /// <summary>The primary monitor's working area (excluding the taskbar).</summary>
    public static Rectangle GetPrimaryWorkingArea()
    {
        var primary = EnumerateMonitors().FirstOrDefault(m => m.Primary);
        return primary.WorkingArea.IsEmpty ? new Rectangle(0, 0, 1920, 1080) : primary.WorkingArea;
    }

    /// <summary>
    /// Checks if a point is on an outer edge of the desktop.
    /// </summary>
    public EdgeInfo? GetEdgeAt(int x, int y, int threshold = 0) => Layout.GetEdgeAt(x, y, threshold);

    /// <summary>Every outer edge the point is on (two in a corner).</summary>
    public List<EdgeInfo> GetEdgesAt(int x, int y, int threshold = 0) => Layout.GetEdgesAt(x, y, threshold);

    private readonly record struct Monitor(Rectangle Bounds, Rectangle WorkingArea, bool Primary, string Id = "", int Scale = 100);

    [ThreadStatic]
    private static List<Monitor>? t_enumerating;

    private static List<Monitor> EnumerateMonitors()
    {
        var list = new List<Monitor>();
        t_enumerating = list;
        try
        {
            NativeMethods.EnumDisplayMonitors(0, null, &MonitorCallback, 0);
        }
        finally
        {
            t_enumerating = null;
        }
        return list;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int MonitorCallback(nint hMonitor, nint hdc, NativeMethods.RECT* rect, nint data)
    {
        var ex = new NativeMethods.MONITORINFOEXW();
        ex.Info.cbSize = (uint)sizeof(NativeMethods.MONITORINFOEXW);
        if (NativeMethods.GetMonitorInfoExW(hMonitor, &ex))
        {
            var info = ex.Info;
            t_enumerating?.Add(new Monitor(
                ToRectangle(info.rcMonitor),
                ToRectangle(info.rcWork),
                (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0,
                new string(ex.szDevice),
                ReadScale(hMonitor)));
        }
        return 1; // continue
    }

    /// <summary>The monitor's scaling in percent; 100 where shcore is missing or the call fails.</summary>
    private static int ReadScale(nint hMonitor)
    {
        try
        {
            uint dpiX, dpiY;
            if (NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MDT_EFFECTIVE_DPI, &dpiX, &dpiY) == 0 && dpiX > 0)
                return (int)Math.Round(dpiX * 100.0 / 96);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
        }
        return 100;
    }

    private static Rectangle ToRectangle(NativeMethods.RECT r) => Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
}

/// <summary>
/// Information about a screen edge detection.
/// </summary>
public class EdgeInfo
{
    /// <summary>
    /// Which edge was detected.
    /// </summary>
    public Configuration.ScreenPosition Edge { get; }

    /// <summary>
    /// X coordinate at the edge.
    /// </summary>
    public int X { get; }

    /// <summary>
    /// Y coordinate at the edge.
    /// </summary>
    public int Y { get; }

    /// <summary>
    /// Normalized position along the edge (0-1).
    /// For Left/Right edges, this is the vertical position.
    /// For Top/Bottom edges, this is the horizontal position.
    /// </summary>
    public float NormalizedPosition { get; }

    public EdgeInfo(Configuration.ScreenPosition edge, int x, int y, float normalizedPosition)
    {
        Edge = edge;
        X = x;
        Y = y;
        NormalizedPosition = Math.Clamp(normalizedPosition, 0f, 1f);
    }
}
