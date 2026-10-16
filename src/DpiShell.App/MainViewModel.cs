using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DpiShell.Core;

namespace DpiShell.App;

public enum StatusKind { Off, On, Busy, Error }

public static class Pages
{
    public const int Home = 0, Select = 1, Targets = 2, Diag = 3;
}

/// <summary>Всё состояние окна. Долгие и «sc»-операции Core — только через Task.Run, чтобы окно не замирало.</summary>
public sealed class MainViewModel : Observable
{
    readonly Shell _shell;
    CancellationTokenSource? _selectCts;
    Task _current = Task.CompletedTask;
    string? _error;
    string? _coreProblem;
    SvcState _svc = SvcState.Unknown;
    bool _active;   // обход работает в своём режиме: служба запущена или наш winws жив (Shell.IsActive)

    public Shell Shell => _shell;
    public RouterViewModel Router { get; }

    /// <summary>Вопрос «Да/Нет» (в тестах подменяется).</summary>
    public Func<string, bool> Confirm { get; set; } = text =>
        MessageBox.Show(text, "DpiShell", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    public event Action? StatusChanged;

    public MainViewModel(Shell shell)
    {
        _shell = shell;

        RetryCmd = RelayCommand.Async(InitializeAsync, () => LoadFailed);
        EnableCmd = RelayCommand.Async(EnableAsync, () => !Busy && !IsLoading);
        DisableCmd = RelayCommand.Async(DisableAsync, () => !Busy && !IsLoading);
        GoSelectCmd = new RelayCommand(() => Page = Pages.Select);
        GoDiagCmd = new RelayCommand(() => Page = Pages.Diag);
        CheckNowCmd = RelayCommand.Async(CheckNowAsync, () => !Busy && !IsLoading);

        StartSelectCmd = RelayCommand.Async(StartSelectAsync, () => !Busy && !IsLoading);
        StartSearchCmd = RelayCommand.Async(StartSearchAsync, () => !Busy && !IsLoading);
        CheckDnsCmd = RelayCommand.Async(CheckDnsAsync, () => !IsLoading && !DnsChecking);
        StopSelectCmd = new RelayCommand(() => _selectCts?.Cancel(), () => Selecting);
        ApplyBestCmd = RelayCommand.Async(() => ApplyAsync(_bestId!), () => !Busy && _bestId is not null);
        ApplySelectedCmd = RelayCommand.Async(() => ApplyAsync(SelectedScore!.Id), () => !Busy && SelectedScore is { Usable: true });

        SaveTargetsCmd = new RelayCommand(SaveTargets);

        RefreshDiagCmd = RelayCommand.Async(RefreshDiagAsync, () => !IsLoading && !DiagRefreshing);
        RepairCoreCmd = RelayCommand.Async(RepairCoreAsync, () => !Busy && !IsLoading);
        OpenLogsCmd = new RelayCommand(() => OpenFolder(_shell.Paths.Logs));
        OpenResultsCmd = new RelayCommand(() => OpenFolder(_shell.Paths.Results));
        RemoveServiceCmd = RelayCommand.Async(RemoveServiceAsync, () => !Busy && !IsLoading);

        // Обход на роутере (docs/07-router-api.md §6): своя логика, свои команды; сюда — только строка состояния и переходы.
        Router = new RouterViewModel(shell)
        {
            Say = s => Message = s,
            Navigate = p => Page = p,
            SelectOptions = RouterSelectOptions,
        };
        Router.Changed += UpdateStatus;

        var chosen = _shell.State.Groups.ToHashSet();
        foreach (var g in _shell.DefaultTargets.Groups) Groups.Add(new(g.Id, g.Title, chosen.Contains(g.Id)));
        Groups.Add(new("user", "Свои адреса", chosen.Contains("user")));

        IReadOnlyList<string> targets = _shell.State.UserTargets.Count > 0 ? _shell.State.UserTargets : _shell.Data.ReadUserDomains();
        _targetsText = string.Join(Environment.NewLine, targets);
        UpdateStatus();
    }

    // ---------- общее ----------

    int _page;
    public int Page
    {
        get => _page;
        set
        {
            if (!Set(ref _page, value) || value != Pages.Diag || IsLoading) return;
            _ = RefreshDiagAsync();
            if (Router.Connected) _ = Router.RefreshDiagAsync();
        }
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
            _error = Texts.Friendly(e);
            Message = _error;
            // Чужие программы обхода: что именно мешает — на «Диагностике».
            if (e is ConflictException) Page = Pages.Diag;
            return false;
        }
        finally
        {
            Busy = false; BusyText = "";
            UpdateStatus();
            done.TrySetResult();
        }
    }

    /// <summary>Дождаться текущей операции (для выхода во время подбора).</summary>
    public async Task WhenIdleAsync(TimeSpan timeout)
    {
        _selectCts?.Cancel();
        await Task.WhenAny(_current, Task.Delay(timeout));
    }

    // ---------- загрузка ----------

    bool _isLoading = true;
    public bool IsLoading { get => _isLoading; private set { if (Set(ref _isLoading, value)) { Raise(nameof(ShowOverlay)); CommandManager.InvalidateRequerySuggested(); } } }

    bool _loadFailed;
    public bool LoadFailed { get => _loadFailed; private set { if (Set(ref _loadFailed, value)) { Raise(nameof(ShowOverlay)); CommandManager.InvalidateRequerySuggested(); } } }

    public bool ShowOverlay => IsLoading;

    string _loadingText = "Загрузка ядра…";
    public string LoadingText { get => _loadingText; private set => Set(ref _loadingText, value); }

    public async Task InitializeAsync()
    {
        IsLoading = true; LoadFailed = false; LoadingText = "Загрузка ядра…";
        try
        {
            var log = new Progress<string>(s => LoadingText = s);
            var check = await Task.Run(() => _shell.PrepareAsync(log));
            _coreProblem = CoreProblem(check);
            IsLoading = false;
            // Роутер ищем сразу и параллельно: его нет — блок просто не появится.
            _ = Router.RefreshAsync();
            // Без службы обход живёт, пока открыта программа: был включён — вернуть.
            try { await Task.Run(_shell.ResumeIfNeeded); }
            catch (Exception e) { _error = Texts.Friendly(e); Message = _error; }
            await RefreshStatusAsync();
            await ReloadChoicesAsync();
            _ = CheckDnsAsync();
        }
        catch (Exception e)
        {
            LoadFailed = true;
            LoadingText = Texts.Friendly(e);
        }
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

    /// <summary>Состояние службы и «работает ли обход» в текущем режиме. Вызывать в фоне: в режиме службы это sc.exe.</summary>
    (SvcState Svc, bool Active) ReadRunState()
    {
        var svc = _shell.Service.State;
        return (svc, _shell.ServiceMode ? svc == SvcState.Running : _shell.IsActive);
    }

    readonly Dictionary<string, string> _titles = [];

    /// <summary>Название по id — и готовых, и найденных поиском (те читаются из файла, поэтому кэш).</summary>
    string StrategyTitle(string? id)
    {
        if (id is null) return "";
        if (_shell.Catalog.Find(id) is { } s) return s.Title;
        if (_titles.TryGetValue(id, out var t)) return t;
        try { t = _shell.FindStrategy(id)?.Title ?? id; } catch (Exception) { t = id; }
        return _titles[id] = t;
    }

    void UpdateStatus()
    {
        var err = _error ?? _coreProblem;
        if (Selecting) { Kind = StatusKind.Busy; StatusTitle = _searching ? "Глубокий поиск…" : "Подбор…"; StatusDetail = SelectProgressText; }
        else if (Busy) { Kind = StatusKind.Busy; StatusTitle = BusyText; StatusDetail = ""; }
        else if (err is not null) { Kind = StatusKind.Error; StatusTitle = "Ошибка"; StatusDetail = err; }
        else if (_active) { Kind = StatusKind.On; StatusTitle = "Работает"; StatusDetail = "Стратегия: " + StrategyTitle(_shell.State.StrategyId); }
        else if (_shell.ServiceMode && _svc == SvcState.StartPending) { Kind = StatusKind.Busy; StatusTitle = "Запускается…"; StatusDetail = ""; }
        else
        {
            Kind = StatusKind.Off; StatusTitle = "Выключено";
            StatusDetail = Router.Found && Router.RouterEnabled ? "Обход делает роутер — на этом ПК включать не нужно"
                : _shell.State.StrategyId is { } id ? "Выбрана стратегия: " + StrategyTitle(id) : "Стратегия не выбрана: выберите в списке или нажмите «Подобрать»";
        }
        StatusBrush = BrushOf(Kind);
        Raise(nameof(IsRunning));
        Raise(nameof(ToggleText));
        Raise(nameof(CanChoose));
        Raise(nameof(StrategyHeader));
        // Стратегию могли сменить с «Подбора» или автопереключением — меню показывает текущую, не вызывая перезапуск.
        if (_chosen != _shell.State.StrategyId) { _chosen = _shell.State.StrategyId; Raise(nameof(ChosenStrategyId)); }
        StatusChanged?.Invoke();
    }

    string _vpnWarning = "";
    public string VpnWarning { get => _vpnWarning; private set { if (Set(ref _vpnWarning, value)) Raise(nameof(HasVpn)); } }
    public bool HasVpn => VpnWarning.Length > 0;

    string _conflictWarning = "";
    public string ConflictWarning { get => _conflictWarning; private set { if (Set(ref _conflictWarning, value)) Raise(nameof(HasConflicts)); } }
    public bool HasConflicts => ConflictWarning.Length > 0;

    public ObservableCollection<CheckRow> CheckRows { get; } = [];

    string _checkSummary = "";
    public string CheckSummary { get => _checkSummary; private set => Set(ref _checkSummary, value); }

    // ---------- выбор стратегии (отдельно от выключателя) ----------

    public ObservableCollection<StrategyChoice> StrategyChoices { get; } = [];

    /// <summary>Список на главной: готовые и найденные, с долей удачных подборов из статистики.</summary>
    public async Task ReloadChoicesAsync()
    {
        var (all, stats) = await Task.Run(() => (_shell.AllStrategies, StrategyStats.LoadDetailed(_shell.Paths)));
        StrategyChoices.Clear();
        foreach (var s in StrategyStats.Order(all, stats.ToDictionary(kv => kv.Key, kv => kv.Value.Score)))
        {
            // Не «процент сайтов», а сколько раз в прошлых подборах открыла всё отмеченное.
            var note = stats.TryGetValue(s.Id, out var st) && st.Runs > 0
                ? $"открыла всё в {st.FullOk} из {st.Runs} подборов ({(double)st.FullOk / st.Runs:P0})"
                : "в подборах ещё не участвовала";
            StrategyChoices.Add(new(s.Id, s.Title, note, s.Source == "search"));
        }
        _chosen = _shell.State.StrategyId;
        Raise(nameof(ChosenStrategyId));
    }

    string? _chosen;
    /// <summary>Выбранная стратегия. Обход включён — перезапуск службы на новой; выключен — только запоминаем.</summary>
    public string? ChosenStrategyId
    {
        get => _chosen;
        set
        {
            if (value is null || value == _chosen) return;
            _chosen = value;
            Raise();
            if (IsRunning) _ = ApplyAsync(value);
            else
            {
                _shell.State.StrategyId = value;
                WatchdogPolicy.Applied(_shell.State, value);
                _shell.SaveState();
                UpdateStatus();
            }
        }
    }

    public string StrategyHeader => _shell.State.StrategyId is { } id ? "Стратегия: " + StrategyTitle(id) : "Стратегия не выбрана";

    // ---------- режим работы ----------

    /// <summary>Галочка «Режим службы». Переключение — в фоне; обход, если работал, продолжает в новом режиме.</summary>
    public bool ServiceMode
    {
        get => _shell.ServiceMode;
        set { if (value != _shell.ServiceMode) _ = SetRunModeAsync(value); }
    }

    public string RunModeHint => _shell.ServiceMode
        ? "Включается вместе с Windows и работает сама по себе — закрытие программы её не выключает"
        : "Обход работает, пока открыта программа (в окне или в трее)";

    async Task SetRunModeAsync(bool service)
    {
        await Guard(service ? "Перехожу в режим службы…" : "Перехожу на работу из программы…", () => Task.Run(() => _shell.SetRunMode(service)));
        // И при отказе (идёт другая операция, ошибка) — галочка показывает настоящий режим.
        Raise(nameof(ServiceMode)); Raise(nameof(RunModeHint)); Raise(nameof(CloseHint));
        await RefreshStatusAsync();
    }

    public string ToggleText => IsRunning ? "Выключить" : "Включить";
    public bool CanChoose => !Busy && !IsLoading && !Selecting;
    public ICommand ToggleCmd => _toggleCmd ??= RelayCommand.Async(() => IsRunning ? DisableAsync() : EnableAsync(), () => !Busy && !IsLoading);
    ICommand? _toggleCmd;

    public ICommand RetryCmd { get; }
    public ICommand EnableCmd { get; }
    public ICommand DisableCmd { get; }
    public ICommand GoSelectCmd { get; }
    public ICommand GoDiagCmd { get; }
    public ICommand CheckNowCmd { get; }

    /// <summary>Служба, конфликты, VPN, целостность ядра. Всё синхронное в Core — в фоне.</summary>
    public async Task RefreshStatusAsync()
    {
        if (IsLoading) return;
        try
        {
            var (run, conflicts, vpn, core) = await Task.Run(() =>
                (ReadRunState(), Conflicts.Find(_shell.Paths), Conflicts.ActiveVpnAdapters(), _shell.Installer.Check()));
            (_svc, _active) = run;
            _coreProblem = CoreProblem(core);
            VpnWarning = vpn.Count == 0 ? "" : $"Включён VPN ({string.Join(", ", vpn)}). С ним обход обычно не нужен, а подбор может показать неверный результат.";
            ConflictWarning = conflicts.Count == 0 ? "" : $"Мешают другие программы обхода: {string.Join(", ", conflicts.Select(c => c.Name).Distinct())}.";
        }
        catch (Exception e) { Message = Texts.Friendly(e); }
        UpdateStatus();
    }

    /// <summary>Тихое обновление по таймеру — только когда ничего не идёт.</summary>
    public Task TickAsync()
    {
        if (IsLoading) return Task.CompletedTask;
        _ = Router.TickAsync();
        return Busy ? Task.CompletedTask : RefreshStatusAsync();
    }

    static string? CoreProblem(CoreCheck c) => c.State switch
    {
        CoreState.Ok => null,
        CoreState.Missing => "Файлы ядра не найдены (антивирус?). Откройте «Диагностику» → «Восстановить ядро».",
        _ => "Файлы ядра изменены (антивирус?). Откройте «Диагностику» → «Восстановить ядро».",
    };

    public Task EnableAsync()
    {
        // Роутер уже обходит для всего дома — двойная обработка ломает сайты: включать здесь только осознанно.
        if (!IsRunning && Router.Found && Router.RouterEnabled && !Confirm(
                "Обход уже делает роутер — для всех устройств дома, и для этого ПК тоже. Если включить и здесь, " +
                "трафик будет обрабатываться дважды, и сайты могут перестать открываться.\n\nВсё равно включить на этом ПК?"))
            return Task.CompletedTask;
        // Стратегия не выбрана — не отправляем человека разбираться. Подборы уже были: берём лучшую по ним
        // (первая в списке, порядок по статистике). Не было: подбираем и включаем сами (автоприменение).
        if (_shell.State.StrategyId is null)
        {
            var stats = StrategyStats.Load(_shell.Paths);
            var top = StrategyChoices.FirstOrDefault()?.Id;
            if (top is not null && stats.TryGetValue(top, out var score) && score > 0) return ApplyAsync(top);
            Page = Pages.Select;
            return StartSelectAsync();
        }
        return ApplyAsync(_shell.State.StrategyId);
    }

    public Task DisableAsync() => Guard("Выключаю…", async () =>
    {
        await Task.Run(_shell.Disable);
        await RefreshStatusAsync();
    });

    Task<bool> ApplyAsync(string id) => Guard("Включаю…", async () =>
    {
        await Task.Run(() => _shell.Enable(id));
        // Выбранная руками — первая в запасных (запасные хранятся после подбора, в окне не показываются).
        WatchdogPolicy.Applied(_shell.State, id);
        _shell.SaveState();
        await RefreshStatusAsync();
        Page = Pages.Home;
    });

    Task CheckNowAsync()
    {
        if (_shell.SelectedGroups().Count == 0) { Message = "Нечего проверять: отметьте группы на вкладке «Подбор»."; return Task.CompletedTask; }
        return Guard("Проверяю…", async () =>
        {
            CheckRows.Clear(); CheckSummary = "";
            var dns = CheckDnsAsync();
            var verdicts = await Task.Run(() => _shell.CheckNowAsync());
            foreach (var v in verdicts) CheckRows.Add(CheckRow.From(v, GroupTitle));
            CheckSummary = $"Открывается {verdicts.Count(v => v.Ok)} из {verdicts.Count}";
            await dns;
            await RefreshStatusAsync();
        });
    }

    // ---------- DNS ----------

    public ObservableCollection<DnsRow> DnsRows { get; } = [];

    bool _dnsSpoofed;
    /// <summary>Системный DNS подменяет адреса проверяемых сайтов (ТЗ §9 п. 11).</summary>
    public bool DnsSpoofed { get => _dnsSpoofed; private set => Set(ref _dnsSpoofed, value); }

    string _dnsSummary = "Ещё не проверялось.";
    public string DnsSummary { get => _dnsSummary; private set => Set(ref _dnsSummary, value); }

    bool _dnsChecking;
    public bool DnsChecking { get => _dnsChecking; private set { if (Set(ref _dnsChecking, value)) CommandManager.InvalidateRequerySuggested(); } }

    public ICommand CheckDnsCmd { get; }

    /// <summary>Сравнить системный DNS с DoH. Тихо: без Busy, ошибки — в сводку DNS.</summary>
    public async Task CheckDnsAsync()
    {
        if (DnsChecking || IsLoading) return;
        DnsChecking = true;
        DnsSummary = "Проверяю…";
        try
        {
            var list = await Task.Run(() => _shell.CheckDnsAsync());
            DnsRows.Clear();
            foreach (var c in list) DnsRows.Add(DnsRow.From(c));
            DnsSpoofed = list.Any(c => c.Spoofed);
            DnsSummary = list.Count == 0 ? "Нечего проверять: не выбраны группы."
                : DnsSpoofed ? "DNS провайдера подменяет адреса сайтов."
                : list.All(c => c.Doh.Length == 0) ? "Защищённый DNS не ответил — сравнить не с чем."
                : "Подмены нет.";
        }
        catch (Exception e) { DnsSummary = Texts.Friendly(e); }
        finally { DnsChecking = false; }
    }

    string GroupTitle(string id) => Groups.FirstOrDefault(g => g.Id == id)?.Title ?? id;

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

    /// <summary>Отмеченные группы (и запомнить выбор); null — проверять нечего, причина уже в строке состояния.</summary>
    IReadOnlyList<TargetGroup>? TakeGroups()
    {
        var ids = Groups.Where(g => g.IsChecked).Select(g => g.Id).ToList();
        if (ids.Count == 0) { Message = "Отметьте, что проверять."; return null; }
        if (ids.Contains("user") && _shell.State.UserTargets.Count == 0)
        {
            Message = "Список своих адресов пуст — заполните его на вкладке «Свои адреса».";
            return null;
        }
        _shell.State.Groups = ids;
        try { _shell.SaveState(); } catch (Exception e) { Message = Texts.Friendly(e); }
        return _shell.SelectedGroups(ids);
    }

    /// <summary>Группы и режим для подбора на роутере. «Свои адреса» — роутерные, поэтому список ПК не проверяем.</summary>
    (IReadOnlyList<string> Groups, bool Full)? RouterSelectOptions()
    {
        var ids = Groups.Where(g => g.IsChecked).Select(g => g.Id).ToList();
        if (ids.Count == 0) { Message = "Отметьте, что проверять."; return null; }
        _shell.State.Groups = ids;
        try { _shell.SaveState(); } catch (Exception e) { Message = Texts.Friendly(e); }
        return (ids, Full);
    }

    void ResetResults()
    {
        Scores.Clear(); SelectedScore = null;
        ResultText = ""; ResultHint = ""; SetBest(null);
        SelectProgress = 0; SelectIndeterminate = false;
        SelectProgressText = "Подготовка…";
    }

    public async Task StartSelectAsync()
    {
        if (Busy) return;
        if (TakeGroups() is not { } groups) return;
        if (!ConfirmPauseIfRunning()) return;

        ResetResults();
        SelectProgressMax = Math.Max(1, _shell.Catalog.Strategies.Count);

        using var cts = new CancellationTokenSource();
        _selectCts = cts;
        Selecting = true;
        var progress = new Progress<SelectorProgress>(p =>
        {
            SelectProgressMax = Math.Max(1, p.Total);
            SelectProgress = p.Index;
            SelectProgressText = p.Message;
            if (p.Last is { } s) Scores.Add(new ScoreRow(s));
            UpdateStatus();
        });
        var quick = Quick;
        try
        {
            await Guard("Подбор…", async () =>
            {
                try { ShowReport(await Task.Run(() => _shell.SelectAsync(quick, groups, progress, cts.Token))); }
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
    }

    bool _indeterminate;
    /// <summary>Шаги глубокого поиска заранее не сосчитать — полоса «бегущая».</summary>
    public bool SelectIndeterminate { get => _indeterminate; private set => Set(ref _indeterminate, value); }

    bool _searching;

    public ICommand StartSearchCmd { get; }

    /// <summary>Глубокий поиск (ТЗ §4.7): от лучшей стратегии последнего подбора или от текущей.</summary>
    public async Task StartSearchAsync()
    {
        if (Busy) return;
        if (TakeGroups() is not { } groups) return;
        if (!ConfirmPauseIfRunning()) return;
        var baseId = _bestId ?? _shell.State.StrategyId;

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
                if (p.Last is { } s) Scores.Add(new ScoreRow(s));
            }
            UpdateStatus();
        });
        try
        {
            await Guard("Глубокий поиск…", async () =>
            {
                try { ShowSearch(await Task.Run(() => _shell.SearchAsync(baseId, groups, progress, cts.Token))); }
                catch (OperationCanceledException) { ResultText = "Поиск остановлен."; }
                finally { _titles.Clear(); }
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

    /// <summary>Подбор при включённом обходе (служба или из программы): обход на время проверки стоит, стратегии меняются — предупредить.</summary>
    bool ConfirmPauseIfRunning()
    {
        if (!IsRunning) return true;
        return MessageBox.Show(
            "Обход сейчас включён. На время проверки он будет приостановлен, а стратегии будут меняться одна за другой — " +
            "сайты, звонки и загрузки могут прерываться.\n\nПосле проверки обход включится на лучшей найденной стратегии. Продолжить?",
            "DpiShell", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.Yes) == MessageBoxResult.Yes;
    }

    public ICommand StopConflictsCmd => _stopConflictsCmd ??= RelayCommand.Async(StopConflictsAsync, () => !Busy && !IsLoading && HasConflicts);
    ICommand? _stopConflictsCmd;

    /// <summary>Остановить мешающие обходы одной кнопкой, а не объяснять, как это сделать.</summary>
    async Task StopConflictsAsync()
    {
        if (MessageBox.Show(ConflictWarning + "\n\nОстановить их сейчас? Настройки этих программ не меняются — их можно будет запустить снова.",
                "DpiShell", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) != MessageBoxResult.Yes) return;
        (IReadOnlyList<string> Stopped, IReadOnlyList<string> ComeBackOnReboot) r = ([], []);
        if (!await Guard("Останавливаю другие программы обхода…", async () => r = await Task.Run(_shell.StopForeign))) return;
        await RefreshStatusAsync();
        Message = r.Stopped.Count == 0 ? "Нечего останавливать." : "Остановлено: " + string.Join(", ", r.Stopped) + "."
            + (r.ComeBackOnReboot.Count > 0 ? $" Служба {string.Join(", ", r.ComeBackOnReboot)} запустится снова после перезагрузки — удалите её в программе, которая её поставила." : "");
    }

    string? _autoApplyId;

    /// <summary>После подбора/поиска: лучшая стратегия — в службу, обход работает на ней (ТЗ §9 п. 17).</summary>
    async Task AutoApplyAsync()
    {
        if (_autoApplyId is not { } id) return;
        _autoApplyId = null;
        var title = StrategyTitle(id);
        if (await ApplyAsync(id))
        {
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
        // Итоговая таблица — в порядке оценки, лучшая отмечена.
        var ordered = r.Scores.OrderByDescending(s => s.GroupShare).ThenByDescending(s => s.TargetShare).ThenBy(s => s.AvgSeconds).ToList();
        Scores.Clear();
        foreach (var s in ordered) Scores.Add(new ScoreRow(s) { IsBest = ReferenceEquals(s, r.Best) });
        SelectProgress = SelectProgressMax;
        _ = ReloadChoicesAsync();  // новая статистика и найденные стратегии — в меню на главной

        // Рабочие по рейтингу — запасные для автопереключения.
        WatchdogPolicy.Remember(_shell.State, r);
        try { _shell.SaveState(); } catch (Exception e) { Message = Texts.Friendly(e); }

        if (r.NothingBlocked)
        {
            ResultText = "Без обхода всё открывается: блокировок нет или включён VPN.";
            return;
        }
        if (r.Best is not { } best)
        {
            ResultText = r.Cancelled ? "Подбор остановлен, подходящей стратегии пока нет." : "Ни одна стратегия не помогла.";
            if (!r.Cancelled)
                ResultHint = "Проверьте, что свои адреса набраны верно и открываются хотя бы через VPN, или добавьте другие адреса этих сайтов и запустите полный подбор.";
            return;
        }
        SetBest(best.Strategy.Id);
        ResultText = "Подходит: " + best.Strategy.Title;
        // Подбор дошёл до конца — лучшая сразу ставится в службу (после выхода из Guard, см. AutoApplyAsync).
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
    public string TargetsText { get => _targetsText; set => Set(ref _targetsText, value); }

    string _targetsMessage = "";
    public string TargetsMessage { get => _targetsMessage; private set => Set(ref _targetsMessage, value); }

    public ICommand SaveTargetsCmd { get; }

    void SaveTargets()
    {
        try
        {
            var lines = TargetsText.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            var rejected = _shell.Data.WriteUserDomains(lines);
            var good = lines.Where(l => DataStore.NormalizeDomain(l) is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _shell.State.UserTargets = good;
            if (good.Count > 0 && !_shell.State.Groups.Contains("user"))
            {
                _shell.State.Groups.Add("user");
                if (Groups.FirstOrDefault(g => g.Id == "user") is { } ug) ug.IsChecked = true;
            }
            _shell.SaveState();
            TargetsText = string.Join(Environment.NewLine, good);
            TargetsMessage = rejected.Count == 0
                ? $"Сохранено адресов: {good.Count}."
                : $"Сохранено адресов: {good.Count}. Не распознаны и убраны: {string.Join(", ", rejected)}.";
        }
        catch (Exception e) { TargetsMessage = Texts.Friendly(e); }
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

    public ICommand RefreshDiagCmd { get; }
    public ICommand RepairCoreCmd { get; }
    public ICommand OpenLogsCmd { get; }
    public ICommand OpenResultsCmd { get; }
    public ICommand RemoveServiceCmd { get; }

    bool _diagRefreshing;
    /// <summary>Идёт обновление «Диагностики» — кнопка показывает «Обновляю…» и не нажимается.</summary>
    public bool DiagRefreshing { get => _diagRefreshing; private set { if (Set(ref _diagRefreshing, value)) { Raise(nameof(RefreshButtonText)); CommandManager.InvalidateRequerySuggested(); } } }
    public string RefreshButtonText => DiagRefreshing ? "Обновляю…" : "Обновить";

    string _diagUpdated = "";
    public string DiagUpdated { get => _diagUpdated; private set => Set(ref _diagUpdated, value); }

    public async Task RefreshDiagAsync()
    {
        if (DiagRefreshing) return;
        DiagRefreshing = true;
        try
        {
            // Без службы её настройки не читаем: InspectService «верит службе» и сбросил бы включённый обход.
            bool serviceMode = _shell.ServiceMode;
            var (info, run, drv, core, conflicts, vpn) = await Task.Run(() => (
                serviceMode ? _shell.InspectService() : null, ReadRunState(), ServiceManager.Query("WinDivert"), _shell.Installer.Check(),
                Conflicts.Find(_shell.Paths, _shell.AllStrategies), Conflicts.ActiveVpnAdapters()));
            var svc = run.Svc;
            // Стратегию показываем ту, что реально стоит в службе Windows; чужой файл в службе — отдельно.
            ServiceText = info is null ? "не используется (обход работает из программы)"
                : Texts.Svc(svc)
                + (info.Strategy is { } st ? $" (стратегия: {st.Title})" : _shell.State.StrategyId is { } id && svc != SvcState.NotInstalled ? $" (стратегия: {StrategyTitle(id)})" : "")
                + (svc != SvcState.NotInstalled && !info.OurCore ? $" — запускает чужой файл: {info.ExePath}" : "");
            DriverText = Texts.Svc(drv);
            CoreBroken = core.State != CoreState.Ok;
            CoreText = core.State switch
            {
                CoreState.Ok => $"в порядке (версия {_shell.Installer.Manifest.Version})",
                CoreState.Missing => "не найдено — возможно, удалено антивирусом",
                _ => "изменено, возможно антивирусом: " + string.Join(", ", core.BadFiles),
            };
            if (core.ExtraFiles.Count > 0) CoreText += $"; лишние файлы: {string.Join(", ", core.ExtraFiles)}";
            ConflictList.Clear();
            foreach (var c in conflicts)
                ConflictList.Add($"{c.Name}. {c.Detail}");
            VpnList.Clear();
            foreach (var v in vpn) VpnList.Add(v);
            (_svc, _active) = run;
            _coreProblem = CoreProblem(core);
            UpdateStatus();
            if (DnsRows.Count == 0) _ = CheckDnsAsync();
            DiagUpdated = $"обновлено в {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception e) { Message = Texts.Friendly(e); }
        finally { DiagRefreshing = false; }
    }

    // ---------- оформление ----------

    /// <summary>"system" / "light" / "dark" — хранится в AppState, применяется сразу.</summary>
    public string Theme
    {
        get => ThemeManager.Normalize(_shell.State.Theme);
        set
        {
            value = ThemeManager.Normalize(value);
            if (value == Theme) return;
            _shell.State.Theme = value;
            try { _shell.SaveState(); } catch (Exception e) { Message = Texts.Friendly(e); }
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
    public bool CloseToTray => _shell.State.CloseAction != "exit";
    public bool CloseTray { get => CloseToTray; set { if (value) SetCloseAction("tray"); } }
    public bool CloseExit { get => !CloseToTray; set { if (value) SetCloseAction("exit"); } }
    void SetCloseAction(string a)
    {
        if (_shell.State.CloseAction == a) return;
        _shell.State.CloseAction = a;
        _shell.SaveState();
        Raise(nameof(CloseTray)); Raise(nameof(CloseExit));
    }

    public string CloseHint => _shell.ServiceMode
        ? "Обход работает и без окна: его выключает только кнопка «Выключить»."
        : "Без режима службы закрытие программы выключает и обход.";

    Task RepairCoreAsync() => Guard("Восстанавливаю ядро…", async () =>
    {
        var log = new Progress<string>(s => Message = s);
        // Файлы работающего winws (служба или процесс программы) заняты: остановить, заменить, вернуть как было.
        bool wasRunning = await Task.Run(() => _shell.IsActive);
        if (wasRunning)
        {
            if (_shell.ServiceMode) await Task.Run(_shell.Service.Stop);
            else
            {
                await Task.Run(_shell.Disable);
                // Обход по-прежнему включён: не вернётся сейчас — вернётся при следующем запуске программы.
                _shell.State.Enabled = true;
                _shell.SaveState();
            }
        }
        await Task.Run(() => _shell.RepairCoreAsync(log));
        if (wasRunning && _shell.State.Enabled && _shell.State.StrategyId is { } id)
            await Task.Run(() => _shell.Enable(id));
        await RefreshDiagAsync();
        await RefreshStatusAsync();
    });

    async Task RemoveServiceAsync()
    {
        bool serviceMode = _shell.ServiceMode;
        var ok = MessageBox.Show(serviceMode
            ? "Удалить службу обхода и драйвер WinDivert? Обход перестанет работать, пока вы снова не нажмёте «Включить»."
            : "Удалить службу обхода и драйвер WinDivert? Обход из программы, если включён, прервётся до следующего включения.",
            "DpiShell", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (ok != MessageBoxResult.Yes) return;
        if (await Guard("Удаляю службу…", async () =>
        {
            await Task.Run(_shell.Service.Remove);
            if (serviceMode)
            {
                _shell.State.Enabled = false;
                _shell.SaveState();
            }
            await RefreshDiagAsync();
            await RefreshStatusAsync();
        }))
            Message = "Служба и драйвер удалены.";
    }

    public ICommand ClearHistoryCmd => _clearHistoryCmd ??= RelayCommand.Async(ClearHistoryAsync, () => !Busy && !IsLoading && !Selecting);
    ICommand? _clearHistoryCmd;

    async Task ClearHistoryAsync()
    {
        var ok = MessageBox.Show(
            "Удалить результаты подборов, статистику и логи?\nСвои адреса, найденные стратегии и включённый обход останутся.",
            "DpiShell", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (ok != MessageBoxResult.Yes) return;
        var n = await Task.Run(() => ResultStore.ClearHistory(_shell.Paths));
        await ReloadChoicesAsync();
        Message = $"История очищена: удалено файлов — {n}.";
    }

    void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception e) { Message = Texts.Friendly(e); }
    }
}
