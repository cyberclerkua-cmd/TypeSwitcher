using TypeSwitcher.Core;

namespace TypeSwitcher.Config;

/// <summary>Explicit layout policy; independent of the legacy word-substitution files.</summary>
public sealed record LayoutRule
{
    public string Pattern { get; set; } = "";
    public string Source { get; set; } = "global";
    public string Target { get; set; } = "ru";
    public string Match { get; set; } = "exact";
    public string Action { get; set; } = "keep";
    public bool CaseSensitive { get; set; }
    public bool Enabled { get; set; } = true;

    public bool IsValid() => !string.IsNullOrEmpty(Pattern) && Pattern.Length <= 64 && !Pattern.Any(char.IsWhiteSpace)
        && Pattern.Any(char.IsLetter) && (Source == "global" || LanguageProfile.Find(Source) != null)
        && LanguageProfile.Find(Target) != null && Match is "exact" or "prefix" or "contains" && Action is "keep" or "convert";

    public bool Matches(string word, LayoutTarget source)
    {
        if (!Enabled || !IsValid() || Source != "global" && Source != LanguageProfile.Find(source)?.Id) return false;
        var comparison = CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return Match switch { "prefix" => word.StartsWith(Pattern, comparison), "contains" => word.Contains(Pattern, comparison), _ => word.Equals(Pattern, comparison) };
    }

    public static LayoutRule? Find(IEnumerable<LayoutRule> rules, string word, LayoutTarget source) => rules
        .Where(r => r.Matches(word, source))
        .OrderByDescending(r => r.Match == "exact" ? 3 : r.Match == "prefix" ? 2 : 1)
        .ThenByDescending(r => r.Pattern.Length).ThenByDescending(r => r.Action == "keep").FirstOrDefault();
}
