using RoboMouse.Core.Input;
using Xunit;

namespace RoboMouse.Core.Tests;

/// <summary>
/// The Keys enum replaces the Windows Forms one; its values must stay the Windows virtual-key codes
/// because they go on the wire and are what SendInput receives on the other machine.
/// </summary>
public class KeysTests
{
    [Theory]
    [InlineData(Keys.Back, 0x08)]
    [InlineData(Keys.Tab, 0x09)]
    [InlineData(Keys.Return, 0x0D)]
    [InlineData(Keys.Escape, 0x1B)]
    [InlineData(Keys.Space, 0x20)]
    [InlineData(Keys.D0, 0x30)]
    [InlineData(Keys.D9, 0x39)]
    [InlineData(Keys.A, 0x41)]
    [InlineData(Keys.Z, 0x5A)]
    [InlineData(Keys.LWin, 0x5B)]
    [InlineData(Keys.NumPad0, 0x60)]
    [InlineData(Keys.Divide, 0x6F)]
    [InlineData(Keys.F1, 0x70)]
    [InlineData(Keys.F12, 0x7B)]
    [InlineData(Keys.F24, 0x87)]
    [InlineData(Keys.NumLock, 0x90)]
    [InlineData(Keys.LShiftKey, 0xA0)]
    [InlineData(Keys.RControlKey, 0xA3)]
    [InlineData(Keys.RMenu, 0xA5)]
    [InlineData(Keys.OemSemicolon, 0xBA)]
    [InlineData(Keys.OemOpenBrackets, 0xDB)]
    [InlineData(Keys.OemBackslash, 0xE2)]
    [InlineData(Keys.OemClear, 0xFE)]
    public void Values_AreWindowsVirtualKeyCodes(Keys key, int expected)
    {
        Assert.Equal(expected, (int)key);
    }

    [Fact]
    public void LetterAndDigitRanges_AreContiguous()
    {
        for (var c = 'A'; c <= 'Z'; c++)
            Assert.Equal(c, (int)Enum.Parse<Keys>(c.ToString()));
        for (var d = 0; d <= 9; d++)
            Assert.Equal('0' + d, (int)Enum.Parse<Keys>("D" + d));
        for (var f = 1; f <= 24; f++)
            Assert.Equal(0x70 + f - 1, (int)Enum.Parse<Keys>("F" + f));
    }

    [Theory]
    [InlineData("Ctrl+Alt+M")]
    [InlineData("Win+Shift+F12")]
    [InlineData("Ctrl+Alt+PageUp")]
    [InlineData("Ctrl+Oemtilde")]
    public void HotkeyStringsFromOlderSettings_StillParse(string text)
    {
        Assert.NotNull(Hotkey.Parse(text));
    }
}
