using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using DpiShell.Core;
using DpiShell.Core.Router;

namespace DpiShell.App;

/// <summary>Свой адрес роутера: адрес и отметка «важный» (договор §5.1 targets_get).</summary>
public sealed class RouterTargetRow(string url, bool important) : Observable
{
    string _url = url;
    bool _important = important;
    public string Url { get => _url; set => Set(ref _url, value); }
    public bool Important { get => _important; set => Set(ref _important, value); }
}

/// <summary>Шаг обновления DpiShell на роутере: Current — идёт сейчас (выделен), Done — пройден, Last — без стрелки после.</summary>
public sealed record UpdateStepRow(string Title, bool Current, bool Done, bool Last)
{
    public string Arrow => Last ? "" : "  →  ";
}

/// <summary>Стратегия роутера в меню выбора. Params — роли, если роутер их отдаёт (strategies.items[].roles), иначе пусто и не показываются.</summary>
public sealed record RouterStrategyRow(string Id, string Title, string Stats, string Params = "")
{
    public bool HasParams => Params.Length > 0;
}

/// <summary>Строка таблицы подбора на роутере (select_progress.rows).</summary>
public sealed class RouterScoreRow(RouterSelectRow row, string title, GroupNames? names = null, int groupsTotal = 0) : Observable
{
    bool _best, _expanded;
    IReadOnlyList<CheckRow>? _details;
    public RouterSelectRow Row { get; } = row;
    public string Id => Row.Id;
    /// <summary>Роли стратегии (если известны): автоимя показывается по ним.</summary>
    public IReadOnlyDictionary<string, string>? Roles { get; init; }
    /// <summary>Название на языке окна: записанное роутером автоимя переводится при показе.</summary>
    public string Title => StrategyNaming.DisplayTitle(title, Roles);
    /// <summary>Вариант глубокого поиска (candidate): в списке стратегий роутера его нет — только посмотреть, применить нельзя.</summary>
    public bool IsCandidate => Row.Candidate;
    public string Kind => IsCandidate ? L.T("router.row.candidate") : "";
    /// <summary>Как на ПК: «N из M» (M — отмеченных групп); число групп роутер не присылает — берём по адресам в разбивке, нет её — доля.</summary>
    public string Groups => Row is { GroupsOk: { } gok, GroupsTotal: > 0 and var gt } ? L.T("router.row.groups_of", new { ok = gok, total = gt })   // роутер 0.2.0-r8 присылает сам
        : IsCandidate ? "—"
        : groupsTotal > 0 ? L.T("router.row.groups_of", new { ok = (int)Math.Round(Row.GroupShare * groupsTotal), total = groupsTotal }) : Percent(Row.GroupShare * 100);
    static string Percent(double v) => L.T("router.row.percent", new { value = v.ToString("0", L.Culture) });
    static string Seconds(double v, string format) => L.T("router.row.seconds", new { value = v.ToString(format, L.Culture) });
    /// <summary>У варианта поиска доля обязательных адресов его части (ok из total) — тоже процентом, как у остальных строк.</summary>
    public string Share => IsCandidate && Row.Total is > 0 ? Percent((double)(Row.Ok ?? 0) / Row.Total.Value * 100) : Percent(Row.TargetShare * 100);
    public string Avg => Row.Error is null && Row.AvgS > 0 ? Seconds(Row.AvgS, "0.00") : "—";
    public string Error => RouterTexts.SelectError(Row.Error);
    /// <summary>Пустая колонка выглядит как сбой отрисовки: когда ошибок нет — «нет» серым.</summary>
    public string ErrorShown => Error.Length > 0 ? Error : L.T("router.row.none");
    public bool ErrorIsProblem => Error.Length > 0;
    public bool Usable => !IsCandidate && Row.Error is null && Row.GroupShare > 0;
    public bool IsBest { get => _best; set { if (Set(ref _best, value)) Raise(nameof(Mark)); } }
    public string Mark => IsBest ? "★" : "";

    /// <summary>Результаты по адресам — если роутер их отдаёт (verdicts); старый роутер присылает только доли.</summary>
    protected override void OnLanguageChanged() => _details = null;
    public IReadOnlyList<CheckRow> Details => _details ??= (Row.Verdicts ?? []).Select(v =>
    {
        var n = names ?? GroupNames.Empty;
        bool control = n.Control(v.Group);
        return new CheckRow(v.Ok, n.Title(v.Group), v.Url, v.Shown ?? "", v.Seconds is { } sec ? Seconds(sec, "0.0") : "",
            !v.Ok && control ? L.T("router.row.breaks_site") : "", !v.Ok && control);
    }).OrderByDescending(r => r.Alert).ToList();
    public bool HasDetails => Row.Verdicts is { Count: > 0 };
    /// <summary>Подсказка про обновление — только у полных проверок: у кандидатов поиска роутер разбивку не присылает.</summary>
    public bool NoDetails => !HasDetails && !IsCandidate;
    /// <summary>Стрелка: есть что раскрыть — разбивка или подсказка.</summary>
    public bool CanExpand => HasDetails || NoDetails;
    public string NoDetailsHint => L.T("router.row.no_details");
    public bool Expanded { get => _expanded; set => Set(ref _expanded, value); }

    /// <summary>То же содержимое строки (без сравнения списков по ссылке): ход опрашивается часто, раскрытую строку не пересоздаём.</summary>
    public bool Same(RouterSelectRow r) => Row with { Verdicts = null, Roles = null } == r with { Verdicts = null, Roles = null } && (Row.Verdicts?.Count ?? -1) == (r.Verdicts?.Count ?? -1);
}

/// <summary>Чужой обход на роутере (diag.conflicts). Пакет (kind: package) не остановить — только удалить.</summary>
public sealed record RouterConflictRow(string Name, string Detail, bool CanStop = true);

/// <summary>Предупреждение роутера на главной; Fix — подпись кнопки, если роутер умеет исправить сам.</summary>
public sealed record RouterWarningRow(string Code, string Text, string? Fix = null)
{
    public bool HasFix => Fix is not null;
}

public static class RouterTexts
{
    /// <summary>Ошибка роутера → строка состояния. Тексты уже в исключении; пароля в них нет (RouterClient не кладёт запрос в текст).</summary>
    public static string Friendly(RouterException e) => e.AppError?.Code switch
    {
        "busy" => L.T("router.busy", new { reason = e.AppError.Text is { Length: > 0 } t ? char.ToLower(t[0]) + t[1..] : L.T("router.busy.default") }),
        // §9: enable/select_start при чужом обходе — conflict.
        "conflict" => Conflict,
        _ => e.Message,
    };

    public static string Conflict => L.T("router.conflict");

    /// <summary>
    /// Причина отказа в строке подбора роутера (select_progress.rows[].error): известные формы роутера — «ломает обычные сайты: …»,
    /// «ошибка запуска: …» — на языке окна, остальное как есть.
    /// </summary>
    public static string SelectError(string? error)
    {
        if (string.IsNullOrEmpty(error)) return "";
        const string breaks = "ломает обычные сайты: ", failed = "ошибка запуска: ";
        if (error.StartsWith(breaks, StringComparison.Ordinal)) return L.T("core.score.breaks", new { sites = error[breaks.Length..] });
        if (error.StartsWith(failed, StringComparison.Ordinal)) return L.T("router.row.start_failed", new { error = error[failed.Length..] });
        return error;
    }

    /// <summary>Выключение без человека (§5.1): сторож, кнопка на корпусе, откат неподтверждённого включения.</summary>
    public static bool IsOffCode(string code) => code is "watchdog_off" or "button_off" or "confirm_reverted" or "watchdog_select_failed";

    /// <summary>
    /// Фраза о выключении без человека: by — watchdog | button | revert (last_off.by или код предупреждения), at — когда.
    /// enabled — обход сейчас включён (откат вернул прежнюю стратегию, а не выключил).
    /// </summary>
    public static string Off(string by, DateTime? at, bool enabled = false)
    {
        string Pick(string key, string keyAt) => at is null ? L.T(key) : L.T(keyAt, new { time = Time(at) });
        return by switch
        {
            // Слово «сторож» — один раз и с пояснением (UX-аудит 7, п. 9); дальше — «выключили автоматически».
            "watchdog" or "watchdog_off" => Pick("router.off.watchdog", "router.off.watchdog_at"),
            "watchdog_select_failed" => RouterViewModel.WatchdogSelectText(RouterViewModel.WatchdogSelectState.Failed, null),
            "button" or "button_off" => Pick("router.off.button", "router.off.button_at"),
            _ when enabled => Pick("router.off.revert_strategy", "router.off.revert_strategy_at"),
            _ => Pick("router.off.revert_enable", "router.off.revert_enable_at"),
        };
    }

    /// <summary>Сколько осталось до отката: «2:45».</summary>
    public static string Countdown(TimeSpan left)
    {
        var s = Math.Max(0, (int)Math.Ceiling(left.TotalSeconds));
        return $"{s / 60}:{s % 60:D2}";
    }

    /// <summary>Предупреждение status.warnings → строка главной. flow_offload и ipv6 роутер исправляет сам (§9).</summary>
    public static RouterWarningRow Warning(RouterWarning w, IReadOnlyList<RouterConflictRow>? conflicts = null) => w.Code switch
    {
        "flow_offload" or "ipv6" => new(w.Code, WarningText(w, conflicts), L.T("router.warn.fix")),
        "conflict" => new(w.Code, WarningText(w, conflicts), L.T("common.more")),
        _ => new(w.Code, WarningText(w, conflicts)),
    };

    /// <summary>
    /// Текст предупреждения роутера на языке окна: по ключу, если роутер прислал его и он известен; иначе по коду (таблица ниже);
    /// неизвестный код — текст роутера как есть.
    /// </summary>
    public static string WarningText(RouterWarning w, IReadOnlyList<RouterConflictRow>? conflicts = null)
    {
        if (RouterPhrase.ByKey(w.Key, w.Params) is { } byKey) return byKey;
        // Старый роутер ключа не присылает: имя стратегии и ошибку берём из его русской фразы по форме (r8: «…включена запасная X», «Автоподбор включил X»).
        string? name = null, error = null;
        if (w.Params is not null && w.Params.TryGetValue("name", out var pn) && pn.ValueKind == System.Text.Json.JsonValueKind.String) name = pn.GetString();
        else if (w.Text is { Length: > 0 } text && OldNameRx.Match(text) is { Success: true } nm) name = nm.Groups[1].Value;
        if (w.Text is { Length: > 0 } t2 && t2.StartsWith("Подбор прервался: ", StringComparison.Ordinal)) error = t2["Подбор прервался: ".Length..];
        if (name is not null) name = StrategyNaming.DisplayTitle(name, null);
        var at = WarningTime(w) is { } wt ? Time(wt) : null;
        // Без имени (форма не узнана) фразу с именем не собрать — остаётся текст роутера.
        if (name is null && w.Code is "auto_selected" or "switched_backup") return w.Text ?? w.Code;
        return w.Code switch
        {
            "core_missing" => L.T("web.core.missing"),
            "conflict" => conflicts is { Count: > 0 } ? L.T("web.warn.conflict", new { names = string.Join(", ", conflicts.Select(c => c.Name)) }) : L.T("router.warn.conflict"),
            "flow_offload" => L.T("router.warn.flow_offload"),
            "ipv6" => L.T("router.warn.ipv6"),
            "watchdog_off" => at is null ? L.T("router.off.watchdog") : L.T("router.off.watchdog_at", new { time = at }),
            "button_off" => at is null ? L.T("router.off.button") : L.T("router.off.button_at", new { time = at }),
            "confirm_reverted" => at is null ? L.T("router.off.revert_enable") : L.T("router.off.revert_enable_at", new { time = at }),
            "auto_selected" => w.Text?.StartsWith("Сторож", StringComparison.Ordinal) == true ? L.T("router.wd.select_found", new { name }) : L.T("web.warn.auto_selected", new { name }),
            "watchdog_select_failed" => L.T("router.wd.select_failed"),
            "auto_select_failed" => L.T("web.warn.auto_select_failed"),
            "switched_backup" => L.T("web.warn.switched_backup", new { name }),
            "dns_fallback" => L.T("web.warn.dns_fallback"),
            "dns_spoofed" => L.T("web.warn.dns_spoofed"),
            "select_failed" => L.T("web.warn.select_failed", new { error = error ?? w.Text ?? "" }),
            _ => w.Text ?? w.Code,
        };
    }

    static readonly System.Text.RegularExpressions.Regex OldNameRx = new(@"(?:включена запасная|^Автоподбор включил|подобрал и включил) (.+?)\.?$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Когда случилось, если новый роутер прислал время в params (unix-секунды).</summary>
    public static DateTime? WarningTime(RouterWarning w) => RouterPhrase.Time(w.Params);


    /// <summary>Хост из адреса для списка исключений: «https://www.example.com/a» → «www.example.com».</summary>
    public static string? Host(string url)
    {
        var s = url.Trim();
        if (s.Length == 0) return null;
        if (!s.Contains("://")) s = "https://" + s;
        return Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Host.Contains('.') ? u.Host.ToLowerInvariant() : null;
    }

    /// <summary>Время роутера (UTC) → «ЧЧ:ММ» по часам этого ПК.</summary>
    public static string Time(DateTime? at) => at is { } t
        ? L.Time((t.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : t).ToLocalTime())
        : "";

    public static string NotFoundText => L.T("router.not_found");

    public static string Found(RouterFound f, string? version) => f switch
    {
        RouterFound.NotFound => NotFoundText,
        RouterFound.AppTooOld => L.T("router.found.app_too_old"),
        RouterFound.RouterTooOld => L.T("router.found.router_too_old"),
        _ => L.T("router.found.password", new { version }),
    };

    /// <summary>Номер элемента из error.field (§4: «items[3].url» → 3).</summary>
    public static int? Index(string? field) =>
        field is not null && System.Text.RegularExpressions.Regex.Match(field, @"\[(\d+)\]") is { Success: true } m ? int.Parse(m.Groups[1].Value) : null;

    /// <summary>Адрес из поля «Свои сайты» → URL для роутера: домен — https://домен/.</summary>
    public static string ToUrl(string line)
    {
        var s = line.Trim();
        return s.Contains("://") ? s : "https://" + s.TrimEnd('/') + "/";
    }
}

/// <summary>
/// Обход на роутере (docs/dev/router-api.md §6): поиск, вход, статус для главной, подбор, свои сайты, диагностика.
/// Все запросы к роутеру — по одному (семафор), асинхронно; действия пользователя — по одному (Busy).
/// Пароль: из поля — только на время входа, на диске — только зашифрованным и только после успешного входа.
/// </summary>
public sealed partial class RouterViewModel : Observable, IDisposable
{
    readonly Shell _shell;
    readonly WindowSettings _settings;
    readonly SemaphoreSlim _gate = new(1, 1);
    RouterClient? _client;
    // Название и роли (данные): автоимя показывается по ролям и на языке окна при каждом чтении.
    readonly Dictionary<string, (string Title, IReadOnlyDictionary<string, string>? Roles)> _titles = [];
    readonly Dictionary<string, Dictionary<string, string>> _roles = [];

    public RouterViewModel(Shell shell, WindowSettings settings)
    {
        _shell = shell;
        _settings = settings;
        settings.StrategySortChanged += () => { Raise(nameof(SortByName)); Raise(nameof(SortByResult)); ResortScores(); };
        _auto = string.IsNullOrWhiteSpace(settings.RouterAddress);
        _custom = settings.RouterAddress ?? "";

        LoginCmd = new RelayCommand(() => LoginRequested?.Invoke());
        InstallCmd = new RelayCommand(() => InstallRequested?.Invoke(), () => !Busy);
        FindCmd = RelayCommand.Async(() => ConnectAsync(null), () => CanConnect);
        ToggleCmd = RelayCommand.Async(ToggleAsync, () => Can(RouterEnabled ? "disable" : "enable"));
        CheckCmd = RelayCommand.Async(CheckAsync, () => Can("check_now"));
        GoSelectCmd = new RelayCommand(() => { SelectOnRouter = true; Navigate(Pages.Select); }, () => Connected && !Blocked);
        StartSelectCmd = RelayCommand.Async(() => StartSelectAsync(false), () => Can("select_start") && !Selecting);
        StartDeepCmd = RelayCommand.Async(() => StartSelectAsync(true), () => Can("select_start") && !Selecting);
        StopSelectCmd = RelayCommand.Async(CancelSelectAsync, () => Connected && Selecting && !Busy);
        ApplyCmd = RelayCommand.Async(() => ApplyAsync(_best!), () => Can("apply") && HasBest);
        ApplySelectedCmd = RelayCommand.Async(() => ApplyAsync(SelectedScore!.Id),
            () => Can("apply") && !Selecting && SelectedScore is { Usable: true } s && s.Id != CurrentStrategyId);
        SendStrategyCmd = new RelayCommand(p => { if (p is string id) _ = SendStrategyAsync(id); }, _ => Can("strategies_user_set"));
        AddTargetCmd = new RelayCommand(AddTarget, () => Connected);
        RemoveTargetCmd = new RelayCommand(p => { if (p is RouterTargetRow r) Targets.Remove(r); });
        CopyFromPcCmd = new RelayCommand(CopyFromPc, () => Connected);
        SaveTargetsCmd = RelayCommand.Async(SaveTargetsAsync, () => Can("targets_set"));
        RefreshDiagCmd = RelayCommand.Async(RefreshDiagAsync, () => Can("diag"));
        StopConflictCmd = new RelayCommand(p => { if (p is string name) _ = StopConflictAsync(name); }, _ => Can("conflict_stop"));
        SaveReportCmd = RelayCommand.Async(SaveReportAsync, () => Can("report"));
        SiteCheckCmd = RelayCommand.Async(SiteCheckAsync, () => Can("site_check"));
        AddExcludeCmd = RelayCommand.Async(AddSiteToExcludeAsync, () => Can("lists_set") && SiteSuggestExclude);
        DnsCheckCmd = RelayCommand.Async(DnsAnalyzeAsync, () => Can("dns_analyze"));
        DnsEnableCmd = RelayCommand.Async(DnsEnableAsync, () => Can("dns_enable") && !DnsProtected);
        DnsRestoreCmd = RelayCommand.Async(DnsRestoreAsync, () => Can("dns_restore") && DnsProtected);
        DnsPick = new DnsPicker(settings) { Apply = DnsApplyAsync, Restore = DnsRestoreAsync, Enabled = () => Can("dns_set") };
        FixWarningCmd = new RelayCommand(p => { if (p is string code) _ = FixWarningAsync(code); }, p => Can(p as string == "ipv6" ? "ipv6_set" : "flow_offload_fix"));
        StopPcCmd = RelayCommand.Async(StopPcForRouterAsync, () => !Busy);
        StopRouterCmd = RelayCommand.Async(async () => { if (await DisableAsync()) Say(L.T("router.stop.done")); }, () => Can("disable"));
        EnableTimestampsCmd = RelayCommand.Async(() => CheckTimestampsAsync(ask: true), () => !Busy);
        InitAdvanced();   // продвинутый режим на роутере — RouterViewModel.Advanced.cs
        WatchSites();
    }

    // ---------- связь с окном (в тестах подменяются) ----------

    /// <summary>Строка состояния окна.</summary>
    public Action<string> Say { get; set; } = _ => { };
    /// <summary>Ошибка действия: окно показывает её тостом, а не только в строке состояния (UX-аудит 2, п. 16). По умолчанию — как <see cref="Say"/>.</summary>
    public Action<string>? Fail { get; set; }
    internal void Failed(string text) { _lastFailure = text; (Fail ?? Say)(text); }
    /// <summary>Стратегии, найденные глубоким поиском на ПК (их хранит помощник).</summary>
    public Func<IReadOnlyList<Strategy>> FoundStrategies { get; set; } = () => [];
    /// <summary>Свои сайты этого ПК (сохранены у помощника).</summary>
    public Func<IReadOnlyList<string>> PcUserTargets { get; set; } = () => [];
    public Func<string, bool> Confirm { get; set; } = text =>
    {
        UiGuard.Check(text);
        return MessageBox.Show(text, "DpiShell", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    };
    public Func<string?> AskReportPath { get; set; } = () =>
    {
        var d = new Microsoft.Win32.SaveFileDialog { FileName = $"dpishell-router-{DateTime.Now:yyyy-MM-dd_HH-mm}.txt", Filter = L.T("router.save.filter") };
        return d.ShowDialog() == true ? d.FileName : null;
    };
    public Action<int> Navigate { get; set; } = _ => { };
    /// <summary>Что подбирать (группы и режим — с экрана «Подбор»); null — нечего, причина уже сказана.</summary>
    public Func<(IReadOnlyList<string> Groups, bool Full)?> SelectOptions { get; set; } = () => (DefaultSelectGroups, true);
    /// <summary>Группы подбора по умолчанию — как у самого роутера: YouTube и Discord.</summary>
    public static readonly IReadOnlyList<string> DefaultSelectGroups = ["youtube", "discord"];
    /// <summary>Отметить группы подбора на экране «Подбор» (выбор общий с ПК, держит окно).</summary>
    public Action<IReadOnlyList<string>> SetSelectGroups { get; set; } = _ => { };
    public Func<string?> Gateway { get; set; } = RouterClient.DefaultGateway;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan CheckTimeout { get; set; } = TimeSpan.FromMinutes(3);
    /// <summary>Установка пакета защищённого DNS на роутере — 10–20 с (§5.1), с запасом.</summary>
    public TimeSpan DnsTimeout { get; set; } = TimeSpan.FromMinutes(2);

    // Этот ПК (router-api §6): обходы ПК и роутера вместе не работают.
    /// <summary>Обход на этом ПК включён.</summary>
    public Func<bool> PcRunning { get; set; } = () => false;
    /// <summary>Выключить обход на этом ПК (через помощника); true — выключен.</summary>
    public Func<Task<bool>> StopPc { get; set; } = () => Task.FromResult(false);
    /// <summary>Метки TCP на этом ПК: null — не удалось узнать. Чтение без прав.</summary>
    public Func<bool?> PcTimestamps { get; set; } = TcpTimestamps.IsEnabled;
    /// <summary>Включить метки TCP на этом ПК через помощника; ask — можно предложить его установку (по кнопке), иначе только если он уже есть.</summary>
    public Func<bool, Task<bool>> EnablePcTimestamps { get; set; } = _ => Task.FromResult(false);

    /// <summary>Поменялось то, что видно на главной (найден ли роутер, включён ли на нём обход).</summary>
    public event Action? Changed;
    /// <summary>«Войти» — окно показывает на месте небольшой диалог входа (UX-аудит п. 14).</summary>
    public event Action? LoginRequested;

    // ---------- адрес и вход ----------

    bool _auto;
    public bool AddressAuto { get => _auto; set { if (Set(ref _auto, value)) Raise(nameof(AddressCustom)); } }
    public bool AddressCustom { get => !_auto; set => AddressAuto = !value; }

    string _custom;
    public string CustomAddress { get => _custom; set => Set(ref _custom, value); }

    string? _gateway;
    public string AutoAddressText => _gateway is { } g ? L.T("router.address.auto_gw", new { gateway = g }) : L.T("router.address.auto");

    string? EffectiveAddress()
    {
        if (!AddressAuto) return CustomAddress.Trim() is { Length: > 0 } a ? a : null;
        _gateway = Gateway();
        Raise(nameof(AutoAddressText));
        return _gateway;
    }

    RouterDetection? _detection;
    public RouterDetection? Detection => _detection;

    /// <summary>DpiShell на роутере найден (любой версии) — на главной есть блок роутера.</summary>
    public bool Found => Enabled && _detection is { Found: not RouterFound.NotFound };

    /// <summary>«Показывать роутер» (Настройки): снята — окно ведёт себя как при ненайденном роутере, поиск и обращения к нему не идут. Единственная точка.</summary>
    public bool Enabled => _settings.ShowRouter;

    /// <summary>Галочку переключили: снята — забыть найденное и закрыть связь; поставлена — искать заново.</summary>
    public void ShowRouterChanged()
    {
        if (!Enabled)
        {
            _client?.Dispose();
            _client = null;
            SetDetection(null);
            SetConnection(() => RouterTexts.NotFoundText, notFound: true);
        }
        else _ = RefreshAsync();
        Raise(nameof(Enabled)); Raise(nameof(Found));
    }

    bool _searched, _autoFindTried;
    /// <summary>Роутер уже искали (хоть раз): до того пустое состояние — «не проверялся», а не «не найден» (UX-аудит 2, п. 10).</summary>
    public bool Searched { get => _searched; private set { if (Set(ref _searched, value)) { Raise(nameof(EmptyTitle)); Raise(nameof(ShowConnectionText)); } } }
    // ---------- «Проверить, подходит ли мой роутер» (UX-аудит 7, п. 10) ----------

    /// <summary>Открыть страницу роутера по адресу; в тестах — подмена без сети.</summary>
    public Func<string, Task<RouterFitResult>> FitProbe { get; set; } = address => RouterFit.ProbeAsync(address);

    string _fitText = "";
    /// <summary>Ответ на «Проверить, подходит ли мой роутер»: да / нет / не знаю — простыми словами.</summary>
    public string FitText { get => _fitText; private set { if (Set(ref _fitText, value)) Raise(nameof(HasFit)); } }
    public bool HasFit => _fitText.Length > 0;

    bool _fitChecking;
    public bool FitChecking { get => _fitChecking; private set { if (Set(ref _fitChecking, value)) Requery(); } }

    public ICommand FitCmd => _fitCmd ??= RelayCommand.Async(CheckFitAsync, () => !FitChecking && !Busy);
    ICommand? _fitCmd;

    /// <summary>Программа сама находит роутер (основной шлюз) и говорит «похоже на OpenWrt — можно ставить» / «не OpenWrt» / «не ответил».</summary>
    public async Task CheckFitAsync()
    {
        FitChecking = true;
        FitText = L.T("router.fit.searching");
        try
        {
            string? gw;
            try { gw = Gateway(); }
            catch (Exception e) when (e is InvalidOperationException or System.Net.NetworkInformation.NetworkInformationException) { gw = null; }
            if (gw is null) { FitText = L.T("router.fit.no_network"); return; }
            FitText = await FitProbe(gw) switch
            {
                RouterFitResult.Yes => L.T("router.fit.yes", new { address = gw }),
                RouterFitResult.No => L.T("router.fit.no", new { address = gw }),
                _ => L.T("router.fit.unknown", new { address = gw }),
            };
        }
        finally { FitChecking = false; }
    }

    public string EmptyTitle => Removed ? L.T("router.empty.removed") : Searched ? L.T("router.empty.not_found") : _autoFindTried ? L.T("router.fit.searching") : L.T("router.empty.not_checked");

    /// <summary>Инструкция «Как поставить DpiShell на роутер» (п. 9).</summary>
    public const string GuideUrl = "https://github.com/Slavdew/dpishell/blob/master/docs/router/README.md";
    public Action<string> OpenUrl { get; set; } = url => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    public ICommand GuideCmd => _guideCmd ??= new RelayCommand(() =>
    {
        try { OpenUrl(GuideUrl); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Failed(L.T("router.err.browser", new { error = e.Message })); }
    });
    ICommand? _guideCmd;

    ICommand? _installCmd;
    /// <summary>
    /// «Установить на роутер» (установка по SSH, router-api §5.4) — место в пустом состоянии готово; кнопка видна, когда окно задаст команду.
    /// </summary>
    public ICommand? InstallCmd { get => _installCmd; set { if (Set(ref _installCmd, value)) { Raise(nameof(CanInstall)); RaiseAbout(); } } }
    public bool CanInstall => InstallCmd is not null;

    bool _connected;
    /// <summary>Вошли и API совместим: можно управлять.</summary>
    public bool Connected
    {
        get => _connected;
        private set
        {
            if (!Set(ref _connected, value)) return;
            Raise(nameof(NeedLogin)); Raise(nameof(CanAct)); Raise(nameof(ScheduleEditable)); RaiseCompat(); Raise(nameof(SelectOnRouterVisible)); Raise(nameof(SelectOnPcVisible));
            Raise(nameof(TargetsOnRouterVisible)); Raise(nameof(TargetsOnPcVisible)); Raise(nameof(JointAvailable)); Raise(nameof(JointCardVisible));
            if (!value) { Selecting = false; DiagLoaded = false; DnsPresetsAvailable = false; _security = null; RaiseSecurity(); }
            Raise(nameof(DiagHint));
            Requery();
        }
    }

    /// <summary>Роутер найден, но управлять нельзя: нет входа (или версия не та — тогда кнопка «Войти» не поможет).</summary>
    public bool NeedLogin => Found && !Connected;
    public bool CanAct => Connected && !Busy && !Blocked;
    public bool CanConnect => !Busy;

    // Состояние подключения хранится правилом сборки (данные в замыкании): «Подключено, DpiShell 0.2.0» читается на языке окна.
    readonly LiveText _connectionText = new(() => L.T("router.conn.not_checked"));
    public string ConnectionText { get => _connectionText.Value; private set { _connectionText.Set(value); Raise(); Raise(nameof(ShowConnectionText)); } }

    /// <summary>«DpiShell на роутере не найден» в «Подключении» не повторяем: то же уже сказано в карточке выше (UX-аудит 6, п. 14).</summary>
    public bool ShowConnectionText => Searched && (Found || !_connNotFound);

    // Вид состояния подключения — флагами, а не сравнением текста (текст зависит от языка).
    bool _connNotFound, _connNeedsPassword;
    /// <summary>Подключение не прошло из-за пароля: у роутера он есть, а нужный не введён или неверен.</summary>
    internal bool ConnectionNeedsPassword => _connNeedsPassword;

    bool _connectionOk;
    public bool ConnectionOk { get => _connectionOk; private set => Set(ref _connectionOk, value); }

    void SetConnection(string text, bool ok = false, bool notFound = false, bool needsPassword = false) => SetConnection(() => text, ok, notFound, needsPassword);

    void SetConnection(Func<string> text, bool ok = false, bool notFound = false, bool needsPassword = false)
    {
        _connNotFound = notFound; _connNeedsPassword = needsPassword;
        _connectionText.Set(text); Raise(nameof(ConnectionText)); Raise(nameof(ShowConnectionText));
        ConnectionOk = ok; Raise(nameof(DiagHint)); UpdateHome();
    }

    void SetFound(RouterFound f, string? version) => SetConnection(() => RouterTexts.Found(f, version), notFound: f == RouterFound.NotFound, needsPassword: f == RouterFound.Ok);

    RouterClient? ClientFor(string address)
    {
        if (_client is not null && string.Equals(_client.Address, address.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) return _client;
        _client?.Dispose();
        _client = null;
        Connected = false;
        try { _client = new RouterClient(address, pins: _settings.HostKeys, httpsPort: RouterHttpsPort); }
        catch (ArgumentException e) { SetConnection(e.Message); }
        return _client;
    }

    async Task<T> Call<T>(Func<RouterClient, Task<T>> f)
    {
        await _gate.WaitAsync();
        try { return await f(_client ?? throw new RouterException(RouterFailure.Unreachable, L.T("router.err.no_router"))); }
        finally { _gate.Release(); }
    }

    Task Call(Func<RouterClient, Task> f) => Call(async c => { await f(c); return true; });

    /// <summary>
    /// «Подключиться»: адрес из настроек, поиск, вход. Пусто в поле пароля — сохранённый; его нет — только поиск (status.json).
    /// <paramref name="noPassword"/> — «Пароль не задан» (у свежей OpenWrt пароль root пустой, UX-аудит 2, п. 11): вход с пустым паролем.
    /// </summary>
    public async Task<bool> ConnectAsync(string? password, bool noPassword = false)
    {
        if (Busy) return false;
        Busy = true;
        Say(L.T("router.fit.searching"));
        try
        {
            _settings.RouterAddress = AddressAuto ? null : CustomAddress.Trim();
            try { _settings.Save(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Failed(Texts.Friendly(e)); }
            if (!await DetectAsync(interactive: true)) return false;
            var typed = noPassword ? "" : string.IsNullOrEmpty(password) ? null : password;
            if (typed is null && !HasSavedPassword)
            {
                SetFound(_detection!.Found, _detection.Status?.Version);
                return false;
            }
            return await LoginAsync(typed);
        }
        finally
        {
            Busy = false;
            Say(ConnectionText);
        }
    }

    /// <summary>Есть сохранённый пароль (и пустой — «пароль не задан»): пустое поле в окне входа значит «взять его».</summary>
    public bool HasSavedPassword => RouterSecrets.Unprotect(_settings.RouterPassword) is not null;

    /// <summary>
    /// status.json без входа. false — роутера нет (блок на главной пропадает) или сменился сертификат HTTPS и новый не принят.
    /// <paramref name="interactive"/> — пользователь сам нажал «Войти»: можно спросить про сертификат; иначе (фоновая проверка) — только сверка по SSH.
    /// </summary>
    async Task<bool> DetectAsync(bool interactive = false)
    {
        Searched = true;
        if (!Enabled)
        {
            // «Показывать роутер» снята: как при ненайденном роутере, но сеть не трогаем.
            SetDetection(null);
            SetConnection(() => RouterTexts.NotFoundText, notFound: true);
            return false;
        }
        var address = EffectiveAddress();
        if (address is null)
        {
            SetDetection(null);
            SetConnection(() => AddressAuto ? L.T("router.addr.unknown") : L.T("router.addr.enter"));
            return false;
        }
        if (ClientFor(address) is null) { SetDetection(null); return false; }
        RouterDetection d;
        try { d = await Call(c => c.DetectAsync()); }
        catch (RouterException e) when (e is { Failure: RouterFailure.CertificateChanged, Certificate: { } cert })
        {
            Connected = false;
            if (!await AcceptCertificateAsync(cert, interactive)) return false;
            d = await Call(c => c.DetectAsync());
        }
        SetDetection(d);
        if (d.Found == RouterFound.NotFound)
        {
            Connected = false;
            SetFound(d.Found, d.Status?.Version);
            return false;
        }
        // Другая версия договора: войти всё равно можно — доступны состояние, обновление и удаление (§5.4).
        if (d.Found != RouterFound.Ok && !Connected) SetFound(d.Found, d.Status?.Version);
        if (_client?.HttpsLost is { } lost && !AcceptPlainHttp(lost, interactive)) return false;
        return true;
    }

    void SetDetection(RouterDetection? d)
    {
        _detection = d;
        if (d is null || d.Found == RouterFound.NotFound) { Status = null; Connected = false; }
        else ClearRemoved();
        Raise(nameof(Detection)); Raise(nameof(Found)); Raise(nameof(NeedLogin)); Raise(nameof(InstallText)); RaiseAbout();
        UpdateHome();
    }

    /// <summary>Вход. Пароль из поля сохраняется (DPAPI) только после успеха; сохранённый, который больше не подходит, стирается.</summary>
    async Task<bool> LoginAsync(string? typed)
    {
        string? plain = typed;
        // Для повторного входа при истёкшей сессии: после успеха — уже из зашифрованного.
        Func<string> pw = () => plain ?? RouterSecrets.Unprotect(_settings.RouterPassword) ?? "";
        try
        {
            await Call(c => c.LoginAsync(pw));
        }
        catch (RouterException e) when (e.Failure == RouterFailure.AccessDenied)
        {
            plain = null;
            Connected = false;
            // Сохранённый перестал подходить (сменили пароль, переставили прошивку) — стереть, иначе каждый поиск ошибался бы им.
            if (typed is null && _settings.RouterPassword is not null)
            {
                _settings.RouterPassword = null;
                TrySave();
                RaisePassword();
            }
            SetConnection(() => typed == "" ? L.T("router.conn.has_password") : L.T("router.conn.bad_password"), needsPassword: true);
            return false;
        }
        catch (RouterException e)
        {
            plain = null;
            Connected = false;
            SetConnection(() => RouterTexts.Friendly(e));
            return false;
        }
        if (typed is not null) RememberPassword(typed);
        plain = null;
        Connected = true;
        var detectedVersion = _detection?.Status?.Version;
        SetConnection(() => L.T("router.conn.connected", new { version = detectedVersion }), ok: true);
        if (SelectOnRouterChosen is null) SelectOnRouter = true;   // роутер подключён — подбирать по умолчанию на нём
        try
        {
            await RefreshStatusCoreAsync();
            if (Compat?.CanCall("strategies") != false) await LoadStrategiesAsync();
        }
        catch (RouterException e) { Failed(RouterTexts.Friendly(e)); }
        return true;
    }

    void TrySave()
    {
        try { _settings.Save(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Failed(Texts.Friendly(e)); }
    }

    bool _refreshing;
    DateTime _refreshedAt;

    /// <summary>При запуске и после смены сети: поиск заново; есть сохранённый пароль — тихий вход; роутера нет — блок пропадает.</summary>
    public async Task RefreshAsync()
    {
        if (Busy || _refreshing) return;
        _refreshing = true;
        _refreshedAt = DateTime.Now;
        try
        {
            if (!await DetectAsync()) return;
            if (_client!.LoggedIn && Connected) await RefreshStatusCoreAsync();
            else if (HasSavedPassword) await LoginAsync(null);
            else if (_detection!.Found == RouterFound.Ok) SetFound(RouterFound.Ok, _detection.Status?.Version);
        }
        catch (RouterException e)
        {
            // Тихо: роутер мог пропасть на полпути (смена сети) — блок покажет последнее, следующая проверка уточнит.
            if (e.Failure is RouterFailure.Unreachable or RouterFailure.NoResponse) SetDetection(null);
            else SetConnection(() => RouterTexts.Friendly(e));
        }
        finally
        {
            _refreshing = false;
            JointRecheck();   // сеть сменилась или роутер вернулся: адаптер и MAC могли стать другими — связка проверяется сразу
        }
    }

    /// <summary>Тихое обновление по таймеру окна — раз в минуту, когда ничего не идёт.</summary>
    public async Task TickAsync()
    {
        if (Busy || Selecting || _refreshing || DateTime.Now - _refreshedAt < TimeSpan.FromMinutes(1)) return;
        await RefreshAsync();
        await JointTickSafeAsync();   // совместная работа: продление связки не реже раза в час, проверка «действует ли» — раз в минуту
    }

    // ---------- главная ----------

    RouterStatus? _status;
    public RouterStatus? Status
    {
        get => _status;
        private set
        {
            _status = value;
            Raise();
            // Расписание — как на роутере (без обратного вызова schedule_set).
            if (value?.Schedule is { } sch)
            {
                _scheduleEnabled = sch.Enabled; _scheduleHours = sch.Hours; _autoSelect = sch.AutoSelect;
                _watchdog = sch.Watchdog ?? value.Watchdog?.Enabled ?? false;
                Raise(nameof(ScheduleEnabled)); Raise(nameof(ScheduleHours)); Raise(nameof(ScheduleEditable)); Raise(nameof(AutoSelect));
            }
            SyncTrial(value?.PendingConfirm);
            RaiseWatchdog();
            Raise(nameof(ModeAll)); Raise(nameof(ModeLists)); Raise(nameof(ModeHint));
            RaiseCompat();
            UpdateHome();
        }
    }

    // ---------- проверка по расписанию на роутере (schedule_set) ----------

    /// <summary>Пункт списка периодов: подпись берётся из языка при каждом чтении, при смене языка список обновляется.</summary>
    public sealed class ScheduleOption(double hours, string key) : Observable
    {
        public double Hours { get; } = hours;
        public string Title => L.T(key);
    }

    /// <summary>Часы — по договору (§5.2, редакция §9): 0.5, 1, 3, 6, 12, 24.</summary>
    public static IReadOnlyList<ScheduleOption> ScheduleOptions { get; } =
    [
        new(0.5, "router.schedule.opt.30m"), new(1, "router.schedule.opt.1h"), new(3, "router.schedule.opt.3h"),
        new(6, "router.schedule.opt.6h"), new(12, "router.schedule.opt.12h"), new(24, "router.schedule.opt.24h"),
    ];

    bool _scheduleEnabled;
    double _scheduleHours = 1;

    /// <summary>«Проверять по расписанию»: при неудаче роутер сам переключается на запасную стратегию.</summary>
    public bool ScheduleEnabled
    {
        get => _scheduleEnabled;
        set { if (value != _scheduleEnabled) _ = SetScheduleAsync(value, _scheduleHours, _autoSelect); }
    }

    public bool ScheduleEditable => CanAct && ScheduleEnabled;

    public double ScheduleHours
    {
        get => _scheduleHours;
        set { if (value != _scheduleHours) _ = SetScheduleAsync(_scheduleEnabled, value, _autoSelect); }
    }

    bool _autoSelect;
    /// <summary>«Подбирать заново сам»: проверка не прошла и запасные кончились — роутер сам подбирает и включает лучшую (§5.2 schedule_set).</summary>
    public bool AutoSelect
    {
        get => _autoSelect;
        set { if (value != _autoSelect) _ = SetScheduleAsync(_scheduleEnabled, _scheduleHours, value); }
    }

    /// <param name="watchdog">Сторож (§5.2 schedule_set); null — как сейчас на роутере (у старого роутера поле не передаётся).</param>
    public async Task SetScheduleAsync(bool enabled, double hours, bool autoSelect = false, bool? watchdog = null)
    {
        if (watchdog is { } w)
        {
            // Сторож меняется своим сообщением: расписание то же.
            if (await Guard(w ? L.T("router.watchdog.on_busy") : L.T("router.watchdog.off_busy"), async () =>
                {
                    await Call(c => c.ScheduleSetAsync(enabled, hours, autoSelect, w));
                    await RefreshStatusCoreAsync();
                }))
                Say(w ? WatchdogOnText : L.T("router.watchdog.off_done"));
            else RaiseWatchdog();
            return;
        }
        bool? wd = WatchdogAvailable ? _watchdog : null;
        if (await Guard(L.T("router.schedule.busy"), async () =>
            {
                await Call(c => c.ScheduleSetAsync(enabled, hours, autoSelect, wd));
                await RefreshStatusCoreAsync();
            }))
            Say(!enabled ? L.T("router.schedule.off_done")
                : L.T(autoSelect ? "router.schedule.on_reselect_done" : "router.schedule.on_done",
                    new { interval = ScheduleOptions.FirstOrDefault(o => o.Hours == hours)?.Title ?? L.T("router.schedule.opt.other", new { hours }) }));
        else
        {
            // Не получилось — переключатели показывают то, что на роутере.
            Raise(nameof(ScheduleEnabled)); Raise(nameof(ScheduleHours)); Raise(nameof(AutoSelect));
        }
    }

    // ---------- режим: все сайты / по спискам (mode_set) ----------

    bool? _modeWanted;
    /// <summary>«Все сайты, кроме исключений» — по умолчанию на роутере. Пока смена идёт — то, что выбрали.</summary>
    public bool ModeAll
    {
        get => _modeWanted ?? (_status?.Mode ?? "all") != "lists";
        set { if (value != ModeAll) _ = SetModeAsync(value); }
    }
    public bool ModeLists { get => !ModeAll; set => ModeAll = !value; }
    public string ModeHint => ModeAll
        ? L.T("router.mode.hint_all")
        : L.T("router.mode.hint_lists");

    public async Task SetModeAsync(bool all)
    {
        _modeWanted = all;
        RaiseMode();
        try
        {
            if (await Guard(L.T("router.mode.busy"), async () =>
                {
                    await Call(c => c.ModeSetAsync(all));
                    await RefreshStatusCoreAsync();
                }))
                Say(L.T(all ? "router.mode.all_done" : "router.mode.lists_done"));
        }
        finally
        {
            _modeWanted = null;
            RaiseMode();
        }
    }

    void RaiseMode() { Raise(nameof(ModeAll)); Raise(nameof(ModeLists)); Raise(nameof(ModeHint)); }

    /// <summary>Обход на роутере включён (по status, без входа — по status.json).</summary>
    public bool RouterEnabled => _status?.Enabled ?? _detection?.Status?.Enabled ?? false;

    string _homeTitle = "", _homeDetail = "", _warnings = "", _toggleText = L.T("router.toggle.on");
    public string HomeTitle { get => _homeTitle; private set => Set(ref _homeTitle, value); }
    public string HomeDetail { get => _homeDetail; private set => Set(ref _homeDetail, value); }
    string _homeLine = "";
    /// <summary>Пояснение в строке роутера на главной: первое предупреждение, если есть, иначе <see cref="HomeDetail"/>.</summary>
    public string HomeLine { get => _homeLine; private set => Set(ref _homeLine, value); }
    public string Warnings { get => _warnings; private set { if (Set(ref _warnings, value)) Raise(nameof(HasWarnings)); } }
    public bool HasWarnings => Warnings.Length > 0;
    public string ToggleText { get => _toggleText; private set => Set(ref _toggleText, value); }

    /// <summary>Название как записано (без перевода автоимени): для строк, которые переводят себя сами.</summary>
    string RawTitle(string id) => _titles.TryGetValue(id, out var t) ? t.Title : _shell.Catalog.Find(id)?.Title ?? id;

    string StrategyTitle(string? id)
    {
        if (id is null) return L.T("router.strategy.none");
        if (_titles.TryGetValue(id, out var t)) return StrategyNaming.DisplayTitle(t.Title, t.Roles);
        // Найденная глубоким поиском на роутере («r-…») — в строках подбора она уже с названием.
        return _shell.Catalog.Find(id)?.Title ?? Scores.FirstOrDefault(s => s.Id == id && s.Title != id)?.Title ?? id;
    }

    /// <summary>Тексты главной и вкладки (заголовок, подробности, предупреждения, подпись кнопки) из состояния — без побочных действий.</summary>
    void ComputeHome()
    {
        var pub = _detection?.Status;
        bool enabled = RouterEnabled;
        var strategy = _status?.Strategy ?? pub?.Strategy;
        var at = _status?.LastCheck?.At ?? pub?.CheckedAt;
        var ok = _status?.LastCheck?.Ok ?? pub?.LastCheckOk;

        HomeTitle = Selecting ? L.T("router.home.selecting")
            : enabled ? L.T("router.home.on") : L.T("router.home.off");
        var parts = new List<string>();
        if (Selecting) parts.Add(SelectProgressText);
        else
        {
            parts.Add(L.T("router.home.strategy", new { name = StrategyTitle(strategy) }));
            if (Checking) parts.Add(L.T("router.home.checking"));
            else if (enabled && at is not null) parts.Add(L.T(ok == false ? "router.home.checked_bad" : "router.home.checked", new { time = RouterTexts.Time(at) }));
        }
        if (Updating) parts.Add(L.T("router.home.updating"));
        else if (_status?.Update is { Available: true, Version: { } uv }) parts.Add(L.T("router.home.update_available", new { version = uv }));
        if (Compat is { Level: not RouterCompatLevel.Ok }) parts.Add(CompatNeedsApp ? Compat.Level == RouterCompatLevel.Blocked ? L.T("router.home.app_needed") : L.T("router.home.app_available") :L.T("router.home.router_update"));
        else if (!Connected && _detection?.Found is RouterFound.AppTooOld or RouterFound.RouterTooOld)
            parts.Add(RouterTexts.Found(_detection.Found, null) is var t ? char.ToLowerInvariant(t[0]) + t[1..] : "");
        // Выключение сторожем, кнопкой или откатом — отдельной фразой с «Подробнее» (UpdateOff), не строкой предупреждений.
        UpdateOff();
        var rows = (_status?.Warnings ?? []).Where(w => !RouterTexts.IsOffCode(w.Code) && !(w.Code == "auto_selected" && _wdState == WatchdogSelectState.Found)).Select(w => RouterTexts.Warning(w, Conflicts)).ToList();
        if (!rows.SequenceEqual(WarningRows))
        {
            WarningRows.Clear();
            foreach (var r in rows) WarningRows.Add(r);
        }
        HomeDetail = string.Join(", ", parts.Where(p => p.Length > 0));
        // Строка на главной: есть что исправить — вместо стратегии и времени (они на вкладке) первое предупреждение (UX-аудит 2, п. 7).
        HomeLine = !Selecting && HasOff ? OffText : !Selecting && !enabled && FirstFix() is { } fix ? fix : HomeDetail;   // работает — текст предупреждений не на главной, а по «Подробнее»
        // Обновление для роутера — коротко и первым, даже когда строка занята предупреждением.
        if (!Selecting && !Updating && _status?.Update is { Available: true, Version: { } upd } && !HomeLine.Contains(upd))
            HomeLine = L.T("router.home.update_available", new { version = upd }) + " · " + HomeLine;
        Warnings = string.Join(" · ", rows.Select(r => r.Text));
        ToggleText = enabled ? L.T("router.toggle.off") : L.T("router.toggle.on");
    }

    void UpdateHome()
    {
        ComputeHome();
        Raise(nameof(RouterEnabled));
        Raise(nameof(FlowOffloadFixable)); Raise(nameof(Ipv6Fixable));
        RaisePc();
        RememberBypass();
        Changed?.Invoke();
        JointKick();
    }

    /// <summary>Предупреждения роутера — по строке, у исправимых кнопка (flow_offload, ipv6 — §9).</summary>
    public ObservableCollection<RouterWarningRow> WarningRows { get; } = [];
    public ICommand FixWarningCmd { get; }
    public bool FlowOffloadFixable => _status?.Warnings?.Any(w => w.Code == "flow_offload") == true;
    public bool Ipv6Fixable => _status?.Warnings?.Any(w => w.Code == "ipv6") == true;

    /// <summary>«Исправить» у предупреждения: разгрузку — zapret (flow_offload_fix), обход по IPv6 (ipv6_set); чужой обход — в диагностику.</summary>
    public async Task FixWarningAsync(string code)
    {
        switch (code)
        {
            case "flow_offload":
                if (await Guard(L.T("router.fix.flow_busy"), async () =>
                    {
                        await Call(c => c.FlowOffloadFixAsync());
                        await RefreshStatusCoreAsync();
                    }))
                    Say(L.T("router.fix.flow_done"));
                break;
            case "ipv6":
                if (await Guard(L.T("router.fix.ipv6_busy"), async () =>
                    {
                        await Call(c => c.Ipv6SetAsync(true));
                        await RefreshStatusCoreAsync();
                    }))
                    Say(L.T("router.fix.ipv6_done"));
                break;
            case "conflict":
                Navigate(Pages.Router);   // сам обновит «Роутер: что мешает»; на вкладке уже — обновим здесь
                await RefreshDiagAsync();
                break;
        }
        if (DiagLoaded && code != "conflict") await RefreshDiagAsync();
    }

    // ---------- этот ПК и роутер вместе (router-api §6) ----------

    /// <summary>Обход включён и на этом ПК, и на роутере: пакеты обрабатываются дважды, сайты ломаются.</summary>
    public bool BothOn => RouterEnabled && PcRunning() && !JointCovers;

    /// <summary>Совместная работа включена и не откатилась: ПК и роутер работают вместе по договору (у каждого свои адреса) — «включён и там, и там» не помеха.</summary>
    bool JointCovers => _joint is { Wanted: true } j && j.State != JointState.Fallback;
    public ICommand StopPcCmd { get; }
    public ICommand StopRouterCmd { get; }

    /// <summary>Окно сообщает, что обход на ПК включился или выключился.</summary>
    public void PcChanged() { RaisePc(); JointKick(); }

    void RaisePc()
    {
        Raise(nameof(BothOn)); Raise(nameof(TimestampsNote)); Raise(nameof(HasTimestampsNote)); Raise(nameof(TimestampsNeedAction)); Raise(nameof(TimestampsDone));
        Raise(nameof(HomeFixText)); Raise(nameof(HasHomeFix));
    }

    /// <summary>Главная — одной строкой: сколько у роутера того, что можно исправить (сами предупреждения — на вкладке «Роутер»).</summary>
    public string HomeFixText => FixCount is var n && n > 0 ? "· " + WarningsCount(n) : "";
    public bool HasHomeFix => HomeFixText.Length > 0;
    int FixCount => WarningRows.Count + (TimestampsNeedAction ? 1 : 0);
    string? FirstFix() => WarningRows.FirstOrDefault()?.Text ?? (TimestampsNeedAction ? TimestampsNeed : null);

    /// <summary>«1 предупреждение», «2 предупреждения», «5 предупреждений».</summary>
    public static string WarningsCount(int n) => L.Plural("router.warnings_count", n);

    async Task StopPcForRouterAsync()
    {
        if (await StopPc()) Say(L.T("router.pc.stopped"));
        RaisePc();
    }

    /// <summary>Выключить обход на роутере (например, чтобы включить на ПК). true — выключен.</summary>
    public async Task<bool> DisableAsync()
    {
        if (!RouterEnabled) return true;
        return await Guard(L.T("router.disable.busy"), async () =>
        {
            await Call(c => c.DisableAsync());
            await RefreshStatusCoreAsync();
        }) && !RouterEnabled;
    }

    /// <summary>Включить обход на роутере обратно (после подбора на ПК).</summary>
    public async Task<bool> EnableAsync()
    {
        if (RouterEnabled) return true;
        return await TrialAsync(L.T("router.enable.busy"), (c, confirmS) => c.EnableAsync(confirmS), done: null);
    }

    // ---------- временные метки TCP (needs_tcp_timestamps, §6) ----------

    bool? _pcTimestamps;
    bool _timestampsTried, _timestampsByRouter;

    /// <summary>
    /// Стратегия роутера работает только с временными метками TCP (настройка Windows): включили сами — одна строка;
    /// не смогли — плашка как у предупреждений роутера и «Включить» со щитом (UX-аудит 2, п. 8). Термин — только в подсказке.
    /// </summary>
    public string TimestampsNote => !NeedsTimestamps ? ""
        : _pcTimestamps == false ? TimestampsNeed
        : _timestampsByRouter ? TimestampsOn
        : "";
    public static string TimestampsNeed => L.T("router.timestamps.need");
    public static string TimestampsOn => L.T("router.timestamps.on");
    public static string TimestampsTerm => L.T("router.timestamps.term");
    public bool HasTimestampsNote => TimestampsNote.Length > 0;
    /// <summary>Включили сами — серой строкой (плашка — только когда нужно действие).</summary>
    public bool TimestampsDone => HasTimestampsNote && !TimestampsNeedAction;
    public bool TimestampsNeedAction => NeedsTimestamps && _pcTimestamps == false;
    public ICommand EnableTimestampsCmd { get; }
    bool NeedsTimestamps => _status is { NeedsTcpTimestamps: true, Enabled: true };

    /// <summary>Метки нужны, а на ПК выключены — включить через помощника. Сам — один раз и без вопросов; по кнопке — с предложением установить помощника.</summary>
    public async Task CheckTimestampsAsync(bool ask = false)
    {
        if (!NeedsTimestamps) { RaisePc(); return; }
        if (_pcTimestamps != true) _pcTimestamps = await Task.Run(PcTimestamps);
        if (_pcTimestamps == false && (ask || !_timestampsTried))
        {
            _timestampsTried = true;
            bool on;
            try { on = await EnablePcTimestamps(ask); }
            catch (Exception e) when (e is IOException or InvalidOperationException or TimeoutException) { on = false; }
            if (on)
            {
                _pcTimestamps = true;
                _timestampsByRouter = true;
                Say(TimestampsOn);
            }
            else if (ask) Failed(L.T("router.timestamps.failed"));
        }
        UpdateHome();   // число предупреждений и первое из них на главной
    }

    /// <param name="resumeSelect">Подбор уже идёт (запущен не нами или до запуска программы) — показать его ход.</param>
    async Task RefreshStatusCoreAsync(bool resumeSelect = true)
    {
        Status = await Call(c => c.StatusAsync());
        if (resumeSelect && _status?.Jobs?.Select == true && !_polling && !Blocked) _ = PollSelectAsync();
        await CheckTimestampsAsync();
    }

    public ObservableCollection<RouterStrategyRow> Strategies { get; } = [];

    int _catalogVersion;
    /// <summary>Версия каталога роутера (strategies.catalog_version): с 3 он знает роль discord_tcp и поле parts (§5.3); 0 — не узнали.</summary>
    public int CatalogVersion => _catalogVersion;
    /// <summary>Роутер принимает стратегии с ролью discord_tcp (смешанные с Discord-частью).</summary>
    public bool KnowsDiscordTcp => _catalogVersion >= RouterClient.DiscordTcpCatalogVersion;

    async Task LoadStrategiesAsync()
    {
        var list = await Call(c => c.StrategiesAsync());
        _catalogVersion = list.CatalogVersion;
        _strategyItems = list.Items;
        foreach (var s in list.Items)
        {
            _titles[s.Id] = (s.Title, s.Roles); if (s.Roles is not null) _roles[s.Id] = s.Roles;
        }
        FillStrategies();
    }

    IReadOnlyList<RouterStrategyItem> _strategyItems = [];

    /// <summary>Меню стратегий роутера из сохранённого ответа (собирается заново при смене языка).</summary>
    void FillStrategies()
    {
        Strategies.Clear();
        foreach (var s in _strategyItems)
            Strategies.Add(new(s.Id, StrategyNaming.DisplayTitle(s.Title, s.Roles), StrategyNote(s), s.Roles is { Count: > 0 } roles ? L.T("router.strategy.roles") + "\n" + StrategyParams.Roles(roles) : ""));
    }

    /// <summary>
    /// «найдена на роутере · ✓ проверено 18.10 в 14:30» — только последняя проверка (если роутер её отдаёт), без истории
    /// «удачных N из M»: прошлые прогоны о сегодняшней сети не говорят.
    /// </summary>
    public static string StrategyNote(RouterStrategyItem s)
    {
        var parts = new List<string>();
        if (s.FoundOnRouter) parts.Add(L.T("router.strategy.found"));
        else if (s.FromPc) parts.Add(L.T("router.strategy.from_pc"));
        if (s.Last is { } last)
        {
            var when = last.At?.ToLocalTime();
            var failed = !last.Ok && last.Failed is { Count: > 0 } f ? string.Join(", ", f) : null;
            parts.Add(L.T(last.Ok ? (when is null ? "router.strategy.ok" : "router.strategy.ok_at")
                : failed is null ? (when is null ? "router.strategy.fail" : "router.strategy.fail_at")
                : when is null ? "router.strategy.fail_sites" : "router.strategy.fail_at_sites",
                new { date = when is { } d ? L.DayMonth(d) : "", time = when is { } d2 ? L.Time(d2) : "", sites = failed ?? "" }));
        }
        return string.Join(" · ", parts);
    }

    public string? CurrentStrategyId => _status?.Strategy ?? _detection?.Status?.Strategy;

    /// <summary>Положить текст в буфер обмена (в тестах — подмена).</summary>
    public Action<string> CopyText { get; set; } = text => System.Windows.Clipboard.SetText(text);

    /// <summary>«Скопировать параметры» стратегии роутера (только если роутер отдал роли).</summary>
    public void CopyParams(RouterStrategyRow s)
    {
        if (!s.HasParams) return;
        try { CopyText($"{s.Title}\n{s.Params}"); Say(L.T("router.strategy.copied", new { name = s.Title })); }
        catch (System.Runtime.InteropServices.ExternalException) { Failed(L.T("router.strategy.copy_failed")); }
    }

    public ICommand LoginCmd { get; }
    /// <summary>«Найти»: поиск роутера по адресу из настроек; есть сохранённый пароль — и вход.</summary>
    public ICommand FindCmd { get; }
    public ICommand ToggleCmd { get; }
    public ICommand CheckCmd { get; }
    public ICommand GoSelectCmd { get; }

    bool _busy;
    public bool Busy
    {
        get => _busy;
        private set { if (Set(ref _busy, value)) { Raise(nameof(CanAct)); Raise(nameof(CanConnect)); Raise(nameof(ScheduleEditable)); Requery(); } }
    }

    /// <summary>Одно действие за раз; ошибки роутера — коротким текстом в строку состояния.</summary>
    async Task<bool> Guard(string text, Func<Task> work, bool changes = true)
    {
        if (Busy) { Say(L.T("router.guard.busy")); return false; }
        if (!Connected) { Say(L.T("router.guard.not_connected")); return false; }
        if (Blocked) { Failed(Compat!.Text ?? L.T("router.guard.incompatible")); return false; }
        if (changes && !RiskAccepted()) return false;
        Busy = true;
        Say(text);
        try
        {
            await work();
            return true;
        }
        catch (RouterException e)
        {
            Failed(RouterTexts.Friendly(e));
            if (e.Failure == RouterFailure.AccessDenied)
            {
                Connected = false;
                SetConnection(() => L.T("router.conn.relogin"));
            }
            else if (e.Failure == RouterFailure.Unreachable) SetConnection(() => L.T("router.conn.unreachable"));
            // Занят подбором (запущен с другого устройства или страницы роутера) — показать его ход.
            if (e.AppError?.Code == "busy" && !_polling) _ = RefreshStatusSafeAsync();
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Failed(Texts.Friendly(e));
            return false;
        }
        finally { Busy = false; }
    }

    async Task RefreshStatusSafeAsync(bool resumeSelect = true)
    {
        try { await RefreshStatusCoreAsync(resumeSelect); }
        catch (RouterException) { /* следующая проверка по таймеру */ }
    }

    public async Task ToggleAsync()
    {
        bool enable = !RouterEnabled;
        // §6: вместе с обходом ПК роутер не работает — включение второго только осознанно и с выключением первого.
        if (enable && PcRunning())
        {
            if (!Confirm(L.T("router.enable.confirm"))) return;
            if (!await StopPc()) { Failed(L.T("router.enable.pc_off_failed")); return; }
        }
        if (enable)
        {
            // На пробу (§5.2 confirm_s): проверка с этого ПК решит, оставить ли; без ответа роутер вернёт как было сам.
            // Журнал окна: оптимизация на роутере была выключена — запись ДО включения.
            var rec = Connected ? BeginRouter(ChangeKinds.RouterEnable, L.T("router.change.enabled"), new JsonObject { ["enabled"] = true }) : "";
            await TrialAsync(L.T("router.enable.busy"), (c, confirmS) => c.EnableAsync(confirmS), L.T("router.enable.done"));
            if (rec.Length > 0) { if (RouterEnabled) Recorder.Done(rec); else Recorder.Abort(rec); }
            return;
        }
        if (await Guard(L.T("router.disable.busy"), async () =>
            {
                await Call(c => c.DisableAsync());
                await RefreshStatusCoreAsync();
            }))
        {
            Recorder.Close(ChangeKinds.RouterEnable, L.T("router.change.disabled_button"));
            Say(L.T("router.disable.done"));
        }
    }

    public async Task ChooseStrategyAsync(string id)
    {
        if (id == CurrentStrategyId) return;
        // При включённом обходе смена стратегии — на пробу, как включение (§5.2).
        bool trial = RouterEnabled;
        await TrialAsync(L.T("router.strategy.busy"), (c, confirmS) => c.StrategySetAsync(id, trial ? confirmS : null), L.T("router.strategy.done", new { name = StrategyTitle(id) }));
    }

    bool _checking;
    public bool Checking { get => _checking; private set { if (Set(ref _checking, value)) UpdateHome(); } }

    public async Task CheckAsync()
    {
        string? result = null;
        if (await Guard(L.T("router.check.busy"), async () => result = await CheckCoreAsync(), changes: false))
            Say(result ?? "");
    }

    /// <summary>check_now и ждать новый status.last_check (опрос раз в PollInterval).</summary>
    async Task<string> CheckCoreAsync()
    {
        Checking = true;
        try
        {
            var before = (await Call(c => c.StatusAsync())).LastCheck?.At;
            await Call(c => c.CheckNowAsync());
            var deadline = DateTime.UtcNow + CheckTimeout;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(PollInterval);
                var st = await Call(c => c.StatusAsync());
                if (st.LastCheck?.At != before && st.Jobs?.Check != true)
                {
                    Checking = false;
                    Status = st;
                    var bad = (st.LastCheck!.Groups ?? []).Where(g => !g.Ok).Select(g => GroupTitle(g.Id)).ToList();
                    return st.LastCheck.Ok ? L.T("router.check.ok")
                        : bad.Count > 0 ? L.T("router.check.fail", new { sites = string.Join(", ", bad) }) : L.T("router.check.fail_some");
                }
            }
            return L.T("router.check.pending");
        }
        finally { Checking = false; }
    }

    GroupNames? _names;
    GroupNames Names => _names ??= new GroupNames(_shell.DefaultTargets.Groups);

    string GroupTitle(string id) => id switch
    {
        "user" => L.T("router.group.user"),
        _ => _shell.DefaultTargets.Groups.FirstOrDefault(g => g.Id == id)?.Title ?? id,
    };

    // ---------- стратегия с ПК → на роутер (§6) ----------

    public ICommand SendStrategyCmd { get; }

    /// <summary>
    /// Найденную на ПК стратегию — на роутер: strategies_user_set (все найденные на ПК, вместе с этой: вопрос 7 в §8),
    /// strategy_set, check_now и ждать итог проверки.
    /// </summary>
    public async Task SendStrategyAsync(string id)
    {
        var found = FoundStrategies().ToList();
        if (found.FirstOrDefault(s => s.Id == id) is not { } strategy)
        {
            Say(L.T("router.send.not_found"));
            return;
        }
        // Набор из приложения роутер заменяет целиком (§5.2) — шлём все найденные; название — до 80 символов.
        // Старому роутеру (catalog_version < 3) роль discord_tcp и parts не шлём — вариант без них и предложение обновить пакет (§5.3).
        bool newRouter = KnowsDiscordTcp;
        var items = found.Select(s => RouterClient.ToRouter(s, newRouter)).Select(x => x.Title.Length > 80 ? x with { Title = x.Title[..80] } : x).ToList();
        var routerId = RouterClient.ToRouter(strategy, newRouter).Id;
        bool trimmed = !newRouter && strategy.Roles.ContainsKey("discord_tcp");
        string? result = null;
        if (await Guard(L.T("router.send.busy"), async () =>
            {
                try { await Call(c => c.StrategiesUserSetAsync(items)); }
                catch (RouterException e) when (e.Failure == RouterFailure.NotSupported)
                {
                    throw new RouterException(e.Failure, L.T("router.send.unsupported"));
                }
                catch (RouterException e) when (e.Failure == RouterFailure.InvalidArgs || e.AppError?.Code == "invalid")
                {
                    throw new RouterException(e.Failure, e.AppError?.Field is { } f
                        ? L.T("router.send.rejected_field", new { name = strategy.Title, field = f })
                        : L.T("router.send.rejected", new { name = strategy.Title }), e.AppError);
                }
                _titles[routerId] = (strategy.Title, strategy.Roles);
                var confirmS = RouterEnabled ? TrialArg : null;
                await Call(c => c.StrategySetAsync(routerId, confirmS));
                result = await CheckCoreAsync();
                await LoadStrategiesAsync();
            }))
        {
            Say(L.T("router.send.done", new { name = strategy.Title, result = result ?? "" })
                + (trimmed ? " " + L.T("router.send.discord_trimmed") : ""));
            if (_status?.PendingConfirm is not null) await VerifyTrialAsync();
        }
    }

    // ---------- подбор на роутере ----------

    bool? _selectOnRouter;
    /// <summary>Выбор пользователя «Где подбирать»; null — ещё не выбирал.</summary>
    bool? SelectOnRouterChosen => _selectOnRouter;
    public bool SelectOnRouter
    {
        get => _selectOnRouter == true;
        set
        {
            if (_selectOnRouter == value) return;
            _selectOnRouter = value;
            Raise(); Raise(nameof(SelectOnPc)); Raise(nameof(SelectOnRouterVisible)); Raise(nameof(SelectOnPcVisible));
        }
    }
    public bool SelectOnPc { get => !SelectOnRouter; set => SelectOnRouter = !value; }
    public bool SelectOnRouterVisible => Connected && SelectOnRouter;
    public bool SelectOnPcVisible => !SelectOnRouterVisible;

    bool _selecting;
    public bool Selecting
    {
        get => _selecting;
        private set { if (Set(ref _selecting, value)) { Requery(); UpdateHome(); } }
    }

    int _progress, _progressMax = 1;
    public int SelectProgress { get => _progress; private set => Set(ref _progress, value); }
    public int SelectProgressMax { get => _progressMax; private set => Set(ref _progressMax, value); }

    string _progressText = "";
    public string SelectProgressText { get => _progressText; private set { if (Set(ref _progressText, value) && Selecting) UpdateHome(); } }

    readonly AddressBlockTracker _addressBlock = new();
    bool _addressNoteOn;
    /// <summary>«Не открылось ни с одной стратегией: … — похоже на блокировку по адресу…»; пусто, когда таких адресов нет. Собирается из учёта адресов при показе.</summary>
    string? _addressNoteFixed;   // готовая строка — только если её поставили извне (тесты)
    public string AddressNote { get => _addressNoteFixed ?? (_addressNoteOn ? Texts.WithNextStep(_addressBlock.Note(), router: true) : ""); private set { _addressNoteFixed = value; Raise(); Raise(nameof(HasAddressNote)); } }
    void SetAddressNote(bool on) { _addressNoteFixed = null; _addressNoteOn = on; Raise(nameof(AddressNote)); Raise(nameof(HasAddressNote)); }
    public bool HasAddressNote => AddressNote.Length > 0;

    string _result = "", _resultHint = "";
    public string ResultText { get => _result; private set { if (Set(ref _result, value)) { Raise(nameof(HasResult)); RaiseResultShape(); } } }
    public bool HasResult => ResultText.Length > 0;
    public string ResultHint { get => _resultHint; private set => Set(ref _resultHint, value); }

    bool _resultExpanded;
    /// <summary>«Подробнее» раскрыло прежнюю карточку итога; новый подбор снова показывает её целиком.</summary>
    public bool ResultExpanded { get => _resultExpanded; private set { if (Set(ref _resultExpanded, value)) RaiseResultShape(); } }
    /// <summary>Роутер включил (или мы применили) лучшую — итог сворачивается в строку «✓ Включена на роутере …».</summary>
    public bool ResultCompact => HasResult && _applied && _best is not null && !_resultExpanded;
    public bool ResultFull => HasResult && !ResultCompact;
    public string ResultSummary
    {
        get
        {
            var row = Scores.FirstOrDefault(s => s.Id == _best);
            var name = row?.Title ?? StrategyTitle(_best ?? "");
            return row is null ? L.T("router.result.applied", new { name }) : L.T("router.result.applied_row", new { name, groups = row.Groups, share = row.Share });
        }
    }
    public ICommand ShowResultDetailsCmd => _showResultDetailsCmd ??= new RelayCommand(() => ResultExpanded = true);
    ICommand? _showResultDetailsCmd;
    void RaiseResultShape() { Raise(nameof(ResultCompact)); Raise(nameof(ResultFull)); Raise(nameof(ResultSummary)); }

    string? _best;
    bool _applied;
    /// <summary>Лучшая по итогу подбора.</summary>
    public string? Best => _best;
    /// <summary>Роутер сам включил лучшую (редакция §9: applied).</summary>
    public bool Applied => _applied;
    /// <summary>Подбор закончился, а лучшая не включена (например, подбор со страницы роутера без автоприменения) — кнопка «Применить на роутере».</summary>
    public bool HasBest => _best is not null && !_applied;

    public ObservableCollection<RouterScoreRow> Scores { get; } = [];

    RouterScoreRow? _selectedScore;
    public RouterScoreRow? SelectedScore { get => _selectedScore; set { if (Set(ref _selectedScore, value)) { Raise(nameof(ApplySelectedText)); Requery(); } } }
    /// <summary>Выбрана лучшая — «Применить лучшую»; другая строка — «Применить выбранную».</summary>
    public string ApplySelectedText => _selectedScore is { IsBest: true } ? L.T("router.apply.best") : L.T("router.apply.selected");

    public ICommand StartSelectCmd { get; }
    public ICommand StartDeepCmd { get; }

    bool _offerDeep;
    /// <summary>Подбор на роутере ничего не нашёл — в итоге предложить глубокий поиск.</summary>
    public bool OfferDeep { get => _offerDeep; private set => Set(ref _offerDeep, value); }
    public ICommand StopSelectCmd { get; }
    public ICommand ApplyCmd { get; }
    public ICommand ApplySelectedCmd { get; }

    void SetBest(string? id, bool applied)
    {
        _best = id; _applied = applied;
        Raise(nameof(Best)); Raise(nameof(Applied)); Raise(nameof(HasBest));
        RaiseResultShape();
        Requery();
    }

    public Task StartSelectAsync(bool deep) => StartSelectAsync(deep, null);

    /// <summary>Подбор на роутере; options — свои группы и режим вместо выбранных на экране «Подбор».</summary>
    public async Task StartSelectAsync(bool deep, (IReadOnlyList<string> Groups, bool Full)? options)
    {
        if (Selecting || (options ?? SelectOptions()) is not { } o) return;
        bool onlyUser = o.Groups.Count == 1 && o.Groups[0] == "user";
        string? hint = null;
        // Подбор только по своим адресам, а на роутере их нет (router-api §5.2): есть на ПК — предложить их заранее,
        // нет нигде — снять «Свои сайты» (выбор мог остаться от прежней установки роутера).
        if (onlyUser && _targetsLoaded && Targets.Count == 0)
        {
            if (PcUserTargets().Count == 0) o = WithoutUser(o, out hint);
            else if (!await OfferPcTargetsAsync()) return;
        }
        bool emptyOnRouter = false, dropUser = false;
        RouterException? busyError = null;
        for (int attempt = 0; ; attempt++)
        {
            if (dropUser) { o = WithoutUser(o, out hint); dropUser = false; }
            if (!await Guard(L.T("router.select.busy"), async () =>
                {
                    Scores.Clear(); SelectedScore = null; ResultText = ""; ResultHint = ""; SetBest(null, false); ResultExpanded = false; SetAddressNote(false);
                    if (!deep) _addressBlock.Clear();   // глубокий поиск продолжает счёт проверок прежнего подбора
                    SelectProgress = 0; SelectProgressText = L.T("router.select.preparing");
                    try { await Call(c => c.SelectStartAsync(o.Groups, o.Full, deep)); }
                    catch (RouterException e) when (e.AppError?.Code == "busy" && !Selecting)
                    {
                        // «Подбор уже запущен» — не ошибка: он идёт (первый запуск мы могли не заметить), покажем его ход.
                        busyError = e; SelectProgressText = StartingText;
                    }
                    catch (RouterException e) when (attempt == 0 && IsEmptyUserTargets(e))
                    {
                        // Роутер ответил «Список своих сайтов на роутере пуст…» — предложим отправить с ПК и повторим;
                        // на ПК их тоже нет — повторим без «Своих сайтов».
                        Targets.Clear(); _targetsLoaded = true;
                        if (PcUserTargets().Count > 0) emptyOnRouter = true; else dropUser = true;
                        SelectProgressText = "";
                    }
                }))
            {
                SelectProgressText = "";
                return;
            }
            if (dropUser) continue;
            if (!emptyOnRouter) break;
            emptyOnRouter = false;
            if (!await OfferPcTargetsAsync()) { SelectProgressText = ""; return; }
        }
        if (busyError is not null)
        {
            // Занято подбором — показать его; занято другим (проверка) — сказать как есть.
            await RefreshStatusSafeAsync(resumeSelect: false);
            if (_status?.Jobs?.Select != true)
            {
                SelectProgressText = "";
                Failed(RouterTexts.Friendly(busyError));
                return;
            }
            Say(L.T("router.select.already"));
        }
        else Say(hint ?? "");
        await PollSelectAsync(justStarted: true);
    }

    public static string StartingText => L.T("router.select.starting");

    /// <summary>Сколько после запуска ошибка или пустой ответ select_progress — ещё «Запускаю подбор…» (роутер поднимает подбор не сразу).</summary>
    public TimeSpan SelectStartGrace { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Выбор без «Своих сайтов» (на роутере их нет); ничего не осталось — группы по умолчанию. Экран «Подбор» — следом.</summary>
    (IReadOnlyList<string> Groups, bool Full) WithoutUser((IReadOnlyList<string> Groups, bool Full) o, out string hint)
    {
        var rest = o.Groups.Where(g => g != "user").ToList();
        IReadOnlyList<string> groups = rest.Count > 0 ? rest : DefaultSelectGroups;
        SetSelectGroups(groups);
        hint = L.T("router.select.no_user_sites");
        return (groups, o.Full);
    }

    /// <summary>
    /// DpiShell на роутере поставлен заново или удалён: прежние свои сайты и выбор групп к нему не относятся —
    /// забыть их и вернуть группы подбора по умолчанию (иначе «только Свои сайты» на свежем роутере не подбирает ничего).
    /// </summary>
    public void ForgetRouterSetup()
    {
        Targets.Clear();
        _targetsLoaded = _listsLoaded = false;
        SetSelectGroups(DefaultSelectGroups);
    }

    /// <summary>Отказ подбора: только «Свои сайты», а на роутере их список пуст (router-api §5.2: invalid, field groups).</summary>
    static bool IsEmptyUserTargets(RouterException e) =>
        e.AppError is { Code: "invalid", Field: "groups" };

    /// <summary>
    /// «Отправить свои сайты с компьютера на роутер»: с согласия — свои сайты ПК (до 50) уходят на роутер (targets_set).
    /// true — отправлены, подбор можно запускать.
    /// </summary>
    async Task<bool> OfferPcTargetsAsync()
    {
        var pc = PcUserTargets();
        if (!Confirm(L.T("router.targets.offer", new { count = pc.Count })))
        {
            Say(L.T("router.targets.not_started"));
            return false;
        }
        var items = pc.Select(d => new RouterTarget(RouterTexts.ToUrl(d), false))
            .DistinctBy(t => t.Url, StringComparer.OrdinalIgnoreCase).Take(MaxTargets).ToList();
        if (!await Guard(L.T("router.targets.sending"), () => Call(c => c.TargetsSetAsync(items)))) return false;
        Targets.Clear();
        foreach (var t in items) Targets.Add(new(t.Url, t.Important));
        _targetsLoaded = true;
        MarkSitesSaved();
        TargetsMessage = L.T(pc.Count > items.Count ? "router.targets.saved_skipped" : "router.targets.saved", new { count = items.Count, skipped = pc.Count - items.Count });
        return true;
    }

    bool _polling;

    /// <summary>Ход подбора — select_progress раз в PollInterval, пока running. Подбор мог запустить и не этот ПК (started_by).</summary>
    public async Task PollSelectAsync(bool justStarted = false)
    {
        if (_polling || !Connected) return;
        _polling = true;
        Selecting = true;
        int failures = 0;
        var grace = justStarted ? DateTime.UtcNow + SelectStartGrace : DateTime.MinValue;
        try
        {
            while (Connected)
            {
                RouterSelectProgress p;
                try { p = await Call(c => c.SelectProgressAsync()); failures = 0; }
                catch (RouterException e) when (e.Failure != RouterFailure.AccessDenied && DateTime.UtcNow < grace)
                {
                    SelectProgressText = StartingText;
                    await Task.Delay(PollInterval);
                    continue;
                }
                catch (RouterException e) when (e.Failure is RouterFailure.Unreachable or RouterFailure.NoResponse && ++failures < 5)
                {
                    await Task.Delay(PollInterval);
                    continue;
                }
                ShowProgress(p);
                if (!p.Running) { Finish(p); break; }
                await Task.Delay(PollInterval);
            }
        }
        catch (RouterException e) { Failed(RouterTexts.Friendly(e)); ResultText = L.T("router.select.status_failed"); }
        finally
        {
            _polling = false;
            Selecting = false;
            SelectProgressText = "";
        }
        // Только что опросили до конца — не начинать опрос снова, даже если status ещё не обновил jobs.select.
        await RefreshStatusSafeAsync(resumeSelect: false);
        // Глубокий поиск мог найти новые (id «r-…») — они появляются в списке стратегий роутера.
        try { if (Connected) await LoadStrategiesAsync(); }
        catch (RouterException) { /* список обновится при следующем входе */ }
    }

    /// <summary>«осталось ~3 мин» по eta_s; null — оценки ещё нет.</summary>
    public static string Eta(int? seconds) => seconds switch
    {
        null => "",
        < 60 => L.T("router.eta.under_minute"),
        var s => L.T("router.eta.minutes", new { n = (int)Math.Ceiling(s.Value / 60.0) }),
    };

    int _groupsTotal;

    void ShowProgress(RouterSelectProgress p)
    {
        SelectProgressMax = Math.Max(1, p.Total);
        SelectProgress = Math.Min(p.Index, SelectProgressMax);
        if (p.Running && p.Total == 0 && p.Index == 0 && p.Message is not { Length: > 0 }) SelectProgressText = StartingText;
        else if (p.Running)
        {
            var who = p.StartedBy switch { "page" => L.T("router.select.started_page") + " ", "auto" or "timer" or "watchdog" => L.T("router.select.started_auto") + " ", _ => "" };
            var shown = RouterPhrase.FromObject(p.MessageMsg, p.Message);
            var parts = new[] { shown is { Length: > 0 } m ? L.T("router.select.progress_msg", new { index = p.Index, total = p.Total, message = m }) : L.T("router.select.progress", new { index = p.Index, total = p.Total }), Eta(p.EtaS) };
            SelectProgressText = who + string.Join(" · ", parts.Where(x => x.Length > 0));
        }
        else SelectProgressText = "";
        var seen = (p.Rows ?? []).Where(r => !r.Candidate).Select(r => r.Verdicts?.Select(v => v.Group).Distinct().Count() ?? 0).DefaultIfEmpty(0).Max();
        if (seen > 0) _groupsTotal = seen;
        foreach (var r in p.Rows ?? [])
        {
            var i = Scores.ToList().FindIndex(x => x.Id == r.Id);
            var row = new RouterScoreRow(r, r.Title ?? RawTitle(r.Id), Names, _groupsTotal) { Roles = r.Roles ?? _roles.GetValueOrDefault(r.Id) };
            if (i < 0) Scores.Add(row);
            else if (!Scores[i].Same(r)) { row.Expanded = Scores[i].Expanded; Scores[i] = row; }
        }
    }

    /// <summary>Порядок списков стратегий (общий с ПК, window.json): по имени / по результату.</summary>
    public bool SortByName { get => _settings.SortByName; set => _settings.SetStrategySort(value); }
    public bool SortByResult { get => !_settings.SortByName; set => _settings.SetStrategySort(!value); }

    List<RouterScoreRow> InOrder(IEnumerable<RouterScoreRow> rows) => _settings.SortByName
        ? StrategyOrder.SortByName(rows, s => s.Title).ToList()
        : rows.OrderByDescending(s => s.Usable).ThenByDescending(s => s.Row.GroupShare).ThenByDescending(s => s.Row.TargetShare).ThenBy(s => s.Row.AvgS).ToList();

    void ResortScores()
    {
        if (Scores.Count < 2 || Selecting) return;
        var sel = SelectedScore;
        var ordered = InOrder(Scores);
        Scores.Clear();
        foreach (var s in ordered) Scores.Add(s);
        SelectedScore = sel;
    }

    void Finish(RouterSelectProgress p)
    {
        var ordered = InOrder(Scores);
        Scores.Clear();
        foreach (var s in ordered) { s.IsBest = s.Id == p.Best; Scores.Add(s); }
        SelectedScore = Scores.FirstOrDefault(s => s.IsBest);   // лучшая выбрана сразу, «Применить лучшую» активна
        SelectProgress = SelectProgressMax;
        foreach (var row in Scores.Where(x => !x.IsCandidate && x.Row.Error is null))
            foreach (var v in row.Row.Verdicts ?? []) _addressBlock.Add(row.Id, v.Url, v.Ok, !v.Ok && AddressBlockTracker.IsSilent(v.Detail));
        SetAddressNote(true);
        if (p.Cancelled)
        {
            SetBest(null, false);
            ResultText = L.T("router.select.stopped");
            ResultHint = L.T("router.select.stopped_hint");
        }
        else if (p.Best is { } best)
        {
            SetBest(best, p.Applied);
            var backups = (p.Backups ?? []).Select(StrategyTitle).ToList();
            // Включена сама — «Включена»; иначе «Подходит» только когда открылось всё (при частичном — «Лучшая из проверенных: … — открывается N из M»).
            var bestRow = Scores.FirstOrDefault(s => s.Id == best);
            bool perfect = bestRow is null || bestRow.Row.GroupShare >= 0.999;
            int? gok = bestRow?.Row.GroupsOk, gtotal = bestRow?.Row.GroupsTotal is > 0 ? bestRow.Row.GroupsTotal : _groupsTotal > 0 ? _groupsTotal : null;
            gok ??= gtotal is { } gt0 && bestRow is not null ? (int)Math.Round(bestRow.Row.GroupShare * gt0) : null;
            ResultText = p.Applied ? L.T("router.select.result_applied", new { name = StrategyTitle(best) })
                : !perfect && gok is { } okN && gtotal is { } totN ? L.T("router.select.result_best_partial", new { name = StrategyTitle(best), ok = okN, total = totN })
                : L.T(HasAddressNote || !perfect ? "router.select.result_best" : "router.select.result_ok", new { name = StrategyTitle(best) });
            ResultHint = backups.Count > 0
                ? L.T(p.Applied ? "router.select.hint_backups_applied" : "router.select.hint_backups_apply", new { list = string.Join(", ", backups) })
                : p.Applied ? L.T("router.select.hint_applied") : L.T("router.select.hint_apply");
        }
        else
        {
            SetBest(null, false);
            ResultText = L.T("router.select.none_helped");
            ResultHint = L.T("router.select.none_hint");
        }
        // «Искать глубже» — только когда готовые не помогли (UX-аудит, п. 30), а не равной кнопкой рядом с «Начать подбор».
        OfferDeep = !p.Cancelled && p.Best is null;
    }

    public async Task CancelSelectAsync()
    {
        if (await Guard(L.T("router.select.stop_busy"), () => Call(c => c.SelectCancelAsync())))
            Say(L.T("router.select.stopped"));
    }

    /// <summary>«Применить на роутере» (лучшая не включилась сама) или «Применить выбранную» (другая из итога, `apply`). Запасные — 2 следующие рабочие.</summary>
    public async Task ApplyAsync(string id)
    {
        var backups = Scores.Where(s => s.Usable && s.Id != id).Select(s => s.Id).Take(2).ToList();
        if (await TrialAsync(L.T("router.apply.busy"), (c, confirmS) => c.ApplyAsync(id, backups, confirmS), L.T("router.apply.done", new { name = StrategyTitle(id) })))
        {
            SetBest(_best, true);
            foreach (var s in Scores) s.IsBest = s.Id == id;
            ResultText = L.T("router.apply.result_applied", new { name = StrategyTitle(id) });
            ResultHint = backups.Count > 0 ? L.T("router.apply.backups", new { list = string.Join(", ", backups.Select(StrategyTitle)) }) : "";
        }
    }

    // ---------- свои сайты и списки роутера ----------

    bool _targetsOnRouter;
    public bool TargetsOnRouter
    {
        get => _targetsOnRouter;
        set
        {
            if (!Set(ref _targetsOnRouter, value)) return;
            Raise(nameof(TargetsOnPc)); Raise(nameof(TargetsOnRouterVisible)); Raise(nameof(TargetsOnPcVisible));
            if (value && !_targetsLoaded) _ = LoadTargetsAsync();
            if (value) _ = Advanced.RefreshAsync();
        }
    }
    public bool TargetsOnPc { get => !TargetsOnRouter; set => TargetsOnRouter = !value; }
    public bool TargetsOnRouterVisible => Connected && TargetsOnRouter;
    public bool TargetsOnPcVisible => !TargetsOnRouterVisible;

    public ObservableCollection<RouterTargetRow> Targets { get; } = [];
    public const int MaxTargets = 50;

    string _listGeneral = "", _listExclude = "", _targetsMessage = "";
    public string ListGeneralText { get => _listGeneral; set { if (Set(ref _listGeneral, value)) RaiseSites(); } }
    public string ListExcludeText { get => _listExclude; set { if (Set(ref _listExclude, value)) RaiseSites(); } }
    public string TargetsMessage { get => _targetsMessage; private set => Set(ref _targetsMessage, value); }

    bool _targetsLoaded, _listsLoaded;

    // Один статус «Не сохранено / Сохранено» на вкладку «Сайты» (UX-аудит 6, п. 2–3): адреса, списки и строки «Свои настройки для сайтов».
    string _sitesSaved = "";
    string SitesSignature() => string.Join("\n", Targets.Select(t => t.Url.Trim() + "|" + (t.Important ? 1 : 0))) + "\u0001" + ListGeneralText + "\u0001" + ListExcludeText;
    public bool SitesDirty => (_targetsLoaded && SitesSignature() != _sitesSaved) || Advanced.Dirty;
    public bool SitesSaved => !SitesDirty;
    void RaiseSites() { Raise(nameof(SitesDirty)); Raise(nameof(SitesSaved)); }
    void MarkSitesSaved() { _sitesSaved = SitesSignature(); RaiseSites(); }

    void WatchSites()
    {
        Targets.CollectionChanged += (_, e) =>
        {
            foreach (var r in e.NewItems?.OfType<RouterTargetRow>() ?? []) r.PropertyChanged += (_, _) => RaiseSites();
            RaiseSites();
        };
        Advanced.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AdvancedViewModel.Dirty)) RaiseSites(); };
    }

    public ICommand AddTargetCmd { get; }
    public ICommand RemoveTargetCmd { get; }
    public ICommand CopyFromPcCmd { get; }
    public ICommand SaveTargetsCmd { get; }

    public async Task LoadTargetsAsync()
    {
        await Guard(L.T("router.targets.reading"), async () =>
        {
            var t = await Call(c => c.TargetsGetAsync());
            Targets.Clear();
            foreach (var x in t.Items) Targets.Add(new(x.Url, x.Important));
            _targetsLoaded = true;
            ListGeneralText = string.Join(Environment.NewLine, (await Call(c => c.ListsGetAsync("user-general"))).Lines);
            ListExcludeText = string.Join(Environment.NewLine, (await Call(c => c.ListsGetAsync("user-exclude"))).Lines);
            _listsLoaded = true;
            TargetsMessage = L.T("router.targets.count", new { count = Targets.Count, max = MaxTargets });
            MarkSitesSaved();
        }, changes: false);
        Say("");
    }

    void AddTarget()
    {
        if (Targets.Count >= MaxTargets) { TargetsMessage = L.T("router.targets.max", new { max = MaxTargets }); return; }
        Targets.Add(new("", false));
    }

    /// <summary>«Скопировать с ПК»: свои сайты этого ПК, которых ещё нет на роутере (до 50).</summary>
    public void CopyFromPc()
    {
        var have = Targets.Select(t => t.Url.Trim().TrimEnd('/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int added = 0, skipped = 0;
        foreach (var d in PcUserTargets())
        {
            var url = RouterTexts.ToUrl(d);
            if (!have.Add(url.TrimEnd('/'))) continue;
            if (Targets.Count >= MaxTargets) { skipped++; continue; }
            Targets.Add(new(url, false));
            added++;
        }
        TargetsMessage = PcUserTargets().Count == 0 ? L.T("router.targets.pc_empty")
            : L.T(skipped > 0 ? "router.targets.added_skipped" : "router.targets.added", new { added, skipped });
    }

    public async Task SaveTargetsAsync()
    {
        var items = Targets.Where(t => t.Url.Trim().Length > 0)
            .Select(t => new RouterTarget(RouterTexts.ToUrl(t.Url), t.Important))
            .DistinctBy(t => t.Url, StringComparer.OrdinalIgnoreCase).ToList();
        if (items.Count > MaxTargets) { TargetsMessage = L.T("router.targets.over_max", new { max = MaxTargets, count = items.Count }); return; }
        static List<string> Lines(string text) => text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (await Guard(L.T("router.targets.saving"), async () =>
            {
                try
                {
                    // Не загружали — не трогаем: иначе пустое поле стёрло бы то, что уже есть на роутере.
                    if (_targetsLoaded) await Call(c => c.TargetsSetAsync(items));
                    if (_listsLoaded)
                    {
                        await Call(c => c.ListsSetAsync("user-general", Lines(ListGeneralText)));
                        await Call(c => c.ListsSetAsync("user-exclude", Lines(ListExcludeText)));
                    }
                }
                catch (RouterException e) when (e.AppError?.Code == "invalid" || e.Failure == RouterFailure.InvalidArgs)
                {
                    // field — путь к значению (§4): items[3].url → какой именно адрес.
                    throw new RouterException(e.Failure,
                        RouterTexts.Index(e.AppError?.Field) is { } i && i < items.Count && e.AppError!.Field!.StartsWith("items")
                            ? L.T("router.targets.rejected_url", new { url = items[i].Url })
                            : L.T("router.targets.rejected_line"), e.AppError);
                }
            }))
        {
            Targets.Clear();
            foreach (var t in items) Targets.Add(new(t.Url, t.Important));
            TargetsMessage = L.T("router.targets.saved", new { count = items.Count });
            MarkSitesSaved();
            Say(TargetsMessage);
            // Строки «Свои настройки для сайтов» — той же кнопкой: у вкладки одно «Сохранить» (применение пробное, как у переключателя).
            if (Advanced.Dirty && Advanced.Available) await Advanced.ApplyAsync();
        }
        else TargetsMessage = L.T("router.targets.not_saved");
    }

    // ---------- вкладка «Роутер» ----------

    /// <summary>Открыли вкладку «Роутер»: DNS и «Роутер: что мешает» — свежим (статус уже есть).</summary>
    public async Task RefreshRouterPageAsync()
    {
        // Первый заход на вкладку за сеанс: роутер не подключён и ещё не искали — один раз ищем сами, как по «Найти».
        if (!Connected && !Searched && !_autoFindTried && !Busy)
        {
            _autoFindTried = true; Raise(nameof(EmptyTitle));
            try { await ConnectAsync(null); }
            catch (RouterException) { /* поиск не удался — «Найти» остаётся вручную */ }
        }
        if (!Connected || Busy) return;
        await Guard(L.T("router.dns.reading"), async () =>
        {
            await LoadDnsPresetsAsync();
            ShowDns(await Call(c => c.DnsStatusAsync()));
        }, changes: false);
        Say("");
        await RefreshDiagAsync();
    }

    // ---------- «Сайт не открывается» (site_check) ----------

    string _siteUrl = "", _siteResult = "", _siteHint = "", _siteDetail = "";
    bool _siteSuggest, _siteChecking;
    string? _siteHost;
    public string SiteUrl { get => _siteUrl; set => Set(ref _siteUrl, value); }
    public string SiteResult { get => _siteResult; private set { if (Set(ref _siteResult, value)) Raise(nameof(HasSiteResult)); } }
    public bool HasSiteResult => SiteResult.Length > 0;
    public string SiteHint { get => _siteHint; private set => Set(ref _siteHint, value); }
    /// <summary>Что именно ответил сайт с обходом и без — под «Подробнее».</summary>
    public string SiteDetail { get => _siteDetail; private set { if (Set(ref _siteDetail, value)) Raise(nameof(HasSiteDetail)); } }
    public bool HasSiteDetail => SiteDetail.Length > 0;
    /// <summary>Открывается только без обхода — кнопка «Добавить в исключения».</summary>
    public bool SiteSuggestExclude { get => _siteSuggest; private set { if (Set(ref _siteSuggest, value)) Requery(); } }
    public bool SiteChecking { get => _siteChecking; private set => Set(ref _siteChecking, value); }
    public ICommand SiteCheckCmd { get; }
    public ICommand AddExcludeCmd { get; }

    /// <summary>Роутер проверяет адрес с обходом и без; ждём site_check_result.running == false.</summary>
    public async Task SiteCheckAsync()
    {
        var url = SiteUrl.Trim();
        if (RouterTexts.Host(url) is not { } host) { SiteResult = L.T("router.site.enter"); SiteHint = ""; SiteDetail = ""; SiteSuggestExclude = false; return; }
        RouterSiteCheckResult? res = null;
        bool progressShown = false;
        SiteSuggestExclude = false;
        if (!await Guard(L.T("router.site.busy"), async () =>
            {
                SiteChecking = true;
                SiteResult = L.T("router.site.checking", new { host }); progressShown = true; SiteHint = ""; SiteDetail = "";
                try
                {
                    await Call(c => c.SiteCheckAsync(url));
                    var deadline = DateTime.UtcNow + CheckTimeout;
                    while (DateTime.UtcNow < deadline)
                    {
                        await Task.Delay(PollInterval);
                        var r = await Call(c => c.SiteCheckResultAsync());
                        if (!r.Running) { res = r; break; }
                    }
                }
                finally { SiteChecking = false; }
            }, changes: false))
        {
            if (progressShown) SiteResult = "";
            return;
        }
        Say("");
        ShowSiteResult(host, res);
    }

    void ShowSiteResult(string host, RouterSiteCheckResult? r)
    {
        _siteHost = host;
        if (r is null) { SiteResult = L.T("router.site.pending"); SiteHint = ""; SiteDetail = ""; return; }
        bool with = r.WithBypass?.Ok == true, without = r.WithoutBypass?.Ok == true;
        (SiteResult, SiteHint) = (with, without) switch
        {
            (true, _) => (L.T("router.site.opens"), L.T("router.site.opens_hint")),
            (false, true) => (L.T("router.site.only_without"), L.T("router.site.only_without_hint")),
            _ => (L.T("router.site.closed"), L.T("router.site.closed_hint")),
        };
        SiteSuggestExclude = r.SuggestExclude || (!with && without);
        static string Side(RouterSiteCheckSide? s) => s is null ? L.T("router.site.side_none") : s.Ok ? L.T("router.site.side_ok") : s.Shown is { Length: > 0 } d ? L.T("router.site.side_fail_detail", new { detail = d }) : L.T("router.site.side_fail");
        SiteDetail = L.T("router.site.detail", new { @with = Side(r.WithBypass), without = Side(r.WithoutBypass) });
    }

    /// <summary>«Добавить в исключения»: хост — в user-exclude роутера (lists_set), не трогая остальные строки.</summary>
    public async Task AddSiteToExcludeAsync()
    {
        if (_siteHost is not { } host) return;
        if (await AddHostToExcludeAsync(host))
        {
            SiteSuggestExclude = false;
            SiteResult = L.T("router.site.excluded", new { host });
            SiteHint = L.T("router.site.excluded_hint");
            Say(SiteResult);
        }
    }

    /// <summary>«Открыть без оптимизации» с Главной (UX-аудит 7, п. 5): сайт — в «Исключения» роутера. false — не вышло (нет входа, роутер без lists_set).</summary>
    public async Task<bool> ExcludeHostAsync(string host) => Can("lists_set") && await AddHostToExcludeAsync(host);

    async Task<bool> AddHostToExcludeAsync(string host)
    {
        // Тот же список, что поле «Исключения» ниже: если оно загружено — берём его строки (с несохранёнными правками), иначе читаем с роутера.
        var wasDirty = SitesDirty;
        if (await Guard(L.T("router.exclude.busy"), async () =>
            {
                var lines = _listsLoaded
                    ? ListExcludeText.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList()
                    : (await Call(c => c.ListsGetAsync("user-exclude"))).Lines.ToList();
                if (!lines.Contains(host, StringComparer.OrdinalIgnoreCase)) lines.Add(host);
                await Call(c => c.ListsSetAsync("user-exclude", lines));
                if (_listsLoaded) ListExcludeText = string.Join(Environment.NewLine, lines);
            }))
        {
            if (!wasDirty) MarkSitesSaved();
            return true;
        }
        return false;
    }

    // ---------- DNS для всей сети (dns_status, dns_analyze / dns_enable / dns_restore) ----------

    RouterDnsStatus? _dnsStatus;
    string _dnsState = "", _dnsHint = "", _dnsJob = "";
    bool _dnsRunning;
    public bool DnsLoaded => _dnsStatus is not null;
    /// <summary>Защищённый DNS включён для всей сети — кнопка «Вернуть как было».</summary>
    public bool DnsProtected => _dnsStatus?.Mode == "doh";
    public bool DnsCanEnable => !DnsProtected;
    public string DnsState { get => _dnsState; private set => Set(ref _dnsState, value); }
    public string DnsHint { get => _dnsHint; private set { if (Set(ref _dnsHint, value)) Raise(nameof(HasDnsHint)); } }
    public bool HasDnsHint => DnsHint.Length > 0;
    /// <summary>Ход и итог последнего DNS-задания (установка занимает 10–20 с).</summary>
    public string DnsJobText { get => _dnsJob; private set { if (Set(ref _dnsJob, value)) Raise(nameof(HasDnsJob)); } }
    public bool HasDnsJob => DnsJobText.Length > 0;
    public bool DnsRunning { get => _dnsRunning; private set => Set(ref _dnsRunning, value); }
    /// <summary>Под «Подробнее»: серверы и время ответа.</summary>
    public ObservableCollection<string> DnsServers { get; } = [];
    bool _dnsDetailsOpen;
    /// <summary>«Подробнее» у DNS сети раскрыт: открывается сам после «Проверить» — итог там.</summary>
    public bool DnsDetailsOpen { get => _dnsDetailsOpen; set => Set(ref _dnsDetailsOpen, value); }
    public ICommand DnsCheckCmd { get; }
    public ICommand DnsEnableCmd { get; }
    public ICommand DnsRestoreCmd { get; }

    void ShowDns(RouterDnsStatus d)
    {
        _dnsStatus = d;
        if (d.Mode == "doh")
        {
            DnsState = d.FallbackActive ? L.T("router.dns.unavailable") : L.T("router.dns.on");
            DnsHint = d.FallbackActive ? L.T("router.dns.fallback_hint") : "";
        }
        else if (d.CheckedAt is null) { DnsState = L.T("router.dns.not_checked"); DnsHint = L.T("router.dns.press_check"); }
        else if (d.Spoofed) { DnsState = L.T("router.dns.spoofed_at", new { time = RouterTexts.Time(d.CheckedAt) }); DnsHint = L.T("router.dns.spoofed_hint"); }
        else { DnsState = L.T("router.dns.clean_at", new { time = RouterTexts.Time(d.CheckedAt) }); DnsHint = ""; }
        DnsServers.Clear();
        foreach (var s in d.Servers ?? []) DnsServers.Add(!s.DohOk ? L.T("router.dns.server_fail", new { name = s.Name })
            : s.Ms is { } ms ? L.T("router.dns.server_ok_ms", new { name = s.Name, ms }) : L.T("router.dns.server_ok", new { name = s.Name }));
        DnsJobText = d.Job switch
        {
            null => "",
            { Running: true } j => L.T("router.dns.job.running", new { action = JobTitle(j.Action) }),
            { Ok: false } j => j.Message is { Length: > 0 } m
                ? L.T("router.dns.job.failed_msg", new { action = JobTitle(j.Action), message = m })
                : L.T("router.dns.job.failed", new { action = JobTitle(j.Action) }),
            _ => "",
        };
        // Выбор вариантов (dns_presets): включённый, «Вернуть как было» — когда DNS не как у провайдера.
        var untouched = DnsPick.SelectionUntouched;
        if (d.Preset == "custom" && d.Custom is { } own) DnsPick.CurrentId = DnsPicker.CustomId(own.Title);
        else if (d.Preset is { } p) DnsPick.CurrentId = p;
        if (untouched && d.Spoofed && d.CheckedAt is not null && d.Mode != "doh" && d.Preset == "provider") DnsPick.PreselectRecommended(MainViewModel.RecommendedDns);
        DnsPick.CanRestore = d.Mode is "doh" or "plain" || d.Preset is { } q && q != "provider";
        var title = d.Preset == "custom" ? d.Custom?.Title ?? L.T("router.dns.custom") : DnsPick.Options.FirstOrDefault(o => o.Id == d.Preset)?.Title;
        DnsPick.CurrentText = title is null ? "" : d.Mode switch
        {
            "doh" => L.T("router.dns.now_doh", new { title }),
            "plain" => L.T("router.dns.now_plain", new { title }),
            _ => L.T("router.dns.now", new { title }),
        };
        Raise(nameof(DnsLoaded)); Raise(nameof(DnsProtected)); Raise(nameof(DnsCanEnable));
        Requery();
    }

    // ---------- варианты DNS для всей сети (dns_presets / dns_set, §9 2026-10-17) ----------

    /// <summary>Выбор DNS — тот же, что для этого компьютера: готовые с роутера + свои из window.json.</summary>
    public DnsPicker DnsPick { get; }

    IReadOnlyList<RouterDnsPreset> _dnsPresetList = [];
    bool _dnsPresets;
    /// <summary>Роутер знает dns_presets: выбор вариантов вместо одной кнопки «Включить защищённый DNS».</summary>
    public bool DnsPresetsAvailable { get => _dnsPresets; private set { if (Set(ref _dnsPresets, value)) Raise(nameof(DnsSingleButton)); } }
    public bool DnsSingleButton => !DnsPresetsAvailable;

    async Task LoadDnsPresetsAsync()
    {
        if (DnsPresetsAvailable) return;
        RouterDnsPresets p;
        try { p = await Call(c => c.DnsPresetsAsync()); }
        catch (RouterException e) when (e.Failure is RouterFailure.NotSupported) { return; }   // старый пакет на роутере — одна кнопка
        if (p.Presets is not { Count: > 0 } list) return;
        _dnsPresetList = list;
        DnsPick.PresetSource = () => _dnsPresetList.Select(ToOption).ToList();
        DnsPick.SetPresets(list.Select(ToOption).ToList());
        DnsPresetsAvailable = true;
    }

    static DnsOption ToOption(RouterDnsPreset p)
    {
        // Названия и заметки известных вариантов — по ключам окна (роутер присылает их на своём языке), текст роутера — только для незнакомого id.
        var known = p.Id == "provider" ? null : DnsPresets.Find(p.Id);
        var title = p.Id == "provider" ? L.T("web.dns.preset.provider") : known?.Title ?? p.Title;
        var note = known is not null ? known.Note : p.Note;
        var ips = p.Resolvers is { Count: > 0 } r ? r.SelectMany(x => x.Bootstrap ?? []).Distinct().ToList() : p.Plain ?? [];
        var detail = p.Id == "provider" ? L.T("router.dns.opt.provider")
            : p.Resolvers is { Count: > 0 } ? (p.Resolvers.Count > 1 ? L.T("router.dns.opt.secure_n", new { count = p.Resolvers.Count }) : L.T("router.dns.opt.secure"))
            : L.T("router.dns.opt.plain", new { ips = string.Join(", ", ips) });
        if (note is { Length: > 0 } n) detail += " · " + n;
        // Описание: адреса и шаблоны — с роутера; кто держит — из пресетов ПК по тому же id (у «Google + Cloudflare» — оба).
        var dohs = (p.Resolvers ?? []).Select(x => x.Doh).OfType<string>().ToList();
        var owner = DnsPresets.Find(p.Id)?.Owner ?? (p.Id == "default" ? L.T("router.dns.owner_default") : null);
        return new DnsOption(p.Id, title, detail, false, ips, dohs.FirstOrDefault(), dohs, owner, MainViewModel.PresetNote(p.Id, note));
    }

    /// <summary>«Применить» в выборе DNS роутера: готовый — dns_set preset, свой — dns_set custom (адреса — bootstrap при DoH, иначе plain).</summary>
    public async Task DnsApplyAsync(DnsOption o)
    {
        if (o.Id != "provider" && !Confirm(L.T("router.dns.apply_confirm", new { title = o.Title })))
            return;
        var done = L.T("router.dns.set_done", new { title = o.Title });
        var change = L.T("router.change.dns", new { name = o.Title });
        if (o.Custom)
        {
            var custom = new RouterDnsCustom(o.Title, o.Doh, o.Doh is null ? null : [.. o.Ips], o.Doh is null ? [.. o.Ips] : null);
            await DnsJobAsync("dns_set", c => c.DnsSetCustomAsync(custom), done, "custom", change);
        }
        else await DnsJobAsync("dns_set", c => c.DnsSetAsync(o.Id), done, o.Id, change);
    }

    static string JobTitle(string? action) => action switch
    {
        "dns_set" => L.T("router.dns.job.dns_set"),
        "dns_enable" => L.T("router.dns.job.dns_enable"),
        "dns_restore" => L.T("router.dns.job.dns_restore"),
        _ => L.T("router.dns.job.dns_analyze"),
    };

    public Task DnsAnalyzeAsync() => DnsJobAsync("dns_analyze", c => c.DnsAnalyzeAsync(), L.T("router.dns.analyze_done"));

    public Task DnsEnableAsync()
    {
        if (!Confirm(L.T("router.dns.enable_confirm")))
            return Task.CompletedTask;
        return DnsJobAsync("dns_enable", c => c.DnsEnableAsync(), L.T("router.dns.enable_done"), changeText: L.T("router.change.dns_enable"));
    }

    public Task DnsRestoreAsync() => DnsJobAsync("dns_restore", c => c.DnsRestoreAsync(), L.T("router.dns.restore_done"));

    /// <summary>Фоновое DNS-задание: запустить и опрашивать dns_status, пока job этого действия не закончится.</summary>
    async Task DnsJobAsync(string action, Func<RouterClient, Task<RouterStarted>> start, string done, string? preset = null, string? changeText = null)
    {
        RouterDnsJob? job = null;
        // Журнал окна: DNS сети меняем — запись ДО команды роутеру; «как было» закрывает записи.
        string rec = "";
        var after = new JsonObject { ["preset"] = preset };
        bool changes = action is "dns_set" or "dns_enable";
        if (changes && Connected) rec = BeginRouter(ChangeKinds.RouterDns, changeText ?? done, after);
        try { await DnsJobCoreAsync(action, start, done, r => job = r); }
        finally
        {
            if (changes && rec.Length > 0) { if (job?.Ok == true) Recorder.Done(rec); else Recorder.Abort(rec); }
            else if (action == "dns_restore" && job?.Ok == true) Recorder.Close(ChangeKinds.RouterDns, L.T("router.change.dns_restored"));
        }
    }

    async Task DnsJobCoreAsync(string action, Func<RouterClient, Task<RouterStarted>> start, string done, Action<RouterDnsJob?> found)
    {
        RouterDnsJob? job = null;
        if (!await Guard(L.T("router.dns.job.busy", new { action = JobTitle(action) }), async () =>
            {
                DnsRunning = true;
                DnsPick.Working = true;
                try
                {
                    var before = _dnsStatus?.Job;
                    await Call(start);
                    DnsJobText = L.T("router.dns.job.running", new { action = JobTitle(action) });
                    bool sawRunning = false;
                    var deadline = DateTime.UtcNow + DnsTimeout;
                    while (DateTime.UtcNow < deadline)
                    {
                        await Task.Delay(PollInterval);
                        var d = await Call(c => c.DnsStatusAsync());
                        var j = d.Job;
                        if (j?.Running == true) { sawRunning = true; DnsJobText = L.T("router.dns.job.running", new { action = JobTitle(action) }); continue; }
                        // Закончилось наше: видели, как шло, или у задания новое время (at) — не прошлое такое же.
                        if (j is not null && j.Action == action && (sawRunning || (j.At is not null && j.At != before?.At)))
                        {
                            job = j;
                            found(j);
                            ShowDns(d);
                            if (action == "dns_analyze" && DnsServers.Count > 0) DnsDetailsOpen = true;
                            return;
                        }
                    }
                    DnsJobText = L.T("router.dns.job.pending", new { action = JobTitle(action) });
                }
                finally { DnsRunning = false; DnsPick.Working = false; }
            }))
            return;
        if (job is null) { Say(DnsJobText); return; }
        Say(job.Ok == false ? DnsJobText : done);
    }

    // ---------- диагностика роутера ----------

    public ObservableCollection<RouterConflictRow> Conflicts { get; } = [];

    // Ответ diag (данные) и время: тексты строк собираются при показе — на языке окна.
    RouterDiag? _diag;
    DateTime _diagAt;
    public string FlowOffloadText => _diag is not { } d ? "" : !d.FlowOffload
        ? (d.Flowoffload is "software" or "hardware" ? L.T("router.diag.flow_handed") : L.T("router.diag.flow_off"))
        : FlowOffloadFixable ? L.T("router.diag.flow_on_bad") : L.T("router.diag.flow_on");
    public string Ipv6Text => _diag is not { } d ? "" : !d.Ipv6Wan ? L.T("router.diag.ipv6_none") : Ipv6Fixable ? L.T("router.diag.ipv6_bad") : L.T("router.diag.ipv6_yes");
    // Время проверки DNS — одно, в карточке «DNS для всей сети»; здесь только итог.
    public string DnsText => _diag is not { } d ? "" : d.Dns is null ? L.T("router.diag.dns_not_checked") : d.Dns.Spoofed ? L.T("router.diag.dns_spoofed") : L.T("router.diag.dns_clean");
    public string SpaceText => _diag is not { } d ? "" : L.T(d.OverlayFreeKb < 2048 ? "router.diag.space_low" : "router.diag.space_value", new { value = (d.OverlayFreeKb / 1024.0).ToString("0.#", L.Culture) });
    public string DiagUpdated => _diag is null ? "" : L.T("router.diag.updated", new { time = L.Time(_diagAt) });

    void FillConflicts()
    {
        Conflicts.Clear();
        foreach (var c in _diag?.Conflicts ?? [])
            Conflicts.Add(c.Kind == "package"
                // §9: пакет conflict_stop не останавливает — только удаление.
                ? new(c.Name, L.T("router.diag.package"), CanStop: false)
                : new(c.Name, c.Kind == "service"
                    ? L.T(c.Running ? "router.diag.service_running" : "router.diag.service_stopped")
                    : L.T(c.Running ? "router.diag.kind_running" : "router.diag.kind_stopped", new { kind = c.Kind })));
    }

    bool _diagLoaded;
    public bool DiagLoaded { get => _diagLoaded; private set { if (Set(ref _diagLoaded, value)) { Raise(nameof(ShowDiagHint)); Raise(nameof(DiagHint)); } } }
    public bool ShowDiagHint => !DiagLoaded;
    public string DiagHint => Connected ? L.T("router.diag.hint_check") : NeedLogin ? L.T("router.diag.hint_login") : L.T("router.diag.hint_other", new { text = ConnectionText });

    public ICommand RefreshDiagCmd { get; }
    public ICommand StopConflictCmd { get; }
    public ICommand SaveReportCmd { get; }

    public async Task RefreshDiagAsync()
    {
        if (!Connected || Busy) return;
        if (await Guard(L.T("router.diag.busy"), async () =>
            {
                var d = await Call(c => c.DiagAsync());
                _diag = d; _diagAt = DateTime.Now;
                FillConflicts();
                Raise(nameof(FlowOffloadText)); Raise(nameof(Ipv6Text)); Raise(nameof(DnsText)); Raise(nameof(SpaceText)); Raise(nameof(DiagUpdated));
                DiagLoaded = true;
                await LoadSecurityAsync();
            }, changes: false))
            Say("");
    }

    // ---------- безопасность роутера (справочно, router-api §5.1 security_check, с 0.2.0-r13) ----------
    // Решение владельца (22.10): только текст — что защищено и что можно улучшить, без кнопок; настройки сети окно не меняет.

    RouterSecurityReport? _security;
    /// <summary>Строки собираются при показе — на языке окна (смена языка поднимает все свойства заново).</summary>
    public IReadOnlyList<RouterSecurityRow> SecurityRows => RouterSecurity.Rows(_security);
    public string SecuritySummary => RouterSecurity.Summary(SecurityRows);
    public bool SecurityHasWarn => SecurityRows.Any(r => r.IsWarn);
    /// <summary>Роутер без метода (до r13) или ответ не пришёл — блока нет.</summary>
    public bool SecurityShown => DiagLoaded && _security is not null;
    /// <summary>Ускорение сети включено: в «Безопасности» — только ссылка на «Что мешает», без повтора.</summary>
    public bool SecurityFlowRef => _diag?.FlowOffload == true;
    bool HasSecurityMethod => _status?.Methods?.Contains(RouterSecurity.Method) == true && !Blocked;

    void RaiseSecurity()
    {
        Raise(nameof(SecurityRows)); Raise(nameof(SecuritySummary)); Raise(nameof(SecurityHasWarn)); Raise(nameof(SecurityShown)); Raise(nameof(SecurityFlowRef));
    }

    async Task LoadSecurityAsync()
    {
        // Отказ этого справочного метода диагностику не роняет: блок просто не показывается.
        try { _security = HasSecurityMethod ? await Call(c => c.SecurityCheckAsync()) : null; }
        catch (RouterException) { _security = null; }
        RaiseSecurity();
    }

    public async Task StopConflictAsync(string name)
    {
        if (!Confirm(L.T("router.diag.stop_confirm", new { name }))) return;
        if (await Guard(L.T("router.diag.stop_busy", new { name }), () => Call(c => c.ConflictStopAsync(name))))
        {
            await RefreshDiagAsync();
            Say(L.T("router.diag.stopped", new { name }));
        }
    }

    public async Task SaveReportAsync()
    {
        string? path = null;
        if (await Guard(L.T("router.diag.report_busy"), async () =>
            {
                var text = (await Call(c => c.ReportAsync())).Text;
                path = AskReportPath();
                if (path is not null) await File.WriteAllTextAsync(path, text);
            }, changes: false))
            Say(path is null ? "" : L.T("router.diag.report_saved", new { path }));
    }

    static void Requery()
    {
        // Команды пересчитываются на потоке окна; в тестах окна нет — ничего не делаем.
        if (Application.Current?.Dispatcher is { } d) d.BeginInvoke(CommandManager.InvalidateRequerySuggested);
    }

    /// <summary>Смена языка: состояние роутера (подключение, главная, предупреждения, меню стратегий, DNS, диагностика) собирается заново из данных.</summary>
    protected override void OnLanguageChanged()
    {
        Retranslate(ref _result); Retranslate(ref _resultHint); Retranslate(ref _targetsMessage); Retranslate(ref _progressText);
        ComputeHome();
        FillStrategies();
        FillConflicts();
        DnsPick.RefreshForLanguage();
        if (_dnsStatus is { } d) ShowDns(d);
    }

    public void Dispose()
    {
        _disposed = true;
        _client?.Dispose();

    }
}
