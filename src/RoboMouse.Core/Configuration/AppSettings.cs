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
    /// Hotkey that locks the cursor to the screen it is on (press again to unlock), so it cannot
    /// cross to another PC by accident. Null or empty for none. Scroll Lock, as in Synergy.
    /// </summary>
    public string? LockCursorHotkey { get; set; } = "Scroll";

    /// <summary>
    /// Hotkey that locks this PC and asks every connected peer to lock too. Null or empty for none.
    /// </summary>
    public string? LockAllHotkey { get; set; }

    /// <summary>
    /// When the cursor may cross to another screen: guards against switching by accident.
    /// </summary>
    public CrossingSettings Crossing { get; set; } = new();

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
    /// Mirror the power state of the machine that last controlled this one: stay awake with the display
    /// on while its display is on, and turn this display off when its display turns off or it sleeps.
    /// </summary>
    public bool FollowHostPower { get; set; } = false;

    /// <summary>
    /// Lock this PC when the machine that last controlled it locks (Win+L, or "Lock all PCs" there).
    /// Only a configured, enabled peer whose identity key is pinned is followed.
    /// </summary>
    public bool LockWithHost { get; set; } = false;

    /// <summary>
    /// Start this PC's screen saver when the machine that last controlled it starts its own (unless
    /// this PC was used in the last minute).
    /// </summary>
    public bool ScreensaverWithHost { get; set; } = false;

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
    /// Machine ids of peers the user removed. They are refused instead of coming back as pending
    /// connection requests; adding the machine again by hand takes it off this list.
    /// </summary>
    public List<string> BlockedMachineIds { get; set; } = new();

    /// <summary>
    /// What <see cref="Load"/> had to do because the settings file could not be read. The app shows
    /// it once; it is never saved.
    /// </summary>
    [JsonIgnore]
    public SettingsLoadNotice LoadNotice { get; private set; }

    /// <summary>Where the unreadable settings file was moved to, when <see cref="LoadNotice"/> is set.</summary>
    [JsonIgnore]
    public string? CorruptFilePath { get; private set; }

    // One lock for every load and save in the process: saves come from the UI thread and from
    // background work (a peer's MAC address, a peer switched on or off) and must not interleave.
    private static readonly object FileLock = new();

    /// <summary>
    /// Loads settings from the configuration file. A file that cannot be parsed is never overwritten:
    /// it is moved aside to <c>settings.corrupt-&lt;timestamp&gt;.json</c>, the backup from the last
    /// good save is tried, and only then do the defaults apply. <see cref="LoadNotice"/> says which.
    /// </summary>
    public static AppSettings Load(string? path = null)
    {
        var configPath = path ?? DefaultConfigPath;

        lock (FileLock)
        {
            var settings = TryRead(configPath, out var unreadable);
            if (settings == null)
            {
                string? corrupt = unreadable ? MoveAside(configPath) : null;

                // A missing file with a backup beside it also means the last save went wrong.
                settings = TryRead(BackupPath(configPath), out _);
                if (settings != null)
                    settings.LoadNotice = SettingsLoadNotice.RestoredFromBackup;
                else
                    settings = new AppSettings { LoadNotice = unreadable ? SettingsLoadNotice.Reset : SettingsLoadNotice.None };
                settings.CorruptFilePath = corrupt;
            }

            var needsSave = settings.LoadNotice != SettingsLoadNotice.None || !File.Exists(configPath);
            if (string.IsNullOrWhiteSpace(settings.PairingCode))
            {
                settings.PairingCode = Network.SecureChannel.GeneratePairingCode();
                needsSave = true;
            }

            if (needsSave)
                settings.SaveLocked(configPath);

            return settings;
        }
    }

    /// <summary>
    /// Saves settings. The file is written to <c>settings.json.tmp</c> and then swapped in, keeping the
    /// previous version as <c>settings.json.bak</c>, so a crash or a full disk mid-write never leaves a
    /// truncated file. Safe to call from any thread.
    /// </summary>
    public void Save(string? path = null)
    {
        lock (FileLock)
        {
            SaveLocked(path ?? DefaultConfigPath);
        }
    }

    private void SaveLocked(string configPath)
    {
        var directory = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.SerializeToUtf8Bytes(Snapshot(), SettingsJsonContext.Default.AppSettings);

        var temp = configPath + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(json);
            stream.Flush(flushToDisk: true);
        }

        if (!File.Exists(configPath))
        {
            File.Move(temp, configPath, overwrite: true);
            return;
        }

        try
        {
            File.Replace(temp, configPath, BackupPath(configPath), ignoreMetadataErrors: true);
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            // Some file systems (network shares) cannot replace in one step; copy, then move instead.
            File.Copy(configPath, BackupPath(configPath), overwrite: true);
            File.Move(temp, configPath, overwrite: true);
        }
    }

    /// <summary>
    /// A copy to serialize with its own lists, so the UI adding or removing a peer while a background
    /// save runs cannot change a collection underneath the serializer.
    /// </summary>
    private AppSettings Snapshot()
    {
        var copy = (AppSettings)MemberwiseClone();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                copy.Peers = Peers.Where(p => p is not null).ToList();
                copy.BlockedMachineIds = BlockedMachineIds.Where(id => id is not null).ToList();
                return copy;
            }
            catch (InvalidOperationException) when (attempt < 3)
            {
                // Changed mid-copy on another thread; take the copy again.
            }
        }
    }

    private static string BackupPath(string configPath) => configPath + ".bak";

    /// <summary>
    /// Parses a settings file. Returns null when it is missing (<paramref name="unreadable"/> false) or
    /// cannot be read or parsed (true).
    /// </summary>
    private static AppSettings? TryRead(string file, out bool unreadable)
    {
        unreadable = false;
        if (!File.Exists(file))
            return null;
        try
        {
            var settings = JsonSerializer.Deserialize(File.ReadAllBytes(file), SettingsJsonContext.Default.AppSettings);
            if (settings != null)
            {
                // "null" in the file for a collection would otherwise surface as a crash much later.
                settings.Peers ??= new();
                settings.BlockedMachineIds ??= new();
                settings.Clipboard ??= new();
                settings.Crossing ??= new();
                return settings;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
        unreadable = true;
        return null;
    }

    /// <summary>Renames an unreadable settings file so it is kept for inspection. Returns the new path, or null.</summary>
    private static string? MoveAside(string configPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(configPath) ?? string.Empty;
            var name = Path.GetFileNameWithoutExtension(configPath);
            var target = Path.Combine(directory, $"{name}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Move(configPath, target, overwrite: true);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>What loading the settings had to do because the file could not be read.</summary>
public enum SettingsLoadNotice
{
    /// <summary>The file was read normally, or did not exist yet.</summary>
    None,

    /// <summary>The file was unreadable; the backup from the previous save was used.</summary>
    RestoredFromBackup,

    /// <summary>The file and its backup were unreadable; the defaults were used.</summary>
    Reset
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
