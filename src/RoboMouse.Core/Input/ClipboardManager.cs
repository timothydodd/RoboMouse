using System.Runtime.InteropServices;
using System.Text;
using RoboMouse.Core.Logging;
using RoboMouse.Core.Network.Protocol;

namespace RoboMouse.Core.Input;

/// <summary>
/// Converts between PNG and a Windows DIB (the CF_DIB clipboard format). The core has no image
/// codec of its own; the app supplies one so images can be offered in both formats.
/// </summary>
public interface IClipboardImageCodec
{
    /// <summary>Encodes a CF_DIB (BITMAPINFOHEADER followed by pixels) as PNG, or null if it cannot.</summary>
    byte[]? DibToPng(ReadOnlySpan<byte> dib);

    /// <summary>Decodes PNG into a CF_DIB (BITMAPINFOHEADER followed by pixels), or null if it cannot.</summary>
    byte[]? PngToDib(ReadOnlySpan<byte> png);
}

/// <summary>
/// Manages clipboard monitoring and synchronization through the Win32 clipboard API.
/// </summary>
public sealed unsafe class ClipboardManager : IDisposable
{
    private readonly MessageWindow _window;
    private readonly StaWorker _fileSta;
    private bool _monitoring;
    private bool _disposed;
    private volatile bool _ignoreNextChange;
    private string? _lastTextHash;
    private readonly long _maxDataSize;
    private VirtualFileDataObject? _virtualFiles;
    private int _fileScanVersion;

    private static readonly uint CfPng = NativeMethods.RegisterClipboardFormatW("PNG");
    private static readonly uint CfHtml = NativeMethods.RegisterClipboardFormatW("HTML Format");
    private static readonly uint CfRtf = NativeMethods.RegisterClipboardFormatW("Rich Text Format");

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

    /// <summary>Optional PNG/DIB converter so images can be read from and written to the clipboard in both forms.</summary>
    public IClipboardImageCodec? ImageCodec { get; set; }

    public ClipboardManager(long maxDataSize = 10 * 1024 * 1024)
    {
        _maxDataSize = maxDataSize;
        _window = new MessageWindow();
        _window.Message += OnWindowMessage;
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
        if (_monitoring || _disposed)
            return;
        NativeMethods.AddClipboardFormatListener(_window.Handle);
        _monitoring = true;
    }

    /// <summary>
    /// Stops monitoring clipboard changes.
    /// </summary>
    public void Stop()
    {
        if (!_monitoring)
            return;
        NativeMethods.RemoveClipboardFormatListener(_window.Handle);
        _monitoring = false;
    }

    /// <summary>
    /// Sets the clipboard content from a received message. Safe to call from any thread: the work is
    /// marshalled to the thread that owns the notification window so change notifications stay ordered.
    /// </summary>
    public void SetClipboard(ClipboardMessage message)
    {
        if (_disposed)
            return;

        if (!_window.IsOwnerThread)
        {
            _window.BeginInvoke(() => SetClipboard(message));
            return;
        }

        _ignoreNextChange = true;

        try
        {
            switch (message.ContentType)
            {
                case ClipboardContentType.Text:
                    _lastTextHash = HashOf(message.Data);
                    SetClipboardText(Encoding.UTF8.GetString(message.Data));
                    break;

                case ClipboardContentType.Image:
                    SetClipboardImage(message.Data, message.FormatHint);
                    break;

                case ClipboardContentType.Html:
                    SetClipboardHtml(Encoding.UTF8.GetString(message.Data));
                    break;

                case ClipboardContentType.Rtf:
                    SetClipboardRtf(Encoding.UTF8.GetString(message.Data));
                    break;

                default:
                    _ignoreNextChange = false;
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

    private void OnWindowMessage(uint msg, nint wParam, nint lParam)
    {
        if (msg == NativeMethods.WM_CLIPBOARDUPDATE)
            OnClipboardUpdated();
    }

    private void OnClipboardUpdated()
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
            if (ShareFiles && NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_HDROP))
            {
                var paths = ReadFileDropList();
                if (paths.Count > 0)
                {
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

    #region Reading

    private ClipboardMessage? GetClipboardContent()
    {
        var hasText = NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_UNICODETEXT);
        var hasImage = NativeMethods.IsClipboardFormatAvailable(CfPng)
                       || NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_DIB);
        if (!hasText && !hasImage)
            return null;

        using var clipboard = OpenClipboard();
        if (!clipboard.IsOpen)
            return null;

        // Plain text is what every application can paste, so it is what we send. Rich formats
        // (HTML, RTF) would arrive without a plain-text fallback and be unpastable in most fields.
        if (hasText)
        {
            var text = ReadUnicodeText();
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

        if (hasImage)
        {
            // Prefer a PNG the source application already produced (browsers, Office, screenshot tools).
            var png = ReadFormatBytes(CfPng);
            if (png == null)
            {
                var dib = ReadFormatBytes(NativeMethods.CF_DIB);
                if (dib != null)
                {
                    png = ImageCodec?.DibToPng(dib);
                    if (png == null)
                    {
                        // No codec: ship the DIB wrapped as a .bmp so the other side can at least paste it.
                        var bmp = DibToBmp(dib);
                        if (bmp.Length <= _maxDataSize)
                        {
                            return new ClipboardMessage
                            {
                                ContentType = ClipboardContentType.Image,
                                Data = bmp,
                                FormatHint = "image/bmp"
                            };
                        }
                        return null;
                    }
                }
            }

            if (png != null && png.Length <= _maxDataSize)
            {
                return new ClipboardMessage
                {
                    ContentType = ClipboardContentType.Image,
                    Data = png,
                    FormatHint = "image/png"
                };
            }
        }

        return null;
    }

    /// <summary>Reads CF_HDROP. The clipboard must not be open already.</summary>
    private static List<string> ReadFileDropList()
    {
        var paths = new List<string>();
        using var clipboard = OpenClipboard();
        if (!clipboard.IsOpen)
            return paths;

        var hDrop = NativeMethods.GetClipboardData(NativeMethods.CF_HDROP);
        if (hDrop == 0)
            return paths;

        var count = NativeMethods.DragQueryFileW(hDrop, 0xFFFFFFFF, null, 0);
        for (uint i = 0; i < count; i++)
        {
            var length = NativeMethods.DragQueryFileW(hDrop, i, null, 0);
            if (length == 0)
                continue;
            var buffer = new char[length + 1];
            fixed (char* p = buffer)
            {
                var written = NativeMethods.DragQueryFileW(hDrop, i, p, length + 1);
                if (written > 0)
                    paths.Add(new string(p, 0, (int)written));
            }
        }
        return paths;
    }

    private static string? ReadUnicodeText()
    {
        var handle = NativeMethods.GetClipboardData(NativeMethods.CF_UNICODETEXT);
        if (handle == 0)
            return null;
        var ptr = NativeMethods.GlobalLock(handle);
        if (ptr == 0)
            return null;
        try
        {
            // Bounded by the allocation so a missing terminator cannot run off the end.
            var maxChars = (int)Math.Min(int.MaxValue / 2, (long)NativeMethods.GlobalSize(handle) / 2);
            var span = new ReadOnlySpan<char>((char*)ptr, maxChars);
            var end = span.IndexOf('\0');
            return end >= 0 ? new string(span[..end]) : new string(span);
        }
        finally
        {
            NativeMethods.GlobalUnlock(handle);
        }
    }

    private static byte[]? ReadFormatBytes(uint format)
    {
        if (!NativeMethods.IsClipboardFormatAvailable(format))
            return null;
        var handle = NativeMethods.GetClipboardData(format);
        if (handle == 0)
            return null;
        var ptr = NativeMethods.GlobalLock(handle);
        if (ptr == 0)
            return null;
        try
        {
            var size = (long)NativeMethods.GlobalSize(handle);
            if (size <= 0 || size > int.MaxValue)
                return null;
            return new ReadOnlySpan<byte>((void*)ptr, (int)size).ToArray();
        }
        finally
        {
            NativeMethods.GlobalUnlock(handle);
        }
    }

    #endregion

    #region Writing

    private void SetClipboardText(string text)
    {
        WithClipboard(() =>
        {
            NativeMethods.EmptyClipboard();
            PutBytes(NativeMethods.CF_UNICODETEXT, Encoding.Unicode.GetBytes(text + "\0"));
        });
    }

    private void SetClipboardImage(byte[] imageData, string? formatHint)
    {
        byte[]? png = null;
        byte[]? dib = null;

        if (string.Equals(formatHint, "image/bmp", StringComparison.OrdinalIgnoreCase))
        {
            dib = BmpToDib(imageData);
            if (dib != null)
                png = ImageCodec?.DibToPng(dib);
        }
        else
        {
            png = imageData;
            dib = ImageCodec?.PngToDib(imageData);
        }

        if (png == null && dib == null)
            throw new InvalidOperationException("The image could not be converted for the clipboard.");

        WithClipboard(() =>
        {
            NativeMethods.EmptyClipboard();
            if (dib != null)
                PutBytes(NativeMethods.CF_DIB, dib);
            if (png != null)
                PutBytes(CfPng, png);
        });
    }

    private void SetClipboardHtml(string html)
    {
        WithClipboard(() =>
        {
            NativeMethods.EmptyClipboard();
            PutBytes(CfHtml, BuildCfHtml(html));
        });
    }

    private void SetClipboardRtf(string rtf)
    {
        WithClipboard(() =>
        {
            NativeMethods.EmptyClipboard();
            PutBytes(CfRtf, Encoding.Latin1.GetBytes(rtf + "\0"));
        });
    }

    /// <summary>Wraps an HTML fragment in the header the "HTML Format" clipboard format requires.</summary>
    private static byte[] BuildCfHtml(string html)
    {
        const string headerTemplate =
            "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        const string prefix = "<html><body>\r\n<!--StartFragment-->";
        const string suffix = "<!--EndFragment-->\r\n</body></html>";

        var headerLength = Encoding.UTF8.GetByteCount(string.Format(headerTemplate, 0, 0, 0, 0));
        var prefixLength = Encoding.UTF8.GetByteCount(prefix);
        var fragmentLength = Encoding.UTF8.GetByteCount(html);
        var suffixLength = Encoding.UTF8.GetByteCount(suffix);

        var startHtml = headerLength;
        var startFragment = startHtml + prefixLength;
        var endFragment = startFragment + fragmentLength;
        var endHtml = endFragment + suffixLength;

        var text = string.Format(headerTemplate, startHtml, endHtml, startFragment, endFragment) + prefix + html + suffix + "\0";
        return Encoding.UTF8.GetBytes(text);
    }

    private static void PutBytes(uint format, ReadOnlySpan<byte> bytes)
    {
        var handle = NativeMethods.GlobalAlloc(NativeMethods.GHND, (nuint)bytes.Length);
        if (handle == 0)
            throw new OutOfMemoryException("GlobalAlloc failed.");
        var ptr = NativeMethods.GlobalLock(handle);
        if (ptr == 0)
        {
            NativeMethods.GlobalFree(handle);
            throw new InvalidOperationException("GlobalLock failed.");
        }
        bytes.CopyTo(new Span<byte>((void*)ptr, bytes.Length));
        NativeMethods.GlobalUnlock(handle);

        if (NativeMethods.SetClipboardData(format, handle) == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            NativeMethods.GlobalFree(handle);
            throw new InvalidOperationException($"SetClipboardData failed. Error code: {error}");
        }
        // On success the system owns the memory.
    }

    private void WithClipboard(Action operation, int retries = 3)
    {
        for (int i = 0; i < retries; i++)
        {
            using var clipboard = OpenClipboard(_window.Handle);
            if (clipboard.IsOpen)
            {
                operation();
                return;
            }
            if (i == retries - 1)
                throw new InvalidOperationException("The clipboard is in use by another application.");
            Thread.Sleep(100);
        }
    }

    #endregion

    #region DIB / BMP

    /// <summary>Prepends a BITMAPFILEHEADER so a CF_DIB becomes a .bmp file image.</summary>
    internal static byte[] DibToBmp(ReadOnlySpan<byte> dib)
    {
        var pixelOffset = 14 + DibPixelDataOffset(dib);
        var bmp = new byte[14 + dib.Length];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BitConverter.TryWriteBytes(bmp.AsSpan(2), bmp.Length);
        BitConverter.TryWriteBytes(bmp.AsSpan(10), pixelOffset);
        dib.CopyTo(bmp.AsSpan(14));
        return bmp;
    }

    /// <summary>Strips the BITMAPFILEHEADER from a .bmp file image, giving a CF_DIB.</summary>
    internal static byte[]? BmpToDib(ReadOnlySpan<byte> bmp)
    {
        if (bmp.Length < 14 + 40 || bmp[0] != 'B' || bmp[1] != 'M')
            return null;
        return bmp[14..].ToArray();
    }

    /// <summary>Byte offset of the pixel array within a DIB: header, then colour masks/table.</summary>
    private static int DibPixelDataOffset(ReadOnlySpan<byte> dib)
    {
        var headerSize = BitConverter.ToInt32(dib);
        var bitCount = BitConverter.ToUInt16(dib[14..]);
        var compression = BitConverter.ToUInt32(dib[16..]);
        var colorsUsed = BitConverter.ToUInt32(dib[32..]);

        var offset = headerSize;
        const uint BI_BITFIELDS = 3;
        if (compression == BI_BITFIELDS && headerSize == 40)
            offset += 12; // three DWORD masks follow a BITMAPINFOHEADER
        if (bitCount <= 8)
            offset += (int)(colorsUsed != 0 ? colorsUsed : 1u << bitCount) * 4;
        return offset;
    }

    #endregion

    /// <summary>Opens the clipboard, retrying briefly while another application holds it.</summary>
    private static ClipboardScope OpenClipboard(nint owner = 0)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (NativeMethods.OpenClipboard(owner))
                return new ClipboardScope(true);
            Thread.Sleep(10);
        }
        return new ClipboardScope(false);
    }

    private readonly struct ClipboardScope : IDisposable
    {
        public bool IsOpen { get; }
        public ClipboardScope(bool isOpen) => IsOpen = isOpen;
        public void Dispose()
        {
            if (IsOpen)
                NativeMethods.CloseClipboard();
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
        Stop();
        _window.Message -= OnWindowMessage;
        _window.Dispose();
    }
}
