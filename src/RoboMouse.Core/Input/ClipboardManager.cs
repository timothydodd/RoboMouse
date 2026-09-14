using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using RoboMouse.Core.Logging;
using RoboMouse.Core.Network.Protocol;

namespace RoboMouse.Core.Input;

/// <summary>
/// Manages clipboard monitoring and synchronization.
/// </summary>
public sealed class ClipboardManager : IDisposable
{
    private readonly ClipboardNotificationForm _notificationForm;
    private readonly StaWorker _fileSta;
    private bool _disposed;
    private volatile bool _ignoreNextChange;
    private string? _lastTextHash;
    private readonly long _maxDataSize;
    private VirtualFileDataObject? _virtualFiles;
    private int _fileScanVersion;

    /// <summary>
    /// Event raised when the clipboard content changes.
    /// </summary>
    public event EventHandler<ClipboardMessage>? ClipboardChanged;

    /// <summary>
    /// Raised (on a background thread) when files were copied locally and are ready to be offered.
    /// </summary>
    public event EventHandler<FileOfferSource>? FilesCopied;

    /// <summary>
    /// Raised when the local clipboard no longer holds the files last offered.
    /// </summary>
    public event EventHandler? FilesCleared;

    /// <summary>Whether local file copies are turned into offers.</summary>
    public bool ShareFiles { get; set; } = true;

    public ClipboardManager(long maxDataSize = 10 * 1024 * 1024)
    {
        _maxDataSize = maxDataSize;
        _notificationForm = new ClipboardNotificationForm();
        _notificationForm.ClipboardUpdated += OnClipboardUpdated;
        _fileSta = new StaWorker("RoboMouse-ClipboardFiles");
    }

    #region Virtual files (receiving side)

    /// <summary>
    /// Puts remote files on the clipboard as virtual files. Applications that paste them read each
    /// file through <paramref name="read"/>, which pulls bytes from the remote on demand.
    /// </summary>
    public void SetVirtualFiles(FileOfferMessage offer, VirtualFileDataObject.ReadRange read)
    {
        if (_disposed)
            return;

        _fileSta.BeginInvoke(() =>
        {
            try
            {
                var dataObject = new VirtualFileDataObject(offer.OfferId, offer.Entries, read);
                _ignoreNextChange = true;
                dataObject.SetOnClipboard();
                _virtualFiles = dataObject;
                SimpleLogger.Log("Files", $"Clipboard now offers {offer.Entries.Count} remote item(s), {offer.TotalSize / 1024.0 / 1024.0:0.#} MB");
            }
            catch (Exception ex)
            {
                _ignoreNextChange = false;
                SimpleLogger.Log("Files", $"Could not place remote files on the clipboard: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Empties the clipboard if it still holds the given offer (or any remote offer when null).
    /// </summary>
    public void ClearVirtualFiles(string? offerId)
    {
        if (_disposed)
            return;

        _fileSta.BeginInvoke(() =>
        {
            var current = _virtualFiles;
            if (current == null || (offerId != null && current.OfferId != offerId))
                return;

            try
            {
                if (current.IsOnClipboard())
                {
                    _ignoreNextChange = true;
                    current.RemoveFromClipboard();
                }
            }
            catch (Exception ex)
            {
                _ignoreNextChange = false;
                SimpleLogger.Log("Files", $"Could not clear remote files from the clipboard: {ex.Message}");
            }
            _virtualFiles = null;
        });
    }

    #endregion

    /// <summary>
    /// Starts monitoring clipboard changes.
    /// </summary>
    public void Start()
    {
        _notificationForm.StartMonitoring();
    }

    /// <summary>
    /// Stops monitoring clipboard changes.
    /// </summary>
    public void Stop()
    {
        _notificationForm.StopMonitoring();
    }

    /// <summary>
    /// Sets the clipboard content from a received message. Safe to call from any thread: the
    /// clipboard can only be touched from the STA thread that owns the notification window.
    /// </summary>
    public void SetClipboard(ClipboardMessage message)
    {
        if (_disposed)
            return;

        if (_notificationForm.InvokeRequired)
        {
            _notificationForm.BeginInvoke(() => SetClipboard(message));
            return;
        }

        _ignoreNextChange = true;

        try
        {
            switch (message.ContentType)
            {
                case ClipboardContentType.Text:
                    var text = Encoding.UTF8.GetString(message.Data);
                    _lastTextHash = HashOf(message.Data);
                    SetClipboardText(text);
                    break;

                case ClipboardContentType.Image:
                    SetClipboardImage(message.Data);
                    break;

                case ClipboardContentType.Html:
                    var html = Encoding.UTF8.GetString(message.Data);
                    SetClipboardHtml(html);
                    break;

                case ClipboardContentType.Rtf:
                    var rtf = Encoding.UTF8.GetString(message.Data);
                    SetClipboardRtf(rtf);
                    break;
            }
        }
        catch (Exception ex)
        {
            // Nothing was set, so no change notification will arrive to consume the flag.
            _ignoreNextChange = false;
            SimpleLogger.Log("Clipboard", $"Failed to set clipboard: {ex.Message}");
        }
    }

    private static string HashOf(byte[] data) =>
        Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(data));

    private void OnClipboardUpdated(object? sender, EventArgs e)
    {
        if (_ignoreNextChange)
        {
            _ignoreNextChange = false;
            return;
        }

        // Files: announce them (names only) after enumerating on a background thread, since a large
        // folder can take a while to walk and this runs on the UI thread.
        var scan = ++_fileScanVersion;
        try
        {
            if (ShareFiles && Clipboard.ContainsFileDropList())
            {
                var paths = Clipboard.GetFileDropList().Cast<string>().ToList();
                Task.Run(() =>
                {
                    var offer = FileOfferSource.FromPaths(paths);
                    if (scan != _fileScanVersion)
                        return; // Clipboard changed again while we were scanning
                    if (offer == null)
                    {
                        SimpleLogger.Log("Files", "Copied files not offered (nothing readable, or more than the entry limit)");
                        FilesCleared?.Invoke(this, EventArgs.Empty);
                        return;
                    }
                    FilesCopied?.Invoke(this, offer);
                });
                return;
            }
        }
        catch (Exception ex)
        {
            SimpleLogger.Log("Files", $"Failed to read copied files: {ex.Message}");
        }

        FilesCleared?.Invoke(this, EventArgs.Empty);

        try
        {
            var message = GetClipboardContent();
            if (message != null)
            {
                // Check for duplicate text content
                if (message.ContentType == ClipboardContentType.Text)
                {
                    var hash = HashOf(message.Data);
                    if (hash == _lastTextHash)
                        return;
                    _lastTextHash = hash;
                }

                ClipboardChanged?.Invoke(this, message);
            }
        }
        catch (Exception ex)
        {
            SimpleLogger.Log("Clipboard", $"Failed to read clipboard: {ex.Message}");
        }
    }

    private ClipboardMessage? GetClipboardContent()
    {
        if (!Clipboard.ContainsText() && !Clipboard.ContainsImage())
            return null;

        // Plain text is what every application can paste, so it is what we send. Rich formats
        // (HTML, RTF) would arrive without a plain-text fallback and be unpastable in most fields.
        if (Clipboard.ContainsText())
        {
            var text = Clipboard.GetText();
            if (!string.IsNullOrEmpty(text))
            {
                var data = Encoding.UTF8.GetBytes(text);
                if (data.Length <= _maxDataSize)
                {
                    return new ClipboardMessage
                    {
                        ContentType = ClipboardContentType.Text,
                        Data = data,
                        FormatHint = "text/plain"
                    };
                }
            }
        }

        // Try image
        if (Clipboard.ContainsImage())
        {
            var image = Clipboard.GetImage();
            if (image != null)
            {
                using var ms = new MemoryStream();
                image.Save(ms, ImageFormat.Png);
                var data = ms.ToArray();

                if (data.Length <= _maxDataSize)
                {
                    return new ClipboardMessage
                    {
                        ContentType = ClipboardContentType.Image,
                        Data = data,
                        FormatHint = "image/png"
                    };
                }
            }
        }

        return null;
    }

    private static void SetClipboardText(string text)
    {
        RetryClipboardOperation(() =>
        {
            Clipboard.SetText(text);
        });
    }

    private static void SetClipboardImage(byte[] imageData)
    {
        using var ms = new MemoryStream(imageData);
        using var image = Image.FromStream(ms);
        RetryClipboardOperation(() =>
        {
            Clipboard.SetImage(image);
        });
    }

    private static void SetClipboardHtml(string html)
    {
        RetryClipboardOperation(() =>
        {
            Clipboard.SetText(html, TextDataFormat.Html);
        });
    }

    private static void SetClipboardRtf(string rtf)
    {
        RetryClipboardOperation(() =>
        {
            Clipboard.SetText(rtf, TextDataFormat.Rtf);
        });
    }

    private static void RetryClipboardOperation(Action operation, int retries = 3)
    {
        for (int i = 0; i < retries; i++)
        {
            try
            {
                operation();
                return;
            }
            catch (ExternalException)
            {
                if (i == retries - 1)
                    throw;
                Thread.Sleep(100);
            }
            catch (System.Threading.ThreadStateException)
            {
                throw new InvalidOperationException("Clipboard access must happen on the UI thread.");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        try
        {
            _fileSta.Invoke(() => _virtualFiles?.RemoveFromClipboard());
        }
        catch { }
        _fileSta.Dispose();
        _notificationForm.Dispose();
    }
}

/// <summary>
/// Hidden form to receive clipboard notifications.
/// </summary>
internal class ClipboardNotificationForm : Form
{
    private bool _monitoring;

    public event EventHandler? ClipboardUpdated;

    public ClipboardNotificationForm()
    {
        // Create a hidden window
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        Size = new Size(1, 1);
        Location = new Point(-1000, -1000);

        // Create the handle now so InvokeRequired/BeginInvoke work before monitoring starts.
        CreateHandle();
    }

    public void StartMonitoring()
    {
        if (_monitoring)
            return;

        if (!IsHandleCreated)
        {
            CreateHandle();
        }

        NativeMethods.AddClipboardFormatListener(Handle);
        _monitoring = true;
    }

    public void StopMonitoring()
    {
        if (!_monitoring)
            return;

        if (IsHandleCreated)
        {
            NativeMethods.RemoveClipboardFormatListener(Handle);
        }
        _monitoring = false;
    }

    protected override void WndProc(ref System.Windows.Forms.Message m)
    {
        if (m.Msg == NativeMethods.WM_CLIPBOARDUPDATE)
        {
            ClipboardUpdated?.Invoke(this, EventArgs.Empty);
        }

        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopMonitoring();
        }
        base.Dispose(disposing);
    }
}
