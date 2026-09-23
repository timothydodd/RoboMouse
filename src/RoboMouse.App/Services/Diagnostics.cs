using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RoboMouse.Core.Logging;
using RoboMouse.Core.Screen;

namespace RoboMouse.App.Services;

/// <summary>Where <see cref="Diagnostics.Export"/> collects files from, and the system summary to add.</summary>
public sealed record DiagnosticsSources(string DataFolder, string? ServiceLogFolder, string SystemInfo);

/// <summary>
/// The log folder, the crash file and the "Export diagnostics" zip: logs, the settings file with every
/// secret taken out, and a summary of the version, OS and monitors, for attaching to a bug report.
/// </summary>
public static class Diagnostics
{
    private const string Redacted = "(redacted)";

    /// <summary>%AppData%\RoboMouse: settings, logs and the crash file.</summary>
    public static string DataFolder => Path.GetDirectoryName(SimpleLogger.LogFilePath)!;

    /// <summary>The last unhandled exception, overwritten by the next one.</summary>
    public static string CrashFilePath => Path.Combine(DataFolder, "crash.txt");

    /// <summary>Where the desktop service logs (only readable when the service allows it).</summary>
    public static string ServiceLogFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RoboMouse");

    public static string SuggestedFileName(DateTime now) => $"RoboMouse-diagnostics-{now:yyyyMMdd-HHmmss}.zip";

    /// <summary>
    /// Writes <paramref name="exception"/> to crash.txt. Never throws: it runs from the handlers of
    /// last resort, where a second exception would hide the first.
    /// </summary>
    public static void WriteCrash(string source, object? exception, string? folder = null)
    {
        try
        {
            var path = folder == null ? CrashFilePath : Path.Combine(folder, "crash.txt");
            File.WriteAllText(path,
                $"RoboMouse {UpdateChecker.CurrentVersion} crashed at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ({source})\n" +
                $"{RuntimeInformation.OSDescription}\n\n{exception}\n");
        }
        catch
        {
        }
    }

    /// <summary>Plain-text summary: versions, OS, and the monitor layout (null when it could not be read).</summary>
    public static string DescribeSystem(Version appVersion, bool packaged, MonitorLayout? layout)
    {
        var text = new StringBuilder();
        text.AppendLine($"RoboMouse {appVersion} ({(packaged ? "Microsoft Store" : "direct download")})");
        text.AppendLine($"OS: {RuntimeInformation.OSDescription} ({Environment.OSVersion.Version}), {RuntimeInformation.OSArchitecture}");
        text.AppendLine($".NET: {RuntimeInformation.FrameworkDescription}, process {RuntimeInformation.ProcessArchitecture}");
        text.AppendLine($"Exported: {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
        text.AppendLine();
        if (layout == null)
        {
            text.AppendLine("Monitors: could not be read");
        }
        else
        {
            text.AppendLine($"Desktop: {Describe(layout.VirtualBounds)}");
            var number = 0;
            foreach (var monitor in layout.Monitors)
                text.AppendLine($"Monitor {++number}{(monitor.Primary ? " (main)" : "")}: {Describe(monitor.Bounds)}, working area {Describe(monitor.WorkingArea)}");
        }
        return text.ToString();

        static string Describe(System.Drawing.Rectangle r) => $"{r.Width}x{r.Height} at ({r.X}, {r.Y})";
    }

    /// <summary>Writes the diagnostics zip. Files that are missing or cannot be read are listed in it instead.</summary>
    public static void Export(string zipPath, DiagnosticsSources sources)
    {
        var skipped = new List<string>();
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        Add(zip, "system.txt", Encoding.UTF8.GetBytes(sources.SystemInfo));

        foreach (var file in Files(sources.DataFolder, "debug*.log").Concat(Files(sources.DataFolder, "crash.txt")).Concat(Files(sources.DataFolder, "app.json")))
            AddFile(zip, file, Path.GetFileName(file), skipped);

        var settings = Path.Combine(sources.DataFolder, "settings.json");
        if (File.Exists(settings))
        {
            var redacted = TryRead(settings, skipped) is { } bytes ? RedactSettings(Encoding.UTF8.GetString(bytes)) : null;
            if (redacted != null)
                Add(zip, "settings.json", Encoding.UTF8.GetBytes(redacted));
            else
                skipped.Add("settings.json (could not be read or parsed, so it was left out rather than risk its secrets)");
        }

        if (sources.ServiceLogFolder != null)
        {
            foreach (var file in Files(sources.ServiceLogFolder, "service*.log"))
                AddFile(zip, file, "service/" + Path.GetFileName(file), skipped);
        }

        if (skipped.Count > 0)
            Add(zip, "skipped.txt", Encoding.UTF8.GetBytes(string.Join(Environment.NewLine, skipped)));
    }

    /// <summary>
    /// The settings JSON with the pairing code and anything that looks like key material replaced.
    /// Returns null when it cannot be parsed (the file is then left out, never copied raw).
    /// </summary>
    public static string? RedactSettings(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException)
        {
            return null;
        }
        if (root == null)
            return null;

        Redact(root);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            root.WriteTo(writer);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Redact(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Select(p => p.Key).ToList())
                {
                    if (IsSecret(name))
                    {
                        if (obj[name] != null)
                            obj[name] = Redacted;
                    }
                    else if (obj[name] is { } child)
                    {
                        Redact(child);
                    }
                }
                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    if (item != null)
                        Redact(item);
                }
                break;
        }
    }

    /// <summary>
    /// The pairing code, and any key, secret, token or password field (including identity keys a
    /// later version may add); a hotkey is not a secret.
    /// </summary>
    internal static bool IsSecret(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("hotkey"))
            return false;
        return n.Contains("pairingcode") || n.EndsWith("key") || n.EndsWith("keys") || n.Contains("privatekey")
               || n.Contains("secret") || n.Contains("password") || n.Contains("token");
    }

    private static IEnumerable<string> Files(string folder, string pattern)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.GetFiles(folder, pattern).OrderBy(f => f, StringComparer.OrdinalIgnoreCase) : Enumerable.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Enumerable.Empty<string>();
        }
    }

    private static void AddFile(ZipArchive zip, string path, string entryName, List<string> skipped)
    {
        if (TryRead(path, skipped) is { } bytes)
            Add(zip, entryName, bytes);
    }

    /// <summary>Reads a file another process (the logger, the service) may still be writing.</summary>
    private static byte[]? TryRead(string path, List<string> skipped)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            skipped.Add($"{path}: {ex.Message}");
            return null;
        }
    }

    private static void Add(ZipArchive zip, string name, byte[] content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(content);
    }
}
