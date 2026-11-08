using System.Text;
using TypeSwitcher.Config;

namespace TypeSwitcher.Core;

/// <summary>
/// Диагностика триграммной модели: строит модели из разных источников и сравнивает
/// баллы "естественности" на реальных словах против мусора-раскладки. Чем больше
/// разрыв реальное-мусор, тем лучше модель отличает язык от шума.
/// Запуск: TypeSwitcher.exe --ngram-probe [доп_частотный_файл]
/// (доп. файл в формате "слово частота" сравнивается наравне с нашим ru-300k).
/// </summary>
public static class NgramProbe
{
    public static int Run(string? extraFreqPath = null)
    {
        var store = new ConfigStore();
        var cfg = store.Load();
        string ruDic = store.ResolveAppPath(cfg.Dictionaries.RuHunspell);
        string freq = store.ResolveAppPath(cfg.Dictionaries.RuFreq);

        var models = new List<(string name, NgramModel m)>
        {
            (".dic (вес 1)", NgramModel.BuildOrLoad(ruDic, freq,
                System.IO.Path.Combine(store.DataDir, "ngram_probe_dic.cache"), weightByFreq: false)),
            ("ru-300k (freq)", NgramModel.BuildOrLoad(ruDic, freq,
                System.IO.Path.Combine(store.DataDir, "ngram_probe_freq.cache"), weightByFreq: true)),
        };
        if (!string.IsNullOrEmpty(extraFreqPath) && System.IO.File.Exists(extraFreqPath))
        {
            models.Add(($"доп ({System.IO.Path.GetFileName(extraFreqPath)})",
                NgramModel.BuildOrLoad(ruDic, extraFreqPath,
                    System.IO.Path.Combine(store.DataDir, "ngram_probe_extra.cache"), weightByFreq: true)));
        }

        string[] real =
        {
            "привет", "работа", "сегодня", "человек", "документ", "сообщение",
            "программа", "клавиатура", "разработчик", "оптимизация",
        };
        string[] garbage =
        {
            "ыфпва", "джлорп", "фывапр", "нгшщзх", "цукенг", "ждлорп", "ячсмит", "укенгш",
        };

        var sb = new StringBuilder();
        sb.AppendLine("Балл 'естественности' (средний лог/символ; выше = правдоподобнее)");
        foreach (var (name, m) in models) sb.AppendLine($"  {name}: {m.TrigramCount} триграмм");
        sb.AppendLine(new string('-', 20 + models.Count * 14));

        var header = new StringBuilder($"{"слово",-16}");
        foreach (var (name, _) in models) header.Append($"{name,14}");
        sb.AppendLine(header.ToString());

        var realAvg = new double[models.Count];
        var garbAvg = new double[models.Count];

        sb.AppendLine("РЕАЛЬНЫЕ:");
        foreach (var w in real)
        {
            var line = new StringBuilder($"  {w,-14}");
            for (int i = 0; i < models.Count; i++)
            {
                double s = models[i].m.Score(w);
                realAvg[i] += s;
                line.Append($"{s,14:0.000}");
            }
            sb.AppendLine(line.ToString());
        }
        sb.AppendLine("МУСОР:");
        foreach (var w in garbage)
        {
            var line = new StringBuilder($"  {w,-14}");
            for (int i = 0; i < models.Count; i++)
            {
                double s = models[i].m.Score(w);
                garbAvg[i] += s;
                line.Append($"{s,14:0.000}");
            }
            sb.AppendLine(line.ToString());
        }
        for (int i = 0; i < models.Count; i++) { realAvg[i] /= real.Length; garbAvg[i] /= garbage.Length; }

        sb.AppendLine(new string('-', 20 + models.Count * 14));
        Row(sb, "среднее РЕАЛЬНЫЕ", realAvg);
        Row(sb, "среднее МУСОР", garbAvg);
        var gap = new double[models.Count];
        for (int i = 0; i < models.Count; i++) gap[i] = realAvg[i] - garbAvg[i];
        Row(sb, "РАЗРЫВ (real-мусор)", gap);
        sb.AppendLine("Больше разрыв => модель увереннее отделяет язык от шума.");

        string report = sb.ToString();
        string outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "typeswitcher_ngram_probe.txt");
        System.IO.File.WriteAllText(outPath, report);
        Console.WriteLine(report);
        Console.WriteLine($"[report -> {outPath}]");
        return 0;
    }

    private static void Row(StringBuilder sb, string label, double[] vals)
    {
        var line = new StringBuilder($"{label,-16}");
        foreach (var v in vals) line.Append($"{v,14:0.000}");
        sb.AppendLine(line.ToString());
    }
}
