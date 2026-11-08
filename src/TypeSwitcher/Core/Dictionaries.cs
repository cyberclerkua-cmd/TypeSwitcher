using System.IO;
using System.Text;
using WeCantSpell.Hunspell;

namespace TypeSwitcher.Core;

public readonly record struct TypoOptions(
    int MaxEditDistance, double DominanceRatio, long MinFrequency,
    bool UseNgram, double NgramWeight, double FreqWeight, double ScoreGap);
public readonly record struct TypoCandidate(string Word, int Distance, long Frequency, double Score);

/// <summary>
/// Языковые словари. Набор строится вне потока ввода и затем публикуется целиком.
/// Пользовательские списки и модели могут подменяться готовым снимком.
/// </summary>
public sealed class Dictionaries
{
    internal Action<string, string, double>? SearchPhaseMeasured { get; set; }
    private readonly TimeProvider _clock;
    public Dictionaries(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;
    // Hunspell может долго перебирать далёкие n-gram-кандидаты, хотя корректору
    // нужны только ближайшие. Ограничения кооперативные, непосредственно в запросах.
    private static readonly QueryOptions InputQueries = new()
    {
        TimeLimitSuggestGlobal = TimeSpan.FromMilliseconds(15),
        TimeLimitSuggestStep = TimeSpan.FromMilliseconds(5),
        TimeLimitCompoundSuggest = TimeSpan.FromMilliseconds(5),
        TimeLimitCompoundCheck = TimeSpan.FromMilliseconds(5),
        MinTimer = 1
    };
    private sealed class LanguageData
    {
        public WordList? Main;
        public readonly List<WordList> Supplements = new();
        public readonly HashSet<string> Extra = new(StringComparer.Ordinal);
        public readonly Dictionary<string, long> Frequencies = new(StringComparer.Ordinal);
        public readonly Dictionary<string, List<string>> Prefix = new(StringComparer.Ordinal);
        public bool Indexed;
        public volatile NgramModel? Ngram;
    }

    private sealed record Lists(HashSet<string> Words, HashSet<string> Stops, Dictionary<string, string> Rules)
    {
        public static Lists Empty() => new(new(StringComparer.Ordinal), new(StringComparer.Ordinal), new(StringComparer.Ordinal));
    }

    private sealed record UserSnapshot(Lists Global, IReadOnlyDictionary<LayoutTarget, Lists> Scoped);
    private readonly Dictionary<LayoutTarget, LanguageData> _languages = LanguageProfile.All.ToDictionary(p => p.Target, _ => new LanguageData());
    private volatile UserSnapshot _users = new(Lists.Empty(), LanguageProfile.All.ToDictionary(p => p.Target, _ => Lists.Empty()));

    public bool RuLoaded => _languages[LayoutTarget.Ru].Main != null;
    public bool EnLoaded => _languages[LayoutTarget.En].Main != null;
    public bool UkLoaded => _languages[LayoutTarget.Uk].Main != null;
    public bool FreqLoaded => FreqCount > 0;
    public int FreqCount => _languages[LayoutTarget.Ru].Frequencies.Count;
    public int ExtraWordsCount => _languages[LayoutTarget.Ru].Extra.Count;
    public int EnExtraWordsCount => _languages[LayoutTarget.En].Extra.Count;
    public int MyWordsCount => _users.Global.Words.Count;
    public int StopWordsCount => _users.Global.Stops.Count;
    public int RulesCount => _users.Global.Rules.Count;
    public bool NgramLoaded => HasNgram(LayoutTarget.Ru);

    public bool IsLoaded(LayoutTarget language) => _languages.TryGetValue(language, out var data)
        && (data.Main != null || data.Supplements.Count != 0 || data.Extra.Count != 0);
    public bool HasNgram(LayoutTarget language) => _languages.TryGetValue(language, out var data) && data.Ngram is { Loaded: true };
    public void SetNgram(NgramModel? ngram, LayoutTarget language = LayoutTarget.Ru) => _languages[language].Ngram = ngram;
    public IEnumerable<(string Word, long Frequency)> FrequencyWords(LayoutTarget language)
        => _languages[language].Frequencies.Select(p => (p.Key, p.Value));

    public bool LoadRuHunspell(string path) => LoadHunspell(LayoutTarget.Ru, path);
    public bool LoadEnHunspell(string path) => LoadHunspell(LayoutTarget.En, path);
    public bool LoadUkHunspell(string path) => LoadHunspell(LayoutTarget.Uk, path);
    public bool LoadHunspell(LayoutTarget language, string path)
    {
        _languages[language].Main = TryLoadHunspell(path, Path.ChangeExtension(path, ".aff"));
        return _languages[language].Main != null;
    }

    public bool LoadRuExtraHunspell(string path, string affPath) => LoadSupplement(LayoutTarget.Ru, path, affPath, cleanComments: true);
    public bool LoadSupplement(LayoutTarget language, string path, string affPath, bool cleanComments = false)
    {
        WordList? words = TryLoadHunspell(path, affPath, cleanComments);
        if (words == null) return false;
        _languages[language].Supplements.Add(words);
        return true;
    }

    private static WordList? TryLoadHunspell(string dicPath, string affPath, bool cleanComments = false)
    {
        try
        {
            if (!File.Exists(dicPath) || !File.Exists(affPath)) return null;
            if (!cleanComments) return WordList.CreateFromFiles(dicPath, affPath);
            var entries = File.ReadLines(dicPath).Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
            if (entries.Count > 0 && long.TryParse(entries[0], out _)) entries.RemoveAt(0);
            using var dic = new MemoryStream(Encoding.UTF8.GetBytes(entries.Count + "\n" + string.Join('\n', entries) + "\n"));
            using var aff = File.OpenRead(affPath);
            return WordList.CreateFromStreams(dic, aff);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Line($"dictionary load failed: {dicPath}: {ex.Message}");
            return null;
        }
    }

    public bool LoadFreq(string path) => LoadFrequency(LayoutTarget.Ru, path);
    public bool LoadFrequency(LayoutTarget language, string path, bool merge = false)
    {
        var data = _languages[language];
        if (!merge) data.Frequencies.Clear();
        data.Indexed = false;
        if (!File.Exists(path)) return false;
        foreach (var raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int split = line.IndexOf(' ');
            if (split <= 0 || !long.TryParse(line[(split + 1)..].Trim(), out long count) || count < 0) continue;
            data.Frequencies[Key(line[..split])] = count;
        }
        return data.Frequencies.Count > 0;
    }

    public long Frequency(string word, LayoutTarget language = LayoutTarget.Ru)
        => _languages[language].Frequencies.GetValueOrDefault(Key(word));

    public bool LoadExtra(string path) => LoadExtraWords(LayoutTarget.Ru, path, withFrequency: true);
    public bool LoadEnExtra(string path) => LoadExtraWords(LayoutTarget.En, path);
    public bool LoadExtraWords(LayoutTarget language, string path, bool withFrequency = false)
    {
        if (!File.Exists(path)) return false;
        var data = _languages[language];
        foreach (var raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int space = withFrequency ? line.IndexOf(' ') : -1;
            string word = Key(space < 0 ? line : line[..space]);
            if (word.Length == 0) continue;
            data.Extra.Add(word);
            if (space > 0 && long.TryParse(line[(space + 1)..].Trim(), out long count) && count >= 0)
                data.Frequencies.TryAdd(word, count);
        }
        data.Indexed = false;
        return data.Extra.Count > 0;
    }

    public bool IsEnExtra(string word) => _languages[LayoutTarget.En].Extra.Contains(Key(word));
    public bool IsValidRu(string word) => IsValid(LayoutTarget.Ru, word);
    public bool IsValidEn(string word) => IsValid(LayoutTarget.En, word);
    public bool IsValid(LayoutTarget language, string word)
    {
        if (!_languages.TryGetValue(language, out var data) || string.IsNullOrEmpty(word) || word.Length > 128) return false;
        string normalized = LanguageProfile.Normalize(word), lower = normalized.ToLowerInvariant();
        var users = _users;
        // В 1.1.2 общий my_words давал глобальный иммунитет и расширял именно RU-оракул.
        if (language == LayoutTarget.Ru && users.Global.Words.Contains(lower)) return true;
        if (users.Scoped[language].Words.Contains(lower) || data.Extra.Contains(lower)) return true;
        if (data.Main != null && (data.Main.Check(normalized, InputQueries) || data.Main.Check(lower, InputQueries))) return true;
        return data.Supplements.Any(d => d.Check(normalized, InputQueries) || d.Check(lower, InputQueries));
    }

    public void PrewarmIndexes()
    {
        foreach (var data in _languages.Values) EnsureIndex(data);
        // Первый Suggest включает большой объём JIT-компиляции Hunspell. Прогреваем
        // весь путь до установки хуков / до публикации нового снимка, а не на первом слове.
        var options = new TypoOptions(1, 5, 50, true, 1, 1, 1.5);
        foreach (var profile in LanguageProfile.All.Where(p => p.Target != LayoutTarget.En && IsLoaded(p.Target)))
        {
            _ = RankCandidates(profile.Target, "абвгдеж", options);
            _ = _languages[profile.Target].Ngram?.Score("абвгдеж");
        }
    }

    private static void EnsureIndex(LanguageData data)
    {
        if (data.Indexed) return;
        data.Prefix.Clear();
        foreach (var word in data.Frequencies.Keys)
        {
            if (word.Length < 2) continue;
            string prefix = word[..2];
            if (!data.Prefix.TryGetValue(prefix, out var list)) data.Prefix[prefix] = list = new();
            list.Add(word);
        }
        data.Indexed = true;
    }

    public string? SuggestTypo(string word, in TypoOptions options) => SuggestTypo(LayoutTarget.Ru, word, options);
    public string? SuggestTypo(LayoutTarget language, string word, in TypoOptions options)
    {
        var top = RankCandidates(language, word, options);
        if (top.Count == 0) return null;
        if (options.UseNgram && HasNgram(language))
            return CandidateRanker.TryChooseBestWithGap(top, options.ScoreGap, c => c.Score, out var best) ? best.Word : null;
        return top.Count == 1 || (top[0].Frequency >= options.MinFrequency && top[0].Frequency >= options.DominanceRatio * top[1].Frequency)
            ? top[0].Word : null;
    }

    public IReadOnlyList<TypoCandidate> RankCandidates(LayoutTarget language, string word, in TypoOptions options)
    {
        if (!_languages.TryGetValue(language, out var data) || !IsLoaded(language) || word.Length > 128) return [];
        long started = _clock.GetTimestamp();
        long phaseStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        void Phase(string name)
        {
            if (SearchPhaseMeasured == null) return;
            SearchPhaseMeasured(word, name, System.Diagnostics.Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds);
            phaseStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        }
        bool Expired() => _clock.GetElapsedTime(started).TotalMilliseconds >= 100;
        int inspected = 0;
        bool BatchExpired() => (++inspected & 63) == 0 && Expired();
        string lower = Key(word);
        var scored = new List<(string Term, int Distance, long Frequency)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int maximum = options.MaxEditDistance;
        void Add(string candidate, bool validate = false, bool normalized = false)
        {
            if (normalized && Math.Abs(lower.Length - candidate.Length) > maximum) return;
            string term = normalized ? candidate : Key(candidate);
            if (term == lower || term.Any(char.IsWhiteSpace) || !seen.Add(term)) return;
            if (Math.Abs(lower.Length - term.Length) > maximum) return;
            int distance = EditDistance.DamerauLevenshtein(lower, term, maximum);
            if (distance >= 0 && distance <= maximum && (!validate || IsValid(language, term))) scored.Add((term, distance, data.Frequencies.GetValueOrDefault(term)));
        }

        if (data.Main != null)
            foreach (var candidate in data.Main.Suggest(lower, InputQueries)) Add(candidate);
        Phase("hunspell");
        foreach (var supplement in data.Supplements)
        {
            if (Expired()) return [];
            foreach (var candidate in supplement.Suggest(lower, InputQueries)) Add(candidate);
        }
        Phase("supplements");
        foreach (var candidate in data.Extra) { if (BatchExpired()) return []; Add(candidate, normalized: true); }
        foreach (var candidate in _users.Scoped[language].Words) { if (BatchExpired()) return []; Add(candidate, normalized: true); }
        Phase("extra-words");
        EnsureIndex(data);
        if (lower.Length >= 2 && data.Prefix.TryGetValue(lower[..2], out var bucket))
            foreach (var candidate in bucket) { if (BatchExpired()) return []; Add(candidate, validate: true, normalized: true); }
        Phase("frequency");
        if (Expired()) return []; // неполный пул не выдаём за однозначный выбор
        if (scored.Count == 0) return [];

        int bestDistance = scored.Min(c => c.Distance);
        var top = scored.Where(c => c.Distance == bestDistance).ToList();
        if (options.UseNgram && data.Ngram is { Loaded: true } model)
        {
            double ngramWeight = options.NgramWeight, frequencyWeight = options.FreqWeight;
            double scale = LanguageProfile.Find(language)!.FrequencyScale;
            double Score((string Term, int Distance, long Frequency) c)
                => ngramWeight * model.Margin(c.Term, lower) + frequencyWeight * Math.Log(c.Frequency / scale + 1);
            return top.Select(c => new TypoCandidate(c.Term, c.Distance, c.Frequency, Score(c)))
                .OrderByDescending(c => c.Score).ThenBy(c => c.Word, StringComparer.Ordinal).ToArray();
        }
        top.Sort((a, b) => b.Frequency.CompareTo(a.Frequency));
        return top.Select(c => new TypoCandidate(c.Term, c.Distance, c.Frequency, c.Frequency)).ToArray();
    }

    public bool IsMyWord(string word, LayoutTarget language = LayoutTarget.None)
    {
        var users = _users;
        string key = Key(word);
        return users.Global.Words.Contains(key) || (users.Scoped.TryGetValue(language, out var scoped) && scoped.Words.Contains(key));
    }

    public bool IsStopWord(string word, LayoutTarget language = LayoutTarget.None)
    {
        var users = _users;
        string key = Key(word);
        return users.Global.Stops.Contains(key) || (users.Scoped.TryGetValue(language, out var scoped) && scoped.Stops.Contains(key));
    }

    public bool TryRule(string word, out string replacement, LayoutTarget language = LayoutTarget.None)
    {
        var users = _users;
        string key = Key(word);
        if (users.Global.Rules.TryGetValue(key, out replacement!)) return true;
        if (users.Scoped.TryGetValue(language, out var scoped) && scoped.Rules.TryGetValue(key, out replacement!)) return true;
        replacement = "";
        return false;
    }

    public void LoadUserLists(string wordsPath, string stopsPath, string rulesPath)
    {
        Lists Read(string words, string stops, string rules) => new(ReadWords(words), ReadWords(stops), ReadRules(rules));
        string Scoped(string path, string id) => Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "." + id + ".txt");
        var global = Read(wordsPath, stopsPath, rulesPath);
        var scoped = LanguageProfile.All.ToDictionary(p => p.Target,
            p => Read(Scoped(wordsPath, p.Id), Scoped(stopsPath, p.Id), Scoped(rulesPath, p.Id)));
        _users = new(global, scoped);
    }

    private static HashSet<string> ReadWords(string path) => !File.Exists(path) ? new(StringComparer.Ordinal)
        : File.ReadLines(path).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).Select(Key).ToHashSet(StringComparer.Ordinal);

    private static Dictionary<string, string> ReadRules(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return result;
        foreach (var raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            int eq = line.IndexOf('=');
            if (line.StartsWith('#') || eq <= 0) continue;
            string from = Key(line[..eq].Trim()), to = line[(eq + 1)..].Trim();
            if (from.Length > 0 && to.Length > 0) result[from] = to;
        }
        return result;
    }

    private static string Key(string word) => LanguageProfile.Normalize(word).ToLowerInvariant();
}
