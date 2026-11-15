using TypeSwitcher.Config;

namespace TypeSwitcher.Core;

public sealed partial class Analyzer
{
    private static Decision Keep(LayoutTarget source, string reason) => Decision.None with { Source = source, Reason = reason };
    private static readonly HashSet<string> ShortRussian = ["и", "в", "у", "на", "по", "от", "до", "за", "не", "но", "мы", "вы", "он", "да"];
    private static readonly HashSet<string> ShortUkrainian = ["і", "в", "у", "на", "по", "до", "за", "не", "ми", "ви", "це", "та"];

    public Decision MatchLayoutRule(string word, LayoutTarget source)
    {
        if (IsStopped(word, source) || !IsLanguageEnabled(source)) return Decision.None;
        var rule = LayoutRule.Find(_cfg.Layout.Rules, word, source);
        if (rule == null) return Decision.None;
        if (rule.Action == "keep") return Keep(source, "layout-rule-keep");
        var target = LanguageProfile.Find(rule.Target)!.Target;
        if (source == target || !CanSwitchTo(target)) return Keep(source, "layout-rule-unavailable");
        string? mapped = LayoutMap.MapBetween(word, source, target);
        return mapped != null && mapped != word ? new(ActionKind.Layout, mapped, target, source, "layout-rule") : Keep(source, "layout-rule-unavailable");
    }

    public LayoutTarget RecognizeCompletedWord(string word, LayoutTarget source)
    {
        if (word.Any(c => char.IsDigit(c) || c is '_' or '=' or '/' or '\\') || TokenClassifier.IsTechnical(word)) return LayoutTarget.None;
        if (IsLanguageEnabled(source) && _dict.IsValid(source, word)) return source;
        var valid = LanguageOrder.Where(language => _dict.IsValid(language, word)).ToArray();
        return valid.Length == 1 ? valid[0] : LayoutTarget.None;
    }

    public ContextAnalysis AnalyzeWithContext(string word, LayoutTarget source, RecentContext? context, long now)
    {
        Decision baseline = Analyze(word, source);
        if (_cfg.Layout.ContextMode == "off" || context == null || baseline.Kind != ActionKind.None)
            return new(baseline, baseline, baseline.Kind == ActionKind.None ? ContextAction.Keep : ContextAction.Convert);
        if (!LayoutDetectionEnabled || !_cfg.Layout.Enabled || !IsLanguageEnabled(source) || IsProtected(word, source)
            || _dict.IsValid(source, word) || TokenClassifier.IsTechnical(word) || LayoutRule.Find(_cfg.Layout.Rules, word, source) != null)
            return new(baseline, baseline, ContextAction.Keep);
        var preferred = context.Preferred(now);
        if (preferred == LayoutTarget.None || preferred == source || !CanSwitchTo(preferred))
            return new(baseline, baseline, word.Length <= 2 ? ContextAction.Wait : ContextAction.Keep);
        string? mapped = LayoutMap.MapBetween(word, source, preferred);
        bool shortWord = mapped != null && word.Length <= 2 && (preferred == LayoutTarget.Ru ? ShortRussian.Contains(mapped.ToLowerInvariant())
            : preferred == LayoutTarget.Uk && ShortUkrainian.Contains(mapped.ToLowerInvariant()));
        if (mapped == null || !_dict.IsValid(preferred, mapped) || baseline.Reason != "ambiguous-layout" && !shortWord)
            return new(baseline, baseline, ContextAction.Keep);
        var proposed = new Decision(ActionKind.Layout, mapped, preferred, source, "context-layout");
        return new(_cfg.Layout.ContextMode == "assist" ? proposed : baseline, proposed,
            _cfg.Layout.ContextMode == "assist" ? ContextAction.Convert : ContextAction.Suggest);
    }

    /// <summary>Conservative prefix model: the source has no known continuation; at least three target continuations exist.</summary>
    public Decision AnalyzePrefix(string prefix, LayoutTarget source, RecentContext? context = null, long now = 0)
    {
        if (!_cfg.Layout.EarlySwitch || prefix.Length < _cfg.Layout.EarlyMinLength || prefix.Length > 12 || !prefix.All(char.IsLetter)
            || !IsLanguageEnabled(source) || IsProtected(prefix, source) || TokenClassifier.IsTechnical(prefix)
            || _dict.PrefixCount(source, prefix) != 0) return Keep(source, "prefix-wait");
        // A keep-rule may name a word whose ending is not typed yet.
        if (_cfg.Layout.Rules.Any(r => r.Enabled && r.Action == "keep" && (r.Source == "global" || r.Source == LanguageProfile.Find(source)?.Id)
            && (r.Match == "contains" || r.Pattern.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || prefix.StartsWith(r.Pattern, StringComparison.OrdinalIgnoreCase))))
            return Keep(source, "layout-rule-keep");
        var candidates = new List<Decision>();
        foreach (var target in LanguageOrder.Where(t => t != source && CanSwitchTo(t)))
        {
            string? mapped = LayoutMap.MapBetween(prefix, source, target);
            if (mapped != null && _dict.PrefixCount(target, mapped) >= 3) candidates.Add(new(ActionKind.Layout, mapped, target, source, "early-prefix"));
        }
        if (candidates.Count == 1) return candidates[0];
        var preferred = _cfg.Layout.ContextMode == "assist" ? context?.Preferred(now) ?? LayoutTarget.None : LayoutTarget.None;
        return candidates.FirstOrDefault(c => c.Layout == preferred, Keep(source, "prefix-wait"));
    }
}
