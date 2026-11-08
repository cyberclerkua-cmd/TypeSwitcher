namespace TypeSwitcher.Core;

/// <summary>
/// Счётчик откатов пользователя по словам (ТЗ, раздел 9). Считаются именно
/// откаты (программа исправила - пользователь вернул), а не сами автозамены.
/// По достижении порога сигналит, что пора показать обучающее окно, и сбрасывает
/// счётчик по этому слову.
/// </summary>
public sealed class SuggestionCounter
{
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

    public bool Enabled { get; set; } = true;
    public int Threshold { get; set; } = 3;

    /// <summary>
    /// Регистрирует откат по слову. Возвращает true, если достигнут порог
    /// (тогда счётчик по слову сбрасывается - пора показать окно).
    /// </summary>
    public bool RegisterRejection(string lowerWord)
    {
        if (!Enabled || string.IsNullOrEmpty(lowerWord)) return false;

        int c = _counts.GetValueOrDefault(lowerWord) + 1;
        if (c >= Threshold)
        {
            _counts.Remove(lowerWord);
            return true;
        }
        _counts[lowerWord] = c;
        return false;
    }

    /// <summary>Сбросить счётчик по слову (после действия в окне).</summary>
    public void Reset(string lowerWord) => _counts.Remove(lowerWord);
}
