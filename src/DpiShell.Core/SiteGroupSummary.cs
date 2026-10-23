namespace DpiShell.Core;

/// <summary>Один проверенный адрес для сводки по группам. Group — имя группы ("" — «Прочее»; из истории — «группа №N»); OfAll — сколько адресов в группе всего (0 — не известно).</summary>
public sealed record SubItem(string Group, string Address, bool Ok, bool Blocked, int OfAll = 0);

/// <summary>
/// Итог по одной группе своих сайтов: «Telegram ✗ 0/12», «Прочее ✓ 7/8 — не открылся: example.org». Total и Ok — без адресов, закрытых по адресу
/// (<see cref="AddressBlocks"/>), они посчитаны отдельно в Blocked. OfAll больше проверенных — группа проверена выборкой («проверено 10 из 120»).
/// </summary>
public sealed record SubLine(string Group, int Ok, int Total, int Blocked, int OfAll, IReadOnlyList<string> Missed)
{
    public bool IsOther => Group.Length == 0;
    /// <summary>Открылось всё, что можно проверить; нечего проверять (все адреса закрыты по адресу) — не провал.</summary>
    public bool Passed => Ok == Total;
    /// <summary>Метка «✓»: открылась хотя бы половина проверенных адресов; меньше — «✗» («Telegram ✗ 0/12», «Прочее ✓ 7/8»).</summary>
    public bool Good => Total > 0 && Ok * 2 >= Total;
    public bool Sampled => OfAll > Total + Blocked;
    /// <summary>Сколько адресов группы проверяли: выборка вместе с закрытыми по адресу.</summary>
    public int Checked => Total + Blocked;
}

public static class SiteGroupSummary
{
    /// <summary>Сводка по группам: именованные в порядке появления, «Прочее» последней. Пусто — своих сайтов в проверке не было.</summary>
    public static IReadOnlyList<SubLine> Build(IEnumerable<SubItem> items)
    {
        var lines = new List<SubLine>();
        foreach (var g in items.GroupBy(i => i.Group, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key.Length == 0 ? 1 : 0))
        {
            var live = g.Where(i => !i.Blocked).ToList();
            lines.Add(new(g.Key, live.Count(i => i.Ok), live.Count, g.Count(i => i.Blocked), g.Max(i => i.OfAll),
                [.. live.Where(i => !i.Ok).Select(i => i.Address)]));
        }
        return lines;
    }

    /// <summary>Свои сайты в отчёте подбора для одной стратегии.</summary>
    public static IReadOnlyList<SubLine> Build(StrategyScore s, IReadOnlyList<TargetGroup> tested)
    {
        var totals = tested.FirstOrDefault(g => g.Id == "user")?.Totals;
        int OfAll(TargetVerdict v) => totals is not null && totals.TryGetValue(v.Target.Group ?? "", out var n) ? n : 0;
        static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;
        return Build(s.Verdicts.Select(v => (v, blocked: false)).Concat(s.Blocked.Select(v => (v, blocked: true)))
            .Where(x => x.v.GroupId == "user")
            .Select(x => new SubItem(x.v.Target.Group ?? "", Host(x.v.Target.Url), x.v.Ok, x.blocked, OfAll(x.v))));
    }
}
