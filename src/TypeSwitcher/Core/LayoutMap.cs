using System.Text;

namespace TypeSwitcher.Core;

/// <summary>
/// Фиксированная таблица соответствия клавиш ЙЦУКЕН (русская) и QWERTY
/// (английская) по физическим позициям. Используется для проверки гипотезы
/// "слово набрано не в той раскладке" (ТЗ, раздел 7).
/// </summary>
public static class LayoutMap
{
    // Английская буква -> русская буква на той же физической клавише.
    private static readonly Dictionary<char, char> EnToRuLower = new()
    {
        ['q'] = 'й', ['w'] = 'ц', ['e'] = 'у', ['r'] = 'к', ['t'] = 'е', ['y'] = 'н',
        ['u'] = 'г', ['i'] = 'ш', ['o'] = 'щ', ['p'] = 'з',
        ['a'] = 'ф', ['s'] = 'ы', ['d'] = 'в', ['f'] = 'а', ['g'] = 'п', ['h'] = 'р',
        ['j'] = 'о', ['k'] = 'л', ['l'] = 'д',
        ['z'] = 'я', ['x'] = 'ч', ['c'] = 'с', ['v'] = 'м', ['b'] = 'и', ['n'] = 'т', ['m'] = 'ь',
    };

    private static readonly Dictionary<char, char> RuToEnLower;

    static LayoutMap()
    {
        RuToEnLower = new Dictionary<char, char>();
        foreach (var kv in EnToRuLower)
            RuToEnLower[kv.Value] = kv.Key;
    }

    /// <summary>Все символы строки - латинские буквы.</summary>
    public static bool IsAllLatinLetters(string s)
    {
        if (s.Length == 0) return false;
        foreach (char c in s)
            if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))) return false;
        return true;
    }

    /// <summary>Все символы строки - кириллические буквы.</summary>
    public static bool IsAllCyrillic(string s)
    {
        if (s.Length == 0) return false;
        foreach (char c in s)
        {
            bool ru = LanguageProfile.IsCyrillic(c);
            if (!ru) return false;
        }
        return true;
    }

    public static string? MapEnToRu(string s) => Map(s, EnToRuLower);
    public static string? MapRuToEn(string s) => Map(s, RuToEnLower);

    /// <summary>Буквенная гипотеза по стандартным QWERTY/ЙЦУКЕН/ЙЦУКЕН-позициям.
    /// Полный сырой ввод, включая альтернативные клавиши, переводит KeyTranslator.</summary>
    public static string? MapBetween(string text, LayoutTarget source, LayoutTarget target)
    {
        if (source == target) return text;
        string? Row(LayoutTarget language) => language switch
        {
            LayoutTarget.En => "qwertyuiop[]asdfghjkl;'zxcvbnm,.",
            LayoutTarget.Ru => "йцукенгшщзхъфывапролджэячсмитьбю",
            LayoutTarget.Uk => "йцукенгшщзхїфівапролджєячсмитьбю",
            _ => null
        };
        string? from = Row(source), to = Row(target);
        if (from == null || to == null) return null;
        var result = new StringBuilder();
        foreach (char c in LanguageProfile.Normalize(text))
        {
            int index = from.IndexOf(char.ToLowerInvariant(c));
            if (index >= 0)
            {
                char mapped = to[index];
                if (char.IsUpper(c)) mapped = mapped switch { '[' => '{', ']' => '}', ';' => ':', '\'' => '"', ',' => '<', '.' => '>', _ => char.ToUpperInvariant(mapped) };
                result.Append(mapped);
            }
            else if (source == LayoutTarget.En && c == '`') result.Append(target == LayoutTarget.Uk ? '\'' : 'ё');
            else if (source == LayoutTarget.Uk && c == '\'') result.Append(target == LayoutTarget.En ? '`' : 'ё');
            else if (source == LayoutTarget.Ru && char.ToLowerInvariant(c) == 'ё') result.Append(target == LayoutTarget.En ? '`' : '\'');
            else if (char.IsDigit(c) || c == '-') result.Append(c);
            else return null;
        }
        return result.ToString();
    }

    public static string? NormalizeToLanguage(string text, LayoutTarget target)
    {
        var profile = LanguageProfile.Find(target);
        if (profile == null) return null;
        var result = new StringBuilder();
        foreach (char c in LanguageProfile.Normalize(text))
        {
            if (profile.Contains(c) || LanguageProfile.IsApostrophe(c) || c == '-') { result.Append(c); continue; }
            LayoutTarget source = LanguageProfile.English.Contains(c) ? LayoutTarget.En
                : LanguageProfile.Ukrainian.Contains(c) && !LanguageProfile.Russian.Contains(c) ? LayoutTarget.Uk : LayoutTarget.Ru;
            string? mapped = MapBetween(c.ToString(), source, target);
            if (mapped == null) return null;
            result.Append(mapped);
        }
        return result.ToString();
    }

    /// <summary>
    /// Перекладка ru->en для доменов/имён файлов: как MapRuToEn, но дополнительно
    /// знает клавишу точки - в русской раскладке QWERTY-клавиша '.>' даёт букву 'ю',
    /// поэтому 'куфвьуюьв' -> 'readme.md', 'пщщпдуюсщь' -> 'google.com'. В основную
    /// таблицу этот знак НЕ добавляем: буквенный авто-детект слов должен остаться
    /// строго буквенным (иначе 'слово.'/'привет,' глючат - см. CLAUDE.md). Возвращает
    /// null, если попался символ без пары.
    /// </summary>
    public static string? MapRuToEnDomain(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            char lo = char.ToLowerInvariant(c);
            if (lo == 'ю') { sb.Append('.'); continue; }
            if (!RuToEnLower.TryGetValue(lo, out char mapped)) return null;
            sb.Append(char.IsUpper(c) ? char.ToUpperInvariant(mapped) : mapped);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Привести смешанное слово к одному алфавиту: буквы чужой раскладки
    /// перекладываются на клавишу-соответствие, буквы целевого алфавита остаются.
    /// Возвращает null, если чужая буква не имеет пары. Для нормализации слов вроде
    /// 'привеn' -> 'привет' (toCyrillic=true) или 'hellо' -> 'hello' (false).
    /// </summary>
    public static string? NormalizeToScript(string s, bool toCyrillic)
    {
        var table = toCyrillic ? EnToRuLower : RuToEnLower;
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            char lo = char.ToLowerInvariant(c);
            bool alreadyTarget = toCyrillic
                ? ((lo >= 'а' && lo <= 'я') || lo == 'ё')
                : (lo >= 'a' && lo <= 'z');
            if (alreadyTarget) { sb.Append(c); continue; }
            if (!table.TryGetValue(lo, out char mapped)) return null; // чужая буква без пары
            sb.Append(char.IsUpper(c) ? char.ToUpperInvariant(mapped) : mapped);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Слово в противоположной раскладке (en->ru или ru->en) либо null, если оно
    /// не целиком из букв одной из раскладок. Удобно для правил «сменить раскладку».
    /// </summary>
    public static string? ToOtherLayout(string s)
    {
        if (IsAllLatinLetters(s)) return MapEnToRu(s);
        if (IsAllCyrillic(s)) return MapRuToEn(s);
        return null;
    }

    private static string? Map(string s, Dictionary<char, char> table)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            char lo = char.ToLowerInvariant(c);
            if (!table.TryGetValue(lo, out char mapped)) return null; // символ без пары - не раскладка
            sb.Append(char.IsUpper(c) ? char.ToUpperInvariant(mapped) : mapped);
        }
        return sb.ToString();
    }
}
