using RoboMouse.Core.Network.Protocol;

namespace RoboMouse.Core.Input;

/// <summary>
/// The local side of a file offer: the entries announced to peers plus the absolute paths they map
/// to. Absolute paths never leave this machine; peers address entries by index.
/// </summary>
public sealed class FileOfferSource
{
    /// <summary>Upper bound on entries in one offer, so copying an enormous tree cannot stall the app.</summary>
    public const int MaxEntries = 20_000;

    public string OfferId { get; } = Guid.NewGuid().ToString("N");

    public List<FileOfferEntry> Entries { get; } = new();

    /// <summary>Absolute local path for each entry, parallel to <see cref="Entries"/>.</summary>
    public List<string> LocalPaths { get; } = new();

    public long TotalSize => Entries.Sum(e => e.Size);

    /// <summary>
    /// Builds an offer from the paths on the clipboard. Directories are walked recursively. Returns
    /// null if nothing usable was found or the tree is too large.
    /// </summary>
    public static FileOfferSource? FromPaths(IEnumerable<string> paths)
    {
        var offer = new FileOfferSource();

        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                offer.Add(info.Name, info.Length, false, info.LastWriteTimeUtc, path);
            }
            else if (Directory.Exists(path))
            {
                var root = new DirectoryInfo(path);
                if (!offer.AddDirectory(root, root.Name))
                    return null;
            }

            if (offer.Entries.Count > MaxEntries)
                return null;
        }

        return offer.Entries.Count == 0 ? null : offer;
    }

    private bool AddDirectory(DirectoryInfo directory, string relative)
    {
        Add(relative, 0, true, directory.LastWriteTimeUtc, directory.FullName);

        IEnumerable<FileSystemInfo> children;
        try
        {
            children = directory.EnumerateFileSystemInfos();
        }
        catch (UnauthorizedAccessException)
        {
            return true; // Skip what we cannot read
        }

        foreach (var child in children)
        {
            if (Entries.Count > MaxEntries)
                return false;

            // Reparse points (junctions, symlinks) are skipped to avoid cycles and surprises.
            if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                continue;

            var childRelative = relative + "\\" + child.Name;
            if (child is DirectoryInfo sub)
            {
                if (!AddDirectory(sub, childRelative))
                    return false;
            }
            else if (child is FileInfo file)
            {
                Add(childRelative, file.Length, false, file.LastWriteTimeUtc, file.FullName);
            }
        }

        return true;
    }

    private void Add(string relative, long size, bool isDirectory, DateTime lastWriteUtc, string localPath)
    {
        Entries.Add(new FileOfferEntry
        {
            RelativePath = relative,
            Size = size,
            IsDirectory = isDirectory,
            LastWriteTimeUtc = lastWriteUtc
        });
        LocalPaths.Add(localPath);
    }

    public FileOfferMessage ToMessage() => new() { OfferId = OfferId, Entries = Entries };

    /// <summary>
    /// Reads a range of one entry. Returns fewer bytes than asked at end of file, or throws.
    /// </summary>
    public byte[] Read(int entryIndex, long offset, int length)
    {
        if (entryIndex < 0 || entryIndex >= Entries.Count)
            throw new ArgumentOutOfRangeException(nameof(entryIndex));
        if (Entries[entryIndex].IsDirectory)
            throw new InvalidOperationException("Entry is a directory.");
        if (offset < 0 || length < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));

        using var stream = new FileStream(LocalPaths[entryIndex], FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.None);
        if (offset >= stream.Length)
            return Array.Empty<byte>();

        stream.Position = offset;
        var buffer = new byte[(int)Math.Min(length, stream.Length - offset)];
        var got = 0;
        while (got < buffer.Length)
        {
            var read = stream.Read(buffer, got, buffer.Length - got);
            if (read == 0)
                break;
            got += read;
        }
        return got == buffer.Length ? buffer : buffer[..got];
    }
}
