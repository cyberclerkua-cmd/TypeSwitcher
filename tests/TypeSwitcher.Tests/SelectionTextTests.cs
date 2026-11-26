using TypeSwitcher.Config;
using TypeSwitcher.Core;
using Xunit;

namespace TypeSwitcher.Tests;

public sealed class SelectionTextTests
{
    [Theory]
    [InlineData("ghbdtn vbh", LayoutTarget.Ru, LayoutTarget.En, "привет мир")]
    [InlineData("привет vbh!", LayoutTarget.Ru, LayoutTarget.En, "привет мир!")]
    [InlineData("привіт світ", LayoutTarget.En, LayoutTarget.Uk, "ghbdsn cdsn")]
    [InlineData("Привет, мир!", LayoutTarget.En, LayoutTarget.Ru, "Ghbdtn, vbh!")]
    [InlineData("ghbdtn 🙂", LayoutTarget.Ru, LayoutTarget.En, "привет 🙂")]
    [InlineData("ыэъ", LayoutTarget.Uk, LayoutTarget.Ru, "ієї")]
    public void LayoutConversionPreservesWordSeparatorsAndOtherAlphabets(string text, LayoutTarget target, LayoutTarget active, string expected)
        => Assert.Equal(expected, SelectionText.ConvertLayout(text, target, active));

    [Theory]
    [InlineData("hello world", "HELLO WORLD")]
    [InlineData("HELLO WORLD", "Hello World")]
    [InlineData("Hello World", "hello world")]
    [InlineData("пРИВЕТ", "Привет")]
    [InlineData("ПРИВІТ СВІТ", "Привіт Світ")]
    [InlineData("123 🙂", "123 🙂")]
    public void CaseCycleWorksForWholeSelections(string input, string expected) => Assert.Equal(expected, SelectionText.ChangeCase(input));

    [Theory]
    [InlineData("Щука", LayoutTarget.Ru, "Shchuka")]
    [InlineData("Київ", LayoutTarget.Uk, "Kyiv")]
    [InlineData("Євген", LayoutTarget.Uk, "Yevhen")]
    [InlineData("Ґанок", LayoutTarget.Uk, "Ganok")]
    [InlineData("Згорани, Згурський, розгін", LayoutTarget.Uk, "Zghorany, Zghurskyi, rozghin")]
    [InlineData("ЗГОРАНИ", LayoutTarget.Uk, "ZGHORANY")]
    [InlineData("з г, з-г, з’г, ж", LayoutTarget.Uk, "z h, z-h, zh, zh")]
    [InlineData("Zghorany ZGH zh", LayoutTarget.Uk, "Згорани ЗГ ж")]
    [InlineData("разгар жгут", LayoutTarget.Ru, "razgar zhgut")]
    [InlineData("Shchuka", LayoutTarget.Ru, "Щука")]
    public void TransliterationUsesTheSelectedLanguage(string input, LayoutTarget language, string expected)
        => Assert.Equal(expected, SelectionText.Transliterate(input, language));

    [Theory]
    [InlineData("")]
    [InlineData("first\nsecond")]
    [InlineData("first\tsecond")]
    [InlineData("\ud800")]
    public void UnsupportedSelectionsAreRejectedBeforeWriting(string text) => Assert.False(SelectionText.IsSupported(text));

    [Fact]
    public void SelectionSizeIsBoundedAndValidSurrogatesAreKept()
    {
        Assert.True(SelectionText.IsSupported("привіт 🙂"));
        Assert.False(SelectionText.IsSupported(new string('x', SelectionText.MaximumCharacters + 1)));
    }

    [Fact]
    public void ThreeLanguageAmbiguityOffersBothCyrillicTargets()
    {
        var cfg = new AppConfig(); cfg.Languages.Enabled = ["en", "ru", "uk"];
        Assert.Equal(new[] { LayoutTarget.Ru, LayoutTarget.Uk }, SelectionText.Targets("ghbdsn", LayoutTarget.En, cfg));
        cfg.Selection.TargetLanguage = "uk";
        Assert.Equal(new[] { LayoutTarget.Uk }, SelectionText.Targets("ghbdsn", LayoutTarget.En, cfg));
    }
}
