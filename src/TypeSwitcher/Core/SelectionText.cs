using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TypeSwitcher.Config;

namespace TypeSwitcher.Core;

/// <summary>Pure, local transformations. Selection acquisition and replacement are kept in the Windows adapter.</summary>
public static class SelectionText
{
    public const int MaximumCharacters = 2048;
    private static readonly Regex Tokens = new(@"\S+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Dictionary<char, string> Russian = MakeTable(
        "абвгдеёжзийклмнопрстуфхцчшщъыьэюя", "a b v g d e yo zh z i y k l m n o p r s t u f kh ts ch sh shch - y - e yu ya");
    private static readonly Dictionary<char, string> Ukrainian = MakeTable(
        "абвгґдеєжзиіїйклмнопрстуфхцчшщьюя", "a b v h g d e ie zh z y i i i k l m n o p r s t u f kh ts ch sh shch - iu ia");

    public static bool IsSupported(string text)
    {
        if (text.Length is 0 or > MaximumCharacters || text.Any(char.IsControl) || text.Contains('\uFFFD')) return false;
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i])) { if (++i >= text.Length || !char.IsLowSurrogate(text[i])) return false; }
            else if (char.IsLowSurrogate(text[i])) return false;
        }
        return true;
    }

    public static LayoutTarget SourceLanguage(string text, LayoutTarget active)
    {
        if (!text.Any(LanguageProfile.IsCyrillic)) return LayoutTarget.En;
        if (text.Any(c => LanguageProfile.Ukrainian.Contains(c) && !LanguageProfile.Russian.Contains(c))) return LayoutTarget.Uk;
        return active == LayoutTarget.Uk ? LayoutTarget.Uk : LayoutTarget.Ru;
    }

    public static LayoutTarget[] Targets(string text, LayoutTarget active, AppConfig cfg)
    {
        if (LanguageProfile.Find(cfg.Selection.TargetLanguage) is { } explicitTarget) return [explicitTarget.Target];
        var enabled = cfg.Languages.Order.Where(cfg.Languages.Enabled.Contains).Select(id => LanguageProfile.Find(id)!.Target).ToArray();
        bool latin = text.Any(LanguageProfile.English.Contains), cyrillic = text.Any(LanguageProfile.IsCyrillic);
        if (cyrillic && !latin) return enabled.Contains(LayoutTarget.En) ? [LayoutTarget.En] : [];
        if (active is LayoutTarget.Ru or LayoutTarget.Uk && enabled.Contains(active)) return [active];
        return enabled.Where(l => l != LayoutTarget.En).ToArray();
    }

    public static string ConvertLayout(string text, LayoutTarget target, LayoutTarget active,
        Func<LayoutTarget, string, bool>? isWord = null)
    {
        if (LanguageProfile.Find(target) == null) return text;
        return Tokens.Replace(text, match =>
        {
            string token = match.Value;
            var source = target == LayoutTarget.En || !token.Any(LanguageProfile.English.Contains) ? SourceLanguage(token, active) : LayoutTarget.En;
            if (source == target) return token;
            var profile = LanguageProfile.Find(source)!;
            int first = -1, last = -1;
            for (int i = 0; i < token.Length; i++) if (profile.Contains(token[i])) { if (first < 0) first = i; last = i; }
            if (first < 0) return token;
            string? full = LayoutMap.MapBetween(token, source, target);
            if (full != null && isWord?.Invoke(target, full) == true) return full;
            var result = new StringBuilder(token);
            for (int i = first; i <= last; i++)
            {
                string? mapped = LayoutMap.MapBetween(token[i].ToString(), source, target);
                if (mapped is { Length: 1 }) result[i] = mapped[0];
            }
            return result.ToString();
        });
    }

    public static string ChangeCase(string text)
    {
        var letters = text.Where(char.IsLetter).ToArray();
        if (letters.Length == 0) return text;
        bool inverted = letters.Length > 1 && char.IsLower(letters[0]) && letters.Skip(1).All(char.IsUpper);
        if (inverted || letters.All(char.IsUpper)) return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.ToLowerInvariant());
        if (letters.All(char.IsLower)) return text.ToUpperInvariant();
        return text.ToLowerInvariant();
    }

    public static string Transliterate(string text, LayoutTarget language)
        => text.Any(LanguageProfile.IsCyrillic) ? ToLatin(text, language) : FromLatin(text, language);

    private static string ToLatin(string text, LayoutTarget language)
    {
        var table = language == LayoutTarget.Uk ? Ukrainian : Russian;
        var result = new StringBuilder();
        bool start = true;
        bool capitals = text.Any(char.IsLetter) && text.Where(char.IsLetter).All(char.IsUpper);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            char lower = char.ToLowerInvariant(c);
            string? mapped = table.GetValueOrDefault(lower);
            // Ukrainian зг is zgh; ж alone is zh. Only adjacent letters form the pair.
            if (language == LayoutTarget.Uk && lower == 'г' && i > 0 && char.ToLowerInvariant(text[i - 1]) == 'з')
                mapped = c == 'Г' && text[i - 1] == 'З' ? "GH" : "gh";
            if (language == LayoutTarget.Uk && start)
                mapped = lower switch { 'є' => "ye", 'ї' => "yi", 'й' => "y", 'ю' => "yu", 'я' => "ya", _ => mapped };
            if (LanguageProfile.IsApostrophe(c) && language == LayoutTarget.Uk) continue;
            if (mapped == null) result.Append(c);
            else if (capitals) result.Append(mapped.ToUpperInvariant());
            else if (char.IsUpper(c) && mapped.Length > 0) result.Append(char.ToUpperInvariant(mapped[0])).Append(mapped.AsSpan(1));
            else result.Append(mapped);
            start = !char.IsLetter(c) && !LanguageProfile.IsApostrophe(c);
        }
        return result.ToString();
    }

    private static string FromLatin(string text, LayoutTarget language)
    {
        // Reverse transliteration is inherently ambiguous and is always previewed by the UI.
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["shch"] = "щ", ["zh"] = "ж", ["kh"] = "х", ["ts"] = "ц", ["ch"] = "ч", ["sh"] = "ш",
            ["yu"] = "ю", ["ya"] = "я", ["a"] = "а", ["b"] = "б", ["v"] = "в", ["d"] = "д", ["e"] = "е",
            ["z"] = "з", ["k"] = "к", ["l"] = "л", ["m"] = "м", ["n"] = "н", ["o"] = "о", ["p"] = "п",
            ["r"] = "р", ["s"] = "с", ["t"] = "т", ["u"] = "у", ["f"] = "ф", ["q"] = "к", ["w"] = "в", ["x"] = "кс", ["j"] = "й"
        };
        if (language == LayoutTarget.Uk)
        {
            map["zgh"] = "зг"; map["h"] = "г"; map["g"] = "ґ"; map["i"] = "і"; map["y"] = "и";
            map["ye"] = "є"; map["yi"] = "ї"; map["ie"] = "є"; map["iu"] = "ю"; map["ia"] = "я";
        }
        else { map["h"] = "х"; map["g"] = "г"; map["i"] = "и"; map["y"] = "ы"; map["yo"] = "ё"; map["ye"] = "е"; }
        var result = new StringBuilder();
        for (int i = 0; i < text.Length;)
        {
            bool matched = false;
            for (int length = Math.Min(4, text.Length - i); length > 0; length--)
            {
                string part = text.Substring(i, length);
                if (!map.TryGetValue(part, out string? value)) continue;
                if (part.All(char.IsUpper)) value = value.ToUpperInvariant();
                else if (char.IsUpper(part[0])) value = char.ToUpperInvariant(value[0]) + value[1..];
                result.Append(value); i += length; matched = true; break;
            }
            if (!matched) result.Append(text[i++]);
        }
        return result.ToString();
    }

    private static Dictionary<char, string> MakeTable(string alphabet, string values)
    {
        var words = values.Split(' ');
        return alphabet.Select((c, i) => (c, word: words[i] == "-" ? "" : words[i])).ToDictionary(p => p.c, p => p.word);
    }
}
