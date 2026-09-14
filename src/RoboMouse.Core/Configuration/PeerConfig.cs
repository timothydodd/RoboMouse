using System.Text.Json.Serialization;

namespace RoboMouse.Core.Configuration;

/// <summary>
/// Configuration for a peer machine.
/// </summary>
public class PeerConfig
{
    /// <summary>
    /// Display name for this peer.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// IP address or hostname of the peer.
    /// </summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// Port number for the peer connection.
    /// </summary>
    public int Port { get; set; } = 24800;

    /// <summary>
    /// Position of this peer's screen relative to the local screen.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ScreenPosition Position { get; set; } = ScreenPosition.Right;

    /// <summary>
    /// Vertical offset in pixels (for Left/Right positions).
    /// Positive values shift the peer screen down, negative shifts up.
    /// </summary>
    public int OffsetY { get; set; } = 0;

    /// <summary>
    /// Horizontal offset in pixels (for Top/Bottom positions).
    /// Positive values shift the peer screen right, negative shifts left.
    /// </summary>
    public int OffsetX { get; set; } = 0;

    /// <summary>
    /// The peer's screen width in pixels (received during handshake).
    /// </summary>
    [JsonIgnore]
    public int ScreenWidth { get; set; } = 1920;

    /// <summary>
    /// The peer's screen height in pixels (received during handshake).
    /// </summary>
    [JsonIgnore]
    public int ScreenHeight { get; set; } = 1080;

    /// <summary>
    /// Unique identifier for this peer (generated or received).
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
}
