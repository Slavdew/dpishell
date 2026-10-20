using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DpiShell.Core;

/// <summary>
/// Что собрано из отчёта подбора: стратегия и откуда взята каждая часть. Части без своей группы в прогоне (не выбрали «Свои сайты»)
/// или без открывшей хоть что-то стратегии — null; их роли взяты из основы (<see cref="MixBuilder.BasePart"/>).
/// </summary>
public sealed record MixPlan(Strategy Mix, StrategyScore? Youtube, StrategyScore? Discord, StrategyScore? User)
{
    /// <summary>Выбранные части по порядку youtube, discord, user.</summary>
    public IEnumerable<StrategyScore> Picked => new[] { Youtube, Discord, User }.OfType<StrategyScore>();
}

/// <summary>
/// Смешанная стратегия (docs/research/mixed-strategies.md §2–3, шаг 2): три части — YouTube, Discord, свои сайты. Каждая — из лучшей для её группы
/// стратегии уже сделанного полного прогона, без новых проверок; одна проверка собранного идёт отдельно (<see cref="Selector.RunAsync"/>).
/// Роли частей — как у роутера (<c>select.uc</c>, <c>MIX_ROLES</c>):
/// youtube — <c>google</c>; discord — <c>discord_tcp</c> (у каталожных стратегий его нет — берётся их <c>general</c>), <c>discord_udp</c>, <c>discord_media</c>;
/// user — <c>general</c>, <c>quic</c>, <c>ipset_*</c>, <c>game_*</c>. Нулевая фаза из IP-списка (syndata в ipset_tcp) остаётся тем же приёмом, что и у general,
/// а роли, которые проверка не меряет (голос Discord, QUIC), идут целиком вместе со своей частью, без подбора по отдельности.
/// </summary>
public static class MixBuilder
{
    public const string Source = "mix";
    /// <summary>Части (ключи <see cref="Strategy.Parts"/>): YouTube, Discord, свои сайты.</summary>
    public const string YoutubeGroup = "youtube", DiscordGroup = "discord", UserGroup = "user";
    /// <summary>Сколько последних смешанных держать в файле своих стратегий.</summary>
    public const int MaxKept = 5;

    public static readonly IReadOnlyList<string> PartOrder = [YoutubeGroup, DiscordGroup, UserGroup];

    /// <summary>Роли каждой части (совпадает с роутером).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> PartRoles = new Dictionary<string, string[]>
    {
        [YoutubeGroup] = ["google"],
        [DiscordGroup] = ["discord_tcp", "discord_udp", "discord_media"],
        [UserGroup] = ["general", "quic", "ipset_udp", "ipset_tcp", "game_tcp", "game_udp"],
    };

    public static bool IsMix(string source) => source == Source;

    /// <summary>Часть, которая обслуживает группу целей: YouTube и Discord — свои, всё остальное (свои сайты) — user.</summary>
    public static string PartOf(string groupId) => groupId switch { YoutubeGroup => YoutubeGroup, DiscordGroup => DiscordGroup, _ => UserGroup };

    /// <summary>Роль части, по которой стратегии ранжируются; у каталожной стратегии без discord_tcp — её general.</summary>
    static string? RoleOf(Strategy s, string part, string role) =>
        s.Roles.GetValueOrDefault(role) ?? (part == DiscordGroup && role == "discord_tcp" ? s.Roles.GetValueOrDefault("general") : null);

    /// <summary>
    /// Смешанная из отчёта или null: идеальная одиночная есть, частей меньше двух, у всех частей одна и та же стратегия (смешивать нечего)
    /// либо ни одна стратегия не открыла ничего по группе. Стратегии с ошибкой (в том числе «ломает обычные сайты») и ранее собранные
    /// смешанные частью не бывают. Группы-строки продвинутого режима (Extra) в смесь не входят.
    /// </summary>
    public static MixPlan? Build(SelectionReport r)
    {
        if (r.Cancelled || r.Best is { Perfect: true }) return null;
        var goals = r.TestedGroups.Where(g => g.Goal).ToList();
        var parts = PartOrder.Where(p => goals.Any(g => PartOf(g.Id) == p)).ToList();
        if (parts.Count < 2) return null;

        var usable = r.Scores.Where(s => s.Error is null && s.Verdicts.Count > 0 && !IsMix(s.Strategy.Source)).ToList();
        var pick = new Dictionary<string, StrategyScore>();
        foreach (var p in parts)
        {
            var groups = goals.Where(g => PartOf(g.Id) == p).ToList();
            var best = Pick(usable, groups, p, PartRoles[p][0], r.Best);
            if (best is not null) pick[p] = best;
        }
        if (pick.Count < 2 || pick.Values.Select(s => s.Strategy.Id).Distinct().Count() < 2) return null;

        var from = PartOrder.ToDictionary(p => p, p => (pick.GetValueOrDefault(p) ?? pick[BasePart(pick)]).Strategy);
        var roles = new Dictionary<string, string>();
        foreach (var p in PartOrder)
            foreach (var role in PartRoles[p])
                if (RoleOf(from[p], p, role) is { } v) roles[role] = v;
        var parted = pick.ToDictionary(x => x.Key, x => x.Value.Strategy.Id);
        var chosen = PartOrder.Where(pick.ContainsKey).Select(p => (p, pick[p].Strategy)).ToList();
        var mix = new Strategy(MakeId(from[YoutubeGroup], from[DiscordGroup], from[UserGroup]), MakeTitle(chosen), Source, roles, parted);
        WinwsArgs.Validate(mix);
        return new(mix, pick.GetValueOrDefault(YoutubeGroup), pick.GetValueOrDefault(DiscordGroup), pick.GetValueOrDefault(UserGroup));
    }

    /// <summary>
    /// Часть, из которой берутся роли отсутствующих частей: «свои», иначе Discord, иначе YouTube. Роутер (<c>mix_from</c>) берёт «свои», иначе первую
    /// выбранную, то есть YouTube; для общих ролей (general, quic, ipset) стратегия Discord подходит обычным сайтам лучше, чем «только YouTube».
    /// </summary>
    public static string BasePart(IReadOnlyDictionary<string, StrategyScore> pick) =>
        pick.ContainsKey(UserGroup) ? UserGroup : pick.ContainsKey(DiscordGroup) ? DiscordGroup : YoutubeGroup;

    /// <summary>
    /// Лучшая стратегия для набора групп: больше групп прошло, выше доля адресов, при равенстве — та, что и в целом лучшая
    /// (чтобы не плодить смешанные из одинаковых), тише приём роли и быстрее. Ничего не открывшая частью не бывает.
    /// </summary>
    static StrategyScore? Pick(List<StrategyScore> scores, List<TargetGroup> goals, string part, string role, StrategyScore? best)
    {
        return scores
            .Select(s => (Score: s, Rank: Rank(s, goals, part, role)))
            .Where(x => x.Rank.Share > 0)
            .OrderByDescending(x => x.Rank.Passed).ThenByDescending(x => x.Rank.Share)
            .ThenBy(x => best is not null && x.Score.Strategy.Id == best.Strategy.Id ? 0 : 1)
            .ThenBy(x => x.Rank.Cost).ThenBy(x => x.Rank.Seconds)
            .Select(x => x.Score).FirstOrDefault();
    }

    static (int Passed, double Share, double Cost, double Seconds) Rank(StrategyScore s, List<TargetGroup> groups, string part, string role)
    {
        var ids = groups.Select(g => g.Id).ToHashSet();
        var v = s.Verdicts.Where(x => ids.Contains(x.GroupId)).ToList();
        int passed = groups.Count(g => v.Where(x => x.GroupId == g.Id && x.Target.Required).All(x => x.Ok) && v.Any(x => x.GroupId == g.Id && x.Ok));
        double share = v.Count == 0 ? 0 : (double)v.Count(x => x.Ok) / v.Count;
        double cost = RoleOf(s.Strategy, part, role) is { } d ? CandidateGenerator.Cost(d) : 0;
        double sec = v.Where(x => x.Ok).Select(x => x.BestSeconds).DefaultIfEmpty(99).Average();
        return (passed, share, cost, sec);
    }

    /// <summary>
    /// Детерминированный id по частям: повторный подбор с теми же частями не плодит копии, статистика копится.
    /// Одинаковые части Discord и «свои» записываются одним звеном: <c>mix-alt-alt10</c> (как у двухчастной смеси), разные — <c>mix-alt-alt2-alt10</c>.
    /// </summary>
    public static string MakeId(Strategy youtube, Strategy discord, Strategy user)
    {
        var id = discord.Id == user.Id ? $"mix-{youtube.Id}-{discord.Id}" : $"mix-{youtube.Id}-{discord.Id}-{user.Id}";
        if (id.Length <= 64) return id;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..12].ToLowerInvariant();
        return "mix-" + hash;
    }

    /// <summary>Две части: YouTube и «остальное» (Discord и свои сайты — из одной стратегии).</summary>
    public static string MakeId(Strategy youtube, Strategy rest) => MakeId(youtube, rest, rest);

    static string Label(string part) => part switch { YoutubeGroup => "YouTube", DiscordGroup => "Discord", _ => "свои сайты" };

    /// <summary>
    /// «Смешанная · YouTube — ALT · Discord — ALT2 · свои сайты — ALT10». Одинаковые части сливаются: Discord и свои сайты из одной стратегии —
    /// «остальное — ALT10», другие пары — «YouTube и Discord — ALT».
    /// </summary>
    public static string MakeTitle(IReadOnlyList<(string Part, Strategy Strategy)> parts)
    {
        var groups = parts.GroupBy(p => p.Strategy.Id).Select(g => (Parts: g.Select(x => x.Part).ToList(), Strategy: g.First().Strategy)).ToList();
        string Name(List<string> ps, bool shortUser) =>
            ps.Count == 2 && ps.Contains(DiscordGroup) && ps.Contains(UserGroup) ? "остальное"
            : string.Join(" и ", ps.Select(p => shortUser && p == UserGroup ? "свои" : Label(p)));
        string Make(bool shortUser) => "Смешанная · " + string.Join(" · ", groups.Select(g => $"{Name(g.Parts, shortUser)} — {PartName(g.Strategy)}"));
        // Название не длиннее MaxTitle: сначала «свои сайты», а не влезает — «свои».
        var full = Make(false);
        return StrategyExchange.CleanTitle(full.Length <= StrategyExchange.MaxTitle ? full : Make(true));
    }

    /// <summary>
    /// Состав смешанной для подсказки и «Параметров»: «YouTube — general (ALT); остальное — general (ALT10)». Имена — полные, как в каталоге
    /// (<paramref name="titleOf"/> по id); одинаковые части сливаются, как в названии. Не смешанная — пусто.
    /// </summary>
    public static string Composition(IReadOnlyDictionary<string, string>? parts, Func<string, string> titleOf)
    {
        if (parts is null || parts.Count == 0) return "";
        var groups = PartOrder.Where(parts.ContainsKey).GroupBy(p => parts[p]).ToList();
        return string.Join("; ", groups.Select(g =>
        {
            var ps = g.ToList();
            var name = ps.Count == 2 && ps.Contains(DiscordGroup) && ps.Contains(UserGroup) ? "остальное" : string.Join(" и ", ps.Select(Label));
            return $"{name} — {titleOf(g.Key)}";
        }));
    }

    /// <summary>Подпись для двух частей: YouTube и «остальное».</summary>
    public static string MakeTitle(Strategy youtube, Strategy rest) =>
        MakeTitle([(YoutubeGroup, youtube), (DiscordGroup, rest), (UserGroup, rest)]);

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
