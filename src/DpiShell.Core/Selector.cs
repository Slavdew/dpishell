using System.Security.Authentication;
using DpiShell.Core.Localization;

namespace DpiShell.Core;

/// <summary>Ok — по DoH (честно для стратегии). Browser — проверка по DNS системы, если адреса разошлись (<see cref="BrowserCheck"/>).</summary>
public sealed record TargetVerdict(string GroupId, Target Target, bool Ok, double BestSeconds, IReadOnlyList<ProbeResult> Probes, BrowserPath? Browser = null)
{
    /// <summary>Программа открывает, а браузер (DNS системы) — нет.</summary>
    public bool BrowserFails => Ok && Browser is { Ok: false };
}

public sealed record StrategyScore(Strategy Strategy, IReadOnlyList<TargetVerdict> Verdicts, int GroupsPassed, int GroupsTotal, string? Error = null,
    string? ErrorCode = null, string? ErrorArg = null)
{
    /// <summary>Коды причин отказа (<see cref="ErrorCode"/>): текст берётся из языка окна, а не из записанной строки <see cref="Error"/>.</summary>
    public const string BreaksCode = "breaks", ExitedCode = "winws_exit", DiedCode = "winws_died";

    /// <summary>Сломан хоть один «обычный сайт» — оценка ноль (на роутере причина приходит текстом: у записи без кода — по началу строки).</summary>
    public bool BreaksRegular => ErrorCode == BreaksCode || Error?.StartsWith("ломает обычные сайты", StringComparison.Ordinal) == true;

    /// <summary>Причина отказа на языке окна; без кода (текст роутера или записанный раньше) — как есть.</summary>
    public string? ErrorText => ErrorCode switch
    {
        BreaksCode => L.T("core.score.breaks", new { sites = ErrorArg ?? "" }),
        ExitedCode => L.T("core.score.winws_exit", new { output = ErrorArg ?? "" }),
        DiedCode => L.T("core.score.winws_died", new { output = ErrorArg ?? "" }),
        _ => Error,
    };

    /// <summary>
    /// Адреса «Своих сайтов», до которых не устанавливается даже TCP-соединение ни без обхода, ни ни с одной стратегией (<see cref="AddressBlocks"/>):
    /// это не провал стратегии, в оценку они не входят и из <see cref="Verdicts"/> убраны — здесь они только для показа.
    /// </summary>
    public IReadOnlyList<TargetVerdict> Blocked { get; init; } = [];

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
    /// <summary>Откуда проверка брала адреса сайтов (<see cref="CheckDns"/>): защищённый DNS, система по выбору или система, потому что DoH не ответил. null — не известно.</summary>
    public string? AddressSource { get; init; }

    /// <summary>Адреса своих сайтов, заблокированные по адресу (<see cref="AddressBlocks"/>); из оценок исключены.</summary>
    public IReadOnlyList<string> AddressBlocked { get; init; } = [];

    public StrategyScore? Best => Scores.Where(s => s.Error is null && s.GroupsPassed > 0)
        .OrderByDescending(s => s.GroupShare).ThenByDescending(s => s.TargetShare).ThenBy(s => s.AvgSeconds).FirstOrDefault();

    /// <summary>Всё открылось без обхода — подбирать нечего (нет блокировок или включён VPN). Контрольные группы — не цель подбора.</summary>
    public bool NothingBlocked => TestedGroups.All(g => g.Control) && Baseline.Count > 0;
}

public sealed record SelectorProgress(int Index, int Total, string Message, StrategyScore? Last = null);

/// <summary>
/// Последовательный подбор: служба стоит, для каждой стратегии поднимается свой winws, проверяются цели, процесс убивается.
/// Цели, которые открываются и без обхода, из оценки выбрасываются (как в blockcheck).
/// </summary>
public class Selector(AppPaths paths, HttpProbe probe)
{
    public TimeSpan Settle { get; init; } = TimeSpan.FromSeconds(1);
    public int ProbeParallelism { get; init; } = 8;
    public IReadOnlyList<SslProtocols> Tls { get; init; } = HttpProbe.DefaultTls;
    /// <summary>Как собирать командную строку кандидата: null — все роли стратегии; при подборе своих сайтов рядом с роутером — только общие роли (<see cref="Shell.SelectOwnOnly"/>).</summary>
    public BuildOptions? Options { get; init; }

    public async Task<IReadOnlyList<TargetVerdict>> CheckGroupsAsync(IEnumerable<TargetGroup> groups, CancellationToken ct)
    {
        var items = groups.SelectMany(g => g.Targets.Select(t => (g.Id, t))).ToList();
        var results = new TargetVerdict[items.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, items.Count),
            new ParallelOptions { MaxDegreeOfParallelism = ProbeParallelism, CancellationToken = ct },
            async (i, token) =>
            {
                var (gid, t) = items[i];
                // TLS 1.3, которого нет в этой Windows, — не провал стратегии (TlsSupport); HttpProbe такие уже выбрасывает.
                var probes = (await probe.CheckAsync(t, Tls, token)).Where(p => p.Outcome != ProbeOutcome.Tls13Unsupported).ToList();
                var ok = probes.Count > 0 && probes.All(p => p.Ok);
                results[i] = new(gid, t, ok, ok ? probes.Max(p => p.Seconds) : 0, probes);
            });
        return results;
    }

    // Контрольные группы в счёт не идут: они не цель, а условие (см. RunOneAsync).
    static int GroupsPassed(IEnumerable<TargetGroup> groups, IReadOnlyList<TargetVerdict> v) =>
        groups.Where(g => g.Goal).Count(g => v.Where(x => x.GroupId == g.Id && x.Target.Required).All(x => x.Ok)
                       && v.Any(x => x.GroupId == g.Id && x.Ok));

    /// <summary>
    /// Что оставить в подборе по итогам проверки без обхода. Обычная группа — только то, что без обхода НЕ открылось
    /// (как blockcheck). Контрольная («Обычные сайты», architecture.md «Оценка и порядок») — наоборот, только то, что открылось:
    /// стратегия не должна это сломать, а недоступное и без обхода оценить нельзя.
    /// </summary>
    public static IReadOnlyList<TargetGroup> KeepForSelection(IReadOnlyList<TargetGroup> groups, IReadOnlyList<TargetVerdict> baseline) =>
        groups.Select(g => g with
            {
                Targets = g.Targets.Where(t => baseline.Any(b => b.Target == t && b.GroupId == g.Id && b.Ok) == g.Control).ToList(),
            })
            .Where(g => g.Targets.Count > 0).ToList();

    public async Task<SelectionReport> RunAsync(
        IReadOnlyList<Strategy> strategies, IReadOnlyList<TargetGroup> groups, bool quick,
        IProgress<SelectorProgress>? progress = null, CancellationToken ct = default, bool mix = false)
    {
        progress?.Report(new(0, strategies.Count, L.T("core.select.baseline")));
        var baseline = await CheckGroupsAsync(groups, ct);

        var blocked = KeepForSelection(groups, baseline);
        if (blocked.All(g => g.Control)) return new(baseline, blocked, [], false);

        var scores = new List<StrategyScore>();
        // Итог всегда пересчитывается без адресов, закрытых по адресу: они не провал стратегии и в выбор не входят.
        SelectionReport Finish(bool cancelled)
        {
            var gone = AddressBlocks.Find(baseline, scores);
            return new SelectionReport(baseline, blocked, [.. scores.Select(x => AddressBlocks.Rescore(x, blocked, gone))], cancelled)
            { AddressBlocked = [.. gone.Select(AddressBlocks.UrlOf)] };
        }
        for (int i = 0; i < strategies.Count; i++)
        {
            if (ct.IsCancellationRequested) return Finish(true);
            var s = strategies[i];
            progress?.Report(new(i, strategies.Count, L.T("core.select.strategy", new { title = s.Title })));
            StrategyScore score;
            try { score = await RunOneAsync(s, blocked, ct); }
            catch (OperationCanceledException) { return Finish(true); }
            scores.Add(score);
            progress?.Report(new(i + 1, strategies.Count, $"{s.Title}: {score.GroupsPassed}/{score.GroupsTotal}", score));
            if (quick && AddressBlocks.Rescore(score, blocked, AddressBlocks.Find(baseline, scores)).Perfect) break;
            await Task.Delay(300, ct);
        }
        var report = Finish(false);
        if (!mix) return report;

        // Одной стратегии на всё не нашлось — собрать смешанную из вердиктов этого же прогона и проверить её одним прогоном.
        // Берётся в отчёт, только если лучше лучшей одиночной; иначе всё как было.
        if (MixBuilder.Build(report) is not { } plan) return report;
        progress?.Report(new(strategies.Count, strategies.Count + 1, L.T("core.select.mixed", new { title = plan.Mix.Title })));
        StrategyScore mixScore;
        var withoutBlocked = AddressBlocks.Without(blocked, report.AddressBlocked);
        try { mixScore = await RunOneAsync(plan.Mix, withoutBlocked, ct); }
        catch (OperationCanceledException) { return report with { Cancelled = true }; }
        if (!MixBuilder.BeatsSingle(mixScore, report.Best)) return report;
        progress?.Report(new(strategies.Count + 1, strategies.Count + 1, $"{plan.Mix.Title}: {mixScore.GroupsPassed}/{mixScore.GroupsTotal}", mixScore));
        return report with { Scores = [.. report.Scores, mixScore] };
    }

    public virtual async Task<StrategyScore> RunOneAsync(Strategy s, IReadOnlyList<TargetGroup> groups, CancellationToken ct)
    {
        var args = WinwsArgs.Build(s, paths, Options);
        using var w = WinwsProcess.Start(paths, args);
        if (!await w.WaitReadyAsync(Settle, ct))
            return new(s, [], 0, groups.Count(g => g.Goal), "winws завершился: " + Tail(w.Output), StrategyScore.ExitedCode, Tail(w.Output));
        var verdicts = await CheckGroupsAsync(groups, ct);
        int goals = groups.Count(g => g.Goal);
        if (!w.Alive) return new(s, verdicts, 0, goals, "winws упал во время проверки: " + Tail(w.Output), StrategyScore.DiedCode, Tail(w.Output));
        return Score(s, groups, verdicts);
    }

    /// <summary>Оценка по проверке. Сломан хоть один «обычный сайт» — ноль с причиной, как на роутере (architecture.md «Оценка и порядок»).</summary>
    public static StrategyScore Score(Strategy s, IReadOnlyList<TargetGroup> groups, IReadOnlyList<TargetVerdict> verdicts)
    {
        int goals = groups.Count(g => g.Goal);
        var controlIds = groups.Where(g => g.Control).Select(g => g.Id).ToHashSet();
        var broken = verdicts.Where(v => controlIds.Contains(v.GroupId) && !v.Ok)
            .Select(v => Uri.TryCreate(v.Target.Url, UriKind.Absolute, out var u) ? u.Host : v.Target.Url).ToList();
        if (broken.Count > 0)
            return new(s, verdicts, 0, goals, "ломает обычные сайты: " + string.Join(", ", broken), StrategyScore.BreaksCode, string.Join(", ", broken));
        return new(s, verdicts, GroupsPassed(groups, verdicts), goals);
    }

    static string Tail(string s) => s.Length > 400 ? s[^400..] : s;
}
