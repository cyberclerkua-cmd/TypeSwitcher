using TypeSwitcher.Config;
using TypeSwitcher.Core;
using TypeSwitcher.Interop;
using Xunit;
using Keyboard = TypeSwitcher.Core.SelfTest.FakeKeyboard;

namespace TypeSwitcher.Tests;

[Collection("Dictionaries")]
public sealed class MixedInputTests(TestEnvironment environment)
{
    [Theory]
    [InlineData("ru", "SQL-pfghjc", "SQL-запрос")]
    [InlineData("ru", "Type-C-rf,tkm", "Type-C-кабель")]
    [InlineData("ru", "JSON-afqk", "JSON-файл")]
    [InlineData("ru", "GitHub-htgjpbnjhbq", "GitHub-репозиторий")]
    [InlineData("ru", "key-afqk", "key-файл")]
    [InlineData("ru", "rfrjq-nj", "какой-то")]
    [InlineData("uk", "SQL-pfgbn", "SQL-запит")]
    [InlineData("uk", "GitHub-htgjpbnjhsq", "GitHub-репозиторій")]
    public void HyphenatedTermKeepsItsCorrectParts(string language, string keys, string expected)
    {
        var cfg = new AppConfig(); cfg.Languages.Enabled = ["en", language];
        var keyboard = new Keyboard(environment.Analyzer(cfg));
        keyboard.Type(keys + " ");
        Assert.Equal(expected + " ", keyboard.Screen);
        Assert.Equal(language == "ru" ? Keyboard.Ru : Keyboard.Uk, keyboard.Current);
        keyboard.Press(0x13, false);
        Assert.Equal(keys + " ", keyboard.Screen);
    }

    [Fact]
    public void ACorrectCyrillicSuffixKeepsItsLayoutAfterAnEnglishTermIsRecovered()
    {
        var keyboard = new Keyboard(environment.Analyzer()) { Current = Keyboard.Ru };
        keyboard.Type("HTTP-pfghjc ");
        Assert.Equal("HTTP-запрос ", keyboard.Screen);
        Assert.Equal(Keyboard.Ru, keyboard.Current);
        keyboard.Press(0x13, false);
        Assert.Equal("РЕЕЗ-запрос ", keyboard.Screen);
    }

    [Theory]
    [InlineData("SQL-vfvf")]
    [InlineData("Type-C")]
    [InlineData("SQL-JSON")]
    [InlineData("foo_bar-pfghjc")]
    [InlineData("--SQL-pfghjc")]
    [InlineData("SQL-pfghjc-")]
    public void AmbiguousOrAlreadyCorrectCompoundsAreKept(string keys)
    {
        var cfg = new AppConfig(); cfg.Languages.Enabled = ["en", "ru", "uk"];
        var keyboard = new Keyboard(environment.Analyzer(cfg));
        keyboard.Type(keys + " ");
        Assert.Equal(keys + " ", keyboard.Screen);
        Assert.Equal(Keyboard.En, keyboard.Current);
    }

    [Theory]
    [InlineData("ru", "dthcbz 1.5.3", "версия 1.5.3")]
    [InlineData("uk", "dthcsz 1.5.3", "версія 1.5.3")]
    [InlineData("ru", "192.168.1.1", "192.168.1.1")]
    [InlineData("uk", "1.2.", "1.2.")]
    [InlineData("ru", "2.", "2.")]
    [InlineData("ru", "1/5/3", "1.5.3")]
    [InlineData("ru", "1/5.3", "1.5.3")]
    [InlineData("ru", "1.5/3", "1.5.3")]
    [InlineData("ru", "ljv 3,", "дом 3б")]
    [InlineData("ru", "1..2", "1юю2")]
    [InlineData("ru", "1.2f", "1ю2а")]
    [InlineData("ru", "1//5.3", "1..5ю3")]
    [InlineData("uk", "1>2", "1Ю2")]
    public void NumericDotsDoNotChangeTheTypingLayout(string language, string keys, string expected)
    {
        var cfg = new AppConfig(); cfg.Languages.Enabled = ["en", language];
        IntPtr layout = language == "ru" ? Keyboard.Ru : Keyboard.Uk;
        var keyboard = new Keyboard(environment.Analyzer(cfg)) { Current = layout };
        keyboard.Type(keys + " ");
        Assert.Equal(expected + " ", keyboard.Screen);
        Assert.Equal(layout, keyboard.Current);
    }

    [Fact]
    public void NumericDotsAreOneUndoableCorrectionAndLeaveTheLayoutActive()
    {
        var keyboard = new Keyboard(environment.Analyzer()) { Current = Keyboard.Ru };
        var corrections = new List<ReplacementRequest>();
        keyboard.Processor.ReplacementRequested += corrections.Add;
        keyboard.Type("1.5/3 ");
        var correction = Assert.Single(corrections);
        Assert.Equal("1ю5.3", correction.Original);
        Assert.Equal(IntPtr.Zero, correction.TargetHkl);
        Undo(keyboard);
        Assert.Equal("1ю5.3 ", keyboard.Screen);
        Assert.Equal(Keyboard.Ru, keyboard.Current);
    }

    [Fact]
    public void LayoutSettingsAndExplicitRulesTakePrecedence()
    {
        var cfg = new AppConfig(); cfg.Layout.Enabled = false;
        var analyzer = environment.Analyzer(cfg);
        Assert.Equal(ActionKind.None, analyzer.Analyze("1ю5ю3", LayoutTarget.Ru).Kind);
        Assert.Equal(ActionKind.None, analyzer.Analyze("SQL-pfghjc", LayoutTarget.En).Kind);
        cfg.Layout.Enabled = true;
        cfg.Layout.Rules.Add(new() { Pattern = "1ю5ю3", Action = "keep", Source = "ru" });
        cfg.Layout.Rules.Add(new() { Pattern = "SQL-pfghjc", Action = "keep", Source = "en" });
        var ru = new Keyboard(analyzer) { Current = Keyboard.Ru }; ru.Type("1.5.3 ");
        var en = new Keyboard(analyzer); en.Type("SQL-pfghjc ");
        Assert.Equal("1ю5ю3 ", ru.Screen);
        Assert.Equal("SQL-pfghjc ", en.Screen);
    }

    [Fact]
    public void PausingInsideANumberDoesNotChangeAnUnfinishedIdentifier()
    {
        var cfg = new AppConfig(); cfg.Layout.CorrectOnIdle = true;
        var keyboard = new Keyboard(environment.Analyzer(cfg)) { Current = Keyboard.Ru };
        long now = 1000; keyboard.Processor.Milliseconds = () => now;
        keyboard.Type("1.5"); keyboard.Processor.Tick();
        now += 1000; keyboard.Processor.Tick();
        keyboard.Type("f ");
        Assert.Equal("1ю5а ", keyboard.Screen);
    }

    [Fact]
    public void DiagnosticsUsesTheSameCompoundAndNumberDecisions()
    {
        var cfg = new AppConfig();
        var compound = Diagnostics.Analyze(environment.Dictionary, cfg, "Type-C-rf,tkm", LayoutTarget.En);
        var dots = Diagnostics.Analyze(environment.Dictionary, cfg, "1ю5ю3", LayoutTarget.Ru);
        Assert.Equal("Type-C-кабель", Assert.Single(compound).Analysis.Applied.Replacement);
        Assert.Equal("1.5.3", Assert.Single(dots).Analysis.Applied.Replacement);
    }

    [Fact]
    public void UndoRestoresTheOriginalSeparatorAndKeepsTheWordInProgress()
    {
        var keyboard = new Keyboard(environment.Analyzer());
        var rejected = new List<WordContext>();
        keyboard.Processor.WordRejected += rejected.Add;
        keyboard.Type("ghbdtn/ v");
        Assert.Equal("привет. м", keyboard.Screen);
        Undo(keyboard);
        Assert.Equal("ghbdtn/ м", keyboard.Screen);
        Assert.Equal(Keyboard.Ru, keyboard.Current);
        keyboard.Type("bh");
        keyboard.Press(0x13, true);
        Assert.Equal("ghbdtn/ МИР", keyboard.Screen);
        Undo(keyboard);
        Assert.Equal("ghbdtn/ МИР", keyboard.Screen);
        Assert.Equal("ghbdtn", Assert.Single(rejected).Word);
    }

    [Theory]
    [InlineData(24, 10_000, true)]
    [InlineData(25, 10_000, false)]
    [InlineData(2, 10_001, false)]
    public void DelayedUndoHasATimeAndKeyLimit(int keys, long elapsed, bool canUndo)
    {
        var keyboard = new Keyboard(environment.Analyzer());
        long now = 1000; keyboard.Processor.Milliseconds = () => now;
        keyboard.Type("ghbdtn " + new string('a', keys));
        now += elapsed;
        Undo(keyboard);
        Assert.Equal((canUndo ? "ghbdtn " : "привет ") + new string('ф', keys), keyboard.Screen);
    }

    [Fact]
    public void WaitingBeforeTheNextWordDoesNotLookLikeAManualLayoutChange()
    {
        var keyboard = new Keyboard(environment.Analyzer());
        long now = 1000; keyboard.Processor.Milliseconds = () => now;
        keyboard.Type("ghbdtn ");
        now += 9_000;
        keyboard.Type("vbh"); Undo(keyboard);
        Assert.Equal("ghbdtn мир", keyboard.Screen);
    }

    [Fact]
    public void LiveCorrectionExpiresFromWhenItWasMadeNotFromTheNextSpace()
    {
        var cfg = new AppConfig(); cfg.Layout.EarlySwitch = true;
        var keyboard = new Keyboard(environment.Analyzer(cfg));
        long now = 1000; keyboard.Processor.Milliseconds = () => now;
        keyboard.Type("ghbd");
        Assert.Equal("прив", keyboard.Screen);
        now += 10_001; keyboard.Type("tn vbh"); Undo(keyboard);
        Assert.Equal("привет мир", keyboard.Screen);
    }

    [Theory]
    [InlineData(0x08)] // Backspace
    [InlineData(0x25)] // Left
    [InlineData(0x2E)] // Delete
    [InlineData(0x09)] // Tab
    [InlineData(0x0D)] // Enter
    public void EditingOrNavigatingCancelsDelayedUndo(uint key)
    {
        var keyboard = new Keyboard(environment.Analyzer());
        keyboard.Type("ghbdtn vbh");
        keyboard.Press(key, false);
        string before = keyboard.Screen;
        Undo(keyboard);
        Assert.Equal(before, keyboard.Screen);
    }

    [Theory]
    [InlineData("  ")]
    [InlineData("//")]
    [InlineData("(")]
    public void EditorSensitiveTextCancelsDelayedUndo(string keys)
    {
        var keyboard = new Keyboard(environment.Analyzer());
        keyboard.Type("ghbdtn vbh" + keys);
        string before = keyboard.Screen;
        Undo(keyboard);
        Assert.Equal(before, keyboard.Screen);
    }

    [Theory]
    [InlineData("click")]
    [InlineData("focus")]
    [InlineData("caret")]
    [InlineData("layout")]
    [InlineData("shortcut")]
    public void DelayedUndoStaysInItsOriginalTypingContext(string change)
    {
        var keyboard = new Keyboard(environment.Analyzer());
        int position = 0, field = 2;
        keyboard.Processor.TargetReader = () => new(keyboard.Window, field, Caret: 3, CaretLeft: position);
        keyboard.Type("ghbdtn vbh");
        switch (change)
        {
            case "click": keyboard.Processor.ResetWordState(); break;
            case "focus": field++; break;
            case "caret": position++; break;
            case "layout": keyboard.Current = Keyboard.En; break;
            case "shortcut": keyboard.Chord = true; keyboard.Press(0x2D, false); keyboard.Chord = false; break;
        }
        Undo(keyboard);
        Assert.Equal("привет мир", keyboard.Screen);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ReadableFieldMustStillMatchWhenDelayedUndoIsApplied(bool matches, bool moreInput)
    {
        var keyboard = new Keyboard(environment.Analyzer());
        keyboard.Type("ghbdtn vbh");
        string? checkedText = null;
        keyboard.Processor.VerifyUndoText = (_, expected) =>
        {
            checkedText = expected;
            if (moreInput) keyboard.Type("f");
            return Task.FromResult(matches);
        };
        Undo(keyboard);
        Assert.Equal("привет мир", checkedText);
        Assert.Equal(moreInput ? "привет мира" : matches ? "ghbdtn мир" : "привет мир", keyboard.Screen);
    }

    [Fact]
    public void PauseStillConvertsTheCurrentWordAndInvalidatesTheEarlierUndo()
    {
        var keyboard = new Keyboard(environment.Analyzer());
        keyboard.Type("ghbdtn vbh");
        keyboard.Press(0x13, false);
        Undo(keyboard);
        Assert.Equal("привет vbh", keyboard.Screen);
    }

    [Fact]
    public void UndoTracksOnlyTheLatestAutomaticCorrection()
    {
        var keyboard = new Keyboard(environment.Analyzer());
        keyboard.Type("ghbdtn hello hi");
        Assert.Equal("привет hello hi", keyboard.Screen);
        Undo(keyboard);
        Assert.Equal("привет руддщ hi", keyboard.Screen);
        Assert.Equal(Keyboard.En, keyboard.Current);
    }

    private static void Undo(Keyboard keyboard)
    {
        void Modifier(uint vk, bool down) => keyboard.Processor.OnKey(null, new KeyboardHook.KeyArgs { VkCode = vk, IsKeyDown = down });
        keyboard.ExtraModifiers = KeyModifiers.Control | KeyModifiers.Alt;
        Modifier(0xA2, true); Modifier(0xA4, true);
        keyboard.Press('Z', false);
        keyboard.ExtraModifiers = KeyModifiers.Alt; Modifier(0xA2, false);
        keyboard.ExtraModifiers = KeyModifiers.None; Modifier(0xA4, false);
    }
}
