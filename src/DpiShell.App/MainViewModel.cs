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

    public Shell Shell => _shell;
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
        StopSelectCmd = new RelayCommand(() => _selectCts?.Cancel(), () => Selecting);
        ApplyBestCmd = RelayCommand.Async(() => ApplyAsync(_bestId!), () => !Busy && _bestId is not null);
        ApplySelectedCmd = RelayCommand.Async(() => ApplyAsync(SelectedScore!.Id), () => !Busy && SelectedScore is { Usable: true });

        SaveTargetsCmd = new RelayCommand(SaveTargets);

        RefreshDiagCmd = RelayCommand.Async(RefreshDiagAsync, () => !IsLoading);
        RepairCoreCmd = RelayCommand.Async(RepairCoreAsync, () => !Busy && !IsLoading);
        OpenLogsCmd = new RelayCommand(() => OpenFolder(_shell.Paths.Logs));
        OpenResultsCmd = new RelayCommand(() => OpenFolder(_shell.Paths.Results));
        RemoveServiceCmd = RelayCommand.Async(RemoveServiceAsync, () => !Busy && !IsLoading);

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
        set { if (Set(ref _page, value) && value == Pages.Diag && !IsLoading) _ = RefreshDiagAsync(); }
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
            await RefreshStatusAsync();
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

    public bool IsRunning => _svc == SvcState.Running;

    string StrategyTitle(string? id) => id is null ? "" : _shell.Catalog.Find(id)?.Title ?? id;

    void UpdateStatus()
    {
        var err = _error ?? _coreProblem;
        if (Selecting) { Kind = StatusKind.Busy; StatusTitle = "Подбор…"; StatusDetail = SelectProgressText; }
        else if (Busy) { Kind = StatusKind.Busy; StatusTitle = BusyText; StatusDetail = ""; }
        else if (err is not null) { Kind = StatusKind.Error; StatusTitle = "Ошибка"; StatusDetail = err; }
        else if (_svc == SvcState.Running) { Kind = StatusKind.On; StatusTitle = "Работает"; StatusDetail = "Стратегия: " + StrategyTitle(_shell.State.StrategyId); }
        else if (_svc == SvcState.StartPending) { Kind = StatusKind.Busy; StatusTitle = "Запускается…"; StatusDetail = ""; }
        else
        {
            Kind = StatusKind.Off; StatusTitle = "Выключено";
            StatusDetail = _shell.State.StrategyId is { } id ? "Выбрана стратегия: " + StrategyTitle(id) : "Стратегия ещё не подобрана";
        }
        StatusBrush = BrushOf(Kind);
        Raise(nameof(IsRunning));
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
            var (svc, conflicts, vpn, core) = await Task.Run(() =>
                (_shell.Service.State, Conflicts.Find(_shell.Paths), Conflicts.ActiveVpnAdapters(), _shell.Installer.Check()));
            _svc = svc;
            _coreProblem = CoreProblem(core);
            VpnWarning = vpn.Count == 0 ? "" : $"Включён VPN ({string.Join(", ", vpn)}). С ним обход обычно не нужен, а подбор может показать неверный результат.";
            ConflictWarning = conflicts.Count == 0 ? "" : $"Мешают другие программы обхода: {string.Join(", ", conflicts.Select(c => c.Name).Distinct())}.";
        }
        catch (Exception e) { Message = Texts.Friendly(e); }
        UpdateStatus();
    }

    /// <summary>Тихое обновление по таймеру — только когда ничего не идёт.</summary>
    public Task TickAsync() => Busy || IsLoading ? Task.CompletedTask : RefreshStatusAsync();

    static string? CoreProblem(CoreCheck c) => c.State switch
    {
        CoreState.Ok => null,
        CoreState.Missing => "Файлы ядра не найдены (антивирус?). Откройте «Диагностику» → «Восстановить ядро».",
        _ => "Файлы ядра изменены (антивирус?). Откройте «Диагностику» → «Восстановить ядро».",
    };

    public Task EnableAsync()
    {
        if (_shell.State.StrategyId is null)
        {
            Page = Pages.Select;
            Message = "Сначала подберите стратегию.";
            return Task.CompletedTask;
        }
        return ApplyAsync(_shell.State.StrategyId);
    }

    public Task DisableAsync() => Guard("Выключаю…", async () =>
    {
        await Task.Run(_shell.Disable);
        await RefreshStatusAsync();
    });

    Task ApplyAsync(string id) => Guard("Включаю…", async () =>
    {
        await Task.Run(() => _shell.Enable(id));
        await RefreshStatusAsync();
        Page = Pages.Home;
    });

    Task CheckNowAsync()
    {
        if (_shell.SelectedGroups().Count == 0) { Message = "Нечего проверять: отметьте группы на вкладке «Подбор»."; return Task.CompletedTask; }
        return Guard("Проверяю…", async () =>
        {
            CheckRows.Clear(); CheckSummary = "";
            var verdicts = await Task.Run(() => _shell.CheckNowAsync());
            foreach (var v in verdicts) CheckRows.Add(CheckRow.From(v, GroupTitle));
            CheckSummary = $"Открывается {verdicts.Count(v => v.Ok)} из {verdicts.Count}";
            await RefreshStatusAsync();
        });
    }

    string GroupTitle(string id) => Groups.FirstOrDefault(g => g.Id == id)?.Title ?? id;

    // ---------- подбор ----------

    public ObservableCollection<GroupOption> Groups { get; } = [];
    public ObservableCollection<ScoreRow> Scores { get; } = [];

    bool _quick = true;
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

    public async Task StartSelectAsync()
    {
        if (Busy) return;
        var ids = Groups.Where(g => g.IsChecked).Select(g => g.Id).ToList();
        if (ids.Count == 0) { Message = "Отметьте, что проверять."; return; }
        if (ids.Contains("user") && _shell.State.UserTargets.Count == 0)
        {
            Message = "Список своих адресов пуст — заполните его на вкладке «Свои адреса».";
            return;
        }

        _shell.State.Groups = ids;
        try { _shell.SaveState(); } catch (Exception e) { Message = Texts.Friendly(e); }
        var groups = _shell.SelectedGroups(ids);

        Scores.Clear(); SelectedScore = null;
        ResultText = ""; ResultHint = ""; SetBest(null);
        SelectProgress = 0; SelectProgressMax = Math.Max(1, _shell.Catalog.Strategies.Count);
        SelectProgressText = "Подготовка…";

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
        await RefreshStatusAsync();
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

    public async Task RefreshDiagAsync()
    {
        try
        {
            var (svc, drv, core, conflicts, vpn) = await Task.Run(() => (
                _shell.Service.State, ServiceManager.Query("WinDivert"), _shell.Installer.Check(),
                Conflicts.Find(_shell.Paths), Conflicts.ActiveVpnAdapters()));
            ServiceText = Texts.Svc(svc) + (_shell.State.StrategyId is { } id ? $" (стратегия: {StrategyTitle(id)})" : "");
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
                ConflictList.Add(c.Kind == "service" ? $"Служба {c.Name} ({c.Detail})" : $"Программа {c.Name} ({c.Detail})");
            VpnList.Clear();
            foreach (var v in vpn) VpnList.Add(v);
            _svc = svc;
            _coreProblem = CoreProblem(core);
            UpdateStatus();
        }
        catch (Exception e) { Message = Texts.Friendly(e); }
    }

    Task RepairCoreAsync() => Guard("Восстанавливаю ядро…", async () =>
    {
        var log = new Progress<string>(s => Message = s);
        // Файлы работающей службы заняты: остановить, заменить, вернуть как было.
        bool wasRunning = await Task.Run(() => _shell.Service.State == SvcState.Running);
        if (wasRunning) await Task.Run(_shell.Service.Stop);
        await Task.Run(() => _shell.RepairCoreAsync(log));
        if (wasRunning && _shell.State.Enabled && _shell.State.StrategyId is { } id)
            await Task.Run(() => _shell.Enable(id));
        await RefreshDiagAsync();
        await RefreshStatusAsync();
    });

    async Task RemoveServiceAsync()
    {
        var ok = MessageBox.Show(
            "Удалить службу обхода и драйвер WinDivert? Обход перестанет работать, пока вы снова не нажмёте «Включить».",
            "DpiShell", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (ok != MessageBoxResult.Yes) return;
        if (await Guard("Удаляю службу…", async () =>
        {
            await Task.Run(_shell.Service.Remove);
            _shell.State.Enabled = false;
            _shell.SaveState();
            await RefreshDiagAsync();
            await RefreshStatusAsync();
        }))
            Message = "Служба и драйвер удалены.";
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
