using TypeSwitcher.Config;

namespace TypeSwitcher.Core;

public enum ActionKind { None, Typo, Rule, Layout }
public enum LayoutTarget { None, Ru, En, Uk }

public readonly record struct Decision(ActionKind Kind, string Replacement, LayoutTarget Layout = LayoutTarget.None,
    LayoutTarget Source = LayoutTarget.None, string Reason = "")
{
    public static readonly Decision None = new(ActionKind.None, string.Empty);
}

/// <summary>Пословный анализ с явным исходным языком. Первое уверенное правило выигрывает.</summary>
public sealed class Analyzer
{
    private Dictionaries _dict;
    private AppConfig _cfg;
    public bool LayoutDetectionEnabled { get; set; }
    public int LayoutMinWordLength => _cfg.Layout.MinWordLength;
    public IReadOnlySet<LayoutTarget>? AvailableLayouts { get; set; }
    public IReadOnlyList<LayoutTarget> LanguageOrder => _cfg.Languages.Order
        .Where(_cfg.Languages.Enabled.Contains).Select(id => LanguageProfile.Find(id)!.Target).ToArray();

    public Analyzer(Dictionaries dict, AppConfig cfg) { _dict = dict; _cfg = cfg; }
    public void UpdateConfig(AppConfig cfg) => _cfg = cfg;
    public void UpdateDictionaries(Dictionaries dictionaries) => _dict = dictionaries;
    public bool IsLanguageEnabled(LayoutTarget language) => LanguageProfile.Find(language) is { } profile && _cfg.Languages.Enabled.Contains(profile.Id);
    public bool IsProtected(string word, LayoutTarget language) => _dict.IsStopWord(word, language) || _dict.IsMyWord(word, language);
    public bool IsStopped(string word, LayoutTarget language) => _dict.IsStopWord(word, language);

    public LayoutTarget GuessLanguage(string word)
    {
        if (!word.Any(LanguageProfile.IsCyrillic)) return LayoutTarget.En;
        if (IsLanguageEnabled(LayoutTarget.Uk) && word.Any(c => LanguageProfile.Ukrainian.Contains(c) && !LanguageProfile.Russian.Contains(c))) return LayoutTarget.Uk;
        return IsLanguageEnabled(LayoutTarget.Ru) ? LayoutTarget.Ru : IsLanguageEnabled(LayoutTarget.Uk) ? LayoutTarget.Uk : LayoutTarget.None;
    }

    public Decision MatchRule(string token, LayoutTarget source = LayoutTarget.None)
    {
        if (source != LayoutTarget.None && !IsLanguageEnabled(source)) return Decision.None;
        if (string.IsNullOrEmpty(token) || _dict.IsStopWord(token, source)) return Decision.None;
        if (_dict.TryRule(token, out var to, source))
        {
            string replacement = CaseHelper.ApplyCasePattern(token, to);
            if (replacement != token) return new(ActionKind.Rule, replacement, Source: source, Reason: "rule");
        }
        return Decision.None;
    }

    public Decision Analyze(string word, LayoutTarget source = LayoutTarget.None)
    {
        if (string.IsNullOrEmpty(word)) return Decision.None;
        if (source == LayoutTarget.None) source = GuessLanguage(word);
        if (!IsLanguageEnabled(source)) return Decision.None;
        // Приложения по-разному удаляют составной графемный кластер Backspace.
        // Нормализацию используем для поиска, но такие последовательности не перенабираем.
        if (word.Any(LanguageProfile.IsCombining)) return Decision.None with { Source = source, Reason = "combining-marks" };
        if (_dict.IsStopWord(word, source)) return Decision.None;

        string? twoCaps = _cfg.Case.FixTwoCapitals ? CaseHelper.FixTwoCapitals(word) : null;
        if (twoCaps == null) return AnalyzeCore(word, source);
        string lower = LanguageProfile.Normalize(word).ToLowerInvariant();
        string tail = lower[1..];
        if (tail.Length >= 4 && _dict.IsValid(source, tail)) return AnalyzeCore(word, source);
        Decision result = AnalyzeCore(twoCaps, source);
        if (result.Kind != ActionKind.None) return result;
        if (_dict.TryRule(lower, out _, source)) return Decision.None;
        bool valid = _dict.IsValid(source, lower) && (source != LayoutTarget.En || !_dict.IsEnExtra(lower));
        return valid ? new(ActionKind.Typo, twoCaps, Source: source, Reason: "case") : result;
    }

    private Decision AnalyzeCore(string word, LayoutTarget source)
    {
        if (_dict.IsStopWord(word, source)) return Decision.None;
        if (_dict.TryRule(word, out _, source)) return MatchRule(word, source);
        if (_dict.IsMyWord(word, source)) return Decision.None;
        string lower = LanguageProfile.Normalize(word).ToLowerInvariant();

        if (LayoutDetectionEnabled && _cfg.Layout.Enabled)
        {
            Decision layout = DetectLayout(word, lower, source);
            if (layout.Kind != ActionKind.None || layout.Reason == "ambiguous-layout") return layout;
            if (_cfg.Layout.NormalizeMixedScript)
            {
                Decision mixed = NormalizeMixedScript(word, source);
                if (mixed.Kind != ActionKind.None) return mixed;
            }
        }

        if (!_cfg.Typo.Enabled || word.Length < _cfg.Typo.MinWordLength || source == LayoutTarget.En) return Decision.None;
        if (LanguageProfile.Find(source) is not { } profile || !profile.IsWord(word) || HasInnerCapital(word)) return Decision.None;
        if (_dict.IsValid(source, word)) return Decision.None;
        string? suggestion = _dict.SuggestTypo(source, lower, TypoOpts());
        if (suggestion == null) return Decision.None;
        string corrected = LanguageProfile.MatchApostrophe(word, CaseHelper.ApplyCasePattern(word, suggestion));
        return corrected == word ? Decision.None : new(ActionKind.Typo, corrected, Source: source, Reason: "spelling");
    }

    private bool CanSwitchTo(LayoutTarget target) => LayoutDetectionEnabled && _cfg.Layout.Enabled && IsLanguageEnabled(target)
        && _dict.IsLoaded(target) && (AvailableLayouts == null || AvailableLayouts.Contains(target));

    /// <summary>Проверка уже отрендеренной гипотезы (сырой сегмент с пунктуацией).</summary>
    public bool IsLayoutSwap(string current, string targetText, LayoutTarget target, LayoutTarget source = LayoutTarget.None)
    {
        if (source == LayoutTarget.None) source = target == LayoutTarget.En ? GuessLanguage(current) : LayoutTarget.En;
        if (source == target || !CanSwitchTo(target) || !IsLanguageEnabled(source) || targetText.Length < LayoutMinWordLength) return false;
        if (_dict.IsStopWord(current, source) || _dict.IsMyWord(current, source)) return false;
        if (_cfg.Layout.ProtectTechnical && TokenClassifier.IsTechnical(current)) return false;
        return _dict.IsValid(target, targetText.ToLowerInvariant()) && !_dict.IsValid(source, current.ToLowerInvariant());
    }

    private Decision DetectLayout(string word, string lower, LayoutTarget source)
    {
        if (LanguageProfile.Find(source) is not { } sourceProfile) return Decision.None;
        if (word.Length < LayoutMinWordLength || (_cfg.Layout.ProtectTechnical && TokenClassifier.IsTechnical(word))) return Decision.None;
        if (_dict.IsValid(source, lower)) return Decision.None;

        var exact = new List<Decision>();
        foreach (LayoutTarget target in LanguageOrder.Where(t => t != source && CanSwitchTo(t)))
        {
            string? mapped = LayoutMap.MapBetween(word, source, target);
            if (mapped == null || mapped == word) continue;
            if (_dict.IsValid(target, mapped.ToLowerInvariant())) exact.Add(new(ActionKind.Layout, mapped, target, source, "dictionary-layout"));
            else if (target == LayoutTarget.En && _cfg.Layout.SwitchKnownDomainExt && LooksLikeDomainOrFile(mapped.ToLowerInvariant()))
                exact.Add(new(ActionKind.Layout, mapped, target, source, "domain-layout"));
        }
        if (exact.Count == 1) return exact[0];
        if (exact.Count > 1) return Decision.None with { Reason = "ambiguous-layout" };

        // Ошибка раскладки вместе с опечаткой. Баллы независимых моделей не сравниваем:
        // нужен единственный язык с прошедшим его собственный порог кандидатом.
        if (source != LayoutTarget.En || !sourceProfile.IsWord(word) || !_cfg.Layout.FixTypoOnSwitch || !_cfg.Typo.Enabled || word.Length < _cfg.Layout.FixTypoMinLength) return Decision.None;
        var fuzzy = new List<Decision>();
        foreach (LayoutTarget target in LanguageOrder.Where(t => t != LayoutTarget.En && CanSwitchTo(t)))
        {
            string? mapped = LayoutMap.MapBetween(lower, source, target);
            if (mapped == null || !LanguageProfile.Find(target)!.IsWord(mapped)) continue;
            string? suggestion = _dict.SuggestTypo(target, mapped, TypoOpts());
            if (suggestion != null && suggestion != mapped)
                fuzzy.Add(new(ActionKind.Layout, CaseHelper.ApplyCasePattern(word, suggestion), target, source, "layout-and-spelling"));
        }
        return fuzzy.Count == 1 ? fuzzy[0] : fuzzy.Count > 1 ? Decision.None with { Reason = "ambiguous-layout" } : Decision.None;
    }

    private Decision NormalizeMixedScript(string word, LayoutTarget source)
    {
        if (!TokenClassifier.IsMixedScript(word) || (_cfg.Layout.ProtectTechnical && TokenClassifier.IsTechnical(word))) return Decision.None;
        int cyrillic = word.Count(LanguageProfile.IsCyrillic), latin = word.Count(LanguageProfile.English.Contains);
        bool toCyrillic = cyrillic > latin || (cyrillic == latin && LanguageProfile.IsCyrillic(word[^1]));
        IEnumerable<LayoutTarget> targets = toCyrillic
            ? source is LayoutTarget.Ru or LayoutTarget.Uk ? new[] { source } : LanguageOrder.Where(t => t != LayoutTarget.En)
            : new[] { LayoutTarget.En };
        var candidates = new List<string>();
        foreach (var target in targets.Where(IsLanguageEnabled))
        {
            string? normalized = LayoutMap.NormalizeToLanguage(word, target);
            if (normalized != null && normalized != word && _dict.IsValid(target, normalized.ToLowerInvariant())) candidates.Add(normalized);
        }
        return candidates.Count == 1 ? new(ActionKind.Typo, candidates[0], Source: source, Reason: "mixed-script") : Decision.None;
    }

    private TypoOptions TypoOpts() => new(_cfg.Typo.MaxEditDistance, _cfg.Typo.DominanceRatio, _cfg.Typo.MinFrequency,
        _cfg.Ngram.Enabled && _cfg.Typo.UseNgram, _cfg.Typo.NgramWeight, _cfg.Typo.FreqWeight, _cfg.Typo.ScoreGap);

    private static bool HasInnerCapital(string word) => word.Any(char.IsLower) && word.Skip(1).Any(char.IsUpper);

    private static readonly HashSet<string> KnownDomainSuffix = new(StringComparer.Ordinal)
    {
        "com", "ru", "ua", "org", "net", "io", "dev", "ai", "me", "app", "co", "info", "xyz", "biz", "pro", "su", "online", "site", "store", "tech", "cloud",
        "txt", "md", "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "csv", "log", "ini", "cfg", "json", "xml", "yaml", "yml", "html", "htm", "css",
        "scss", "js", "ts", "jsx", "tsx", "py", "cs", "cpp", "java", "go", "rs", "php", "rb", "sh", "bat", "ps1", "sql", "png", "jpg", "jpeg", "gif", "svg",
        "webp", "ico", "zip", "rar", "gz", "exe", "msi", "dll", "mp3", "mp4", "avi", "mkv", "wav"
    };

    private static bool LooksLikeDomainOrFile(string latin)
    {
        int dot = latin.LastIndexOf('.');
        return dot > 0 && dot < latin.Length - 1 && KnownDomainSuffix.Contains(latin[(dot + 1)..])
            && latin.All(c => c is >= 'a' and <= 'z' or '.') && latin.Split('.').All(s => s.Length > 0);
    }
}
