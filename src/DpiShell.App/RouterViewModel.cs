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
    public string Title { get; } = title;
    /// <summary>Вариант глубокого поиска (candidate): в списке стратегий роутера его нет — только посмотреть, применить нельзя.</summary>
    public bool IsCandidate => Row.Candidate;
    public string Kind => IsCandidate ? "проверяется в поиске" : "";
    /// <summary>Как на ПК: «N из M» (M — отмеченных групп); число групп роутер не присылает — берём по адресам в разбивке, нет её — доля.</summary>
    public string Groups => Row is { GroupsOk: { } gok, GroupsTotal: > 0 and var gt } ? $"{gok} из {gt}"   // роутер 0.2.0-r8 присылает сам
        : IsCandidate ? "—"
        : groupsTotal > 0 ? $"{(int)Math.Round(Row.GroupShare * groupsTotal)} из {groupsTotal}" : $"{Row.GroupShare * 100:0} %";
    /// <summary>У варианта поиска доля обязательных адресов его части (ok из total) — тоже процентом, как у остальных строк.</summary>
    public string Share => IsCandidate && Row.Total is > 0 ? $"{(double)(Row.Ok ?? 0) / Row.Total.Value * 100:0} %" : $"{Row.TargetShare * 100:0} %";
    public string Avg => Row.Error is null && Row.AvgS > 0 ? $"{Row.AvgS:0.00} с" : "—";
    public string Error => Row.Error ?? "";
    /// <summary>Пустая колонка выглядит как сбой отрисовки: когда ошибок нет — «нет» серым.</summary>
    public string ErrorShown => Error.Length > 0 ? Error : "нет";
    public bool ErrorIsProblem => Error.Length > 0;
    public bool Usable => !IsCandidate && Row.Error is null && Row.GroupShare > 0;
    public bool IsBest { get => _best; set { if (Set(ref _best, value)) Raise(nameof(Mark)); } }
    public string Mark => IsBest ? "★" : "";

    /// <summary>Результаты по адресам — если роутер их отдаёт (verdicts); старый роутер присылает только доли.</summary>
    public IReadOnlyList<CheckRow> Details => _details ??= (Row.Verdicts ?? []).Select(v =>
    {
        var n = names ?? GroupNames.Empty;
        bool control = n.Control(v.Group);
        return new CheckRow(v.Ok, n.Title(v.Group), v.Url, v.Detail ?? "", v.Seconds is { } sec ? $"{sec:0.0} с" : "",
            !v.Ok && control ? "ломает обычный сайт" : "", !v.Ok && control);
    }).OrderByDescending(r => r.Alert).ToList();
    public bool HasDetails => Row.Verdicts is { Count: > 0 };
    /// <summary>Подсказка про обновление — только у полных проверок: у кандидатов поиска роутер разбивку не присылает.</summary>
    public bool NoDetails => !HasDetails && !IsCandidate;
    /// <summary>Стрелка: есть что раскрыть — разбивка или подсказка.</summary>
    public bool CanExpand => HasDetails || NoDetails;
    public string NoDetailsHint => "Подробности по адресам появятся после обновления роутера.";
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
        "busy" => "Роутер занят: " + (e.AppError.Text is { Length: > 0 } t ? char.ToLower(t[0]) + t[1..] : "идёт подбор или проверка"),
        // §9: enable/select_start при чужом обходе — conflict.
        "conflict" => Conflict,
        _ => e.Message,
    };

    public const string Conflict = "На роутере работает похожая программа — остановите или удалите её: «Роутер» → «Роутер: что мешает».";

    /// <summary>Выключение без человека (§5.1): сторож, кнопка на корпусе, откат неподтверждённого включения.</summary>
    public static bool IsOffCode(string code) => code is "watchdog_off" or "button_off" or "confirm_reverted" or "watchdog_select_failed";

    /// <summary>
    /// Фраза о выключении без человека: by — watchdog | button | revert (last_off.by или код предупреждения), at — когда.
    /// enabled — обход сейчас включён (откат вернул прежнюю стратегию, а не выключил).
    /// </summary>
    public static string Off(string by, DateTime? at, bool enabled = false)
    {
        var when = at is null ? "" : " в " + Time(at);
        return by switch
        {
            "watchdog" or "watchdog_off" => $"Оптимизация на роутере выключена сторожем{when} — с оптимизацией пропал интернет.",
            "watchdog_select_failed" => RouterViewModel.WatchdogSelectText(RouterViewModel.WatchdogSelectState.Failed, null),
            "button" or "button_off" => $"Оптимизация на роутере выключена кнопкой на корпусе{when}",
            _ when enabled => $"Роутер вернул прежнюю стратегию{when}: новую не подтвердили вовремя",
            _ => $"Роутер вернул как было{when}: включение не подтвердили вовремя",
        };
    }

    /// <summary>Сколько осталось до отката: «2:45».</summary>
    public static string Countdown(TimeSpan left)
    {
        var s = Math.Max(0, (int)Math.Ceiling(left.TotalSeconds));
        return $"{s / 60}:{s % 60:D2}";
    }

    /// <summary>Предупреждение status.warnings → строка главной. flow_offload и ipv6 роутер исправляет сам (§9).</summary>
    public static RouterWarningRow Warning(RouterWarning w) => w.Code switch
    {
        "flow_offload" => new(w.Code, "Ускорение сети на роутере мешает оптимизации", "Исправить"),
        "ipv6" => new(w.Code, "Сайты по IPv6 идут мимо оптимизации", "Исправить"),
        "conflict" => new(w.Code, "На роутере работает похожая программа", "Подробнее"),
        _ => new(w.Code, w.Text ?? w.Code),
    };

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
        ? (t.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : t).ToLocalTime().ToString("HH:mm")
        : "";

    public static string Found(RouterFound f, string? version) => f switch
    {
        RouterFound.NotFound => "DpiShell на роутере не найден",
        RouterFound.AppTooOld => "Обновите программу: роутер новее",
        RouterFound.RouterTooOld => "Обновите пакет на роутере",
        _ => $"DpiShell {version} найден — введите пароль роутера",
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
    readonly Dictionary<string, string> _titles = [];
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
        StopRouterCmd = RelayCommand.Async(async () => { if (await DisableAsync()) Say("Оптимизация на роутере выключена — работает этот ПК."); }, () => Can("disable"));
        EnableTimestampsCmd = RelayCommand.Async(() => CheckTimestampsAsync(ask: true), () => !Busy);
    }

    // ---------- связь с окном (в тестах подменяются) ----------

    /// <summary>Строка состояния окна.</summary>
    public Action<string> Say { get; set; } = _ => { };
    /// <summary>Ошибка действия: окно показывает её тостом, а не только в строке состояния (UX-аудит 2, п. 16). По умолчанию — как <see cref="Say"/>.</summary>
    public Action<string>? Fail { get; set; }
    internal void Failed(string text) => (Fail ?? Say)(text);
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
        var d = new Microsoft.Win32.SaveFileDialog { FileName = $"dpishell-router-{DateTime.Now:yyyy-MM-dd_HH-mm}.txt", Filter = "Текст (*.txt)|*.txt" };
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
    public string AutoAddressText => "Автоматически" + (_gateway is { } g ? $" ({g})" : "");

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
    public bool Found => _detection is { Found: not RouterFound.NotFound };

    bool _searched, _autoFindTried;
    /// <summary>Роутер уже искали (хоть раз): до того пустое состояние — «не проверялся», а не «не найден» (UX-аудит 2, п. 10).</summary>
    public bool Searched { get => _searched; private set { if (Set(ref _searched, value)) Raise(nameof(EmptyTitle)); } }
    public string EmptyTitle => Removed ? "DpiShell с роутера удалён" : Searched ? "Роутер с DpiShell не найден" : _autoFindTried ? "Ищу роутер…" : "Роутер не проверялся";

    /// <summary>Инструкция «Как поставить DpiShell на роутер» (п. 9).</summary>
    public const string GuideUrl = "https://github.com/Slavdew/dpishell/blob/master/docs/router/README.md";
    public Action<string> OpenUrl { get; set; } = url => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    public ICommand GuideCmd => _guideCmd ??= new RelayCommand(() =>
    {
        try { OpenUrl(GuideUrl); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Failed("Не удалось открыть браузер: " + e.Message); }
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
            Raise(nameof(TargetsOnRouterVisible)); Raise(nameof(TargetsOnPcVisible));
            if (!value) { Selecting = false; DiagLoaded = false; DnsPresetsAvailable = false; }
            Raise(nameof(DiagHint));
            Requery();
        }
    }

    /// <summary>Роутер найден, но управлять нельзя: нет входа (или версия не та — тогда кнопка «Войти» не поможет).</summary>
    public bool NeedLogin => Found && !Connected;
    public bool CanAct => Connected && !Busy && !Blocked;
    public bool CanConnect => !Busy;

    string _connectionText = "Не проверялось";
    public string ConnectionText { get => _connectionText; private set => Set(ref _connectionText, value); }

    bool _connectionOk;
    public bool ConnectionOk { get => _connectionOk; private set => Set(ref _connectionOk, value); }

    void SetConnection(string text, bool ok = false) { ConnectionText = text; ConnectionOk = ok; Raise(nameof(DiagHint)); UpdateHome(); }

    RouterClient? ClientFor(string address)
    {
        if (_client is not null && string.Equals(_client.Address, address.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) return _client;
        _client?.Dispose();
        _client = null;
        Connected = false;
        try { _client = new RouterClient(address); }
        catch (ArgumentException e) { SetConnection(e.Message); }
        return _client;
    }

    async Task<T> Call<T>(Func<RouterClient, Task<T>> f)
    {
        await _gate.WaitAsync();
        try { return await f(_client ?? throw new RouterException(RouterFailure.Unreachable, "Роутер не выбран")); }
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
        Say("Ищу роутер…");
        try
        {
            _settings.RouterAddress = AddressAuto ? null : CustomAddress.Trim();
            try { _settings.Save(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Failed(Texts.Friendly(e)); }
            if (!await DetectAsync()) return false;
            var typed = noPassword ? "" : string.IsNullOrEmpty(password) ? null : password;
            if (typed is null && !HasSavedPassword)
            {
                SetConnection(RouterTexts.Found(_detection!.Found, _detection.Status?.Version));
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

    /// <summary>status.json без входа. false — роутера нет (блок на главной пропадает).</summary>
    async Task<bool> DetectAsync()
    {
        Searched = true;
        var address = EffectiveAddress();
        if (address is null)
        {
            SetDetection(null);
            SetConnection(AddressAuto ? "Не удалось узнать адрес роутера — укажите его в «Дополнительно»" : "Укажите адрес роутера");
            return false;
        }
        if (ClientFor(address) is null) { SetDetection(null); return false; }
        var d = await Call(c => c.DetectAsync());
        SetDetection(d);
        if (d.Found == RouterFound.NotFound)
        {
            Connected = false;
            SetConnection(RouterTexts.Found(d.Found, d.Status?.Version));
            return false;
        }
        // Другая версия договора: войти всё равно можно — доступны состояние, обновление и удаление (§5.4).
        if (d.Found != RouterFound.Ok && !Connected) SetConnection(RouterTexts.Found(d.Found, d.Status?.Version));
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
            SetConnection(typed == "" ? "У роутера есть пароль — введите его" : "Неверный пароль");
            return false;
        }
        catch (RouterException e)
        {
            plain = null;
            Connected = false;
            SetConnection(RouterTexts.Friendly(e));
            return false;
        }
        if (typed is not null) RememberPassword(typed);
        plain = null;
        Connected = true;
        SetConnection($"Подключено, DpiShell {_detection?.Status?.Version}", ok: true);
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
            else if (_detection!.Found == RouterFound.Ok) SetConnection(RouterTexts.Found(RouterFound.Ok, _detection.Status?.Version));
        }
        catch (RouterException e)
        {
            // Тихо: роутер мог пропасть на полпути (смена сети) — блок покажет последнее, следующая проверка уточнит.
            if (e.Failure is RouterFailure.Unreachable or RouterFailure.NoResponse) SetDetection(null);
            else SetConnection(RouterTexts.Friendly(e));
        }
        finally { _refreshing = false; }
    }

    /// <summary>Тихое обновление по таймеру окна — раз в минуту, когда ничего не идёт.</summary>
    public Task TickAsync() =>
        Busy || Selecting || _refreshing || DateTime.Now - _refreshedAt < TimeSpan.FromMinutes(1) ? Task.CompletedTask : RefreshAsync();

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

    public sealed record ScheduleOption(double Hours, string Title);

    /// <summary>Часы — по договору (§5.2, редакция §9): 0.5, 1, 3, 6, 12, 24.</summary>
    public static IReadOnlyList<ScheduleOption> ScheduleOptions { get; } =
    [
        new(0.5, "каждые 30 минут"), new(1, "каждый час"), new(3, "каждые 3 часа"),
        new(6, "каждые 6 часов"), new(12, "каждые 12 часов"), new(24, "раз в сутки"),
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
            if (await Guard(w ? "Включаю сторож на роутере…" : "Выключаю сторож на роутере…", async () =>
                {
                    await Call(c => c.ScheduleSetAsync(enabled, hours, autoSelect, w));
                    await RefreshStatusCoreAsync();
                }))
                Say(w ? WatchdogOnText : "Сторож на роутере выключен.");
            else RaiseWatchdog();
            return;
        }
        bool? wd = WatchdogAvailable ? _watchdog : null;
        if (await Guard("Меняю расписание проверки на роутере…", async () =>
            {
                await Call(c => c.ScheduleSetAsync(enabled, hours, autoSelect, wd));
                await RefreshStatusCoreAsync();
            }))
            Say(!enabled ? "Роутер больше не проверяет сайты сам."
                : "Роутер будет проверять сайты " + (ScheduleOptions.FirstOrDefault(o => o.Hours == hours)?.Title ?? $"раз в {hours} ч")
                  + (autoSelect ? ", а если запасные не помогут — подберёт заново." : "."));
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
        ? "Оптимизация для всех сайтов. Если какой-то сайт сломался — добавьте его в исключения (выше — «Сайт не открывается»)."
        : "Оптимизация только для YouTube, Discord и сайтов из списков.";

    public async Task SetModeAsync(bool all)
    {
        _modeWanted = all;
        RaiseMode();
        try
        {
            if (await Guard("Меняю режим на роутере…", async () =>
                {
                    await Call(c => c.ModeSetAsync(all));
                    await RefreshStatusCoreAsync();
                }))
                Say((all ? "Роутер оптимизирует соединение со всеми сайтами, кроме исключений." : "Роутер оптимизирует соединение только с сайтами из списков.") + " Он сам перепроверит, что всё открывается.");
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

    string _homeTitle = "", _homeDetail = "", _warnings = "", _toggleText = "Включить на роутере";
    public string HomeTitle { get => _homeTitle; private set => Set(ref _homeTitle, value); }
    public string HomeDetail { get => _homeDetail; private set => Set(ref _homeDetail, value); }
    string _homeLine = "";
    /// <summary>Пояснение в строке роутера на главной: первое предупреждение, если есть, иначе <see cref="HomeDetail"/>.</summary>
    public string HomeLine { get => _homeLine; private set => Set(ref _homeLine, value); }
    public string Warnings { get => _warnings; private set { if (Set(ref _warnings, value)) Raise(nameof(HasWarnings)); } }
    public bool HasWarnings => Warnings.Length > 0;
    public string ToggleText { get => _toggleText; private set => Set(ref _toggleText, value); }

    string StrategyTitle(string? id)
    {
        if (id is null) return "не выбрана";
        if (_titles.TryGetValue(id, out var t)) return t;
        // Найденная глубоким поиском на роутере («r-…») — в строках подбора она уже с названием.
        return _shell.Catalog.Find(id)?.Title ?? Scores.FirstOrDefault(s => s.Id == id && s.Title != id)?.Title ?? id;
    }

    void UpdateHome()
    {
        var pub = _detection?.Status;
        bool enabled = RouterEnabled;
        var strategy = _status?.Strategy ?? pub?.Strategy;
        var at = _status?.LastCheck?.At ?? pub?.CheckedAt;
        var ok = _status?.LastCheck?.Ok ?? pub?.LastCheckOk;

        HomeTitle = Selecting ? "На роутере идёт подбор"
            : enabled ? "Оптимизация работает на роутере" : "На роутере оптимизация выключена";
        var parts = new List<string>();
        if (Selecting) parts.Add(SelectProgressText);
        else
        {
            parts.Add("Стратегия " + StrategyTitle(strategy));
            if (Checking) parts.Add("проверяю…");
            else if (enabled && at is not null) parts.Add($"проверено в {RouterTexts.Time(at)}" + (ok == false ? " — не всё открывается" : ""));
        }
        if (Updating) parts.Add("обновляется");
        else if (_status?.Update is { Available: true, Version: { } uv }) parts.Add("доступно обновление " + uv);
        if (Compat is { Level: not RouterCompatLevel.Ok }) parts.Add(CompatNeedsApp ? Compat.Level == RouterCompatLevel.Blocked ? "нужна новая версия программы" : "есть новая версия программы" :"обновите DpiShell на роутере");
        else if (!Connected && _detection?.Found is RouterFound.AppTooOld or RouterFound.RouterTooOld)
            parts.Add(RouterTexts.Found(_detection.Found, null) is var t ? char.ToLowerInvariant(t[0]) + t[1..] : "");
        // Выключение сторожем, кнопкой или откатом — отдельной фразой с «Подробнее» (UpdateOff), не строкой предупреждений.
        UpdateOff();
        var rows = (_status?.Warnings ?? []).Where(w => !RouterTexts.IsOffCode(w.Code) && !(w.Code == "auto_selected" && _wdState == WatchdogSelectState.Found)).Select(RouterTexts.Warning).ToList();
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
            HomeLine = $"доступно обновление {upd} · {HomeLine}";
        Warnings = string.Join(" · ", rows.Select(r => r.Text));
        ToggleText = enabled ? "Выключить на роутере" : "Включить на роутере";
        Raise(nameof(RouterEnabled));
        Raise(nameof(FlowOffloadFixable)); Raise(nameof(Ipv6Fixable));
        RaisePc();
        RememberBypass();
        Changed?.Invoke();
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
                if (await Guard("Исправляю ускорение сети на роутере…", async () =>
                    {
                        await Call(c => c.FlowOffloadFixAsync());
                        await RefreshStatusCoreAsync();
                    }))
                    Say("Готово: ускорение сети работает вместе с оптимизацией; скорость обычно почти не меняется.");
                break;
            case "ipv6":
                if (await Guard("Включаю оптимизацию по IPv6 на роутере…", async () =>
                    {
                        await Call(c => c.Ipv6SetAsync(true));
                        await RefreshStatusCoreAsync();
                    }))
                    Say("Готово: оптимизация работает и для сайтов по IPv6.");
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
    public bool BothOn => RouterEnabled && PcRunning();
    public ICommand StopPcCmd { get; }
    public ICommand StopRouterCmd { get; }

    /// <summary>Окно сообщает, что обход на ПК включился или выключился.</summary>
    public void PcChanged() => RaisePc();

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
    public static string WarningsCount(int n)
    {
        int d = n % 10, h = n % 100;
        var word = d == 1 && h != 11 ? "предупреждение" : d is >= 2 and <= 4 && h is < 12 or > 14 ? "предупреждения" : "предупреждений";
        return $"{n} {word}";
    }

    async Task StopPcForRouterAsync()
    {
        if (await StopPc()) Say("Оптимизация на этом ПК выключена — работает роутер.");
        RaisePc();
    }

    /// <summary>Выключить обход на роутере (например, чтобы включить на ПК). true — выключен.</summary>
    public async Task<bool> DisableAsync()
    {
        if (!RouterEnabled) return true;
        return await Guard("Выключаю оптимизацию на роутере…", async () =>
        {
            await Call(c => c.DisableAsync());
            await RefreshStatusCoreAsync();
        }) && !RouterEnabled;
    }

    /// <summary>Включить обход на роутере обратно (после подбора на ПК).</summary>
    public async Task<bool> EnableAsync()
    {
        if (RouterEnabled) return true;
        return await TrialAsync("Включаю оптимизацию на роутере…", (c, confirmS) => c.EnableAsync(confirmS), done: null);
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
    public const string TimestampsNeed = "Для стратегии роутера этому ПК нужна одна настройка Windows";
    public const string TimestampsOn = "На этом ПК включена настройка Windows, нужная стратегии роутера.";
    public const string TimestampsTerm = "Настройка — временные метки TCP (TCP timestamps). Без неё часть сайтов с этого ПК может не открываться. Включается одной командой, вернуть — «Диагностика».";
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
            else if (ask) Failed("Не удалось включить настройку Windows для роутера. Попробуйте ещё раз или перезагрузите компьютер.");
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

    async Task LoadStrategiesAsync()
    {
        var list = await Call(c => c.StrategiesAsync());
        Strategies.Clear();
        foreach (var s in list.Items)
        {
            _titles[s.Id] = StrategyNaming.DisplayTitle(s.Title, s.Roles); if (s.Roles is not null) _roles[s.Id] = s.Roles;
            Strategies.Add(new(s.Id, _titles[s.Id], StrategyNote(s), s.Roles is { Count: > 0 } roles ? "Параметры по ролям:\n" + StrategyParams.Roles(roles) : ""));
        }
    }

    /// <summary>
    /// «найдена на роутере · ✓ проверено 18.10 в 14:30» — только последняя проверка (если роутер её отдаёт), без истории
    /// «удачных N из M»: прошлые прогоны о сегодняшней сети не говорят.
    /// </summary>
    public static string StrategyNote(RouterStrategyItem s)
    {
        var parts = new List<string>();
        if (s.FoundOnRouter) parts.Add("найдена на роутере");
        else if (s.FromPc) parts.Add("с ПК");
        if (s.Last is { } last)
        {
            var at = last.At is { } t ? $" {t.ToLocalTime():dd.MM} в {t.ToLocalTime():HH:mm}" : "";
            parts.Add(last.Ok ? $"✓ проверено{at}"
                : $"✗ проверено{at}" + (last.Failed is { Count: > 0 } f ? ", не открылось: " + string.Join(", ", f) : ""));
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
        try { CopyText($"{s.Title}\n{s.Params}"); Say("Параметры скопированы: " + s.Title); }
        catch (System.Runtime.InteropServices.ExternalException) { Failed("Не удалось скопировать — буфер обмена занят другой программой."); }
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
        if (Busy) { Say("Подождите: роутер ещё выполняет прошлую команду"); return false; }
        if (!Connected) { Say("Сначала подключитесь к роутеру"); return false; }
        if (Blocked) { Failed(Compat!.Text ?? "Версия DpiShell на роутере не подходит к программе"); return false; }
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
                SetConnection("Неверный пароль или сессия — войдите заново");
            }
            else if (e.Failure == RouterFailure.Unreachable) SetConnection("Роутер недоступен");
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
            if (!Confirm("На этом ПК оптимизация включена. Вместе с роутером она не работает: трафик обрабатывается дважды, и сайты перестают открываться.\n\n"
                         + "Выключить оптимизацию на этом ПК и включить на роутере?")) return;
            if (!await StopPc()) { Failed("Не удалось выключить оптимизацию на этом ПК — на роутере не включаю."); return; }
        }
        if (enable)
        {
            // На пробу (§5.2 confirm_s): проверка с этого ПК решит, оставить ли; без ответа роутер вернёт как было сам.
            // Журнал окна: оптимизация на роутере была выключена — запись ДО включения.
            var rec = Connected ? BeginRouter(ChangeKinds.RouterEnable, "Включена оптимизация на роутере", new JsonObject { ["enabled"] = true }) : "";
            await TrialAsync("Включаю оптимизацию на роутере…", (c, confirmS) => c.EnableAsync(confirmS), "Оптимизация на роутере включена.");
            if (rec.Length > 0) { if (RouterEnabled) Recorder.Done(rec); else Recorder.Abort(rec); }
            return;
        }
        if (await Guard("Выключаю оптимизацию на роутере…", async () =>
            {
                await Call(c => c.DisableAsync());
                await RefreshStatusCoreAsync();
            }))
        {
            Recorder.Close(ChangeKinds.RouterEnable, "оптимизация на роутере выключена кнопкой");
            Say("Оптимизация на роутере выключена.");
        }
    }

    public async Task ChooseStrategyAsync(string id)
    {
        if (id == CurrentStrategyId) return;
        // При включённом обходе смена стратегии — на пробу, как включение (§5.2).
        bool trial = RouterEnabled;
        await TrialAsync("Меняю стратегию на роутере…", (c, confirmS) => c.StrategySetAsync(id, trial ? confirmS : null), "На роутере стратегия: " + StrategyTitle(id) + ".");
    }

    bool _checking;
    public bool Checking { get => _checking; private set { if (Set(ref _checking, value)) UpdateHome(); } }

    public async Task CheckAsync()
    {
        string? result = null;
        if (await Guard("Роутер проверяет сайты…", async () => result = await CheckCoreAsync(), changes: false))
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
                    return st.LastCheck.Ok ? "Роутер проверил: всё открывается."
                        : "Роутер проверил: не открывается " + (bad.Count > 0 ? string.Join(", ", bad) : "часть сайтов") + ".";
                }
            }
            return "Роутер ещё проверяет — результат появится на главной позже.";
        }
        finally { Checking = false; }
    }

    GroupNames? _names;
    GroupNames Names => _names ??= new GroupNames(_shell.DefaultTargets.Groups);

    string GroupTitle(string id) => id switch
    {
        "user" => "свои сайты",
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
            Say("Эта стратегия не найдена среди своих и найденных поиском");
            return;
        }
        // Набор из приложения роутер заменяет целиком (§5.2) — шлём все найденные; название — до 80 символов.
        var items = found.Select(RouterClient.ToRouter).Select(x => x.Title.Length > 80 ? x with { Title = x.Title[..80] } : x).ToList();
        var routerId = RouterClient.ToRouter(strategy).Id;
        string? result = null;
        if (await Guard("Отправляю стратегию на роутер…", async () =>
            {
                try { await Call(c => c.StrategiesUserSetAsync(items)); }
                catch (RouterException e) when (e.Failure == RouterFailure.NotSupported)
                {
                    throw new RouterException(e.Failure, "Роутер не умеет принимать стратегии с ПК — обновите пакет DpiShell на роутере");
                }
                catch (RouterException e) when (e.Failure == RouterFailure.InvalidArgs || e.AppError?.Code == "invalid")
                {
                    throw new RouterException(e.Failure, $"Роутер не принял стратегию «{strategy.Title}»: на нём нет нужных параметров или файлов"
                        + (e.AppError?.Field is { } f ? $" ({f})" : ""), e.AppError);
                }
                _titles[routerId] = strategy.Title;
                var confirmS = RouterEnabled ? TrialArg : null;
                await Call(c => c.StrategySetAsync(routerId, confirmS));
                result = await CheckCoreAsync();
                await LoadStrategiesAsync();
            }))
        {
            Say($"Стратегия «{strategy.Title}» включена на роутере. {result}");
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
    string _addressNote = "";
    /// <summary>«Не открылось ни с одной стратегией: … — похоже на блокировку по адресу…»; пусто, когда таких адресов нет.</summary>
    public string AddressNote { get => _addressNote; private set { if (Set(ref _addressNote, value)) Raise(nameof(HasAddressNote)); } }
    public bool HasAddressNote => _addressNote.Length > 0;

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
            return "✓ Включена на роутере " + (row?.Title ?? StrategyTitle(_best ?? "")) + (row is null ? "" : $" · {row.Groups} · {row.Share}");
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
    public string ApplySelectedText => _selectedScore is { IsBest: true } ? "Применить лучшую" : "Применить выбранную";

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
            if (!await Guard("Запускаю подбор на роутере…", async () =>
                {
                    Scores.Clear(); SelectedScore = null; ResultText = ""; ResultHint = ""; SetBest(null, false); ResultExpanded = false; AddressNote = "";
                    if (!deep) _addressBlock.Clear();   // глубокий поиск продолжает счёт проверок прежнего подбора
                    SelectProgress = 0; SelectProgressText = "Подготовка…";
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
            Say("Подбор уже идёт — показываю ход.");
        }
        else Say(hint ?? "");
        await PollSelectAsync(justStarted: true);
    }

    public const string StartingText = "Запускаю подбор…";

    /// <summary>Сколько после запуска ошибка или пустой ответ select_progress — ещё «Запускаю подбор…» (роутер поднимает подбор не сразу).</summary>
    public TimeSpan SelectStartGrace { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Выбор без «Своих сайтов» (на роутере их нет); ничего не осталось — группы по умолчанию. Экран «Подбор» — следом.</summary>
    (IReadOnlyList<string> Groups, bool Full) WithoutUser((IReadOnlyList<string> Groups, bool Full) o, out string hint)
    {
        var rest = o.Groups.Where(g => g != "user").ToList();
        IReadOnlyList<string> groups = rest.Count > 0 ? rest : DefaultSelectGroups;
        SetSelectGroups(groups);
        hint = "На роутере нет своих сайтов — подбираю без них. Отправить их с ПК: «Сайты» → «На роутере» → «Скопировать с ПК».";
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
        if (!Confirm($"Список своих сайтов на роутере пуст, а на этом компьютере их {pc.Count}.\n\n"
                     + "Отправить свои сайты с компьютера на роутер и подобрать по ним?"))
        {
            Say("Подбор не запущен: на роутере нет своих сайтов. Добавьте их на вкладке «Сайты» → «На роутере».");
            return false;
        }
        var items = pc.Select(d => new RouterTarget(RouterTexts.ToUrl(d), false))
            .DistinctBy(t => t.Url, StringComparer.OrdinalIgnoreCase).Take(MaxTargets).ToList();
        if (!await Guard("Отправляю свои сайты на роутер…", () => Call(c => c.TargetsSetAsync(items)))) return false;
        Targets.Clear();
        foreach (var t in items) Targets.Add(new(t.Url, t.Important));
        _targetsLoaded = true;
        TargetsMessage = $"Сохранено на роутере: адресов — {items.Count}" + (pc.Count > items.Count ? $" (не поместилось: {pc.Count - items.Count})." : ".");
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
        catch (RouterException e) { Failed(RouterTexts.Friendly(e)); ResultText = "Не удалось узнать ход подбора на роутере."; }
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
        < 60 => "осталось меньше минуты",
        var s => $"осталось ~{(int)Math.Ceiling(s.Value / 60.0)} мин",
    };

    int _groupsTotal;

    void ShowProgress(RouterSelectProgress p)
    {
        SelectProgressMax = Math.Max(1, p.Total);
        SelectProgress = Math.Min(p.Index, SelectProgressMax);
        if (p.Running && p.Total == 0 && p.Index == 0 && p.Message is not { Length: > 0 }) SelectProgressText = StartingText;
        else if (p.Running)
        {
            var who = p.StartedBy switch { "page" => "Запущен со страницы роутера. ", "auto" or "timer" or "watchdog" => "Роутер подбирает сам. ", _ => "" };
            var parts = new[] { $"Проверено {p.Index} из {p.Total}" + (p.Message is { Length: > 0 } m ? $": {m}" : ""), Eta(p.EtaS) };
            SelectProgressText = who + string.Join(" · ", parts.Where(x => x.Length > 0));
        }
        else SelectProgressText = "";
        var seen = (p.Rows ?? []).Where(r => !r.Candidate).Select(r => r.Verdicts?.Select(v => v.Group).Distinct().Count() ?? 0).DefaultIfEmpty(0).Max();
        if (seen > 0) _groupsTotal = seen;
        foreach (var r in p.Rows ?? [])
        {
            var i = Scores.ToList().FindIndex(x => x.Id == r.Id);
            var row = new RouterScoreRow(r, StrategyNaming.DisplayTitle(r.Title ?? StrategyTitle(r.Id), r.Roles ?? _roles.GetValueOrDefault(r.Id)), Names, _groupsTotal);
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
        AddressNote = _addressBlock.Note();
        if (p.Cancelled)
        {
            SetBest(null, false);
            ResultText = "Подбор на роутере остановлен.";
            ResultHint = "Роутер вернул всё как было.";
        }
        else if (p.Best is { } best)
        {
            SetBest(best, p.Applied);
            var backups = (p.Backups ?? []).Select(StrategyTitle).ToList();
            ResultText = (p.Applied ? "Включена на роутере: " : "Подходит: ") + StrategyTitle(best);
            ResultHint = (backups.Count > 0 ? "Запасные: " + string.Join(", ", backups) + ". " : "")
                + (p.Applied ? "Можно выбрать другую в таблице." : "Нажмите «Применить на роутере».");
        }
        else
        {
            SetBest(null, false);
            ResultText = "Ни одна стратегия не помогла.";
            ResultHint = "Проверьте свои сайты роутера или поищите глубже — роутер придумает новые стратегии.";
        }
        // «Искать глубже» — только когда готовые не помогли (UX-аудит, п. 30), а не равной кнопкой рядом с «Начать подбор».
        OfferDeep = !p.Cancelled && p.Best is null;
    }

    public async Task CancelSelectAsync()
    {
        if (await Guard("Останавливаю подбор на роутере…", () => Call(c => c.SelectCancelAsync())))
            Say("Подбор на роутере остановлен.");
    }

    /// <summary>«Применить на роутере» (лучшая не включилась сама) или «Применить выбранную» (другая из итога, `apply`). Запасные — 2 следующие рабочие.</summary>
    public async Task ApplyAsync(string id)
    {
        var backups = Scores.Where(s => s.Usable && s.Id != id).Select(s => s.Id).Take(2).ToList();
        if (await TrialAsync("Применяю на роутере…", (c, confirmS) => c.ApplyAsync(id, backups, confirmS), "Включена на роутере: " + StrategyTitle(id) + "."))
        {
            SetBest(_best, true);
            foreach (var s in Scores) s.IsBest = s.Id == id;
            ResultText = "Включена на роутере: " + StrategyTitle(id);
            ResultHint = backups.Count > 0 ? "Запасные: " + string.Join(", ", backups.Select(StrategyTitle)) + "." : "";
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
        }
    }
    public bool TargetsOnPc { get => !TargetsOnRouter; set => TargetsOnRouter = !value; }
    public bool TargetsOnRouterVisible => Connected && TargetsOnRouter;
    public bool TargetsOnPcVisible => !TargetsOnRouterVisible;

    public ObservableCollection<RouterTargetRow> Targets { get; } = [];
    public const int MaxTargets = 50;

    string _listGeneral = "", _listExclude = "", _targetsMessage = "";
    public string ListGeneralText { get => _listGeneral; set => Set(ref _listGeneral, value); }
    public string ListExcludeText { get => _listExclude; set => Set(ref _listExclude, value); }
    public string TargetsMessage { get => _targetsMessage; private set => Set(ref _targetsMessage, value); }

    bool _targetsLoaded, _listsLoaded;

    public ICommand AddTargetCmd { get; }
    public ICommand RemoveTargetCmd { get; }
    public ICommand CopyFromPcCmd { get; }
    public ICommand SaveTargetsCmd { get; }

    public async Task LoadTargetsAsync()
    {
        await Guard("Читаю свои сайты роутера…", async () =>
        {
            var t = await Call(c => c.TargetsGetAsync());
            Targets.Clear();
            foreach (var x in t.Items) Targets.Add(new(x.Url, x.Important));
            _targetsLoaded = true;
            ListGeneralText = string.Join(Environment.NewLine, (await Call(c => c.ListsGetAsync("user-general"))).Lines);
            ListExcludeText = string.Join(Environment.NewLine, (await Call(c => c.ListsGetAsync("user-exclude"))).Lines);
            _listsLoaded = true;
            TargetsMessage = $"На роутере адресов: {Targets.Count} из {MaxTargets}.";
        }, changes: false);
        Say("");
    }

    void AddTarget()
    {
        if (Targets.Count >= MaxTargets) { TargetsMessage = $"На роутере — не больше {MaxTargets} адресов."; return; }
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
        TargetsMessage = PcUserTargets().Count == 0 ? "На этом ПК своих сайтов нет."
            : $"Добавлено с ПК: {added}" + (skipped > 0 ? $", не поместилось: {skipped}" : "") + ". Нажмите «Сохранить на роутере».";
    }

    public async Task SaveTargetsAsync()
    {
        var items = Targets.Where(t => t.Url.Trim().Length > 0)
            .Select(t => new RouterTarget(RouterTexts.ToUrl(t.Url), t.Important))
            .DistinctBy(t => t.Url, StringComparer.OrdinalIgnoreCase).ToList();
        if (items.Count > MaxTargets) { TargetsMessage = $"На роутере — не больше {MaxTargets} адресов, сейчас {items.Count}."; return; }
        static List<string> Lines(string text) => text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (await Guard("Сохраняю на роутере…", async () =>
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
                    var what = RouterTexts.Index(e.AppError?.Field) is { } i && i < items.Count && e.AppError!.Field!.StartsWith("items")
                        ? $"адрес «{items[i].Url}»" : "адрес или строку списка";
                    throw new RouterException(e.Failure, $"Роутер не принял {what} — проверьте, что это домен или https-адрес", e.AppError);
                }
            }))
        {
            Targets.Clear();
            foreach (var t in items) Targets.Add(new(t.Url, t.Important));
            TargetsMessage = $"Сохранено на роутере: адресов — {items.Count}.";
            Say(TargetsMessage);
        }
        else TargetsMessage = "Не сохранено — причина в строке состояния.";
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
        await Guard("Читаю DNS роутера…", async () =>
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
        if (RouterTexts.Host(url) is not { } host) { SiteResult = "Впишите адрес сайта, например example.com"; SiteHint = ""; SiteDetail = ""; SiteSuggestExclude = false; return; }
        RouterSiteCheckResult? res = null;
        SiteSuggestExclude = false;
        if (!await Guard("Роутер проверяет сайт…", async () =>
            {
                SiteChecking = true;
                SiteResult = "Проверяю " + host + "…"; SiteHint = ""; SiteDetail = "";
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
            if (SiteResult.StartsWith("Проверяю")) SiteResult = "";
            return;
        }
        Say("");
        ShowSiteResult(host, res);
    }

    void ShowSiteResult(string host, RouterSiteCheckResult? r)
    {
        _siteHost = host;
        if (r is null) { SiteResult = "Роутер ещё проверяет — попробуйте через минуту."; SiteHint = ""; SiteDetail = ""; return; }
        bool with = r.WithBypass?.Ok == true, without = r.WithoutBypass?.Ok == true;
        (SiteResult, SiteHint) = (with, without) switch
        {
            (true, _) => ("С оптимизацией сайт открывается", "Значит, дело не в оптимизации: попробуйте обновить страницу или другой браузер."),
            (false, true) => ("Сайт открывается только без оптимизации", "Добавьте его в исключения — роутер перестанет его трогать."),
            _ => ("Сайт не открывается и без оптимизации", "Похоже, он не работает сам или недоступен по адресу."),
        };
        SiteSuggestExclude = r.SuggestExclude || (!with && without);
        static string Side(RouterSiteCheckSide? s) => s is null ? "не проверялось" : s.Ok ? "открылся" : "не открылся" + (s.Detail is { Length: > 0 } d ? $" ({d})" : "");
        SiteDetail = $"С оптимизацией: {Side(r.WithBypass)}. Без оптимизации: {Side(r.WithoutBypass)}.";
    }

    /// <summary>«Добавить в исключения»: хост — в user-exclude роутера (lists_set), не трогая остальные строки.</summary>
    public async Task AddSiteToExcludeAsync()
    {
        if (_siteHost is not { } host) return;
        if (await Guard("Добавляю в исключения роутера…", async () =>
            {
                var lines = (await Call(c => c.ListsGetAsync("user-exclude"))).Lines.ToList();
                if (!lines.Contains(host, StringComparer.OrdinalIgnoreCase)) lines.Add(host);
                await Call(c => c.ListsSetAsync("user-exclude", lines));
                if (_listsLoaded) ListExcludeText = string.Join(Environment.NewLine, lines);
            }))
        {
            SiteSuggestExclude = false;
            SiteResult = $"{host} — в исключениях роутера";
            SiteHint = "Роутер больше не применяет к нему оптимизацию.";
            Say(SiteResult + ".");
        }
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
        var at = d.CheckedAt is { } t ? $" · проверено в {RouterTexts.Time(t)}" : "";
        if (d.Mode == "doh")
        {
            DnsState = d.FallbackActive ? "Защищённый DNS сейчас недоступен" : "Защищённый DNS включён для всей сети";
            DnsHint = d.FallbackActive ? "Временно работает DNS провайдера — роутер вернёт защищённый сам." : "";
        }
        else if (d.CheckedAt is null) { DnsState = "DNS провайдера, не проверялся"; DnsHint = "Нажмите «Проверить»."; }
        else if (d.Spoofed) { DnsState = "Провайдер подменяет адреса сайтов" + at; DnsHint = "Включите защищённый DNS — он поможет всем устройствам дома."; }
        else { DnsState = "Подмены нет" + at; DnsHint = ""; }
        DnsServers.Clear();
        foreach (var s in d.Servers ?? []) DnsServers.Add($"{s.Name}: " + (s.DohOk ? "отвечает" + (s.Ms is { } ms ? $", {ms} мс" : "") : "не отвечает"));
        DnsJobText = d.Job switch
        {
            null => "",
            { Running: true } j => JobTitle(j.Action) + "…",
            { Ok: false } j => JobTitle(j.Action) + ": не получилось" + (j.Message is { Length: > 0 } m ? $" — {m}" : ""),
            _ => "",
        };
        // Выбор вариантов (dns_presets): включённый, «Вернуть как было» — когда DNS не как у провайдера.
        var untouched = DnsPick.SelectionUntouched;
        if (d.Preset == "custom" && d.Custom is { } own) DnsPick.CurrentId = DnsPicker.CustomId(own.Title);
        else if (d.Preset is { } p) DnsPick.CurrentId = p;
        if (untouched && d.Spoofed && d.CheckedAt is not null && d.Mode != "doh" && d.Preset == "provider") DnsPick.PreselectRecommended(MainViewModel.RecommendedDns);
        DnsPick.CanRestore = d.Mode is "doh" or "plain" || d.Preset is { } q && q != "provider";
        var title = d.Preset == "custom" ? d.Custom?.Title ?? "свой" : DnsPick.Options.FirstOrDefault(o => o.Id == d.Preset)?.Title;
        DnsPick.CurrentText = title is null ? "" : "Сейчас: " + title + (d.Mode switch { "doh" => ", защищённый", "plain" => ", без шифрования", _ => "" });
        Raise(nameof(DnsLoaded)); Raise(nameof(DnsProtected)); Raise(nameof(DnsCanEnable));
        Requery();
    }

    // ---------- варианты DNS для всей сети (dns_presets / dns_set, §9 2026-10-17) ----------

    /// <summary>Выбор DNS — тот же, что для этого компьютера: готовые с роутера + свои из window.json.</summary>
    public DnsPicker DnsPick { get; }

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
        DnsPick.SetPresets(list.Select(ToOption).ToList());
        DnsPresetsAvailable = true;
    }

    static DnsOption ToOption(RouterDnsPreset p)
    {
        var ips = p.Resolvers is { Count: > 0 } r ? r.SelectMany(x => x.Bootstrap ?? []).Distinct().ToList() : p.Plain ?? [];
        var detail = p.Id == "provider" ? "адреса DNS — от провайдера"
            : p.Resolvers is { Count: > 0 } ? "защищённый" + (p.Resolvers.Count > 1 ? $" · серверов: {p.Resolvers.Count}" : "")
            : string.Join(", ", ips) + " · без шифрования";
        if (p.Note is { Length: > 0 } n) detail += " · " + n;
        // Описание: адреса и шаблоны — с роутера; кто держит — из пресетов ПК по тому же id (у «Google + Cloudflare» — оба).
        var dohs = (p.Resolvers ?? []).Select(x => x.Doh).OfType<string>().ToList();
        var owner = DnsPresets.Find(p.Id)?.Owner ?? (p.Id == "default" ? "Google и Cloudflare (США)" : null);
        return new DnsOption(p.Id, p.Title, detail, false, ips, dohs.FirstOrDefault(), dohs, owner, MainViewModel.PresetNote(p.Id, p.Note));
    }

    /// <summary>«Применить» в выборе DNS роутера: готовый — dns_set preset, свой — dns_set custom (адреса — bootstrap при DoH, иначе plain).</summary>
    public async Task DnsApplyAsync(DnsOption o)
    {
        if (o.Id != "provider" && !Confirm($"Включить DNS «{o.Title}» для всей сети?\n\n"
                + "Все устройства дома будут брать адреса сайтов у него. Фильтры провайдера перестанут действовать. "
                + "Не заработает — роутер вернёт прежний вариант; «Вернуть как было» — DNS провайдера."))
            return;
        var done = $"DNS для всей сети: {o.Title}.";
        if (o.Custom)
        {
            var custom = new RouterDnsCustom(o.Title, o.Doh, o.Doh is null ? null : [.. o.Ips], o.Doh is null ? [.. o.Ips] : null);
            await DnsJobAsync("dns_set", c => c.DnsSetCustomAsync(custom), done, "custom");
        }
        else await DnsJobAsync("dns_set", c => c.DnsSetAsync(o.Id), done, o.Id);
    }

    static string JobTitle(string? action) => action switch
    {
        "dns_set" => "Включаю выбранный DNS",
        "dns_enable" => "Включаю защищённый DNS",
        "dns_restore" => "Возвращаю DNS как было",
        _ => "Проверяю DNS",
    };

    public Task DnsAnalyzeAsync() => DnsJobAsync("dns_analyze", c => c.DnsAnalyzeAsync(), "Роутер проверил DNS.");

    public Task DnsEnableAsync()
    {
        if (!Confirm("Включить защищённый DNS для всей сети?\n\n"
                     + "Все устройства дома будут получать настоящие адреса сайтов — провайдер обычно не подменит их, но может перекрыть сам защищённый DNS. "
                     + "Фильтры провайдера (родительский контроль) перестанут действовать, сайты могут открываться чуть медленнее. "
                     + "Прежние настройки сохранятся: «Вернуть как было» вернёт их."))
            return Task.CompletedTask;
        return DnsJobAsync("dns_enable", c => c.DnsEnableAsync(), "Защищённый DNS включён для всей сети.");
    }

    public Task DnsRestoreAsync() => DnsJobAsync("dns_restore", c => c.DnsRestoreAsync(), "DNS на роутере — как было.");

    /// <summary>Фоновое DNS-задание: запустить и опрашивать dns_status, пока job этого действия не закончится.</summary>
    async Task DnsJobAsync(string action, Func<RouterClient, Task<RouterStarted>> start, string done, string? preset = null)
    {
        RouterDnsJob? job = null;
        // Журнал окна: DNS сети меняем — запись ДО команды роутеру; «как было» закрывает записи.
        string rec = "";
        var after = new JsonObject { ["preset"] = preset };
        bool changes = action is "dns_set" or "dns_enable";
        if (changes && Connected) rec = BeginRouter(ChangeKinds.RouterDns, $"DNS для всей сети на роутере: {done.Replace("DNS для всей сети: ", "").TrimEnd('.')}", after);
        try { await DnsJobCoreAsync(action, start, done, r => job = r); }
        finally
        {
            if (changes && rec.Length > 0) { if (job?.Ok == true) Recorder.Done(rec); else Recorder.Abort(rec); }
            else if (action == "dns_restore" && job?.Ok == true) Recorder.Close(ChangeKinds.RouterDns, "DNS сети возвращён кнопкой «Вернуть как было»");
        }
    }

    async Task DnsJobCoreAsync(string action, Func<RouterClient, Task<RouterStarted>> start, string done, Action<RouterDnsJob?> found)
    {
        RouterDnsJob? job = null;
        if (!await Guard(JobTitle(action) + " на роутере…", async () =>
            {
                DnsRunning = true;
                DnsPick.Working = true;
                try
                {
                    var before = _dnsStatus?.Job;
                    await Call(start);
                    DnsJobText = JobTitle(action) + "…";
                    bool sawRunning = false;
                    var deadline = DateTime.UtcNow + DnsTimeout;
                    while (DateTime.UtcNow < deadline)
                    {
                        await Task.Delay(PollInterval);
                        var d = await Call(c => c.DnsStatusAsync());
                        var j = d.Job;
                        if (j?.Running == true) { sawRunning = true; DnsJobText = JobTitle(action) + "…"; continue; }
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
                    DnsJobText = JobTitle(action) + " — роутер ещё не закончил, итог появится здесь позже.";
                }
                finally { DnsRunning = false; DnsPick.Working = false; }
            }))
            return;
        if (job is null) { Say(DnsJobText); return; }
        Say(job.Ok == false ? DnsJobText : done);
    }

    // ---------- диагностика роутера ----------

    public ObservableCollection<RouterConflictRow> Conflicts { get; } = [];

    string _flow = "", _ipv6 = "", _dns = "", _space = "", _diagUpdated = "";
    public string FlowOffloadText { get => _flow; private set => Set(ref _flow, value); }
    public string Ipv6Text { get => _ipv6; private set => Set(ref _ipv6, value); }
    public string DnsText { get => _dns; private set => Set(ref _dns, value); }
    public string SpaceText { get => _space; private set => Set(ref _space, value); }
    public string DiagUpdated { get => _diagUpdated; private set => Set(ref _diagUpdated, value); }

    bool _diagLoaded;
    public bool DiagLoaded { get => _diagLoaded; private set { if (Set(ref _diagLoaded, value)) { Raise(nameof(ShowDiagHint)); Raise(nameof(DiagHint)); } } }
    public bool ShowDiagHint => !DiagLoaded;
    public string DiagHint => Connected ? "Нажмите «Проверить снова»." : NeedLogin ? "Войдите на роутер («Войти» выше), чтобы увидеть, что ему мешает." : ConnectionText + ".";

    public ICommand RefreshDiagCmd { get; }
    public ICommand StopConflictCmd { get; }
    public ICommand SaveReportCmd { get; }

    public async Task RefreshDiagAsync()
    {
        if (!Connected || Busy) return;
        if (await Guard("Читаю диагностику роутера…", async () =>
            {
                var d = await Call(c => c.DiagAsync());
                Conflicts.Clear();
                foreach (var c in d.Conflicts ?? [])
                    Conflicts.Add(c.Kind == "package"
                        // §9: пакет conflict_stop не останавливает — только удаление.
                        ? new(c.Name, "установлен пакет, он мешает — удалите его в меню роутера: «Система» → «Программное обеспечение» (название может отличаться)", CanStop: false)
                        : new(c.Name, (c.Kind == "service" ? "служба" : c.Kind) + (c.Running ? ", работает" : ", остановлена")));
                FlowOffloadText = !d.FlowOffload
                    ? (d.Flowoffload is "software" or "hardware" ? "передано оптимизации — не мешает" : "выключено")
                    : FlowOffloadFixable ? "включено — мешает оптимизации" : "включено";
                Ipv6Text = !d.Ipv6Wan ? "нет" : Ipv6Fixable ? "есть — сайты по IPv6 идут мимо оптимизации" : "есть";
                DnsText = d.Dns is null ? "не проверялся"
                    : (d.Dns.Spoofed ? "провайдер подменяет адреса сайтов" : "подмены нет") + (d.Dns.CheckedAt is { } at ? $" (проверено в {RouterTexts.Time(at)})" : "");
                SpaceText = $"{d.OverlayFreeKb / 1024.0:0.#} МБ" + (d.OverlayFreeKb < 2048 ? " — мало, обновления могут не поместиться" : "");
                DiagLoaded = true;
                DiagUpdated = $"обновлено в {DateTime.Now:HH:mm:ss}";
            }, changes: false))
            Say("");
    }

    public async Task StopConflictAsync(string name)
    {
        if (!Confirm($"Остановить и отключить «{name}» на роутере? Он перехватывает тот же трафик, что и DpiShell.")) return;
        if (await Guard($"Останавливаю {name} на роутере…", () => Call(c => c.ConflictStopAsync(name))))
        {
            await RefreshDiagAsync();
            Say($"На роутере остановлен: {name}.");
        }
    }

    public async Task SaveReportAsync()
    {
        string? path = null;
        if (await Guard("Получаю отчёт роутера…", async () =>
            {
                var text = (await Call(c => c.ReportAsync())).Text;
                path = AskReportPath();
                if (path is not null) await File.WriteAllTextAsync(path, text);
            }, changes: false))
            Say(path is null ? "" : "Отчёт роутера сохранён: " + path);
    }

    static void Requery()
    {
        // Команды пересчитываются на потоке окна; в тестах окна нет — ничего не делаем.
        if (Application.Current?.Dispatcher is { } d) d.BeginInvoke(CommandManager.InvalidateRequerySuggested);
    }

    public void Dispose()
    {
        _disposed = true;
        _client?.Dispose();

    }
}
