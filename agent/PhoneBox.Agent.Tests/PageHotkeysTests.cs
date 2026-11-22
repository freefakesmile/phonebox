using PhoneBox.Agent;
using Xunit;

/// <summary>Settings › General › Features: keyboard shortcuts that flip the phone's pages.</summary>
public sealed class PageHotkeysTests
{
    [Fact]
    public void The_defaults_parse()
    {
        Assert.Equal((PageHotkeys.ModControl | PageHotkeys.ModAlt, 0x25u), PageHotkeys.Parse("Ctrl+Alt+Left"));
        Assert.Equal((PageHotkeys.ModControl | PageHotkeys.ModAlt, 0x27u), PageHotkeys.Parse("ctrl + alt + right"));
    }

    [Theory]
    [InlineData("Win+Alt+PageDown", PageHotkeys.ModWin | PageHotkeys.ModAlt, 0x22u)]
    [InlineData("Ctrl+Shift+F9", PageHotkeys.ModControl | PageHotkeys.ModShift, 0x78u)]
    [InlineData("Control+Alt+K", PageHotkeys.ModControl | PageHotkeys.ModAlt, (uint)'K')]
    [InlineData("Ctrl+Alt+Num6", PageHotkeys.ModControl | PageHotkeys.ModAlt, 0x66u)]
    [InlineData("Alt+]", PageHotkeys.ModAlt, 0xDDu)]
    public void Other_combinations_parse(string text, uint mods, uint vk) =>
        Assert.Equal((mods, vk), PageHotkeys.Parse(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Right")]          // no modifier: it would take the key from every program
    [InlineData("Shift+Right")]    // Shift alone is still typing
    [InlineData("Ctrl+Alt")]       // no key
    [InlineData("Ctrl+Left+Right")]
    [InlineData("Ctrl+Alt+Banana")]
    [InlineData("Ctrl+F25")]
    public void Nonsense_is_not_a_hotkey(string? text) => Assert.Null(PageHotkeys.Parse(text));

    [Fact]
    public void The_setting_is_off_by_default_with_the_arrow_keys()
    {
        var path = Path.Combine(Path.GetTempPath(), $"phonebox-pagekeys-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{\"pageKeys\":{\"prev\":\"  \",\"next\":null}}");
            var c = new ConfigStore(path).Get().PageKeys;
            Assert.False(c.Enabled);
            Assert.Equal(PageKeysConfig.DefaultPrev, c.Prev);
            Assert.Equal(PageKeysConfig.DefaultNext, c.Next);
        }
        finally { File.Delete(path); }
    }
}
