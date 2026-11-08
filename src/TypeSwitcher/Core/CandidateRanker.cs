namespace TypeSwitcher.Core;

/// <summary>
/// Единый механизм консервативности: "бери лучшего, только если он уверенно
/// обгоняет второго". Заменяет разрозненные пороги на один принцип (идея
/// choose_best_with_gap из аналога lay). Если отрыв меньше gap - решение
/// неоднозначно, лучше не трогать текст.
/// </summary>
public static class CandidateRanker
{
    /// <summary>
    /// Выбирает лучший кандидат по <paramref name="score"/>, если он обгоняет
    /// второго не меньше чем на <paramref name="minGap"/> (единственный кандидат
    /// проходит всегда). Возвращает false, если кандидатов нет или отрыв мал.
    /// </summary>
    public static bool TryChooseBestWithGap<T>(
        IReadOnlyList<T> candidates, double minGap, Func<T, double> score, out T best)
    {
        best = default!;
        if (candidates.Count == 0) return false;

        best = candidates[0];
        double bestScore = score(best);
        double secondScore = double.NegativeInfinity;

        for (int i = 1; i < candidates.Count; i++)
        {
            double s = score(candidates[i]);
            if (s > bestScore) { secondScore = bestScore; bestScore = s; best = candidates[i]; }
            else if (s > secondScore) secondScore = s;
        }

        if (candidates.Count == 1) return true;
        return bestScore - secondScore >= minGap;
    }
}
