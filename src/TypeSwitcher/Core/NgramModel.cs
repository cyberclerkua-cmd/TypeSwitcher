using System.IO;
using System.Text;
using System.Security.Cryptography;

namespace TypeSwitcher.Core;

/// <summary>
/// Символьная триграммная модель русского языка (add-1 / Лаплас) для оценки
/// "естественности" слова. В отличие от списка слов даёт осмысленный балл и для
/// форм, которых нет в словаре (флективный русский), поэтому решает две болячки
/// частотно-списочного подхода: ложную правку нормальных слов и слабый выбор
/// "на что менять".
///
/// Слово оборачивается граничными маркерами (^^слово$) и режется на триграммы;
/// балл = СРЕДНИЙ ln((count+1)/(total+vocab)) по триграммам. Решения принимаются
/// НЕ по абсолютному баллу, а по <see cref="Margin"/> между кандидатом и исходным
/// словом.
///
/// Обучение: (а) из частотного списка "слово частота" с весом по частоте — тогда
/// счётчики триграмм воспроизводят их реальную встречаемость в тексте (как если бы
/// учили на живом корпусе); (б) резервно из ru_RU.dic (каждая лемма с весом 1).
/// Модель кешируется на диск; пересборка при смене источника или файла.
/// </summary>
public sealed class NgramModel
{
    private const int N = 3;
    private static readonly char Start = (char)1; // маркер начала слова (^)
    private static readonly char End = (char)2;   // маркер конца слова ($)
    private const uint Magic = 0x324E5452; // "RTN2"
    private const int Version = 3;

    private readonly Dictionary<string, long> _counts;
    private readonly long _total;
    private readonly int _vocab;
    private readonly double _logDenom;  // ln(total+vocab) - предвычислено
    private readonly double _floorLogP; // ln(1/(total+vocab)) для невиданных триграмм

    public bool Loaded => _vocab > 0;
    public int TrigramCount => _vocab;

    private NgramModel(Dictionary<string, long> counts, long total)
    {
        _counts = counts;
        _total = total;
        _vocab = counts.Count;
        double denom = _total + _vocab;
        _logDenom = denom > 0 ? Math.Log(denom) : 0;
        _floorLogP = denom > 0 ? -_logDenom : 0;
    }

    /// <summary>
    /// Средний лог-вероятности триграмм слова ("естественность" на символ). Именно
    /// СРЕДНИЙ, а не сумма: сумма штрафует длину, из-за чего короткие кандидаты
    /// (удаления букв) всегда бы выигрывали. Среднее длинно-нейтрально.
    /// </summary>
    public double Score(string word)
    {
        if (string.IsNullOrEmpty(word) || _vocab == 0) return 0;
        string padded = Pad(word);
        double sum = 0;
        int n = 0;
        for (int i = 0; i + N <= padded.Length; i++)
        {
            string tri = padded.Substring(i, N);
            if (_counts.TryGetValue(tri, out long c))
                sum += Math.Log(c + 1) - _logDenom;
            else
                sum += _floorLogP;
            n++;
        }
        return n > 0 ? sum / n : 0;
    }

    /// <summary>
    /// Насколько кандидат "естественнее" исходного слова (candidate - baseline).
    /// Положительное значение - кандидат правдоподобнее; на этом строятся пороги.
    /// </summary>
    public double Margin(string candidate, string baseline)
        => Score(candidate) - Score(baseline);

    private static string Pad(string word)
    {
        var sb = new StringBuilder(word.Length + N);
        sb.Append(Start).Append(Start);
        sb.Append(word);
        sb.Append(End);
        return sb.ToString();
    }

    // ---- Построение / кеш ------------------------------------------------

    /// <summary>
    /// Загрузить модель из кеша (если он актуален выбранному источнику) либо построить
    /// и сохранить кеш. При <paramref name="weightByFreq"/> и наличии частотного файла
    /// учим на нём с весом по частоте; иначе на .dic (вес 1). Ключ кеша учитывает режим
    /// и штамп источника, поэтому смена настройки перестраивает модель.
    /// </summary>
    public static NgramModel BuildOrLoad(string dicPath, string freqPath, string cachePath, bool weightByFreq,
        LayoutTarget language = LayoutTarget.Ru)
    {
        bool useFreq = weightByFreq && File.Exists(freqPath);
        string sourcePath = useFreq ? freqPath : dicPath;
        byte mode = (byte)(useFreq ? 1 : 0);

        try
        {
            if (File.Exists(sourcePath) && File.Exists(cachePath))
            {
                var cached = TryLoadCache(cachePath, sourcePath, mode, language);
                if (cached != null) return cached;
            }
        }
        catch { /* битый кеш - перестроим */ }

        var words = useFreq ? FreqWords(freqPath) : DicWords(dicPath);
        var model = BuildFromWords(words, language);
        try { if (model.Loaded) SaveCache(cachePath, sourcePath, mode, model, language); }
        catch { /* нет прав на запись кеша - не критично */ }
        return model;
    }

    public static NgramModel BuildFromWords(IEnumerable<(string word, long weight)> words, LayoutTarget language = LayoutTarget.Ru)
    {
        var profile = LanguageProfile.Find(language) ?? throw new ArgumentException("Unknown language.", nameof(language));
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        long total = 0;
        foreach (var (rawWord, weight) in words)
        {
            string word = LanguageProfile.Normalize(rawWord).ToLowerInvariant();
            if (weight <= 0 || !profile.IsWord(word)) continue;
            string padded = Pad(word);
            for (int i = 0; i + N <= padded.Length; i++)
            {
                string tri = padded.Substring(i, N);
                counts.TryGetValue(tri, out long c);
                counts[tri] = c + weight;
                total += weight;
            }
        }
        return new NgramModel(counts, total);
    }

    // Леммы Hunspell .dic (первая строка - число слов; аффиксы после '/'), вес 1.
    private static IEnumerable<(string, long)> DicWords(string dicPath)
    {
        if (!File.Exists(dicPath)) yield break;
        foreach (var raw in File.ReadLines(dicPath))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int slash = line.IndexOf('/');
            string word = (slash >= 0 ? line[..slash] : line).Trim().ToLowerInvariant();
            yield return (word, 1);
        }
    }

    // Частотный список "слово частота", вес = частота (воспроизводит корпусную
    // встречаемость триграмм).
    private static IEnumerable<(string, long)> FreqWords(string freqPath)
    {
        if (!File.Exists(freqPath)) yield break;
        foreach (var raw in File.ReadLines(freqPath))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int sp = line.IndexOf(' ');
            if (sp <= 0) continue;
            string word = line[..sp].Trim().ToLowerInvariant();
            if (!long.TryParse(line[(sp + 1)..].Trim(), out long freq)) continue;
            yield return (word, freq);
        }
    }

    private static bool IsCyrillicWord(string w)
    {
        if (w.Length == 0) return false;
        foreach (char c in w)
        {
            bool ru = (c >= 'а' && c <= 'я') || c == 'ё' || c == '-';
            if (!ru) return false;
        }
        return w[0] != '-';
    }

    private static NgramModel? TryLoadCache(string cachePath, string sourcePath, byte mode, LayoutTarget language)
    {
        var src = new FileInfo(sourcePath);
        using var fs = File.OpenRead(cachePath);
        using var br = new BinaryReader(fs, Encoding.UTF8);
        if (br.ReadUInt32() != Magic) return null;
        if (br.ReadInt32() != Version) return null;
        if (br.ReadByte() != mode) return null;          // сменился режим обучения
        if (br.ReadByte() != (byte)language || br.ReadString() != Fingerprint(sourcePath)) return null;
        long srcLen = br.ReadInt64();
        long srcTicks = br.ReadInt64();
        if (srcLen != src.Length || srcTicks != src.LastWriteTimeUtc.Ticks) return null; // источник изменился

        long total = br.ReadInt64();
        int n = br.ReadInt32();
        var counts = new Dictionary<string, long>(n, StringComparer.Ordinal);
        for (int i = 0; i < n; i++)
        {
            string tri = br.ReadString();
            long c = br.ReadInt64();
            counts[tri] = c;
        }
        return new NgramModel(counts, total);
    }

    private static void SaveCache(string cachePath, string sourcePath, byte mode, NgramModel model, LayoutTarget language)
    {
        var src = new FileInfo(sourcePath);
        string? dir = Path.GetDirectoryName(cachePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var fs = File.Create(cachePath);
        using var bw = new BinaryWriter(fs, Encoding.UTF8);
        bw.Write(Magic);
        bw.Write(Version);
        bw.Write(mode);
        bw.Write((byte)language);
        bw.Write(Fingerprint(sourcePath));
        bw.Write(src.Length);
        bw.Write(src.LastWriteTimeUtc.Ticks);
        bw.Write(model._total);
        bw.Write(model._counts.Count);
        foreach (var kv in model._counts)
        {
            bw.Write(kv.Key);
            bw.Write(kv.Value);
        }
    }

    private static string Fingerprint(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
