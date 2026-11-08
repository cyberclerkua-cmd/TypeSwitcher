namespace TypeSwitcher.Core;

/// <summary>
/// Расстояние редактирования Дамерау-Левенштейна (с учётом перестановки соседних
/// букв - частый тип опечатки). С ранним выходом при превышении порога.
/// </summary>
public static class EditDistance
{
    /// <summary>
    /// Возвращает расстояние, либо -1 если оно заведомо больше <paramref name="max"/>.
    /// </summary>
    public static int DamerauLevenshtein(string a, string b, int max)
    {
        int la = a.Length, lb = b.Length;
        if (Math.Abs(la - lb) > max) return -1;
        if (la == 0) return lb <= max ? lb : -1;
        if (lb == 0) return la <= max ? la : -1;

        var prevPrev = new int[lb + 1];
        var prev = new int[lb + 1];
        var curr = new int[lb + 1];

        for (int j = 0; j <= lb; j++) prev[j] = j;

        for (int i = 1; i <= la; i++)
        {
            curr[0] = i;
            int rowMin = curr[0];
            char ai = a[i - 1];

            for (int j = 1; j <= lb; j++)
            {
                char bj = b[j - 1];
                int cost = ai == bj ? 0 : 1;

                int del = prev[j] + 1;
                int ins = curr[j - 1] + 1;
                int sub = prev[j - 1] + cost;
                int val = Math.Min(Math.Min(del, ins), sub);

                // Перестановка соседних символов.
                if (i > 1 && j > 1 && ai == b[j - 2] && a[i - 2] == bj)
                    val = Math.Min(val, prevPrev[j - 2] + 1);

                curr[j] = val;
                if (val < rowMin) rowMin = val;
            }

            if (rowMin > max) return -1; // дальше только хуже

            var tmp = prevPrev;
            prevPrev = prev;
            prev = curr;
            curr = tmp;
        }

        int result = prev[lb];
        return result <= max ? result : -1;
    }
}
