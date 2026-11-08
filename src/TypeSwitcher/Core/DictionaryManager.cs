using System.IO;
using System.Security.Cryptography;
using System.Text;
using TypeSwitcher.Config;
using WeCantSpell.Hunspell;
using TypeSwitcher.Localization;

namespace TypeSwitcher.Core;

public sealed record BuiltinDictionary(string Id, string Name, string Language, string Kind, string File, string? AffFile = null);
public sealed record DictionaryStatus(string Id, string Name, string Language, bool Enabled, bool Loaded, string? Error = null);
public sealed record DictionaryLoadResult(Dictionaries Dictionaries, IReadOnlyList<DictionaryStatus> Status)
{
    public IEnumerable<DictionaryStatus> Errors => Status.Where(s => s.Error != null);
}

/// <summary>Загрузка готового снимка и транзакционный импорт локальных словарей.</summary>
public static class DictionaryManager
{
    public const long MaximumFileBytes = 64 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public static IReadOnlyList<BuiltinDictionary> Builtins(AppConfig cfg) =>
    [
        new("ru-hunspell", "Русский · Hunspell", "ru", "hunspell", cfg.Dictionaries.RuHunspell),
        new("en-hunspell", "English · Hunspell", "en", "hunspell", cfg.Dictionaries.EnHunspell),
        new("uk-hunspell", "Українська · ВЕСУМ 6.8.6", "uk", "hunspell", cfg.Dictionaries.UkHunspell),
        new("ru-frequency", "Русский · частоты 300k", "ru", "frequency", cfg.Dictionaries.RuFreq),
        new("uk-frequency", "Українська · частоты 50k", "uk", "frequency", cfg.Dictionaries.UkFreq),
        new("ru-extra", "Русский · современная лексика", "ru", "extra", cfg.Dictionaries.RuExtra),
        new("ru-extra-affix", "Русский · дополнительные словоформы", "ru", "affix", cfg.Dictionaries.RuExtraDic, Path.ChangeExtension(cfg.Dictionaries.RuHunspell, ".aff")),
        new("en-extra", "English · технические термины", "en", "words", cfg.Dictionaries.EnExtra)
    ];

    public static DictionaryLoadResult Load(ConfigStore store, AppConfig config, CancellationToken cancellation = default)
    {
        var cfg = ConfigStore.Clone(config);
        cfg.Normalize();
        var dictionaries = new Dictionaries();
        var status = new List<DictionaryStatus>();
        var primary = new Dictionary<LayoutTarget, string>();
        var frequencies = new Dictionary<LayoutTarget, string>();
        var customFrequencies = new HashSet<LayoutTarget>();
        foreach (var item in Builtins(cfg))
        {
            cancellation.ThrowIfCancellationRequested();
            bool enabled = cfg.Languages.Enabled.Contains(item.Language) && !cfg.Dictionaries.DisabledBuiltins.Contains(item.Id);
            if (!enabled) { status.Add(new(item.Id, item.Name, item.Language, false, false)); continue; }
            var language = LanguageProfile.Find(item.Language)!.Target;
            string path = store.ResolveAppPath(item.File);
            try
            {
                bool loaded = item.Kind switch
                {
                    "hunspell" => dictionaries.LoadHunspell(language, path),
                    "frequency" => dictionaries.LoadFrequency(language, path),
                    "extra" => dictionaries.LoadExtraWords(language, path, withFrequency: true),
                    "affix" => dictionaries.LoadSupplement(language, path, store.ResolveAppPath(item.AffFile!), cleanComments: true),
                    "words" => dictionaries.LoadExtraWords(language, path),
                    _ => false
                };
                if (!loaded) throw new InvalidDataException(L.T("Файл отсутствует, пуст или не удалось прочитать словарь."));
                if (item.Kind == "frequency") frequencies[language] = path;
                else primary.TryAdd(language, path);
                status.Add(new(item.Id, item.Name, item.Language, true, true));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            {
                status.Add(new(item.Id, item.Name, item.Language, true, false, ex.Message));
            }
        }

        foreach (var item in cfg.Dictionaries.Installed)
        {
            cancellation.ThrowIfCancellationRequested();
            bool enabled = item.Enabled && cfg.Languages.Enabled.Contains(item.Language);
            if (!enabled) { status.Add(new(item.Id, item.Name, item.Language, false, false)); continue; }
            try
            {
                var profile = LanguageProfile.Find(item.Language) ?? throw new InvalidDataException(L.T("Неизвестный язык словаря."));
                string path = ResolveOwnedPath(store, item.File, item.Id);
                CheckHash(path, item.Sha256);
                bool loaded;
                if (item.Kind == "hunspell")
                {
                    string aff = ResolveOwnedPath(store, item.AffFile ?? "", item.Id);
                    CheckHash(aff, item.AffSha256 ?? "");
                    loaded = dictionaries.LoadSupplement(profile.Target, path, aff);
                }
                else if (item.Kind == "frequency")
                {
                    loaded = dictionaries.LoadFrequency(profile.Target, path, merge: true);
                    customFrequencies.Add(profile.Target);
                }
                else if (item.Kind == "words") loaded = dictionaries.LoadExtraWords(profile.Target, path);
                else throw new InvalidDataException(L.T("Неизвестный формат словаря."));
                if (!loaded) throw new InvalidDataException(L.T("Не удалось загрузить словарь."));
                if (item.Kind != "frequency") primary.TryAdd(profile.Target, path);
                status.Add(new(item.Id, item.Name, item.Language, true, true));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            {
                status.Add(new(item.Id, item.Name, item.Language, true, false, ex.Message));
            }
        }

        dictionaries.LoadUserLists(store.MyWordsPath, store.StopWordsPath, store.RulesPath);
        foreach (var profile in LanguageProfile.All.Where(p => p.Target != LayoutTarget.En && cfg.Languages.Enabled.Contains(p.Id)))
        {
            cancellation.ThrowIfCancellationRequested();
            if (!primary.TryGetValue(profile.Target, out string? path)) continue;
            try
            {
                NgramModel model;
                if (customFrequencies.Contains(profile.Target) && cfg.Ngram.WeightByFrequency)
                    model = NgramModel.BuildFromWords(dictionaries.FrequencyWords(profile.Target), profile.Target);
                else
                    model = NgramModel.BuildOrLoad(path, frequencies.GetValueOrDefault(profile.Target) ?? "",
                        Path.Combine(store.DataDir, profile.Target == LayoutTarget.Ru ? cfg.Ngram.CacheFile : "ngram_uk.cache"),
                        cfg.Ngram.WeightByFrequency, profile.Target);
                dictionaries.SetNgram(model, profile.Target);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            {
                status.Add(new("model-" + profile.Id, profile.Name + " · модель", profile.Id, true, false, ex.Message));
            }
        }
        dictionaries.PrewarmIndexes();
        cancellation.ThrowIfCancellationRequested();
        return new(dictionaries, status);
    }

    public static AppConfig.DictionaryEntry Import(ConfigStore store, string source, string language, string kind,
        string? affSource = null, CancellationToken cancellation = default)
    {
        var profile = LanguageProfile.Find(language) ?? throw new InvalidDataException(L.T("Выберите язык словаря."));
        if (kind is not ("hunspell" or "words" or "frequency")) throw new InvalidDataException(L.T("Неизвестный формат словаря."));
        if (kind == "hunspell" && string.IsNullOrWhiteSpace(affSource)) throw new InvalidDataException(L.T("Для Hunspell необходим файл .aff."));
        string root = Path.Combine(store.DataDir, "imported");
        Directory.CreateDirectory(root);
        string id = Guid.NewGuid().ToString("N");
        string staging = Path.Combine(root, ".stage-" + id), destination = Path.Combine(root, id);
        Directory.CreateDirectory(staging);
        try
        {
            string fileName = kind == "hunspell" ? "dictionary.dic" : kind + ".txt";
            string path = Path.Combine(staging, fileName);
            CopyBounded(source, path, cancellation);
            string? affPath = null;
            if (kind == "hunspell")
            {
                affPath = Path.Combine(staging, "dictionary.aff");
                CopyBounded(affSource!, affPath, cancellation);
            }
            int count = Validate(path, kind, affPath, cancellation);
            cancellation.ThrowIfCancellationRequested();
            var entry = new AppConfig.DictionaryEntry
            {
                Id = id, Name = Path.GetFileNameWithoutExtension(source), Language = profile.Id, Kind = kind,
                File = $"imported/{id}/{fileName}", AffFile = affPath == null ? null : $"imported/{id}/dictionary.aff",
                Sha256 = Hash(path), AffSha256 = affPath == null ? null : Hash(affPath),
                ImportedAt = DateTimeOffset.UtcNow, WordCount = count
            };
            Directory.Move(staging, destination);
            return entry;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    public static int Validate(string path, string kind, string? affPath = null, CancellationToken cancellation = default)
    {
        CheckSize(path);
        int entries = 0;
        int declared = -1;
        foreach (string raw in ReadUtf8(path))
        {
            cancellation.ThrowIfCancellationRequested();
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.Length > 2048 || line.Any(c => char.IsControl(c) && c != '\t')) throw new InvalidDataException(L.T("Слишком длинная строка или управляющие символы."));
            if (kind == "hunspell" && declared < 0)
            {
                if (!int.TryParse(line, out declared) || declared <= 0 || declared > 1_000_000) throw new InvalidDataException(L.T("Первая строка .dic должна содержать число слов."));
                continue;
            }
            if (kind == "words" && line.Any(char.IsWhiteSpace)) throw new InvalidDataException(L.T("В списке слов должно быть одно слово на строку."));
            if (kind == "frequency")
            {
                int space = line.IndexOf(' ');
                if (space <= 0 || line[..space].Any(char.IsWhiteSpace) || !long.TryParse(line[(space + 1)..].Trim(), out long frequency) || frequency < 0 || frequency > 1_000_000_000_000)
                    throw new InvalidDataException(L.T("Частотный формат: слово и неотрицательная целая частота через пробел (до 10^12)."));
            }
            if (++entries > 1_000_000) throw new InvalidDataException(L.T("Словарь содержит более миллиона записей."));
        }
        if (entries == 0) throw new InvalidDataException(L.T("Словарь пуст."));
        if (kind == "hunspell")
        {
            if (declared != entries) throw new InvalidDataException(L.T("В .dic объявлено {0} слов, найдено {1}.", declared, entries));
            if (affPath == null) throw new InvalidDataException(L.T("Файл .aff не выбран."));
            CheckSize(affPath);
            if (!ReadUtf8(affPath).Any(line => line.Trim().Equals("SET UTF-8", StringComparison.OrdinalIgnoreCase)
                || line.Trim().Equals("SET UTF8", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException(L.T("Поддерживаются словари Hunspell в UTF-8 (SET UTF-8 в .aff)."));
            // Парсер проверяет реальную совместимость пары до публикации файлов.
            _ = WordList.CreateFromFiles(path, affPath);
        }
        else if (kind is not ("words" or "frequency")) throw new InvalidDataException(L.T("Неизвестный формат словаря."));
        return entries;
    }

    public static void RemoveFiles(ConfigStore store, AppConfig.DictionaryEntry entry)
    {
        string path = ResolveOwnedPath(store, entry.File, entry.Id);
        string directory = Path.GetDirectoryName(path)!;
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    public static string ResolveOwnedPath(ConfigStore store, string relativePath, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _) || Path.IsPathRooted(relativePath)) throw new InvalidDataException(L.T("Неверный путь импортированного словаря."));
        string root = Path.GetFullPath(Path.Combine(store.DataDir, "imported", id));
        string path = Path.GetFullPath(Path.Combine(store.DataDir, relativePath));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetDirectoryName(path), root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(L.T("Путь находится вне каталога словаря."));
        string imported = Path.Combine(store.DataDir, "imported");
        if (Directory.Exists(imported) && (File.GetAttributes(imported) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException(L.T("Каталог импортов не должен быть ссылкой."));
        if (Directory.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException(L.T("Каталог словаря не должен быть ссылкой."));
        return path;
    }

    private static IEnumerable<string> ReadUtf8(string path)
    {
        using var reader = new StreamReader(path, StrictUtf8, detectEncodingFromByteOrderMarks: false);
        string? line;
        bool first = true;
        while ((line = reader.ReadLine()) != null)
        {
            yield return first ? line.TrimStart('\uFEFF') : line;
            first = false;
        }
    }

    private static void CopyBounded(string source, string destination, CancellationToken cancellation)
    {
        CheckSize(source);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[64 * 1024];
        long total = 0;
        int count;
        while ((count = input.Read(buffer)) > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            total += count;
            if (total > MaximumFileBytes) throw new InvalidDataException(L.T("Максимальный размер файла словаря: 64 МБ."));
            output.Write(buffer, 0, count);
        }
        output.Flush(flushToDisk: true);
    }

    public static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void CheckHash(string path, string expected)
    {
        CheckSize(path);
        if (expected.Length != 64 || !Hash(path).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(L.T("Файл изменён после импорта. Импортируйте обновлённый словарь заново."));
    }

    private static void CheckSize(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length == 0 || file.Length > MaximumFileBytes) throw new InvalidDataException(L.T("Файл должен существовать и иметь размер от 1 байта до 64 МБ."));
    }
}
