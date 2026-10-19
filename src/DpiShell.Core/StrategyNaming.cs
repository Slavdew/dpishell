using System.Text.RegularExpressions;

namespace DpiShell.Core;

/// <summary>
/// Имена найденных стратегий для опытных: по главным параметрам — приём (fake, multisplit…), fooling (ts, md5sig…), повторы.
/// «Новая: fake+multisplit · ts · ×6». Роли google и general различаются — обе: «Новая: google fake · ts · ×6; general multisplit · ×2».
/// </summary>
public static partial class StrategyNaming
{
    /// <summary>Имя, которое дала программа или роутер сама («Новая стратегия 2 (поиск 16.10 23:19)», «Найденная 1 (261016-2213)»); своё имя пользователя — нет.</summary>
    public static bool IsAutoName(string? title) => title is not null && AutoNameRx().IsMatch(title);

    /// <summary>Дата из автоимени («поиск 16.10 23:19», «261016-2213») для подсказки; нет — null.</summary>
    public static string? AutoNameDate(string? title)
    {
        if (title is null || AutoNameRx().Match(title) is not { Success: true } m) return null;
        return m.Groups["d"].Value.Trim();
    }

    /// <summary>
    /// Название для показа: автоимя с известными ролями — по параметрам, иначе как есть (своё имя, нет ролей, нечего показать).
    /// </summary>
    public static string DisplayTitle(string title, IReadOnlyDictionary<string, string>? roles) =>
        IsAutoName(title) && Name(roles) is { } n ? n : title;

    /// <summary>Подсказка к названию: что было записано (с датой поиска), если показано другое имя; иначе пусто.</summary>
    public static string Tip(string title, IReadOnlyDictionary<string, string>? roles) =>
        DisplayTitle(title, roles) != title ? title : "";

    /// <summary>«Новая: fake+multisplit · ts · ×6» по ролям google и general; ролей с приёмом нет — null.</summary>
    public static string? Name(IReadOnlyDictionary<string, string>? roles, string prefix = "Новая")
    {
        if (roles is null) return null;
        var google = roles.TryGetValue("google", out var g) ? Summary(g) : null;
        var discord = roles.TryGetValue("discord_tcp", out var ds) ? Summary(ds) : null;
        var general = roles.TryGetValue("general", out var d) ? Summary(d) : null;
        var named = new[] { ("google", google), ("discord", discord), ("general", general) }.Where(x => x.Item2 is not null).ToList();
        if (named.Count == 0) return null;
        if (named.Select(x => x.Item2).Distinct().Count() == 1) return $"{prefix}: {named[0].Item2}";
        return $"{prefix}: " + string.Join("; ", named.Select(x => $"{x.Item1} {x.Item2}"));
    }

    /// <summary>«fake+multisplit · ts · ×6» из одной строки аргументов; приёма нет — null.</summary>
    public static string? Summary(string? args)
    {
        if (string.IsNullOrWhiteSpace(args)) return null;
        var desync = Value(args, "dpi-desync");
        if (desync is null) return null;
        var parts = new List<string> { desync.Replace(',', '+') };
        if (Value(args, "dpi-desync-fooling") is { } f) parts.Add(f.Replace(',', '+'));
        if (Value(args, "dpi-desync-repeats") is { } r) parts.Add("×" + r);
        return string.Join(" · ", parts);
    }

    static string? Value(string args, string name)
    {
        var m = Regex.Match(args, @"(?:^|\s)--" + Regex.Escape(name) + @"=(\S+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    [GeneratedRegex(@"^(?:Новая стратегия|Найденная) \d+ \((?<d>.*)\)$")]
    private static partial Regex AutoNameRx();
}
