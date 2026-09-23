using System.Text.Json;
using System.Text.Json.Serialization;
using RoboMouse.Core.Logging;

namespace RoboMouse.App.Services;

/// <summary>
/// Settings and bookkeeping that only the desktop app uses (the core never reads them), kept in
/// <c>%AppData%\RoboMouse\app.json</c> beside the main settings file. A file that cannot be read
/// simply gives the defaults.
/// </summary>
public sealed class AppState
{
    /// <summary>Look for a newer release on GitHub once a day. Ignored in the Store build.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>When the last update check finished, successfully or not.</summary>
    public DateTime? LastUpdateCheckUtc { get; set; }

    /// <summary>The newest version the user was already told about, so the notification comes once per release.</summary>
    public string? LastNotifiedVersion { get; set; }

    [JsonIgnore]
    public string? FilePath { get; private set; }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RoboMouse", "app.json");

    public static AppState Load(string? path = null)
    {
        path ??= DefaultPath;
        AppState? state = null;
        try
        {
            if (File.Exists(path))
                state = JsonSerializer.Deserialize(File.ReadAllBytes(path), AppStateJsonContext.Default.AppState);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            SimpleLogger.Log("App", $"Could not read {path}: {ex.Message}");
        }
        state ??= new AppState();
        state.FilePath = path;
        return state;
    }

    public void Save()
    {
        if (FilePath == null)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllBytes(FilePath, JsonSerializer.SerializeToUtf8Bytes(this, AppStateJsonContext.Default.AppState));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SimpleLogger.Log("App", $"Could not save {FilePath}: {ex.Message}");
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppState))]
internal partial class AppStateJsonContext : JsonSerializerContext
{
}
