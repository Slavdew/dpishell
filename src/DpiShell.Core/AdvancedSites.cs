using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DpiShell.Core.Localization;

namespace DpiShell.Core;

/// <summary>
/// Строка продвинутого режима «сайты → стратегия» (docs/research/mixed-strategies.md §6, договор роутера §5.2 advanced_set).
/// <see cref="Strategy"/>: «auto» (программа выбирает при подборе, до подбора — текущая стратегия), «direct» (напрямую) или id стратегии.
/// <see cref="Resolved"/> — итог подбора для «auto»; ставит только подбор, окно его не присылает.
/// </summary>
public sealed class AdvancedRow
{
    public List<string> Domains { get; set; } = [];
    public string Strategy { get; set; } = AdvancedSites.Auto;
    public string? Resolved { get; set; }

    public AdvancedRow Clone() => new() { Domains = [.. Domains], Strategy = Strategy, Resolved = Resolved };
}

/// <summary>
/// Продвинутый режим целиком: включён ли, что делать с остальными адресами и строки. Выключен — строки хранятся, но в командную
/// строку не попадают, всё работает как раньше. Живёт в state.json помощника.
/// </summary>
public sealed class AdvancedConfig
{
    public bool Enabled { get; set; }
    /// <summary>«direct» — роли каталога не включаются, работают только строки; «lists» — роли каталога как есть (по умолчанию); «all» — общая роль без списка (для всех адресов).</summary>
    public string Others { get; set; } = AdvancedSites.OthersLists;
    public List<AdvancedRow> Rows { get; set; } = [];

    public AdvancedConfig Clone() => new() { Enabled = Enabled, Others = Others, Rows = [.. Rows.Select(r => r.Clone())] };

    /// <summary>То, что задал человек (без итога подбора): для «изменилось ли».</summary>
    public bool SameChoice(AdvancedConfig o) =>
        Enabled == o.Enabled && Others == o.Others && Rows.Count == o.Rows.Count
        && Rows.Zip(o.Rows).All(p => p.First.Strategy == p.Second.Strategy && p.First.Domains.SequenceEqual(p.Second.Domains));

    /// <summary>Снимок для журнала изменений. Итог подбора не пишем: его ведёт подбор.</summary>
    public JsonObject ToJson() => new()
    {
        ["enabled"] = Enabled, ["others"] = Others,
        ["rows"] = new JsonArray([.. Rows.Select(r => (JsonNode)new JsonObject
        {
            ["domains"] = new JsonArray([.. r.Domains.Select(d => (JsonNode)d)]), ["strategy"] = r.Strategy,
        })]),
    };

    public static AdvancedConfig FromJson(JsonNode? n)
    {
        var c = new AdvancedConfig();
        if (n is not JsonObject o) return c;
        c.Enabled = o["enabled"]?.GetValue<bool>() ?? false;
        c.Others = o["others"]?.GetValue<string>() is { } s && AdvancedSites.IsOthers(s) ? s : AdvancedSites.OthersLists;
        if (o["rows"] is JsonArray rows)
            foreach (var r in rows)
                c.Rows.Add(new()
                {
                    Domains = r?["domains"] is JsonArray d ? [.. d.Select(x => x?.GetValue<string>() ?? "")] : [],
                    Strategy = r?["strategy"]?.GetValue<string>() ?? AdvancedSites.Auto,
                });
        return c;
    }
}

/// <summary>Строка плана сборки: сайты и стратегия, по которой они идут; null — напрямую.</summary>
public sealed record AdvancedPlanRow(IReadOnlyList<string> Domains, Strategy? Strategy);

/// <summary>Что попадает в командную строку: уже разрешённые строки (стратегии найдены, сайты проверены) и режим остальных адресов.</summary>
public sealed record AdvancedPlan(string Others, IReadOnlyList<AdvancedPlanRow> Rows)
{
    /// <summary>Сайты «напрямую» — исключения во всех ролях каталога.</summary>
    public IReadOnlyList<string> Direct => [.. Rows.Where(r => r.Strategy is null).SelectMany(r => r.Domains).Distinct()];
    /// <summary>Строки со своей стратегией — профили в начале командной строки.</summary>
    public IEnumerable<AdvancedPlanRow> Applied => Rows.Where(r => r.Strategy is not null);
}

/// <summary>Правила продвинутого режима: что допустимо в state.json, в командах помощника и в командной строке winws.</summary>
public static partial class AdvancedSites
{
    public const string Auto = "auto", Direct = "direct";
    public const string OthersDirect = "direct", OthersLists = "lists", OthersAll = "all";
    public const int MaxRows = 20, MaxDomains = 50;
    /// <summary>Сайтов в «Открывать без оптимизации» (API 1.10): они идут в командную строку winws и службы — длина ограничена.</summary>
    public const int MaxExclude = 200;

    public static bool IsOthers(string? s) => s is OthersDirect or OthersLists or OthersAll;

    /// <summary>id стратегии — как в каталоге: латиница, цифры, «-», «_», «.».</summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.\-]{0,63}\z")]
    private static partial Regex IdRx();

    public static bool IsStrategyChoice(string? s) => s is Auto or Direct || s is not null && IdRx().IsMatch(s);

    /// <summary>
    /// Домен для --hostlist-domains: строгий шаблон, как у списков (<see cref="DataStore.NormalizeDomain"/>): только строчные латиница, цифры, «-» и точки
    /// (русские имена — в punycode), без схемы, пути, «^» и пробелов. null — не домен.
    /// </summary>
    public static string? Domain(string? raw)
    {
        raw = raw?.Trim();
        if (raw is null || raw.Length > 300 || raw.Any(c => char.IsWhiteSpace(c) || c is '^' or ',' or '\\' or '"' or '*')) return null;
        var d = DataStore.NormalizeDomain(raw);
        return d is not null && IsDomain(d) ? d : null;
    }

    /// <summary>Строка уже в нормальном виде (для проверки перед командной строкой).</summary>
    public static bool IsDomain(string d) => d.Length is > 0 and <= 253 && DomainRx().IsMatch(d);

    [GeneratedRegex(@"^([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+(xn--[a-z0-9-]{2,59}|[a-z]{2,63})\z")]
    private static partial Regex DomainRx();

    /// <summary>Сайты строки → нормальный вид без повторов; первая негодная — причина.</summary>
    public static (List<string>? Domains, string? Problem) Domains(IEnumerable<string> raw, int row)
    {
        var list = new List<string>();
        foreach (var r in raw)
        {
            if (string.IsNullOrWhiteSpace(r)) continue;
            var d = Domain(r);
            if (d is null) return (null, L.T("sites.adv.err.row_not_domain", new { row = row + 1 }));
            if (!list.Contains(d)) list.Add(d);
        }
        if (list.Count == 0) return (null, L.T("sites.adv.err.row_empty", new { row = row + 1 }));
        if (list.Count > MaxDomains) return (null, L.T("sites.adv.err.row_too_many", new { row = row + 1, max = MaxDomains }));
        return (list, null);
    }

    static string Short(string s) => s.Length > 30 ? s[..30] + "…" : s.Trim();

    /// <summary>
    /// Проверка того, что задал человек. <paramref name="strategyExists"/> — есть ли такая стратегия (null — не проверять). Null — всё в порядке.
    /// Сайты в конфигурации уже в нормальном виде (<see cref="Domains"/>), здесь они проверяются заново.
    /// </summary>
    public static string? Problem(AdvancedConfig c, Func<string, bool>? strategyExists = null)
    {
        if (!IsOthers(c.Others)) return L.T("core.adv.others");
        if (c.Rows.Count > MaxRows) return L.T("core.adv.too_many_rows", new { max = MaxRows });
        for (int i = 0; i < c.Rows.Count; i++)
        {
            var r = c.Rows[i];
            var (d, p) = Domains(r.Domains, i);
            if (p is not null) return p;
            if (d!.Count != r.Domains.Count || !d.SequenceEqual(r.Domains)) return L.T("core.adv.row_sites", new { n = i + 1 });
            if (!IsStrategyChoice(r.Strategy)) return L.T("core.adv.row_strategy", new { n = i + 1 });
            if (r.Strategy is not (Auto or Direct) && strategyExists is { } ex && !ex(r.Strategy)) return L.T("core.adv.row_no_strategy", new { n = i + 1 });
        }
        return null;
    }

    /// <summary>Текст «оптимизировать нечего»: остальные напрямую и ни одной строки со своей стратегией (ни профилей программ).</summary>
    public static string NothingToDo => L.T("core.adv.nothing_to_do");

    public static bool IsEmpty(AdvancedConfig c) => c.Enabled && c.Others == OthersDirect && !c.Rows.Any(r => r.Strategy != Direct);

    /// <summary>
    /// Конфигурация → план сборки. Выключен — null. Негодные строки (не прошли проверку) и строки, чьей стратегии нет (удалили),
    /// пропускаются: файл state.json мог тронуть кто угодно, а удалённая стратегия не должна ронять запуск.
    /// «auto» — итог подбора, до него — текущая стратегия.
    /// </summary>
    public static AdvancedPlan? Plan(AdvancedConfig? c, Func<string, Strategy?> find, string? currentId)
    {
        if (c is not { Enabled: true } || !IsOthers(c.Others)) return null;
        var rows = new List<AdvancedPlanRow>();
        foreach (var r in c.Rows.Take(MaxRows))
        {
            var (domains, problem) = Domains(r.Domains, 0);
            if (problem is not null || domains is null || !domains.SequenceEqual(r.Domains)) continue;
            if (r.Strategy == Direct) { rows.Add(new(domains, null)); continue; }
            var id = r.Strategy == Auto ? r.Resolved ?? currentId : IsStrategyChoice(r.Strategy) ? r.Strategy : null;
            if (id is not null && find(id) is { } s) rows.Add(new(domains, s));
        }
        return new(c.Others, rows);
    }

    /// <summary>Id группы подбора для строки «auto» с номером <paramref name="row"/> (с нуля): adv-0, adv-1…</summary>
    public static string GroupId(int row) => $"adv-{row}";

    public static bool IsGroupId(string? id) => id is not null && id.StartsWith("adv-", StringComparison.Ordinal);

    /// <summary>
    /// Группы подбора для строк «auto»: у каждой своя группа с адресом первого сайта, вне оценки стратегии (<see cref="TargetGroup.Extra"/>).
    /// Подбор по ним нужен, пока режим включён.
    /// </summary>
    public static List<TargetGroup> Groups(AdvancedConfig c)
    {
        var groups = new List<TargetGroup>();
        if (!c.Enabled) return groups;
        for (int i = 0; i < c.Rows.Count && i < MaxRows; i++)
        {
            var r = c.Rows[i];
            if (r.Strategy != Auto || r.Domains.Count == 0 || Domain(r.Domains[0]) is not { } first) continue;
            groups.Add(new(GroupId(i), L.T("core.adv.group_line", new { n = i + 1 }), [new($"https://{first}/")], Extra: true));
        }
        return groups;
    }

    /// <summary>
    /// Лучшая стратегия подбора для каждой строки «auto» (по доле открытых адресов группы, потом по времени); стратегии с ошибкой не берутся.
    /// Строка, у которой не открылось ничего, остаётся без итога — идёт по текущей стратегии. Меняет <paramref name="c"/>; true — что-то поменялось.
    /// </summary>
    public static bool Resolve(AdvancedConfig c, SelectionReport report)
    {
        bool changed = false;
        for (int i = 0; i < c.Rows.Count; i++)
        {
            var row = c.Rows[i];
            if (row.Strategy != Auto) continue;
            var id = GroupId(i);
            if (report.TestedGroups.All(g => g.Id != id)) continue;   // группа не проверялась (открывалась и без обхода) — прежний итог остаётся
            string? best = null; double bestShare = 0, bestSec = 0;
            foreach (var s in report.Scores)
            {
                if (s.Error is not null) continue;
                var v = s.Verdicts.Where(x => x.GroupId == id).ToList();
                if (v.Count == 0) continue;
                var ok = v.Where(x => x.Ok).ToList();
                double share = (double)ok.Count / v.Count, sec = ok.Count == 0 ? 99 : ok.Average(x => x.BestSeconds);
                if (share > bestShare || share == bestShare && share > 0 && sec < bestSec) { best = s.Strategy.Id; bestShare = share; bestSec = sec; }
            }
            if (best != row.Resolved) { row.Resolved = best; changed = true; }
        }
        return changed;
    }
}
