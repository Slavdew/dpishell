using System.Security.Authentication;

namespace DpiShell.Core;

public sealed record TargetVerdict(string GroupId, Target Target, bool Ok, double BestSeconds, IReadOnlyList<ProbeResult> Probes);

public sealed record StrategyScore(Strategy Strategy, IReadOnlyList<TargetVerdict> Verdicts, int GroupsPassed, int GroupsTotal, string? Error = null)
{
    public double GroupShare => GroupsTotal == 0 ? 0 : (double)GroupsPassed / GroupsTotal;
    public double TargetShare => Verdicts.Count == 0 ? 0 : (double)Verdicts.Count(v => v.Ok) / Verdicts.Count;
    public double AvgSeconds => Verdicts.Where(v => v.Ok).Select(v => v.BestSeconds).DefaultIfEmpty(99).Average();
    public bool Perfect => Error is null && GroupsTotal > 0 && GroupsPassed == GroupsTotal;
}

public sealed record SelectionReport(
    IReadOnlyList<TargetVerdict> Baseline,
    IReadOnlyList<TargetGroup> TestedGroups,
    IReadOnlyList<StrategyScore> Scores,
    bool Cancelled)
{
    public StrategyScore? Best => Scores.Where(s => s.Error is null && s.GroupsPassed > 0)
        .OrderByDescending(s => s.GroupShare).ThenByDescending(s => s.TargetShare).ThenBy(s => s.AvgSeconds).FirstOrDefault();

    /// <summary>Всё открылось без обхода — подбирать нечего (нет блокировок или включён VPN).</summary>
    public bool NothingBlocked => TestedGroups.Count == 0 && Baseline.Count > 0;
}

public sealed record SelectorProgress(int Index, int Total, string Message, StrategyScore? Last = null);

/// <summary>
/// Последовательный подбор: служба стоит, для каждой стратегии поднимается свой winws, проверяются цели, процесс убивается.
/// Цели, которые открываются и без обхода, из оценки выбрасываются (как в blockcheck).
/// </summary>
public sealed class Selector(AppPaths paths, HttpProbe probe)
{
    public TimeSpan Settle { get; init; } = TimeSpan.FromSeconds(1);
    public int ProbeParallelism { get; init; } = 8;
    public IReadOnlyList<SslProtocols> Tls { get; init; } = HttpProbe.DefaultTls;

    public async Task<IReadOnlyList<TargetVerdict>> CheckGroupsAsync(IEnumerable<TargetGroup> groups, CancellationToken ct)
    {
        var items = groups.SelectMany(g => g.Targets.Select(t => (g.Id, t))).ToList();
        var results = new TargetVerdict[items.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, items.Count),
            new ParallelOptions { MaxDegreeOfParallelism = ProbeParallelism, CancellationToken = ct },
            async (i, token) =>
            {
                var (gid, t) = items[i];
                var probes = await probe.CheckAsync(t, Tls, token);
                var ok = probes.All(p => p.Ok);
                results[i] = new(gid, t, ok, ok ? probes.Max(p => p.Seconds) : 0, probes);
            });
        return results;
    }

    static int GroupsPassed(IEnumerable<TargetGroup> groups, IReadOnlyList<TargetVerdict> v) =>
        groups.Count(g => v.Where(x => x.GroupId == g.Id && x.Target.Required).All(x => x.Ok)
                       && v.Any(x => x.GroupId == g.Id && x.Ok));

    public async Task<SelectionReport> RunAsync(
        IReadOnlyList<Strategy> strategies, IReadOnlyList<TargetGroup> groups, bool quick,
        IProgress<SelectorProgress>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(new(0, strategies.Count, "Проверка без обхода…"));
        var baseline = await CheckGroupsAsync(groups, ct);

        // Оставляем только то, что без обхода не открылось.
        var blocked = groups
            .Select(g => g with { Targets = g.Targets.Where(t => !baseline.Any(b => b.Target == t && b.GroupId == g.Id && b.Ok)).ToList() })
            .Where(g => g.Targets.Count > 0).ToList();
        if (blocked.Count == 0) return new(baseline, blocked, [], false);

        var scores = new List<StrategyScore>();
        for (int i = 0; i < strategies.Count; i++)
        {
            if (ct.IsCancellationRequested) return new(baseline, blocked, scores, true);
            var s = strategies[i];
            progress?.Report(new(i, strategies.Count, $"Стратегия {s.Title}…"));
            StrategyScore score;
            try { score = await RunOneAsync(s, blocked, ct); }
            catch (OperationCanceledException) { return new(baseline, blocked, scores, true); }
            scores.Add(score);
            progress?.Report(new(i + 1, strategies.Count, $"{s.Title}: {score.GroupsPassed}/{score.GroupsTotal}", score));
            if (quick && score.Perfect) break;
            await Task.Delay(300, ct);
        }
        return new(baseline, blocked, scores, false);
    }

    public async Task<StrategyScore> RunOneAsync(Strategy s, IReadOnlyList<TargetGroup> groups, CancellationToken ct)
    {
        var args = WinwsArgs.Build(s, paths);
        using var w = WinwsProcess.Start(paths, args);
        if (!await w.WaitReadyAsync(Settle, ct))
            return new(s, [], 0, groups.Count, "winws завершился: " + Tail(w.Output));
        var verdicts = await CheckGroupsAsync(groups, ct);
        if (!w.Alive) return new(s, verdicts, 0, groups.Count, "winws упал во время проверки: " + Tail(w.Output));
        return new(s, verdicts, GroupsPassed(groups, verdicts), groups.Count);
    }

    static string Tail(string s) => s.Length > 400 ? s[^400..] : s;
}
