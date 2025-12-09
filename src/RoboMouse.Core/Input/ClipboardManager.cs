using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using RoboMouse.Core.Network.Protocol;

namespace RoboMouse.Core.Input;

/// <summary>
/// Manages clipboard monitoring and synchronization.
/// </summary>
public sealed class ClipboardManager : IDisposable
{
    private readonly ClipboardNotificationForm _notificationForm;
    private bool _disposed;
    private bool _ignoreNextChange;
    private string? _lastTextHash;
    private readonly long _maxDataSize;

    /// <summary>
    /// Event raised when the clipboard content changes.
    /// </summary>
    public event EventHandler<ClipboardMessage>? ClipboardChanged;

    public ClipboardManager(long maxDataSize = 10 * 1024 * 1024)
    {
        _maxDataSize = maxDataSize;
        _notificationForm = new ClipboardNotificationForm();
        _notificationForm.ClipboardUpdated += OnClipboardUpdated;
    }

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
    /// Sets the clipboard content from a received message.
    /// </summary>
    public void SetClipboard(ClipboardMessage message)
    {
        if (_disposed)
            return;

        _ignoreNextChange = true;

        try
        {
            switch (message.ContentType)
            {
                case ClipboardContentType.Text:
                    var text = Encoding.UTF8.GetString(message.Data);
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
            System.Diagnostics.Debug.WriteLine($"Failed to set clipboard: {ex.Message}");
        }
    }

    private void OnClipboardUpdated(object? sender, EventArgs e)
    {
        if (_ignoreNextChange)
        {
            _ignoreNextChange = false;
            return;
        }

        try
        {
            var message = GetClipboardContent();
            if (message != null)
            {
                // Check for duplicate text content
                if (message.ContentType == ClipboardContentType.Text)
                {
                    var hash = Convert.ToBase64String(
                        System.Security.Cryptography.SHA256.HashData(message.Data));
                    if (hash == _lastTextHash)
                        return;
                    _lastTextHash = hash;
                }

                ClipboardChanged?.Invoke(this, message);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to read clipboard: {ex.Message}");
        }
    }

    private ClipboardMessage? GetClipboardContent()
    {
        if (!Clipboard.ContainsText() && !Clipboard.ContainsImage())
            return null;

        // Try text first
        if (Clipboard.ContainsText())
        {
            var text = Clipboard.GetText();
            if (!string.IsNullOrEmpty(text))
            {
                var data = Encoding.UTF8.GetBytes(text);
                if (data.Length <= _maxDataSize)
                {
                    // Check for HTML format
                    if (Clipboard.ContainsText(TextDataFormat.Html))
                    {
                        var html = Clipboard.GetText(TextDataFormat.Html);
                        var htmlData = Encoding.UTF8.GetBytes(html);
                        if (htmlData.Length <= _maxDataSize)
                        {
                            return new ClipboardMessage
                            {
                                ContentType = ClipboardContentType.Html,
                                Data = htmlData,
                                FormatHint = "text/html"
                            };
                        }
                    }

                    // Check for RTF format
                    if (Clipboard.ContainsText(TextDataFormat.Rtf))
                    {
                        var rtf = Clipboard.GetText(TextDataFormat.Rtf);
                        var rtfData = Encoding.UTF8.GetBytes(rtf);
                        if (rtfData.Length <= _maxDataSize)
                        {
                            return new ClipboardMessage
                            {
                                ContentType = ClipboardContentType.Rtf,
                                Data = rtfData,
                                FormatHint = "text/rtf"
                            };
                        }
                    }

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
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
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
