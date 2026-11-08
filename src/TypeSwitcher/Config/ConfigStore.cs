using System.IO;
using System.Text.Json;
using TypeSwitcher.Localization;

namespace TypeSwitcher.Config;

/// <summary>
/// Каталог данных data\ рядом с exe и чтение/запись config.json.
/// Пользовательские списки (my_words/stopwords/rules) - текстовые файлы рядом.
/// </summary>
public sealed class ConfigStore
{
    public string DataDir { get; }
    public string ConfigPath => Path.Combine(DataDir, "config.json");
    public string MyWordsPath => Path.Combine(DataDir, "my_words.txt");
    public string StopWordsPath => Path.Combine(DataDir, "stopwords.txt");
    public string RulesPath => Path.Combine(DataDir, "rules.txt");
    public string UserListPath(string kind, string language)
    {
        if (kind is not ("my_words" or "stopwords" or "rules")) throw new ArgumentException("Unknown list kind.", nameof(kind));
        if (language == "global") return Path.Combine(DataDir, kind + ".txt");
        var profile = Core.LanguageProfile.Find(language) ?? throw new ArgumentException("Unknown language.", nameof(language));
        return Path.Combine(DataDir, $"{kind}.{profile.Id}.txt");
    }

    /// <summary>Каталог приложения (там лежат assets/ и dict/).</summary>
    public string AppDir { get; }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Последняя загрузка не удалась: config.json не прочитан и программа работает на
    /// дефолтах. Текст - для пользователя (куда сохранена копия испорченного файла).
    /// null - загрузка прошла нормально.
    /// </summary>
    public string? LoadProblem { get; private set; }

    private readonly object _saveLock = new();
    public static string PeekInterfaceLanguage()
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "config.json")));
            return json.RootElement.TryGetProperty("general", out var general) && general.TryGetProperty("language", out var language)
                ? L.NormalizeLanguage(language.GetString()) : "ru";
        }
        catch { return "ru"; }
    }

    public ConfigStore()
        : this(Path.Combine(AppContext.BaseDirectory, "data"))
    {
    }

    /// <summary>Хранилище в произвольном каталоге данных (для самопроверки).</summary>
    public ConfigStore(string dataDir, string? appDir = null)
    {
        // Портабл: все данные пользователя в подпапке data рядом с exe.
        AppDir = appDir ?? AppContext.BaseDirectory;
        DataDir = dataDir;
        Directory.CreateDirectory(DataDir);
    }

    public AppConfig Load()
    {
        LoadProblem = null;
        if (!File.Exists(ConfigPath))
        {
            var fresh = new AppConfig();
            Save(fresh);
            return fresh;
        }

        try
        {
            string json = File.ReadAllText(ConfigPath);
            using var document = JsonDocument.Parse(json);
            int version = document.RootElement.TryGetProperty("schema_version", out var v) ? v.GetInt32() : 1;
            if (version > 2) throw new JsonException(L.T("Версия настроек {0} новее поддерживаемой (2)", version));
            var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts)
                      ?? throw new JsonException(L.T("config.json пуст (null)"));
            cfg.Normalize();
            if (version < 2)
            {
                string backup = ConfigPath + ".v1.bak";
                if (!File.Exists(backup)) File.Copy(ConfigPath, backup);
                cfg.SchemaVersion = 2;
            }
            return cfg;
        }
        catch (Exception ex)
        {
            // Битый конфиг не должен ронять программу, но и молча затираться дефолтами
            // тоже: копия испорченного файла откладывается рядом, а вызывающий код по
            // LoadProblem не пересохраняет конфиг на старте и предупреждает пользователя.
            string? backup = BackupBroken();
            LoadProblem = backup != null
                ? L.T("config.json не прочитан ({0}). Копия сохранена как {1}; загружены настройки по умолчанию.", ex.Message, Path.GetFileName(backup))
                : L.T("config.json не прочитан ({0}), и сделать его копию не удалось. Загружены настройки по умолчанию; файл пока не перезаписан.", ex.Message);
            Core.Log.Line($"config: {LoadProblem}");
            return new AppConfig();
        }
    }

    // Копия нечитаемого config.json: config.json.broken-ГГГГММДД-ЧЧММСС.
    private string? BackupBroken()
    {
        try
        {
            string path = $"{ConfigPath}.broken-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(ConfigPath, path, overwrite: false);
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Атомарная запись: во временный файл с flush на диск, затем переименование поверх
    /// config.json. Сбой питания или падение посреди записи оставляет либо старый, либо
    /// новый файл целиком - а не полупустой, который при старте превратился бы в дефолты.
    /// </summary>
    public void Save(AppConfig config)
    {
        config.Normalize(); // значения из формы настроек - в допустимые диапазоны
        string json = JsonSerializer.Serialize(config, JsonOpts);
        string tmp = ConfigPath + ".tmp";
        lock (_saveLock)
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var w = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
            {
                w.Write(json);
                w.Flush();
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, ConfigPath, overwrite: true);
        }
    }

    /// <summary>Независимая копия настроек (снимок для потока хука).</summary>
    public static AppConfig Clone(AppConfig config)
        => JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config, JsonOpts), JsonOpts)!;

    public void SaveText(string path, string text)
    {
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        lock (_saveLock)
        {
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(file, new System.Text.UTF8Encoding(false)))
                {
                    writer.Write(text);
                    writer.Flush();
                    file.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    /// <summary>Импорт личного списка: существующее правило побеждает конфликтующее.</summary>
    public static string MergeUserList(string existing, string incoming, bool rules)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        string? Key(string raw)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) return null;
            if (rules)
            {
                int equals = line.IndexOf('=');
                if (equals <= 0 || line[(equals + 1)..].Trim().Length == 0) throw new InvalidDataException(L.T("Формат правила: что = на что."));
                line = line[..equals].Trim();
            }
            else if (line.Any(char.IsWhiteSpace)) throw new InvalidDataException(L.T("В списке должно быть одно слово на строку."));
            return Core.LanguageProfile.Normalize(line).ToLowerInvariant();
        }
        foreach (string line in existing.Split('\n')) if (Key(line) is { } key) keys.Add(key);
        var result = new System.Text.StringBuilder(existing);
        foreach (string line in incoming.Split('\n'))
        {
            string? key = Key(line);
            if (key == null || !keys.Add(key)) continue;
            if (result.Length > 0 && result[^1] != '\n') result.Append('\n');
            result.Append(line.Trim()).Append('\n');
        }
        return result.ToString();
    }

    /// <summary>Гарантирует существование пользовательских текстовых списков.</summary>
    public void EnsureUserLists()
    {
        foreach (var language in Core.LanguageProfile.All)
            foreach (string kind in new[] { "my_words", "stopwords", "rules" })
            {
                string path = UserListPath(kind, language.Id);
                if (!File.Exists(path)) File.WriteAllText(path, "");
            }
        if (!File.Exists(MyWordsPath))
            File.WriteAllText(MyWordsPath, "# Мой словарь: корректные слова, по одному на строку\n");
        if (!File.Exists(StopWordsPath))
            File.WriteAllText(StopWordsPath, "# Стоп-слова: не трогать, по одному на строку\n");
        if (!File.Exists(RulesPath))
            File.WriteAllText(RulesPath, "# Правила замены: опечатка = исправление\n");
    }

    /// <summary>Дозаписать слово в список (если его там ещё нет). Возвращает true, если добавлено.</summary>
    public bool AppendWord(string path, string word)
    {
        word = word.Trim();
        if (word.Length == 0) return false;

        if (File.Exists(path))
        {
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                if (string.Equals(line, word, StringComparison.OrdinalIgnoreCase))
                    return false; // уже есть
            }
        }

        // Гарантируем перевод строки перед добавлением.
        if (File.Exists(path))
        {
            string existing = File.ReadAllText(path);
            if (existing.Length > 0 && !existing.EndsWith('\n'))
                File.AppendAllText(path, "\n");
        }
        File.AppendAllText(path, word + "\n");
        return true;
    }

    /// <summary>Дозаписать правило замены "from = to" (если такого ещё нет).</summary>
    public void AppendRule(string from, string to, string language = "global")
    {
        string path = UserListPath("rules", language);
        from = from.Trim();
        to = to.Trim();
        if (from.Length == 0 || to.Length == 0) return;

        if (File.Exists(path))
        {
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                if (string.Equals(line[..eq].Trim(), from, StringComparison.OrdinalIgnoreCase))
                    return; // правило для этого слова уже есть
            }
            string existing = File.ReadAllText(path);
            if (existing.Length > 0 && !existing.EndsWith('\n'))
                File.AppendAllText(path, "\n");
        }
        File.AppendAllText(path, $"{from} = {to}\n");
    }

    /// <summary>Превращает относительный путь из конфига в абсолютный (от каталога приложения).</summary>
    public string ResolveAppPath(string relativeOrAbsolute)
    {
        if (Path.IsPathRooted(relativeOrAbsolute))
            return relativeOrAbsolute;
        return Path.Combine(AppDir, relativeOrAbsolute);
    }
}
