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
    /// Whether this peer takes part at all. A disabled peer keeps its configuration but is never
    /// connected to, never accepted when it connects to us, and its edge acts as a normal screen edge.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The peer's MAC address (12 hex digits), learned when it connects and kept so it can be woken
    /// with Wake-on-LAN while it sleeps. Empty until the peer has connected once.
    /// </summary>
    public string MacAddress { get; set; } = string.Empty;

    /// <summary>
    /// The peer's identity public key (base64 SubjectPublicKeyInfo), pinned the first time it paired
    /// with this machine. Later connections must prove this key, and need no pairing code. Empty until
    /// then, or after the user chose to pair again (<see cref="RoboMouseService.ForgetPeerIdentity"/>).
    /// </summary>
    public string IdentityKey { get; set; } = string.Empty;

    /// <summary>
    /// Whether clipboard content and copied files are shared with this peer, both ways: with it off,
    /// nothing copied here (or relayed through here) is sent to it, and nothing it sends is applied
    /// or passed on. The General page's clipboard switches still apply on top.
    /// </summary>
    public bool ShareClipboard { get; set; } = true;

    /// <summary>
    /// Hotkey that moves the cursor straight onto this peer's screen. Null means the default for its
    /// place in the list (Ctrl+Alt+F1 to F4 for the first four, see <see cref="Input.HotkeySet.DefaultJumpHotkey"/>);
    /// empty means none.
    /// </summary>
    public string? JumpHotkey { get; set; }

    /// <summary>
    /// Unique identifier for this peer (generated or received).
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
}
