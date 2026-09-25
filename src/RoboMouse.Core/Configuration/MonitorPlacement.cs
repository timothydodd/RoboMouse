using System.Text.Json.Serialization;

namespace RoboMouse.Core.Configuration;

/// <summary>
/// Where one of a peer's monitors sits on this PC's layout, plus what that monitor looked like on the
/// peer when it last reported it (used to place monitors that appear later next to their neighbours).
/// Layout coordinates are this PC's virtual-screen pixels; a remote monitor's layout size is its pixel
/// size scaled by this PC's main display scaling over its own, so screens of the same physical size
/// line up. Treated as immutable once in <see cref="PeerConfig.Monitors"/>: changes replace the list.
/// </summary>
public class MonitorPlacement
{
    /// <summary>The peer's name for the monitor (its Windows device name, <c>\\.\DISPLAY1</c>).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Position and size on this PC's layout.</summary>
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>The monitor's rectangle in the peer's own virtual-screen pixels.</summary>
    public int RemoteX { get; set; }
    public int RemoteY { get; set; }
    public int RemoteWidth { get; set; }
    public int RemoteHeight { get; set; }

    /// <summary>The monitor's display scaling on the peer, in percent.</summary>
    public int Scale { get; set; } = 100;

    /// <summary>Whether it is the peer's main display.</summary>
    public bool Primary { get; set; }

    public MonitorPlacement Clone() => (MonitorPlacement)MemberwiseClone();

    [JsonIgnore]
    public System.Drawing.Rectangle Rect => new(X, Y, Width, Height);

    [JsonIgnore]
    public System.Drawing.Rectangle RemoteRect => new(RemoteX, RemoteY, RemoteWidth, RemoteHeight);
}
