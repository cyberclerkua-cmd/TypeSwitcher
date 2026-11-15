using System.IO;
using TypeSwitcher.Config;
using TypeSwitcher.Core;
using TypeSwitcher.Interop;
using Xunit;

namespace TypeSwitcher.Tests;

[Collection("Dictionaries")]
public sealed class InteractionTests(TestEnvironment environment)
{
    [Theory]
    [InlineData("Ctrl+Alt+K", 0x4B, KeyModifiers.Control | KeyModifiers.Alt)]
    [InlineData("Shift+Pause", 0x13, KeyModifiers.Shift)]
    [InlineData("Alt+Scroll Lock", 0x91, KeyModifiers.Alt)]
    [InlineData("Ctrl+1", 0x31, KeyModifiers.Control)]
    public void ChordsParseAndMatchExactly(string text, int vk, KeyModifiers modifiers)
    {
        Assert.True(KeyChord.TryParse(text, out var chord));
        Assert.Equal(new KeyChord(vk, modifiers), chord);
        Assert.True(chord.Matches((uint)vk, modifiers));
        Assert.False(chord.Matches((uint)vk, modifiers ^ KeyModifiers.Windows));
    }

    [Theory]
    [InlineData("Ctrl+Ctrl+K")]
    [InlineData("Ctrl+K+L")]
    [InlineData("Ctrl")]
    [InlineData("MissingKey")]
    [InlineData("999")]
    public void InvalidChordsAreRefused(string text) => Assert.False(KeyChord.TryParse(text, out _));

    [Fact]
    public void VersionTwoMigrationKeepsCustomKeysAndBacksUpOriginalBytes()
    {
        using var temp = new TestDirectory();
        var store = new ConfigStore(temp.Path);
        const string original = "{\"schema_version\":2,\"layout\":{\"hotkey_undo_vk\":120,\"hotkey_suggest_vk\":121},\"case\":{\"hotkey_vk\":122,\"hotkey_shift\":false}}";
        File.WriteAllText(store.ConfigPath, original);
        var cfg = store.Load();
        Assert.Null(store.LoadProblem);
        Assert.Equal(AppConfig.CurrentSchemaVersion, cfg.SchemaVersion);
        Assert.Equal(new KeyChord(120), cfg.Hotkeys.ConvertWord);
        Assert.Equal(new KeyChord(121), cfg.Hotkeys.WordActions);
        Assert.Equal(new KeyChord(122), cfg.Hotkeys.ChangeCase);
        Assert.Equal(original, File.ReadAllText(store.ConfigPath + ".v2.bak"));
        store.Save(cfg);
        Assert.Equal(cfg.Hotkeys.ConvertWord, store.Load().Hotkeys.ConvertWord);
    }

    [Fact]
    public void GlobalPauseCanResumeAnInactiveProcessor()
    {
        var keyboard = new SelfTest.FakeKeyboard(environment.Analyzer());
        int toggles = 0;
        keyboard.Processor.ToggleEnabledRequested += () => { toggles++; keyboard.Processor.Enabled = !keyboard.Processor.Enabled; };
        keyboard.ExtraModifiers = KeyModifiers.Control | KeyModifiers.Alt;
        keyboard.Press(0x50, false);
        Assert.False(keyboard.Processor.Enabled);
        keyboard.Press(0x50, false);
        Assert.True(keyboard.Processor.Enabled);
        Assert.Equal(2, toggles);
        Assert.Equal("", keyboard.Screen);
    }

    [Fact]
    public void ModifiedConversionWaitsUntilBothModifiersAreReleased()
    {
        var cfg = new AppConfig();
        cfg.Hotkeys.ConvertWord = new(0x4B, KeyModifiers.Control | KeyModifiers.Alt);
        var keyboard = new SelfTest.FakeKeyboard(environment.Analyzer(cfg));
        keyboard.Type("ghbdtn");
        keyboard.ExtraModifiers = KeyModifiers.Control | KeyModifiers.Alt;
        Key(keyboard, 0xA2, true); Key(keyboard, 0xA4, true);
        keyboard.Press(0x4B, false);
        Assert.Equal("ghbdtn", keyboard.Screen);
        keyboard.ExtraModifiers = KeyModifiers.Alt;
        Key(keyboard, 0xA2, false);
        Assert.Equal("ghbdtn", keyboard.Screen);
        keyboard.ExtraModifiers = 0;
        Key(keyboard, 0xA4, false);
        Assert.Equal("привет", keyboard.Screen);
    }

    [Fact]
    public void ManualSystemLayoutProtectsOnlyTheFirstWord()
    {
        var keyboard = new SelfTest.FakeKeyboard(environment.Analyzer());
        keyboard.Type("hello ");
        keyboard.Current = SelfTest.FakeKeyboard.Ru;
        keyboard.Type("hello hello ");
        Assert.Equal("hello руддщ hello ", keyboard.Screen);
    }

    [Fact]
    public void ManualLayoutProtectionStillHonorsAnExplicitTextExpansion()
    {
        var dictionary = new Dictionaries();
        dictionary.LoadUserTexts(new Dictionary<(string Kind, string Scope), string> { [("rules", "global")] = "тлф = телефон\n" });
        var keyboard = new SelfTest.FakeKeyboard(new Analyzer(dictionary, new AppConfig()));
        keyboard.Type("hello "); keyboard.Current = SelfTest.FakeKeyboard.Ru;
        keyboard.Type("nka ");
        Assert.Equal("hello телефон ", keyboard.Screen);
    }

    [Fact]
    public void ModifierTapSwitchesButControlShortcutsDoNot()
    {
        var cfg = new AppConfig(); cfg.Hotkeys.LayoutTapKey = 0xA3;
        var keyboard = new SelfTest.FakeKeyboard(environment.Analyzer(cfg));
        keyboard.ExtraModifiers = KeyModifiers.Control;
        Key(keyboard, 0xA3, true);
        keyboard.ExtraModifiers = 0;
        Key(keyboard, 0xA3, false);
        Assert.Equal(SelfTest.FakeKeyboard.Ru, keyboard.Current);
        keyboard.ExtraModifiers = KeyModifiers.Control;
        Key(keyboard, 0xA3, true); Key(keyboard, 0x43, true); Key(keyboard, 0x43, false);
        keyboard.ExtraModifiers = 0; Key(keyboard, 0xA3, false);
        Assert.Equal(SelfTest.FakeKeyboard.Ru, keyboard.Current);
    }

    [Fact]
    public void TargetIdentityRejectsAnotherFieldOrMovedCaretInTheSameWindow()
    {
        var target = new InputTarget((IntPtr)1, (IntPtr)2, 5, (IntPtr)3, 100, 20);
        Assert.True(target.Matches(target));
        Assert.False(target.Matches(target with { Control = (IntPtr)4 }));
        Assert.False(target.Matches(target with { FocusVersion = 6 }));
        Assert.False(target.Matches(target with { CaretLeft = 101 }));
    }

    [Fact]
    public void UkrainianAltGrDoesNotTriggerAnOverlappingGlobalShortcut()
    {
        var cfg = new AppConfig(); cfg.Languages.Enabled = ["en", "ru", "uk"];
        cfg.Hotkeys.ToggleEnabled = new(0x55, KeyModifiers.Control | KeyModifiers.Alt);
        var keyboard = new SelfTest.FakeKeyboard(environment.Analyzer(cfg)) { Current = SelfTest.FakeKeyboard.Uk, AltGr = true, ExtraModifiers = KeyModifiers.Control | KeyModifiers.Alt };
        int toggles = 0; keyboard.Processor.ToggleEnabledRequested += () => toggles++;
        keyboard.Press(0x55, false);
        Assert.Equal(0, toggles);
        Assert.Equal("ґ", keyboard.Screen);
    }

    private static void Key(SelfTest.FakeKeyboard keyboard, uint vk, bool down)
        => keyboard.Processor.OnKey(null, new KeyboardHook.KeyArgs { VkCode = vk, IsKeyDown = down });
}
