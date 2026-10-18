namespace DpiShell.Core;

/// <summary>Порядок списка стратегий в окне: «по результату» (сколько сайтов открылось в последнем прогоне) или «по имени» (числа как числа).</summary>
public static class StrategyOrder
{
    /// <summary>Значение в window.json: порядок по результату (по умолчанию).</summary>
    public const string ByResult = "result";
    /// <summary>Значение в window.json: порядок по имени.</summary>
    public const string ByName = "name";

    /// <summary>
    /// Сравнение имён с естественным порядком чисел: ALT2, ALT3 … ALT10, ALT11 (не ALT10 перед ALT2).
    /// Имя без номера («general») — первым. Регистр не важен.
    /// </summary>
    public static int NaturalCompare(string? a, string? b)
    {
        a ??= ""; b ??= "";
        bool na = a.Any(char.IsDigit), nb = b.Any(char.IsDigit);
        if (na != nb) return na ? 1 : -1;
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                var x = a[si..i].TrimStart('0'); var y = b[sj..j].TrimStart('0');
                int c = x.Length != y.Length ? x.Length.CompareTo(y.Length) : string.CompareOrdinal(x, y);
                if (c != 0) return c;
            }
            else
            {
                int c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                if (c != 0) return c;
                i++; j++;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }

    /// <summary>Сколько сайтов открылось в последнем прогоне (без контрольных «обычных сайтов»); null — прогона нет.</summary>
    public static int? Opened(StrategyStats.Stat? st) =>
        st?.Last is { } last ? last.Targets.Count(t => t.Ok && !t.Control) : null;

    /// <summary>По имени, естественный порядок чисел; при равенстве — исходный.</summary>
    public static IReadOnlyList<T> SortByName<T>(IEnumerable<T> items, Func<T, string> title) =>
        items.Select((s, i) => (s, i)).OrderBy(x => title(x.s), Comparer<string>.Create(NaturalCompare)).ThenBy(x => x.i).Select(x => x.s).ToList();

    /// <summary>По числу открывшихся сайтов (по убыванию); без прогона — в конце; при равенстве — исходный порядок (он уже по оценке).</summary>
    public static IReadOnlyList<T> SortByOpened<T>(IEnumerable<T> items, Func<T, int?> opened) =>
        items.Select((s, i) => (s, i)).OrderByDescending(x => opened(x.s) ?? -1).ThenBy(x => x.i).Select(x => x.s).ToList();
}
