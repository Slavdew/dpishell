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
    // «Сайты» (свои сайты и исключения), «Игры и программы» (запись подключений, профили, игровой режим, IP-списки), «Сеть» (блоки DNS, «Прокси», «Починить адреса»); порядок вкладок — по номерам.
    // К блоку «Сети» ведёт MainViewModel.OpenNet(NetBlock).
    public const int Home = 0, Select = 1, Sites = 2, Apps = 3, Net = 4, Diag = 5, Router = 6, Settings = 7, About = 8;
}

/// <summary>Блок вкладки «Сеть»: порядок на экране — DNS, «Прокси», «Починить адреса».</summary>
public enum NetBlock { Dns, Proxy, Hosts }

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
    CoreState _coreState;   // состояние ядра (данные): текст проблемы собирается при показе
    SvcState _svc = SvcState.Unknown;
    bool _active;   // оптимизация работает в своём режиме: служба запущена или winws помощника жив
    string? _draftStrategy;   // выбрана в списке без помощника — включится по «Включить»
    string? _localStrategy;   // без помощника: стратегия из настроек службы DpiShell (реестр читается без прав)
    bool _installing;
    bool _groupsFromHelper, _targetsFromHelper;

    /// <summary>Данные поставки: каталог, цели, пути (только чтение — папка программы закрыта).</summary>
    public Shell Shell => _shell;
    public HelperLink Helper { get; }
    public WindowSettings Settings { get; }
    public RouterViewModel Router { get; }
    /// <summary>Подключения программ: «Настроить под игру» и «Отладка подключений» (ТЗ 13).</summary>
    public AppsViewModel Apps { get; }
    /// <summary>Игровой режим и IP-списки — на вкладке «Игры и программы», в «Дополнительно» (09-features-spec §1–2).</summary>
    public NetViewModel Net { get; }
    /// <summary>«Починить адреса» — hosts (09-features-spec §3).</summary>
    public HostsViewModel Hosts { get; }
    /// <summary>Обновления данных (architecture.md «Обновления данных»).</summary>
    public UpdatesViewModel Updates { get; }
    /// <summary>Окно при входе в Windows (architecture.md «Окно, трей, автозапуск»).</summary>
    public Autostart Autostart { get; }
    /// <summary>«Отменить всё, что сделала DpiShell» (Настройки): журнал помощника и журнал окна.</summary>
    public ChangesViewModel Changes { get; }
    /// <summary>Язык окна: значок внизу меню, «Настройки» → «Язык», шаблон перевода (docs/localization.md).</summary>
    public LangViewModel Lang { get; }
    /// <summary>«Выгрузить шаблон перевода» («Настройки» → «Для опытных»): файл рядом с window.json, папка открывается.</summary>
    public ICommand ExportLangTemplateCmd => _exportLangCmd ??= new RelayCommand(() =>
    {
        try
        {
            var file = Lang.ExportTemplate();
            Message = L.T("settings.lang.template.saved", new { path = file });
            OpenFolderAction(Path.GetDirectoryName(file)!);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Fail(Texts.Friendly(e), e); }
    });
    ICommand? _exportLangCmd;
    /// <summary>Открыть папку в Проводнике (в тестах подменяется).</summary>
    public Action<string> OpenFolderAction { get; set; }
    /// <summary>Вкладка «Прокси»: системный прокси Windows, свои прокси, проверка.</summary>
    public ProxyViewModel Proxy { get; }
    /// <summary>Уведомление в трей (окно может быть свёрнуто): «Все адреса» закончились, адрес в hosts устарел.</summary>
    public event Action<string>? Notify;

    /// <summary>Вопрос «Да/Нет» (в тестах подменяется).</summary>
    public Func<string, bool> Confirm { get; set; } = text =>
    {
        UiGuard.Check(text);
        return MessageBox.Show(text, "DpiShell", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    };

    /// <summary>Вопрос об установке помощника (в тестах подменяется).</summary>
    public Func<string, bool> AskHelper { get; set; } = text =>
    {
        UiGuard.Check(text);
        return MessageBox.Show(text, "DpiShell", MessageBoxButton.OKCancel, MessageBoxImage.Information, MessageBoxResult.OK) == MessageBoxResult.OK;
    };

    public event Action? StatusChanged;

    public MainViewModel(Shell shell, HelperLink helper, WindowSettings settings)
    {
        _shell = shell;
        Helper = helper;
        Settings = settings;
        settings.StrategySortChanged += OnSortChanged;
        Helper.Changed += OnHelperChanged;
        Updates = new UpdatesViewModel(helper, shell, settings)
        {
            Guard = Guard,
            Need = NeedHelperAsync,
            Say = s => Message = s,
            Confirm = s => Confirm(s),
            AfterInstall = ReloadChoicesAsync,
            IsBusy = () => Busy,
        };
        Autostart = new Autostart(settings) { Say = s => Message = s, Fail = Fail };
        Lang = new LangViewModel(settings);
        shell.CheckDnsMode = CheckDns.Normalize(settings.CheckDns);

        RetryCmd = RelayCommand.Async(InitializeAsync, () => ShowLoadActions);
        OpenWithoutHelperCmd = RelayCommand.Async(OpenWithoutHelperAsync, () => ShowLoadActions);
        EnableCmd = RelayCommand.Async(EnableAsync, () => !Busy && !IsLoading);
        DisableCmd = RelayCommand.Async(DisableAsync, () => !Busy && !IsLoading);
        GoSelectCmd = new RelayCommand(() => Page = Pages.Select);
        GoAboutCmd = new RelayCommand(() => Page = Pages.About);
        GoDiagCmd = new RelayCommand(() => Page = Pages.Diag);
        GoProxyCmd = new RelayCommand(() => OpenNet(NetBlock.Proxy));
        CheckNowCmd = RelayCommand.Async(CheckNowAsync, () => !Busy && !IsLoading && !Checking);

        StartSelectCmd = RelayCommand.Async(StartSelectAsync, () => !Busy && !IsLoading);
        StartSearchCmd = RelayCommand.Async(StartSearchAsync, () => !Busy && !IsLoading);
        CheckDnsCmd = RelayCommand.Async(CheckDnsAsync, () => !IsLoading && !DnsChecking);
        StopSelectCmd = new RelayCommand(() => _selectCts?.Cancel(), () => Selecting);
        ApplyBestCmd = RelayCommand.Async(() => ApplyAsync(_bestId!), () => !Busy && _bestId is not null);
        ApplySelectedCmd = RelayCommand.Async(ApplySelectedAsync, () => !Busy && SelectedScore is { Usable: true } or { Unchecked: true });
        InitStrategies();   // свои стратегии: копирование, вставка, правка, удаление — MainViewModel.Strategies.cs

        SaveTargetsCmd = RelayCommand.Async(SaveTargetsAsync, () => !Busy);

        RefreshDiagCmd = RelayCommand.Async(RefreshDiagAsync, () => !IsLoading && !DiagRefreshing);
        RepairCoreCmd = RelayCommand.Async(RepairCoreAsync, () => !Busy && !IsLoading);
        OpenFolderAction = OpenFolder;
        OpenLogsCmd = new RelayCommand(() => OpenFolder(Path.Combine(WindowSettings.DefaultDir, "logs")));
        OpenResultsCmd = RelayCommand.Async(OpenResultsAsync);
        RemoveServiceCmd = RelayCommand.Async(RemoveServiceAsync, () => !Busy && !IsLoading);
        FixHelperCmd = RelayCommand.Async(async () => { if (await InstallHelperAsync()) Message = L.T("about.helper.installed"); }, () => !Busy);
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
        // Откат всего: окно ведёт свой журнал (автозапуск, роутер); системные изменения пишет помощник.
        var windowJournal = new ChangeJournal(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settings.File))!, "changes-window.jsonl"));
        var windowChanges = new ChangeUndoEngine(windowJournal);
        windowChanges.Register(new DelegateUndoer([ChangeKinds.Autostart], e => Task.FromResult(Autostart.Undo(e))));
        windowChanges.Register(new DelegateUndoer([ChangeKinds.RouterDns], Router.UndoDnsAsync));
        windowChanges.Register(new DelegateUndoer([ChangeKinds.RouterEnable], Router.UndoEnableAsync));
        windowChanges.Register(new DelegateUndoer([ChangeKinds.RouterAdvanced], Router.UndoAdvancedAsync));
        windowChanges.Register(new DelegateUndoer([ChangeKinds.RouterJoint], Router.UndoJointAsync));
        // «Вернуть как было» в карточке «Продвинутый режим» на роутере: откат записи журнала окна.
        Router.Advanced.Revert = async () =>
        {
            var entry = windowJournal.Active(ChangeKinds.RouterAdvanced).FirstOrDefault();
            return entry is null ? L.T("shell.undo.no_record") : AdvancedViewModel.UndoText((await windowChanges.UndoAsync(entry.Id)).Items.FirstOrDefault());
        };
        Autostart.Recorder = windowJournal;
        Router.Recorder = windowJournal;
        Proxy = new ProxyViewModel(helper, settings)
        {
            Recorder = windowJournal,
            Need = NeedHelperAsync,
            Guard = Guard,
            IsBusy = () => Busy,
            Say = s => Message = s,
            Confirm = s => Confirm(s),
        };
        Proxy.LoadList();
        windowChanges.Register(new DelegateUndoer([ChangeKinds.ProxyUser], Proxy.UndoAsync));
        Changes = new ChangesViewModel(helper, windowChanges)
        {
            Say = s => Message = s,
            Confirm = s => Confirm(s),
            Guard = Guard,
            // Отменили — всё, что окно показывает из данных помощника (сайты, hosts, DNS, профили), перечитать.
            AfterUndo = () => AfterImportAsync(),
        };
        WireRouterPc();   // оптимизация ПК и роутера вместе не работают (router-api §6) — MainViewModel.Router.cs
        InitPcDns();      // DNS этого компьютера — MainViewModel.Dns.cs
        InitAdvanced();   // продвинутый режим «сайты → стратегия» на этом ПК — MainViewModel.Advanced.cs
        InitJoint();      // совместная работа с роутером: роутер ведёт YouTube и Discord, ПК — свои сайты — MainViewModel.Joint.cs

        Net = new NetViewModel(helper)
        {
            Guard = Guard,
            Need = NeedHelperAsync,
            Say = s => Message = s,
            Fail = Fail,
            Confirm = s => Confirm(s),
            AfterChange = RefreshStatusAsync,
        };
        Net.Notify += s => Notify?.Invoke(s);
        Apps = new AppsViewModel(helper)
        {
            OptimizationRunning = () => IsRunning,
            // «Включить» ведёт на Главную; с «Игр» человек остаётся на месте — он пришёл настроить игру, а не смотреть итог.
            EnableOptimization = async () =>
            {
                int from = Page;
                await EnableAsync();
                if (Page == Pages.Home && IsRunning) Page = from;
            },
            Guard = Guard,
            Need = NeedHelperAsync,
            Say = s => Message = s,
            Fail = Fail,
            Confirm = s => Confirm(s),
            AfterChange = RefreshStatusAsync,
        };
        Apps.DetailRequested += () => { _detailOpened = true; Raise(nameof(ShowAdvanced)); };
        Hosts = new HostsViewModel(helper)
        {
            Need = NeedHelperAsync,
            Say = s => Message = s,
            Confirm = s => Confirm(s),
        };
        Hosts.Notify += s => { Message = s; Notify?.Invoke(s); };
        ClearDiscordCmd = RelayCommand.Async(ClearDiscordAsync, () => !Busy && !IsLoading);
        InitNet();        // вкладка «Сеть», меню, «Показывать роутер» — MainViewModel.Net.cs

        // Группы — по умолчанию; у помощника свой выбор — подставится, когда он ответит.
        var chosen = new AppState().Groups.ToHashSet();
        foreach (var g in _shell.DefaultTargets.Groups.Where(g => !g.Control)) Groups.Add(new(g.Id, g.Title, chosen.Contains(g.Id)));
        Groups.Add(new("user", L.T("select.group.user"), chosen.Contains("user")));
        InitHome();       // главная: помехи, итог проверки — MainViewModel.Home.cs
        UpdateStatus();
    }

    // ---------- помощник ----------

    /// <summary>Помощник не готов — у кнопок, которым нужны права, значок щита.</summary>
    public bool NeedsHelper => !Helper.Ready;

    /// <summary>Помощник есть, но не отвечает / устарел / чужой — текст и кнопка «Переустановить».</summary>
    public string HelperProblem => Helper.State is HelperState.NotResponding or HelperState.Denied or HelperState.Outdated ? Helper.Problem : "";
    public bool HasHelperProblem => HelperProblem.Length > 0;
    public string FixHelperText => L.T(Helper.State == HelperState.Outdated ? "about.helper.fix_update" : "about.helper.fix_reinstall");

    /// <summary>Состояние помощника — на вкладке «Информация и обновления».</summary>
    public string HelperText => Helper.State switch
    {
        HelperState.Connected => Helper.Reader?.HelperVersion is { Length: > 0 } v ? L.T("about.helper.state_running_v", new { version = v }) : L.T("about.helper.state_running"),
        HelperState.Outdated => L.T("about.helper.state_outdated"),
        HelperState.NotResponding or HelperState.Denied => Helper.Problem,
        HelperState.NotInstalled => L.T("about.helper.state_missing"),
        _ => L.T("about.helper.state_checking"),
    };
    public bool CanUninstallHelper => Helper.State is not (HelperState.NotInstalled or HelperState.Unknown);

    public ICommand FixHelperCmd { get; }
    public ICommand UninstallHelperCmd { get; }


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
            HelperState.Outdated => L.T("shell.helper.ask_update"),
            HelperState.NotResponding => L.T("shell.helper.ask_reinstall", new { problem = Helper.Problem }),
            HelperState.Denied => L.T("shell.helper.ask_reinstall_user", new { problem = Helper.Problem }),
            _ => L.T("shell.helper.ask_install"),
        };
        if (!AskHelper(question)) { Message = L.T("shell.helper.view_only"); return null; }
        if (!await InstallHelperAsync()) return null;
        // Порядок первого запуска (ТЗ av-exclusion, п. 10): помощник уже стоит и ядро сам не качает — вопрос об исключении, затем скачивание.
        return Helper.Client is { } c && await FirstRunCoreAsync(c, add: null) ? c : null;
    }

    /// <summary>Установка/обновление/переустановка: UAC → ожидание → подключение → данные помощника.</summary>
    async Task<bool> InstallHelperAsync()
    {
        if (_installing) return false;
        _installing = true;
        Busy = true; BusyText = L.T("shell.helper.busy_install"); Message = L.T("shell.helper.confirm_prompt");
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
                Fail(L.T("shell.helper.declined"));
                return false;
            case SetupResult.Failed:
                Fail(L.T("shell.helper.install_failed", new { error = r.Error }));
                return false;
        }
        Message = "";
        await AfterConnectAsync();
        return true;
    }

    async Task UninstallHelperAsync()
    {
        // Журнал изменений хранит помощник: после его удаления откатить по журналу уже нельзя — предложить сейчас.
        if (Helper.Ready)
        {
            await Changes.RefreshAsync();
            if (Changes.Count > 0 && Confirm(L.T("shell.helper.uninstall_undo_first", new { count = Changes.Count })))
                await Changes.UndoAllAsync(ask: false);
        }
        if (!Confirm(L.T("shell.helper.uninstall_confirm")))
            return;
        Busy = true; BusyText = L.T("shell.helper.busy_uninstall"); UpdateStatus();
        try
        {
            var r = await Helper.UninstallAsync();
            Message = r.Result switch
            {
                SetupResult.Done => L.T("shell.helper.uninstalled"),
                SetupResult.Declined => L.T("shell.helper.uninstall_declined"),
                _ => L.T("shell.helper.uninstall_failed", new { error = r.Error }),
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
        // Обход «живёт» на роутере — не включать его здесь, пока не узнаем, где мы (ReconcileRouterPcAsync).
        if (Router.BypassRemembered && Helper.Status is { RunMode: "process", Enabled: true, Active: false, StrategyId: { } deferred })
            _resumeDeferred = deferred;
        else if (Helper.Client is { } c && Helper.Status is { RunMode: "process", Enabled: true, Active: false, StrategyId: { } id })
        {
            try { await c.EnableAsync(id); }
            catch (Exception e) when (e is HelperException or HelperUnavailableException) { _error = Texts.Friendly(e); Fail(_error, e); }
            await FetchAsync(full: false);
        }
        await RefreshStatusAsync();
    }

    /// <summary>Перечитать у помощника: status, а полностью — ещё результаты и сведения. Помощника нет — ничего.</summary>
    async Task FetchAsync(bool full)
    {
        if (await Helper.FetchAsync(full) is { } error) Message = error;
        // Исключение Защитника — вместе с полными данными (подключение, «Диагностика», после действий), не каждым тиком: WMI у помощника не бесплатен.
        if (full) await RefreshAvAsync();
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
            TakeTargetsFromHelper(s);
            TakeExcludeFromHelper();
        }
        ReloadChoices();
        Net.Refresh();
        Apps.StatusChanged();
        Hosts.RefreshNow();
        _ = AdvancedPc.RefreshAsync();
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
            if (!Set(ref _page, value)) return;
            if (!Busy && Message.Length > 0 && Message != ReadyText) Message = ReadyText;   // сообщение от давнего действия не едет по вкладкам
            if (IsLoading) return;
            if (was == Pages.Sites) _ = SaveDraftOnLeaveAsync();
            if (value == Pages.Apps) _ = RefreshNetAsync();
            if (value == Pages.Net) _ = RefreshNetTabAsync();
            if (value == Pages.Settings) _ = Changes.RefreshAsync();
            if (value == Pages.Router) _ = Router.RefreshRouterPageAsync();   // и «Роутер: что мешает» — внутри
            if (value == Pages.Diag || value == Pages.About) _ = RefreshDiagAsync();   // «Информация и обновления» показывает версию ядра
        }
    }

    async Task RefreshNetAsync()
    {
        await FetchAsync(full: true);
        Net.Refresh();
        await Apps.RefreshAsync();
    }

    bool _busy;
    public bool Busy { get => _busy; private set { if (Set(ref _busy, value)) { Raise(nameof(CanChoose)); Raise(nameof(ChooseBlockedText)); Raise(nameof(StopConflictsTip)); CommandManager.InvalidateRequerySuggested(); } } }

    string _busyText = "";
    public string BusyText { get => _busyText; private set => Set(ref _busyText, value); }

    string _message = "";
    /// <summary>Строка состояния внизу окна.</summary>
    public string Message
    {
        get => _message;
        set
        {
            if (!Set(ref _message, value)) return;
            if (value.Length > 0 && value != ReadyText) ScheduleReady(value);
        }
    }

    public static string ReadyText => L.T("common.ready");
    /// <summary>Сколько сообщение висит в строке состояния, прежде чем вернётся «Готово.».</summary>
    public TimeSpan MessageLifetime { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Старое сообщение не должно висеть вечно: через 10 с (если не сменилось) и при смене вкладки — «Готово.». Без потока окна (тесты) таймера нет.</summary>
    void ScheduleReady(string shown)
    {
        if (SynchronizationContext.Current is not { } ui) return;
        _ = Task.Delay(MessageLifetime).ContinueWith(_ => ui.Post(_ => { if (Message == shown) Message = ReadyText; }, null), TaskScheduler.Default);
    }

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
            Fail(_error, e);
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
    public bool IsLoading { get => _isLoading; private set { if (Set(ref _isLoading, value)) { Raise(nameof(ShowOverlay)); Raise(nameof(ShowLoadActions)); Raise(nameof(CanChoose)); Raise(nameof(ChooseBlockedText)); Raise(nameof(StopConflictsTip)); CommandManager.InvalidateRequerySuggested(); } } }

    bool _loadFailed;
    public bool LoadFailed { get => _loadFailed; private set { if (Set(ref _loadFailed, value)) { Raise(nameof(ShowOverlay)); Raise(nameof(ShowLoadActions)); CommandManager.InvalidateRequerySuggested(); } } }

    public bool ShowOverlay => IsLoading;

    string _loadingText = L.T("shell.loading.connecting");
    public string LoadingText { get => _loadingText; private set => Set(ref _loadingText, value); }

    /// <summary>
    /// Старт окна без прав: подключиться к помощнику (нет — режим просмотра), данные — от него или из поставки.
    /// Ядро ставит и проверяет помощник при своём запуске.
    /// </summary>
    public async Task InitializeAsync()
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        IsLoading = true; LoadFailed = false; LoadSlow = false; LoadingText = L.T("shell.loading.connecting");
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
        LoadingText = L.T("shell.loading.slow");
        LoadSlow = true;
    }

    /// <summary>«Открыть без помощника»: не ждать его — окно в режиме просмотра; помощник подхватится сам, когда ответит (TickAsync).</summary>
    public async Task OpenWithoutHelperAsync()
    {
        _loadCts?.Cancel();
        LoadFailed = false;
        try { await OpenAsync(); }
        catch (Exception e) { Fail(Texts.Friendly(e), e); }
    }

    /// <summary>Снять заставку и наполнить окно: от помощника, если он на связи, иначе — то, что видно без прав.</summary>
    async Task OpenAsync()
    {
        IsLoading = false; LoadSlow = false;
        // Роутер ищем сразу и параллельно: его нет — блок просто не появится.
        var router = Router.RefreshAsync();
        if (Helper.Reader is not null) await AfterConnectAsync();
        else
        {
            ApplyHelperData();
            await RefreshStatusAsync();
            // Первый запуск: Защитник работает — карточка предложит исключение до первого скачивания ядра.
            if (Helper.State == HelperState.NotInstalled) await ProbeLocalAvAsync();
        }
        _ = ReconcileRouterPcAsync(router);
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

    // Название и роли (данные): автоимя показывается по ролям и на языке окна при каждом чтении.
    readonly Dictionary<string, (string Title, IReadOnlyDictionary<string, string>? Roles)> _titles = [];

    /// <summary>Название для показа: автоимя найденной стратегии — по её параметрам (роли у помощника), своё имя — как есть.</summary>
    string ShownTitle(string id, string title) =>
        StrategyNaming.DisplayTitle(title, Helper.Details?.Found.FirstOrDefault(f => f.Id == id)?.Roles);

    /// <summary>Название по id — и готовых, и найденных поиском.</summary>
    string StrategyTitle(string? id)
    {
        if (id is null) return "";
        if (Helper.Status?.Strategies.FirstOrDefault(s => s.Id == id) is { } i) return ShownTitle(i.Id, i.Title);
        if (_shell.Catalog.Find(id) is { } s) return s.Title;
        return _titles.TryGetValue(id, out var t) ? StrategyNaming.DisplayTitle(t.Title, t.Roles) : id;
    }

    /// <summary>«YouTube, Discord» — группы, не открывшиеся в последней проверке «Проверить сейчас»; null — проверки нет или открылось всё.</summary>
    string? FailingCheckGroups()
    {
        if (Checking || CheckGroups.Count == 0) return null;
        var bad = CheckGroups.Where(g => !g.Ok || g.Browser).Select(g => g.Title).ToList();
        return bad.Count == 0 ? null : string.Join(", ", bad);
    }

    /// <summary>Заголовок и подробности статуса из состояния (без побочных действий): вызывается и при смене языка.</summary>
    void ComputeStatus()
    {
        var err = _error ?? CoreProblemText;
        var id = CurrentStrategyId;
        bool partial = false;
        if (Selecting) { Kind = StatusKind.Busy; StatusTitle = L.T(_searching ? "home.status.deep" : "home.status.select"); StatusDetail = SelectProgressText; }
        else if (Busy) { Kind = StatusKind.Busy; StatusTitle = BusyText; StatusDetail = ""; }
        else if (err is not null) { Kind = StatusKind.Error; StatusTitle = L.T("home.status.error"); StatusDetail = err; }
        else if (_active)
        {
            Kind = StatusKind.On; StatusTitle = L.T("home.status.running");
            // Совместная работа: тем же местом, одной короткой фразой, кто что ведёт.
            var joint = Router.JointOn && Router.HasJointStatus ? Router.JointStatus : null;
            StatusDetail = id is null ? joint ?? "" : joint is null ? L.T("home.strategy", new { name = StrategyTitle(id) }) : L.T("home.strategy_joint", new { name = StrategyTitle(id), joint });
            // Проверка нашла, что что-то не открывается: заголовок не зелёный «Работает» (UX-аудит 6, п. 7).
            if (FailingCheckGroups() is { } bad)
            {
                partial = true; StatusTitle = L.T("home.status.partial");
                StatusDetail = id is null ? L.T("home.status.partial_plain", new { groups = bad }) : L.T("home.status.partial_strategy", new { groups = bad, name = StrategyTitle(id) });
            }
        }
        else if (ServiceMode && _svc == SvcState.StartPending) { Kind = StatusKind.Busy; StatusTitle = L.T("home.status.starting"); StatusDetail = ""; }
        else if (Router.Found && Router.RouterEnabled)
        {
            // Обход делает роутер — для этого ПК тоже: не «Выключено» (читается как «не работает», UX-аудит 2, п. 3).
            Kind = StatusKind.Off; StatusTitle = L.T("home.status.router");
            StatusDetail = L.T("home.status.router_detail");
            // «Включать не нужно» — только когда открылось всё; иначе заголовок про то, что не открылось.
            if (FailingCheckGroups() is { } bad) { partial = true; StatusTitle = L.T("home.status.partial"); StatusDetail = L.T("home.status.partial_router", new { groups = bad }); }
        }
        else
        {
            Kind = StatusKind.Off; StatusTitle = L.T("home.status.off");
            StatusDetail = id is not null ? L.T("home.strategy", new { name = StrategyTitle(id) }) : L.T("home.status.off_hint");
        }
        // Через роутер — зелёный кружок: обход работает, хоть и не на этом ПК (значок в трее — про этот ПК).
        _partial = partial;
        StatusBrush = partial ? BrushOf(StatusKind.Busy) : BrushOf(Kind == StatusKind.Off && Router.Found && Router.RouterEnabled ? StatusKind.On : Kind);
    }

    void UpdateStatus()
    {
        ComputeStatus();
        var id = CurrentStrategyId;
        DropStaleCheck();
        MaybeAutoCheck();
        Raise(nameof(ShowSiteFix));
        Raise(nameof(IsRunning)); Raise(nameof(ResultApplied));
        Raise(nameof(ToggleText)); Raise(nameof(PcSecondary)); Raise(nameof(PcPrimary));
        Raise(nameof(CanChoose)); Raise(nameof(ChooseBlockedText)); Raise(nameof(StopConflictsTip)); RaiseSitesDirty();
        Router.PcChanged();
        Raise(nameof(StrategyHeader));
        RaiseRunMode();
        // Автозапуск по умолчанию следует за режимом — когда режим известен (помощник ответил).
        if (Live is not null) Autostart.OnRunMode(ServiceMode);
        Updates.Refresh();
        RaiseIssues();
        // Стратегию могли сменить с «Подбора» или автопереключением — список показывает текущую, не вызывая перезапуск.
        if (_chosen != id) { _chosen = id; Raise(nameof(ChosenStrategyId)); }
        StatusChanged?.Invoke();
    }

    // Хранятся имена (данные), текст собирается при показе — при смене языка он перечитывается сам.
    string _vpnNames = "";
    readonly LiveText _vpnWarning = new();
    string VpnNames
    {
        set
        {
            _vpnNames = value;
            _vpnWarning.Set(() => value.Length == 0 ? "" : L.T("home.vpn.warn", new { names = value }));
            Raise(nameof(VpnWarning)); Raise(nameof(HasVpn)); RaiseIssues();
        }
    }
    public string VpnWarning { get => _vpnWarning.Value; private set { _vpnWarning.Set(value); Raise(); Raise(nameof(HasVpn)); RaiseIssues(); } }
    /// <summary>Плашка VPN на главной: есть VPN и человек не сказал «Понятно» про этот же набор адаптеров.</summary>
    public bool HasVpn => VpnWarning.Length > 0 && _vpnNames != _vpnDismissed;

    string? _vpnDismissed;
    /// <summary>«Понятно, не показывать»: до смены набора VPN-адаптеров (в «Диагностике» VPN виден всегда).</summary>
    public ICommand DismissVpnCmd => _dismissVpnCmd ??= new RelayCommand(() => { _vpnDismissed = _vpnNames; Raise(nameof(HasVpn)); RaiseIssues(); });
    ICommand? _dismissVpnCmd;

    IReadOnlyList<Conflict> _conflicts = [];
    IReadOnlyList<Conflict> _dormant = [];
    /// <summary>
    /// Похожие программы (данные): мешающие и спящие (<see cref="Conflict.Dormant"/> — остановлены, запустятся с Windows).
    /// Главная и «Диагностика» — из одного списка: после «Остановить» обе показывают то, что есть сейчас.
    /// </summary>
    void SetConflicts(IReadOnlyList<Conflict> list)
    {
        _conflicts = [.. list.Where(c => !c.Dormant)];
        _dormant = [.. list.Where(c => c.Dormant)];
        _conflictsShown = list;
        RebuildConflictList();
        Raise(nameof(ConflictNames)); Raise(nameof(ConflictWarning)); Raise(nameof(HasConflicts)); RaiseIssues();
    }

    /// <summary>Похожие программы: от помощника (status) или сами, без прав. Помощник до foreignDormant присылает всё в Foreign.</summary>
    IReadOnlyList<Conflict> ForeignOf(HelperStatus? st) =>
        st is null ? Conflicts.Scan(_shell.Paths, _shell.Catalog.Strategies) : [.. st.Foreign, .. st.ForeignDormant ?? []];

    /// <summary>Имена спящих (остановлены, запустятся после перезагрузки).</summary>
    string DormantNames => string.Join(", ", _dormant.Select(c => c.NameNow).Distinct());
    /// <summary>Имена мешающих программ (для плашки и текста подтверждения «Остановить»).</summary>
    string ConflictNames => string.Join(", ", _conflicts.Select(c => c.NameNow).Distinct());
    public string ConflictWarning => _conflicts.Count == 0 ? "" : L.T("home.conflict.warn", new { names = ConflictNames });
    public bool HasConflicts => _conflicts.Count > 0;

    public ObservableCollection<CheckRow> CheckRows { get; } = [];

    readonly LiveText _checkSummary = new();
    public string CheckSummary { get => _checkSummary.Value; private set { _checkSummary.Set(value); Raise(); Raise(nameof(HeroDetail)); } }
    void CheckSummaryIs(Func<string> build) { _checkSummary.Set(build); Raise(nameof(CheckSummary)); Raise(nameof(HeroDetail)); }
    /// <summary>Адреса последней проверки (данные): строки таблицы собираются заново при смене языка.</summary>
    IReadOnlyList<TargetVerdict>? _checkVerdicts;

    void RebuildCheckRows()
    {
        if (_checkVerdicts is not { } v || CheckRows.Count == 0) return;
        CheckRows.Clear();
        foreach (var x in v) CheckRows.Add(CheckRow.From(x, Names));
    }

    // ---------- выбор стратегии (отдельно от выключателя) ----------

    public ObservableCollection<StrategyChoice> StrategyChoices { get; } = [];

    /// <summary>Порядок списков стратегий (общий с роутером, хранится в window.json): по имени / по результату.</summary>
    public bool SortByName { get => Settings.SortByName; set => Settings.SetStrategySort(value); }
    public bool SortByResult { get => !Settings.SortByName; set => Settings.SetStrategySort(!value); }

    void OnSortChanged()
    {
        Raise(nameof(SortByName)); Raise(nameof(SortByResult));
        ReloadChoices();
        ResortScores();
    }

    /// <summary>Таблица подбора в выбранном порядке (по результату — как её собрал подбор).</summary>
    void ResortScores()
    {
        if (Scores.Count < 2) return;
        var sel = SelectedScore;
        var rows = Scores.ToList();
        var ordered = Settings.SortByName ? StrategyOrder.SortByName(rows, r => r.Title)
            : rows.OrderByDescending(r => r.Score.GroupShare).ThenByDescending(r => r.Score.TargetShare).ThenBy(r => r.Score.AvgSeconds).ToList();
        Scores.Clear();
        foreach (var r in ordered) Scores.Add(r);
        SelectedScore = sel;
    }

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
        // Порядок — тот же, что у подбора: обязательные группы, доля своих сайтов, время.
        var choices = new List<StrategyChoice>();
        foreach (var s in StrategyStats.Order(list, x => x.Id, stats))
        {
            // Только последний прогон: прошлые подборы о сегодняшней сети не говорят.
            if (stats.TryGetValue(s.Id, out var st) && st.Runs > 0)
            {
                Names.UserGroupNames = SavedGroupNames();
                var (note, warning) = StrategyChoice.Describe(st, Names);
                choices.Add(new(s.Id, ShownTitle(s.Id, s.Title), note, s.Source == "search", warning, StrategyChoice.LastDetails(st, Names), ParamsOf(s.Id)) { Opened = StrategyOrder.Opened(st), Tip = TipFor(s.Id, s.Title), Source = s.Source });
            }
            else choices.Add(new(s.Id, ShownTitle(s.Id, s.Title), L.T("home.strategy.not_checked_yet"), s.Source == "search", parameters: ParamsOf(s.Id)) { Tip = TipFor(s.Id, s.Title), Source = s.Source });
        }
        StrategyChoices.Clear();
        foreach (var c in Settings.SortByName ? StrategyOrder.SortByName(choices, c => c.Title) : StrategyOrder.SortByOpened(choices, c => c.Opened))
            StrategyChoices.Add(c);
        _chosen = CurrentStrategyId;
        Raise(nameof(ChosenStrategyId));
    }

    /// <summary>Подсказка к названию в списке: у смешанной — из чего собрана (полными именами), у найденной — прежняя запись с датой.</summary>
    string TipFor(string id, string title) =>
        StrategyOf(id) is { Parts: { } parts } && MixBuilder.Composition(parts, StrategyTitle) is { Length: > 0 } c ? ShownTitle(id, title) + "\n" + c
        : StrategyNaming.Tip(title, Helper.Details?.Found.FirstOrDefault(f => f.Id == id)?.Roles);

    /// <summary>Роли стратегии и командная строка winws с режимами, что сейчас у помощника; найденные поиском — от помощника. У смешанной — ещё состав.</summary>
    string ParamsOf(string id)
    {
        var s = Helper.Details?.Found.FirstOrDefault(x => x.Id == id) ?? _shell.Catalog.Find(id);
        if (s is null) return "";
        var f = Helper.Status?.Features;
        var full = StrategyParams.Full(s, _shell.Paths, f is null ? BuildOptions.Default : new(f.Ipset, f.Game));
        return MixBuilder.Composition(s.Parts, StrategyTitle) is { Length: > 0 } c ? L.T("home.params.composition", new { composition = c, @params = full }) : full;
    }

    /// <summary>Положить текст в буфер обмена (в тестах — подмена).</summary>
    public Action<string> CopyText { get; set; } = text => Clipboard.SetText(text);

    public ICommand ToggleParamsCmd => _toggleParamsCmd ??= new RelayCommand(p => { if (p is StrategyChoice c) c.ShowParams = !c.ShowParams; });
    ICommand? _toggleParamsCmd;

    public ICommand CopyParamsCmd => _copyParamsCmd ??= new RelayCommand(p =>
    {
        if (p is not StrategyChoice { HasParams: true } c) return;
        try { CopyText($"{c.Title}\n{c.Params}"); Message = L.T("home.params.copied", new { name = c.Title }); }
        catch (System.Runtime.InteropServices.ExternalException) { Fail(L.T("home.clipboard_busy")); }
    });
    ICommand? _copyParamsCmd;

    string? _chosen;
    /// <summary>Выбранная стратегия. Обход включён — перезапуск на новой; выключен — запоминаем (у помощника или в окне).</summary>
    public string? ChosenStrategyId
    {
        get => _chosen;
        set
        {
            if (value is null || value == _chosen) return;
            // Своя или вставленная без единой проверки — только с подтверждением; отказ возвращает прежний выбор в списке.
            if (!ConfirmUnchecked(value))
            {
                if (Application.Current?.Dispatcher is { } d) d.BeginInvoke(() => Raise(nameof(ChosenStrategyId)));
                else Raise(nameof(ChosenStrategyId));
                return;
            }
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
        catch (Exception e) when (e is HelperException or HelperUnavailableException) { Fail(Texts.Friendly(e), e); }
        UpdateStatus();
    }

    public string StrategyHeader => CurrentStrategyId is { } id ? L.T("home.strategy", new { name = StrategyTitle(id) }) : L.T("home.strategy.none");

    // ---------- режим работы ----------

    /// <summary>Галочка «Режим службы» (режим — у помощника). Переключение — через помощника; обход, если работал, продолжает.</summary>
    public bool ServiceMode
    {
        get => Live?.RunMode != "process";
        set { if (value != ServiceMode) _ = SetRunModeAsync(value); }
    }

    public string RunModeHint => L.T(ServiceMode ? "settings.run_mode.hint_service" : "settings.run_mode.hint_process");

    /// <summary>
    /// Главная, под «Включить»: без службы — кнопка «Установить службу»; в режиме службы — надпись. «Служба установлена» — только когда
    /// служба Windows уже есть (её создаёт первое включение оптимизации); до этого честнее «Режим службы».
    /// </summary>
    public bool ShowInstallService => !ServiceMode;
    public bool ShowServiceInstalled => ServiceMode;
    public string ServiceStateText => L.T(ServiceExists ? "home.service.installed" : "home.service.mode");
    public string ServiceStateTip => ServiceExists ? "" : L.T("home.service.mode.tip");
    public bool HasServiceStateTip => !ServiceExists;
    bool ServiceExists => _svc is not (SvcState.NotInstalled or SvcState.Unknown);
    /// <summary>«Установить службу»: режим службы через помощника (нет помощника — предложит поставить, как у других действий).</summary>
    public ICommand InstallServiceCmd => _installServiceCmd ??= RelayCommand.Async(() => SetRunModeAsync(true), () => !Busy && !IsLoading);
    ICommand? _installServiceCmd;

    void RaiseRunMode()
    {
        Raise(nameof(ServiceMode)); Raise(nameof(RunModeHint)); Raise(nameof(CloseHint)); Raise(nameof(HasCloseHint));
        Raise(nameof(ShowInstallService)); Raise(nameof(ShowServiceInstalled)); Raise(nameof(ServiceStateText)); Raise(nameof(ServiceStateTip)); Raise(nameof(HasServiceStateTip));
    }

    async Task SetRunModeAsync(bool service)
    {
        await WithHelper(L.T(service ? "settings.run_mode.busy_service" : "settings.run_mode.busy_process"), async c =>
        {
            await c.SetRunModeAsync(service);
            await FetchAsync(full: false);
        });
        // И при отказе (идёт другая операция, нет помощника) — галочка показывает настоящий режим.
        RaiseRunMode();
        await RefreshStatusAsync();
    }

    /// <summary>Роутер найден — «Выключить на ПК»: на главной рядом его собственная кнопка.</summary>
    public string ToggleText => L.T(IsRunning ? (Router.Found ? "home.toggle.off_pc" : "home.toggle.off") : PcSecondary ? "home.toggle.on_pc" : "home.toggle.on");
    public bool CanChoose => !Busy && !IsLoading && !Selecting;

    /// <summary>Почему выбор стратегии сейчас недоступен (список серый).</summary>
    public string ChooseBlockedText => L.T(Selecting ? "home.choose_blocked.select" : IsLoading ? "home.choose_blocked.loading" : "home.choose_blocked.busy");
    public ICommand ToggleCmd => _toggleCmd ??= RelayCommand.Async(() => IsRunning ? DisableAsync() : EnableAsync(), () => !Busy && !IsLoading);
    ICommand? _toggleCmd;

    public ICommand RetryCmd { get; }
    public ICommand OpenWithoutHelperCmd { get; }
    public ICommand EnableCmd { get; }
    public ICommand DisableCmd { get; }
    public ICommand GoSelectCmd { get; }
    public ICommand GoDiagCmd { get; }
    /// <summary>«Настроить →» у находки «Системный прокси включён» на «Диагностике».</summary>
    public ICommand GoProxyCmd { get; }
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
                    ForeignOf(st),
                    Conflicts.ActiveVpnAdapters());
            });
            if (st is not null)
            {
                (_svc, _active) = (st.Service, st.Active);
                _coreState = st.Core;
            }
            else
            {
                // Без помощника видно только службу; winws режима без службы без помощника не работает.
                (_svc, _active) = (svc, svc == SvcState.Running);
                _localStrategy = strategy;
                _coreState = CoreState.Ok;
            }
            // Обход ПК заработал, а роутер уже «включён» (служба ПК поднялась с Windows, состояние пришло позже ответа роутера) — §6.
            if (_active && Router.Found && Router.RouterEnabled) OnRouterChanged();
            VpnNames = string.Join(", ", vpn);
            SetConflicts(conflicts);
        }
        catch (Exception e) { Fail(Texts.Friendly(e), e); }
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

    string? CoreProblemText => _coreState switch
    {
        CoreState.Ok => null,
        CoreState.Missing => L.T("home.core.missing"),
        _ => L.T("home.core.damaged"),
    };

    public async Task EnableAsync()
    {
        // Роутер уже обходит для всего дома — двойная обработка ломает сайты (router-api §6): подобрать там или выключить там.
        if (!IsRunning && !await RouterGateAsync(select: false)) return;
        // Роутер не отвечает, а человек включает обход здесь — его выбор: при следующем старте окно этот обход не выключит.
        if (!Router.Found) Router.ForgetBypass();
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

    public Task DisableAsync() => WithHelper(L.T("home.busy.disable"), async c =>
    {
        await c.DisableAsync();
        await RefreshStatusAsync();
    });

    /// <summary>Включить на стратегии; включилось — сразу «Проверить сейчас» (UX-аудит 2, п. 1): итог в карточке — про новое состояние.</summary>
    async Task<bool> ApplyAsync(string id)
    {
        bool ok = await WithHelper(L.T("home.busy.enable"), async c =>
        {
            // Выбранная руками — первая в запасных: это делает помощник вместе с включением.
            await c.EnableAsync(id);
            _draftStrategy = null;
            await RefreshStatusAsync();
            Page = Pages.Home;
        });
        if (ok) Router.JointRecheck();   // другая стратегия — другие приманки: роутеру они уходят сразу
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
        private set { if (Set(ref _checking, value)) { Raise(nameof(HasCheck)); RaiseIssues(); CommandManager.InvalidateRequerySuggested(); } }
    }

    /// <summary>Для какого состояния обхода (включён, стратегия) сделана проверка; другое состояние — итог устарел.</summary>
    (bool Active, string? Strategy)? _checkFor;
    int _checkGen;

    (bool, string?) CheckState() => (_active, _active ? CurrentStrategyId : null);

    /// <summary>Обход включили, выключили или сменили стратегию — прежний итог не про то, что сейчас: убрать.</summary>
    void DropStaleCheck()
    {
        if (_checkFor is not { } f || f == CheckState()) return;
        DropCheck();
    }

    /// <summary>Итог до включения — «было 1 из 2» в строке после включения (UX-аудит 7, п. 1). Null — прежней проверки нет.</summary>
    (int Ok, int Total)? _checkWas;

    void DropCheck()
    {
        if (!Checking && CheckGroups.Count > 0) _checkWas = (CheckGroups.Count(g => g.Ok && !g.Browser), CheckGroups.Count);
        _checkGen++;
        _checkFor = null;
        _checkVerdicts = null;
        CheckRows.Clear(); CheckSummary = ""; CheckedAt = ""; _checkedTime = null;
        Checking = false;
    }

    /// <summary>Проверять сайты сами, как только оптимизация заработала (на ПК или через роутер) и итога для этого состояния ещё нет. В тестах — выключено.</summary>
    public bool AutoCheck { get; set; }

    bool _covered;

    /// <summary>
    /// Оптимизация заработала — сразу «Проверить сейчас», чтобы было видно, стало ли лучше (в том числе при совместной работе и через роутер,
    /// где «Включить» нажимали не здесь). Включение из «Включить» проверяет само (ApplyAsync) — здесь остальные пути и запуск программы.
    /// </summary>
    void MaybeAutoCheck()
    {
        bool covered = Covered, was = _covered;
        _covered = covered;
        if (!covered) { if (was && _checkFor is not null) DropCheck(); return; }
        // Роутер включился, а состояние ПК то же: прежний итог (без оптимизации) уже не про это.
        if (!was && _checkFor is not null) DropCheck();
        if (!AutoCheck || IsLoading || Busy || Selecting || Checking || _checkFor is not null || CheckedGroups().Count == 0) return;
        _ = CheckNowAsync();
    }

    /// <summary>«Проверить сейчас» — без прав: HTTPS-проверка целей с DoH, winws не нужен. Не занимает окно: идёт рядом с другими действиями.</summary>
    public Task CheckNowAsync() => CheckNowCoreAsync();

    /// <summary>«Проверить сейчас»; итог — проверенные адреса (null — не проверяли, проверка не удалась или устарела по ходу).</summary>
    async Task<IReadOnlyList<TargetVerdict>?> CheckNowCoreAsync()
    {
        var groups = CheckedGroups();
        if (groups.Count == 0) { Message = L.T("home.check.nothing"); return null; }
        if (Checking) return null;
        IReadOnlyList<TargetVerdict>? result = null;
        int gen = ++_checkGen;
        // Повторная проверка в том же состоянии — «было» уже не про включение.
        if (_checkFor is not null && CheckGroups.Count > 0) _checkWas = null;
        _checkFor = CheckState();
        _checkVerdicts = null;
        CheckRows.Clear(); CheckedAt = "";
        CheckSummaryIs(() => L.T("home.check.running"));
        Checking = true;
        try
        {
            var dns = ProbeGroups is null ? CheckDnsAsync() : Task.CompletedTask;   // подмена проверки — и DNS без сети не трогаем
            // По DoH и, где DNS системы даёт другой адрес, — по пути браузера (BrowserCheck): только эти цели, по очереди.
            _shell.CheckDnsMode = CheckDnsMode;
            var verdicts = ProbeGroups is { } probe ? await probe(groups) : await Task.Run(() => _shell.CheckGroupsAsync(groups));
            await dns;
            if (ProbeGroups is null && AddressSourceNote(_shell.LastAddressSource, fragment: false, onlyFallback: true) is { } fb) Message = fb;
            if (gen != _checkGen) return null;   // пока проверяли, оптимизацию включили или выключили — итог уже не про это
            _checkVerdicts = verdicts;
            foreach (var v in verdicts) CheckRows.Add(CheckRow.From(v, Names));
            result = verdicts;
            // В тех же единицах, что и отметки рядом (п. 5): группы, а не адреса; адреса — в «Подробнее».
            CheckSummaryIs(() => L.T("home.check.summary", new { ok = CheckGroups.Count(g => g.Ok && !g.Browser), total = CheckGroups.Count }));
            // «Было» — итог до включения: виден, чтобы не гадать, помогло ли.
            var checkedAt = DateTime.Now;
            _checkedTime = checkedAt;
            CheckedAtIs(Covered && _checkWas is { } w ? () => L.T("home.check.at_was", new { time = L.Time(checkedAt), ok = w.Ok, total = w.Total }) : () => L.T("home.check.at", new { time = L.Time(checkedAt) }));
        }
        catch (Exception e)
        {
            if (gen != _checkGen) return null;
            CheckSummary = "";
            Fail(L.T("home.check.failed", new { reason = Texts.Friendly(e) }));
        }
        finally
        {
            if (gen == _checkGen) Checking = false;
        }
        await RefreshStatusAsync();
        return result;
    }

    /// <summary>
    /// Включили обход на роутере на пробу (router-api §5.2 confirm_s): «Проверить сейчас» с этого ПК.
    /// Как в подборе: судят обязательные адреса, контрольная группа не в счёт. true — открылись, false — нет, null — проверить нельзя.
    /// </summary>
    async Task<bool?> VerifyRouterFromPcAsync()
    {
        var control = CheckedGroups().Where(g => g.Control).Select(g => g.Id).ToHashSet();
        if (await CheckNowCoreAsync() is not { } verdicts) return null;
        var required = verdicts.Where(v => v.Target.Required && !control.Contains(v.GroupId)).ToList();
        return required.Count == 0 ? null : required.All(v => v.Ok);
    }

    /// <summary>Отмеченные группы целей. Свои сайты — из поля «Свои сайты», как на экране.</summary>
    IReadOnlyList<TargetGroup> CheckedGroups()
    {
        var want = Groups.Where(g => g.IsChecked).Select(g => g.Id).ToHashSet();
        var groups = _shell.DefaultTargets.Groups.Where(g => want.Contains(g.Id)).ToList();
        if (want.Contains("user"))
        {
            // То, что в поле «Свои сайты», — как видит человек, сохранено оно или нет.
            // Группы — как в окне; «Проверить сейчас» проверяет всё без выборки (выборка — для подбора).
            var mine = SiteGroups.Normalize(DraftGroups().Select(g => new SiteGroup(g.Name, g.Sites.Select(DataStore.NormalizeDomain).OfType<string>())), out _);
            if (mine.Any(g => g.Sites.Count > 0)) groups.Add(TargetSet.UserGroups(mine, int.MaxValue));
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

    readonly LiveText _dnsSummary = new(() => L.T("dns.summary.initial"));
    public string DnsSummary { get => _dnsSummary.Value; private set { _dnsSummary.Set(value); Raise(); } }
    void DnsSummaryIs(Func<string> build) { _dnsSummary.Set(build); Raise(nameof(DnsSummary)); }
    DnsComparison[]? _dnsList;   // ответы DNS (данные): строки таблицы и итог собираются на языке окна

    bool _dnsChecking;
    public bool DnsChecking { get => _dnsChecking; private set { if (Set(ref _dnsChecking, value)) CommandManager.InvalidateRequerySuggested(); } }

    bool _dnsDetailsOpen;
    /// <summary>«Подробнее: ответы DNS» раскрыт: открывается сам после «Проверить DNS» — итог там.</summary>
    public bool DnsDetailsOpen { get => _dnsDetailsOpen; set => Set(ref _dnsDetailsOpen, value); }

    public ICommand CheckDnsCmd { get; }

    /// <summary>Сравнить системный DNS с DoH (без прав). Тихо: без Busy, ошибки — в сводку DNS.</summary>
    public async Task CheckDnsAsync()
    {
        if (DnsChecking || IsLoading) return;
        // «Адреса для проверок — как у системы»: защищённый DNS не трогаем совсем, сравнивать не с чем.
        if (CheckDnsSystem)
        {
            _dnsList = [];
            DnsRows.Clear();
            DnsSpoofed = false;
            DnsSummaryIs(() => L.T("dns.summary.system"));
            return;
        }
        DnsChecking = true;
        DnsSummaryIs(() => L.T("dns.summary.checking"));
        try
        {
            var hosts = CheckedGroups().SelectMany(g => g.Targets).Select(t => new Uri(t.Url).Host).Distinct().ToList();
            var list = await Task.Run(async () =>
            {
                using var doh = new DohResolver();
                return await Task.WhenAll(hosts.Select(h => DnsCheck.CompareAsync(h, doh)));
            });
            _dnsList = list;
            DnsRows.Clear();
            foreach (var c in list) DnsRows.Add(DnsRow.From(c));
            DnsSpoofed = list.Any(c => c.Spoofed);
            DnsSummaryIs(() => list.Length == 0 ? L.T("dns.summary.no_groups")
                : DnsSpoofed ? L.T("dns.summary.spoofed")
                : list.All(c => c.Doh.Length == 0) ? L.T("dns.summary.no_secure")
                : L.T("dns.summary.clean"));
            if (DnsRows.Count > 0) DnsDetailsOpen = true;
        }
        catch (Exception e) { DnsSummary = Texts.Friendly(e); }
        finally { DnsChecking = false; }
    }

    void RebuildDnsRows()
    {
        if (_dnsList is not { } list || DnsRows.Count == 0) return;
        DnsRows.Clear();
        foreach (var c in list) DnsRows.Add(DnsRow.From(c));
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
    public bool Selecting { get => _selecting; private set { if (Set(ref _selecting, value)) { Raise(nameof(CanChoose)); Raise(nameof(ChooseBlockedText)); Raise(nameof(StopConflictsTip)); CommandManager.InvalidateRequerySuggested(); } } }

    int _progress, _progressMax = 1;
    public int SelectProgress { get => _progress; private set => Set(ref _progress, value); }
    public int SelectProgressMax { get => _progressMax; private set => Set(ref _progressMax, value); }

    string _progressText = "";
    public string SelectProgressText { get => _progressText; private set => Set(ref _progressText, value); }

    string _result = "";
    public string ResultText { get => _result; private set { if (Set(ref _result, value)) { Raise(nameof(HasResult)); Raise(nameof(ResultFull)); } } }
    public bool HasResult => ResultText.Length > 0;

    string _appliedLine = "";
    bool _resultExpanded;
    /// <summary>«✓ Включена general (ALT) · 2 из 2 · 83 %»: лучшая стратегия подбора уже в работе — итог сворачивается в эту строку.</summary>
    public string ResultSummary { get => _appliedLine; private set { if (Set(ref _appliedLine, value)) { Raise(nameof(ResultCompact)); Raise(nameof(ResultFull)); } } }
    /// <summary>«Подробнее» раскрыло прежнюю карточку итога; новый подбор снова показывает её целиком.</summary>
    public bool ResultExpanded { get => _resultExpanded; private set { if (Set(ref _resultExpanded, value)) { Raise(nameof(ResultCompact)); Raise(nameof(ResultFull)); } } }
    public bool ResultCompact => _appliedLine.Length > 0 && !_resultExpanded;
    public bool ResultFull => HasResult && !ResultCompact;
    public ICommand ShowResultDetailsCmd => _showResultDetailsCmd ??= new RelayCommand(() => ResultExpanded = true);
    ICommand? _showResultDetailsCmd;

    readonly LiveText _resultHint = new();
    public string ResultHint { get => _resultHint.Value; private set { _resultHint.Set(value); Raise(); } }
    void ResultHintIs(Func<string> build) { _resultHint.Set(build); Raise(nameof(ResultHint)); }

    string? _bestId, _resultId;
    public bool HasBest => _bestId is not null;

    /// <summary>Стратегия из итога уже в работе: в карточке вместо «Применить» — «Уже работает».</summary>
    public bool ResultApplied => _bestId is null && _resultId is not null && IsRunning && CurrentStrategyId == _resultId;

    /// <summary>Подсказка к названию итога: у смешанной — из каких стратегий она собрана (полными именами каталога).</summary>
    public string ResultTip => _resultId is not null && (Scores.FirstOrDefault(s => s.Id == _resultId)?.Score.Strategy ?? StrategyOf(_resultId)) is { Parts: { } parts }
        ? MixBuilder.Composition(parts, StrategyTitle) : "";

    string _deepText = "";
    /// <summary>Почему предлагается «Искать глубже»: сколько сервисов открылось у лучшей (без «не нашлось…», когда открылось всё).</summary>
    public string DeepText { get => _deepText; private set => Set(ref _deepText, value); }

    ScoreRow? _selected;
    public ScoreRow? SelectedScore { get => _selected; set { if (Set(ref _selected, value)) { RaiseResultGroupLines(); Raise(nameof(HasSelectedScore)); Raise(nameof(ApplySelectedText)); CommandManager.InvalidateRequerySuggested(); } } }
    public bool HasSelectedScore => _selected is not null;
    /// <summary>Выбрана лучшая — «Применить лучшую»; другая строка таблицы — «Применить выбранную».</summary>
    public string ApplySelectedText => L.T(_selected is { IsBest: true } ? "select.apply_best" : "select.apply_selected");

    public ICommand StartSelectCmd { get; }
    public ICommand StopSelectCmd { get; }
    public ICommand ApplyBestCmd { get; }
    public ICommand ApplySelectedCmd { get; }

    /// <summary>Отмеченные группы (выбор запомнит помощник вместе с подбором); null — проверять нечего, причина уже в строке состояния.</summary>
    IReadOnlyList<string>? TakeGroups()
    {
        var ids = Groups.Where(g => g.IsChecked).Select(g => g.Id).ToList();
        if (ids.Count == 0) { Message = L.T("select.pick_groups"); return null; }
        if (ids.Contains("user") && UserTargets.Count == 0 && Helper.Ready)
        {
            Message = L.T("select.user_empty");
            return null;
        }
        return ids;
    }

    bool AnyChecked()
    {
        if (Groups.Any(g => g.IsChecked)) return true;
        Message = L.T("select.pick_groups");
        return false;
    }

    /// <summary>Группы и режим для подбора на роутере. «Свои сайты» — роутерные, поэтому список ПК не проверяем.</summary>
    (IReadOnlyList<string> Groups, bool Full)? RouterSelectOptions()
    {
        var ids = Groups.Where(g => g.IsChecked).Select(g => g.Id).ToList();
        if (ids.Count == 0) { Message = L.T("select.pick_groups"); return null; }
        return (ids, Full);
    }

    bool _offerDeep;
    /// <summary>
    /// «Искать глубже» — только после подбора, который не нашёл стратегию для всего (UX-аудит, п. 30): рядом с «Начать подбор»
    /// равной кнопкой её нажимали по ошибке и ждали 15 минут.
    /// </summary>
    public bool OfferDeep { get => _offerDeep; private set => Set(ref _offerDeep, value); }

    readonly AddressBlockTracker _addressBlock = new();
    bool _addressNoteOn;
    /// <summary>«Не открылось ни с одной стратегией: … — похоже на блокировку по адресу…»; пусто, когда таких адресов нет. Собирается из учёта адресов при показе.</summary>
    string? _addressNoteFixed;   // готовая строка — только если её поставили извне (тесты)
    public string AddressNote { get => _addressNoteFixed ?? (_addressNoteOn ? Texts.WithNextStep(_addressBlock.Note(), router: false) : ""); private set { _addressNoteFixed = value; Raise(); Raise(nameof(HasAddressNote)); } }
    void SetAddressNote(bool on) { _addressNoteFixed = null; _addressNoteOn = on; Raise(nameof(AddressNote)); Raise(nameof(HasAddressNote)); }
    public bool HasAddressNote => AddressNote.Length > 0;

    void ResetResults()
    {
        SetAddressNote(false);
        Scores.Clear(); SelectedScore = null; _reportTested = [];
        ResultText = ""; ResultHint = ""; SetBest(null); _resultId = null; OfferDeep = false;
        ResultSummary = ""; ResultExpanded = false;
        SelectProgress = 0; SelectIndeterminate = false;
        SelectProgressText = L.T("select.preparing");
    }

    /// <summary>Подбор — у помощника (запускает winws); ход и отмена — через канал.</summary>
    public async Task StartSelectAsync()
    {
        if (Busy) return;
        if (!AnyChecked()) return;
        if (await NeedHelperAsync() is not { } c) return;
        // Свои сайты проверяем после подключения: до установки их у помощника ещё нет.
        if (!await SaveDraftBeforeSelectAsync()) return;
        if (TakeGroups() is not { } ids) return;
        if (!ConfirmPauseIfRunning()) return;
        // Обход на роутере: подбор на ПК при нём неверен (router-api §6) — подобрать там или выключить там на время подбора.
        if (!await RouterGateAsync(select: true)) return;

        _addressBlock.Clear();   // новый подбор — счёт проверок адресов заново (глубокий поиск продолжает прежний)
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
            if (p.Last is { } s) Scores.Add(new ScoreRow(s, Names, StrategyTitle));
            UpdateStatus();
        });
        var quick = Quick;
        try
        {
            await Guard(L.T("select.busy.select"), async () =>
            {
                try { ShowReport(await c.SelectAsync(quick, ids, progress, cts.Token, CheckDnsMode)); }
                catch (OperationCanceledException) { ResultText = L.T("select.stopped"); }
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
                SelectProgressText = L.T("select.search.progress", new { role = RoleTitle(p.Message), n = p.Index });
            }
            else
            {
                // Финал: полная проверка найденного.
                SelectIndeterminate = false;
                SelectProgressMax = Math.Max(1, p.Total);
                SelectProgress = p.Index;
                SelectProgressText = L.T("select.search.final", new { step = p.Message });
                if (p.Last is { } s) Scores.Add(new ScoreRow(s, Names, StrategyTitle));
            }
            UpdateStatus();
        });
        try
        {
            await Guard(L.T("select.busy.deep"), async () =>
            {
                try { ShowSearch(await c.SearchAsync(baseId, ids, progress, cts.Token, CheckDnsMode)); }
                catch (OperationCanceledException) { ResultText = L.T("select.search.stopped"); }
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
        "general" => L.T("select.search.role.general"),
        var r => r,
    };

    void ShowSearch(SearchReport r)
    {
        // Причина остановки приходит из ядра («остановлено» — отмена окном; прочее — текст ядра как есть).
        bool cancelled = r.Cancelled;
        string? stopped = r.Stopped is null || cancelled ? null : char.ToUpper(r.Stopped[0]) + r.Stopped[1..];
        if (r.Final is not { } f)
        {
            Scores.Clear();
            ResultText = r.Stopped is null ? L.T("select.search.none") : cancelled ? L.T("select.search.stopped") : stopped + ".";
            if (r.Stopped is null) ResultHint = L.T("select.search.none_hint");
            return;
        }
        ShowReport(f);
        if (f.Best is { Strategy.Source: "search" } best) ResultText = L.T("select.search.found", new { name = best.Strategy.Title });
        if (cancelled) ResultHint = L.T("select.search.stopped_hint", new { hint = ResultHint });
        else if (stopped is not null) ResultHint = stopped + ". " + ResultHint;
    }

    /// <summary>Подбор при включённом обходе: обход на время проверки стоит, стратегии меняются — предупредить.</summary>
    bool ConfirmPauseIfRunning()
    {
        if (!IsRunning) return true;
        return Confirm(L.T("select.confirm.pause"));
    }

    /// <summary>Подсказка серой «Остановить» у похожих программ: почему сейчас нельзя; пусто — кнопка доступна.</summary>
    public string StopConflictsTip => Busy || IsLoading ? L.T(IsLoading ? "diag.conflicts.stop_loading" : "diag.conflicts.stop_busy") : "";

    public ICommand StopConflictsCmd => _stopConflictsCmd ??= RelayCommand.Async(StopConflictsAsync, () => !Busy && !IsLoading && HasConflicts);
    ICommand? _stopConflictsCmd;

    /// <summary>Остановить мешающие обходы одной кнопкой (через помощника: чужие службы и процессы — с правами).</summary>
    async Task StopConflictsAsync()
    {
        if (!Confirm(L.T("home.conflict.confirm", new { names = ConflictNames }))) return;
        StopForeignResult r = new([], []);
        StopConflictsResult = "";
        if (!await WithHelper(L.T("home.conflict.busy"), async c => r = await c.StopForeignAsync())) return;
        await RefreshStatusAsync();
        StopConflictsResult = Message = StopForeignText(r);
    }

    /// <summary>Итог «Остановить» под кнопкой (выделяемый: причины и как остановить вручную); пусто — не нажимали.</summary>
    public string StopConflictsResult { get => _stopConflictsResult; private set { _stopConflictsResult = value; Raise(); Raise(nameof(HasStopConflictsResult)); } }
    string _stopConflictsResult = "";
    public bool HasStopConflictsResult => _stopConflictsResult.Length > 0;

    /// <summary>Правда об остановке: «Остановлено» — только остановленное; не вышло — что и почему, снова работает — что; и как остановить вручную.</summary>
    public static string StopForeignText(StopForeignResult r)
    {
        var parts = new List<string>();
        var stopped = string.Join(", ", r.Stopped);
        if (r.Stopped.Count > 0)
            parts.Add(r.ComeBackOnReboot.Count > 0 ? L.T("home.conflict.stopped_service", new { names = stopped, services = string.Join(", ", r.ComeBackOnReboot) })
                : L.T("home.conflict.stopped", new { names = stopped }));
        if (r.Failed is { Count: > 0 } failed) parts.Add(L.T("home.conflict.failed", new { items = string.Join("; ", failed) }));
        if (r.StillRunning is { Count: > 0 } still) parts.Add(L.T("home.conflict.still_running", new { names = string.Join(", ", still) }));
        if (parts.Count == 0) return L.T("home.conflict.none");
        if (r.Hints is { } hints) parts.AddRange(hints.Distinct());
        return string.Join(" ", parts);
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
            ResultText = L.T("select.applied", new { name = title });
            Message = ResultText;
            var row = Scores.FirstOrDefault(s => s.Id == id);
            ResultSummary = row is null ? L.T("select.summary.applied", new { name = title }) : L.T("select.summary.applied_stats", new { name = title, groups = row.Groups, share = row.Share });
        }
    }

    void SetBest(string? id)
    {
        _bestId = id;
        if (id is not null) _resultId = id;
        Raise(nameof(HasBest)); Raise(nameof(ResultApplied)); Raise(nameof(ResultTip));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Заголовок итога подбора: «Подходит» — только когда открылось всё и нет «подбор тут, скорее всего, не поможет»; иначе — «Лучшая из проверенных».</summary>
    public static string BestTitle(bool perfect, bool addressNote, string strategy, int ok = 0, int total = 0) =>
        !perfect && total > 0 ? L.T("select.best.of_checked_partial", new { name = strategy, ok, total })
        : L.T(perfect && !addressNote ? "select.best.fits" : "select.best.of_checked", new { name = strategy });

    void ShowReport(SelectionReport r)
    {
        _reportTested = r.TestedGroups;
        foreach (var s in r.Scores) _titles[s.Strategy.Id] = (s.Strategy.Title, s.Strategy.Roles);
        // Итоговая таблица — в порядке оценки, лучшая отмечена.
        var ordered = r.Scores.OrderByDescending(s => s.GroupShare).ThenByDescending(s => s.TargetShare).ThenBy(s => s.AvgSeconds).ToList();
        var best = r.Best;
        Scores.Clear();
        foreach (var s in ordered) Scores.Add(new ScoreRow(s, Names, StrategyTitle) { IsBest = best is not null && s.Strategy.Id == best.Strategy.Id });
        ResortScores();
        SelectedScore = Scores.FirstOrDefault(r => r.IsBest);   // лучшая выбрана сразу — «Применить лучшую» активна
        RaiseResultGroupLines();
        SelectProgress = SelectProgressMax;
        // Статистика до этого подбора — для подсказки «работала N раз подряд, сейчас нет»; перечитывается ниже.
        var regression = StrategyChoice.Regression(Helper.Results?.Stats, r, CurrentStrategyId, Names);
        // Новая статистика и найденные стратегии — в список на главной; запасные помощник запомнил сам.
        _ = ReloadChoicesAsync();

        // Готовые не помогли (или помогли не со всем), подбор дошёл до конца — предложить глубокий поиск; после него — уже нет.
        OfferDeep = !r.Cancelled && !r.NothingBlocked && !_searching && best is not { Perfect: true };
        if (TlsSupport.Tls13Client && TlsSupport.Tls12Only(r) != _tls12Seen) { _tls12Seen = !_tls12Seen; Raise(nameof(TlsText)); }
        foreach (var s in r.Scores) _addressBlock.Add(s);
        SetAddressNote(true);
        // Лучшая не открыла только то, что молчит у всех стратегий, — искать новые ради этого нечего.
        if (OfferDeep && best is not null && _addressBlock.ExplainsAllFailures(best.Verdicts.Where(v => !v.Ok).Select(v => v.Target.Url)))
            OfferDeep = false;
        // «Не нашлось…» — только пока у лучшей открылось не всё: «2 из 2» такой фразы не заслуживает.
        DeepText = best is null ? L.T("select.deep.none")
            : best.Perfect ? "" : L.T("select.deep.partial", new { ok = best.GroupsPassed, total = best.GroupsTotal });
        if (r.NothingBlocked)
        {
            ResultText = L.T("select.result.nothing_blocked");
            return;
        }
        if (best is null)
        {
            ResultText = L.T(r.Cancelled ? "select.result.cancelled_none" : "select.result.none");
            if (!r.Cancelled)
                ResultHint = (regression.Length > 0 ? regression + " " : "") + L.T("select.result.none_hint")
                    + (AddressSourceNote(r.AddressSource, fragment: false) is { } addr0 ? " " + addr0 : "")
                    + (TlsNote(r) is { } tls0 ? " " + char.ToUpper(tls0[0]) + tls0[1..] + "." : "");
            else ResultHint = regression;
            return;
        }
        SetBest(best.Strategy.Id);
        // «Подходит» — только когда открылось всё и рядом нет «подбор, скорее всего, не поможет»; иначе заголовок не обещает успех (UX-аудит 7, п. 7).
        ResultText = BestTitle(best.Perfect, HasAddressNote, best.Strategy.Title, best.GroupsPassed, best.GroupsTotal);
        // Подбор дошёл до конца — лучшая сразу ставится в работу (после выхода из Guard, см. AutoApplyAsync).
        if (!r.Cancelled) _autoApplyId = best.Strategy.Id;
        // Подсказка собирается из данных подбора при показе — на языке окна.
        ResultHintIs(() =>
        {
            var notes = new List<string>();
            if (!best.Perfect) notes.Add(L.T("select.note.partial", new { ok = best.GroupsPassed, total = best.GroupsTotal }));
            if (r.Cancelled) notes.Add(L.T("select.note.cancelled"));
            if (AddressSourceNote(r.AddressSource, fragment: true) is { } addr) notes.Add(addr);
            if (TlsNote(r) is { } tls) notes.Add(tls);
            if (MixBuilder.IsMix(best.Strategy.Source)) notes.Insert(0, L.T("select.note.mix"));
            var blocked = string.Join(", ", r.TestedGroups.Select(g => Names.Title(g.Id)));
            notes.Add(L.T("select.note.blocked", new { groups = blocked }));
            // Пояснения — фрагменты списка (в обоих языках со строчной): с заглавной начинается фраза целиком.
            var explain = string.Join("; ", notes) + ".";
            return char.ToUpper(explain[0]) + explain[1..] + (regression.Length > 0 ? " " + L.Relocalize(regression, own: true) : "");
        });
    }

    // ---------- свои сайты ----------

    // Черновик «Своих сайтов» (TargetsText) и группы — в MainViewModel.SiteGroups.cs. Подбор берёт сохранённое у помощника (UX-аудит 2, п. 15;
    // п. 33 первого круга), поэтому перед подбором и при уходе с вкладки черновик сохраняется (или об этом говорит тост).

    /// <summary>Ушли с «Своих сайтов», не сохранив: с помощником — сохранить сами (прав не спросит), без него — тост «Сохранить».</summary>
    async Task SaveDraftOnLeaveAsync()
    {
        if (!(TargetsDirty || ExcludeDirty) || Busy) return;
        if (Helper.Ready)
        {
            bool ok = !TargetsDirty || await SaveTargetsAsync();
            if (ok && ExcludeDirty) ok = await SaveExcludeAsync();
            if (ok) ShowToast(L.T("sites.toast.saved"));
            return;
        }
        ShowToast(L.T("sites.toast.not_saved"), L.T("sites.save"), async () => await SaveTargetsAsync());
    }

    /// <summary>Перед подбором: несохранённые свои сайты — сохранить (помощник уже подключён), иначе подбор их молча не учтёт.</summary>
    async Task<bool> SaveDraftBeforeSelectAsync()
    {
        if (!TargetsDirty) return true;
        if (!await SaveTargetsAsync()) return false;
        Message = L.T("select.user_saved");
        return true;
    }

    string _targetsMessage = "";
    /// <summary>«Сохранено: N» — только пока в поле то же, что сохранено; рядом с «Не сохранено» два статуса не показываем.</summary>
    public string TargetsMessage { get => TargetsDirty ? "" : _targetsMessage; private set => Set(ref _targetsMessage, value); }

    public ICommand SaveTargetsCmd { get; }

    async Task<bool> SaveTargetsAsync()
    {
        var lines = TargetsText.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        // С группами (или были сохранённые группы, которых теперь нет) — командой групп; без них — прежним плоским списком.
        bool withGroups = SiteGroupRows.Any(g => g.Named) || Helper.Status?.UserGroups is { Count: > 0 };
        UserTargetsResult? r = null;
        UserGroupsResult? gr = null;
        if (!await WithHelper(L.T("sites.targets.busy.save"), async c =>
            {
                if (withGroups && c.Compat.Supports("user_groups_set")) gr = await c.SetUserGroupsAsync(DraftGroups());
                else r = await c.SetUserTargetsAsync(lines);
                await FetchAsync(full: false);
            }) || (r is null && gr is null))
            return false;
        IReadOnlyList<string> savedSites = r?.Saved ?? SiteGroups.Flatten(gr!.Saved);
        IReadOnlyList<string> rejected = r?.Rejected ?? gr!.Rejected;
        // Свои сайты сохранены — подбор их проверяет (галочка группы сама); список пуст — галочка снимается.
        if (Groups.FirstOrDefault(g => g.Id == "user") is { } ug) ug.IsChecked = savedSites.Count > 0;
        if (gr is not null) LoadGroups(gr.Saved);
        else TargetsText = string.Join(Environment.NewLine, savedSites);
        TargetsMessage = rejected.Count == 0
            ? L.T("sites.targets.saved", new { n = savedSites.Count })
            : L.T("sites.targets.saved_rejected", new { n = savedSites.Count, rejected = string.Join(", ", rejected) });
        RaiseSitesDirty();
        Router.JointRecheck();   // свои сайты — часть связки: роутеру они уходят сразу, а не через минуту
        return true;
    }

    // ---------- диагностика ----------

    // Тексты «Диагностики» хранятся правилом сборки (данные в замыкании) и читаются на языке окна.
    readonly LiveText _serviceText = new(), _driverText = new(), _coreText = new();
    public string ServiceText { get => _serviceText.Value; private set { _serviceText.Set(value); Raise(); } }
    public string DriverText { get => _driverText.Value; private set { _driverText.Set(value); Raise(); } }
    public string CoreText { get => _coreText.Value; private set { _coreText.Set(value); Raise(); Raise(nameof(CoreRowText)); } }
    void ServiceTextIs(Func<string> build) { _serviceText.Set(build); Raise(nameof(ServiceText)); }
    void DriverTextIs(Func<string> build) { _driverText.Set(build); Raise(nameof(DriverText)); }
    void CoreTextIs(Func<string> build) { _coreText.Set(build); Raise(nameof(CoreText)); Raise(nameof(CoreRowText)); }
    /// <summary>Строка «Ядро» на «Информация и обновления»: пустая (ещё не проверяли) — «неизвестно».</summary>
    public string CoreRowText
    {
        get
        {
            // Версия — из ответа помощника, а пока его нет — из поставки (data\core.json рядом с программой).
            var version = string.IsNullOrEmpty(_coreVersion) ? _shell.Installer.Manifest.Version : _coreVersion;
            // Имена повреждённых файлов — на «Диагностике» («Подробнее»); здесь одна фраза.
            var state = _coreText.IsEmpty ? "" : _coreBroken ? L.T(_coreMissing ? "about.core.missing" : "about.core.damaged") : L.T("about.core.ok");
            return (version, state) switch
            {
                ({ Length: > 0 }, { Length: > 0 }) => L.T("about.core.row", new { version, state }),
                ({ Length: > 0 }, _) => version,
                (_, { Length: > 0 }) => state,
                _ => L.T("about.core.unknown"),
            };
        }
    }
    string? _coreVersion;
    bool _coreMissing;

    bool _coreBroken;
    public bool CoreBroken { get => _coreBroken; private set => Set(ref _coreBroken, value); }

    /// <summary>Мешающие сейчас: «Остановить» — для них.</summary>
    public ObservableCollection<string> ConflictList { get; } = [];
    /// <summary>Остановлены, но запустятся после перезагрузки — спокойная строка, без «Остановить».</summary>
    public ObservableCollection<string> DormantList { get; } = [];
    /// <summary>«Не найдены.» — ни мешающих, ни спящих.</summary>
    public bool NoConflicts => ConflictList.Count == 0 && DormantList.Count == 0;
    IReadOnlyList<Conflict>? _conflictsShown;

    void RebuildConflictList()
    {
        if (_conflictsShown is not { } list) return;
        ConflictList.Clear();
        DormantList.Clear();
        foreach (var c in list)
            if (c.Dormant) DormantList.Add(L.T("diag.conflict.dormant_line", new { name = c.NameNow, detail = c.DetailNow }));
            else ConflictList.Add(L.T("diag.conflict.line", new { name = c.NameNow, detail = c.DetailNow }));
        Raise(nameof(NoConflicts));
    }

    /// <summary>Смена языка: всё, что окно держит готовой строкой, собирается заново из данных (сообщения последних действий остаются).</summary>
    protected override void OnLanguageChanged()
    {
        // «Готово.» — не сообщение, а отсутствие сообщения: следует за языком; прочие — последние действия — остаются, фразы ядра переводятся.
        _message = _message == "" ? "" : L.BuiltIn.Values.Any(p => p.Strings.TryGetValue("common.ready", out var r) && r.Text == _message) ? ReadyText : L.Relocalize(_message, own: true);
        Retranslate(ref _result); Retranslate(ref _appliedLine); Retranslate(ref _deepText);
        foreach (var t in new[] { _resultHint, _checkSummary, _dnsSummary, _checkedAt, _timestampsText, _diagUpdated }) t.Retranslate();
        Raise(nameof(TlsText));
        // Главная: списки и строки, собранные на прежнем языке, — заново (вычисляемые свойства прочтутся после этого).
        _pcItems = _routerItems = null; _tiles = null; _autoRows = null; _homeShown.Clear();
        Retranslate(ref _directMessage); Retranslate(ref _toast); Retranslate(ref _toastAction);
        ComputeStatus();
        RebuildCheckRows();
        RebuildDnsRows();
        RebuildConflictList();
        PcDns.RefreshForLanguage();
        if (_pcDnsStatus is { } dnsStatus) ShowPcDns(dnsStatus);
        for (int i = 0; i < Findings.Count; i++) if (Findings[i].Source is not null) Findings[i] = Findings[i].Refreshed();
        // Список стратегий на главной: подписи («открыла всё · проверено …»), предупреждения, названия — из статистики; раскрытое остаётся.
        var open = StrategyChoices.Where(c => c.Expanded || c.ShowParams).ToDictionary(c => c.Id, c => (c.Expanded, c.ShowParams));
        if (StrategyChoices.Count > 0)
        {
            ReloadChoices();
            foreach (var c in StrategyChoices) if (open.TryGetValue(c.Id, out var f)) (c.Expanded, c.ShowParams) = f;
        }
    }
    public ObservableCollection<string> VpnList { get; } = [];

    public string CoreFolder => Demo.Path(_shell.Paths.Core, @"C:\Users\user\AppData\Local\DpiShell\core");

    /// <summary>Путь для исключения Защитника — не переписывать руками (п. 16 аудита).</summary>
    public ICommand CopyCoreFolderCmd => _copyCoreFolderCmd ??= new RelayCommand(() =>
    {
        try { Clipboard.SetText(CoreFolder); Message = L.T("diag.say.path_copied", new { path = CoreFolder }); }
        catch (System.Runtime.InteropServices.ExternalException) { Fail(L.T("diag.err.copy_busy")); }
    });
    ICommand? _copyCoreFolderCmd;

    /// <summary>«Безопасность Windows» → параметры защиты от вирусов (там «Исключения»).</summary>
    public ICommand OpenDefenderCmd => _openDefenderCmd ??= new RelayCommand(() =>
    {
        try { Process.Start(new ProcessStartInfo("windowsdefender://threatsettings") { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Fail(L.T("diag.err.defender", new { error = e.Message })); }
    });
    ICommand? _openDefenderCmd;

    /// <summary>«Информация и обновления» → «Версия»: версия окна и состояние помощника.</summary>
    public string AboutText => L.T("shell.about_text", new { version = typeof(MainViewModel).Assembly.GetName().Version?.ToString(3), helper = HelperText });

    public string AppVersionText => typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "";

    /// <summary>Переход на вкладку «Информация и обновления» (обновления, версия, лицензии).</summary>
    public ICommand GoAboutCmd { get; }

    public const string RepoUrl = "https://github.com/Slavdew/dpishell";
    public const string ZapretUrl = "https://github.com/bol-van/zapret";
    public static string NoticesPath => Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");
    public bool HasNotices => File.Exists(NoticesPath);
    public ICommand OpenRepoCmd => _openRepoCmd ??= new RelayCommand(() => OpenLink(RepoUrl));
    ICommand? _openRepoCmd;
    public const string IssuesUrl = RepoUrl + "/issues";
    /// <summary>«Сообщить о проблеме» — страница обращений на GitHub.</summary>
    public ICommand OpenIssuesCmd => _openIssuesCmd ??= new RelayCommand(() => OpenLink(IssuesUrl));
    ICommand? _openIssuesCmd;
    /// <summary>Частые вопросы и «как вернуть как было»: docs/FAQ.md (ru) или docs/FAQ.en.md (остальные языки) в репозитории.</summary>
    public static string FaqUrl => RepoUrl + "/blob/master/docs/" + (string.Equals(L.Current, "ru", StringComparison.OrdinalIgnoreCase) ? "FAQ.md" : "FAQ.en.md");
    public ICommand OpenFaqCmd => _openFaqCmd ??= new RelayCommand(() => OpenLink(FaqUrl));
    ICommand? _openFaqCmd;
    public ICommand OpenZapretCmd =>_openZapretCmd ??= new RelayCommand(() => OpenLink(ZapretUrl));
    ICommand? _openZapretCmd;
    public ICommand OpenNoticesCmd => _openNoticesCmd ??= new RelayCommand(() => OpenLink(NoticesPath));
    ICommand? _openNoticesCmd;

    void OpenLink(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Fail(L.T("shell.open_failed", new { error = e.Message })); }
    }

    public ICommand RefreshDiagCmd { get; }
    public ICommand RepairCoreCmd { get; }
    public ICommand OpenLogsCmd { get; }
    public ICommand OpenResultsCmd { get; }
    public ICommand RemoveServiceCmd { get; }

    bool _diagRefreshing;
    /// <summary>Идёт обновление «Диагностики» — кнопка показывает «Проверяю…» и не нажимается.</summary>
    public bool DiagRefreshing { get => _diagRefreshing; private set { if (Set(ref _diagRefreshing, value)) { Raise(nameof(RefreshButtonText)); CommandManager.InvalidateRequerySuggested(); } } }
    public string RefreshButtonText => DiagRefreshing ? L.T("diag.refreshing") : L.T("diag.refresh");

    readonly LiveText _diagUpdated = new();
    public string DiagUpdated { get => _diagUpdated.Value; private set { _diagUpdated.Set(value); Raise(); } }
    void DiagUpdatedIs(Func<string> build) { _diagUpdated.Set(build); Raise(nameof(DiagUpdated)); }

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
                    ForeignOf(st), Conflicts.ActiveVpnAdapters());
            });
            if (st is not null)
            {
                // Стратегию показываем ту, что реально стоит в службе Windows; чужой файл в службе — отдельно.
                ServiceTextIs(() =>
                {
                    var state = Texts.Svc(st.Service);
                    var strategy = st.ServiceStrategy is { } ss ? StrategyTitle(ss) : st.StrategyId is { } id && st.Service != SvcState.NotInstalled ? StrategyTitle(id) : null;
                    var foreign = st.Service != SvcState.NotInstalled && !st.ServiceOurCore;
                    return st.RunMode == "process" ? L.T("diag.service.process")
                        : strategy is not null
                            ? L.T(foreign ? "diag.service.with_strategy_foreign" : "diag.service.with_strategy", new { state, strategy })
                            : foreign ? L.T("diag.service.foreign", new { state }) : state;
                });
                (_svc, _active) = (st.Service, st.Active);
                _coreState = st.Core;
            }
            else
            {
                ServiceTextIs(() => local is { } l ? L.T("diag.service.with_strategy", new { state = Texts.Svc(svc), strategy = StrategyTitle(l) }) : Texts.Svc(svc));
                (_svc, _active) = (svc, svc == SvcState.Running);
                _localStrategy = local;
                _coreState = CoreState.Ok;
            }
            DriverTextIs(() => Texts.Svc(drv));
            if (d is not null)
            {
                var core = d.Core;
                _coreVersion = d.CoreVersion;
                CoreBroken = core.State != CoreState.Ok;
                _coreMissing = core.State == CoreState.Missing;
                CoreTextIs(() =>
                {
                    var text = core.State switch
                    {
                        CoreState.Ok => L.T("diag.core.ok", new { version = d.CoreVersion }),
                        CoreState.Missing => L.T("diag.core.missing"),
                        _ => L.T("diag.core.changed", new { files = string.Join(", ", core.BadFiles) }),
                    };
                    return core.ExtraFiles.Count > 0 ? L.T("diag.core.extra", new { text, files = string.Join(", ", core.ExtraFiles) }) : text;
                });
            }
            else
            {
                CoreBroken = false;
                var noHelper = Helper.State == HelperState.NotInstalled;
                CoreTextIs(() => noHelper ? L.T("diag.core.no_helper") : L.T("diag.core.helper_down"));
            }
            SetConflicts(conflicts);
            VpnList.Clear();
            foreach (var v in vpn) VpnList.Add(v);
            UpdateStatus();
            if (DnsRows.Count == 0) _ = CheckDnsAsync();
            // Проверки окружения (§4) и временные метки — только чтение: netsh show, sc query, реестр, процессы.
            var (ts, findings) = await Task.Run(() => (TcpTimestamps.IsEnabled(), SystemChecks.Run(_shell.Paths)));
            TimestampsTextIs(() => TimestampsLine(ts));
            Findings.Clear();
            foreach (var f in findings) Findings.Add(new FindingRow(f.Ok, f.Title, f.Text, f.Go, f));
            OnHelperChanged();
            var updated = DateTime.Now;
            DiagUpdatedIs(() => L.T("diag.updated", new { time = L.Time(updated) }));
        }
        catch (Exception e) { Fail(Texts.Friendly(e), e); }
        finally { DiagRefreshing = false; }
    }

    readonly LiveText _timestampsText = new();
    /// <summary>Решение 8: метки включаются сами при «Включить» и перед подбором — здесь только показываем.</summary>
    public string TimestampsText { get => _timestampsText.Value; private set { _timestampsText.Set(value); Raise(); } }
    void TimestampsTextIs(Func<string> build) { _timestampsText.Set(build); Raise(nameof(TimestampsText)); }

    /// <summary>Подбор шёл без TLS 1.3 (самопроверка помощника), хотя по сборке окна он есть.</summary>
    bool _tls12Seen;
    /// <summary>«Диагностика»: какими версиями TLS идут проверки (на Windows 10 — только 1.2, <see cref="TlsSupport"/>).</summary>
    public string TlsText => TlsLine(TlsSupport.Tls13Client && !_tls12Seen ? null : TlsSupport.DisabledHere || (TlsSupport.Tls13Client && _tls12Seen));

    /// <summary>null — 1.3 и 1.2; false — 1.3 нет в этой Windows (до Windows 11); true — по сборке есть, но отключён.</summary>
    public static string TlsLine(bool? disabled) => disabled switch
    {
        null => L.T("diag.tls.both"),
        false => L.T("diag.tls.only12"),
        true => L.T("diag.tls.off12"),
    };

    /// <summary>Фрагмент пояснения к подбору без TLS 1.3; null — 1.3 проверялся.</summary>
    string? TlsNote(SelectionReport r)
    {
        if (!TlsSupport.Tls12Only(r)) return null;
        return L.T(TlsSupport.Tls13Client || TlsSupport.DisabledHere ? "select.note.tls_off" : "select.note.tls12");
    }

    public static string TimestampsLine(bool? enabled) => enabled switch
    {
        true => L.T("diag.timestamps.on"),
        false => L.T("diag.timestamps.off"),
        _ => L.T("diag.timestamps.unknown"),
    };

    /// <summary>Проверки окружения (09-features-spec §4): ✓ — в порядке, ⚠ — что мешает и что сделать.</summary>
    public ObservableCollection<FindingRow> Findings { get; } = [];

    public ICommand ClearDiscordCmd { get; }

    /// <summary>Решение 5: кэш Discord (Cache, Code Cache, GPUCache) — с подтверждением; запущенный Discord закрывается. Права не нужны.</summary>
    async Task ClearDiscordAsync()
    {
        var found = await Task.Run(() => DiscordCache.Find());
        if (found.Folders.Count == 0) { Message = L.T("diag.discord.not_found"); return; }
        bool running = await Task.Run(DiscordCache.IsRunning);
        if (!Confirm(L.T(running ? "diag.discord.confirm_running" : "diag.discord.confirm", new { size = Mb(found.Bytes) })))
            return;
        DiscordCacheInfo? r = null;
        if (await Guard(L.T("diag.discord.busy"), async () => r = await Task.Run(() =>
            {
                if (running) DiscordCache.Close();
                return DiscordCache.Clear();
            })) && r is not null)
            Message = r.Failed.Count > 0
                ? L.T("diag.discord.cleared_failed", new { size = Mb(r.Bytes), n = r.Failed.Count })
                : L.T("diag.discord.cleared", new { size = Mb(r.Bytes) });
    }

    static string Mb(long bytes) => bytes >= 1024 * 1024
        ? L.T("diag.size.mb", new { n = (bytes / 1024.0 / 1024).ToString("0.#", L.Culture) })
        : L.T("diag.size.kb", new { n = Math.Max(1, bytes / 1024) });

    // ---------- оформление (настройки окна — %LocalAppData%) ----------

    void SaveSettings()
    {
        try { Settings.Save(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Fail(Texts.Friendly(e), e); }
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

    bool _detailOpened;
    /// <summary>
    /// «Показывать инструменты для опытных» (Настройки): раздел «Отладка подключений» на вкладке «Игры и программы».
    /// «Подробнее» в простом режиме открывает его и без галочки — до перезапуска окна.
    /// </summary>
    public bool ShowAdvancedSetting
    {
        get => Settings.ShowAdvanced;
        set
        {
            if (Settings.ShowAdvanced == value) return;
            Settings.ShowAdvanced = value;
            SaveSettings();
            Raise(); Raise(nameof(ShowAdvanced));
        }
    }
    public bool ShowAdvanced => Settings.ShowAdvanced || _detailOpened;

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

    /// <summary>Только без «Режима службы» (UX-аудит 2, п. 12): с ней подсказка под галочкой уже всё сказала.</summary>
    public string CloseHint => ServiceMode ? "" : L.T("settings.close.hint");
    public bool HasCloseHint => CloseHint.Length > 0;

    /// <summary>Восстановить ядро — помощник останавливает обход, ставит файлы заново и возвращает как было.</summary>
    Task RepairCoreAsync() => WithHelper(L.T("about.core.busy"), async c =>
    {
        await c.CoreRepairAsync(new Progress<string>(s => Message = s));
        await RefreshDiagAsync();
        await RefreshStatusAsync();
    });

    /// <summary>«Удалить службу оптимизации» — помощник: служба обхода, WinDivert, временные метки TCP (если включали мы).</summary>
    async Task RemoveServiceAsync()
    {
        if (!Confirm(ServiceMode
                ? L.T("settings.remove_service.confirm_service")
                : L.T("settings.remove_service.confirm_process")))
            return;
        if (await WithHelper(L.T("settings.remove_service.busy"), async c =>
            {
                await Router.JointShutdownAsync();   // совместная работа выключается, связка на роутере снимается (pc_link_del)
                await c.RemoveAllAsync();
                await RefreshDiagAsync();
                await RefreshStatusAsync();
            }))
            Message = L.T("settings.remove_service.done");
    }

    public ICommand ClearHistoryCmd => _clearHistoryCmd ??= RelayCommand.Async(ClearHistoryAsync, () => !Busy && !IsLoading && !Selecting);
    ICommand? _clearHistoryCmd;

    async Task ClearHistoryAsync()
    {
        if (!Confirm(L.T("settings.history.confirm"))) return;
        int n = 0;
        if (!await WithHelper(L.T("settings.history.busy"), async c => n = await c.ClearHistoryAsync())) return;
        await ReloadChoicesAsync();
        Message = L.T("settings.history.done", new { count = n });
    }

    /// <summary>
    /// «Папка результатов»: результаты лежат в закрытой папке — помощник отдаёт их (свои сайты уже скрыты),
    /// окно кладёт копию в %LocalAppData%\DpiShell\results и открывает.
    /// </summary>
    async Task OpenResultsAsync()
    {
        await FetchAsync(full: true);
        if (Helper.Reader is null || Helper.Results is not { } r)
        {
            Message = L.T("diag.results.no_helper");
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
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Fail(Texts.Friendly(e), e); return; }
        if (r.Results.Count == 0) Message = L.T("diag.results.none");
        OpenFolder(dir);
    }

    void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception e) { Fail(Texts.Friendly(e), e); }
    }
}
