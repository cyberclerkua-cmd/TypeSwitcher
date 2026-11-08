using System.Globalization;
using System.Text;

namespace TypeSwitcher.Core;

/// <summary>Языки текста и идентификаторы Windows. Профиль не зависит от наличия раскладки.</summary>
public sealed record LanguageProfile(LayoutTarget Target, string Id, string Name, ushort WindowsId, string Alphabet, double FrequencyScale = 1)
{
    public static readonly LanguageProfile English = new(LayoutTarget.En, "en", "English", 0x0409, "abcdefghijklmnopqrstuvwxyz");
    public static readonly LanguageProfile Russian = new(LayoutTarget.Ru, "ru", "Русский", 0x0419, "абвгдеёжзийклмнопрстуфхцчшщъыьэюя");
    // Редкие наблюдения в небольшом UK-корпусе сглаживаются: несколько употреблений
    // одной словоформы не должны подавлять остальные варианты окончания.
    public static readonly LanguageProfile Ukrainian = new(LayoutTarget.Uk, "uk", "Українська", 0x0422, "абвгґдеєжзиіїйклмнопрстуфхцчшщьюя", 50);
    public static IReadOnlyList<LanguageProfile> All { get; } = [English, Russian, Ukrainian];

    public static LanguageProfile? Find(LayoutTarget target) => All.FirstOrDefault(p => p.Target == target);
    public static LanguageProfile? Find(string? id) => All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    public static LayoutTarget FromLayout(IntPtr hkl) => All.FirstOrDefault(p => p.WindowsId == (hkl.ToInt64() & 0xFFFF))?.Target ?? LayoutTarget.None;

    public bool Contains(char c) => Alphabet.Contains(char.ToLowerInvariant(c));
    public static bool IsCyrillic(char c) => Russian.Contains(c) || Ukrainian.Contains(c);
    public static bool IsApostrophe(char c) => c is '\'' or '\u2019' or '\u02BC';
    public static bool IsCombining(char c) => char.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;

    public static string Normalize(string word) => word.Normalize(NormalizationForm.FormC).Replace('\u2019', '\'').Replace('\u02BC', '\'');

    public bool IsWord(string word)
    {
        string normalized = Normalize(word);
        if (normalized.Length == 0) return false;
        for (int i = 0; i < normalized.Length; i++)
        {
            char c = normalized[i];
            if (Contains(c)) continue;
            if ((c == '-' || (Target is LayoutTarget.Uk or LayoutTarget.En && c == '\'')) &&
                i > 0 && i + 1 < normalized.Length && Contains(normalized[i - 1]) && Contains(normalized[i + 1])) continue;
            return false;
        }
        return true;
    }

    public static string MatchApostrophe(string original, string replacement)
    {
        char style = original.FirstOrDefault(IsApostrophe);
        return style == default ? replacement : replacement.Replace('\'', style).Replace('\u2019', style).Replace('\u02BC', style);
    }
}
