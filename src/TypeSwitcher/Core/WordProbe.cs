using System.IO;
using System.Text;
using TypeSwitcher.Config;

namespace TypeSwitcher.Core;

/// <summary>Вердикты языковых словарей и причины решений: --check слова --language uk.</summary>
public static class WordProbe
{
    public static int Run(IReadOnlyList<string> words, string? language = null)
    {
        var store = new ConfigStore();
        var cfg = store.Load();
        store.EnsureUserLists();
        if (language != null && LanguageProfile.Find(language) == null) return Report("--language: en, ru или uk", 2);
        cfg.Languages.Enabled = ["en", "ru", "uk"];
        var loaded = DictionaryManager.Load(store, cfg);
        if (loaded.Errors.Any() || store.LoadProblem != null) return Report(string.Join("\n", loaded.Errors) + "\n" + store.LoadProblem, 2);
        var dict = loaded.Dictionaries;
        var analyzer = new Analyzer(dict, cfg) { LayoutDetectionEnabled = true };
        var expanded = new List<string>();
        foreach (string word in words)
        {
            if (word.StartsWith('@'))
            {
                if (!File.Exists(word[1..])) return Report("Файл не найден: " + word[1..], 2);
                expanded.AddRange(File.ReadLines(word[1..]).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')));
            }
            else expanded.Add(word);
        }
        if (expanded.Count == 0) return Report("Укажите слова после --check или @файл.", 2);
        var report = new StringBuilder();
        report.AppendLine($"Источник: {language ?? "по алфавиту"}; проверяются словари ru/en/uk.");
        foreach (string word in expanded)
        {
            var source = LanguageProfile.Find(language)?.Target ?? analyzer.GuessLanguage(word);
            var decision = analyzer.Analyze(word, source);
            report.AppendLine($"{word}: ru={dict.IsValidRu(word)}, en={dict.IsValidEn(word)}, uk={dict.IsValid(LayoutTarget.Uk, word)}, source={source}");
            report.AppendLine($"  {decision.Kind}: {(decision.Kind == ActionKind.None ? word : decision.Replacement)}; target={decision.Layout}; reason={decision.Reason}");
            if (source != LayoutTarget.En && !dict.IsValid(source, word))
            {
                var options = new TypoOptions(cfg.Typo.MaxEditDistance, cfg.Typo.DominanceRatio, cfg.Typo.MinFrequency,
                    cfg.Ngram.Enabled && cfg.Typo.UseNgram, cfg.Typo.NgramWeight, cfg.Typo.FreqWeight, cfg.Typo.ScoreGap);
                foreach (var candidate in dict.RankCandidates(source, word, options).Take(5))
                    report.AppendLine($"    {candidate.Word}: distance={candidate.Distance}, frequency={candidate.Frequency}, score={candidate.Score:0.000}");
            }
        }
        return Report(report.ToString(), 0);
    }

    private static int Report(string text, int code)
    {
        string path = Path.Combine(Path.GetTempPath(), "typeswitcher_word_probe.txt");
        File.WriteAllText(path, text);
        Console.WriteLine(text);
        Console.WriteLine($"[report -> {path}]");
        return code;
    }
}
