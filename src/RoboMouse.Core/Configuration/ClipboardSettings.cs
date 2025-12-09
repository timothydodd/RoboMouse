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
    /// Whether to sync file paths/references.
    /// </summary>
    public bool SyncFiles { get; set; } = false;
}
