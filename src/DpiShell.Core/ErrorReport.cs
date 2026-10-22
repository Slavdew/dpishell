using System.Text;
using DpiShell.Core.Helper;
using DpiShell.Core.Localization;

namespace DpiShell.Core;

/// <summary>Ошибка операции для «Подробнее» и «Скопировать»: что случилось, при каком действии, когда.</summary>
/// <param name="Detail">Тип и текст исключения (с вложенными), если ошибка пришла исключением.</param>
public sealed record ErrorInfo(string Text, string? Action, DateTime At, string? Detail)
{
    /// <summary>Тип и текст исключения с вложенными — без стека (в нём пути сборки, человеку не нужны).</summary>
    public static string? Describe(Exception? e)
    {
        if (e is null) return null;
        var parts = new List<string>();
        for (var x = e; x is not null && parts.Count < 4; x = x.InnerException)
        {
            var code = x is HelperException h ? $" [{h.Code}]" : "";
            parts.Add($"{x.GetType().Name}{code}: {x.Message.Trim()}");
        }
        return string.Join(" → ", parts);
    }
}

/// <summary>
/// Текст «Скопировать» у ошибки: ошибка, действие, время, версии окна, помощника и роутера. Личное вычищается так же, как в «Сохранить отчёт»
/// (<see cref="SupportReport.Scrub"/>); версии не чистятся («0.10.0» — не адрес).
/// </summary>
public static class ErrorReport
{
    /// <param name="versions">Готовые строки версий («Программа: 0.10.1»), не чистятся.</param>
    /// <param name="hide">Что окно знает как личное: свои сайты, адреса прокси и роутера.</param>
    public static string Build(ErrorInfo e, IEnumerable<string> versions, IEnumerable<string>? hide = null)
    {
        var h = (hide ?? []).ToList();
        string Clean(string s) => SupportReport.Scrub(One(s), h);
        var sb = new StringBuilder();
        sb.Append(L.T("shell.error.title")).Append("\r\n");
        sb.Append(L.T("shell.error.time", new { v = e.At.ToString("yyyy-MM-dd HH:mm:ss") })).Append("\r\n");
        if (!string.IsNullOrWhiteSpace(e.Action)) sb.Append(L.T("shell.error.action", new { v = Clean(e.Action) })).Append("\r\n");
        sb.Append(L.T("shell.error.text", new { v = Clean(e.Text) })).Append("\r\n");
        if (!string.IsNullOrWhiteSpace(e.Detail)) sb.Append(L.T("shell.error.detail", new { v = Clean(e.Detail) })).Append("\r\n");
        foreach (var v in versions) sb.Append(One(v)).Append("\r\n");
        sb.Append(L.T("shell.error.private"));
        return sb.ToString();
    }

    static string One(string s) => string.Join(" ", s.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()));
}
