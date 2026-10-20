using System.Net;
using System.Security.Authentication;
using DpiShell.Core;

namespace DpiShell.App;

public sealed class GroupOption(string id, string title, bool isChecked) : Observable
{
    bool _checked = isChecked;
    public string Id { get; } = id;
    /// <summary>«Свои сайты» — по ключу на языке окна; остальные названия — из каталога целей.</summary>
    public string Title => Id == "user" ? L.T("select.group.user") : title;
    public bool IsChecked { get => _checked; set => Set(ref _checked, value); }
}

/// <summary>
/// Строка списка стратегий на главной: название, итог последнего прогона по группам, найдена ли поиском.
/// Warning — «ломает обычные сайты» (в последнем прогоне); Details — адреса последнего прогона (раскрываются).
/// </summary>
public sealed class StrategyChoice(string id, string title, string stats, bool found, string warning = "", IReadOnlyList<CheckRow>? details = null,
    string parameters = "") : Observable
{
    bool _expanded, _showParams;
    /// <summary>Точные параметры по ролям и командная строка winws для этого ПК (<see cref="StrategyParams"/>); пусто — стратегии нет у окна.</summary>
    public string Params { get; } = parameters;
    public bool HasParams => Params.Length > 0;
    public bool ShowParams { get => _showParams; set { if (Set(ref _showParams, value)) { Raise(nameof(ParamsLinkText)); Raise(nameof(ParamsVisible)); } } }
    /// <summary>Блок параметров показывается, только когда есть что показать: пустая рамка выглядит как сбой.</summary>
    public bool ParamsVisible => _showParams && HasParams;
    public string ParamsLinkText => L.T(ShowParams ? "home.params.hide" : "home.params.show");
    public string Id { get; } = id;
    public string Title { get; } = title;
    public string Stats { get; } = stats;
    public bool Found { get; } = found;
    /// <summary>Откуда стратегия: flowseal (готовая), search (найдена), mix (смешанная: «Смешанная · …» в названии), user (своя), pasted (вставлена).</summary>
    public string Source { get; init; } = found ? "search" : "flowseal";
    /// <summary>Своя или вставленная: меняется «Изменить», удаляется.</summary>
    public bool Personal => UserStrategies.IsPersonal(Source);
    /// <summary>Лежит в файле своих стратегий (найденная, своя, вставленная): удаляется, уходит на роутер.</summary>
    public bool Removable => Source is "search" or "mix" || Personal;
    /// <summary>Метка у названия; пусто — готовая.</summary>
    public string Label => Source switch { "search" => L.T("home.label.found"), "pasted" => L.T("home.label.pasted"), "user" => L.T("home.label.own"), _ => "" };
    public bool HasLabel => Label.Length > 0;
    public string EditText => L.T(Personal ? "home.edit" : "home.edit_copy");
    /// <summary>Подсказка к «Изменить»: «сохранится копия» верна только для готовых стратегий; у своей — без подсказки.</summary>
    public string? EditTip => Personal ? null : L.T("home.edit_copy.tip");
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
    /// Подпись — только последний прогон: «YouTube ✓ · Discord ✗ · свои 7 из 9 · проверено 18.10 в 14:30». История прошлых подборов
    /// о сегодняшней сети не говорит (провайдер меняет правила, сеть сбоит), она нужна только порядку перебора и подсказке
    /// <see cref="Regression"/>. Warning — «ломает обычные сайты», если в последнем прогоне. Старый формат (без групп) — «открыла всё» / «N из M групп».
    /// </summary>
    public static (string Stats, string Warning) Describe(StrategyStats.Stat st, GroupNames names)
    {
        var newest = st.Newest;
        var parts = new List<string>();
        bool broken;
        // Last — свежий прогон с адресами; старый помощник не отдаёт Newest — тогда Last и есть последний.
        if (st.Last is { } last && (newest is null || newest.File == last.File))
        {
            foreach (var g in last.Targets.Where(t => t.GroupId != "user" && !AdvancedSites.IsGroupId(t.GroupId) && !t.Control).GroupBy(t => t.GroupId))
                parts.Add($"{names.Title(g.Key)} {(g.Where(t => t.Required).All(t => t.Ok) && g.Any(t => t.Ok) ? "✓" : "✗")}");
            var mine = last.Targets.Where(t => t.GroupId == "user").ToList();
            if (mine.Count > 0) parts.Add(L.T("home.stats.mine", new { ok = mine.Count(t => t.Ok), total = mine.Count }));
            broken = newest?.ControlBroken ?? last.Targets.Any(t => t.Control && !t.Ok);
        }
        else if (newest is not null)
        {
            parts.Add(newest.ControlBroken || newest.Passed == newest.Total ? L.T("home.stats.all") : L.T("home.stats.some", new { ok = newest.Passed, total = newest.Total }));
            broken = newest.ControlBroken;
        }
        else return (L.T("home.strategy.not_checked_yet"), "");
        if (Checked(newest?.File ?? st.Last!.File) is { } at) parts.Add(L.T("home.stats.checked", new { date = L.DayMonth(at), time = L.Time(at) }));
        return (string.Join(" · ", parts), broken ? L.T("home.stats.breaks") : "");
    }

    /// <summary>Время подбора из имени файла результатов (yyyy-MM-dd_HH-mm-ss.json).</summary>
    static DateTime? Checked(string file) =>
        DateTime.TryParseExact(System.IO.Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd_HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var at) ? at : null;

    /// <summary>Сколько прогонов подряд должна была открывать всё стратегия, чтобы её провал сейчас стоил подсказки.</summary>
    public const int RegressionRuns = 3;

    /// <summary>
    /// Подсказка после подбора: стратегия, открывавшая всё в последних N (≥ <see cref="RegressionRuns"/>) прогонах, сейчас не прошла —
    /// скорее провайдер изменил правила или сеть сбоит. before — статистика до этого подбора; сначала текущая стратегия,
    /// затем с самой длинной серией. Старые записи (без групп) серии не дают — подсказки нет.
    /// </summary>
    public static string Regression(IReadOnlyDictionary<string, StrategyStats.Stat>? before, SelectionReport r, string? currentId, GroupNames names)
    {
        if (before is null || r.NothingBlocked) return "";
        var hit = r.Scores
            .Where(s => s.Error is null || s.BreaksRegular)
            .Select(s => (s, streak: before.GetValueOrDefault(s.Strategy.Id)?.Streak ?? 0))
            .Where(x => x.streak >= RegressionRuns && Failed(x.s, names) is { Length: > 0 })
            .OrderByDescending(x => x.s.Strategy.Id == currentId).ThenByDescending(x => x.streak)
            .FirstOrDefault();
        if (hit.s is null) return "";
        return L.Plural("select.regression", hit.streak, new { name = hit.s.Strategy.Title, failed = Failed(hit.s, names) });
    }

    /// <summary>«не открывает Discord» / «ломает обычные сайты» / «не открывает свои сайты»; пусто — прошла.</summary>
    static string Failed(StrategyScore s, GroupNames names)
    {
        if (s.Verdicts.Any(v => names.Control(v.GroupId) && !v.Ok)) return L.T("select.failed.breaks");
        var bad = s.Verdicts.Where(v => !names.Control(v.GroupId) && !AdvancedSites.IsGroupId(v.GroupId)).GroupBy(v => v.GroupId)
            .Where(g => !(g.Where(v => v.Target.Required).All(v => v.Ok) && g.Any(v => v.Ok)))
            .Select(g => g.Key == "user" ? L.T("select.group.user_lc") : names.Title(g.Key)).ToList();
        return bad.Count == 0 ? "" : L.T("select.failed.not_opening", new { groups = string.Join(", ", bad) });
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

/// <summary>
/// «Параметры» стратегии: роли как в каталоге (плейсхолдеры {fake}/… как есть) и итоговая командная строка winws для этого ПК
/// (с текущими режимами IP-списка и игр; пути — от папки программы).
/// </summary>
public static class StrategyParams
{
    /// <summary>Роли в порядке командной строки, затем незнакомые (новые роли каталога) — по имени.</summary>
    public static string Roles(IReadOnlyDictionary<string, string> roles) =>
        string.Join("\n", roles.OrderBy(r => Array.IndexOf(WinwsArgs.RoleOrder, r.Key) is var i and >= 0 ? i : int.MaxValue).ThenBy(r => r.Key, StringComparer.Ordinal)
            .Select(r => $"{r.Key}: {r.Value}"));

    public static string Full(Strategy s, AppPaths p, BuildOptions o)
    {
        string line;
        try { line = "winws.exe " + Short(WinwsArgs.Join(WinwsArgs.Build(s, p, o)), p.Root); }
        catch (ArgumentException e) { line = L.T("home.params.build_failed", new { reason = e.Message }); }
        return L.T("home.params.full", new
        {
            roles = Roles(s.Roles), ipset = Mode(o.Ipset), game = L.T(o.GameTcp || o.GameUdp ? "home.params.game.on" : "home.params.game.off"), root = p.Root, line,
        });
    }

    static string Mode(IpsetMode m) => L.T(m switch { IpsetMode.List => "home.params.ipset.list", IpsetMode.Any => "home.params.ipset.any", _ => "home.params.ipset.off" });

    /// <summary>Пути внутри папки программы — относительными: строка короче, а что где лежит, сказано в заголовке.</summary>
    static string Short(string line, string root) =>
        line.Replace(root.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar, "", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Названия групп целей для подробностей: из каталога целей и «Свои сайты»; контрольные («Обычные сайты») отмечены.</summary>
public sealed class GroupNames
{
    readonly Dictionary<string, (string Title, bool Control)> _map = new();

    public GroupNames(IEnumerable<TargetGroup> groups)
    {
        foreach (var g in groups) _map[g.Id] = (g.Title, g.Control);
    }

    public static readonly GroupNames Empty = new([]);

    /// <summary>Название на языке окна: «Свои сайты» и контрольная группа — по ключам (в каталоге целей контрольная названа по-русски), остальные — как в каталоге.</summary>
    public string Title(string id) => id == "user" ? L.T("select.group.user")
        : _map.TryGetValue(id, out var g) ? (g.Control ? L.T("select.group.control") : g.Title)
        : AdvancedSites.IsGroupId(id) && int.TryParse(id[4..], out var n) ? L.T("select.group.line", new { n = n + 1 }) : id;
    public bool Control(string id) => _map.TryGetValue(id, out var g) && g.Control;
}

/// <summary>
/// Строка подробностей по адресу — одна для «Проверить сейчас», таблиц подбора и списка стратегий: группа, адрес, ✓/✗,
/// исход и подробности по обеим версиям TLS (код, байты, цепочка редиректов), время. Alert — провал, который решает исход:
/// обязательный адрес или сломанный «обычный сайт».
/// </summary>
public sealed record CheckRow(bool Ok, string Group, string Url, string Detail, string Time = "", string Note = "", bool Alert = false, string Browser = "", bool Required = false)
{
    /// <summary>⚠ — программа открывает, браузер (DNS системы) — нет; причина — в <see cref="Browser"/>.</summary>
    public string Mark => !Ok ? "✗" : HasBrowser ? "⚠" : "✓";
    public bool HasNote => Note.Length > 0;
    public bool HasBrowser => Browser.Length > 0;

    public static CheckRow From(TargetVerdict v, Func<string, string> groupTitle, bool control = false)
    {
        var note = v.BrowserFails ? L.T(v.Browser!.Reason == BrowserPath.WindowsOnlyReason ? "select.check.note.windows" : "select.check.note.browser")
            : v.Ok ? "" : control ? L.T("select.check.note.breaks") : v.Target.Required ? L.T("select.check.note.required") : "";
        var seconds = v.Ok ? v.BestSeconds : v.Probes.Select(p => p.Seconds).DefaultIfEmpty(0).Max();
        var detail = Texts.ProbeDetail(v.Probes);
        // Адреса разошлись — оба пути с адресами: программа (DoH) и браузер (DNS системы, с hosts).
        if (v.Browser is { } b)
        {
            var args = new
            {
                doh = Ips(b.Doh), detail, source = L.T(b.FromHosts ? "select.check.src_hosts" : "select.check.src_dns"),
                system = b.System.Length > 0 ? Ips(b.System) : L.T("select.check.no_name"),
                result = b.Probes.Count > 0 ? Texts.ProbeDetail(b.Probes) : L.T("select.check.cant_open"),
                reason = b.Ok ? "" : Texts.BrowserReason(b.Reason),
            };
            detail = L.T(b.Ok ? "select.check.browser_detail" : "select.check.browser_detail_reason", args);
        }
        return new(v.Ok, groupTitle(v.GroupId), L.Relocalize(v.Target.Url), detail, L.T("select.check.time", new { sec = seconds.ToString("0.0", L.Culture) }),
            note, !v.Ok && (control || v.Target.Required),
            v.BrowserFails ? v.Browser!.Reason : "", Required: !v.Ok && !control && v.Target.Required);
    }

    static string Ips(IPAddress[] a) => string.Join(", ", a.Take(2)) + (a.Length > 2 ? " …" : "");

    public static CheckRow From(TargetVerdict v, GroupNames names) => From(v, names.Title, names.Control(v.GroupId));

    /// <summary>Подробности по адресам: сначала провалы, решающие исход, затем остальное в порядке проверки.</summary>
    public static IReadOnlyList<CheckRow> List(IEnumerable<TargetVerdict> verdicts, GroupNames names) =>
        verdicts.Select(v => From(v, names)).OrderByDescending(r => r.Alert).ToList();

    /// <summary>«не открылось: YouTube, Discord» — группы, где не открылся обязательный адрес (видно в строке таблицы, без раскрытия).</summary>
    public static string Missed(IEnumerable<CheckRow> rows)
    {
        var bad = rows.Where(r => r.Alert && r.Required).Select(r => r.Group).Distinct().ToList();
        return bad.Count == 0 ? "" : L.T("select.check.missed", new { groups = string.Join(", ", bad) });
    }
}

/// <summary>Строка таблицы подбора. Раскрывается — результаты по всем проверенным адресам.</summary>
public sealed class ScoreRow(StrategyScore s, GroupNames? names = null, Func<string, string>? titleOf = null) : Observable
{
    bool _best, _expanded;
    IReadOnlyList<CheckRow>? _details;
    public StrategyScore Score { get; } = s;
    public string Id => Score.Strategy.Id;
    public string Title => StrategyNaming.DisplayTitle(Score.Strategy.Title, Score.Strategy.Roles);
    /// <summary>
    /// Подсказка к названию: у смешанной — состав полными именами («YouTube — general (ALT); остальное — general (ALT10)»), иначе
    /// прежняя запись названия (с датой поиска), если показано имя по параметрам.
    /// </summary>
    public string Tip => titleOf is not null && MixBuilder.Composition(Score.Strategy.Parts, titleOf) is { Length: > 0 } c ? Title + "\n" + c
        : StrategyNaming.Tip(Score.Strategy.Title, Score.Strategy.Roles) is { Length: > 0 } t ? t : Title;
    public string Groups => Unchecked ? "—" : L.T("select.row.groups", new { ok = Score.GroupsPassed, total = Score.GroupsTotal });
    public string Share => Unchecked ? "—" : L.T("select.row.share", new { pct = (Score.TargetShare * 100).ToString("0", L.Culture) });
    public string Avg => Score.Verdicts.Any(v => v.Ok) ? L.T("select.row.avg", new { sec = Score.AvgSeconds.ToString("0.00", L.Culture) }) : "—";
    /// <summary>Ошибка запуска или «ломает обычные сайты»; без неё — где не открылись обязательные адреса.</summary>
    public string Error => Unchecked ? L.T("select.row.not_checked") : Score.Error is null ? CheckRow.Missed(Details) : Score.ErrorText is { Length: > 80 } e ? e[..80] + "…" : Score.ErrorText ?? "";
    /// <summary>Пустая колонка выглядит как сбой отрисовки: когда ошибок нет — «нет» серым (UX-аудит 5, п. 17).</summary>
    public string ErrorShown => Error.Length > 0 ? Error : L.T("select.row.none");
    public bool ErrorIsProblem => !Unchecked && Error.Length > 0;
    public string ErrorFull => Unchecked ? L.T("select.row.not_checked.tip") : Score.ErrorText ?? CheckRow.Missed(Details);
    public bool Usable => Score.Error is null && Score.GroupsPassed > 0;
    public bool IsBest { get => _best; set { if (Set(ref _best, value)) Raise(nameof(Mark)); } }
    public string Mark => IsBest ? "★" : "";
    /// <summary>Найдена глубоким поиском, а не из готового набора.</summary>
    public bool Found => Score.Strategy.Source == "search";
    /// <summary>Метка у названия: найдена, вставлена или своя; пусто — готовая.</summary>
    public string Label => Score.Strategy.Source switch { "search" => L.T("select.label.found"), "pasted" => L.T("select.label.pasted"), "user" => L.T("select.label.own"), _ => "" };
    public bool HasLabel => Label.Length > 0;
    public bool Personal => UserStrategies.IsPersonal(Score.Strategy.Source);
    public string EditText => L.T(Personal ? "select.edit" : "select.edit_copy");
    /// <summary>Вставлена или собрана руками и ещё не прошла проверку сайтов: «Применить» — только с подтверждением.</summary>
    public bool Unchecked => Score.Error is null && Score.Verdicts.Count == 0;

    public IReadOnlyList<CheckRow> Details => _details ??= CheckRow.List(Score.Verdicts, names ?? GroupNames.Empty);
    protected override void OnLanguageChanged() => _details = null;   // адреса собираются заново на языке окна
    public bool HasDetails => Score.Verdicts.Count > 0;
    public bool Expanded { get => _expanded; set => Set(ref _expanded, value); }
}

/// <summary>Строка сравнения DNS на вкладке «DNS».</summary>
public sealed record DnsRow(string Host, string System, string Doh, bool Spoofed)
{
    public string Mark => Spoofed ? L.T("dns.row.spoofed") : "";

    public static DnsRow From(DnsComparison c) => new(
        c.Host,
        c.System.Length > 0 ? Ips(c.System) : L.T("dns.row.no_name"),
        c.Doh.Length > 0 ? Ips(c.Doh) : L.T("dns.no_answer"),
        c.Spoofed);

    static string Ips(IPAddress[] a) => string.Join(", ", a.Take(2)) + (a.Length > 2 ? " …" : "");
}

/// <summary>Проверка окружения на «Диагностике»: ✓ — в порядке, ⚠ — мешает (что делать — в тексте).</summary>
public sealed record FindingRow(bool Ok, string Title, string Text, string? Go = null, SystemFinding? Source = null)
{
    /// <summary>Те же данные, тексты — на текущем языке (если находка знает, как собрать себя заново).</summary>
    public FindingRow Refreshed() => Source is null ? this : Source.Refreshed() is var f ? this with { Title = f.Title, Text = f.Text } : this;
    /// <summary>У находки есть ссылка «Настроить →» на вкладку «Прокси».</summary>
    public bool HasGo => Go == SystemFinding.GoProxy;
    public string Mark => Ok ? "✓" : "⚠";
}

public static class Texts
{
    public static string Svc(SvcState s) => s switch
    {
        SvcState.NotInstalled => L.T("diag.svc.not_installed"),
        SvcState.Stopped => L.T("diag.svc.stopped"),
        SvcState.StartPending => L.T("diag.svc.starting"),
        SvcState.StopPending => L.T("diag.svc.stopping"),
        SvcState.Running => L.T("diag.svc.running"),
        _ => L.T("diag.svc.unknown"),
    };

    public static string Outcome(ProbeOutcome o) => o switch
    {
        ProbeOutcome.Timeout => L.T("select.outcome.timeout"),
        ProbeOutcome.ConnectFailed => L.T("select.outcome.connect_failed"),
        ProbeOutcome.TlsFailed => L.T("select.outcome.tls_failed"),
        ProbeOutcome.Reset => L.T("select.outcome.reset"),
        ProbeOutcome.Truncated => L.T("select.outcome.truncated"),
        ProbeOutcome.BadStatus => L.T("select.outcome.bad_status"),
        ProbeOutcome.ForeignRedirect => L.T("select.outcome.foreign_redirect"),
        ProbeOutcome.Blocked => L.T("select.outcome.blocked"),
        ProbeOutcome.Ok => L.T("select.outcome.ok"),
        _ => L.T("select.outcome.error"),
    };

    /// <summary>Причина «браузер не откроет» (константы <see cref="BrowserPath"/> в ядре) → текст на языке окна; незнакомая — как есть.</summary>
    public static string BrowserReason(string reason) =>
        reason == BrowserPath.DnsReason ? L.T("home.browser.reason.dns")
        : reason == BrowserPath.HostsReason ? L.T("home.browser.reason.hosts")
        : reason == BrowserPath.WindowsOnlyReason ? L.T("home.browser.reason.windows_only")
        : reason;

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
        .Select(p => (p.Tls, Text: p.Ok ? DetailText(p.Detail) : p.Detail.Length > 0 ? $"{Outcome(p.Outcome)}, {DetailText(p.Detail)}" : Outcome(p.Outcome)))
        .GroupBy(x => x.Text)
        .Select(g => $"{TlsList(g.Select(x => Tls(x.Tls)).ToList())}: {g.Key}"));

    /// <summary>
    /// Подробность проверки (пишет ядро по-русски, хранится в истории): «301 → www.youtube.com/ → 200, 512 КБ» — по звеньям цепочки,
    /// фразы по шаблонам ключей, единицы («КБ», «байт») отдельно.
    /// </summary>
    public static string DetailText(string detail)
    {
        if (detail.Length == 0) return detail;
        return string.Join(" → ", detail.Split(" → ").Select(seg =>
        {
            var t = L.Relocalize(seg);
            if (t != seg) return t;
            t = System.Text.RegularExpressions.Regex.Replace(seg, @"(\d+) КБ$", m => L.T("core.probe.kb", new { n = m.Groups[1].Value }));
            return System.Text.RegularExpressions.Regex.Replace(t, @"(\d+) байт$", m => L.T("core.probe.bytes", new { n = m.Groups[1].Value }));
        }));
    }

    /// <summary>«TLS 1.3 и TLS 1.2» — версий не больше двух.</summary>
    static string TlsList(List<string> tls) => tls.Count == 2 ? L.T("select.probe.and", new { a = tls[0], b = tls[1] }) : string.Join(", ", tls);

    public static string Problem(TargetVerdict v) =>
        v.Probes.FirstOrDefault(p => !p.Ok) is { } p ? Outcome(p.Outcome) : L.T("select.outcome.not_opening");

    /// <summary>Исключение → текст для человека.</summary>
    public static string Friendly(Exception e) => e switch
    {
        System.Net.Http.HttpRequestException => L.T("shell.err.download_core"),
        SecurityException s => s.Message,
        ConflictException c => L.T("shell.err.conflict", new { names = string.Join(", ", c.Conflicts.Select(x => x.Name).Distinct()) }),
        UnauthorizedAccessException => L.T("shell.err.access", new { error = e.Message }),
        System.ComponentModel.Win32Exception w => L.T("shell.err.start_core", new { error = w.Message }),
        // Помощник старше окна не знает новой команды (у помощников до протокола 1.1 нет списка команд — окно не может
        // заранее выключить кнопку). Живой случай 2026-10-17: «Неизвестная команда «dns_set»» вместо «обновите помощника».
        DpiShell.Core.Helper.HelperException { Code: "invalid" } h when h.Message.StartsWith("Неизвестная команда", StringComparison.Ordinal)
            => L.T("shell.err.helper_old"),
        AggregateException { InnerException: { } inner } => Friendly(inner),
        _ => L.Relocalize(e.Message),
    };
}
