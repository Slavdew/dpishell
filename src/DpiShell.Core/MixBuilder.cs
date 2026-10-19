using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DpiShell.Core;

/// <summary>Что собрано из отчёта подбора: стратегия и откуда взяты части.</summary>
public sealed record MixPlan(Strategy Mix, StrategyScore Youtube, StrategyScore Rest);

/// <summary>
/// Смешанная стратегия (docs/research/mixed-strategies.md §2–3, шаг 1): YouTube — из лучшей для YouTube стратегии,
/// всё остальное — из лучшей для остального. Собирается из вердиктов уже сделанного полного прогона, без новых проверок;
/// одна проверка собранного идёт отдельно (<see cref="Selector.RunAsync"/>).
/// Роли: <c>google</c> — часть YouTube; всё прочее (general, ipset_*, game_*, quic, discord_*) — целиком из одной части «остальное».
/// Так нулевая фаза из IP-списка (syndata в ipset_tcp) остаётся тем же приёмом, что и у general, а роли, которые проверка не меряет
/// (голос Discord, QUIC), идут вместе с общей частью, без подбора по отдельности.
/// </summary>
public static class MixBuilder
{
    public const string Source = "mix";
    /// <summary>Группа, которой обслуживает роль google (списки google.txt).</summary>
    public const string YoutubeGroup = "youtube";
    /// <summary>Сколько последних смешанных держать в файле своих стратегий.</summary>
    public const int MaxKept = 5;

    public static bool IsMix(string source) => source == Source;

    /// <summary>
    /// Смешанная из отчёта или null: идеальная одиночная есть, YouTube или «остального» нет среди проверенных групп,
    /// у обеих частей одна и та же стратегия (смешивать нечего) либо ни одна стратегия не открыла ничего по группе.
    /// Стратегии с ошибкой (в том числе «ломает обычные сайты») и ранее собранные смешанные частью не бывают.
    /// </summary>
    public static MixPlan? Build(SelectionReport r)
    {
        if (r.Cancelled || r.Best is { Perfect: true }) return null;
        var goals = r.TestedGroups.Where(g => !g.Control).ToList();
        var rest = goals.Where(g => g.Id != YoutubeGroup).Select(g => g.Id).ToList();
        if (goals.All(g => g.Id != YoutubeGroup) || rest.Count == 0) return null;

        var usable = r.Scores.Where(s => s.Error is null && s.Verdicts.Count > 0 && !IsMix(s.Strategy.Source)).ToList();
        var yt = Pick(usable, goals, [YoutubeGroup], "google", r.Best);
        var other = Pick(usable, goals, rest, "general", r.Best);
        if (yt is null || other is null || yt.Strategy.Id == other.Strategy.Id) return null;

        var roles = new Dictionary<string, string>(other.Strategy.Roles);
        if (yt.Strategy.Roles.TryGetValue("google", out var g)) roles["google"] = g; else roles.Remove("google");
        var parts = new Dictionary<string, string> { [YoutubeGroup] = yt.Strategy.Id };
        foreach (var id in rest) parts[id] = other.Strategy.Id;
        var mix = new Strategy(MakeId(yt.Strategy, other.Strategy), MakeTitle(yt.Strategy, other.Strategy), Source, roles, parts);
        WinwsArgs.Validate(mix);
        return new(mix, yt, other);
    }

    /// <summary>
    /// Лучшая стратегия для набора групп: больше групп прошло, выше доля адресов, при равенстве — та, что и в целом лучшая
    /// (чтобы не плодить смешанные из одинаковых), тише приём роли и быстрее. Ничего не открывшая частью не бывает.
    /// </summary>
    static StrategyScore? Pick(List<StrategyScore> scores, List<TargetGroup> goals, List<string> ids, string role, StrategyScore? best)
    {
        var groups = goals.Where(g => ids.Contains(g.Id)).ToList();
        return scores
            .Select(s => (Score: s, Rank: Rank(s, groups, role)))
            .Where(x => x.Rank.Share > 0)
            .OrderByDescending(x => x.Rank.Passed).ThenByDescending(x => x.Rank.Share)
            .ThenBy(x => best is not null && x.Score.Strategy.Id == best.Strategy.Id ? 0 : 1)
            .ThenBy(x => x.Rank.Cost).ThenBy(x => x.Rank.Seconds)
            .Select(x => x.Score).FirstOrDefault();
    }

    static (int Passed, double Share, double Cost, double Seconds) Rank(StrategyScore s, List<TargetGroup> groups, string role)
    {
        var ids = groups.Select(g => g.Id).ToHashSet();
        var v = s.Verdicts.Where(x => ids.Contains(x.GroupId)).ToList();
        int passed = groups.Count(g => v.Where(x => x.GroupId == g.Id && x.Target.Required).All(x => x.Ok) && v.Any(x => x.GroupId == g.Id && x.Ok));
        double share = v.Count == 0 ? 0 : (double)v.Count(x => x.Ok) / v.Count;
        double cost = s.Strategy.Roles.TryGetValue(role, out var d) ? CandidateGenerator.Cost(d) : 0;
        double sec = v.Where(x => x.Ok).Select(x => x.BestSeconds).DefaultIfEmpty(99).Average();
        return (passed, share, cost, sec);
    }

    /// <summary>Детерминированный id по частям: повторный подбор с теми же частями не плодит копии, статистика копится.</summary>
    public static string MakeId(Strategy youtube, Strategy rest)
    {
        var id = $"mix-{youtube.Id}-{rest.Id}";
        if (id.Length <= 64) return id;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..12].ToLowerInvariant();
        return "mix-" + hash;
    }

    /// <summary>«Смешанная · YouTube — ALT · остальное — ALT10».</summary>
    public static string MakeTitle(Strategy youtube, Strategy rest) =>
        StrategyExchange.CleanTitle($"Смешанная · YouTube — {PartName(youtube)} · остальное — {PartName(rest)}");

    /// <summary>Короткое имя части: «general (ALT10)» → «ALT10»; найденная — по приёму; слишком длинное — id.</summary>
    public static string PartName(Strategy s)
    {
        var shown = StrategyNaming.DisplayTitle(s.Title, s.Roles);
        var m = Regex.Match(shown, @"\(([^()]+)\)\s*$");
        var name = m.Success && shown.StartsWith("general", StringComparison.OrdinalIgnoreCase) ? m.Groups[1].Value : shown;
        return name.Length > 18 ? s.Id : name;
    }

    /// <summary>Лучшая ли смешанная одиночной: выше доля групп, при равной — выше доля адресов (порядок как у <see cref="SelectionReport.Best"/>).</summary>
    public static bool BeatsSingle(StrategyScore mix, StrategyScore? single) =>
        mix.Error is null && mix.GroupsPassed > 0 &&
        (single is null || mix.GroupShare > single.GroupShare || mix.GroupShare == single.GroupShare && mix.TargetShare > single.TargetShare);
}
