using System.Text;
using System.Text.RegularExpressions;
using DpiShell.Core.Localization;

namespace DpiShell.Core;

/// <summary>Раздел отчёта. <paramref name="Raw"/> — тексты не чистятся (версии: «0.10.0.0» не адрес).</summary>
public sealed record ReportSection(string Title, IReadOnlyList<string> Lines, bool Raw = false);

/// <summary>
/// «Сохранить отчёт» на «Диагностике»: один текстовый файл для обращения за помощью. Собирается из готовых разделов, а всё личное
/// вычищается здесь же, в одном месте: адреса (IPv4, IPv6, MAC) этого ПК и сети, учётные данные в ссылках, пароли и токены в строках
/// «имя=значение», имя пользователя и компьютера, пути профиля, а также всё, что окно назвало явно (свои сайты, исключения, адреса прокси и роутера).
/// </summary>
public static partial class SupportReport
{
    /// <summary>Сколько последних строк каждого журнала берётся в отчёт.</summary>
    public const int LogLines = 200;

    public static string Build(DateTime at, IEnumerable<ReportSection> sections, IEnumerable<string>? hide = null)
    {
        var hideList = (hide ?? []).ToList();
        var sb = new StringBuilder();
        sb.Append(L.T("diag.report.header", new { time = at.ToString("yyyy-MM-dd HH:mm") })).Append("\r\n");
        foreach (var s in sections)
        {
            sb.Append("\r\n== ").Append(s.Title).Append(" ==\r\n");
            if (s.Lines.Count == 0) { sb.Append(L.T("diag.report.none")).Append("\r\n"); continue; }
            foreach (var line in s.Lines)
                sb.Append(s.Raw ? line : Scrub(line, hideList)).Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>Убрать личное из строки (см. описание класса). <paramref name="hide"/> — подстроки, которые не должны попасть в файл.</summary>
    public static string Scrub(string text, IEnumerable<string>? hide = null)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var mark = L.T("diag.report.hidden");

        // Названное явно: сначала длинные («https://bank.example/path» раньше «bank.example»).
        var names = (hide ?? []).Where(x => x is { Length: >= 3 }).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(x => x.Length);
        foreach (var n in names)
            text = text.Replace(n, mark, StringComparison.OrdinalIgnoreCase);
        // Имя пользователя и компьютера — целыми словами: короткое имя («user») не должно портить обычные слова.
        foreach (var n in Own())
            text = Regex.Replace(text, @"(?<![\p{L}\d])" + Regex.Escape(n) + @"(?![\p{L}\d])", mark, RegexOptions.IgnoreCase);

        text = UserInfoRx().Replace(text, mark + "@");
        text = BearerRx().Replace(text, m => m.Groups[1].Value + " " + mark);
        text = SecretRx().Replace(text, m => m.Groups[1].Value + m.Groups[2].Value + mark);
        text = ProfileRx().Replace(text, m => m.Groups[1].Value + mark);
        text = Ipv4Rx().Replace(text, mark);
        text = MacRx().Replace(text, mark);
        text = Ipv6Rx().Replace(text, mark);
        return text;
    }

    /// <summary>Имя пользователя и компьютера — слова, которых в чужом отчёте быть не должно.</summary>
    static IEnumerable<string> Own()
    {
        foreach (var n in new[] { Environment.UserName, Environment.MachineName })
            if (n is { Length: >= 3 }) yield return n;
    }

    /// <summary>Хост из адреса или «хост:порт» — чтобы вычищать и его, когда в журнале он без схемы и без логина.</summary>
    public static IEnumerable<string> Variants(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) yield break;
        var a = address.Trim();
        yield return a;
        var rest = a.Contains("://") ? a[(a.IndexOf("://", StringComparison.Ordinal) + 3)..] : a;
        var at = rest.LastIndexOf('@');
        if (at >= 0) { yield return rest[..at]; rest = rest[(at + 1)..]; }
        yield return rest;
        var host = rest.Split('/', '?', '#')[0];
        yield return host;
        if (host.LastIndexOf(':') is > 0 and var colon && !host.Contains(']') && host.Count(c => c == ':') == 1) yield return host[..colon];
    }

    [GeneratedRegex(@"(?<=://)([^/\s@]+)@")]
    private static partial Regex UserInfoRx();

    [GeneratedRegex(@"(?i)(\b(?:password|passwd|pwd|pass|token|secret|api[_-]?key|authorization|cookie)\b)(\s*[:=]\s*)(""[^""]*""|'[^']*'|[^\s,;&]+)")]
    private static partial Regex SecretRx();

    [GeneratedRegex(@"(?i)\b(Bearer|Basic)\s+[A-Za-z0-9._~+/=-]{6,}")]
    private static partial Regex BearerRx();

    [GeneratedRegex(@"(?i)([A-Z]:\\Users\\)[^\\/\s""'<>|]+")]
    private static partial Regex ProfileRx();

    [GeneratedRegex(@"(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?![\d])")]
    private static partial Regex Ipv4Rx();

    [GeneratedRegex(@"(?<![0-9A-Fa-f:-])(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}(?![0-9A-Fa-f:-])")]
    private static partial Regex MacRx();

    // Полный (8 групп) или со сжатием «::»; времени «12:30:45» это не касается.
    [GeneratedRegex(@"(?<![\w:.])(?:(?:[0-9A-Fa-f]{1,4}:){7}[0-9A-Fa-f]{1,4}|(?:[0-9A-Fa-f]{1,4}:){0,6}[0-9A-Fa-f]{1,4}::(?:[0-9A-Fa-f]{1,4}:){0,5}[0-9A-Fa-f]{0,4}|::(?:[0-9A-Fa-f]{1,4}:){0,5}[0-9A-Fa-f]{1,4})(?![\w:])")]
    private static partial Regex Ipv6Rx();
}
