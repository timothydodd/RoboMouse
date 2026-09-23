using RoboMouse.Core.Input;
using Xunit;

namespace RoboMouse.Core.Tests;

/// <summary>Forwarded keys are injected so the controlled machine's own keyboard layout applies.</summary>
public class KeyInjectionTests
{
    [Fact]
    public void Keys_AreInjectedByScanCode_SoTheReceiversLayoutApplies()
    {
        var plan = KeyInjection.For(Keys.Z, 0x2C, KeyboardEventType.KeyDown, isExtended: false);
        Assert.Equal(0, plan.VirtualKey);
        Assert.Equal(0x2C, plan.ScanCode);
        Assert.Equal(KeyInjection.KEYEVENTF_SCANCODE, plan.Flags);

        var arrowUp = KeyInjection.For(Keys.Left, 0x4B, KeyboardEventType.KeyUp, isExtended: true);
        Assert.Equal(KeyInjection.KEYEVENTF_SCANCODE | KeyInjection.KEYEVENTF_EXTENDEDKEY | KeyInjection.KEYEVENTF_KEYUP, arrowUp.Flags);
    }

    [Fact]
    public void KeysWithoutAReliableScanCode_GoByVirtualKey()
    {
        var noScan = KeyInjection.For(Keys.VolumeUp, 0, KeyboardEventType.KeyDown, false);
        Assert.Equal((ushort)Keys.VolumeUp, noScan.VirtualKey);
        Assert.Equal(0u, noScan.Flags & KeyInjection.KEYEVENTF_SCANCODE);

        var pause = KeyInjection.For(Keys.Pause, 0x45, KeyboardEventType.KeyDown, false);
        Assert.Equal((ushort)Keys.Pause, pause.VirtualKey);
        Assert.Equal(0u, pause.Flags & KeyInjection.KEYEVENTF_SCANCODE);

        var media = KeyInjection.For(Keys.MediaPlayPause, 0x22, KeyboardEventType.SysKeyUp, true);
        Assert.Equal(KeyInjection.KEYEVENTF_EXTENDEDKEY | KeyInjection.KEYEVENTF_KEYUP, media.Flags);
    }

    [Theory]
    [InlineData(Keys.LShiftKey, 0x2Au, false)]
    [InlineData(Keys.RShiftKey, 0x36u, true)] // the hook reports Right Shift as extended; E0 36 is no key
    [InlineData(Keys.LControlKey, 0x1Du, false)]
    [InlineData(Keys.RMenu, 0x38u, true)]
    [InlineData(Keys.LWin, 0x5Bu, true)]
    [InlineData(Keys.CapsLock, 0x3Au, false)]
    public void Modifiers_GoByVirtualKey(Keys key, uint scan, bool extended)
    {
        var down = KeyInjection.For(key, scan, KeyboardEventType.KeyDown, extended);
        Assert.Equal((ushort)key, down.VirtualKey);
        Assert.Equal(scan, down.ScanCode);
        Assert.Equal(0u, down.Flags & KeyInjection.KEYEVENTF_SCANCODE);

        var up = KeyInjection.For(key, scan, KeyboardEventType.KeyUp, extended);
        Assert.Equal((ushort)key, up.VirtualKey);
        Assert.NotEqual(0u, up.Flags & KeyInjection.KEYEVENTF_KEYUP);
    }

    [Fact]
    public void UnicodeCharacters_AreTypedAsCharacters()
    {
        var plan = KeyInjection.For(Keys.Packet, 'é', KeyboardEventType.KeyDown, false);
        Assert.Equal(0, plan.VirtualKey);
        Assert.Equal('é', plan.ScanCode);
        Assert.Equal(KeyInjection.KEYEVENTF_UNICODE, plan.Flags);

        var up = KeyInjection.For(Keys.Packet, '€', KeyboardEventType.KeyUp, false);
        Assert.Equal(KeyInjection.KEYEVENTF_UNICODE | KeyInjection.KEYEVENTF_KEYUP, up.Flags);
    }
}
