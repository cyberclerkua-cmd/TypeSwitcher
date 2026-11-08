namespace TypeSwitcher.Core;

/// <summary>
/// Классификация токена: НЕ исправляет, а отвечает "безопасно ли трогать".
/// Отдельный слой перед правкой гасит львиную долю ложных срабатываний на
/// технических токенах, которые по буквам случайно совпадают с валидным словом
/// в другой раскладке (аббревиатуры, camelCase-бренды, слова с цифрами,
/// смешанные ru/en). Идея из word_recognizer аналога lay.
///
/// Буфер слова копит только буквы/цифры (знаки - границы), поэтому URL/пути/флаги
/// сюда не доходят целиком; здесь ловим то, что реально доходит до анализа.
/// </summary>
public static class TokenClassifier
{
    /// <summary>
    /// Токен технический/защищённый - автоправка (опечатка/раскладка) запрещена.
    /// </summary>
    public static bool IsTechnical(string token)
    {
        if (string.IsNullOrEmpty(token)) return false;

        if (HasDigit(token) && HasLetter(token)) return true;   // win10, x64, h264
        if (IsAllUpperAsciiAcronym(token)) return true;          // USB, NTFS, API
        if (IsMixedCaseAsciiBrand(token)) return true;           // AmoCRM, GitHub, iPhone
        return false;
    }

    /// <summary>Слово смешанное по алфавитам (кириллица+латиница) - особый случай.</summary>
    public static bool IsMixedScript(string token)
    {
        bool cyr = false, lat = false;
        foreach (char c in token)
        {
            if (IsCyr(c)) cyr = true;
            else if (IsLat(c)) lat = true;
        }
        return cyr && lat;
    }

    // 2-5 заглавных латинских (аббревиатура): USB, NTFS, HTTP. Одну букву не берём.
    private static bool IsAllUpperAsciiAcronym(string t)
    {
        if (t.Length < 2 || t.Length > 5) return false;
        foreach (char c in t)
            if (!(c >= 'A' && c <= 'Z')) return false;
        return true;
    }

    // Латинский токен с заглавной НЕ в начале: GitHub, AmoCRM, iPhone, kubeCTL.
    // Обычное слово с одной ведущей заглавной (Привет/Hello) под правило не подходит.
    private static bool IsMixedCaseAsciiBrand(string t)
    {
        if (t.Length < 2) return false;
        foreach (char c in t)
            if (!IsLat(c)) return false; // только чистая латиница
        for (int i = 1; i < t.Length; i++)
            if (t[i] >= 'A' && t[i] <= 'Z') return true;
        return false;
    }

    private static bool HasDigit(string t)
    {
        foreach (char c in t) if (c >= '0' && c <= '9') return true;
        return false;
    }

    private static bool HasLetter(string t)
    {
        foreach (char c in t) if (IsLat(c) || IsCyr(c)) return true;
        return false;
    }

    private static bool IsLat(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

    private static bool IsCyr(char c)
        => LanguageProfile.IsCyrillic(c);
}
