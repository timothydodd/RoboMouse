using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RoboMouse.Core.Input;

namespace RoboMouse.Core.Screen;

/// <summary>
/// Information about the local screen configuration, read from the monitor APIs.
/// </summary>
public unsafe class ScreenInfo
{
    /// <summary>
    /// Primary screen bounds.
    /// </summary>
    public Rectangle PrimaryBounds { get; }

    /// <summary>
    /// Virtual screen bounds (all monitors combined).
    /// </summary>
    public Rectangle VirtualBounds { get; }

    /// <summary>
    /// All screen bounds.
    /// </summary>
    public IReadOnlyList<Rectangle> AllScreenBounds { get; }

    public ScreenInfo()
    {
        var monitors = EnumerateMonitors();
        PrimaryBounds = monitors.FirstOrDefault(m => m.Primary).Bounds;
        if (PrimaryBounds.IsEmpty)
            PrimaryBounds = monitors.Count > 0 ? monitors[0].Bounds : new Rectangle(0, 0, 1920, 1080);
        VirtualBounds = GetVirtualScreen();
        AllScreenBounds = monitors.Select(m => m.Bounds).ToList();
    }

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
    /// Gets the screen that contains the specified point (or the nearest one).
    /// </summary>
    public Rectangle GetScreenAt(int x, int y)
    {
        var monitor = NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = x, Y = y }, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = (uint)sizeof(NativeMethods.MONITORINFO) };
        if (monitor != 0 && NativeMethods.GetMonitorInfoW(monitor, &info))
            return ToRectangle(info.rcMonitor);
        return PrimaryBounds;
    }

    /// <summary>
    /// Checks if a point is at the edge of the virtual screen.
    /// </summary>
    public EdgeInfo? GetEdgeAt(int x, int y, int threshold = 0)
    {
        // Check if at left edge
        if (x <= VirtualBounds.Left + threshold)
        {
            return new EdgeInfo(Configuration.ScreenPosition.Left, x, y,
                (y - VirtualBounds.Top) / (float)VirtualBounds.Height);
        }

        // Check if at right edge
        if (x >= VirtualBounds.Right - 1 - threshold)
        {
            return new EdgeInfo(Configuration.ScreenPosition.Right, x, y,
                (y - VirtualBounds.Top) / (float)VirtualBounds.Height);
        }

        // Check if at top edge
        if (y <= VirtualBounds.Top + threshold)
        {
            return new EdgeInfo(Configuration.ScreenPosition.Top, x, y,
                (x - VirtualBounds.Left) / (float)VirtualBounds.Width);
        }

        // Check if at bottom edge
        if (y >= VirtualBounds.Bottom - 1 - threshold)
        {
            return new EdgeInfo(Configuration.ScreenPosition.Bottom, x, y,
                (x - VirtualBounds.Left) / (float)VirtualBounds.Width);
        }

        return null;
    }

    private readonly record struct Monitor(Rectangle Bounds, Rectangle WorkingArea, bool Primary);

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
        var info = new NativeMethods.MONITORINFO { cbSize = (uint)sizeof(NativeMethods.MONITORINFO) };
        if (NativeMethods.GetMonitorInfoW(hMonitor, &info))
        {
            t_enumerating?.Add(new Monitor(
                ToRectangle(info.rcMonitor),
                ToRectangle(info.rcWork),
                (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0));
        }
        return 1; // continue
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
