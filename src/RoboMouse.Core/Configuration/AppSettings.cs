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
    /// Whether TLS encryption is enabled.
    /// </summary>
    public bool EncryptionEnabled { get; set; } = false;

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
    /// Number of pixels from screen edge to trigger transition.
    /// </summary>
    public int EdgeThreshold { get; set; } = 0;

    /// <summary>
    /// Whether the debug panel is enabled.
    /// </summary>
    public bool DebugPanelEnabled { get; set; } = false;

    /// <summary>
    /// Loads settings from the default configuration file.
    /// </summary>
    public static AppSettings Load(string? path = null)
    {
        var configPath = path ?? DefaultConfigPath;

        if (!File.Exists(configPath))
        {
            var settings = new AppSettings();
            settings.Save(configPath);
            return settings;
        }

        try
        {
            var json = File.ReadAllText(configPath);
            return JsonSerializer.Deserialize<AppSettings>(json, GetJsonOptions()) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
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

        var json = JsonSerializer.Serialize(this, GetJsonOptions());
        File.WriteAllText(configPath, json);
    }

    private static JsonSerializerOptions GetJsonOptions()
    {
        return new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };
    }
}
