using System.Text;
using TypeSwitcher.Config;

namespace TypeSwitcher.Core;

/// <summary>
/// Служебный режим: подбор порога n-gram "естественности" по РЕАЛЬНОМУ логу правок
/// (data/corrections.jsonl). Для каждой автозамены типа Typo считает
/// Margin(результат, исходник) и метит её как отвергнутую пользователем (была
/// запись reject по тому же слову) или принятую. Печатает распределение и таблицу
/// "какой порог сколько ложных срежет ценой скольких настоящих".
///
/// Смысл: порча вида "пасхалках -> пасхалка" (кандидат - другая форма того же
/// слова) даёт margin около нуля, тогда как настоящая опечатка ("посомтри ->
/// посмотри") заметно естественнее исходника. Единственный кандидат раньше
/// проходил без всякой проверки естественности - отсюда класс ложных правок.
///
/// Запуск: TypeSwitcher.exe --margin-probe [путь к corrections.jsonl]
/// Результат: %TEMP%/typeswitcher_margin_probe.txt
/// </summary>
public static class MarginProbe
{
    private readonly record struct Sample(string Orig, string Res, double Margin, bool Rejected);

    public static int Run(string? logPath)
    {
        var store = new ConfigStore();
        var cfg = store.Load();

        string path = logPath ?? System.IO.Path.Combine(store.DataDir, cfg.Learning.File);
        if (!System.IO.File.Exists(path))
        {
            Console.WriteLine($"лог правок не найден: {path}");
            return 2;
        }

        string ruDic = store.ResolveAppPath(cfg.Dictionaries.RuHunspell);
        var ngram = NgramModel.BuildOrLoad(ruDic, store.ResolveAppPath(cfg.Dictionaries.RuFreq),
            System.IO.Path.Combine(store.DataDir, cfg.Ngram.CacheFile), cfg.Ngram.WeightByFrequency);
        if (!ngram.Loaded)
        {
            Console.WriteLine("n-gram модель не построена");
            return 2;
        }

        var corrections = new List<(string orig, string res, string kind)>();
        var rejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in System.IO.File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            string? ev = JsonField(line, "ev");
            string? orig = JsonField(line, "orig");
            if (ev == null || orig == null) continue;
            if (ev == "reject") { rejected.Add(orig); continue; }
            if (ev != "correct") continue;
            string? res = JsonField(line, "res");
            string? kind = JsonField(line, "kind");
            if (res == null || kind == null) continue;
            corrections.Add((orig, res, kind));
        }

        var samples = new List<Sample>();
        foreach (var (orig, res, kind) in corrections)
        {
            if (kind != "Typo") continue;
            string o = orig.ToLowerInvariant(), r = res.ToLowerInvariant();
            samples.Add(new Sample(o, r, ngram.Margin(r, o), rejected.Contains(o)));
        }

        var sb = new StringBuilder();
        sb.AppendLine($"лог: {path}");
        sb.AppendLine($"правок Typo: {samples.Count}, из них отвергнуто пользователем: {samples.Count(s => s.Rejected)}");
        sb.AppendLine(new string('-', 72));

        sb.AppendLine("ПОРОГ margin: сколько ложных (отвергнутых) срежет / сколько настоящих потеряет");
        foreach (double th in new[] { 0.0, 0.05, 0.1, 0.15, 0.2, 0.25, 0.3, 0.4, 0.5, 0.75, 1.0 })
        {
            int badCut = samples.Count(s => s.Rejected && s.Margin < th);
            int badTotal = samples.Count(s => s.Rejected);
            int goodCut = samples.Count(s => !s.Rejected && s.Margin < th);
            int goodTotal = samples.Count(s => !s.Rejected);
            sb.AppendLine($"  >= {th,-5:0.00}  ложных срезано {badCut,3}/{badTotal,-3}  настоящих потеряно {goodCut,4}/{goodTotal}");
        }

        sb.AppendLine(new string('-', 72));
        sb.AppendLine("ОТВЕРГНУТЫЕ (по возрастанию margin - первые кандидаты на отсечение):");
        foreach (var s in samples.Where(s => s.Rejected).OrderBy(s => s.Margin).DistinctBy(s => s.Orig))
            sb.AppendLine($"  {s.Margin,7:0.000}  {s.Orig} -> {s.Res}");

        sb.AppendLine(new string('-', 72));
        sb.AppendLine("ПРИНЯТЫЕ с НИЗКИМ margin (риск потерять при отсечении):");
        foreach (var s in samples.Where(s => !s.Rejected).OrderBy(s => s.Margin).DistinctBy(s => s.Orig).Take(60))
            sb.AppendLine($"  {s.Margin,7:0.000}  {s.Orig} -> {s.Res}");

        string report = sb.ToString();
        string outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "typeswitcher_margin_probe.txt");
        System.IO.File.WriteAllText(outPath, report);
        Console.WriteLine(report);
        Console.WriteLine($"[report -> {outPath}]");
        return 0;
    }

    // Минимальный разбор плоского JSONL-объекта: значение строкового поля или null.
    private static string? JsonField(string line, string name)
    {
        string key = "\"" + name + "\":";
        int i = line.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return null;
        i += key.Length;
        while (i < line.Length && line[i] == ' ') i++;
        if (i >= line.Length || line[i] != '"') return null;   // null или число
        i++;
        int end = line.IndexOf('"', i);
        return end < 0 ? null : line[i..end];
    }
}
