using TypeSwitcher.Core;
using Xunit;

namespace TypeSwitcher.Tests;

[Collection("Dictionaries")]
public sealed class RegressionTests(TestEnvironment env)
{
    public static IEnumerable<object[]> GoldenCases() => Eval.CuratedCases()
        .Select(c => new object[] { c.Category, c.Input, c.Expected, c.IsKeep });

    [Theory]
    [MemberData(nameof(GoldenCases))]
    public void GoldenTextIsExact(string category, string input, string expected, bool keep)
    {
        var result = env.Analyzer().Analyze(input);
        string actual = result.Kind == ActionKind.None ? input : result.Replacement;
        Assert.True(expected == actual, $"{category}: '{input}' -> '{actual}', expected '{expected}'");
        if (keep) Assert.Equal(ActionKind.None, result.Kind);
        else Assert.NotEqual(ActionKind.None, result.Kind);
    }

    [Fact]
    public void AllGeneratedLayoutCasesRemainPresent()
    {
        var cases = Eval.GeneratedLayoutCases(env.Dictionary).ToArray();
        Assert.Equal(44, cases.Length);
        foreach (var c in cases)
        {
            var result = env.Analyzer().Analyze(c.Input);
            Assert.Equal(c.Expected, result.Kind == ActionKind.None ? c.Input : result.Replacement);
        }
    }

    [Theory]
    [InlineData("ghbdtn ", "привет ")]
    [InlineData("ghbdtn\r", "привет\r")]
    [InlineData("ghbdtn\t", "привет\t")]
    [InlineData("ghjljk;bnm ", "продолжить ")]
    [InlineData("ghjljk;bnm? ", "продолжить, ")]
    [InlineData(",fylbn ", "бандит ")]
    [InlineData("ghbdtn,rfrjq ", "привет,какой ")]
    [InlineData("test,data ", "test,data ")]
    [InlineData("hello ", "hello ")]
    [InlineData("ghbdtn5 ", "ghbdtn5 ")]
    public void KeyboardPipelinePreservesBoundaries(string keys, string expected)
    {
        var keyboard = new SelfTest.FakeKeyboard(env.Analyzer());
        keyboard.Type(keys);
        Assert.Equal(expected, keyboard.Screen);
    }

    [Fact]
    public void UndoAndRedoRestoreTextAndLayout()
    {
        var keyboard = new SelfTest.FakeKeyboard(env.Analyzer());
        keyboard.Type("ghbdtn ");
        keyboard.Press(0x13, false);
        Assert.Equal("ghbdtn ", keyboard.Screen);
        Assert.Equal(SelfTest.FakeKeyboard.En, keyboard.Current);
        keyboard.Press(0x13, false);
        Assert.Equal("привет ", keyboard.Screen);
        Assert.Equal(SelfTest.FakeKeyboard.Ru, keyboard.Current);
    }

    [Fact]
    public void WindowChangeInvalidatesPendingWord()
    {
        var keyboard = new SelfTest.FakeKeyboard(env.Analyzer());
        keyboard.Type("hello ");
        keyboard.Window = (IntPtr)2;
        keyboard.Press(0x13, false);
        Assert.Equal("hello ", keyboard.Screen);
    }

    [Fact]
    public void PasswordAfterTabDoesNotUseStaleFocusCache()
    {
        var focus = new SelfTest.FakeExclusions();
        var keyboard = new SelfTest.FakeKeyboard(env.Analyzer(), focus.Manager);
        focus.Window = () => keyboard.Window;
        keyboard.Type("hello\t");
        focus.Password = true;
        keyboard.Type("ghbdtn\r");
        Assert.Equal("hello\tghbdtn\r", keyboard.Screen);
    }
}
