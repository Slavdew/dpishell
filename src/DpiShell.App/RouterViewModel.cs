using System.Collections.ObjectModel;
using System.IO;
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

/// <summary>Стратегия роутера в меню выбора.</summary>
public sealed record RouterStrategyRow(string Id, string Title, string Stats);

/// <summary>Строка таблицы подбора на роутере (select_progress.rows).</summary>
public sealed class RouterScoreRow(RouterSelectRow row, string title) : Observable
{
    bool _best;
    public RouterSelectRow Row { get; } = row;
    public string Id => Row.Id;
    public string Title { get; } = title;
    public string Groups => $"{Row.GroupShare * 100:0} %";
    public string Share => $"{Row.TargetShare * 100:0} %";
    public string Avg => Row.Error is null && Row.AvgS > 0 ? $"{Row.AvgS:0.00} с" : "—";
    public string Error => Row.Error ?? "";
    public bool Usable => Row.Error is null && Row.GroupShare > 0;
    public bool IsBest { get => _best; set { if (Set(ref _best, value)) Raise(nameof(Mark)); } }
    public string Mark => IsBest ? "★" : "";
}

/// <summary>Чужой обход на роутере (diag.conflicts).</summary>
public sealed record RouterConflictRow(string Name, string Detail);

public static class RouterTexts
{
    /// <summary>Ошибка роутера → строка состояния. Тексты уже в исключении; пароля в них нет (RouterClient не кладёт запрос в текст).</summary>
    public static string Friendly(RouterException e) => e.AppError?.Code == "busy"
        ? "Роутер занят: " + (e.AppError.Text is { Length: > 0 } t ? char.ToLower(t[0]) + t[1..] : "идёт подбор или проверка")
        : e.Message;

    /// <summary>Время роутера (UTC) → «ЧЧ:ММ» по часам этого ПК.</summary>
    public static string Time(DateTime? at) => at is { } t
        ? (t.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : t).ToLocalTime().ToString("HH:mm")
        : "";

    public static string Found(RouterFound f, string? version) => f switch
    {
        RouterFound.NotFound => "DpiShell на роутере не найден",
        RouterFound.AppTooOld => "Обновите приложение: роутер новее",
        RouterFound.RouterTooOld => "Обновите пакет на роутере",
        _ => $"DpiShell {version} найден — введите пароль роутера",
    };

    /// <summary>Номер элемента из error.field (§4: «items[3].url» → 3).</summary>
    public static int? Index(string? field) =>
        field is not null && System.Text.RegularExpressions.Regex.Match(field, @"\[(\d+)\]") is { Success: true } m ? int.Parse(m.Groups[1].Value) : null;

    /// <summary>Адрес из поля «Свои адреса» → URL для роутера: домен — https://домен/.</summary>
    public static string ToUrl(string line)
    {
        var s = line.Trim();
        return s.Contains("://") ? s : "https://" + s.TrimEnd('/') + "/";
    }
}

/// <summary>
/// Обход на роутере (docs/07-router-api.md §6): поиск, вход, статус для главной, подбор, свои адреса, диагностика.
/// Все запросы к роутеру — по одному (семафор), асинхронно; действия пользователя — по одному (Busy).
/// Пароль: из поля — только на время входа, на диске — только зашифрованным и только после успешного входа.
/// </summary>
public sealed class RouterViewModel : Observable, IDisposable
{
    readonly Shell _shell;
    readonly WindowSettings _settings;
    readonly SemaphoreSlim _gate = new(1, 1);
    RouterClient? _client;
    readonly Dictionary<string, string> _titles = [];

    public RouterViewModel(Shell shell, WindowSettings settings)
    {
        _shell = shell;
        _settings = settings;
        _auto = string.IsNullOrWhiteSpace(settings.RouterAddress);
        _custom = settings.RouterAddress ?? "";

        LoginCmd = new RelayCommand(() => { Navigate(Pages.Diag); LoginRequested?.Invoke(); });
        ToggleCmd = RelayCommand.Async(ToggleAsync, () => CanAct);
        CheckCmd = RelayCommand.Async(CheckAsync, () => CanAct);
        GoSelectCmd = new RelayCommand(() => { SelectOnRouter = true; Navigate(Pages.Select); }, () => Connected);
        StartSelectCmd = RelayCommand.Async(() => StartSelectAsync(false), () => CanAct && !Selecting);
        StartDeepCmd = RelayCommand.Async(() => StartSelectAsync(true), () => CanAct && !Selecting);
        StopSelectCmd = RelayCommand.Async(CancelSelectAsync, () => Connected && Selecting && !Busy);
        ApplyCmd = RelayCommand.Async(() => ApplyAsync(_best!), () => CanAct && HasBest);
        ApplySelectedCmd = RelayCommand.Async(() => ApplyAsync(SelectedScore!.Id),
            () => CanAct && !Selecting && SelectedScore is { Usable: true } s && s.Id != CurrentStrategyId);
        SendStrategyCmd = new RelayCommand(p => { if (p is string id) _ = SendStrategyAsync(id); }, _ => CanAct);
        AddTargetCmd = new RelayCommand(AddTarget, () => Connected);
        RemoveTargetCmd = new RelayCommand(p => { if (p is RouterTargetRow r) Targets.Remove(r); });
        CopyFromPcCmd = new RelayCommand(CopyFromPc, () => Connected);
        SaveTargetsCmd = RelayCommand.Async(SaveTargetsAsync, () => CanAct);
        RefreshDiagCmd = RelayCommand.Async(RefreshDiagAsync, () => CanAct);
        StopConflictCmd = new RelayCommand(p => { if (p is string name) _ = StopConflictAsync(name); }, _ => CanAct);
        SaveReportCmd = RelayCommand.Async(SaveReportAsync, () => CanAct);
    }

    // ---------- связь с окном (в тестах подменяются) ----------

    /// <summary>Строка состояния окна.</summary>
    public Action<string> Say { get; set; } = _ => { };
    /// <summary>Стратегии, найденные глубоким поиском на ПК (их хранит помощник).</summary>
    public Func<IReadOnlyList<Strategy>> FoundStrategies { get; set; } = () => [];
    /// <summary>Свои адреса этого ПК (сохранены у помощника).</summary>
    public Func<IReadOnlyList<string>> PcUserTargets { get; set; } = () => [];
    public Func<string, bool> Confirm { get; set; } = text =>
        MessageBox.Show(text, "DpiShell", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    public Func<string?> AskReportPath { get; set; } = () =>
    {
        var d = new Microsoft.Win32.SaveFileDialog { FileName = $"dpishell-router-{DateTime.Now:yyyy-MM-dd_HH-mm}.txt", Filter = "Текст (*.txt)|*.txt" };
        return d.ShowDialog() == true ? d.FileName : null;
    };
    public Action<int> Navigate { get; set; } = _ => { };
    /// <summary>Что подбирать (группы и режим — с экрана «Подбор»); null — нечего, причина уже сказана.</summary>
    public Func<(IReadOnlyList<string> Groups, bool Full)?> SelectOptions { get; set; } = () => (["youtube", "discord"], true);
    public Func<string?> Gateway { get; set; } = RouterClient.DefaultGateway;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan CheckTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>Поменялось то, что видно на главной (найден ли роутер, включён ли на нём обход).</summary>
    public event Action? Changed;
    /// <summary>«Войти» на главной — окно показывает карточку «Роутер» и ставит курсор в пароль.</summary>
    public event Action? LoginRequested;

    // ---------- адрес и вход ----------

    bool _auto;
    public bool AddressAuto { get => _auto; set { if (Set(ref _auto, value)) Raise(nameof(AddressCustom)); } }
    public bool AddressCustom { get => !_auto; set => AddressAuto = !value; }

    string _custom;
    public string CustomAddress { get => _custom; set => Set(ref _custom, value); }

    string? _gateway;
    public string AutoAddressText => "Автоматически — основной шлюз" + (_gateway is { } g ? $" ({g})" : "");

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

    bool _connected;
    /// <summary>Вошли и API совместим: можно управлять.</summary>
    public bool Connected
    {
        get => _connected;
        private set
        {
            if (!Set(ref _connected, value)) return;
            Raise(nameof(NeedLogin)); Raise(nameof(CanAct)); Raise(nameof(ScheduleEditable)); Raise(nameof(SelectOnRouterVisible)); Raise(nameof(SelectOnPcVisible));
            Raise(nameof(TargetsOnRouterVisible)); Raise(nameof(TargetsOnPcVisible));
            if (!value) { Selecting = false; DiagLoaded = false; }
            Raise(nameof(DiagHint));
            Requery();
        }
    }

    /// <summary>Роутер найден, но управлять нельзя: нет входа (или версия не та — тогда кнопка «Войти» не поможет).</summary>
    public bool NeedLogin => Found && !Connected && _detection?.Found == RouterFound.Ok;
    public bool CanAct => Connected && !Busy;
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

    /// <summary>«Подключиться»: адрес из настроек, поиск, вход. Пусто в поле пароля — сохранённый; его нет — только поиск (status.json).</summary>
    public async Task<bool> ConnectAsync(string? password)
    {
        if (Busy) return false;
        Busy = true;
        Say("Ищу роутер…");
        try
        {
            _settings.RouterAddress = AddressAuto ? null : CustomAddress.Trim();
            try { _settings.Save(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Say(Texts.Friendly(e)); }
            if (!await DetectAsync()) return false;
            if (_detection!.Found != RouterFound.Ok) return false;
            var typed = string.IsNullOrEmpty(password) ? null : password;
            if (typed is null && !HasSavedPassword)
            {
                SetConnection(RouterTexts.Found(RouterFound.Ok, _detection.Status?.Version));
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

    bool HasSavedPassword => RouterSecrets.Unprotect(_settings.RouterPassword) is not null;

    /// <summary>status.json без входа. false — роутера нет (блок на главной пропадает).</summary>
    async Task<bool> DetectAsync()
    {
        var address = EffectiveAddress();
        if (address is null)
        {
            SetDetection(null);
            SetConnection(AddressAuto ? "Основной шлюз не найден — укажите адрес роутера" : "Укажите адрес роутера");
            return false;
        }
        if (ClientFor(address) is null) { SetDetection(null); return false; }
        var d = await Call(c => c.DetectAsync());
        SetDetection(d);
        if (d.Found != RouterFound.Ok)
        {
            Connected = false;
            SetConnection(RouterTexts.Found(d.Found, d.Status?.Version));
            return d.Found != RouterFound.NotFound;
        }
        return true;
    }

    void SetDetection(RouterDetection? d)
    {
        _detection = d;
        if (d is null || d.Found == RouterFound.NotFound) { Status = null; Connected = false; }
        Raise(nameof(Detection)); Raise(nameof(Found)); Raise(nameof(NeedLogin));
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
            if (typed is null && _settings.RouterPassword is not null)
            {
                _settings.RouterPassword = null;
                TrySave();
            }
            SetConnection("Неверный пароль");
            return false;
        }
        catch (RouterException e)
        {
            plain = null;
            Connected = false;
            SetConnection(RouterTexts.Friendly(e));
            return false;
        }
        if (typed is not null)
        {
            _settings.RouterPassword = RouterSecrets.Protect(typed);
            TrySave();
        }
        plain = null;
        Connected = true;
        SetConnection($"Подключено, DpiShell {_detection?.Status?.Version}", ok: true);
        if (SelectOnRouterChosen is null) SelectOnRouter = true;   // роутер подключён — подбирать по умолчанию на нём
        try
        {
            await LoadStrategiesAsync();
            await RefreshStatusCoreAsync();
        }
        catch (RouterException e) { Say(RouterTexts.Friendly(e)); }
        return true;
    }

    void TrySave()
    {
        try { _settings.Save(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Say(Texts.Friendly(e)); }
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
            if (!await DetectAsync() || _detection!.Found != RouterFound.Ok) return;
            if (_client!.LoggedIn && Connected) await RefreshStatusCoreAsync();
            else if (HasSavedPassword) await LoginAsync(null);
            else SetConnection(RouterTexts.Found(RouterFound.Ok, _detection.Status?.Version));
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
                _scheduleEnabled = sch.Enabled; _scheduleHours = sch.Hours;
                Raise(nameof(ScheduleEnabled)); Raise(nameof(ScheduleHours)); Raise(nameof(ScheduleEditable));
            }
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
        set { if (value != _scheduleEnabled) _ = SetScheduleAsync(value, _scheduleHours); }
    }

    public bool ScheduleEditable => CanAct && ScheduleEnabled;

    public double ScheduleHours
    {
        get => _scheduleHours;
        set { if (value != _scheduleHours) _ = SetScheduleAsync(_scheduleEnabled, value); }
    }

    public async Task SetScheduleAsync(bool enabled, double hours)
    {
        if (await Guard("Меняю расписание проверки на роутере…", async () =>
            {
                await Call(c => c.ScheduleSetAsync(enabled, hours));
                await RefreshStatusCoreAsync();
            }))
            Say(enabled ? "Роутер будет проверять сайты " + (ScheduleOptions.FirstOrDefault(o => o.Hours == hours)?.Title ?? $"раз в {hours} ч") + "."
                        : "Проверка по расписанию на роутере выключена.");
        else
        {
            // Не получилось — переключатели показывают то, что на роутере.
            Raise(nameof(ScheduleEnabled)); Raise(nameof(ScheduleHours));
        }
    }

    /// <summary>Обход на роутере включён (по status, без входа — по status.json).</summary>
    public bool RouterEnabled => _status?.Enabled ?? _detection?.Status?.Enabled ?? false;

    string _homeTitle = "", _homeDetail = "", _warnings = "", _toggleText = "Включить на роутере";
    public string HomeTitle { get => _homeTitle; private set => Set(ref _homeTitle, value); }
    public string HomeDetail { get => _homeDetail; private set => Set(ref _homeDetail, value); }
    public string Warnings { get => _warnings; private set { if (Set(ref _warnings, value)) Raise(nameof(HasWarnings)); } }
    public bool HasWarnings => Warnings.Length > 0;
    public string ToggleText { get => _toggleText; private set => Set(ref _toggleText, value); }

    string StrategyTitle(string? id)
    {
        if (id is null) return "не выбрана";
        if (_titles.TryGetValue(id, out var t)) return t;
        return _shell.Catalog.Find(id)?.Title ?? id;
    }

    void UpdateHome()
    {
        var pub = _detection?.Status;
        bool enabled = RouterEnabled;
        var strategy = _status?.Strategy ?? pub?.Strategy;
        var at = _status?.LastCheck?.At ?? pub?.CheckedAt;
        var ok = _status?.LastCheck?.Ok ?? pub?.LastCheckOk;

        HomeTitle = Selecting ? "На роутере идёт подбор"
            : enabled ? "Обход делает роутер" : "На роутере обход выключен";
        var parts = new List<string>();
        if (Selecting) parts.Add(SelectProgressText);
        else
        {
            parts.Add("Стратегия " + StrategyTitle(strategy));
            if (Checking) parts.Add("проверяю…");
            else if (enabled && at is not null) parts.Add($"проверено в {RouterTexts.Time(at)}" + (ok == false ? " — не всё открывается" : ""));
        }
        if (_detection?.Found is RouterFound.AppTooOld or RouterFound.RouterTooOld) parts.Add(ConnectionText.ToLowerInvariant()[..1] + ConnectionText[1..]);
        HomeDetail = string.Join(", ", parts.Where(p => p.Length > 0));
        Warnings = string.Join(" · ", (_status?.Warnings ?? []).Select(w => w.Text ?? w.Code));
        ToggleText = enabled ? "Выключить на роутере" : "Включить на роутере";
        Raise(nameof(RouterEnabled));
        Changed?.Invoke();
    }

    /// <param name="resumeSelect">Подбор уже идёт (запущен не нами или до запуска программы) — показать его ход.</param>
    async Task RefreshStatusCoreAsync(bool resumeSelect = true)
    {
        Status = await Call(c => c.StatusAsync());
        if (resumeSelect && _status?.Jobs?.Select == true && !_polling) _ = PollSelectAsync();
    }

    public ObservableCollection<RouterStrategyRow> Strategies { get; } = [];

    async Task LoadStrategiesAsync()
    {
        var list = await Call(c => c.StrategiesAsync());
        Strategies.Clear();
        foreach (var s in list.Items)
        {
            _titles[s.Id] = s.Title;
            Strategies.Add(new(s.Id, s.Title, s.Runs > 0 ? $"удачных проверок: {s.Passed} из {s.Runs}" : ""));
        }
    }

    public string? CurrentStrategyId => _status?.Strategy ?? _detection?.Status?.Strategy;

    public ICommand LoginCmd { get; }
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
    async Task<bool> Guard(string text, Func<Task> work)
    {
        if (Busy) { Say("Подождите: роутер ещё выполняет прошлую команду"); return false; }
        if (!Connected) { Say("Сначала подключитесь к роутеру"); return false; }
        Busy = true;
        Say(text);
        try
        {
            await work();
            return true;
        }
        catch (RouterException e)
        {
            Say(RouterTexts.Friendly(e));
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
            Say(Texts.Friendly(e));
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
        if (await Guard(enable ? "Включаю обход на роутере…" : "Выключаю обход на роутере…", async () =>
            {
                await Call(c => enable ? c.EnableAsync() : c.DisableAsync());
                await RefreshStatusCoreAsync();
            }))
            Say(enable ? "Обход на роутере включён." : "Обход на роутере выключен.");
    }

    public async Task ChooseStrategyAsync(string id)
    {
        if (id == CurrentStrategyId) return;
        if (await Guard("Меняю стратегию на роутере…", async () =>
            {
                await Call(c => c.StrategySetAsync(id));
                await RefreshStatusCoreAsync();
            }))
            Say("На роутере стратегия: " + StrategyTitle(id));
    }

    bool _checking;
    public bool Checking { get => _checking; private set { if (Set(ref _checking, value)) UpdateHome(); } }

    public async Task CheckAsync()
    {
        string? result = null;
        if (await Guard("Роутер проверяет сайты…", async () => result = await CheckCoreAsync()))
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

    string GroupTitle(string id) => id switch
    {
        "user" => "свои адреса",
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
            Say("Эта стратегия не найдена среди найденных поиском");
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
                await Call(c => c.StrategySetAsync(routerId));
                result = await CheckCoreAsync();
                await LoadStrategiesAsync();
            }))
            Say($"Стратегия «{strategy.Title}» включена на роутере. {result}");
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

    string _result = "", _resultHint = "";
    public string ResultText { get => _result; private set { if (Set(ref _result, value)) Raise(nameof(HasResult)); } }
    public bool HasResult => ResultText.Length > 0;
    public string ResultHint { get => _resultHint; private set => Set(ref _resultHint, value); }

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
    public RouterScoreRow? SelectedScore { get => _selectedScore; set { if (Set(ref _selectedScore, value)) Requery(); } }

    public ICommand StartSelectCmd { get; }
    public ICommand StartDeepCmd { get; }
    public ICommand StopSelectCmd { get; }
    public ICommand ApplyCmd { get; }
    public ICommand ApplySelectedCmd { get; }

    void SetBest(string? id, bool applied)
    {
        _best = id; _applied = applied;
        Raise(nameof(Best)); Raise(nameof(Applied)); Raise(nameof(HasBest));
        Requery();
    }

    public async Task StartSelectAsync(bool deep)
    {
        if (Selecting || SelectOptions() is not { } o) return;
        if (!await Guard("Запускаю подбор на роутере…", async () =>
            {
                Scores.Clear(); SelectedScore = null; ResultText = ""; ResultHint = ""; SetBest(null, false);
                SelectProgress = 0; SelectProgressText = "Подготовка…";
                await Call(c => c.SelectStartAsync(o.Groups, o.Full, deep));
            }))
            return;
        Say("");
        await PollSelectAsync();
    }

    bool _polling;

    /// <summary>Ход подбора — select_progress раз в PollInterval, пока running. Подбор мог запустить и не этот ПК (started_by).</summary>
    public async Task PollSelectAsync()
    {
        if (_polling || !Connected) return;
        _polling = true;
        Selecting = true;
        int failures = 0;
        try
        {
            while (Connected)
            {
                RouterSelectProgress p;
                try { p = await Call(c => c.SelectProgressAsync()); failures = 0; }
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
        catch (RouterException e) { Say(RouterTexts.Friendly(e)); ResultText = "Не удалось узнать ход подбора на роутере."; }
        finally
        {
            _polling = false;
            Selecting = false;
            SelectProgressText = "";
        }
        // Только что опросили до конца — не начинать опрос снова, даже если status ещё не обновил jobs.select.
        await RefreshStatusSafeAsync(resumeSelect: false);
    }

    /// <summary>«осталось ~3 мин» по eta_s; null — оценки ещё нет.</summary>
    public static string Eta(int? seconds) => seconds switch
    {
        null => "",
        < 60 => "осталось меньше минуты",
        var s => $"осталось ~{(int)Math.Ceiling(s.Value / 60.0)} мин",
    };

    void ShowProgress(RouterSelectProgress p)
    {
        SelectProgressMax = Math.Max(1, p.Total);
        SelectProgress = Math.Min(p.Index, SelectProgressMax);
        if (p.Running)
        {
            var who = p.StartedBy switch { "page" => "Запущен со страницы роутера. ", "auto" or "timer" => "Роутер подбирает сам. ", _ => "" };
            var parts = new[] { $"Проверено {p.Index} из {p.Total}" + (p.Message is { Length: > 0 } m ? $": {m}" : ""), Eta(p.EtaS) };
            SelectProgressText = who + string.Join(" · ", parts.Where(x => x.Length > 0));
        }
        else SelectProgressText = "";
        foreach (var r in p.Rows ?? [])
        {
            var i = Scores.ToList().FindIndex(x => x.Id == r.Id);
            var row = new RouterScoreRow(r, r.Title ?? StrategyTitle(r.Id));
            if (i < 0) Scores.Add(row); else if (Scores[i].Row != r) Scores[i] = row;
        }
    }

    void Finish(RouterSelectProgress p)
    {
        var ordered = Scores.OrderByDescending(s => s.Usable).ThenByDescending(s => s.Row.GroupShare).ThenByDescending(s => s.Row.TargetShare)
            .ThenBy(s => s.Row.AvgS).ToList();
        Scores.Clear();
        foreach (var s in ordered) { s.IsBest = s.Id == p.Best; Scores.Add(s); }
        SelectProgress = SelectProgressMax;
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
            ResultHint = "Проверьте свои адреса роутера или запустите полный подбор.";
        }
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
        if (await Guard("Применяю на роутере…", async () =>
            {
                await Call(c => c.ApplyAsync(id, backups));
                await RefreshStatusCoreAsync();
            }))
        {
            SetBest(_best, true);
            foreach (var s in Scores) s.IsBest = s.Id == id;
            ResultText = "Включена на роутере: " + StrategyTitle(id);
            ResultHint = backups.Count > 0 ? "Запасные: " + string.Join(", ", backups.Select(StrategyTitle)) + "." : "";
            Say(ResultText);
        }
    }

    // ---------- свои адреса и списки роутера ----------

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
        await Guard("Читаю свои адреса роутера…", async () =>
        {
            var t = await Call(c => c.TargetsGetAsync());
            Targets.Clear();
            foreach (var x in t.Items) Targets.Add(new(x.Url, x.Important));
            _targetsLoaded = true;
            ListGeneralText = string.Join(Environment.NewLine, (await Call(c => c.ListsGetAsync("user-general"))).Lines);
            ListExcludeText = string.Join(Environment.NewLine, (await Call(c => c.ListsGetAsync("user-exclude"))).Lines);
            _listsLoaded = true;
            TargetsMessage = $"На роутере адресов: {Targets.Count} из {MaxTargets}.";
        });
        Say("");
    }

    void AddTarget()
    {
        if (Targets.Count >= MaxTargets) { TargetsMessage = $"На роутере — не больше {MaxTargets} адресов."; return; }
        Targets.Add(new("", false));
    }

    /// <summary>«Скопировать с ПК»: свои адреса этого ПК, которых ещё нет на роутере (до 50).</summary>
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
        TargetsMessage = PcUserTargets().Count == 0 ? "На этом ПК своих адресов нет."
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
    public string DiagHint => Connected ? "Нажмите «Обновить»." : NeedLogin ? "Войдите на роутер (карточка «Роутер» ниже), чтобы увидеть его состояние." : ConnectionText + ".";

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
                    Conflicts.Add(new(c.Name, (c.Kind == "service" ? "служба" : c.Kind) + (c.Running ? ", работает" : ", остановлена")));
                FlowOffloadText = d.FlowOffload ? "включено — обход может не срабатывать; выключите «Flow offloading» в настройках межсетевого экрана роутера" : "выключено";
                Ipv6Text = d.Ipv6Wan ? "есть — часть сайтов может идти по IPv6 мимо обхода" : "нет";
                DnsText = d.Dns is null ? "не проверялся"
                    : (d.Dns.Spoofed ? "провайдер подменяет адреса сайтов" : "подмены нет") + (d.Dns.CheckedAt is { } at ? $" (проверено в {RouterTexts.Time(at)})" : "");
                SpaceText = $"{d.OverlayFreeKb / 1024.0:0.#} МБ" + (d.OverlayFreeKb < 2048 ? " — мало, обновления могут не поместиться" : "");
                DiagLoaded = true;
                DiagUpdated = $"обновлено в {DateTime.Now:HH:mm:ss}";
            }))
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
            }))
            Say(path is null ? "" : "Отчёт роутера сохранён: " + path);
    }

    static void Requery()
    {
        // Команды пересчитываются на потоке окна; в тестах окна нет — ничего не делаем.
        if (Application.Current?.Dispatcher is { } d) d.BeginInvoke(CommandManager.InvalidateRequerySuggested);
    }

    public void Dispose()
    {
        _client?.Dispose();

    }
}
