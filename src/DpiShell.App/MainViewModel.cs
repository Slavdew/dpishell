using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.App;

public enum StatusKind { Off, On, Busy, Error }

public static class Pages
{
    public const int Home = 0, Select = 1, Targets = 2, Net = 3, Hosts = 4, Diag = 5, Router = 6, Settings = 7;
}

/// <summary>
/// Всё состояние окна. Окно работает без прав администратора (helper-protocol): читает само (проверка сайтов, DNS, службы,
/// роутер), а всё, что меняет систему, — через помощника (<see cref="HelperLink"/>). Помощника нет — значок щита на кнопках,
/// нажатие предлагает установку (один запрос прав), и действие продолжается само.
/// </summary>
public sealed partial class MainViewModel : Observable
{
    readonly Shell _shell;
    CancellationTokenSource? _selectCts;
    Task _current = Task.CompletedTask;
    string? _error;
    string? _coreProblem;
    SvcState _svc = SvcState.Unknown;
    bool _active;   // обход работает в своём режиме: служба запущена или winws помощника жив
    string? _draftStrategy;   // выбрана в списке без помощника — включится по «Включить»
    string? _localStrategy;   // без помощника: стратегия из настроек службы DpiShell (реестр читается без прав)
    bool _installing;
    bool _groupsFromHelper, _targetsFromHelper;

    /// <summary>Данные поставки: каталог, цели, пути (только чтение — папка программы закрыта).</summary>
    public Shell Shell => _shell;
    public HelperLink Helper { get; }
    public WindowSettings Settings { get; }
    public RouterViewModel Router { get; }
    /// <summary>«Игры и IP-списки» (09-features-spec §1–2).</summary>
    public NetViewModel Net { get; }
    /// <summary>«Починить адреса» — hosts (09-features-spec §3).</summary>
    public HostsViewModel Hosts { get; }
    /// <summary>Обновления данных (architecture.md «Обновления данных»).</summary>
    public UpdatesViewModel Updates { get; }
    /// <summary>Окно при входе в Windows (architecture.md «Окно, трей, автозапуск»).</summary>
    public Autostart Autostart { get; }
    /// <summary>Уведомление в трей (окно может быть свёрнуто): «Все адреса» закончились, адрес в hosts устарел.</summary>
    public event Action<string>? Notify;

    /// <summary>Вопрос «Да/Нет» (в тестах подменяется).</summary>
    public Func<string, bool> Confirm { get; set; } = text =>
        MessageBox.Show(text, "DpiShell", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    /// <summary>Вопрос об установке помощника (в тестах подменяется).</summary>
    public Func<string, bool> AskHelper { get; set; } = text =>
        MessageBox.Show(text, "DpiShell", MessageBoxButton.OKCancel, MessageBoxImage.Information, MessageBoxResult.OK) == MessageBoxResult.OK;

    public event Action? StatusChanged;

    public MainViewModel(Shell shell, HelperLink helper, WindowSettings settings)
    {
        _shell = shell;
        Helper = helper;
        Settings = settings;
        Helper.Changed += OnHelperChanged;
        Updates = new UpdatesViewModel(helper, shell, settings)
        {
            Guard = Guard,
            Need = NeedHelperAsync,
            Say = s => Message = s,
            Confirm = s => Confirm(s),
            AfterInstall = ReloadChoicesAsync,
        };
        Autostart = new Autostart(settings) { Say = s => Message = s };

        RetryCmd = RelayCommand.Async(InitializeAsync, () => ShowLoadActions);
        OpenWithoutHelperCmd = RelayCommand.Async(OpenWithoutHelperAsync, () => ShowLoadActions);
        EnableCmd = RelayCommand.Async(EnableAsync, () => !Busy && !IsLoading);
        DisableCmd = RelayCommand.Async(DisableAsync, () => !Busy && !IsLoading);
        GoSelectCmd = new RelayCommand(() => Page = Pages.Select);
        GoDiagCmd = new RelayCommand(() => Page = Pages.Diag);
        CheckNowCmd = RelayCommand.Async(CheckNowAsync, () => !Busy && !IsLoading && !Checking);

        StartSelectCmd = RelayCommand.Async(StartSelectAsync, () => !Busy && !IsLoading);
        StartSearchCmd = RelayCommand.Async(StartSearchAsync, () => !Busy && !IsLoading);
        CheckDnsCmd = RelayCommand.Async(CheckDnsAsync, () => !IsLoading && !DnsChecking);
        StopSelectCmd = new RelayCommand(() => _selectCts?.Cancel(), () => Selecting);
        ApplyBestCmd = RelayCommand.Async(() => ApplyAsync(_bestId!), () => !Busy && _bestId is not null);
        ApplySelectedCmd = RelayCommand.Async(() => ApplyAsync(SelectedScore!.Id), () => !Busy && SelectedScore is { Usable: true });

        SaveTargetsCmd = RelayCommand.Async(SaveTargetsAsync, () => !Busy);

        RefreshDiagCmd = RelayCommand.Async(RefreshDiagAsync, () => !IsLoading && !DiagRefreshing);
        RepairCoreCmd = RelayCommand.Async(RepairCoreAsync, () => !Busy && !IsLoading);
        OpenLogsCmd = new RelayCommand(() => OpenFolder(Path.Combine(WindowSettings.DefaultDir, "logs")));
        OpenResultsCmd = RelayCommand.Async(OpenResultsAsync);
        RemoveServiceCmd = RelayCommand.Async(RemoveServiceAsync, () => !Busy && !IsLoading);
        FixHelperCmd = RelayCommand.Async(async () => { if (await InstallHelperAsync()) Message = "Помощник установлен."; }, () => !Busy);
        UninstallHelperCmd = RelayCommand.Async(UninstallHelperAsync, () => !Busy && !IsLoading);

        // Обход на роутере (docs/dev/router-api.md §6): своя логика, свои команды; сюда — только строка состояния и переходы.
        Router = new RouterViewModel(shell, settings)
        {
            Say = s => Message = s,
            Fail = Fail,
            Navigate = p => Page = p,
            SelectOptions = RouterSelectOptions,
            SetSelectGroups = ids => { var want = ids.ToHashSet(); foreach (var g in Groups) g.IsChecked = want.Contains(g.Id); },
            FoundStrategies = () => Helper.Details?.Found ?? [],
            PcUserTargets = () => Helper.Status?.UserTargets ?? [],
        };
        Router.Changed += UpdateStatus;
        WireRouterPc();   // обход ПК и роутера вместе не работают (router-api §6) — MainViewModel.Router.cs
        InitPcDns();      // DNS этого компьютера — MainViewModel.Dns.cs

        Net = new NetViewModel(helper)
        {
            Guard = Guard,
            Need = NeedHelperAsync,
            Say = s => Message = s,
            Confirm = s => Confirm(s),
            AfterChange = RefreshStatusAsync,
        };
        Net.Notify += s => Notify?.Invoke(s);
        Hosts = new HostsViewModel(helper)
        {
            Need = NeedHelperAsync,
            Say = s => Message = s,
            Confirm = s => Confirm(s),
        };
        Hosts.Notify += s => { Message = s; Notify?.Invoke(s); };
        ClearDiscordCmd = RelayCommand.Async(ClearDiscordAsync, () => !Busy && !IsLoading);

        // Группы — по умолчанию; у помощника свой выбор — подставится, когда он ответит.
        var chosen = new AppState().Groups.ToHashSet();
        foreach (var g in _shell.DefaultTargets.Groups.Where(g => !g.Control)) Groups.Add(new(g.Id, g.Title, chosen.Contains(g.Id)));
        Groups.Add(new("user", "Свои адреса", chosen.Contains("user")));
        InitHome();       // главная: помехи, итог проверки — MainViewModel.Home.cs
        UpdateStatus();
    }

    // ---------- помощник ----------

    /// <summary>Помощник не готов — у кнопок, которым нужны права, значок щита.</summary>
    public bool NeedsHelper => !Helper.Ready;

    /// <summary>Помощник есть, но не отвечает / устарел / чужой — текст и кнопка «Переустановить».</summary>
    public string HelperProblem => Helper.State is HelperState.NotResponding or HelperState.Denied or HelperState.Outdated ? Helper.Problem : "";
    public bool HasHelperProblem => HelperProblem.Length > 0;
    public string FixHelperText => Helper.State == HelperState.Outdated ? "Обновить помощника" : "Переустановить помощника";

    /// <summary>Состояние помощника — в «Настройках» → «О программе».</summary>
    public string HelperText => Helper.State switch
    {
        HelperState.Connected => "работает" + (Helper.Reader?.HelperVersion is { Length: > 0 } v ? $" (версия {v})" : ""),
        HelperState.Outdated => "устарел — обновится по первому действию",
        HelperState.NotResponding or HelperState.Denied => Helper.Problem,
        HelperState.NotInstalled => "не установлен — окно в режиме просмотра",
        _ => "проверяется…",
    };
    public bool CanUninstallHelper => Helper.State is not (HelperState.NotInstalled or HelperState.Unknown);

    public ICommand FixHelperCmd { get; }
    public ICommand UninstallHelperCmd { get; }

    const string ViewOnly = "Без помощника окно работает в режиме просмотра: проверка сайтов, DNS и роутер доступны, менять обход на ПК — нет.";

    void OnHelperChanged()
    {
        foreach (var p in new[] { nameof(NeedsHelper), nameof(HelperProblem), nameof(HasHelperProblem), nameof(FixHelperText), nameof(HelperText), nameof(CanUninstallHelper), nameof(AboutText) })
            Raise(p);
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// Действие с правами: помощник готов — сразу; нет — один вопрос, установка (UAC), подключение,
    /// и вызывающий продолжает своё действие (architecture.md «Принцип»: программа сама ведёт к результату). Null — не вышло, причина в строке состояния.
    /// </summary>
    public async Task<HelperClient?> NeedHelperAsync()
    {
        if (Helper.Client is { } ready) return ready;
        if (_installing) return null;
        // Связь могла оборваться ненадолго (помощник перезапустился) — сначала тихо переподключиться.
        if (Helper.State is HelperState.Unknown or HelperState.NotResponding && await Helper.ConnectAsync())
        {
            await AfterConnectAsync();
            return Helper.Client;
        }
        var question = Helper.State switch
        {
            HelperState.Outdated => "Помощника DpiShell нужно обновить до версии окна: один запрос прав администратора, дальше без вопросов.\n\nОбновить сейчас?",
            HelperState.NotResponding => Helper.Problem + "\n\nПереустановить помощника? Это один запрос прав администратора.",
            HelperState.Denied => Helper.Problem + "\n\nПереустановить помощника для вас? Это один запрос прав администратора.",
            _ => "Нужен помощник DpiShell: один запрос прав администратора, дальше без вопросов.\n\n" +
                 "Он включает обход, подбирает стратегии и меняет системные настройки по командам этого окна. Установить?",
        };
        if (!AskHelper(question)) { Message = ViewOnly; return null; }
        return await InstallHelperAsync() ? Helper.Client : null;
    }

    /// <summary>Установка/обновление/переустановка: UAC → ожидание → подключение → данные помощника.</summary>
    async Task<bool> InstallHelperAsync()
    {
        if (_installing) return false;
        _installing = true;
        Busy = true; BusyText = "Устанавливаю помощника…"; Message = "Подтвердите запрос прав администратора — он нужен один раз.";
        UpdateStatus();
        SetupOutcome r;
        try { r = await Helper.InstallAsync(); }
        catch (Exception e) { r = new(SetupResult.Failed, Texts.Friendly(e)); }
        finally
        {
            _installing = false;
            Busy = false; BusyText = "";
            UpdateStatus();
        }
        switch (r.Result)
        {
            case SetupResult.Declined:
                Fail("Права не выданы. " + ViewOnly);
                return false;
            case SetupResult.Failed:
                Fail("Помощник не установился: " + r.Error);
                return false;
        }
        Message = "";
        await AfterConnectAsync();
        return true;
    }

    async Task UninstallHelperAsync()
    {
        if (!Confirm("Удалить помощника DpiShell? Нужен один запрос прав администратора. Окно останется в режиме просмотра; служба обхода, если стоит, не удаляется — для этого «Удалить службу полностью»."))
            return;
        Busy = true; BusyText = "Удаляю помощника…"; UpdateStatus();
        try
        {
            var r = await Helper.UninstallAsync();
            Message = r.Result switch
            {
                SetupResult.Done => "Помощник удалён. " + ViewOnly,
                SetupResult.Declined => "Права не выданы — помощник остался.",
                _ => "Помощника удалить не удалось: " + r.Error,
            };
        }
        finally { Busy = false; BusyText = ""; UpdateStatus(); }
        await RefreshStatusAsync();
        ReloadChoices();
        Net.Refresh();
        await Hosts.RefreshAsync();
    }

    /// <summary>
    /// Подключились (при запуске, после установки или переподключения): данные помощника, и режим без службы —
    /// был включён, а winws нет (помощник гасит его, когда окно отключается) — вернуть (замена ResumeIfNeeded).
    /// </summary>
    async Task AfterConnectAsync()
    {
        await FetchAsync(full: true);
        ApplyHelperData();
        if (Helper.Client is { } c && Helper.Status is { RunMode: "process", Enabled: true, Active: false, StrategyId: { } id })
        {
            try { await c.EnableAsync(id); }
            catch (Exception e) when (e is HelperException or HelperUnavailableException) { _error = Texts.Friendly(e); Fail(_error); }
            await FetchAsync(full: false);
        }
        await RefreshStatusAsync();
    }

    /// <summary>Перечитать у помощника: status, а полностью — ещё результаты и сведения. Помощника нет — ничего.</summary>
    async Task FetchAsync(bool full)
    {
        if (await Helper.FetchAsync(full) is { } error) Message = error;
    }

    /// <summary>Данные помощника — на экраны (синхронно: и тест окна строит экран по готовому состоянию).</summary>
    public void ApplyHelperData()
    {
        if (Helper.Status is { } s)
        {
            if (!_groupsFromHelper && s.SelectedGroups.Count > 0)
            {
                _groupsFromHelper = true;
                var chosen = s.SelectedGroups.ToHashSet();
                foreach (var g in Groups) g.IsChecked = chosen.Contains(g.Id);
            }
            if (!_targetsFromHelper)
            {
                _targetsFromHelper = true;
                if (s.UserTargets.Count > 0) TargetsText = string.Join(Environment.NewLine, s.UserTargets);
            }
        }
        ReloadChoices();
        Net.Refresh();
        Hosts.RefreshNow();
        UpdateStatus();
    }

    // ---------- общее ----------

    int _page;
    public int Page
    {
        get => _page;
        set
        {
            int was = _page;
            if (!Set(ref _page, value) || IsLoading) return;
            if (was == Pages.Targets) _ = SaveDraftOnLeaveAsync();
            if (value == Pages.Net) _ = RefreshNetAsync();
            if (value == Pages.Hosts) _ = Hosts.RefreshAsync();
            if (value == Pages.Router) _ = Router.RefreshRouterPageAsync();
            if (value != Pages.Diag) return;
            _ = RefreshDiagAsync();
            if (Router.Connected) _ = Router.RefreshDiagAsync();
        }
    }

    async Task RefreshNetAsync()
    {
        await FetchAsync(full: true);
        Net.Refresh();
    }

    bool _busy;
    public bool Busy { get => _busy; private set { if (Set(ref _busy, value)) CommandManager.InvalidateRequerySuggested(); } }

    string _busyText = "";
    public string BusyText { get => _busyText; private set => Set(ref _busyText, value); }

    string _message = "";
    /// <summary>Строка состояния внизу окна.</summary>
    public string Message { get => _message; set => Set(ref _message, value); }

    /// <summary>Одна операция за раз; любая ошибка — в строку состояния и на главную, а не падение.</summary>
    async Task<bool> Guard(string busyText, Func<Task> work)
    {
        if (Busy) return false;
        Busy = true; BusyText = busyText; Message = busyText; _error = null;
        UpdateStatus();
        var done = new TaskCompletionSource();
        _current = done.Task;
        try
        {
            await work();
            Message = "";
            return true;
        }
        catch (Exception e)
        {
            if (e is HelperUnavailableException u) Helper.MarkLost(u);
            _error = Texts.Friendly(e);
            Fail(_error);
            // Чужие программы обхода: что именно мешает — на «Диагностике».
            if (e is ConflictException or HelperException { Code: HelperErrors.Conflict }) Page = Pages.Diag;
            return false;
        }
        finally
        {
            Busy = false; BusyText = "";
            UpdateStatus();
            done.TrySetResult();
        }
    }

    /// <summary>Действие с правами под Guard: сначала помощник (с установкой по требованию), потом работа.</summary>
    async Task<bool> WithHelper(string busyText, Func<HelperClient, Task> work)
    {
        if (Busy) return false;
        if (await NeedHelperAsync() is not { } c) return false;
        return await Guard(busyText, () => work(c));
    }

    /// <summary>Дождаться текущей операции (для выхода во время подбора).</summary>
    public async Task WhenIdleAsync(TimeSpan timeout)
    {
        _selectCts?.Cancel();
        await Task.WhenAny(_current, Task.Delay(timeout));
    }

    // ---------- загрузка ----------

    bool _isLoading = true;
    public bool IsLoading { get => _isLoading; private set { if (Set(ref _isLoading, value)) { Raise(nameof(ShowOverlay)); Raise(nameof(ShowLoadActions)); CommandManager.InvalidateRequerySuggested(); } } }

    bool _loadFailed;
    public bool LoadFailed { get => _loadFailed; private set { if (Set(ref _loadFailed, value)) { Raise(nameof(ShowOverlay)); Raise(nameof(ShowLoadActions)); CommandManager.InvalidateRequerySuggested(); } } }

    public bool ShowOverlay => IsLoading;

    string _loadingText = "Подключаюсь к помощнику…";
    public string LoadingText { get => _loadingText; private set => Set(ref _loadingText, value); }

    /// <summary>
    /// Старт окна без прав: подключиться к помощнику (нет — режим просмотра), данные — от него или из поставки.
    /// Ядро ставит и проверяет помощник при своём запуске.
    /// </summary>
    public async Task InitializeAsync()
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        IsLoading = true; LoadFailed = false; LoadSlow = false; LoadingText = "Подключаюсь к помощнику…";
        _ = MarkSlowAsync(cts.Token);
        try
        {
            await Helper.ConnectAsync(cts.Token);
            if (cts.IsCancellationRequested) return;   // уже повторили или открыли без помощника
            await OpenAsync();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception e)
        {
            if (cts != _loadCts) return;
            LoadFailed = true; LoadSlow = false;
            LoadingText = Texts.Friendly(e);
        }
    }

    CancellationTokenSource? _loadCts;

    /// <summary>Через сколько заставка говорит «дольше обычного» и предлагает выход (UX-аудит, п. 46).</summary>
    public TimeSpan LoadSlowAfter { get; set; } = TimeSpan.FromSeconds(10);

    bool _loadSlow;
    /// <summary>Помощник не ответил за <see cref="LoadSlowAfter"/>: на заставке «Повторить» и «Открыть без помощника».</summary>
    public bool LoadSlow { get => _loadSlow; private set { if (Set(ref _loadSlow, value)) { Raise(nameof(ShowLoadActions)); CommandManager.InvalidateRequerySuggested(); } } }

    /// <summary>Кнопки заставки: подключение не вышло или идёт дольше обычного — не застревать на первом экране.</summary>
    public bool ShowLoadActions => IsLoading && (LoadFailed || LoadSlow);

    async Task MarkSlowAsync(CancellationToken ct)
    {
        try { await Task.Delay(LoadSlowAfter, ct); }
        catch (OperationCanceledException) { return; }
        if (!IsLoading || LoadFailed) return;
        LoadingText = "Помощник отвечает дольше обычного. Можно подождать, повторить или открыть окно без него — только для просмотра.";
        LoadSlow = true;
    }

    /// <summary>«Открыть без помощника»: не ждать его — окно в режиме просмотра; помощник подхватится сам, когда ответит (TickAsync).</summary>
    public async Task OpenWithoutHelperAsync()
    {
        _loadCts?.Cancel();
        LoadFailed = false;
        try { await OpenAsync(); }
        catch (Exception e) { Fail(Texts.Friendly(e)); }
    }

    /// <summary>Снять заставку и наполнить окно: от помощника, если он на связи, иначе — то, что видно без прав.</summary>
    async Task OpenAsync()
    {
        IsLoading = false; LoadSlow = false;
        // Роутер ищем сразу и параллельно: его нет — блок просто не появится.
        _ = Router.RefreshAsync();
        if (Helper.Reader is not null) await AfterConnectAsync();
        else
        {
            ApplyHelperData();
            await RefreshStatusAsync();
        }
        _ = CheckDnsAsync();
        _ = RefreshPcDnsAsync();
        // «Починить адреса» включено и с прошлой проверки прошли сутки — перепроверить записи (устаревшие уберутся).
        _ = Hosts.RecheckIfDueAsync();
        // Обновления данных — раз в день, тихо (architecture.md «Обновления данных»).
        _ = Updates.CheckIfDueAsync();
    }

    // ---------- главная ----------

    StatusKind _kind;
    public StatusKind Kind { get => _kind; private set => Set(ref _kind, value); }

    string _statusTitle = "";
    public string StatusTitle { get => _statusTitle; private set => Set(ref _statusTitle, value); }

    string _statusDetail = "";
    public string StatusDetail { get => _statusDetail; private set => Set(ref _statusDetail, value); }

    Brush _statusBrush = Brushes.Gray;
    public Brush StatusBrush { get => _statusBrush; private set => Set(ref _statusBrush, value); }

    public static Brush BrushOf(StatusKind k) => k switch
    {
        StatusKind.On => Frozen(0x16, 0xA3, 0x4A),
        StatusKind.Busy => Frozen(0xD9, 0x77, 0x06),
        StatusKind.Error => Frozen(0xDC, 0x26, 0x26),
        _ => Frozen(0x6B, 0x72, 0x80),
    };

    static Brush Frozen(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }

    /// <summary>Обход работает — в режиме службы или из программы.</summary>
    public bool IsRunning => _active;

    /// <summary>Статус помощника, если связь жива (иначе — то, что окно читает само).</summary>
    HelperStatus? Live => Helper.Reader is null ? null : Helper.Status;

    /// <summary>Выбранная стратегия: черновик окна, у помощника, или та, что стоит в службе.</summary>
    public string? CurrentStrategyId => _draftStrategy ?? Live?.StrategyId ?? _localStrategy;

    IReadOnlyList<string> UserTargets => Live?.UserTargets ?? [];

    readonly Dictionary<string, string> _titles = [];

    /// <summary>Название по id — и готовых, и найденных поиском.</summary>
    string StrategyTitle(string? id)
    {
        if (id is null) return "";
        if (Helper.Status?.Strategies.FirstOrDefault(s => s.Id == id) is { } i) return i.Title;
        if (_shell.Catalog.Find(id) is { } s) return s.Title;
        return _titles.TryGetValue(id, out var t) ? t : id;
    }

    void UpdateStatus()
    {
        var err = _error ?? _coreProblem;
        var id = CurrentStrategyId;
        if (Selecting) { Kind = StatusKind.Busy; StatusTitle = _searching ? "Глубокий поиск…" : "Подбор…"; StatusDetail = SelectProgressText; }
        else if (Busy) { Kind = StatusKind.Busy; StatusTitle = BusyText; StatusDetail = ""; }
        else if (err is not null) { Kind = StatusKind.Error; StatusTitle = "Ошибка"; StatusDetail = err; }
        else if (_active) { Kind = StatusKind.On; StatusTitle = "Работает"; StatusDetail = id is null ? "" : "Стратегия: " + StrategyTitle(id); }
        else if (ServiceMode && _svc == SvcState.StartPending) { Kind = StatusKind.Busy; StatusTitle = "Запускается…"; StatusDetail = ""; }
        else if (Router.Found && Router.RouterEnabled)
        {
            // Обход делает роутер — для этого ПК тоже: не «Выключено» (читается как «не работает», UX-аудит 2, п. 3).
            Kind = StatusKind.Off; StatusTitle = "Работает через роутер";
            StatusDetail = "На этом ПК включать не нужно";
        }
        else
        {
            Kind = StatusKind.Off; StatusTitle = "Выключено";
            StatusDetail = id is not null ? "Выбрана стратегия: " + StrategyTitle(id) : "Нажмите «Включить» — подходящую стратегию подберу сам (2–3 мин)";
        }
        // Через роутер — зелёный кружок: обход работает, хоть и не на этом ПК (значок в трее — про этот ПК).
        StatusBrush = BrushOf(Kind == StatusKind.Off && Router.Found && Router.RouterEnabled ? StatusKind.On : Kind);
        DropStaleCheck();
        Raise(nameof(IsRunning));
        Raise(nameof(ToggleText)); Raise(nameof(PcSecondary)); Raise(nameof(PcPrimary));
        Raise(nameof(CanChoose)); Raise(nameof(TargetsDirty));
        Router.PcChanged();
        Raise(nameof(StrategyHeader));
        Raise(nameof(ServiceMode)); Raise(nameof(RunModeHint)); Raise(nameof(CloseHint)); Raise(nameof(HasCloseHint));
        // Автозапуск по умолчанию следует за режимом — когда режим известен (помощник ответил).
        if (Live is not null) Autostart.OnRunMode(ServiceMode);
        Updates.Refresh();
        RaiseIssues();
        // Стратегию могли сменить с «Подбора» или автопереключением — список показывает текущую, не вызывая перезапуск.
        if (_chosen != id) { _chosen = id; Raise(nameof(ChosenStrategyId)); }
        StatusChanged?.Invoke();
    }

    string _vpnWarning = "";
    public string VpnWarning { get => _vpnWarning; private set { if (Set(ref _vpnWarning, value)) { Raise(nameof(HasVpn)); RaiseIssues(); } } }
    /// <summary>Плашка VPN на главной: есть VPN и человек не сказал «Понятно» про этот же набор адаптеров.</summary>
    public bool HasVpn => VpnWarning.Length > 0 && VpnWarning != _vpnDismissed;

    string? _vpnDismissed;
    /// <summary>«Понятно, не показывать»: до смены набора VPN-адаптеров (в «Диагностике» VPN виден всегда).</summary>
    public ICommand DismissVpnCmd => _dismissVpnCmd ??= new RelayCommand(() => { _vpnDismissed = VpnWarning; Raise(nameof(HasVpn)); RaiseIssues(); });
    ICommand? _dismissVpnCmd;

    string _conflictWarning = "";
    public string ConflictWarning { get => _conflictWarning; private set { if (Set(ref _conflictWarning, value)) { Raise(nameof(HasConflicts)); RaiseIssues(); } } }
    public bool HasConflicts => ConflictWarning.Length > 0;

    public ObservableCollection<CheckRow> CheckRows { get; } = [];

    string _checkSummary = "";
    public string CheckSummary { get => _checkSummary; private set => Set(ref _checkSummary, value); }

    // ---------- выбор стратегии (отдельно от выключателя) ----------

    public ObservableCollection<StrategyChoice> StrategyChoices { get; } = [];

    /// <summary>Перечитать у помощника и обновить список (после подбора: новая статистика и найденные).</summary>
    public async Task ReloadChoicesAsync()
    {
        await FetchAsync(full: true);
        ReloadChoices();
    }

    /// <summary>
    /// Список на главной: стратегии помощника (готовые и найденные) или каталог поставки, по статистике подборов.
    /// Статистика у помощника; до первого подбора (и без помощника) — «появится после первого подбора».
    /// </summary>
    public void ReloadChoices()
    {
        var list = Helper.Status?.Strategies ?? _shell.Catalog.Strategies.Select(s => new StrategyInfo(s.Id, s.Title, s.Source)).ToList();
        var stats = Helper.Results?.Stats ?? new Dictionary<string, StrategyStats.Stat>();
        StrategyChoices.Clear();
        foreach (var s in list.Select((s, i) => (s, i))
                     .OrderByDescending(x => stats.TryGetValue(x.s.Id, out var v) ? v.Score : 0.5).ThenBy(x => x.i).Select(x => x.s))
        {
            // Не «процент сайтов», а сколько раз в прошлых подборах открыла всё отмеченное.
            var note = stats.TryGetValue(s.Id, out var st) && st.Runs > 0
                ? $"открыла всё в {st.FullOk} из {st.Runs} подборов ({(double)st.FullOk / st.Runs:P0})"
                : stats.Count == 0 ? "статистика появится после первого подбора" : "в подборах ещё не участвовала";
            StrategyChoices.Add(new(s.Id, s.Title, note, s.Source == "search"));
        }
        _chosen = CurrentStrategyId;
        Raise(nameof(ChosenStrategyId));
    }

    string? _chosen;
    /// <summary>Выбранная стратегия. Обход включён — перезапуск на новой; выключен — запоминаем (у помощника или в окне).</summary>
    public string? ChosenStrategyId
    {
        get => _chosen;
        set
        {
            if (value is null || value == _chosen) return;
            _chosen = value;
            Raise();
            if (IsRunning) _ = ApplyAsync(value);
            else _ = ChooseAsync(value);
        }
    }

    /// <summary>Без помощника — черновик окна (включится по «Включить»); с помощником — запомнить у него.</summary>
    async Task ChooseAsync(string id)
    {
        _draftStrategy = id;
        UpdateStatus();
        if (Helper.Client is not { } c) return;
        try
        {
            await c.ChooseAsync(id);
            await FetchAsync(full: false);
            if (Live?.StrategyId == id) _draftStrategy = null;
        }
        catch (Exception e) when (e is HelperException or HelperUnavailableException) { Fail(Texts.Friendly(e)); }
        UpdateStatus();
    }

    public string StrategyHeader => CurrentStrategyId is { } id ? "Стратегия: " + StrategyTitle(id) : "Стратегия не выбрана";

    // ---------- режим работы ----------

    /// <summary>Галочка «Режим службы» (режим — у помощника). Переключение — через помощника; обход, если работал, продолжает.</summary>
    public bool ServiceMode
    {
        get => Live?.RunMode != "process";
        set { if (value != ServiceMode) _ = SetRunModeAsync(value); }
    }

    public string RunModeHint => ServiceMode
        ? "Обход работает и без окна, и после перезагрузки"
        : "Обход работает, пока открыта программа (в окне или в трее)";

    async Task SetRunModeAsync(bool service)
    {
        await WithHelper(service ? "Перехожу на работу без окна…" : "Перехожу на работу из программы…", async c =>
        {
            await c.SetRunModeAsync(service);
            await FetchAsync(full: false);
        });
        // И при отказе (идёт другая операция, нет помощника) — галочка показывает настоящий режим.
        Raise(nameof(ServiceMode)); Raise(nameof(RunModeHint)); Raise(nameof(CloseHint)); Raise(nameof(HasCloseHint));
        await RefreshStatusAsync();
    }

    public string ToggleText => IsRunning ? "Выключить" : PcSecondary ? "Включить и на этом ПК" : "Включить";
    public bool CanChoose => !Busy && !IsLoading && !Selecting;
    public ICommand ToggleCmd => _toggleCmd ??= RelayCommand.Async(() => IsRunning ? DisableAsync() : EnableAsync(), () => !Busy && !IsLoading);
    ICommand? _toggleCmd;

    public ICommand RetryCmd { get; }
    public ICommand OpenWithoutHelperCmd { get; }
    public ICommand EnableCmd { get; }
    public ICommand DisableCmd { get; }
    public ICommand GoSelectCmd { get; }
    public ICommand GoDiagCmd { get; }
    public ICommand CheckNowCmd { get; }

    /// <summary>Без помощника: стратегия из настроек службы DpiShell (реестр) — что стоит, видно и без прав.</summary>
    string? LocalServiceStrategy()
    {
        try
        {
            var (_, args) = ZapretDetector.SplitCommandLine(ServiceManager.QueryBinPath(ServiceManager.Name));
            return ZapretDetector.Match(args, _shell.Catalog.Strategies)?.Id;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException) { return null; }
    }

    /// <summary>Служба, конфликты, VPN, ядро: у помощника (status) или сами, без прав (sc query, реестр, процессы).</summary>
    public async Task RefreshStatusAsync()
    {
        if (IsLoading) return;
        try
        {
            await FetchAsync(full: false);
            var st = Live;
            var (svc, strategy, conflicts, vpn) = await Task.Run(() =>
            {
                var s = ServiceManager.Query(ServiceManager.Name);
                return (s,
                    st is null && s != SvcState.NotInstalled ? LocalServiceStrategy() : null,
                    st?.Foreign ?? Conflicts.Find(_shell.Paths, _shell.Catalog.Strategies),
                    Conflicts.ActiveVpnAdapters());
            });
            if (st is not null)
            {
                (_svc, _active) = (st.Service, st.Active);
                _coreProblem = CoreProblem(st.Core);
            }
            else
            {
                // Без помощника видно только службу; winws режима без службы без помощника не работает.
                (_svc, _active) = (svc, svc == SvcState.Running);
                _localStrategy = strategy;
                _coreProblem = null;
            }
            VpnWarning = vpn.Count == 0 ? "" : $"Включён VPN ({string.Join(", ", vpn)}). С ним обход обычно не нужен, а подбор может показать неверный результат.";
            ConflictWarning = conflicts.Count == 0 ? "" : $"Мешают другие программы обхода: {string.Join(", ", conflicts.Select(c => c.Name).Distinct())}.";
        }
        catch (Exception e) { Message = Texts.Friendly(e); }
        UpdateStatus();
    }

    /// <summary>Тихое обновление по таймеру — только когда ничего не идёт. Помощник пропал — тихо пробуем переподключиться.</summary>
    public async Task TickAsync()
    {
        if (IsLoading) return;
        _ = Router.TickAsync();
        if (Busy) return;
        if (Helper.State == HelperState.NotResponding && !_installing && await Helper.ConnectAsync())
        {
            await AfterConnectAsync();
            return;
        }
        _ = Hosts.RecheckIfDueAsync();
        await RefreshStatusAsync();
    }

    static string? CoreProblem(CoreState c) => c switch
    {
        CoreState.Ok => null,
        CoreState.Missing => "Файлы обхода не найдены (антивирус?). Откройте «Диагностику» → «Восстановить».",
        _ => "Файлы обхода повреждены (антивирус?). Откройте «Диагностику» → «Восстановить».",
    };

    public async Task EnableAsync()
    {
        // Роутер уже обходит для всего дома — двойная обработка ломает сайты (router-api §6): подобрать там или выключить там.
        if (!IsRunning && !await RouterGateAsync(select: false)) return;
        await EnableCoreAsync();
    }

    Task EnableCoreAsync()
    {
        // Стратегия не выбрана — не отправляем человека разбираться. Подборы уже были: берём лучшую по ним
        // (первая в списке, порядок по статистике). Не было: подбираем и включаем сами (автоприменение).
        if (CurrentStrategyId is not { } id)
        {
            var top = StrategyChoices.FirstOrDefault()?.Id;
            if (top is not null && Helper.Results?.Stats.TryGetValue(top, out var score) == true && score.Score > 0) return ApplyAsync(top);
            Page = Pages.Select;
            return StartSelectAsync();
        }
        return ApplyAsync(id);
    }

    public Task DisableAsync() => WithHelper("Выключаю…", async c =>
    {
        await c.DisableAsync();
        await RefreshStatusAsync();
    });

    /// <summary>Включить на стратегии; включилось — сразу «Проверить сейчас» (UX-аудит 2, п. 1): итог в карточке — про новое состояние.</summary>
    async Task<bool> ApplyAsync(string id)
    {
        bool ok = await WithHelper("Включаю…", async c =>
        {
            // Выбранная руками — первая в запасных: это делает помощник вместе с включением.
            await c.EnableAsync(id);
            _draftStrategy = null;
            await RefreshStatusAsync();
            Page = Pages.Home;
        });
        if (ok && IsRunning && CheckedGroups().Count > 0) await CheckNowAsync();
        return ok;
    }

    /// <summary>Проверка сайтов (в тестах — подмена без сети). Null — настоящая: HTTPS с DoH.</summary>
    public Func<IReadOnlyList<TargetGroup>, Task<IReadOnlyList<TargetVerdict>>>? ProbeGroups { get; set; }

    bool _checking;
    /// <summary>Идёт «Проверить сейчас»: в карточке «Проверяю…».</summary>
    public bool Checking
    {
        get => _checking;
        private set { if (Set(ref _checking, value)) { Raise(nameof(HasCheck)); CommandManager.InvalidateRequerySuggested(); } }
    }

    /// <summary>Для какого состояния обхода (включён, стратегия) сделана проверка; другое состояние — итог устарел.</summary>
    (bool Active, string? Strategy)? _checkFor;
    int _checkGen;

    (bool, string?) CheckState() => (_active, _active ? CurrentStrategyId : null);

    /// <summary>Обход включили, выключили или сменили стратегию — прежний итог не про то, что сейчас: убрать.</summary>
    void DropStaleCheck()
    {
        if (_checkFor is not { } f || f == CheckState()) return;
        _checkGen++;
        _checkFor = null;
        CheckRows.Clear(); CheckSummary = ""; CheckedAt = "";
        Checking = false;
    }

    /// <summary>«Проверить сейчас» — без прав: HTTPS-проверка целей с DoH, winws не нужен. Не занимает окно: идёт рядом с другими действиями.</summary>
    public async Task CheckNowAsync()
    {
        var groups = CheckedGroups();
        if (groups.Count == 0) { Message = "Нечего проверять: отметьте группы на вкладке «Подбор»."; return; }
        if (Checking) return;
        int gen = ++_checkGen;
        _checkFor = CheckState();
        CheckRows.Clear(); CheckedAt = "";
        CheckSummary = "Проверяю…";
        Checking = true;
        try
        {
            var dns = ProbeGroups is null ? CheckDnsAsync() : Task.CompletedTask;   // подмена проверки — и DNS без сети не трогаем
            var verdicts = ProbeGroups is { } probe ? await probe(groups) : await Task.Run(async () =>
            {
                using var doh = new DohResolver();
                return await new Selector(_shell.Paths, Shell.NewProbe(doh)).CheckGroupsAsync(groups, CancellationToken.None);
            });
            await dns;
            if (gen != _checkGen) return;   // пока проверяли, обход включили или выключили — итог уже не про это
            foreach (var v in verdicts) CheckRows.Add(CheckRow.From(v, Names));
            // В тех же единицах, что и отметки рядом (п. 5): группы, а не адреса; адреса — в «Подробнее».
            CheckSummary = $"Открывается {CheckGroups.Count(g => g.Ok)} из {CheckGroups.Count}";
            CheckedAt = $"в {DateTime.Now:HH:mm}";
        }
        catch (Exception e)
        {
            if (gen != _checkGen) return;
            CheckSummary = "";
            Fail("Проверка не удалась: " + Texts.Friendly(e));
        }
        finally
        {
            if (gen == _checkGen) Checking = false;
        }
        await RefreshStatusAsync();
    }

    /// <summary>Отмеченные группы целей. Свои адреса — из поля «Свои адреса», как на экране.</summary>
    IReadOnlyList<TargetGroup> CheckedGroups()
    {
        var want = Groups.Where(g => g.IsChecked).Select(g => g.Id).ToHashSet();
        var groups = _shell.DefaultTargets.Groups.Where(g => want.Contains(g.Id)).ToList();
        if (want.Contains("user"))
        {
            // То, что в поле «Свои адреса», — как видит человек, сохранено оно или нет.
            var mine = DraftTargets();
            if (mine.Count > 0) groups.Add(TargetSet.UserGroup(mine));
        }
        return groups;
    }

    List<string> DraftTargets() =>
        TargetsText.Split('\n').Select(DataStore.NormalizeDomain).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    // ---------- DNS ----------

    public ObservableCollection<DnsRow> DnsRows { get; } = [];

    bool _dnsSpoofed;
    /// <summary>Системный DNS подменяет адреса проверяемых сайтов (docs/research/dns.md).</summary>
    public bool DnsSpoofed { get => _dnsSpoofed; private set { if (Set(ref _dnsSpoofed, value)) RaiseIssues(); } }

    string _dnsSummary = "Ещё не проверялось.";
    public string DnsSummary { get => _dnsSummary; private set => Set(ref _dnsSummary, value); }

    bool _dnsChecking;
    public bool DnsChecking { get => _dnsChecking; private set { if (Set(ref _dnsChecking, value)) CommandManager.InvalidateRequerySuggested(); } }

    public ICommand CheckDnsCmd { get; }

    /// <summary>Сравнить системный DNS с DoH (без прав). Тихо: без Busy, ошибки — в сводку DNS.</summary>
    public async Task CheckDnsAsync()
    {
        if (DnsChecking || IsLoading) return;
        DnsChecking = true;
        DnsSummary = "Проверяю…";
        try
        {
            var hosts = CheckedGroups().SelectMany(g => g.Targets).Select(t => new Uri(t.Url).Host).Distinct().ToList();
            var list = await Task.Run(async () =>
            {
                using var doh = new DohResolver();
                return await Task.WhenAll(hosts.Select(h => DnsCheck.CompareAsync(h, doh)));
            });
            DnsRows.Clear();
            foreach (var c in list) DnsRows.Add(DnsRow.From(c));
            DnsSpoofed = list.Any(c => c.Spoofed);
            DnsSummary = list.Length == 0 ? "Нечего проверять: не выбраны группы."
                : DnsSpoofed ? "DNS провайдера подменяет адреса сайтов."
                : list.All(c => c.Doh.Length == 0) ? "Защищённый DNS не ответил — сравнить не с чем."
                : "Подмены нет.";
        }
        catch (Exception e) { DnsSummary = Texts.Friendly(e); }
        finally { DnsChecking = false; }
    }

    GroupNames? _names;
    /// <summary>Названия групп для подробностей по адресам (и контрольные — «Обычные сайты»).</summary>
    public GroupNames Names => _names ??= new GroupNames(_shell.DefaultTargets.Groups);

    // ---------- подбор ----------

    public ObservableCollection<GroupOption> Groups { get; } = [];
    public ObservableCollection<ScoreRow> Scores { get; } = [];

    bool _quick; // основной режим — полный; быстрый — «выключил и забыл, какая стояла»
    public bool Quick { get => _quick; set { if (Set(ref _quick, value)) Raise(nameof(Full)); } }
    public bool Full { get => !_quick; set => Quick = !value; }

    bool _selecting;
    public bool Selecting { get => _selecting; private set { if (Set(ref _selecting, value)) CommandManager.InvalidateRequerySuggested(); } }

    int _progress, _progressMax = 1;
    public int SelectProgress { get => _progress; private set => Set(ref _progress, value); }
    public int SelectProgressMax { get => _progressMax; private set => Set(ref _progressMax, value); }

    string _progressText = "";
    public string SelectProgressText { get => _progressText; private set => Set(ref _progressText, value); }

    string _result = "";
    public string ResultText { get => _result; private set { if (Set(ref _result, value)) Raise(nameof(HasResult)); } }
    public bool HasResult => ResultText.Length > 0;

    string _resultHint = "";
    public string ResultHint { get => _resultHint; private set => Set(ref _resultHint, value); }

    string? _bestId;
    public bool HasBest => _bestId is not null;

    ScoreRow? _selected;
    public ScoreRow? SelectedScore { get => _selected; set { if (Set(ref _selected, value)) CommandManager.InvalidateRequerySuggested(); } }

    public ICommand StartSelectCmd { get; }
    public ICommand StopSelectCmd { get; }
    public ICommand ApplyBestCmd { get; }
    public ICommand ApplySelectedCmd { get; }

    /// <summary>Отмеченные группы (выбор запомнит помощник вместе с подбором); null — проверять нечего, причина уже в строке состояния.</summary>
    IReadOnlyList<string>? TakeGroups()
    {
        var ids = Groups.Where(g => g.IsChecked).Select(g => g.Id).ToList();
        if (ids.Count == 0) { Message = "Отметьте, что проверять."; return null; }
        if (ids.Contains("user") && UserTargets.Count == 0 && Helper.Ready)
        {
            Message = "Список своих адресов пуст — заполните его на вкладке «Свои адреса» и сохраните.";
            return null;
        }
        return ids;
    }

    bool AnyChecked()
    {
        if (Groups.Any(g => g.IsChecked)) return true;
        Message = "Отметьте, что проверять.";
        return false;
    }

    /// <summary>Группы и режим для подбора на роутере. «Свои адреса» — роутерные, поэтому список ПК не проверяем.</summary>
    (IReadOnlyList<string> Groups, bool Full)? RouterSelectOptions()
    {
        var ids = Groups.Where(g => g.IsChecked).Select(g => g.Id).ToList();
        if (ids.Count == 0) { Message = "Отметьте, что проверять."; return null; }
        return (ids, Full);
    }

    bool _offerDeep;
    /// <summary>
    /// «Искать глубже» — только после подбора, который не нашёл стратегию для всего (UX-аудит, п. 30): рядом с «Начать подбор»
    /// равной кнопкой её нажимали по ошибке и ждали 15 минут.
    /// </summary>
    public bool OfferDeep { get => _offerDeep; private set => Set(ref _offerDeep, value); }

    void ResetResults()
    {
        Scores.Clear(); SelectedScore = null;
        ResultText = ""; ResultHint = ""; SetBest(null); OfferDeep = false;
        SelectProgress = 0; SelectIndeterminate = false;
        SelectProgressText = "Подготовка…";
    }

    /// <summary>Подбор — у помощника (запускает winws); ход и отмена — через канал.</summary>
    public async Task StartSelectAsync()
    {
        if (Busy) return;
        if (!AnyChecked()) return;
        if (await NeedHelperAsync() is not { } c) return;
        // Свои адреса проверяем после подключения: до установки их у помощника ещё нет.
        if (!await SaveDraftBeforeSelectAsync()) return;
        if (TakeGroups() is not { } ids) return;
        if (!ConfirmPauseIfRunning()) return;
        // Обход на роутере: подбор на ПК при нём неверен (router-api §6) — подобрать там или выключить там на время подбора.
        if (!await RouterGateAsync(select: true)) return;

        ResetResults();
        SelectProgressMax = Math.Max(1, Helper.Status?.Strategies.Count ?? _shell.Catalog.Strategies.Count);

        using var cts = new CancellationTokenSource();
        _selectCts = cts;
        Selecting = true;
        var progress = new Progress<SelectorProgress>(p =>
        {
            SelectProgressMax = Math.Max(1, p.Total);
            SelectProgress = p.Index;
            SelectProgressText = p.Message;
            if (p.Last is { } s) Scores.Add(new ScoreRow(s, Names));
            UpdateStatus();
        });
        var quick = Quick;
        try
        {
            await Guard("Подбор…", async () =>
            {
                try { ShowReport(await c.SelectAsync(quick, ids, progress, cts.Token)); }
                catch (OperationCanceledException) { ResultText = "Подбор остановлен."; }
            });
        }
        finally
        {
            _selectCts = null;
            Selecting = false;
            SelectProgressText = "";
            UpdateStatus();
        }
        await AutoApplyAsync();
        await RefreshStatusAsync();
        await RouterResumeAsync();
    }

    bool _indeterminate;
    /// <summary>Шаги глубокого поиска заранее не сосчитать — полоса «бегущая».</summary>
    public bool SelectIndeterminate { get => _indeterminate; private set => Set(ref _indeterminate, value); }

    bool _searching;

    public ICommand StartSearchCmd { get; }

    /// <summary>Глубокий поиск (architecture.md «Глубокий поиск»): от лучшей стратегии последнего подбора или от текущей.</summary>
    public async Task StartSearchAsync()
    {
        if (Busy) return;
        if (!AnyChecked()) return;
        if (await NeedHelperAsync() is not { } c) return;
        if (!await SaveDraftBeforeSelectAsync()) return;
        if (TakeGroups() is not { } ids) return;
        if (!ConfirmPauseIfRunning()) return;
        // Обход на роутере: подбор на ПК при нём неверен (router-api §6) — подобрать там или выключить там на время подбора.
        if (!await RouterGateAsync(select: true)) return;
        var baseId = _bestId ?? CurrentStrategyId;

        ResetResults();
        SelectProgressMax = 1;
        SelectIndeterminate = true;

        using var cts = new CancellationTokenSource();
        _selectCts = cts;
        _searching = true;
        Selecting = true;
        var progress = new Progress<SelectorProgress>(p =>
        {
            if (p.Total == 0)
            {
                // Шаги поиска: Index — номер варианта, Message — «роль: …».
                SelectIndeterminate = true;
                SelectProgressText = $"Ищу для {RoleTitle(p.Message)}: проверено вариантов — {p.Index}";
            }
            else
            {
                // Финал: полная проверка найденного.
                SelectIndeterminate = false;
                SelectProgressMax = Math.Max(1, p.Total);
                SelectProgress = p.Index;
                SelectProgressText = "Проверяю найденное: " + p.Message;
                if (p.Last is { } s) Scores.Add(new ScoreRow(s, Names));
            }
            UpdateStatus();
        });
        try
        {
            await Guard("Глубокий поиск…", async () =>
            {
                try { ShowSearch(await c.SearchAsync(baseId, ids, progress, cts.Token)); }
                catch (OperationCanceledException) { ResultText = "Поиск остановлен."; }
            });
        }
        finally
        {
            _selectCts = null;
            _searching = false;
            Selecting = false;
            SelectIndeterminate = false;
            SelectProgressText = "";
            UpdateStatus();
        }
        await AutoApplyAsync();
        await RefreshStatusAsync();
        await RouterResumeAsync();
    }

    static string RoleTitle(string message) => message.Split(':')[0] switch
    {
        "google" => "YouTube",
        "general" => "Discord и своих адресов",
        var r => r,
    };

    void ShowSearch(SearchReport r)
    {
        string? stopped = r.Stopped switch
        {
            null => null,
            "остановлено" => "Поиск остановлен",
            var s => char.ToUpper(s[0]) + s[1..],
        };
        if (r.Final is not { } f)
        {
            Scores.Clear();
            ResultText = stopped is null ? "Новых рабочих сочетаний не нашлось." : stopped + ".";
            if (stopped is null) ResultHint = "Попробуйте позже или в другой сети.";
            return;
        }
        ShowReport(f);
        if (f.Best is { Strategy.Source: "search" } best) ResultText = "Найдена новая: " + best.Strategy.Title;
        if (stopped is not null) ResultHint = stopped + ". " + ResultHint;
    }

    /// <summary>Подбор при включённом обходе: обход на время проверки стоит, стратегии меняются — предупредить.</summary>
    bool ConfirmPauseIfRunning()
    {
        if (!IsRunning) return true;
        return Confirm(
            "Обход сейчас включён. На время проверки он будет приостановлен, а стратегии будут меняться одна за другой — " +
            "сайты, звонки и загрузки могут прерываться.\n\nПосле проверки обход включится на лучшей найденной стратегии. Продолжить?");
    }

    public ICommand StopConflictsCmd => _stopConflictsCmd ??= RelayCommand.Async(StopConflictsAsync, () => !Busy && !IsLoading && HasConflicts);
    ICommand? _stopConflictsCmd;

    /// <summary>Остановить мешающие обходы одной кнопкой (через помощника: чужие службы и процессы — с правами).</summary>
    async Task StopConflictsAsync()
    {
        if (!Confirm(ConflictWarning + "\n\nОстановить их сейчас? Настройки этих программ не меняются — их можно будет запустить снова.")) return;
        StopForeignResult r = new([], []);
        if (!await WithHelper("Останавливаю другие программы обхода…", async c => r = await c.StopForeignAsync())) return;
        await RefreshStatusAsync();
        Message = r.Stopped.Count == 0 ? "Нечего останавливать." : "Остановлено: " + string.Join(", ", r.Stopped) + "."
            + (r.ComeBackOnReboot.Count > 0 ? $" Служба {string.Join(", ", r.ComeBackOnReboot)} запустится снова после перезагрузки — удалите её в программе, которая её поставила." : "");
    }

    string? _autoApplyId;

    /// <summary>После подбора/поиска: лучшая стратегия — в работу (architecture.md «Оценка и порядок»).</summary>
    async Task AutoApplyAsync()
    {
        if (_autoApplyId is not { } id) return;
        _autoApplyId = null;
        var title = StrategyTitle(id);
        if (await ApplyAsync(id))
        {
            SetBest(null);   // уже в работе — «Применить» не нужна
            ResultText = $"Применена: {title} — обход работает.";
            Message = ResultText;
        }
    }

    void SetBest(string? id)
    {
        _bestId = id;
        Raise(nameof(HasBest));
        CommandManager.InvalidateRequerySuggested();
    }

    void ShowReport(SelectionReport r)
    {
        foreach (var s in r.Scores) _titles[s.Strategy.Id] = s.Strategy.Title;
        // Итоговая таблица — в порядке оценки, лучшая отмечена.
        var ordered = r.Scores.OrderByDescending(s => s.GroupShare).ThenByDescending(s => s.TargetShare).ThenBy(s => s.AvgSeconds).ToList();
        var best = r.Best;
        Scores.Clear();
        foreach (var s in ordered) Scores.Add(new ScoreRow(s, Names) { IsBest = best is not null && s.Strategy.Id == best.Strategy.Id });
        SelectProgress = SelectProgressMax;
        // Новая статистика и найденные стратегии — в список на главной; запасные помощник запомнил сам.
        _ = ReloadChoicesAsync();

        // Готовые не помогли (или помогли не со всем), подбор дошёл до конца — предложить глубокий поиск; после него — уже нет.
        OfferDeep = !r.Cancelled && !r.NothingBlocked && !_searching && best is not { Perfect: true };
        if (r.NothingBlocked)
        {
            ResultText = "Без обхода всё открывается: блокировок нет или включён VPN.";
            return;
        }
        if (best is null)
        {
            ResultText = r.Cancelled ? "Подбор остановлен, подходящей стратегии пока нет." : "Ни одна стратегия не помогла.";
            if (!r.Cancelled)
                ResultHint = "Проверьте, что свои адреса набраны верно и открываются хотя бы через VPN, или добавьте другие адреса этих сайтов и запустите полный подбор.";
            return;
        }
        SetBest(best.Strategy.Id);
        ResultText = "Подходит: " + best.Strategy.Title;
        // Подбор дошёл до конца — лучшая сразу ставится в работу (после выхода из Guard, см. AutoApplyAsync).
        if (!r.Cancelled) _autoApplyId = best.Strategy.Id;
        var notes = new List<string>();
        if (!best.Perfect) notes.Add($"работают {best.GroupsPassed} из {best.GroupsTotal} групп");
        if (r.Cancelled) notes.Add("подбор остановлен, проверены не все стратегии");
        var blocked = string.Join(", ", r.TestedGroups.Select(g => g.Title));
        notes.Add("без обхода не открывалось: " + blocked);
        ResultHint = string.Join("; ", notes) + ".";
    }

    // ---------- свои адреса ----------

    string _targetsText = "";
    /// <summary>Черновик до «Сохранить»: сохраняет помощник.</summary>
    public string TargetsText
    {
        get => _targetsText;
        // Правка после «Сохранено адресов: N» — прежний итог уже не про то, что в поле.
        set { if (Set(ref _targetsText, value)) { TargetsMessage = ""; Raise(nameof(TargetsDirty)); } }
    }

    /// <summary>
    /// В поле не то, что сохранено (UX-аудит 2, п. 15; п. 33 первого круга): подбор берёт сохранённое у помощника,
    /// поэтому перед подбором и при уходе с вкладки черновик сохраняется (или об этом говорит тост).
    /// </summary>
    public bool TargetsDirty
    {
        get
        {
            var saved = UserTargets.Select(DataStore.NormalizeDomain).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            return !saved.SetEquals(DraftTargets());
        }
    }

    /// <summary>Ушли с «Своих адресов», не сохранив: с помощником — сохранить сами (прав не спросит), без него — тост «Сохранить».</summary>
    async Task SaveDraftOnLeaveAsync()
    {
        if (!TargetsDirty || Busy) return;
        if (Helper.Ready)
        {
            if (await SaveTargetsAsync()) ShowToast("Свои адреса сохранены.");
            return;
        }
        ShowToast("Свои адреса не сохранены — подбор и обход их не учтут.", "Сохранить", async () => await SaveTargetsAsync());
    }

    /// <summary>Перед подбором: несохранённые свои адреса — сохранить (помощник уже подключён), иначе подбор их молча не учтёт.</summary>
    async Task<bool> SaveDraftBeforeSelectAsync()
    {
        if (!TargetsDirty) return true;
        if (!await SaveTargetsAsync()) return false;
        Message = "Свои адреса сохранены — подбор их учтёт.";
        return true;
    }

    string _targetsMessage = "";
    public string TargetsMessage { get => _targetsMessage; private set => Set(ref _targetsMessage, value); }

    public ICommand SaveTargetsCmd { get; }

    async Task<bool> SaveTargetsAsync()
    {
        var lines = TargetsText.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        UserTargetsResult? r = null;
        if (!await WithHelper("Сохраняю адреса…", async c =>
            {
                r = await c.SetUserTargetsAsync(lines);
                await FetchAsync(full: false);
            }) || r is null)
            return false;
        if (r.Saved.Count > 0 && Groups.FirstOrDefault(g => g.Id == "user") is { } ug) ug.IsChecked = true;
        TargetsText = string.Join(Environment.NewLine, r.Saved);
        TargetsMessage = r.Rejected.Count == 0
            ? $"Сохранено адресов: {r.Saved.Count}."
            : $"Сохранено адресов: {r.Saved.Count}. Не распознаны и убраны: {string.Join(", ", r.Rejected)}.";
        Raise(nameof(TargetsDirty));
        return true;
    }

    // ---------- диагностика ----------

    string _serviceText = "", _driverText = "", _coreText = "";
    public string ServiceText { get => _serviceText; private set => Set(ref _serviceText, value); }
    public string DriverText { get => _driverText; private set => Set(ref _driverText, value); }
    public string CoreText { get => _coreText; private set => Set(ref _coreText, value); }

    bool _coreBroken;
    public bool CoreBroken { get => _coreBroken; private set => Set(ref _coreBroken, value); }

    public ObservableCollection<string> ConflictList { get; } = [];
    public ObservableCollection<string> VpnList { get; } = [];

    public string CoreFolder => _shell.Paths.Core;

    /// <summary>Путь для исключения Защитника — не переписывать руками (п. 16 аудита).</summary>
    public ICommand CopyCoreFolderCmd => _copyCoreFolderCmd ??= new RelayCommand(() =>
    {
        try { Clipboard.SetText(CoreFolder); Message = "Путь скопирован: " + CoreFolder; }
        catch (System.Runtime.InteropServices.ExternalException) { Fail("Не удалось скопировать — буфер обмена занят другой программой."); }
    });
    ICommand? _copyCoreFolderCmd;

    /// <summary>«Безопасность Windows» → параметры защиты от вирусов (там «Исключения»).</summary>
    public ICommand OpenDefenderCmd => _openDefenderCmd ??= new RelayCommand(() =>
    {
        try { Process.Start(new ProcessStartInfo("windowsdefender://threatsettings") { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Fail("Не удалось открыть «Безопасность Windows»: " + e.Message); }
    });
    ICommand? _openDefenderCmd;

    /// <summary>«Настройки» → «О программе»: версия окна и состояние помощника.</summary>
    public string AboutText => $"DpiShell {typeof(MainViewModel).Assembly.GetName().Version?.ToString(3)} · помощник: {HelperText}";

    public ICommand RefreshDiagCmd { get; }
    public ICommand RepairCoreCmd { get; }
    public ICommand OpenLogsCmd { get; }
    public ICommand OpenResultsCmd { get; }
    public ICommand RemoveServiceCmd { get; }

    bool _diagRefreshing;
    /// <summary>Идёт обновление «Диагностики» — кнопка показывает «Проверяю…» и не нажимается.</summary>
    public bool DiagRefreshing { get => _diagRefreshing; private set { if (Set(ref _diagRefreshing, value)) { Raise(nameof(RefreshButtonText)); CommandManager.InvalidateRequerySuggested(); } } }
    public string RefreshButtonText => DiagRefreshing ? "Проверяю…" : "Проверить снова";

    string _diagUpdated = "";
    public string DiagUpdated { get => _diagUpdated; private set => Set(ref _diagUpdated, value); }

    public async Task RefreshDiagAsync()
    {
        if (DiagRefreshing) return;
        DiagRefreshing = true;
        try
        {
            await FetchAsync(full: true);
            _ = RefreshPcDnsAsync();
            var st = Live;
            var d = Helper.Reader is null ? null : Helper.Details;
            var (svc, local, drv, conflicts, vpn) = await Task.Run(() =>
            {
                var s = ServiceManager.Query(ServiceManager.Name);
                return (s, st is null && s != SvcState.NotInstalled ? LocalServiceStrategy() : null, ServiceManager.Query("WinDivert"),
                    st?.Foreign ?? Conflicts.Find(_shell.Paths, _shell.Catalog.Strategies), Conflicts.ActiveVpnAdapters());
            });
            if (st is not null)
            {
                // Стратегию показываем ту, что реально стоит в службе Windows; чужой файл в службе — отдельно.
                ServiceText = st.RunMode == "process" ? "не используется (обход работает из программы)"
                    : Texts.Svc(st.Service)
                    + (st.ServiceStrategy is { } ss ? $" (стратегия: {StrategyTitle(ss)})" : st.StrategyId is { } id && st.Service != SvcState.NotInstalled ? $" (стратегия: {StrategyTitle(id)})" : "")
                    + (st.Service != SvcState.NotInstalled && !st.ServiceOurCore ? " — запускает чужой файл" : "");
                (_svc, _active) = (st.Service, st.Active);
                _coreProblem = CoreProblem(st.Core);
            }
            else
            {
                ServiceText = Texts.Svc(svc) + (local is { } l ? $" (стратегия: {StrategyTitle(l)})" : "");
                (_svc, _active) = (svc, svc == SvcState.Running);
                _localStrategy = local;
                _coreProblem = null;
            }
            DriverText = Texts.Svc(drv);
            if (d is not null)
            {
                var core = d.Core;
                CoreBroken = core.State != CoreState.Ok;
                CoreText = core.State switch
                {
                    CoreState.Ok => $"в порядке (версия {d.CoreVersion})",
                    CoreState.Missing => "не найдено — возможно, удалено антивирусом",
                    _ => "изменено, возможно антивирусом: " + string.Join(", ", core.BadFiles),
                };
                if (core.ExtraFiles.Count > 0) CoreText += $"; лишние файлы: {string.Join(", ", core.ExtraFiles)}";
            }
            else
            {
                CoreBroken = false;
                CoreText = Helper.State == HelperState.NotInstalled ? "проверяет помощник — он не установлен" : "проверяет помощник — он не отвечает";
            }
            ConflictList.Clear();
            foreach (var c in conflicts) ConflictList.Add($"{c.Name}. {c.Detail}");
            VpnList.Clear();
            foreach (var v in vpn) VpnList.Add(v);
            UpdateStatus();
            if (DnsRows.Count == 0) _ = CheckDnsAsync();
            // Проверки окружения (§4) и временные метки — только чтение: netsh show, sc query, реестр, процессы.
            var (ts, findings) = await Task.Run(() => (TcpTimestamps.IsEnabled(), SystemChecks.Run(_shell.Paths)));
            TimestampsText = TimestampsLine(ts);
            Findings.Clear();
            foreach (var f in findings) Findings.Add(new FindingRow(f.Ok, f.Title, f.Text));
            OnHelperChanged();
            DiagUpdated = $"обновлено в {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception e) { Fail(Texts.Friendly(e)); }
        finally { DiagRefreshing = false; }
    }

    string _timestampsText = "";
    /// <summary>Решение 8: метки включаются сами при «Включить» и перед подбором — здесь только показываем.</summary>
    public string TimestampsText { get => _timestampsText; private set => Set(ref _timestampsText, value); }

    public static string TimestampsLine(bool? enabled) => enabled switch
    {
        true => "включены (нужны части стратегий)",
        false => "выключены — включатся при включении обхода",
        _ => "не удалось узнать",
    };

    /// <summary>Проверки окружения (09-features-spec §4): ✓ — в порядке, ⚠ — что мешает и что сделать.</summary>
    public ObservableCollection<FindingRow> Findings { get; } = [];

    public ICommand ClearDiscordCmd { get; }

    /// <summary>Решение 5: кэш Discord (Cache, Code Cache, GPUCache) — с подтверждением; запущенный Discord закрывается. Права не нужны.</summary>
    async Task ClearDiscordAsync()
    {
        var found = await Task.Run(() => DiscordCache.Find());
        if (found.Folders.Count == 0) { Message = "Кэш Discord не найден — чистить нечего."; return; }
        bool running = await Task.Run(DiscordCache.IsRunning);
        if (!Confirm($"Удалить кэш Discord ({Mb(found.Bytes)})? Удаляются только папки Cache, Code Cache и GPUCache — вход, настройки и сообщения останутся, "
                     + "Discord просто загрузит всё заново."
                     + (running ? "\n\nDiscord сейчас открыт — программа закроет его. После очистки запустите Discord снова." : "")))
            return;
        DiscordCacheInfo? r = null;
        if (await Guard("Очищаю кэш Discord…", async () => r = await Task.Run(() =>
            {
                if (running) DiscordCache.Close();
                return DiscordCache.Clear();
            })) && r is not null)
            Message = $"Кэш Discord очищен: {Mb(r.Bytes)}." + (r.Failed.Count > 0 ? $" Не удалось удалить файлов: {r.Failed.Count} — они заняты." : "");
    }

    static string Mb(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024:0.#} МБ" : $"{Math.Max(1, bytes / 1024)} КБ";

    // ---------- оформление (настройки окна — %LocalAppData%) ----------

    void SaveSettings()
    {
        try { Settings.Save(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Fail(Texts.Friendly(e)); }
    }

    /// <summary>"system" / "light" / "dark" — в window.json, применяется сразу.</summary>
    public string Theme
    {
        get => ThemeManager.Normalize(Settings.Theme);
        set
        {
            value = ThemeManager.Normalize(value);
            if (value == Theme) return;
            Settings.Theme = value;
            SaveSettings();
            ThemeManager.Apply(value);
            Raise();
            Raise(nameof(ThemeAuto));
            Raise(nameof(ThemeLight));
            Raise(nameof(ThemeDark));
        }
    }

    // Переключатели: снятие отметки (её снимает соседний) ничего не меняет.
    public bool ThemeAuto { get => Theme == ThemeManager.Auto; set { if (value) Theme = ThemeManager.Auto; } }
    public bool ThemeLight { get => Theme == ThemeManager.Light; set { if (value) Theme = ThemeManager.Light; } }
    public bool ThemeDark { get => Theme == ThemeManager.Dark; set { if (value) Theme = ThemeManager.Dark; } }

    /// <summary>Крестик сворачивает в трей (иначе — закрывает программу; служба обхода работает в обоих случаях, обход из программы — только в трее).</summary>
    public bool CloseToTray => Settings.CloseAction != "exit";
    public bool CloseTray { get => CloseToTray; set { if (value) SetCloseAction("tray"); } }
    public bool CloseExit { get => !CloseToTray; set { if (value) SetCloseAction("exit"); } }
    void SetCloseAction(string a)
    {
        if (Settings.CloseAction == a) return;
        Settings.CloseAction = a;
        SaveSettings();
        Raise(nameof(CloseTray)); Raise(nameof(CloseExit));
    }

    /// <summary>Только без «Работать, даже когда окно закрыто» (UX-аудит 2, п. 12): с ней подсказка под галочкой уже всё сказала.</summary>
    public string CloseHint => ServiceMode ? "" : "Закрытие программы выключит обход.";
    public bool HasCloseHint => CloseHint.Length > 0;

    /// <summary>Восстановить ядро — помощник останавливает обход, ставит файлы заново и возвращает как было.</summary>
    Task RepairCoreAsync() => WithHelper("Восстанавливаю ядро…", async c =>
    {
        await c.CoreRepairAsync(new Progress<string>(s => Message = s));
        await RefreshDiagAsync();
        await RefreshStatusAsync();
    });

    /// <summary>«Удалить службу полностью» — помощник: служба обхода, WinDivert, временные метки TCP (если включали мы).</summary>
    async Task RemoveServiceAsync()
    {
        if (!Confirm(ServiceMode
                ? "Удалить службу обхода и драйвер WinDivert? Обход перестанет работать, пока вы снова не нажмёте «Включить»."
                : "Удалить службу обхода и драйвер WinDivert? Обход из программы, если включён, прервётся до следующего включения."))
            return;
        if (await WithHelper("Удаляю службу…", async c =>
            {
                await c.RemoveAllAsync();
                await RefreshDiagAsync();
                await RefreshStatusAsync();
            }))
            Message = "Служба и драйвер удалены. Помощника можно удалить отдельной кнопкой.";
    }

    public ICommand ClearHistoryCmd => _clearHistoryCmd ??= RelayCommand.Async(ClearHistoryAsync, () => !Busy && !IsLoading && !Selecting);
    ICommand? _clearHistoryCmd;

    async Task ClearHistoryAsync()
    {
        if (!Confirm("Удалить результаты подборов, статистику и логи?\nСвои адреса, найденные стратегии и включённый обход останутся.")) return;
        int n = 0;
        if (!await WithHelper("Очищаю историю…", async c => n = await c.ClearHistoryAsync())) return;
        await ReloadChoicesAsync();
        Message = $"История очищена: удалено файлов — {n}.";
    }

    /// <summary>
    /// «Папка результатов»: результаты лежат в закрытой папке — помощник отдаёт их (свои адреса уже скрыты),
    /// окно кладёт копию в %LocalAppData%\DpiShell\results и открывает.
    /// </summary>
    async Task OpenResultsAsync()
    {
        await FetchAsync(full: true);
        if (Helper.Reader is null || Helper.Results is not { } r)
        {
            Message = "Результаты подборов хранит помощник — появятся после первого подбора.";
            return;
        }
        var dir = Path.Combine(WindowSettings.DefaultDir, "results");
        try
        {
            Directory.CreateDirectory(dir);
            foreach (var f in r.Results)
            {
                var name = Path.GetFileName(f.Name);
                if (name.Length == 0 || f.Content is null) continue;
                File.WriteAllText(Path.Combine(dir, name), f.Content.ToJsonString(new System.Text.Json.JsonSerializerOptions
                { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Fail(Texts.Friendly(e)); return; }
        if (r.Results.Count == 0) Message = "Подборов ещё не было.";
        OpenFolder(dir);
    }

    void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception e) { Fail(Texts.Friendly(e)); }
    }
}
