using RoboMouse.Core.Network.Protocol;

namespace RoboMouse.Core.Network;

/// <summary>
/// Checks the entries of a file offer from a peer before they are put on the clipboard. The names
/// become paths when Explorer pastes them, so a name that climbs out of the paste folder ("..\x"),
/// points somewhere absolute ("C:\x", "\\server\x"), names an alternate data stream ("a:b") or a
/// device ("CON") must never get that far. Any bad entry rejects the whole offer.
/// </summary>
public static class FileOfferValidator
{
    /// <summary>Most entries accepted in one offer.</summary>
    public const int MaxEntries = 100_000;

    /// <summary>Longest relative path accepted (MAX_PATH less the terminator).</summary>
    public const int MaxPathLength = 259;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "COM\u00B9", "COM\u00B2", "COM\u00B3", "LPT\u00B9", "LPT\u00B2", "LPT\u00B3"
    };

    /// <summary>Returns null when the offer is acceptable, otherwise why not.</summary>
    public static string? Validate(IReadOnlyList<FileOfferEntry> entries)
    {
        if (entries.Count == 0)
            return "The offer is empty.";
        if (entries.Count > MaxEntries)
            return $"The offer has {entries.Count} entries; at most {MaxEntries} are accepted.";

        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var path = entry.RelativePath;
            var problem = CheckPath(path);
            if (problem != null)
                return $"\"{Shorten(path)}\": {problem}";

            if (entry.Size < 0 || (entry.IsDirectory && entry.Size != 0))
                return $"\"{Shorten(path)}\" has an invalid size.";

            // Parents must come first, the way the offer is built: a child whose folder has not been
            // listed yet would be created by Explorer somewhere it was not announced.
            var slash = path.LastIndexOf('\\');
            if (slash > 0 && !directories.Contains(path[..slash]))
                return $"\"{Shorten(path)}\" comes before its folder.";

            if (entry.IsDirectory)
                directories.Add(path);
        }
        return null;
    }

    /// <summary>Returns null when a relative path is safe, otherwise why not.</summary>
    public static string? CheckPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return "empty name";
        if (path.Length > MaxPathLength)
            return $"longer than {MaxPathLength} characters";
        if (path.Contains('/'))
            return "forward slash in name";
        if (path.StartsWith('\\'))
            return "rooted path";

        foreach (var part in path.Split('\\'))
        {
            if (part.Length == 0)
                return "empty folder name";
            if (part is "." or "..")
                return "relative folder reference";
            if (part.EndsWith(' ') || part.EndsWith('.'))
                return "name ends with a space or dot";
            foreach (var c in part)
            {
                if (c < 32 || c is ':' or '*' or '?' or '"' or '<' or '>' or '|')
                    return "character not allowed in a file name";
            }

            var stem = part.Split('.')[0].TrimEnd(' ');
            if (ReservedNames.Contains(stem))
                return "reserved device name";
        }
        return null;
    }

    private static string Shorten(string path) => path.Length <= 60 ? path : path[..57] + "...";
}
