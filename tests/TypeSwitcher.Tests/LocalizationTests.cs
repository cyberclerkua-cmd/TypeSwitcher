using System.Globalization;
using System.Text.RegularExpressions;
using TypeSwitcher.Config;
using TypeSwitcher.Localization;
using TypeSwitcher.Tray;
using Xunit;

namespace TypeSwitcher.Tests;

public sealed class LocalizationTests
{
    [Fact]
    public void CatalogsCoverTheSameMessagesAndPreserveFormatArguments()
    {
        var english = L.Catalog("en");
        var ukrainian = L.Catalog("uk");
        Assert.True(english.Count > 180);
        Assert.Equal(english.Keys.Order(), ukrainian.Keys.Order());
        string[] Arguments(string value) => Regex.Matches(value, @"\{(\d+)(?:[^{}]*)\}")
            .Select(m => m.Groups[1].Value).Order().ToArray();
        foreach (string key in english.Keys)
        {
            Assert.False(string.IsNullOrWhiteSpace(english[key]));
            Assert.False(string.IsNullOrWhiteSpace(ukrainian[key]));
            Assert.Equal(Arguments(key), Arguments(english[key]));
            Assert.Equal(Arguments(key), Arguments(ukrainian[key]));
        }
    }

    [Theory]
    [InlineData("ru", "Язык интерфейса", "Язык интерфейса")]
    [InlineData("en", "Язык интерфейса", "Interface language")]
    [InlineData("uk", "Язык интерфейса", "Мова інтерфейсу")]
    [InlineData("en", "Светлая", "Light")]
    [InlineData("uk", "Системная", "Системна")]
    public void InterfaceMessagesAreTranslated(string language, string key, string expected)
        => Assert.Equal(expected, L.Get(key, language));

    [Fact]
    public void FormattingDoesNotChangeThreadCulture()
    {
        var original = CultureInfo.CurrentCulture;
        Assert.Equal("The .dic file declares 3 words, but 2 entries were found.", L.Get("В .dic объявлено {0} слов, найдено {1}.", "en", 3, 2));
        Assert.Same(original, CultureInfo.CurrentCulture);
    }

    [Theory]
    [InlineData("dark", false, true)]
    [InlineData("light", true, false)]
    [InlineData("system", true, true)]
    [InlineData("system", false, false)]
    public void SystemThemeFollowsTheWindowsChoice(string mode, bool systemDark, bool expected)
        => Assert.Equal(expected, Theme.ResolveDark(mode, systemDark));

    [Theory]
    [InlineData("en", "light")]
    [InlineData("uk", "system")]
    [InlineData("ru", "dark")]
    public void AppearancePreferencesPreserveInputLanguages(string language, string theme)
    {
        var cfg = new AppConfig();
        var inputs = cfg.Languages.Enabled.ToArray();
        cfg.General.Language = language; cfg.General.Theme = theme;
        cfg.Normalize();
        Assert.Equal(inputs, cfg.Languages.Enabled);
        Assert.Equal(language, cfg.General.Language);
        Assert.Equal(theme, cfg.General.Theme);
    }
}
