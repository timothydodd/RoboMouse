using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using RoboMouse.Core.Input;
using RoboMouse.Core.Logging;

namespace RoboMouse.App;

/// <summary>
/// PNG/DIB conversion for clipboard images, done with Avalonia's bitmap codec so the core needs no
/// image library of its own. Handles the 24- and 32-bit uncompressed DIBs applications put on the
/// clipboard; anything exotic is declined and the caller falls back to sending the DIB as-is.
/// </summary>
internal sealed unsafe class AvaloniaImageCodec : IClipboardImageCodec
{
    private const int HeaderSize = 40;   // BITMAPINFOHEADER
    private const uint BI_RGB = 0;
    private const uint BI_BITFIELDS = 3;

    public byte[]? DibToPng(ReadOnlySpan<byte> dib)
    {
        try
        {
            if (dib.Length < HeaderSize)
                return null;

            var headerLength = BitConverter.ToInt32(dib);
            var width = BitConverter.ToInt32(dib[4..]);
            var height = BitConverter.ToInt32(dib[8..]);
            var bitCount = BitConverter.ToUInt16(dib[14..]);
            var compression = BitConverter.ToUInt32(dib[16..]);
            if (headerLength < HeaderSize || width <= 0 || height == 0 || (bitCount != 24 && bitCount != 32))
                return null;
            if (compression != BI_RGB && !(compression == BI_BITFIELDS && bitCount == 32))
                return null;

            var topDown = height < 0;
            var rows = Math.Abs(height);
            var pixelOffset = headerLength + (compression == BI_BITFIELDS && headerLength == HeaderSize ? 12 : 0);
            var bytesPerPixel = bitCount / 8;
            var sourceStride = (width * bytesPerPixel + 3) & ~3;
            if (dib.Length < pixelOffset + (long)sourceStride * rows)
                return null;

            // Blue/green/red masks other than the standard BGRA layout are not handled.
            if (compression == BI_BITFIELDS)
            {
                var maskOffset = headerLength == HeaderSize ? HeaderSize : 40;
                var red = BitConverter.ToUInt32(dib[maskOffset..]);
                var green = BitConverter.ToUInt32(dib[(maskOffset + 4)..]);
                var blue = BitConverter.ToUInt32(dib[(maskOffset + 8)..]);
                if (red != 0x00FF0000 || green != 0x0000FF00 || blue != 0x000000FF)
                    return null;
            }

            using var bitmap = new WriteableBitmap(new PixelSize(width, rows), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            using (var fb = bitmap.Lock())
            {
                var hasAlpha = false;
                var dest = (byte*)fb.Address;
                for (var y = 0; y < rows; y++)
                {
                    var sourceRow = topDown ? y : rows - 1 - y;
                    var src = dib.Slice(pixelOffset + sourceRow * sourceStride, width * bytesPerPixel);
                    var row = dest + y * fb.RowBytes;
                    if (bytesPerPixel == 4)
                    {
                        src.CopyTo(new Span<byte>(row, width * 4));
                        for (var x = 3; x < width * 4; x += 4)
                            if (row[x] != 0) { hasAlpha = true; }
                    }
                    else
                    {
                        for (var x = 0; x < width; x++)
                        {
                            row[x * 4 + 0] = src[x * 3 + 0];
                            row[x * 4 + 1] = src[x * 3 + 1];
                            row[x * 4 + 2] = src[x * 3 + 2];
                            row[x * 4 + 3] = 255;
                        }
                        hasAlpha = true;
                    }
                }

                // 32-bit DIBs frequently carry a zero alpha channel that means "opaque".
                if (!hasAlpha)
                {
                    for (var y = 0; y < rows; y++)
                    {
                        var row = dest + y * fb.RowBytes;
                        for (var x = 3; x < width * 4; x += 4)
                            row[x] = 255;
                    }
                }
            }

            using var stream = new MemoryStream();
            bitmap.Save(stream, PngBitmapEncoderOptions.Default);
            return stream.ToArray();
        }
        catch (Exception ex)
        {
            SimpleLogger.Log("Clipboard", $"DIB to PNG failed: {ex.Message}");
            return null;
        }
    }

    public byte[]? PngToDib(ReadOnlySpan<byte> png)
    {
        try
        {
            using var source = new MemoryStream(png.ToArray());
            using var decoded = new Bitmap(source);
            var size = decoded.PixelSize;
            if (size.Width <= 0 || size.Height <= 0)
                return null;

            using var bitmap = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            using var fb = bitmap.Lock();
            decoded.CopyPixels(fb);
            if (fb.Format != PixelFormat.Bgra8888)
                return null;

            var stride = size.Width * 4;
            var dib = new byte[HeaderSize + stride * size.Height];
            BitConverter.TryWriteBytes(dib.AsSpan(0), HeaderSize);          // biSize
            BitConverter.TryWriteBytes(dib.AsSpan(4), size.Width);          // biWidth
            BitConverter.TryWriteBytes(dib.AsSpan(8), size.Height);         // biHeight (positive: bottom-up)
            BitConverter.TryWriteBytes(dib.AsSpan(12), (ushort)1);          // biPlanes
            BitConverter.TryWriteBytes(dib.AsSpan(14), (ushort)32);         // biBitCount
            BitConverter.TryWriteBytes(dib.AsSpan(16), BI_RGB);             // biCompression
            BitConverter.TryWriteBytes(dib.AsSpan(20), stride * size.Height); // biSizeImage
            BitConverter.TryWriteBytes(dib.AsSpan(24), 2835);               // biXPelsPerMeter (72 dpi)
            BitConverter.TryWriteBytes(dib.AsSpan(28), 2835);               // biYPelsPerMeter

            var src = (byte*)fb.Address;
            for (var y = 0; y < size.Height; y++)
            {
                var row = new ReadOnlySpan<byte>(src + y * fb.RowBytes, stride);
                row.CopyTo(dib.AsSpan(HeaderSize + (size.Height - 1 - y) * stride, stride));
            }
            return dib;
        }
        catch (Exception ex)
        {
            SimpleLogger.Log("Clipboard", $"PNG to DIB failed: {ex.Message}");
            return null;
        }
    }
}
