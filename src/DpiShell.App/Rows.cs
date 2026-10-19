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
    public bool ShowParams { get => _showParams; set { if (Set(ref _showParams, value)) Raise(nameof(ParamsLinkText)); } }
    public string ParamsLinkText => ShowParams ? "Скрыть параметры" : "Параметры";
    public string Id { get; } = id;
    public string Title { get; } = title;
    public string Stats { get; } = stats;
    public bool Found { get; } = found;
    /// <summary>Откуда стратегия: flowseal (готовая), search (найдена), user (своя), pasted (вставлена).</summary>
    public string Source { get; init; } = found ? "search" : "flowseal";
    /// <summary>Своя или вставленная: меняется «Изменить», удаляется.</summary>
    public bool Personal => UserStrategies.IsPersonal(Source);
    /// <summary>Лежит в файле своих стратегий (найденная, своя, вставленная): удаляется, уходит на роутер.</summary>
    public bool Removable => Source == "search" || Personal;
    /// <summary>Метка у названия; пусто — готовая.</summary>
    public string Label => Source switch { "search" => "найдена поиском", "pasted" => "вставлена", "user" => "своя", _ => "" };
    public bool HasLabel => Label.Length > 0;
    public string EditText => Personal ? "Изменить" : "Сделать копию и изменить";
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
            foreach (var g in last.Targets.Where(t => t.GroupId != "user" && !t.Control).GroupBy(t => t.GroupId))
                parts.Add($"{names.Title(g.Key)} {(g.Where(t => t.Required).All(t => t.Ok) && g.Any(t => t.Ok) ? "✓" : "✗")}");
            var mine = last.Targets.Where(t => t.GroupId == "user").ToList();
            if (mine.Count > 0) parts.Add($"свои {mine.Count(t => t.Ok)} из {mine.Count}");
            broken = newest?.ControlBroken ?? last.Targets.Any(t => t.Control && !t.Ok);
        }
        else if (newest is not null)
        {
            parts.Add(newest.ControlBroken || newest.Passed == newest.Total ? "открыла всё" : $"открыла {newest.Passed} из {newest.Total} групп");
            broken = newest.ControlBroken;
        }
        else return ("ещё не проверялась", "");
        if (Checked(newest?.File ?? st.Last!.File) is { } at) parts.Add($"проверено {at:dd.MM} в {at:HH:mm}");
        return (string.Join(" · ", parts), broken ? "ломает обычные сайты" : "");
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
            .Where(s => s.Error is null || s.Error.StartsWith("ломает обычные сайты", StringComparison.Ordinal))
            .Select(s => (s, streak: before.GetValueOrDefault(s.Strategy.Id)?.Streak ?? 0))
            .Where(x => x.streak >= RegressionRuns && Failed(x.s, names) is { Length: > 0 })
            .OrderByDescending(x => x.s.Strategy.Id == currentId).ThenByDescending(x => x.streak)
            .FirstOrDefault();
        if (hit.s is null) return "";
        return $"Стратегия «{hit.s.Strategy.Title}», работавшая последние {hit.streak} {Times(hit.streak)}, сейчас {Failed(hit.s, names)} — "
            + "похоже, провайдер изменил правила или сеть сбоит. Можно поискать новые.";
    }

    static string Times(int n) => n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? "раза" : "раз";

    /// <summary>«не открывает Discord» / «ломает обычные сайты» / «не открывает свои сайты»; пусто — прошла.</summary>
    static string Failed(StrategyScore s, GroupNames names)
    {
        if (s.Verdicts.Any(v => names.Control(v.GroupId) && !v.Ok)) return "ломает обычные сайты";
        var bad = s.Verdicts.Where(v => !names.Control(v.GroupId)).GroupBy(v => v.GroupId)
            .Where(g => !(g.Where(v => v.Target.Required).All(v => v.Ok) && g.Any(v => v.Ok)))
            .Select(g => g.Key == "user" ? "свои сайты" : names.Title(g.Key)).ToList();
        return bad.Count == 0 ? "" : "не открывает " + string.Join(", ", bad);
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
        catch (ArgumentException e) { line = "не собрать: " + e.Message; }
        return $"Параметры по ролям:\n{Roles(s.Roles)}\n\n"
            + $"Командная строка на этом ПК (IP-список: {Mode(o.Ipset)}, игровой режим: {(o.GameTcp || o.GameUdp ? "вкл" : "выкл")}; пути — от {p.Root}):\n{line}";
    }

    static string Mode(IpsetMode m) => m switch { IpsetMode.List => "только из списков", IpsetMode.Any => "все адреса", _ => "выкл" };

    /// <summary>Пути внутри папки программы — относительными: строка короче, а что где лежит, сказано в заголовке.</summary>
    static string Short(string line, string root) =>
        line.Replace(root.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar, "", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Названия групп целей для подробностей: из каталога целей и «Свои сайты»; контрольные («Обычные сайты») отмечены.</summary>
public sealed class GroupNames
{
    readonly Dictionary<string, (string Title, bool Control)> _map = new() { ["user"] = ("Свои сайты", false) };

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
public sealed record CheckRow(bool Ok, string Group, string Url, string Detail, string Time = "", string Note = "", bool Alert = false, string Browser = "", bool Required = false)
{
    /// <summary>⚠ — программа открывает, браузер (DNS системы) — нет; причина — в <see cref="Browser"/>.</summary>
    public string Mark => !Ok ? "✗" : HasBrowser ? "⚠" : "✓";
    public bool HasNote => Note.Length > 0;
    public bool HasBrowser => Browser.Length > 0;

    public static CheckRow From(TargetVerdict v, Func<string, string> groupTitle, bool control = false)
    {
        var note = v.BrowserFails ? (v.Browser!.Reason == BrowserPath.WindowsOnlyReason ? "✓ по защищённому DNS, ✗ по DNS Windows" : "✓ для программы, ✗ для браузера")
            : v.Ok ? "" : control ? "ломает обычный сайт" : v.Target.Required ? "важный" : "";
        var seconds = v.Ok ? v.BestSeconds : v.Probes.Select(p => p.Seconds).DefaultIfEmpty(0).Max();
        var detail = Texts.ProbeDetail(v.Probes);
        // Адреса разошлись — оба пути с адресами: программа (DoH) и браузер (DNS системы, с hosts).
        if (v.Browser is { } b)
            detail = $"Программа (защищённый DNS: {Ips(b.Doh)}): {detail}\nЧерез Windows ({(b.FromHosts ? "hosts" : "DNS Windows")}: {(b.System.Length > 0 ? Ips(b.System) : "имени нет")}): "
                + (b.Probes.Count > 0 ? Texts.ProbeDetail(b.Probes) : "не открыть") + (b.Ok ? "" : " — " + b.Reason);
        return new(v.Ok, groupTitle(v.GroupId), v.Target.Url, detail, $"{seconds:0.0} с", note, !v.Ok && (control || v.Target.Required),
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
    public string Groups => Unchecked ? "—" : $"{Score.GroupsPassed} из {Score.GroupsTotal}";
    public string Share => Unchecked ? "—" : $"{Score.TargetShare * 100:0} %";
    public string Avg => Score.Verdicts.Any(v => v.Ok) ? $"{Score.AvgSeconds:0.00} с" : "—";
    /// <summary>Ошибка запуска или «ломает обычные сайты»; без неё — где не открылись обязательные адреса.</summary>
    public string Error => Unchecked ? "не проверена" : Score.Error is null ? CheckRow.Missed(Details) : Score.Error.Length > 80 ? Score.Error[..80] + "…" : Score.Error;
    /// <summary>Пустая колонка выглядит как сбой отрисовки: когда ошибок нет — «нет» серым (UX-аудит 5, п. 17).</summary>
    public string ErrorShown => Error.Length > 0 ? Error : "нет";
    public bool ErrorIsProblem => !Unchecked && Error.Length > 0;
    public string ErrorFull => Unchecked ? "Стратегия ещё не проверена на ваших сайтах — нажмите «Проверить»." : Score.Error ?? CheckRow.Missed(Details);
    public bool Usable => Score.Error is null && Score.GroupsPassed > 0;
    public bool IsBest { get => _best; set { if (Set(ref _best, value)) Raise(nameof(Mark)); } }
    public string Mark => IsBest ? "★" : "";
    /// <summary>Найдена глубоким поиском, а не из готового набора.</summary>
    public bool Found => Score.Strategy.Source == "search";
    /// <summary>Метка у названия: найдена, вставлена или своя; пусто — готовая.</summary>
    public string Label => Score.Strategy.Source switch { "search" => "найдена", "pasted" => "вставлена", "user" => "своя", _ => "" };
    public bool HasLabel => Label.Length > 0;
    public bool Personal => UserStrategies.IsPersonal(Score.Strategy.Source);
    public string EditText => Personal ? "Изменить" : "Сделать копию и изменить";
    /// <summary>Вставлена или собрана руками и ещё не прошла проверку сайтов: «Применить» — только с подтверждением.</summary>
    public bool Unchecked => Score.Error is null && Score.Verdicts.Count == 0;

    public IReadOnlyList<CheckRow> Details => _details ??= CheckRow.List(Score.Verdicts, names ?? GroupNames.Empty);
    public bool HasDetails => Score.Verdicts.Count > 0;
    public bool Expanded { get => _expanded; set => Set(ref _expanded, value); }
}

/// <summary>Строка сравнения DNS на вкладке «DNS».</summary>
public sealed record DnsRow(string Host, string System, string Doh, bool Spoofed)
{
    public string Mark => Spoofed ? "подменён" : "";

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
        ConflictException c => $"Мешают похожие программы: {string.Join(", ", c.Conflicts.Select(x => x.Name).Distinct())}. Закройте их и повторите.",
        UnauthorizedAccessException => "Нет доступа к файлам программы. Возможно, их держит антивирус. " + e.Message,
        System.ComponentModel.Win32Exception w => "Не удалось запустить ядро zapret (антивирус?): " + w.Message,
        // Помощник старше окна не знает новой команды (у помощников до протокола 1.1 нет списка команд — окно не может
        // заранее выключить кнопку). Живой случай 2026-10-17: «Неизвестная команда «dns_set»» вместо «обновите помощника».
        DpiShell.Core.Helper.HelperException { Code: "invalid" } h when h.Message.StartsWith("Неизвестная команда", StringComparison.Ordinal)
            => "Помощник старее программы и этого не умеет — обновите его на вкладке «Информация и обновления» (один запрос прав администратора).",
        AggregateException { InnerException: { } inner } => Friendly(inner),
        _ => e.Message,
    };
}
