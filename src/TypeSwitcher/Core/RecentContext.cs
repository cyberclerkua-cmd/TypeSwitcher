namespace TypeSwitcher.Core;

public enum ContextAction { Keep, Convert, Wait, Suggest }
public readonly record struct ContextAnalysis(Decision Applied, Decision Proposed, ContextAction Action);

/// <summary>
/// Bounded language evidence from recently completed words. No raw words, file I/O or cross-field history.
/// Weights are fixed: ordinary typing never trains or persists a model.
/// </summary>
public sealed class RecentContext
{
    public const int MaximumWords = 6;
    public const long LifetimeMs = 30_000;
    private readonly Queue<(LayoutTarget Language, long Time)> _languages = new();
    public int Count => _languages.Count;
    public void Clear() => _languages.Clear();
    public void Add(LayoutTarget language, long now)
    {
        Expire(now);
        if (language == LayoutTarget.None) return;
        _languages.Enqueue((language, now));
        while (_languages.Count > MaximumWords) _languages.Dequeue();
    }
    public LayoutTarget Preferred(long now)
    {
        Expire(now);
        var votes = _languages.GroupBy(x => x.Language).Select(g => (Language: g.Key, Count: g.Count())).OrderByDescending(g => g.Count).ToArray();
        return votes.Length > 0 && votes[0].Count >= 2 && (votes.Length == 1 || votes[0].Count >= votes[1].Count + 2)
            ? votes[0].Language : LayoutTarget.None;
    }
    private void Expire(long now)
    {
        while (_languages.TryPeek(out var item) && now - item.Time > LifetimeMs) _languages.Dequeue();
    }
}
