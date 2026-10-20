using DpiShell.Core.Localization;
namespace DpiShell.Core;

/// <summary>
/// Списки доменов ролей (lists\google.txt — YouTube, lists\discord.txt — Discord). Домен не должен быть в обоих: первый подходящий профиль
/// забирает соединение (google раньше general, discord_tcp раньше google), и часть адресов ушла бы не в свою часть смешанной стратегии
/// (docs/research/mixed-strategies.md §1). general.txt с discord.txt пересекается намеренно: discord_tcp стоит раньше и перекрывает его.
/// </summary>
public static class RoleLists
{
    public const string Google = "google.txt", Discord = "discord.txt";

    static IEnumerable<string> Read(string file) =>
        File.Exists(file)
            ? File.ReadLines(file).Select(l => l.Trim().ToLowerInvariant()).Where(l => l.Length > 0 && !l.StartsWith('#'))
            : [];

    /// <summary>Совпадающие домены двух списков: одинаковые или один — поддомен другого. Пусто — списки разведены.</summary>
    public static IReadOnlyList<string> Overlap(string googleFile, string discordFile)
    {
        var g = Read(googleFile).ToHashSet();
        var d = Read(discordFile).ToHashSet();
        return g.Where(x => d.Any(y => x == y || x.EndsWith("." + y) || y.EndsWith("." + x)))
            .Concat(d.Where(y => g.Any(x => y.EndsWith("." + x)))).Distinct().Order().ToList();
    }

    /// <summary>Причина отказа для папки данных (<c>lists\</c> внутри): null — всё в порядке.</summary>
    public static string? Problem(string dataDir)
    {
        var lists = Path.Combine(dataDir, "lists");
        var overlap = Overlap(Path.Combine(lists, Google), Path.Combine(lists, Discord));
        if (overlap.Count == 0) return null;
        var shown = string.Join(", ", overlap.Take(3)) + (overlap.Count > 3 ? L.T("core.roles.more", new { n = overlap.Count - 3 }) : "");
        return L.T("core.roles.overlap", new { google = Google, discord = Discord, shown });
    }
}
