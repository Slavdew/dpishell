using System.Security.Authentication;

namespace DpiShell.Core;

public enum WatchOutcome { Ok, Failed, NoNetwork }

public enum WatchAction { None, Switch, GiveUp }

public sealed record WatchDecision(WatchAction Action, string? NextId = null)
{
    public static readonly WatchDecision Nothing = new(WatchAction.None);
}

/// <summary>
/// Запасные стратегии с автопереключением (ТЗ §9 п. 15): что делать по результату лёгкой проверки.
/// Только решения и счётчики в <see cref="AppState"/>; сеть, служба и уведомления — у вызывающего.
/// </summary>
public static class WatchdogPolicy
{
    public const int MaxFallbacks = 3;
    public const int FailsToSwitch = 2;
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);
    /// <summary>После смены сети адрес и маршруты устаканиваются не сразу.</summary>
    public static readonly TimeSpan NetworkSettle = TimeSpan.FromSeconds(10);

    /// <summary>Проверять только включённый обход и когда не идёт подбор/поиск (они сами останавливают службу).</summary>
    public static bool ShouldCheck(AppState s, bool serviceRunning, bool busy) =>
        s.Enabled && s.StrategyId is not null && serviceRunning && !busy;

    /// <summary>Рабочие стратегии отчёта в порядке рейтинга (тот же, что у <see cref="SelectionReport.Best"/>), не больше трёх.</summary>
    public static List<string> Rank(SelectionReport r) =>
        r.Scores.Where(s => s.Error is null && s.GroupsPassed > 0)
            .OrderByDescending(s => s.GroupShare).ThenByDescending(s => s.TargetShare).ThenBy(s => s.AvgSeconds)
            .Select(s => s.Strategy.Id).Distinct().Take(MaxFallbacks).ToList();

    /// <summary>После подбора/поиска: запомнить рейтинг. Пустой рейтинг старый список не стирает.</summary>
    public static void Remember(AppState s, SelectionReport r)
    {
        var ranked = Rank(r);
        if (ranked.Count == 0) return;
        s.Fallbacks = ranked;
        s.WatchFails = 0;
        s.WatchGaveUp = false;
    }

    /// <summary>Пользователь применил стратегию: она первая в списке, счётчики заново.</summary>
    public static void Applied(AppState s, string id)
    {
        s.Fallbacks.Remove(id);
        s.Fallbacks.Insert(0, id);
        if (s.Fallbacks.Count > MaxFallbacks) s.Fallbacks.RemoveRange(MaxFallbacks, s.Fallbacks.Count - MaxFallbacks);
        s.WatchFails = 0;
        s.WatchGaveUp = false;
    }

    /// <summary>
    /// Итог проверки → действие. Нет сети — не в счёт. Две неудачи подряд — следующая по рейтингу;
    /// следующей нет — один раз «подберите заново», дальше молчим до успеха или нового подбора.
    /// </summary>
    public static WatchDecision Decide(AppState s, WatchOutcome outcome, DateTime now, Func<string, bool>? exists = null)
    {
        if (outcome == WatchOutcome.NoNetwork) return WatchDecision.Nothing;
        s.WatchCheckedAt = now;
        if (outcome == WatchOutcome.Ok)
        {
            s.WatchFails = 0;
            s.WatchGaveUp = false;
            return WatchDecision.Nothing;
        }
        if (s.WatchGaveUp) return WatchDecision.Nothing;
        if (++s.WatchFails < FailsToSwitch) return WatchDecision.Nothing;

        s.WatchFails = 0;
        var next = Next(s.Fallbacks, s.StrategyId, exists);
        if (next is null)
        {
            s.WatchGaveUp = true;
            return new(WatchAction.GiveUp);
        }
        return new(WatchAction.Switch, next);
    }

    /// <summary>Следующая после текущей; текущей нет в списке — первая. По кругу не идём: прежние уже не работали.</summary>
    public static string? Next(IReadOnlyList<string> fallbacks, string? current, Func<string, bool>? exists = null)
    {
        int i = current is null ? -1 : fallbacks.ToList().IndexOf(current);
        return fallbacks.Skip(i + 1).FirstOrDefault(id => id != current && (exists?.Invoke(id) ?? true));
    }

    /// <summary>Все цели открылись — ок. Ничего не открылось и DoH молчит — нет сети, а не сломанная стратегия.</summary>
    public static WatchOutcome Classify(IReadOnlyList<TargetVerdict> verdicts, bool dohAlive) =>
        verdicts.All(v => v.Ok) ? WatchOutcome.Ok
        : !verdicts.Any(v => v.Ok) && !dohAlive ? WatchOutcome.NoNetwork
        : WatchOutcome.Failed;
}

/// <summary>Лёгкая проверка для автопереключения: обязательные цели, одна попытка, только TLS 1.3, адреса через DoH.</summary>
public static class WatchdogProbe
{
    public static async Task<WatchOutcome> CheckAsync(IReadOnlyList<TargetGroup> groups, AppPaths paths, CancellationToken ct = default)
    {
        var required = groups.Select(g => g with { Targets = g.Targets.Where(t => t.Required).ToList() })
            .Where(g => g.Targets.Count > 0).ToList();
        if (required.Count == 0) return WatchOutcome.Ok;

        using var doh = new DohResolver();
        var selector = new Selector(paths, new HttpProbe { Resolver = doh, Attempts = 1 }) { Tls = [SslProtocols.Tls13] };
        var verdicts = await selector.CheckGroupsAsync(required, ct);
        bool dohAlive = verdicts.Any(v => v.Ok) || (await doh.ResolveAsync("example.com", ct)).Length > 0;
        return WatchdogPolicy.Classify(verdicts, dohAlive);
    }
}
