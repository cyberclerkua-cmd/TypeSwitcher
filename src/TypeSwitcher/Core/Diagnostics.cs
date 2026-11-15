using System.Text.RegularExpressions;
using TypeSwitcher.Config;

namespace TypeSwitcher.Core;

public sealed record WordDiagnostic(string Original, LayoutTarget Source, ContextAnalysis Analysis,
    IReadOnlyList<TypoCandidate> Candidates, Decision Prefix);

/// <summary>In-memory analyzer preview. Only the explicitly supplied sample is processed.</summary>
public static class Diagnostics
{
    public static IReadOnlyList<WordDiagnostic> Analyze(Dictionaries dictionaries, AppConfig config, string sample, LayoutTarget source,
        CancellationToken cancellation = default)
    {
        if (sample.Length > 4096) throw new ArgumentException("Sample is too long.", nameof(sample));
        var analyzer = new Analyzer(dictionaries, ConfigStore.Clone(config)) { LayoutDetectionEnabled = config.Layout.Enabled };
        var context = new RecentContext();
        var result = new List<WordDiagnostic>();
        foreach (Match match in Regex.Matches(sample, @"\S+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)).Take(64))
        {
            cancellation.ThrowIfCancellationRequested();
            string word = match.Value;
            var language = source == LayoutTarget.None ? analyzer.GuessLanguage(word) : source;
            var analysis = analyzer.AnalyzeWithContext(word, language, context, result.Count);
            var options = new TypoOptions(config.Typo.MaxEditDistance, config.Typo.DominanceRatio, config.Typo.MinFrequency,
                config.Ngram.Enabled && config.Typo.UseNgram, config.Typo.NgramWeight, config.Typo.FreqWeight, config.Typo.ScoreGap);
            var candidates = LanguageProfile.Find(language) is { } profile && profile.IsWord(word) && !dictionaries.IsValid(language, word)
                ? dictionaries.RankCandidates(language, word, options).Take(5).ToArray() : [];
            result.Add(new(word, language, analysis, candidates, analyzer.AnalyzePrefix(word, language, context, result.Count)));
            var decision = analysis.Applied;
            context.Add(analyzer.RecognizeCompletedWord(decision.Kind == ActionKind.None ? word : decision.Replacement,
                decision.Layout == LayoutTarget.None ? language : decision.Layout), result.Count);
        }
        return result;
    }
}
