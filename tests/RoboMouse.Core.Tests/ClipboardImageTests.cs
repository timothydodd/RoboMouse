using RoboMouse.Core.Input;
using Xunit;

namespace RoboMouse.Core.Tests;

public class ClipboardImageTests
{
    /// <summary>A 2x2 24-bit BITMAPINFOHEADER DIB: rows are padded to 4 bytes, so each row is 8 bytes.</summary>
    private static byte[] SampleDib()
    {
        var dib = new byte[40 + 16];
        BitConverter.TryWriteBytes(dib.AsSpan(0), 40);        // biSize
        BitConverter.TryWriteBytes(dib.AsSpan(4), 2);         // biWidth
        BitConverter.TryWriteBytes(dib.AsSpan(8), 2);         // biHeight
        BitConverter.TryWriteBytes(dib.AsSpan(12), (ushort)1);
        BitConverter.TryWriteBytes(dib.AsSpan(14), (ushort)24);
        for (var i = 40; i < dib.Length; i++)
            dib[i] = (byte)i;
        return dib;
    }

    [Fact]
    public void DibToBmp_PrependsFileHeaderPointingAtPixels()
    {
        var dib = SampleDib();
        var bmp = ClipboardManager.DibToBmp(dib);

        Assert.Equal((byte)'B', bmp[0]);
        Assert.Equal((byte)'M', bmp[1]);
        Assert.Equal(bmp.Length, BitConverter.ToInt32(bmp, 2));
        Assert.Equal(14 + 40, BitConverter.ToInt32(bmp, 10)); // no colour table for 24-bit
        Assert.Equal(dib, bmp[14..]);
    }

    [Fact]
    public void DibToBmp_AccountsForColourTableAndMasks()
    {
        var eightBit = SampleDib();
        BitConverter.TryWriteBytes(eightBit.AsSpan(14), (ushort)8);
        BitConverter.TryWriteBytes(eightBit.AsSpan(32), 16u); // biClrUsed
        Assert.Equal(14 + 40 + 16 * 4, BitConverter.ToInt32(ClipboardManager.DibToBmp(eightBit), 10));

        var bitfields = SampleDib();
        BitConverter.TryWriteBytes(bitfields.AsSpan(14), (ushort)32);
        BitConverter.TryWriteBytes(bitfields.AsSpan(16), 3u);  // BI_BITFIELDS
        Assert.Equal(14 + 40 + 12, BitConverter.ToInt32(ClipboardManager.DibToBmp(bitfields), 10));
    }

    [Fact]
    public void BmpToDib_RoundTrips()
    {
        var dib = SampleDib();
        Assert.Equal(dib, ClipboardManager.BmpToDib(ClipboardManager.DibToBmp(dib)));
    }

    [Fact]
    public void BmpToDib_RejectsNonBitmaps()
    {
        Assert.Null(ClipboardManager.BmpToDib(new byte[10]));
        Assert.Null(ClipboardManager.BmpToDib(System.Text.Encoding.ASCII.GetBytes("PNG" + new string('x', 60))));
    }
}
