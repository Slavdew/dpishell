using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.App;

public enum AppsStep
{
    /// <summary>Ничего не идёт: кнопка «Начать».</summary>
    Idle,
    /// <summary>Запись идёт: «Готово» / «Отмена».</summary>
    Recording,
    /// <summary>Собираю профиль и применяю.</summary>
    Working,
    /// <summary>Профиль применён, спрашиваем: заработало?</summary>
    Check,
    /// <summary>Заработало: профиль оставлен.</summary>
    Done,
    /// <summary>Не вышло: ничего не оставлено.</summary>
    Failed,
}

/// <summary>Процесс для выбора в «Отладке подключений».</summary>
public sealed record ProcessChoice(int Pid, string Name, string Title)
{
    public string Display => Title.Length > 0 ? $"{Name} — {Title}" : Name;
}

/// <summary>Строка записи: подключение с галочкой.</summary>
public sealed class FlowRow : Observable
{
    bool _checked;
    public FlowRow(RecordedFlow flow, string? reason, bool isChecked)
    {
        Flow = flow;
        Reason = reason ?? "";
        _checked = isChecked;
    }

    public RecordedFlow Flow { get; }
    /// <summary>Почему программа сама бы это убрала («локальная сеть»); пусто — оставила бы.</summary>
    public string Reason { get; }
    public bool IsChecked { get => _checked; set { if (Set(ref _checked, value)) Changed?.Invoke(); } }
    public event Action? Changed;

    public string Process => Flow.Process;
    public string Address => Flow.Address;
    public int Port => Flow.Port;
    public string Kind => Flow.Proto.ToUpperInvariant();
    public int Count => Flow.Count;
    public string State => Flow.State switch { "works" => L.T("apps.flow.state.works"), "noreply" => L.T("apps.flow.state.noreply"), _ => Flow.Proto == "udp" ? "—" : "" };
}

/// <summary>Сохранённый профиль в списке.</summary>
public sealed class ProfileRow(AppProfileInfo info) : Observable
{
    public AppProfileInfo Info { get; } = info;
    public string Id => Info.Id;
    public string Title => Info.Title;
    public string Summary => AppsViewModel.Summarize(Info);
    public string Variant => Info.VariantTitle;
    public string VariantLabel => L.T("apps.profile.variant", new { variant = Variant });
    public ICommand? RemoveCmd { get; set; }
    public ICommand? NextCmd { get; set; }
}

/// <summary>
/// «Игры и программы» (ТЗ 13, решения владельца 6–8): простой режим «Настроить под игру» — Начать → игра → Готово, всё само;
/// «Отладка подключений» для опытных — список записанного с галочками, ручной выбор, сохранение профиля. Запись и применение —
/// через помощника (команды rec_*, app_profile_*); окно ничего не меняет само. Действие обратимо: «Вернуть как было» удаляет профиль.
/// </summary>
public sealed class AppsViewModel : Observable
{
    public const int MaxAttempts = 5;

    readonly HelperLink _helper;

    public Func<string, Func<Task>, Task<bool>> Guard { get; set; } = async (_, w) => { await w(); return true; };
    public Func<Task<HelperClient?>> Need { get; set; }
    public Action<string> Say { get; set; } = _ => { };
    public Action<string>? Fail { get; set; }
    public Func<string, bool> Confirm { get; set; } = _ => false;
    public Func<Task> AfterChange { get; set; }
    /// <summary>Процессы с окнами для выбора (в тестах подменяется).</summary>
    public Func<IReadOnlyList<ProcessChoice>> ListProcesses { get; set; } = ProcessesWithWindows;
    /// <summary>Расширенный раздел открыли по «Подробнее» (даже если в «Настройках» он не включён).</summary>
    public event Action? DetailRequested;

    public AppsViewModel(HelperLink helper)
    {
        _helper = helper;
        Need = () => Task.FromResult(helper.Client);
        AfterChange = () => helper.FetchAsync(full: false);
        StartCmd = RelayCommand.Async(StartAsync, () => !AdvRecording && Step is AppsStep.Idle or AppsStep.Done or AppsStep.Failed);
        FinishCmd = RelayCommand.Async(FinishAsync, () => Step == AppsStep.Recording && _simple);
        CancelCmd = RelayCommand.Async(CancelAsync, () => Step == AppsStep.Recording && _simple);
        YesCmd = new RelayCommand(Yes, () => Step == AppsStep.Check);
        NoCmd = RelayCommand.Async(() => NextVariantAsync(), () => Step == AppsStep.Check);
        MoreCmd = new RelayCommand(OpenDetail, () => Step is AppsStep.Failed or AppsStep.Idle);
        AdvStartCmd = RelayCommand.Async(AdvStartAsync, () => !AdvRecording && Step is not (AppsStep.Recording or AppsStep.Working));
        AdvStopCmd = RelayCommand.Async(AdvStopAsync, () => AdvRecording);
        RefreshProcessesCmd = new RelayCommand(RefreshProcesses);
        DenoiseCmd = new RelayCommand(Denoise, () => Rows.Count > 0);
        CheckAllCmd = new RelayCommand(() => SetAll(true), () => Rows.Count > 0);
        UncheckAllCmd = new RelayCommand(() => SetAll(false), () => Rows.Count > 0);
        SaveCmd = RelayCommand.Async(SaveAdvancedAsync, () => !AdvRecording && CanSave);
    }

    // ---------- общее ----------

    AppsStep _step;
    public AppsStep Step
    {
        get => _step;
        private set
        {
            if (!Set(ref _step, value)) return;
            foreach (var p in new[] { nameof(IsIdle), nameof(IsRecording), nameof(IsWorking), nameof(IsCheck), nameof(IsDone), nameof(IsFailed), nameof(ShowStart), nameof(ShowMore), nameof(ShowOffHint), nameof(ShowIntro), nameof(StartText), nameof(ShowEnable), nameof(ShowEnablePrimary), nameof(ShowEnablePlain), nameof(ShowStartPrimary), nameof(ShowStartPlain) }) Raise(p);
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public bool IsIdle => Step == AppsStep.Idle;
    public bool IsRecording => Step == AppsStep.Recording;
    public bool IsWorking => Step == AppsStep.Working;
    public bool IsCheck => Step == AppsStep.Check;
    public bool IsDone => Step == AppsStep.Done;
    public bool IsFailed => Step == AppsStep.Failed;
    public bool ShowStart => Step is AppsStep.Idle or AppsStep.Done or AppsStep.Failed;

    public ICommand StartCmd { get; }
    public ICommand FinishCmd { get; }
    public ICommand CancelCmd { get; }
    public ICommand YesCmd { get; }
    public ICommand NoCmd { get; }
    public ICommand MoreCmd { get; }

    public static string AntiCheat => NetViewModel.AntiCheatNote;
    public string Intro => L.T("apps.intro");
    public string Anti => AntiCheat;
    /// <summary>Вступление — до начала и после неудачи; после «Готово» не повторяется (там итог и «Настроить ещё одну игру»).</summary>
    public bool ShowIntro => Step is AppsStep.Idle or AppsStep.Failed;
    public string StartText => Step == AppsStep.Done ? L.T("apps.start_another") : L.T("apps.start");

    string _status = "";
    /// <summary>Главная строка простого режима: что сейчас происходит.</summary>
    public string Status { get => _status; private set { if (Set(ref _status, value)) Raise(nameof(HasStatus)); } }
    public bool HasStatus => Status.Length > 0;
    /// <summary>«Подробнее» — до начала и после неудачи.</summary>
    public bool ShowMore => Step is AppsStep.Idle or AppsStep.Failed;
    public bool ShowOffHint => OptimizationOff && (Step == AppsStep.Check || HasProfiles && Step != AppsStep.Done);   // в «Готово» то же сказано в итоге

    string _detail = "";
    /// <summary>Вторая строка: подробности (попытка, предупреждения записи).</summary>
    public string Detail { get => _detail; private set { if (Set(ref _detail, value)) Raise(nameof(HasDetail)); } }
    public bool HasDetail => Detail.Length > 0;

    /// <summary>Изменилось состояние оптимизации (status помощника).</summary>
    public void StatusChanged() { Raise(nameof(OptimizationOff)); Raise(nameof(ShowOffHint)); foreach (var p in new[] { nameof(ShowEnable), nameof(ShowEnablePrimary), nameof(ShowEnablePlain), nameof(ShowStartPrimary), nameof(ShowStartPlain) }) Raise(p); CommandManager.InvalidateRequerySuggested(); }

    public bool OptimizationOff => _helper.Status is { Enabled: false };
    public string OffHint => L.T("apps.off_hint");

    /// <summary>Включить оптимизацию (как «Включить» на Главной); задаёт окно. Не задана — кнопки нет.</summary>
    public Func<Task>? EnableOptimization { get; set; }
    /// <summary>Профиль применён или сохранён, а оптимизация выключена — «Включить оптимизацию» прямо в карточке (UX-аудит 7, п. 16).</summary>
    public bool ShowEnable => EnableOptimization is not null && OptimizationOff && Step is AppsStep.Check or AppsStep.Done;
    /// <summary>Готово: «Включить оптимизацию» — главная кнопка, «Настроить ещё одну игру» — обычная. Ждём «Заработало?»: «Включить» — обычная, главные — «Да, заработало» и остальные.</summary>
    public bool ShowEnablePrimary => ShowEnable && IsDone;
    public bool ShowEnablePlain => ShowEnable && !IsDone;
    public bool ShowStartPrimary => ShowStart && !ShowEnablePrimary;
    public bool ShowStartPlain => ShowStart && ShowEnablePrimary;
    public ICommand EnableCmd => _enableCmd ??= RelayCommand.Async(async () => { if (EnableOptimization is { } e) await e(); }, () => ShowEnable);
    ICommand? _enableCmd;

    public static string Summarize(AppProfileInfo p) => Summarize(p.TcpPorts, p.UdpPorts, p.Addresses.Count);

    /// <summary>«TCP … · UDP … · адресов: N».</summary>
    static string Summarize(string tcp, string udp, int addresses)
    {
        var parts = new List<string>();
        if (tcp.Length > 0) parts.Add("TCP " + tcp);
        if (udp.Length > 0) parts.Add("UDP " + udp);
        parts.Add(L.T("apps.profile.addresses", new { n = addresses }));
        return string.Join(" · ", parts);
    }

    // ---------- профили ----------

    public ObservableCollection<ProfileRow> Profiles { get; } = [];
    public bool HasProfiles => Profiles.Count > 0;

    IReadOnlyList<ProfileVariant> _variants = [];
    public IReadOnlyList<ProfileVariant> Variants { get => _variants; private set { _variants = value; Raise(); } }

    /// <summary>Перечитать профили и варианты у помощника. Помощника нет — пусто.</summary>
    public async Task RefreshAsync()
    {
        if (_helper.Reader is not { } c) { SetProfiles([]); return; }
        try
        {
            var info = await c.AppProfilesAsync();
            Variants = info.Variants;
            SetProfiles(info.Profiles);
        }
        catch (Exception e) when (e is HelperException or HelperUnavailableException) { SetProfiles([]); }
        Raise(nameof(OptimizationOff));
    }

    void SetProfiles(IReadOnlyList<AppProfileInfo> list)
    {
        Profiles.Clear();
        foreach (var p in list)
        {
            var id = p.Id;
            Profiles.Add(new(p)
            {
                RemoveCmd = RelayCommand.Async(() => RemoveAsync(id)),
                NextCmd = RelayCommand.Async(() => OtherVariantAsync(id)),
            });
        }
        Raise(nameof(HasProfiles));
        Raise(nameof(ShowOffHint));
    }

    /// <summary>«Вернуть как было»: профиль удаляется, оптимизация перезапускается без него.</summary>
    public async Task RemoveAsync(string id)
    {
        if (await Need() is not { } c) return;
        bool removed = false;
        if (!await Guard(L.T("apps.busy.revert"), async () => removed = await c.AppProfileRemoveAsync(id))) return;
        await AfterChange();
        await RefreshAsync();
        if (Step is AppsStep.Check or AppsStep.Done && _profileId == id) { Step = AppsStep.Idle; Status = ""; Detail = ""; }
        Say(L.T(removed ? "apps.profile.removed" : "apps.profile.gone"));
    }

    /// <summary>Для опытных: тот же профиль с другим вариантом параметров.</summary>
    async Task OtherVariantAsync(string id)
    {
        var row = Profiles.FirstOrDefault(p => p.Id == id);
        if (row is null || Variants.Count < 2 || await Need() is not { } c) return;
        int next = (row.Info.Variant + 1) % Variants.Count;
        if (!await Guard(L.T("apps.busy.apply"), () => c.AppProfileSetAsync(id, row.Info.Title, row.Info.TcpPorts, row.Info.UdpPorts, row.Info.Addresses, next))) return;
        await AfterChange();
        await RefreshAsync();
        Say(L.T("apps.profile.variant_changed", new { title = row.Title, variant = Variants[next].Title }));
    }

    // ---------- простой режим ----------

    bool _simple;
    RecordingResult? _last;          // последняя запись: «Подробнее» открывает её в списке с галочками
    ProfileDraft? _draft;
    string? _profileId;
    int _attempt, _variant;
    DateTime _recStarted;
    public RecordingResult? LastRecording => _last;

    string _recText = "";
    /// <summary>Пока идёт запись: время и число подключений.</summary>
    public string RecText { get => _recText; private set => Set(ref _recText, value); }

    async Task StartAsync()
    {
        if (await Need() is not { } c) return;
        _simple = true; _last = null; _draft = null; _profileId = null; _attempt = 0; _variant = 0;
        try
        {
            await c.RecStartAsync(RecordingMode.NewProcesses, null, driver: UseDriver);
        }
        catch (Exception e) when (e is HelperException or HelperUnavailableException)
        {
            Failed(Texts.Friendly(e));
            return;
        }
        _recStarted = DateTime.UtcNow;
        Status = L.T("apps.rec.status");
        Detail = L.T("apps.rec.detail");
        RecText = "0:00";
        Step = AppsStep.Recording;
    }

    async Task CancelAsync()
    {
        if (_helper.Reader is { } c)
        {
            try { await c.RecStopAsync(); }
            catch (Exception e) when (e is HelperException or HelperUnavailableException) { }
        }
        Step = AppsStep.Idle; Status = ""; Detail = ""; RecText = "";
    }

    async Task FinishAsync()
    {
        if (await Need() is not { } c) return;
        Step = AppsStep.Working;
        Status = L.T("apps.busy.parse"); Detail = "";
        RecordingResult rec;
        try { rec = await c.RecStopAsync(); }
        catch (Exception e) when (e is HelperException or HelperUnavailableException) { Failed(Texts.Friendly(e)); return; }
        _last = rec;
        _draft = AppProfileBuilder.FromRecording(rec);
        if (_draft.IsEmpty)
        {
            Status = L.T("apps.rec.nothing");
            Detail = L.T("apps.rec.nothing_detail") + Notes(_draft);
            Step = AppsStep.Failed;   // тексты — до смены шага: окно и тесты читают их сразу по шагу
            return;
        }
        await RefreshAsync();   // варианты: как у текущей стратегии первым
        if (Variants.Count == 0) { Failed(L.T("apps.err.no_variants")); return; }
        await ApplyVariantAsync(_variant);
    }

    string Notes(ProfileDraft d) => d.Notes.Count == 0 ? "" : " " + string.Join(" ", d.Notes);

    /// <summary>Применить профиль с вариантом: первый раз создаёт, дальше заменяет тот же.</summary>
    async Task ApplyVariantAsync(int variant)
    {
        if (await Need() is not { } c || _draft is null) { Step = AppsStep.Idle; return; }
        _attempt++;
        _variant = variant;
        Step = AppsStep.Working;
        Status = L.T("apps.busy.apply_settings");
        AppProfileInfo? info = null;
        var d = _draft;
        bool ok = await Guard(L.T("apps.busy.apply_settings"), async () =>
            info = await c.AppProfileSetAsync(_profileId, d.Title, d.TcpPorts, d.UdpPorts, d.Addresses, variant));
        if (!ok || info is null) { Status = L.T("apps.err.apply"); Detail = L.T("apps.err.apply_detail"); Step = AppsStep.Failed; return; }
        _profileId = info.Id;
        await AfterChange();
        await RefreshAsync();
        Status = L.T("apps.check.applied", new { title = info.Title, summary = Summarize(info) });
        Detail = L.T("apps.check.attempt", new { attempt = _attempt, max = MaxAttempts, variant = info.VariantTitle }) + Notes(d);
        Step = AppsStep.Check;
    }

    void Yes()
    {
        // Одна фраза вместо итога и жёлтой плашки; «Вернуть как было» — в списке профилей ниже.
        var saved = L.T(OptimizationOff ? "apps.profile.saved_off" : "apps.profile.saved", new { title = _draft?.Title ?? L.T("apps.profile.default_title") });
        Status = saved;
        Detail = "";
        Step = AppsStep.Done;
        // Строка состояния и карточка говорят одно: сохранён; заработает ли сейчас — зависит от оптимизации на Главной.
        Say(saved);
    }

    /// <summary>«Нет» и «Стало хуже»: следующий вариант; попытки или варианты кончились — профиль убирается, как было.</summary>
    async Task NextVariantAsync()
    {
        if (_attempt >= MaxAttempts || _variant + 1 >= Variants.Count)
        {
            await GiveUpAsync();
            return;
        }
        await ApplyVariantAsync(_variant + 1);
    }

    async Task GiveUpAsync()
    {
        if (_profileId is not null && _helper.Reader is { } c)
        {
            await Guard(L.T("apps.busy.revert"), async () => await c.AppProfileRemoveAsync(_profileId));
            _profileId = null;
            await AfterChange();
            await RefreshAsync();
        }
        if (_last is not null) LoadRows(_last);
        Status = L.Plural("apps.giveup", _attempt);
        Detail = L.T("apps.giveup_detail");
        Step = AppsStep.Failed;
    }

    void OpenDetail()
    {
        if (_last is not null) LoadRows(_last);
        DetailRequested?.Invoke();
    }

    /// <summary>Раз в секунду из окна: время записи и счётчик подключений.</summary>
    bool _ticking;
    public async Task TickAsync()
    {
        if (!(IsRecording || AdvRecording) || _ticking || _helper.Reader is not { } c) return;
        _ticking = true;
        try { await TickCoreAsync(c); }
        finally { _ticking = false; }
    }

    async Task TickCoreAsync(HelperClient c)
    {
        var secs = (DateTime.UtcNow - _recStarted).TotalSeconds;
        try
        {
            var st = await c.RecStatusAsync();
            if (!st.Active)
            {
                // Помощник перезапустился или запись сорвалась — ничего не записано.
                Failed(L.T("apps.rec.interrupted"));
                return;
            }
            secs = st.Seconds;
            RecText = L.T("apps.rec.progress", new { time = $"{(int)secs / 60}:{(int)secs % 60:00}", n = st.Flows });
            if (!st.Running)
                Detail = L.T("apps.rec.expired");
            else if (!st.UdpRecorded && st.Notes.Count > 0)
                Detail = string.Join(" ", st.Notes);
        }
        catch (Exception e) when (e is HelperException or HelperUnavailableException) { }
    }

    void Failed(string text)
    {
        AdvRecording = false;
        Status = text;
        Detail = "";
        Step = AppsStep.Failed;
        (Fail ?? Say)(text);
    }

    // ---------- «Отладка подключений» ----------

    public ObservableCollection<ProcessChoice> Processes { get; } = [];
    ProcessChoice? _selectedProcess;
    public ProcessChoice? SelectedProcess { get => _selectedProcess; set { if (Set(ref _selectedProcess, value)) { if (value is not null) AdvByProcess = true; } } }

    bool _byProcess;
    /// <summary>Что записывать: выбранный процесс (true) или всё, что запустится после «Записать» (false).</summary>
    public bool AdvByProcess { get => _byProcess; set { if (Set(ref _byProcess, value)) Raise(nameof(AdvNewProcesses)); } }
    public bool AdvNewProcesses { get => !_byProcess; set { if (value) AdvByProcess = false; } }

    bool _noDriver = true;
    /// <summary>Без драйвера: только TCP, зато игры с защитой от читов не видят лишнего драйвера. По умолчанию — так:
    /// человек, не знающий про античиты, не должен из-за записи получить отказ игры. Общий для простого и продвинутого режима.</summary>
    public bool AdvNoDriver { get => _noDriver; set { if (Set(ref _noDriver, value)) Raise(nameof(WithDriver)); } }
    /// <summary>Галочка простого режима «записывать и UDP» — обратная к <see cref="AdvNoDriver"/>.</summary>
    public bool WithDriver { get => !_noDriver; set => AdvNoDriver = !value; }

    /// <summary>Оптимизация уже работает — драйвер WinDivert и так загружен, запись с ним ничего не добавляет.</summary>
    public Func<bool> OptimizationRunning { get; init; } = () => false;
    bool UseDriver => !AdvNoDriver || OptimizationRunning();

    bool _advRecording;
    public bool AdvRecording
    {
        get => _advRecording;
        private set { if (Set(ref _advRecording, value)) { Raise(nameof(AdvIdle)); CommandManager.InvalidateRequerySuggested(); } }
    }
    public bool AdvIdle => !AdvRecording;

    public ICommand AdvStartCmd { get; }
    public ICommand AdvStopCmd { get; }
    public ICommand RefreshProcessesCmd { get; }
    public ICommand DenoiseCmd { get; }
    public ICommand CheckAllCmd { get; }
    public ICommand UncheckAllCmd { get; }
    public ICommand SaveCmd { get; }

    public void RefreshProcesses()
    {
        var list = ListProcesses();
        Processes.Clear();
        foreach (var p in list) Processes.Add(p);
    }

    static IReadOnlyList<ProcessChoice> ProcessesWithWindows()
    {
        var list = new List<ProcessChoice>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.MainWindowHandle != IntPtr.Zero && p.MainWindowTitle.Length > 0)
                    list.Add(new(p.Id, p.ProcessName + ".exe", p.MainWindowTitle.Length > 50 ? p.MainWindowTitle[..50] + "…" : p.MainWindowTitle));
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            finally { p.Dispose(); }
        }
        return [.. list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
    }

    async Task AdvStartAsync()
    {
        if (AdvByProcess && SelectedProcess is null) { Say(L.T("apps.err.pick_process")); return; }
        if (await Need() is not { } c) return;
        _simple = false;
        try
        {
            await c.RecStartAsync(AdvByProcess ? RecordingMode.Process : RecordingMode.NewProcesses, AdvByProcess ? SelectedProcess!.Pid : null, driver: UseDriver);
        }
        catch (Exception e) when (e is HelperException or HelperUnavailableException) { (Fail ?? Say)(Texts.Friendly(e)); return; }
        _recStarted = DateTime.UtcNow;
        Rows.Clear();
        Raise(nameof(HasRows));
        RecText = "0:00";
        AdvRecording = true;
    }

    async Task AdvStopAsync()
    {
        if (await Need() is not { } c) return;
        try { _last = await c.RecStopAsync(); }
        catch (Exception e) when (e is HelperException or HelperUnavailableException) { AdvRecording = false; (Fail ?? Say)(Texts.Friendly(e)); return; }
        AdvRecording = false;
        LoadRows(_last);
        await RefreshAsync();
    }

    // ---------- список с галочками ----------

    public ObservableCollection<FlowRow> Rows { get; } = [];
    public bool HasRows => Rows.Count > 0;

    string _profileTitle = "";
    public string ProfileTitle
    {
        get => _profileTitle;
        set
        {
            if (!Set(ref _profileTitle, value)) return;
            if (!_settingAuto) _titleAuto = false;   // название поправил человек — больше не подставляем
            Raise(nameof(CanSave));
            CommandManager.InvalidateRequerySuggested();
        }
    }
    bool _settingAuto, _titleAuto;

    int _selectedVariant;
    public int SelectedVariant { get => _selectedVariant; set => Set(ref _selectedVariant, value); }

    string _preview = "";
    /// <summary>Что получится из отмеченного.</summary>
    public string Preview { get => _preview; private set => Set(ref _preview, value); }

    string _recapNote = "";
    /// <summary>Что не записалось (например, UDP без драйвера).</summary>
    public string RecapNote { get => _recapNote; private set { if (Set(ref _recapNote, value)) Raise(nameof(HasRecapNote)); } }
    public bool HasRecapNote => RecapNote.Length > 0;

    ProfileDraft? _advDraft;
    public bool CanSave => _advDraft is { IsEmpty: false } && ProfileTitle.Trim().Length > 0;

    /// <summary>Записанное — в таблицу. Галочки по умолчанию — как решила бы программа сама.</summary>
    public void LoadRows(RecordingResult rec)
    {
        _last = rec;
        Rows.Clear();
        var sorted = AppProfileBuilder.Sort(rec);
        foreach (var f in rec.Flows.OrderBy(f => sorted[f] is null ? 0 : 1).ThenBy(f => f.Process).ThenBy(f => f.Address))
        {
            var row = new FlowRow(f, sorted[f], sorted[f] is null);
            row.Changed += Recalc;
            Rows.Add(row);
        }
        RecapNote = string.Join(" ", rec.Notes) + (rec.Truncated ? " " + L.T("apps.debug.too_many") : "");
        Raise(nameof(HasRows));
        Recalc();
        CommandManager.InvalidateRequerySuggested();
    }

    void Denoise()
    {
        if (_last is null) return;
        var sorted = AppProfileBuilder.Sort(_last);
        foreach (var r in Rows) r.IsChecked = sorted[r.Flow] is null;
    }

    void SetAll(bool value)
    {
        foreach (var r in Rows) r.IsChecked = value;
    }

    void Recalc()
    {
        if (_last is null) return;
        _advDraft = AppProfileBuilder.FromRecording(_last, [.. Rows.Where(r => r.IsChecked).Select(r => r.Flow)]);
        if (ProfileTitle.Length == 0 || _titleAuto) { _settingAuto = true; ProfileTitle = _advDraft.Title; _settingAuto = false; _titleAuto = true; }
        Preview = _advDraft.IsEmpty
            ? L.T("apps.debug.preview_empty")
            : Summarize(_advDraft.TcpPorts, _advDraft.UdpPorts, _advDraft.Addresses.Count);
        Raise(nameof(CanSave));
        CommandManager.InvalidateRequerySuggested();
    }

    async Task SaveAdvancedAsync()
    {
        if (_advDraft is not { IsEmpty: false } d || await Need() is not { } c) return;
        if (Variants.Count == 0) await RefreshAsync();
        if (Variants.Count == 0) { Say(L.T("apps.err.no_variants")); return; }
        int v = Math.Clamp(SelectedVariant, 0, Variants.Count - 1);
        AppProfileInfo? info = null;
        if (!await Guard(L.T("apps.busy.save_profile"), async () => info = await c.AppProfileSetAsync(null, ProfileTitle.Trim(), d.TcpPorts, d.UdpPorts, d.Addresses, v))) return;
        await AfterChange();
        await RefreshAsync();
        Say(L.T("apps.profile.saved_summary", new { title = info?.Title, summary = info is null ? "" : Summarize(info) }));
    }
}
