using TypeSwitcher.Config;
using TypeSwitcher.Core;
using Xunit;

namespace TypeSwitcher.Tests;

[Collection("Dictionaries")]
public sealed class UkrainianTests(TestEnvironment env)
{
    public static IEnumerable<object[]> GoldenCases() => Eval.UkrainianCases()
        .Select(c => new object[] { c.Input, c.Expected, c.IsKeep, c.Source, c.Target });

    [Theory]
    [MemberData(nameof(GoldenCases))]
    public void UkrainianGoldenCasesAreExact(string input, string expected, bool keep, LayoutTarget source, LayoutTarget target)
    {
        var result = Analyzer("en", "uk").Analyze(input, source);
        Assert.Equal(expected, result.Kind == ActionKind.None ? input : result.Replacement);
        if (keep) Assert.Equal(ActionKind.None, result.Kind);
        else Assert.NotEqual(ActionKind.None, result.Kind);
        if (target != LayoutTarget.None) Assert.Equal(target, result.Layout);
    }
    private Analyzer Analyzer(params string[] languages)
    {
        var cfg = new AppConfig();
        cfg.Languages.Enabled = languages.ToList();
        return env.Analyzer(cfg);
    }

    [Theory]
    [InlineData("привіт")]
    [InlineData("ґанок")]
    [InlineData("їжак")]
    [InlineData("єдність")]
    [InlineData("український")]
    [InlineData("українською")]
    [InlineData("п'ять")]
    [InlineData("п’ять")]
    [InlineData("пʼять")]
    [InlineData("об'єкт")]
    [InlineData("будь-який")]
    [InlineData("Київ")]
    [InlineData("ПРИВІТ")]
    public void UkrainianWordsAndInflectionsAreValid(string word)
    {
        Assert.True(env.Dictionary.IsValid(LayoutTarget.Uk, word), word);
        Assert.Equal(ActionKind.None, Analyzer("en", "uk").Analyze(word, LayoutTarget.Uk).Kind);
    }

    [Theory]
    [InlineData("привт", "привіт")]
    [InlineData("українськкий", "український")]
    [InlineData("клавіатра", "клавіатура")]
    [InlineData("налаштувння", "налаштування")]
    public void UkrainianSpellingUsesUkrainianCandidates(string input, string expected)
    {
        var result = Analyzer("en", "uk").Analyze(input, LayoutTarget.Uk);
        string candidates = string.Join("; ", env.Dictionary.RankCandidates(LayoutTarget.Uk, input, new TypoOptions(1, 5, 50, true, 1, 1, 1.5)));
        Assert.True(result.Kind == ActionKind.Typo && result.Replacement == expected, $"{input}: {result}; candidates: {candidates}");
    }

    [Theory]
    [InlineData("ghbdsn ", "привіт ")]
    [InlineData(";bnnz ", "життя ")]
    [InlineData("g`znm ", "п'ять ")]
    [InlineData("ghbdsn/ ", "привіт. ")]
    [InlineData("ghbdsn\r", "привіт\r")]
    public void WrongEnglishLayoutIsCorrectedByKeyboardPipeline(string input, string expected)
    {
        var keyboard = new SelfTest.FakeKeyboard(Analyzer("en", "uk"));
        keyboard.Type(input);
        Assert.Equal(expected, keyboard.Screen);
        Assert.Equal(SelfTest.FakeKeyboard.Uk, keyboard.Current);
    }

    [Fact]
    public void UkrainianApostropheStaysInsideWord()
    {
        var keyboard = new SelfTest.FakeKeyboard(Analyzer("en", "uk")) { Current = SelfTest.FakeKeyboard.Uk };
        keyboard.Type("g`znm ");
        Assert.Equal("п'ять ", keyboard.Screen);
    }

    [Fact]
    public void ThreeLanguagesDoNotGuessAmbiguousTarget()
    {
        var decision = Analyzer("en", "ru", "uk").Analyze("vfvf", LayoutTarget.En);
        Assert.Equal(ActionKind.None, decision.Kind);
        Assert.Equal("ambiguous-layout", decision.Reason);
    }

    [Fact]
    public void AmbiguousUkrainianEndingIsNotChangedToMoreFrequentInflection()
    {
        // И "український", и "українській" получаются одной вставкой; контекста нет.
        Assert.Equal(ActionKind.None, Analyzer("en", "uk").Analyze("українськй", LayoutTarget.Uk).Kind);
    }

    [Fact]
    public void ThreeLanguageUndoRestoresActualSource()
    {
        var keyboard = new SelfTest.FakeKeyboard(Analyzer("en", "ru", "uk"));
        keyboard.Type("ghbdsn ");
        Assert.Equal("привіт ", keyboard.Screen);
        keyboard.Press(0x13, false);
        Assert.Equal("ghbdsn ", keyboard.Screen);
        Assert.Equal(SelfTest.FakeKeyboard.En, keyboard.Current);
        keyboard.Press(0x13, false);
        Assert.Equal("привіт ", keyboard.Screen);
        Assert.Equal(SelfTest.FakeKeyboard.Uk, keyboard.Current);
    }

    [Fact]
    public void ManualCycleUsesAllThreeLayoutsAndRespectsChoiceAtBoundary()
    {
        var keyboard = new SelfTest.FakeKeyboard(Analyzer("en", "ru", "uk"));
        keyboard.Type("hello");
        keyboard.Press(0x13, false);
        Assert.Equal(SelfTest.FakeKeyboard.Ru, keyboard.Current);
        keyboard.Press(0x13, false);
        Assert.Equal(SelfTest.FakeKeyboard.Uk, keyboard.Current);
        keyboard.Press(0x13, false);
        Assert.Equal(SelfTest.FakeKeyboard.En, keyboard.Current);
        keyboard.Type(" ");
        Assert.Equal("hello ", keyboard.Screen);
    }

    [Fact]
    public void UndoRestoresOriginalPunctuationAsWellAsLayout()
    {
        var keyboard = new SelfTest.FakeKeyboard(Analyzer("en", "uk"));
        keyboard.Type("ghbdsn/");
        Assert.Equal("привіт.", keyboard.Screen);
        keyboard.Press(0x13, false);
        Assert.Equal("ghbdsn/", keyboard.Screen);
        keyboard.Press(0x13, false);
        Assert.Equal("привіт.", keyboard.Screen);
    }

    [Fact]
    public void MissingTargetLayoutPreventsAutomaticSwitch()
    {
        var analyzer = Analyzer("en", "uk");
        analyzer.AvailableLayouts = new HashSet<LayoutTarget> { LayoutTarget.En };
        Assert.Equal(ActionKind.None, analyzer.Analyze("ghbdsn", LayoutTarget.En).Kind);
    }

    [Fact]
    public void CyrillicPairWorksWithoutEnglishInManualCycle()
    {
        var keyboard = new SelfTest.FakeKeyboard(Analyzer("ru", "uk")) { Current = SelfTest.FakeKeyboard.Ru };
        keyboard.Type("s");
        Assert.Equal("ы", keyboard.Screen);
        keyboard.Press(0x13, false);
        Assert.Equal("і", keyboard.Screen);
        Assert.Equal(SelfTest.FakeKeyboard.Uk, keyboard.Current);
        keyboard.Press(0x13, false);
        Assert.Equal("ы", keyboard.Screen);
    }

    [Fact]
    public void CapsLockAndCaseHotkeyHandleUkrainianLetters()
    {
        var keyboard = new SelfTest.FakeKeyboard(Analyzer("en", "uk")) { Current = SelfTest.FakeKeyboard.Uk, Caps = true };
        keyboard.Type("s]");
        Assert.Equal("ІЇ", keyboard.Screen);
        keyboard.Caps = false;
        keyboard.Press(0x13, true);
        Assert.Equal("Ії", keyboard.Screen);
    }

    [Fact]
    public void RawSegmentOverflowRecoversAtNextBoundary()
    {
        var keyboard = new SelfTest.FakeKeyboard(Analyzer("en", "uk"));
        string longWord = new('x', 80);
        keyboard.Type(longWord + " ");
        keyboard.Type("ghbdsn ");
        Assert.Equal(longWord + " привіт ", keyboard.Screen);
    }

    [Fact]
    public void UnsupportedWindowsLayoutIsIgnored()
    {
        var keyboard = new SelfTest.FakeKeyboard(Analyzer("en", "ru", "uk")) { Current = (IntPtr)0x04070407 };
        keyboard.Type("ghbdsn ");
        keyboard.Press(0x13, false);
        Assert.Equal("ghbdsn ", keyboard.Screen);
    }

    [Fact]
    public void CanonicallyEquivalentWordIsRecognizedWithoutRewritingItsGraphemes()
    {
        const string word = "і\u0308жак";
        Assert.True(env.Dictionary.IsValid(LayoutTarget.Uk, word));
        var result = Analyzer("en", "uk").Analyze(word, LayoutTarget.Uk);
        Assert.Equal(ActionKind.None, result.Kind);
        Assert.Equal("combining-marks", result.Reason);
    }

    [Fact]
    public void AltGrUkrainianLetterIsRecordedAndCannotDisappearDuringRemap()
    {
        var keyboard = new SelfTest.FakeKeyboard(Analyzer("en", "uk")) { Current = SelfTest.FakeKeyboard.Uk, Chord = true, AltGr = true };
        keyboard.Press(0x55, false); // AltGr + г в украинской расширенной раскладке
        keyboard.AltGr = false; keyboard.Chord = false;
        keyboard.Type("fyjr ");
        Assert.Equal("ґанок ", keyboard.Screen);
        keyboard.Press(0x13, false);
        Assert.Equal("ґанок ", keyboard.Screen); // EN не может воспроизвести AltGr-клавишу
    }

    [Fact]
    public void UndoRestoresExactWindowsLayoutVariant()
    {
        IntPtr enhanced = (IntPtr)0x20420422;
        var keyboard = new SelfTest.FakeKeyboard(Analyzer("en", "uk")) { Current = enhanced };
        keyboard.Type("hello ");
        Assert.Equal("hello ", keyboard.Screen);
        Assert.Equal(SelfTest.FakeKeyboard.En, keyboard.Current);
        keyboard.Press(0x13, false);
        Assert.Equal(enhanced, keyboard.Current);
        Assert.Equal("руддщ ", keyboard.Screen);
    }

    [Fact]
    public void CtrlPauseIsNotTheLayoutHotkey()
    {
        var keyboard = new SelfTest.FakeKeyboard(Analyzer("en", "uk"));
        keyboard.Type("hello ");
        keyboard.Chord = true;
        keyboard.Press(0x13, false);
        Assert.Equal("hello ", keyboard.Screen);
        Assert.Equal(SelfTest.FakeKeyboard.En, keyboard.Current);
    }
}
