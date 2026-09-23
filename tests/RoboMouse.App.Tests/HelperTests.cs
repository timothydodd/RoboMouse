using Avalonia.Input;
using RoboMouse.App.Services;
using RoboMouse.App.Views;
using RoboMouse.Core.Configuration;
using Xunit;

namespace RoboMouse.App.Tests;

public class HotkeyBoxTests
{
    [Theory]
    [InlineData(Key.M, KeyModifiers.Control | KeyModifiers.Alt, "Ctrl+Alt+M")]
    [InlineData(Key.F12, KeyModifiers.Shift, "Shift+F12")]
    [InlineData(Key.D3, KeyModifiers.Control | KeyModifiers.Meta, "Ctrl+Win+D3")]
    [InlineData(Key.OemPlus, KeyModifiers.Control, "Ctrl+Oemplus")]
    [InlineData(Key.PageDown, KeyModifiers.Alt, "Alt+PageDown")]
    public void Format_TakesModifierChords(Key key, KeyModifiers modifiers, string expected)
    {
        var text = HotkeyBox.Format(key, modifiers);

        Assert.NotNull(text);
        // Aliased key names (PageDown/Next) may print either way; what matters is that it parses back.
        Assert.Equal(Core.Input.Hotkey.Parse(expected), Core.Input.Hotkey.Parse(text));
    }

    [Theory]
    [InlineData(Key.M, KeyModifiers.None)]                        // plain typing never triggers it
    [InlineData(Key.LeftCtrl, KeyModifiers.Control)]              // modifier on its own
    [InlineData(Key.LeftAlt, KeyModifiers.Control | KeyModifiers.Alt)]
    [InlineData(Key.LWin, KeyModifiers.Meta)]
    [InlineData(Key.None, KeyModifiers.Control)]
    public void Format_IgnoresIncompleteChords(Key key, KeyModifiers modifiers)
    {
        Assert.Null(HotkeyBox.Format(key, modifiers));
    }
}

public class PeerActionsTests
{
    [Fact]
    public async Task Rejection_IsReportedInPlainWords()
    {
        var backend = new FakeBackend { ConnectError = new Core.Network.ConnectionRejectedException(Core.Network.RejectCode.AwaitingApproval, "Waiting for DESK to allow this PC") };

        Assert.Equal("Waiting for DESK to allow this PC", await PeerActions.ConnectNewPeerAsync(backend, Samples.Settings().Peers[0]));
    }

    [Fact]
    public async Task AddAndConnect_SavesUnblocksAndConnects()
    {
        var settings = Samples.Settings();
        var backend = new FakeBackend();
        var peer = new PeerConfig { Id = "studio", Name = "STUDIO-PC", Address = "192.168.1.42", Position = ScreenPosition.Top };

        Assert.Null(await PeerActions.AddAndConnectAsync(settings, backend, peer));

        Assert.Contains(peer, settings.Peers);
        Assert.Contains("studio", backend.Unblocked);
        Assert.Contains(peer, backend.Connected);
        Assert.Equal(2, backend.Saves);
    }

    [Fact]
    public async Task OtherFailures_AreReported()
    {
        var backend = new FakeBackend { ConnectError = new InvalidOperationException("Connection rejected: pairing code") };

        Assert.Equal("Connection rejected: pairing code", await PeerActions.ConnectNewPeerAsync(backend, Samples.Settings().Peers[0]));
    }

    [Fact]
    public async Task Success_ReturnsNull()
    {
        var backend = new FakeBackend();
        var peer = Samples.Settings().Peers[0];

        Assert.Null(await PeerActions.ConnectNewPeerAsync(backend, peer));
        Assert.Contains(peer, backend.Connected);
    }

    [Fact]
    public void FirstFreeEdge_SkipsTakenEdges()
    {
        var settings = Samples.Settings(); // left, right

        Assert.Equal(ScreenPosition.Top, PeerActions.FirstFreeEdge(settings));
        Assert.Equal(ScreenPosition.Left, PeerActions.FirstFreeEdge(settings, except: settings.Peers[0]));

        settings.Peers.Add(new PeerConfig { Position = ScreenPosition.Top });
        settings.Peers.Add(new PeerConfig { Position = ScreenPosition.Bottom });
        Assert.Null(PeerActions.FirstFreeEdge(settings));
    }
}

public class ImageCodecTests
{
    private static byte[] PngHeader(uint width, uint height)
    {
        var png = new byte[33];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(8), 13);
        "IHDR"u8.CopyTo(png.AsSpan(12));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), height);
        png[24] = 8;  // bit depth
        png[25] = 6;  // RGBA
        return png;
    }

    [Fact]
    public void ReadsPngSizeFromHeader()
    {
        Assert.True(AvaloniaImageCodec.TryReadPngSize(PngHeader(1920, 1080), out var w, out var h));
        Assert.Equal(1920, w);
        Assert.Equal(1080, h);
        Assert.False(AvaloniaImageCodec.TryReadPngSize(new byte[40], out _, out _));
    }

    [Theory]
    [InlineData(20_000u, 20_000u)]            // 400 MP
    [InlineData(0xFFFFFFFFu, 0xFFFFFFFFu)]    // would overflow int arithmetic
    [InlineData(0u, 100u)]
    public void PngToDib_RefusesHugeOrEmptyImagesBeforeDecoding(uint width, uint height)
    {
        // No Avalonia platform is set up here, so reaching the decoder would throw and log instead;
        // a null from the header check alone is what this proves.
        Assert.Null(new AvaloniaImageCodec().PngToDib(PngHeader(width, height)));
    }

    [Fact]
    public void DibToPng_RefusesHugeDib()
    {
        var dib = new byte[40];
        BitConverter.TryWriteBytes(dib.AsSpan(0), 40);
        BitConverter.TryWriteBytes(dib.AsSpan(4), 50_000);
        BitConverter.TryWriteBytes(dib.AsSpan(8), 50_000);
        BitConverter.TryWriteBytes(dib.AsSpan(12), (ushort)1);
        BitConverter.TryWriteBytes(dib.AsSpan(14), (ushort)32);

        Assert.Null(new AvaloniaImageCodec().DibToPng(dib));
    }
}
