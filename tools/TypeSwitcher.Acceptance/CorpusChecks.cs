using System.Diagnostics;
using TypeSwitcher.Config;
using TypeSwitcher.Core;

namespace TypeSwitcher.Acceptance;

internal static class CorpusChecks
{
    public static void Run(TestSession session, AcceptanceReport report)
    {
        var store = new ConfigStore(session.Data, session.Root);
        var cfg = store.Load(); cfg.Languages.Enabled = ["en", "uk"];
        var loaded = DictionaryManager.Load(store, cfg);
        if (loaded.Errors.Any()) throw new InvalidOperationException(string.Join("; ", loaded.Errors));
        var analyzer = new Analyzer(loaded.Dictionaries, cfg) { LayoutDetectionEnabled = true };
        string[] keep =
        [
            "ґанок", "ґудзик", "ґрунт", "ґрунтовний", "ґрати", "ґніт", "підґрунтя", "обґрунтування", "ґрунтується",
            "їжа", "їжак", "їхній", "Україна", "український", "українською", "Київ", "київський", "Європа", "єдність",
            "поїзд", "поїздка", "надія", "мрія", "історія", "інформація", "інтерфейс", "п'ять", "п'ятниця", "м'ята",
            "м'який", "зв'язок", "об'єкт", "об'єднання", "сім'я", "пам'ять", "комп'ютер", "інтерв'ю", "під'їзд",
            "роз'яснення", "з'їзд", "від'ємний", "торф'яний", "бур'ян", "півострів", "будь-який", "по-українськи",
            "південно-західний", "жовто-блакитний", "слово", "мама", "мир", "рука", "мати", "банк", "лист", "сон", "сир",
            "ніс", "ліс", "ідея", "місто", "книга", "стіл", "світ", "мова", "лікар", "лікарня", "учитель", "учениця",
            "вчителька", "розробник", "користувач", "повідомлення", "налаштування", "збереження", "завантаження",
            "перевірка", "використання", "програма", "програмування", "української", "українському", "українськими",
            "можливість", "незалежність", "відповідальність", "сьогодні", "завтра", "вчора", "дякую", "будьмо",
            "привіт", "Вітаю", "ПРИВІТ", "ДЯКУЮ", "ЇЖАК", "ҐАНОК", "ЄДНІСТЬ", "API", "GitHub", "OAuth", "win10"
        ];
        var words = keep.SelectMany(word => word.Contains('\'') ? new[] { word, word.Replace('\'', '’'), word.Replace('\'', 'ʼ') } : new[] { word }).Distinct().ToArray();
        int falseCorrections = 0;
        var times = new List<double>();
        foreach (string word in words)
        {
            long start = Stopwatch.GetTimestamp();
            var result = analyzer.Analyze(word, LayoutTarget.Uk);
            times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            if (result.Kind == ActionKind.None) continue;
            falseCorrections++;
            report.Fail("uk-keep", $"{word} -> {result.Replacement} ({result.Reason})");
        }
        report.Note("uk-reviewed-keep", $"{words.Length} cases, {falseCorrections} automatic changes");

        (string Input, string Expected)[] fixes =
        [
            ("привт", "привіт"), ("українськкий", "український"), ("клавіатра", "клавіатура"),
            ("налаштувння", "налаштування"), ("компютер", "комп'ютер"), ("інформаця", "інформація"),
            ("повідомленя", "повідомлення"), ("докумет", "документ"), ("користувч", "користувач"),
            ("збереженя", "збереження"), ("завантаення", "завантаження"), ("переврка", "перевірка"),
            ("ПРивіт", "Привіт"), ("привіn", "привіт")
        ];
        int fixedCount = 0, missed = 0, incorrect = 0;
        foreach (var (input, expected) in fixes)
        {
            var result = analyzer.Analyze(input, LayoutTarget.Uk);
            if (result.Kind == ActionKind.None) { missed++; report.Note("uk-missed", input + " -> expected " + expected); }
            else if (result.Replacement == expected) fixedCount++;
            else { incorrect++; report.Fail("uk-wrong-correction", input + " -> " + result.Replacement + "; expected " + expected); }
        }
        report.Note("uk-fix-results", $"correct={fixedCount}/{fixes.Length}; skipped={missed}; wrong={incorrect}");
        times.Sort();
        report.Note("uk-keep-timing-ms", $"p95={times[(int)((times.Count - 1) * .95)]:F3}, max={times[^1]:F3}");
        report.Check("uk-no-false-corrections", falseCorrections == 0 && incorrect == 0);

        long beforeMemory = GC.GetTotalMemory(true);
        var stopwatch = Stopwatch.StartNew();
        int changed = 0;
        for (int iteration = 0; iteration < 5000; iteration++)
        {
            string word = words[iteration % words.Length];
            if (analyzer.Analyze(word, LayoutTarget.Uk).Kind != ActionKind.None) changed++;
        }
        long afterMemory = GC.GetTotalMemory(true);
        report.Check("uk-repeated-analysis", changed == 0, $"5000 words; elapsed={stopwatch.ElapsedMilliseconds}ms; retained-memory-delta={afterMemory - beforeMemory} bytes");
        report.Note("linguistic-coverage", "Additional manually selected engineering corpus. This is not an independent native-speaker pilot or a representative accuracy estimate.");
    }
}
