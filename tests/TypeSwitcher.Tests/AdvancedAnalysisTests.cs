using TypeSwitcher.Config;
using TypeSwitcher.Core;
using TypeSwitcher.Interop;
using Xunit;

namespace TypeSwitcher.Tests;

[Collection("Dictionaries")]
public sealed class AdvancedAnalysisTests(TestEnvironment environment)
{
    [Theory]
    [InlineData("wrold", "world")]
    [InlineData("helllo", "hello")]
    public void EnglishSpellingIsOptIn(string input, string expected)
    {
        var cfg = new AppConfig(); cfg.Layout.Enabled = false;
        var analyzer = environment.Analyzer(cfg);
        Assert.Equal(ActionKind.None, analyzer.Analyze(input, LayoutTarget.En).Kind);
        cfg.Typo.EnglishEnabled = true;
        var result = analyzer.Analyze(input, LayoutTarget.En);
        var candidates = environment.Dictionary.RankCandidates(LayoutTarget.En, input, new(1, 5, 50, true, 1, 1, cfg.Typo.ScoreGap));
        string details = string.Join(", ", candidates.Select(c => $"{c.Word}:{c.Score:0.000}/{c.Frequency}"));
        Assert.True(result.Kind == ActionKind.Typo, $"{result.Reason}; {details}");
        Assert.Equal(expected, result.Replacement);
    }

    [Fact]
    public void AmbiguousEnglishCandidatesRespectTheConfiguredConfidenceMargin()
    {
        var cfg = new AppConfig(); cfg.Layout.Enabled = false; cfg.Typo.EnglishEnabled = true;
        var analyzer = environment.Analyzer(cfg);
        // Both receive (transposition) and relieve (substitution) are one edit away.
        Assert.Equal("low-confidence", analyzer.Analyze("recieve", LayoutTarget.En).Reason);
        cfg.Typo.ScoreGap = 0.8;
        Assert.Equal("receive", analyzer.Analyze("recieve", LayoutTarget.En).Replacement);
    }

    [Theory]
    [InlineData("GitHub")]
    [InlineData("OAuth")]
    [InlineData("HTTP")]
    [InlineData("win10")]
    [InlineData("var_name")]
    [InlineData("npm")]
    public void EnglishCorrectionKeepsTechnicalText(string word)
    {
        var cfg = new AppConfig(); cfg.Typo.EnglishEnabled = true;
        Assert.Equal(ActionKind.None, environment.Analyzer(cfg).Analyze(word, LayoutTarget.En).Kind);
    }

    [Fact]
    public void LayoutPoliciesHaveExplicitSpecificityAndLanguageScope()
    {
        var cfg = new AppConfig();
        cfg.Layout.Rules.Add(new() { Pattern = "ghb", Match = "prefix", Action = "keep", Source = "en" });
        var analyzer = environment.Analyzer(cfg);
        Assert.Equal("layout-rule-keep", analyzer.Analyze("ghbdtn", LayoutTarget.En).Reason);
        cfg.Layout.Rules.Add(new() { Pattern = "ghbdtn", Match = "exact", Action = "convert", Source = "en", Target = "ru" });
        Assert.Equal("привет", analyzer.Analyze("ghbdtn", LayoutTarget.En).Replacement);
        Assert.Null(LayoutRule.Find(cfg.Layout.Rules, "ghbdtn", LayoutTarget.Uk));
        cfg.Languages.Enabled = ["en"];
        Assert.Equal(ActionKind.None, analyzer.Analyze("ghbdtn", LayoutTarget.En).Kind);
    }

    [Fact]
    public void ContextResolvesOnlyAnAllowedAmbiguousLayoutAndCanObserveWithoutEditing()
    {
        var cfg = new AppConfig(); cfg.Languages.Enabled = ["en", "ru", "uk"]; cfg.Layout.ContextMode = "observe";
        var analyzer = environment.Analyzer(cfg);
        var context = new RecentContext(); context.Add(LayoutTarget.Uk, 1000); context.Add(LayoutTarget.Uk, 1001);
        var observed = analyzer.AnalyzeWithContext("vfvf", LayoutTarget.En, context, 1100);
        Assert.Equal(ActionKind.None, observed.Applied.Kind);
        Assert.Equal(LayoutTarget.Uk, observed.Proposed.Layout);
        Assert.Equal(ContextAction.Suggest, observed.Action);
        cfg.Layout.ContextMode = "assist";
        var applied = analyzer.AnalyzeWithContext("vfvf", LayoutTarget.En, context, 1100);
        Assert.Equal("мама", applied.Applied.Replacement);
        Assert.Equal(LayoutTarget.Uk, applied.Applied.Layout);
        Assert.Equal(ActionKind.None, analyzer.AnalyzeWithContext("hello", LayoutTarget.En, context, 1100).Applied.Kind);
        cfg.Layout.Rules.Add(new() { Pattern = "vfvf", Action = "keep" });
        Assert.Equal(ActionKind.None, analyzer.AnalyzeWithContext("vfvf", LayoutTarget.En, context, 1100).Applied.Kind);
    }

    [Fact]
    public void ContextIsBoundedExpiresAndClears()
    {
        var context = new RecentContext();
        for (int i = 0; i < 100; i++) context.Add(LayoutTarget.Ru, i);
        Assert.Equal(RecentContext.MaximumWords, context.Count);
        Assert.Equal(LayoutTarget.Ru, context.Preferred(100));
        Assert.Equal(LayoutTarget.None, context.Preferred(31_000));
        Assert.Equal(0, context.Count);
        context.Add(LayoutTarget.En, 32_000); context.Clear();
        Assert.Equal(0, context.Count);
    }

    [Fact]
    public void EarlySwitchContinuesTheWordAndProducesOneUndoableCorrection()
    {
        var cfg = new AppConfig(); cfg.Layout.EarlySwitch = true;
        var keyboard = new SelfTest.FakeKeyboard(environment.Analyzer(cfg));
        var completed = new List<CompletedCorrection>();
        keyboard.Processor.CorrectionCompleted += completed.Add;
        keyboard.Type("ghbd");
        Assert.Equal("прив", keyboard.Screen);
        keyboard.Type("tn ");
        Assert.Equal("привет ", keyboard.Screen);
        Assert.Single(completed);
        Assert.Equal("ghbdtn", completed[0].Original);
        keyboard.Press(0x13, false);
        Assert.Equal("ghbdtn ", keyboard.Screen);
        Assert.Equal(SelfTest.FakeKeyboard.En, keyboard.Current);
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("Github")]
    [InlineData("GitHub")]
    [InlineData("config")]
    public void EarlySwitchPreservesKnownPrefixes(string word)
    {
        var cfg = new AppConfig(); cfg.Layout.EarlySwitch = true;
        var keyboard = new SelfTest.FakeKeyboard(environment.Analyzer(cfg));
        keyboard.Type(word);
        Assert.Equal(word, keyboard.Screen);
    }

    [Fact]
    public void IdleCorrectionWaitsAndCanBeUndoneBeforeTheBoundary()
    {
        var cfg = new AppConfig(); cfg.Layout.CorrectOnIdle = true;
        var keyboard = new SelfTest.FakeKeyboard(environment.Analyzer(cfg));
        long now = 1000; keyboard.Processor.Milliseconds = () => now;
        keyboard.Type("ghbdtn"); keyboard.Processor.Tick();
        now += 899; keyboard.Processor.Tick();
        Assert.Equal("ghbdtn", keyboard.Screen);
        now++; keyboard.Processor.Tick();
        Assert.Equal("привет", keyboard.Screen);
        keyboard.Press(0x13, false);
        Assert.Equal("ghbdtn", keyboard.Screen);
        keyboard.Type(" ");
        Assert.Equal("ghbdtn ", keyboard.Screen);
    }

    [Fact]
    public void IdleCorrectionDoesNotFollowAMovedCaret()
    {
        var cfg = new AppConfig(); cfg.Layout.CorrectOnIdle = true;
        var keyboard = new SelfTest.FakeKeyboard(environment.Analyzer(cfg));
        long now = 1000; int caret = 0;
        keyboard.Processor.Milliseconds = () => now;
        keyboard.Processor.TargetReader = () => new((IntPtr)1, (IntPtr)2, 0, (IntPtr)3, caret, 10);
        keyboard.Type("ghbdtn"); keyboard.Processor.Tick();
        caret = 20; now += 1000; keyboard.Processor.Tick();
        Assert.Equal("ghbdtn", keyboard.Screen);
    }
}
