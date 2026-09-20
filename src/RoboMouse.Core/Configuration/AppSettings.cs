using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoboMouse.Core.Configuration;

/// <summary>
/// Main application configuration settings.
/// </summary>
public class AppSettings
{
    private static readonly string DefaultConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RoboMouse",
        "settings.json");

    /// <summary>
    /// Unique identifier for this machine.
    /// </summary>
    public string MachineId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Display name for this machine (shown to peers).
    /// </summary>
    public string MachineName { get; set; } = Environment.MachineName;

    /// <summary>
    /// Port for TCP connections.
    /// </summary>
    public int LocalPort { get; set; } = 24800;

    /// <summary>
    /// Port for UDP discovery broadcasts.
    /// </summary>
    public int DiscoveryPort { get; set; } = 24801;

    /// <summary>
    /// Shared pairing code. Every machine must use the same code; it authenticates connections and
    /// keys the encryption. Generated on first run.
    /// </summary>
    public string PairingCode { get; set; } = string.Empty;

    /// <summary>
    /// Whether the application is enabled (capturing/sending input).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Start with Windows.
    /// </summary>
    public bool StartWithWindows { get; set; } = false;

    /// <summary>
    /// Start minimized to tray.
    /// </summary>
    public bool StartMinimized { get; set; } = true;

    /// <summary>
    /// Configured peer machines.
    /// </summary>
    public List<PeerConfig> Peers { get; set; } = new();

    /// <summary>
    /// Clipboard synchronization settings.
    /// </summary>
    public ClipboardSettings Clipboard { get; set; } = new();

    /// <summary>
    /// Hotkey to toggle mouse sharing on/off (e.g., "Ctrl+Alt+M").
    /// </summary>
    public string? ToggleHotkey { get; set; } = "Ctrl+Alt+M";

    /// <summary>
    /// Pushing through an edge that has no peer comes out on the far side of the peer on the opposite
    /// edge, and a controlled screen hands control back from any edge, so screens form a ring.
    /// </summary>
    public bool WrapAround { get; set; } = false;

    /// <summary>
    /// Pushing the mouse against the edge of a peer that is not connected sends it a Wake-on-LAN packet.
    /// </summary>
    public bool WakeOnEdge { get; set; } = true;

    /// <summary>
    /// Number of pixels from screen edge to trigger transition.
    /// </summary>
    public int EdgeThreshold { get; set; } = 0;

    /// <summary>
    /// Apply a remote controller's input through the separately installed RoboMouse desktop service, so
    /// UAC prompts, the lock screen and elevated windows can be driven. Ignored when it is not installed.
    /// </summary>
    public bool UseDesktopService { get; set; } = false;

    /// <summary>
    /// Whether the debug panel is enabled.
    /// </summary>
    public bool DebugPanelEnabled { get; set; } = false;

    /// <summary>
    /// Visual cue shown when the mouse arrives on this screen from another machine.
    /// </summary>
    public EdgeHighlightStyle EdgeHighlight { get; set; } = EdgeHighlightStyle.Fade;

    /// <summary>
    /// Legacy on/off switch for the highlight, kept so an old settings file that turned it off
    /// still maps to <see cref="EdgeHighlightStyle.None"/>. Never written.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ShowBorderHighlight
    {
        get => null;
        set { if (value == false) EdgeHighlight = EdgeHighlightStyle.None; }
    }

    /// <summary>
    /// Loads settings from the default configuration file.
    /// </summary>
    public static AppSettings Load(string? path = null)
    {
        var configPath = path ?? DefaultConfigPath;

        AppSettings settings;
        if (!File.Exists(configPath))
        {
            settings = new AppSettings();
        }
        else
        {
            try
            {
                var json = File.ReadAllText(configPath);
                settings = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
            }
            catch
            {
                settings = new AppSettings();
            }
        }

        if (string.IsNullOrWhiteSpace(settings.PairingCode))
        {
            settings.PairingCode = Network.SecureChannel.GeneratePairingCode();
            settings.Save(configPath);
        }
        else if (!File.Exists(configPath))
        {
            settings.Save(configPath);
        }

        return settings;
    }

    /// <summary>
    /// Saves settings to the configuration file.
    /// </summary>
    public void Save(string? path = null)
    {
        var configPath = path ?? DefaultConfigPath;
        var directory = Path.GetDirectoryName(configPath);

        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(this, SettingsJsonContext.Default.AppSettings);
        File.WriteAllText(configPath, json);
    }
}

/// <summary>
/// Source-generated serializer metadata for the settings file: camelCase names, enums as strings,
/// indented output. Generated at build time so no reflection is needed at run time.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal partial class SettingsJsonContext : JsonSerializerContext
{
}
