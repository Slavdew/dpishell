using DpiShell.Core.Localization;

namespace DpiShell.Core.Router;

public enum SecurityState { Ok, Warn, Unknown }

/// <summary>Строка «Безопасности роутера»: отметка, название, вывод и (для Warn) варианты решения текстом — без кнопок.</summary>
public sealed record RouterSecurityRow(SecurityState State, string Label, string Text, string Fix)
{
    public string Mark => State switch { SecurityState.Ok => "✓", SecurityState.Warn => "⚠", _ => "•" };
    public bool IsWarn => State == SecurityState.Warn;
    public bool HasFix => Fix.Length > 0;
}

/// <summary>
/// «Безопасность роутера» (router-api.md §5.1 security_check, с 0.2.0-r13) — справочно, как на странице LuCI («Диагностика»).
/// Тексты — общие с страницей: router.security.&lt;id&gt; — название, .&lt;detail или state&gt; — вывод, .fix — варианты.
/// Незнакомый id (роутер новее окна) пропускается, незнакомое состояние — «не удалось проверить».
/// </summary>
public static class RouterSecurity
{
    public const string Method = "security_check";

    public static List<RouterSecurityRow> Rows(RouterSecurityReport? report)
    {
        var rows = new List<RouterSecurityRow>();
        foreach (var c in report?.Checks ?? [])
        {
            if (string.IsNullOrEmpty(c.Id)) continue;
            var b = "router.security." + c.Id;
            if (!L.Has(b)) continue;
            var state = c.State switch { "ok" => SecurityState.Ok, "warn" => SecurityState.Warn, _ => SecurityState.Unknown };
            var args = RouterPhrase.Args(c.Params);
            var detail = $"{b}.{(string.IsNullOrEmpty(c.Detail) ? c.State : c.Detail)}";
            var text = state == SecurityState.Unknown ? L.T("router.security.unknown")
                : L.Has(detail) ? L.T(detail, args) : L.T($"{b}.{c.State}", args);
            var fix = state == SecurityState.Warn && L.Has(b + ".fix") ? L.T(b + ".fix") : "";
            rows.Add(new(state, L.T(b), text, fix));
        }
        return rows;
    }

    /// <summary>Итог над строками: «Можно улучшить: …» или «Замечаний нет» (с оговоркой, если что-то не проверено).</summary>
    public static string Summary(IReadOnlyCollection<RouterSecurityRow> rows)
    {
        var bad = rows.Where(r => r.IsWarn).Select(r => r.Label).Distinct().ToList();
        if (bad.Count > 0) return "⚠ " + L.T("router.security.bad", new { list = string.Join(", ", bad) });
        return "✓ " + L.T(rows.Any(r => r.State == SecurityState.Unknown) ? "router.security.ok_partial" : "router.security.ok_all");
    }
}
