namespace DpiShell.Core;

/// <summary>
/// Блокировка по адресу в подборе: адрес «Своих сайтов», до которого не устанавливается даже TCP-соединение — ни без обхода, ни ни с одной
/// проверенной стратегией (все версии TLS: <see cref="ProbeOutcome.ConnectFailed"/>). Стратегия меняет только поток для DPI, а не то, отвечает
/// ли адрес, поэтому это не провал стратегии: такие адреса не входят в оценку и выбор, в сводке они помечены отдельно. Не то же самое, что
/// <see cref="AddressBlockTracker"/>: тот учитывает и молчание после соединения (таймаут, сброс) и лишь подсказывает; этот строже и решает оценку.
/// </summary>
public static class AddressBlocks
{
    /// <summary>Меньше стратегий — вывод ненадёжен (адрес мог просто мигнуть).</summary>
    public const int MinStrategies = 2;

    /// <summary>Группа, для которой правило действует: свои сайты (адреса каталога подобраны так, что открываются у большинства).</summary>
    const string GroupId = "user";

    public static string Key(TargetVerdict v) => v.GroupId + "\n" + v.Target.Url;
    public static string UrlOf(string key) => key[(key.IndexOf('\n') + 1)..];

    /// <summary>TCP-соединение не устанавливается: каждая проверка (по обеим версиям TLS) закончилась «нет соединения».</summary>
    public static bool NoConnection(TargetVerdict v) => !v.Ok && v.Probes.Count > 0 && v.Probes.All(p => p.Outcome == ProbeOutcome.ConnectFailed);

    /// <summary>Ключи адресов, закрытых по адресу, по проверке без обхода и итогам стратегий. Стратегии с ошибкой запуска не считаются.</summary>
    public static HashSet<string> Find(IReadOnlyList<TargetVerdict> baseline, IEnumerable<StrategyScore> scores)
    {
        var result = new HashSet<string>();
        var checks = scores.Where(s => s.Error is null && s.Verdicts.Count > 0).ToList();
        foreach (var b in baseline.Where(v => v.GroupId == GroupId && NoConnection(v)))
        {
            var key = Key(b);
            var seen = checks.Select(s => s.Verdicts.FirstOrDefault(v => Key(v) == key)).OfType<TargetVerdict>().ToList();
            if (seen.Count >= MinStrategies && seen.All(NoConnection)) result.Add(key);
        }
        return result;
    }

    /// <summary>Оценка без закрытых по адресу: они уходят из Verdicts в Blocked, группа без остальных адресов в счёт не идёт.</summary>
    public static StrategyScore Rescore(StrategyScore s, IReadOnlyList<TargetGroup> groups, HashSet<string> gone)
    {
        if (gone.Count == 0 || s.Error is not null && !s.BreaksRegular) return s;
        var dropped = s.Verdicts.Where(v => gone.Contains(Key(v))).ToList();
        if (dropped.Count == 0) return s;
        var kept = s.Verdicts.Where(v => !gone.Contains(Key(v))).ToList();
        var left = Without(groups, gone.Select(UrlOf));
        return Selector.Score(s.Strategy, left, kept) with { Blocked = dropped };
    }

    /// <summary>Группы без этих адресов (в «Своих сайтах»); группа без адресов не остаётся.</summary>
    public static IReadOnlyList<TargetGroup> Without(IReadOnlyList<TargetGroup> groups, IEnumerable<string> urls)
    {
        var drop = urls.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (drop.Count == 0) return groups;
        return [.. groups.Select(g => g.Id == GroupId ? g with { Targets = [.. g.Targets.Where(t => !drop.Contains(t.Url))] } : g)
            .Where(g => g.Targets.Count > 0)];
    }
}
