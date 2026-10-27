using System.Security.Cryptography;
using System.Text;

namespace DpiShell.Core;

/// <summary>
/// Группа «Своих сайтов» (API 1.18): имя, которое задаёт человек («Telegram», «Spotify»), и сайты. Пустое имя — «Прочее» (сайты без группы).
/// Поле необязательное везде: без групп все сайты — «Прочее», как было.
/// </summary>
public sealed class SiteGroup
{
    public string Name { get; set; } = "";
    public List<string> Sites { get; set; } = [];

    public SiteGroup() { }
    public SiteGroup(string name, IEnumerable<string> sites) { Name = name; Sites = [.. sites]; }
}

/// <summary>Группы своих сайтов: разбор и очистка, выборка для подбора (architecture.md «Группы своих сайтов»).</summary>
public static class SiteGroups
{
    /// <summary>Сколько адресов группы проверяет подбор. Больше — выборка: проверка не должна длиться часами.</summary>
    public const int SampleSize = 10;
    /// <summary>Групп не больше (не считая «Прочее»).</summary>
    public const int MaxGroups = 50;
    /// <summary>Длина имени группы.</summary>
    public const int MaxName = 40;

    /// <summary>
    /// Имя без управляющих и невидимых знаков (Cc и Cf: U+200B, U+202E и т. п. — переворот текста, «двойники» имён) и лишних пробелов,
    /// не длиннее <see cref="MaxName"/>; пусто — «Прочее».
    /// </summary>
    public static string CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var s = new string(name.Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).ToArray()).Trim();
        s = string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return s.Length > MaxName ? s[..MaxName].TrimEnd() : s;
    }

    /// <summary>
    /// Привести группы к виду «именованные в порядке ввода + Прочее последней»: имена очищены, одинаковые (без учёта регистра) слиты,
    /// сайт — голый домен (<see cref="DataStore.NormalizeDomain"/>: без схемы, пути и порта) в одной группе (первое вхождение), негодные строки —
    /// в <paramref name="rejected"/>. Группа без сайтов остаётся (её создали руками).
    /// Прочее — всегда последняя и всегда присутствует в результате (возможно пустая).
    /// </summary>
    public static List<SiteGroup> Normalize(IEnumerable<SiteGroup> groups, out List<string> rejected)
    {
        rejected = [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var named = new List<SiteGroup>();
        var other = new SiteGroup();
        foreach (var g in groups)
        {
            var name = CleanName(g.Name);
            SiteGroup target;
            if (name.Length == 0) target = other;
            else
            {
                target = named.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
                    ?? (named.Count < MaxGroups ? Add(named, name) : other);
            }
            foreach (var raw in g.Sites)
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (DataStore.NormalizeDomain(line) is not { } d) { rejected.Add(line); continue; }
                if (seen.Add(d)) target.Sites.Add(d);
            }
        }
        named.Add(other);
        return named;

        static SiteGroup Add(List<SiteGroup> list, string name) { var g = new SiteGroup(name, []); list.Add(g); return g; }
    }

    /// <summary>Все сайты по группам подряд (именованные, затем Прочее).</summary>
    public static List<string> Flatten(IEnumerable<SiteGroup> groups) => [.. groups.SelectMany(g => g.Sites)];

    /// <summary>
    /// Плоский список → группы по известным именованным: сайт из списка берёт свою группу, остальные — Прочее. Именованные группы
    /// остаются, даже когда сайтов в них не осталось. Прочее — последней.
    /// </summary>
    public static List<SiteGroup> Apply(IReadOnlyList<string> flat, IEnumerable<SiteGroup>? named)
    {
        var result = new List<SiteGroup>();
        var left = new List<string>(flat);
        foreach (var g in named ?? [])
        {
            if (CleanName(g.Name).Length == 0) continue;
            var mine = left.Where(s => g.Sites.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();
            left.RemoveAll(mine.Contains);
            result.Add(new(g.Name, mine));
        }
        result.Add(new("", left));
        return result;
    }

    /// <summary>Именованные группы без «Прочее» — то, что хранится в состоянии.</summary>
    public static List<SiteGroup> Named(IEnumerable<SiteGroup> groups) =>
        [.. groups.Where(g => CleanName(g.Name).Length > 0).Select(g => new SiteGroup(g.Name, g.Sites))];

    /// <summary>
    /// Устойчивая выборка: адреса, упорядоченные по хешу, первые <paramref name="n"/>. Не зависит от порядка списка и от прогона к прогону —
    /// одни и те же адреса у всех стратегий (и у соседних прогонов, их историю можно сравнивать). Список короче — весь.
    /// </summary>
    public static IReadOnlyList<string> Sample(IReadOnlyList<string> sites, int n = SampleSize) =>
        sites.Count <= n ? sites
            : [.. sites.OrderBy(s => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s.Trim().ToLowerInvariant())))).ThenBy(s => s, StringComparer.Ordinal).Take(n)];
}
