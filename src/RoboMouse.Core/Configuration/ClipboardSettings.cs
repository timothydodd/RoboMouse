using RoboMouse.Core.Network.Protocol;

namespace RoboMouse.Core.Configuration;

/// <summary>
/// Configuration for clipboard synchronization.
/// </summary>
public class ClipboardSettings
{
    /// <summary>
    /// Whether clipboard sharing is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Maximum size in bytes for clipboard data transfer.
    /// Default is 10MB.
    /// </summary>
    public long MaxSizeBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>
    /// Whether to sync text content.
    /// </summary>
    public bool SyncText { get; set; } = true;

    /// <summary>
    /// Whether to sync image content.
    /// </summary>
    public bool SyncImages { get; set; } = true;

    /// <summary>
    /// Whether files copied on one machine can be pasted on another. Only names are shared at copy
    /// time; bytes stream from the source machine when the paste happens.
    /// </summary>
    public bool SyncFiles { get; set; } = true;

    /// <summary>
    /// Whether content of this type and size may be shared, in either direction: sharing is on, the
    /// type's switch is on (text covers HTML and RTF), and it is no bigger than <see cref="MaxSizeBytes"/>.
    /// File offers only carry names, so their size is not limited here.
    /// </summary>
    public bool Allows(ClipboardContentType type, long length) =>
        Enabled && type switch
        {
            ClipboardContentType.Text or ClipboardContentType.Html or ClipboardContentType.Rtf => SyncText && length <= MaxSizeBytes,
            ClipboardContentType.Image => SyncImages && length <= MaxSizeBytes,
            ClipboardContentType.Files => SyncFiles,
            _ => false
        };
}
