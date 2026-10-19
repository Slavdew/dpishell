using System.Text.Json;

namespace DpiShell.Core;

/// <summary>
/// Статистика прошлых подборов (results\*.json): по каждой стратегии — последние <see cref="Runs"/> её прогонов,
/// по группам целей и по адресам, свежие прогоны весят больше. Нужна для порядка проверки (стабильные первыми — быстрый подбор
/// заканчивается раньше) и для подсказки «работала N раз подряд, сейчас нет». В списке стратегий видно только последний прогон
/// (<see cref="Stat.Newest"/>, <see cref="Stat.Last"/>): история о сегодняшней сети не говорит. На выбор лучшей в подборе не влияет.
/// </summary>
/// <remarks>
/// Старые файлы (до групп в targets) дают только общие числа: GroupsPassed/GroupsTotal — в Score, Runs и FullOk, как раньше.
/// Сбой запуска (winws упал) прогоном не считается; «ломает обычные сайты» — считается, с нулём для порядка.
/// </remarks>
public static class StrategyStats
{
    /// <summary>Сколько последних прогонов каждой стратегии учитывать.</summary>
    public const int Runs = 10;
    /// <summary>Сколько последних файлов подборов читать (быстрый подбор проверяет не все стратегии).</summary>
    public const int Files = 50;
    const double Unknown = 0.5;   // новые стратегии — после проверенно рабочих, но до проверенно нерабочих

    /// <summary>
    /// Score — взвешенная доля обязательных групп (YouTube, Discord…; без них — доля своих сайтов); Runs — прогонов; FullOk — открыла всё.
    /// UserShare — взвешенная доля своих сайтов; UserOk из UserTotal — в последнем прогоне со своими адресами. Seconds — среднее время.
    /// ControlBroken — в скольких прогонах ломала «Обычные сайты». Groups — по группам каталога; Last — по адресам из последнего прогона.
    /// Newest — самый свежий прогон (и старого формата); Streak — сколько свежих прогонов подряд открывала всё (только с группами).
    /// </summary>
    public sealed record Stat(double Score, int Runs, int FullOk,
        double? UserShare = null, int UserOk = 0, int UserTotal = 0, double? Seconds = null, int ControlBroken = 0,
        IReadOnlyList<GroupStat>? Groups = null, LastRun? Last = null, NewestRun? Newest = null, int Streak = 0);

    /// <summary>Самый свежий прогон: файл (в имени — время подбора), групп прошло из всех, ломала ли обычные сайты.</summary>
    public sealed record NewestRun(string File, int Passed, int Total, bool ControlBroken);

    /// <summary>Группа каталога: открылась (все обязательные адреса) в Ok из Runs прогонов, где проверялась.</summary>
    public sealed record GroupStat(string Id, int Runs, int Ok);

    /// <summary>Последний прогон с разбивкой по адресам (свои сайты — «свой адрес №N», как в файле).</summary>
    public sealed record LastRun(string File, IReadOnlyList<TargetStat> Targets);

    public sealed record TargetStat(string GroupId, string Url, bool Ok, bool Required, bool Control, double Seconds, IReadOnlyList<ProbeStat> Probes);

    /// <summary>Tls и Outcome — имена из SslProtocols и ProbeOutcome.</summary>
    public sealed record ProbeStat(string Tls, string Outcome, string Detail, double Seconds);

    sealed record Run(string File, int Passed, int Total, bool ControlBroken, double AvgSeconds, bool HasSeconds, List<TargetStat>? Targets);

    public static IReadOnlyDictionary<string, double> Load(AppPaths p) =>
        LoadDetailed(p).ToDictionary(kv => kv.Key, kv => kv.Value.Score);

    public static IReadOnlyDictionary<string, Stat> LoadDetailed(AppPaths p)
    {
        var runs = new Dictionary<string, List<Run>>();
        if (!Directory.Exists(p.Results)) return new Dictionary<string, Stat>();
        foreach (var file in Directory.GetFiles(p.Results, "*.json").OrderByDescending(f => f, StringComparer.Ordinal).Take(Files))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (!doc.RootElement.TryGetProperty("scores", out var scores)) continue;
                foreach (var s in scores.EnumerateArray())
                {
                    var id = s.GetProperty("Id").GetString();
                    var total = s.GetProperty("GroupsTotal").GetInt32();
                    var error = s.TryGetProperty("Error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                    bool broken = s.TryGetProperty("ControlBroken", out var cb) && cb.ValueKind == JsonValueKind.True
                                  || error?.StartsWith("ломает обычные сайты", StringComparison.Ordinal) == true;
                    if (id is null || total == 0 || error is not null && !broken) continue;   // сбой запуска — не прогон
                    var list = runs.TryGetValue(id, out var l) ? l : runs[id] = [];
                    if (list.Count >= Runs) continue;
                    bool hasSeconds = error is null && s.TryGetProperty("AvgSeconds", out var avg) && avg.ValueKind == JsonValueKind.Number;
                    list.Add(new(Path.GetFileName(file), s.GetProperty("GroupsPassed").GetInt32(), total, broken,
                        hasSeconds ? s.GetProperty("AvgSeconds").GetDouble() : 0, hasSeconds, Targets(s)));
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or KeyNotFoundException or InvalidOperationException or FormatException) { }
        }
        return runs.ToDictionary(kv => kv.Key, kv => Summarize(kv.Value));
    }

    /// <summary>Адреса прогона — только из файлов с группами (GroupId); в старых их нет.</summary>
    static List<TargetStat>? Targets(JsonElement score)
    {
        if (!score.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Array) return null;
        var list = new List<TargetStat>();
        foreach (var t in targets.EnumerateArray())
        {
            if (!t.TryGetProperty("GroupId", out var g) || g.GetString() is not { } group) return null;
            var probes = new List<ProbeStat>();
            if (t.TryGetProperty("Probes", out var ps) && ps.ValueKind == JsonValueKind.Array)
                foreach (var x in ps.EnumerateArray())
                    probes.Add(new(Str(x, "Tls"), Str(x, "Outcome"), Str(x, "Detail"), Num(x, "Seconds")));
            list.Add(new(group, Str(t, "Url"), Bool(t, "Ok"), !t.TryGetProperty("Required", out var r) || r.ValueKind != JsonValueKind.False,
                Bool(t, "Control"), Num(t, "Seconds"), probes));
        }
        return list;

        static string Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        static double Num(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
        static bool Bool(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
    }

    /// <summary>Группа открылась — как в подборе (Selector): все обязательные адреса и хоть один вообще.</summary>
    static bool GroupOk(IEnumerable<TargetStat> t) => t.Where(x => x.Required).All(x => x.Ok) && t.Any(x => x.Ok);

    /// <summary>Группы каталога — цели подбора: не свои сайты и не контрольные («Обычные сайты»).</summary>
    static bool Catalog(TargetStat t) => t.GroupId != "user" && !t.Control;

    static Stat Summarize(List<Run> runs)   // runs — от свежего к старому
    {
        double w = 0, goal = 0, uw = 0, user = 0, sw = 0, sec = 0;
        var groups = new List<(string Id, int Runs, int Ok)>();
        int? userOk = null, userTotal = null;
        int streak = 0;
        bool streaking = true;
        for (int k = 0; k < runs.Count; k++)
        {
            var r = runs[k];
            double weight = 1.0 / (k + 1);
            double share = (double)r.Passed / r.Total;
            if (r.Targets is { } targets)
            {
                var byGroup = targets.Where(Catalog).GroupBy(t => t.GroupId).ToList();
                foreach (var g in byGroup)
                {
                    int i = groups.FindIndex(x => x.Id == g.Key);
                    if (i < 0) { groups.Add((g.Key, 0, 0)); i = groups.Count - 1; }
                    groups[i] = (g.Key, groups[i].Runs + 1, groups[i].Ok + (GroupOk(g) ? 1 : 0));
                }
                var mine = targets.Where(t => t.GroupId == "user").ToList();
                if (mine.Count > 0)
                {
                    double us = r.ControlBroken ? 0 : (double)mine.Count(t => t.Ok) / mine.Count;
                    uw += weight; user += us * weight;
                    userOk ??= mine.Count(t => t.Ok);
                    userTotal ??= mine.Count;
                }
                if (byGroup.Count > 0) share = (double)byGroup.Count(GroupOk) / byGroup.Count;
                else if (mine.Count > 0) share = (double)mine.Count(t => t.Ok) / mine.Count;
            }
            if (r.ControlBroken) share = 0;
            // Подряд «открыла всё» — только прогоны с группами: старые файлы не знают, что именно открылось.
            if (streaking && r.Targets is not null && share >= 1) streak++;
            else streaking = false;
            w += weight; goal += share * weight;
            if (r.HasSeconds) { sw += weight; sec += r.AvgSeconds * weight; }
        }
        var last = runs.FirstOrDefault(r => r.Targets is not null);
        return new(goal / w, runs.Count, runs.Count(r => !r.ControlBroken && r.Passed == r.Total),
            uw > 0 ? user / uw : null, userOk ?? 0, userTotal ?? 0, sw > 0 ? sec / sw : null, runs.Count(r => r.ControlBroken),
            groups.Select(g => new GroupStat(g.Id, g.Runs, g.Ok)).ToList(),
            last is null ? null : new LastRun(last.File, last.Targets!),
            new NewestRun(runs[0].File, runs[0].Passed, runs[0].Total, runs[0].ControlBroken), streak);
    }

    /// <summary>Порядок проверки по статистике: обязательные группы, затем доля своих сайтов, затем время; при равенстве — исходный.</summary>
    public static IReadOnlyList<Strategy> Order(IReadOnlyList<Strategy> strategies, IReadOnlyDictionary<string, Stat> stats) =>
        Order(strategies, s => s.Id, stats);

    /// <summary>Только общие числа (Score): для старых вызовов и тестов.</summary>
    public static IReadOnlyList<Strategy> Order(IReadOnlyList<Strategy> strategies, IReadOnlyDictionary<string, double> stats) =>
        Order(strategies, s => s.Id, stats.ToDictionary(kv => kv.Key, kv => new Stat(kv.Value, 1, 0)));

    /// <summary>То же для любого списка (окно сортирует свой список стратегий тем же правилом).</summary>
    public static IReadOnlyList<T> Order<T>(IReadOnlyList<T> items, Func<T, string> id, IReadOnlyDictionary<string, Stat> stats) =>
        items.Select((s, i) => (s, i, st: stats.GetValueOrDefault(id(s))))
            .OrderByDescending(x => x.st?.Score ?? Unknown)
            .ThenByDescending(x => x.st?.UserShare ?? Unknown)
            .ThenBy(x => x.st?.Seconds ?? double.MaxValue)
            .ThenBy(x => x.i)
            .Select(x => x.s).ToList();
}
