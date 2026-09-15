using RoboMouse.Core.Input;
using Xunit;

namespace RoboMouse.Core.Tests;

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+M", Keys.M, true, true, false, false)]
    [InlineData("ctrl + shift + F12", Keys.F12, true, false, true, false)]
    [InlineData("Win+Alt+3", Keys.D3, false, true, false, true)]
    [InlineData("Control+Escape", Keys.Escape, true, false, false, false)]
    public void Parse_AcceptsModifierChords(string text, Keys key, bool ctrl, bool alt, bool shift, bool win)
    {
        var hotkey = Hotkey.Parse(text);

        Assert.NotNull(hotkey);
        Assert.Equal(key, hotkey.Key);
        Assert.Equal(ctrl, hotkey.Ctrl);
        Assert.Equal(alt, hotkey.Alt);
        Assert.Equal(shift, hotkey.Shift);
        Assert.Equal(win, hotkey.Win);
    }

    [Theory]
    [InlineData("")]
    [InlineData("M")]            // No modifier: ordinary typing must never trigger it
    [InlineData("Ctrl+Alt")]     // No key
    [InlineData("Ctrl+M+N")]     // Two keys
    [InlineData("Ctrl+Bogus")]
    public void Parse_RejectsInvalidChords(string text)
    {
        Assert.Null(Hotkey.Parse(text));
    }

    [Fact]
    public void Matches_UsesHookTrackedModifiers()
    {
        var hotkey = Hotkey.Parse("Ctrl+Alt+M")!;
        var modifiers = new ModifierState();

        Assert.False(hotkey.Matches(Keys.M, modifiers));

        modifiers.Update(Keys.LControlKey, isDown: true);
        modifiers.Update(Keys.LMenu, isDown: true);
        Assert.True(hotkey.Matches(Keys.M, modifiers));
        Assert.False(hotkey.Matches(Keys.N, modifiers));

        modifiers.Update(Keys.LShiftKey, isDown: true); // Extra modifier must not match
        Assert.False(hotkey.Matches(Keys.M, modifiers));

        modifiers.Update(Keys.LShiftKey, isDown: false);
        modifiers.Update(Keys.LControlKey, isDown: false);
        Assert.False(hotkey.Matches(Keys.M, modifiers));
    }

    [Fact]
    public void ToString_RoundTrips()
    {
        var hotkey = Hotkey.Parse("Shift+Ctrl+Alt+Win+K")!;
        Assert.Equal("Ctrl+Alt+Shift+Win+K", hotkey.ToString());
        Assert.Equal(hotkey, Hotkey.Parse(hotkey.ToString()));
    }
}
