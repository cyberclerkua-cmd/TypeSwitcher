using System.Text;
using TypeSwitcher.Config;

namespace TypeSwitcher.Core;

/// <summary>
/// Служебный режим: выгрузка кандидатов для словаря-дополнения (ru_extra).
/// Берёт частотный список ru-300k и оставляет слова, которые Hunspell ru НЕ знает
/// (т.е. сейчас считаются опечатками), отфильтровав по кириллице, длине и частоте.
/// Дальше список чистится вручную/LLM и кладётся в dict/ru_extra.txt.
/// Запуск: TypeSwitcher.exe --dump-unknown [minFreq] (дефолт minFreq=8).
/// Результат: %TEMP%/typeswitcher_extra_candidates.txt ("слово частота", по убыванию).
/// </summary>
public static class CandidateDump
{
    public static int Run(int minFreq)
    {
        var store = new ConfigStore();
        var cfg = store.Load();

        var dict = new Dictionaries();
        string ruDic = store.ResolveAppPath(cfg.Dictionaries.RuHunspell);
        if (!dict.LoadRuHunspell(ruDic))
        {
            Console.WriteLine($"ru Hunspell НЕ загружен ({ruDic})");
            return 2;
        }
        // Пользовательские списки НЕ грузим: IsValidRu тогда = чистая проверка Hunspell.

        string freqPath = store.ResolveAppPath(cfg.Dictionaries.RuFreq);
        if (!System.IO.File.Exists(freqPath))
        {
            Console.WriteLine($"частотный список не найден ({freqPath})");
            return 2;
        }

        var kept = new List<(string word, long freq)>();
        long total = 0, skippedNonCyr = 0, skippedShort = 0, skippedLowFreq = 0, knownByHunspell = 0;

        foreach (var raw in System.IO.File.ReadLines(freqPath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int sp = line.IndexOf(' ');
            if (sp <= 0) continue;
            string word = line[..sp].Trim().ToLowerInvariant();
            if (word.Length == 0) continue;
            if (!long.TryParse(line[(sp + 1)..].Trim(), out long freq)) continue;
            total++;

            if (freq < minFreq) { skippedLowFreq++; continue; }
            if (word.Length < 3) { skippedShort++; continue; }
            if (!IsPureCyrillic(word)) { skippedNonCyr++; continue; }
            if (dict.IsValidRu(word)) { knownByHunspell++; continue; }  // Hunspell знает — не кандидат

            kept.Add((word, freq));
        }

        kept.Sort((a, b) => b.freq.CompareTo(a.freq));

        var sb = new StringBuilder();
        foreach (var (word, freq) in kept)
            sb.Append(word).Append(' ').Append(freq).Append('\n');

        string outPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "typeswitcher_extra_candidates.txt");
        System.IO.File.WriteAllText(outPath, sb.ToString());

        Console.WriteLine($"всего строк частотного списка: {total}");
        Console.WriteLine($"  отсев по частоте (<{minFreq}): {skippedLowFreq}");
        Console.WriteLine($"  отсев по длине (<3): {skippedShort}");
        Console.WriteLine($"  отсев не-кириллица: {skippedNonCyr}");
        Console.WriteLine($"  знает Hunspell: {knownByHunspell}");
        Console.WriteLine($"КАНДИДАТОВ (нет в Hunspell): {kept.Count}");
        Console.WriteLine($"[-> {outPath}]");
        return 0;
    }

    /// <summary>
    /// Отфильтровать список слов, оставив только те, которых Hunspell ru НЕ знает
    /// (корректное определение словаря-дополнения). Запуск:
    /// TypeSwitcher.exe --filter-unknown &lt;in&gt; &lt;out&gt;.
    /// </summary>
    public static int FilterUnknown(string inPath, string outPath)
    {
        var store = new ConfigStore();
        var cfg = store.Load();
        var dict = new Dictionaries();
        if (!dict.LoadRuHunspell(store.ResolveAppPath(cfg.Dictionaries.RuHunspell)))
        {
            Console.WriteLine("ru Hunspell НЕ загружен");
            return 2;
        }
        if (!System.IO.File.Exists(inPath))
        {
            Console.WriteLine($"входной файл не найден: {inPath}");
            return 2;
        }

        int kept = 0, known = 0;
        var sb = new StringBuilder();
        foreach (var raw in System.IO.File.ReadLines(inPath))
        {
            string word = raw.Trim().ToLowerInvariant();
            if (word.Length == 0 || word.StartsWith('#')) continue;
            if (dict.IsValidRu(word)) { known++; continue; }   // Hunspell знает — не нужно
            sb.Append(word).Append('\n');
            kept++;
        }
        System.IO.File.WriteAllText(outPath, sb.ToString());
        Console.WriteLine($"вход: {kept + known}, знает Hunspell: {known}, оставлено (неизвестных): {kept}");
        Console.WriteLine($"[-> {outPath}]");
        return 0;
    }

    private static bool IsPureCyrillic(string s)
    {
        foreach (char c in s)
        {
            bool ru = (c >= 'а' && c <= 'я') || c == 'ё' || c == '-';
            if (!ru) return false;
        }
        // Дефис допускаем внутри, но не на краях и не одиночный.
        return s[0] != '-' && s[^1] != '-';
    }
}
