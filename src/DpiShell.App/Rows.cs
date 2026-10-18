using System.Net;
using System.Security.Authentication;
using DpiShell.Core;

namespace DpiShell.App;

public sealed class GroupOption(string id, string title, bool isChecked) : Observable
{
    bool _checked = isChecked;
    public string Id { get; } = id;
    public string Title { get; } = title;
    public bool IsChecked { get => _checked; set => Set(ref _checked, value); }
}

/// <summary>
/// Строка списка стратегий на главной: название, статистика подборов по группам, найдена ли поиском.
/// Warning — «ломала обычные сайты»; Details — адреса последнего прогона (раскрываются).
/// </summary>
public sealed class StrategyChoice(string id, string title, string stats, bool found, string warning = "", IReadOnlyList<CheckRow>? details = null) : Observable
{
    bool _expanded;
    public string Id { get; } = id;
    public string Title { get; } = title;
    public string Stats { get; } = stats;
    public bool Found { get; } = found;
    public string Warning { get; } = warning;
    /// <summary>Сколько сайтов открылось в последнем прогоне; null — прогона не было.</summary>
    public int? Opened { get; init; }
    /// <summary>Подсказка к названию: прежняя запись с датой поиска, если показано имя по параметрам; иначе название.</summary>
    public string Tip { get => _tip.Length > 0 ? _tip : Title; init => _tip = value; }
    readonly string _tip = "";
    public bool HasWarning => Warning.Length > 0;
    public IReadOnlyList<CheckRow> Details { get; } = details ?? [];
    public bool HasDetails => Details.Count > 0;
    public bool Expanded { get => _expanded; set => Set(ref _expanded, value); }

    /// <summary>
    /// Подпись по статистике: «YouTube 5/5 · Discord 4/5 · свои сайты 7 из 9» — группы каталога (открылась в N из M прогонов),
    /// свои адреса — сколько открылось в последнем прогоне. Старая статистика (без групп) — «открыла всё в N из M подборов».
    /// </summary>
    public static (string Stats, string Warning) Describe(StrategyStats.Stat st, GroupNames names)
    {
        var parts = (st.Groups ?? []).Select(g => $"{names.Title(g.Id)} {g.Ok}/{g.Runs}").ToList();
        if (st.UserTotal > 0) parts.Add($"свои сайты {st.UserOk} из {st.UserTotal}");
        var stats = parts.Count > 0 ? string.Join(" · ", parts)
            : $"открыла всё в {st.FullOk} из {st.Runs} подборов ({(double)st.FullOk / st.Runs:P0})";
        var warning = st.ControlBroken > 0 ? $"ломала обычные сайты: {st.ControlBroken} из {st.Runs} подборов" : "";
        return (stats, warning);
    }

    /// <summary>Адреса последнего прогона — те же строки, что в таблице подбора.</summary>
    public static IReadOnlyList<CheckRow> LastDetails(StrategyStats.Stat st, GroupNames names) =>
        st.Last is not { } last ? [] : CheckRow.List(last.Targets.Select(t =>
        {
            var target = new Target(t.Url, Required: t.Required);
            var probes = t.Probes.Select(p => new ProbeResult(target,
                Enum.TryParse<SslProtocols>(p.Tls, out var tls) ? tls : SslProtocols.None,
                Enum.TryParse<ProbeOutcome>(p.Outcome, out var o) ? o : ProbeOutcome.Error, p.Seconds, 0, p.Detail)).ToList();
            return new TargetVerdict(t.Control ? "control" : t.GroupId, target, t.Ok, t.Seconds, probes);
        }), names);
}

/// <summary>Названия групп целей для подробностей: из каталога целей и «Свои адреса»; контрольные («Обычные сайты») отмечены.</summary>
public sealed class GroupNames
{
    readonly Dictionary<string, (string Title, bool Control)> _map = new() { ["user"] = ("Свои адреса", false) };

    public GroupNames(IEnumerable<TargetGroup> groups)
    {
        foreach (var g in groups) _map[g.Id] = (g.Title, g.Control);
    }

    public static readonly GroupNames Empty = new([]);

    public string Title(string id) => _map.TryGetValue(id, out var g) ? g.Title : id;
    public bool Control(string id) => _map.TryGetValue(id, out var g) && g.Control;
}

/// <summary>
/// Строка подробностей по адресу — одна для «Проверить сейчас», таблиц подбора и списка стратегий: группа, адрес, ✓/✗,
/// исход и подробности по обеим версиям TLS (код, байты, цепочка редиректов), время. Alert — провал, который решает исход:
/// обязательный адрес или сломанный «обычный сайт».
/// </summary>
public sealed record CheckRow(bool Ok, string Group, string Url, string Detail, string Time = "", string Note = "", bool Alert = false, string Browser = "")
{
    /// <summary>⚠ — программа открывает, браузер (DNS системы) — нет; причина — в <see cref="Browser"/>.</summary>
    public string Mark => !Ok ? "✗" : HasBrowser ? "⚠" : "✓";
    public bool HasNote => Note.Length > 0;
    public bool HasBrowser => Browser.Length > 0;

    public static CheckRow From(TargetVerdict v, Func<string, string> groupTitle, bool control = false)
    {
        var note = v.BrowserFails ? "✓ для программы, ✗ для браузера"
            : v.Ok ? "" : control ? "ломает обычный сайт" : v.Target.Required ? "обязательный" : "необязательный";
        var seconds = v.Ok ? v.BestSeconds : v.Probes.Select(p => p.Seconds).DefaultIfEmpty(0).Max();
        var detail = Texts.ProbeDetail(v.Probes);
        // Адреса разошлись — оба пути с адресами: программа (DoH) и браузер (DNS системы, с hosts).
        if (v.Browser is { } b)
            detail = $"Программа (защищённый DNS: {Ips(b.Doh)}): {detail}\nБраузер ({(b.FromHosts ? "hosts" : "DNS системы")}: {(b.System.Length > 0 ? Ips(b.System) : "имени нет")}): "
                + (b.Probes.Count > 0 ? Texts.ProbeDetail(b.Probes) : "не открыть") + (b.Ok ? "" : " — " + b.Reason);
        return new(v.Ok, groupTitle(v.GroupId), v.Target.Url, detail, $"{seconds:0.0} с", note, !v.Ok && (control || v.Target.Required),
            v.BrowserFails ? v.Browser!.Reason : "");
    }

    static string Ips(IPAddress[] a) => string.Join(", ", a.Take(2)) + (a.Length > 2 ? " …" : "");

    public static CheckRow From(TargetVerdict v, GroupNames names) => From(v, names.Title, names.Control(v.GroupId));

    /// <summary>Подробности по адресам: сначала провалы, решающие исход, затем остальное в порядке проверки.</summary>
    public static IReadOnlyList<CheckRow> List(IEnumerable<TargetVerdict> verdicts, GroupNames names) =>
        verdicts.Select(v => From(v, names)).OrderByDescending(r => r.Alert).ToList();

    /// <summary>«не открылось: YouTube, Discord» — группы, где не открылся обязательный адрес (видно в строке таблицы, без раскрытия).</summary>
    public static string Missed(IEnumerable<CheckRow> rows)
    {
        var bad = rows.Where(r => r.Alert && r.Note == "обязательный").Select(r => r.Group).Distinct().ToList();
        return bad.Count == 0 ? "" : "не открылось: " + string.Join(", ", bad);
    }
}

/// <summary>Строка таблицы подбора. Раскрывается — результаты по всем проверенным адресам.</summary>
public sealed class ScoreRow(StrategyScore s, GroupNames? names = null) : Observable
{
    bool _best, _expanded;
    IReadOnlyList<CheckRow>? _details;
    public StrategyScore Score { get; } = s;
    public string Id => Score.Strategy.Id;
    public string Title => StrategyNaming.DisplayTitle(Score.Strategy.Title, Score.Strategy.Roles);
    /// <summary>Прежняя запись названия (с датой поиска), если показано имя по параметрам.</summary>
    public string Tip => StrategyNaming.Tip(Score.Strategy.Title, Score.Strategy.Roles) is { Length: > 0 } t ? t : Title;
    public string Groups => $"{Score.GroupsPassed} из {Score.GroupsTotal}";
    public string Share => $"{Score.TargetShare * 100:0} %";
    public string Avg => Score.Verdicts.Any(v => v.Ok) ? $"{Score.AvgSeconds:0.00} с" : "—";
    /// <summary>Ошибка запуска или «ломает обычные сайты»; без неё — где не открылись обязательные адреса.</summary>
    public string Error => Score.Error is null ? CheckRow.Missed(Details) : Score.Error.Length > 80 ? Score.Error[..80] + "…" : Score.Error;
    public string ErrorFull => Score.Error ?? CheckRow.Missed(Details);
    public bool Usable => Score.Error is null && Score.GroupsPassed > 0;
    public bool IsBest { get => _best; set { if (Set(ref _best, value)) Raise(nameof(Mark)); } }
    public string Mark => IsBest ? "★" : "";
    /// <summary>Найдена глубоким поиском, а не из готового набора.</summary>
    public bool Found => Score.Strategy.Source == "search";

    public IReadOnlyList<CheckRow> Details => _details ??= CheckRow.List(Score.Verdicts, names ?? GroupNames.Empty);
    public bool HasDetails => Score.Verdicts.Count > 0;
    public bool Expanded { get => _expanded; set => Set(ref _expanded, value); }
}

/// <summary>Строка сравнения DNS на «Диагностике».</summary>
public sealed record DnsRow(string Host, string System, string Doh, bool Spoofed)
{
    public string Mark => Spoofed ? "подмена" : "";

    public static DnsRow From(DnsComparison c) => new(
        c.Host,
        c.System.Length > 0 ? Ips(c.System) : "имени нет",
        c.Doh.Length > 0 ? Ips(c.Doh) : "нет ответа",
        c.Spoofed);

    static string Ips(IPAddress[] a) => string.Join(", ", a.Take(2)) + (a.Length > 2 ? " …" : "");
}

/// <summary>Проверка окружения на «Диагностике»: ✓ — в порядке, ⚠ — мешает (что делать — в тексте).</summary>
public sealed record FindingRow(bool Ok, string Title, string Text)
{
    public string Mark => Ok ? "✓" : "⚠";
}

public static class Texts
{
    public static string Svc(SvcState s) => s switch
    {
        SvcState.NotInstalled => "не установлена",
        SvcState.Stopped => "остановлена",
        SvcState.StartPending => "запускается",
        SvcState.StopPending => "останавливается",
        SvcState.Running => "работает",
        _ => "неизвестно",
    };

    public static string Outcome(ProbeOutcome o) => o switch
    {
        ProbeOutcome.Timeout => "нет ответа",
        ProbeOutcome.ConnectFailed => "нет соединения",
        ProbeOutcome.TlsFailed => "защищённое соединение не устанавливается",
        ProbeOutcome.Reset => "соединение сброшено",
        ProbeOutcome.Truncated => "загрузка обрывается",
        ProbeOutcome.BadStatus => "ошибка на сайте",
        ProbeOutcome.ForeignRedirect => "заглушка провайдера",
        ProbeOutcome.Blocked => "блокировка (451)",
        ProbeOutcome.Ok => "открывается",
        _ => "ошибка",
    };

    public static string Tls(SslProtocols v) => v switch
    {
        SslProtocols.Tls13 => "TLS 1.3",
        SslProtocols.Tls12 => "TLS 1.2",
        _ => v.ToString(),
    };

    /// <summary>
    /// По версиям TLS: «TLS 1.3 и TLS 1.2: 301 → www.youtube.com/ → 200, 512 КБ» — одинаковые сливаются;
    /// у провала — исход словами: «TLS 1.3: загрузка обрывается, получено 16384 из 500000».
    /// </summary>
    public static string ProbeDetail(IEnumerable<ProbeResult> probes) => string.Join(" · ", probes
        .Select(p => (p.Tls, Text: p.Ok ? p.Detail : p.Detail.Length > 0 ? $"{Outcome(p.Outcome)}, {p.Detail}" : Outcome(p.Outcome)))
        .GroupBy(x => x.Text)
        .Select(g => $"{string.Join(" и ", g.Select(x => Tls(x.Tls)))}: {g.Key}"));

    public static string Problem(TargetVerdict v) =>
        v.Probes.FirstOrDefault(p => !p.Ok) is { } p ? Outcome(p.Outcome) : "не открывается";

    /// <summary>Исключение → текст для человека.</summary>
    public static string Friendly(Exception e) => e switch
    {
        System.Net.Http.HttpRequestException => "Не удалось скачать ядро. Проверьте интернет и повторите (GitHub тоже бывает заблокирован — помогает повтор).",
        SecurityException s => s.Message,
        ConflictException c => $"Мешают другие программы обхода: {string.Join(", ", c.Conflicts.Select(x => x.Name).Distinct())}. Закройте их и повторите.",
        UnauthorizedAccessException => "Нет доступа к файлам программы. Возможно, их держит антивирус. " + e.Message,
        System.ComponentModel.Win32Exception w => "Не удалось запустить программу обхода (антивирус?): " + w.Message,
        // Помощник старше окна не знает новой команды (у помощников до протокола 1.1 нет списка команд — окно не может
        // заранее выключить кнопку). Живой случай 2026-10-17: «Неизвестная команда «dns_set»» вместо «обновите помощника».
        DpiShell.Core.Helper.HelperException { Code: "invalid" } h when h.Message.StartsWith("Неизвестная команда", StringComparison.Ordinal)
            => "Помощник старее программы и этого не умеет — обновите его: «Настройки» → «О программе» (один запрос прав администратора).",
        AggregateException { InnerException: { } inner } => Friendly(inner),
        _ => e.Message,
    };
}
