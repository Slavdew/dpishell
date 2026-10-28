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
    readonly string _reason;

    public FlowRow(RecordedFlow flow, string? reason, bool isChecked)
    {
        Flow = flow;
        _reason = reason ?? "";
        _checked = isChecked;
    }

    public RecordedFlow Flow { get; }
    /// <summary>Почему программа сама бы это убрала («локальная сеть»); пусто — оставила бы.</summary>
    public string Reason => L.Relocalize(_reason);
    public bool IsChecked { get => _checked; set { if (Set(ref _checked, value)) Changed?.Invoke(); } }
    public event Action? Changed;

    public string Process => Flow.Process;
    public string Address => Flow.Address;
    public int Port => Flow.Port;
    public string Kind => Flow.Proto.ToUpperInvariant();
    public int Count => Flow.Count;
    public string State => Flow.State switch { "works" => L.T("apps.flow.state.works"), "noreply" => L.T("apps.flow.state.noreply"), _ => Flow.Proto == "udp" ? "—" : "" };
}

/// <summary>Пункт выбора стратегии профиля: ключ («default», «own» или id стратегии) и подпись.</summary>
/// <remarks>Подпись — правилом сборки: при смене языка пункт читается заново на месте, выбор в списке не сбрасывается.</remarks>
public sealed class ChoiceItem(string key, Func<string> title) : Observable
{
    public string Key { get; } = key;
    public string Title => title();
    public override string ToString() => Title;
}

/// <summary>
/// Строка списка профилей: название и краткое описание слева, справа — выбор стратегии (при «Разных стратегиях»), тумблер «вкл» и меню «⋯»
/// («Экспорт», «Скопировать для Flowseal», «Удалить»). Тумблер и выбор сразу уходят помощнику; список после этого перечитывается.
/// </summary>
public sealed class ProfileRow : Observable
{
    readonly AppsViewModel _owner;
    ChoiceItem? _selected;

    public ProfileRow(AppProfileInfo info, AppsViewModel owner)
    {
        Info = info;
        _owner = owner;
        Choices = owner.ChoicesFor(info);
        _selected = Choices.FirstOrDefault(c => c.Key == info.Strategy) ?? Choices[0];
    }

    public AppProfileInfo Info { get; }
    public string Id => Info.Id;
    public string Title => Info.Title;
    public string Summary => AppsViewModel.Summarize(Info);
    /// <summary>Свой фильтр профиля (если задан) — в подсказке к описанию, для опытных.</summary>
    public string? FilterTip => string.Join(Environment.NewLine, new[] { Info.TcpFilter, Info.UdpFilter }.Where(f => f.Length > 0)) is { Length: > 0 } f ? f : null;

    /// <summary>Тумблер «вкл»: включает или выключает только этот профиль.</summary>
    public bool IsOn
    {
        get => Info.Enabled;
        set { if (value != Info.Enabled) _ = _owner.SetEnabledAsync(Id, value); }
    }

    /// <summary>«По умолчанию» и игровые части стратегий каталога.</summary>
    public IReadOnlyList<ChoiceItem> Choices { get; }
    public ChoiceItem? Selected
    {
        get => _selected;
        set
        {
            if (value is null || value == _selected) return;   // список ставит null при пересборке — не выбор человека
            _selected = value;
            Raise();
            _ = _owner.SetStrategyAsync(Id, value.Key);
        }
    }
    public string ChoiceTip => L.T("apps.profile.choice");
    /// <summary>Свои параметры (из сборки или строки профиля): профиль идёт ими всегда, выбора стратегии нет — на его месте «Из профиля» (вопрос 93).</summary>
    public bool HasOwn => Info.TcpOwn.Length > 0 || Info.UdpOwn.Length > 0;
    /// <summary>Подсказка к «Из профиля» — сами параметры.</summary>
    public string OwnTip => string.Join(Environment.NewLine, new[] { ("TCP", Info.TcpOwn), ("UDP", Info.UdpOwn) }.Where(x => x.Item2.Length > 0).Select(x => $"{x.Item1}: {x.Item2}"));
    /// <summary>Выбор виден только при «Разных стратегиях» и без своих параметров; без «Разных стратегий» профиль идёт «По умолчанию», выбор хранится.</summary>
    public bool ShowChoice => _owner.Separate && !HasOwn;
    internal void SeparateChanged() => Raise(nameof(ShowChoice));

    /// <summary>Меню «⋯» строки.</summary>
    public IReadOnlyList<MenuEntry> Menu() =>
    [
        new(L.T("apps.profile.export"), () => _owner.ExportProfile(Id), Tip: L.T("apps.profile.export_tip")),
        new(L.T("apps.profile.flowseal"), () => _owner.CopyProfile(Id), Tip: L.T("apps.profile.flowseal_tip")),
        MenuEntry.Line,
        new(L.T("apps.profile.remove"), () => _ = _owner.RemoveAsync(Id)),
    ];
}

/// <summary>
/// «Игры и программы» (ТЗ 13, решения владельца 6–8): список профилей; «Добавить профиль…» — окно «Новый профиль» (формат Flowseal);
/// «Записать игру…» — Начать → игра → Готово, всё само;
/// «Отладка подключений» для опытных — список записанного с галочками, ручной выбор, сохранение профиля. Запись и применение —
/// через помощника (команды rec_*, app_profile_*); окно ничего не меняет само. Действие обратимо: «Удалить» у профиля.
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
    bool _debugOpen;
    /// <summary>
    /// Раздел «Отладка подключений (для опытных)» внизу вкладки раскрыт. Раздел виден всегда (UX-аудит 8, §3 №9), свёрнут по умолчанию;
    /// «Отладка подключений →» в карточке записи раскрывает его. До перезапуска окна.
    /// </summary>
    public bool DebugOpen { get => _debugOpen; set => Set(ref _debugOpen, value); }

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
            if (value != AppsStep.Idle) RecordOpen = true;
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

    // Строки шага мастера хранятся правилом сборки: после смены языка читаются на новом.
    readonly LiveText _status = new(), _detail = new(), _recText = new();
    /// <summary>Главная строка простого режима: что сейчас происходит.</summary>
    public string Status { get => _status.Value; private set { _status.Set(value); Raise(); Raise(nameof(HasStatus)); } }
    void StatusIs(Func<string> build) { _status.Set(build); Raise(nameof(Status)); Raise(nameof(HasStatus)); }
    void DetailIs(Func<string> build) { _detail.Set(build); Raise(nameof(Detail)); Raise(nameof(HasDetail)); }
    void RecTextIs(Func<string> build) { _recText.Set(build); Raise(nameof(RecText)); }
    public bool HasStatus => Status.Length > 0;
    /// <summary>«Подробнее» — до начала и после неудачи.</summary>
    public bool ShowMore => Step is AppsStep.Idle or AppsStep.Failed;
    public bool ShowOffHint => OptimizationOff && (Step == AppsStep.Check || HasProfiles && Step != AppsStep.Done);   // в «Готово» то же сказано в итоге

    /// <summary>Вторая строка: подробности (попытка, предупреждения записи).</summary>
    public string Detail { get => _detail.Value; private set { _detail.Set(value); Raise(); Raise(nameof(HasDetail)); } }
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

    public static string Summarize(AppProfileInfo p) =>
        Summarize(p.TcpPorts, p.UdpPorts, p.Addresses.Count, p.TcpPayloadLength > 0 || p.TcpPayloadHex.Length > 0 || p.TcpFilter.Length > 0, p.UdpPayloadLength > 0 || p.UdpPayloadHex.Length > 0 || p.UdpFilter.Length > 0,
            p.TcpFilter.Length > 0, p.UdpFilter.Length > 0);

    /// <summary>«TCP … · UDP … · адресов: N»; у протокола с сигнатурой или своим фильтром — пометка, без адресов — без «адресов: 0».</summary>
    static string Summarize(string tcp, string udp, int addresses, bool tcpSig = false, bool udpSig = false, bool tcpFilter = false, bool udpFilter = false)
    {
        var parts = new List<string>();
        static string Mark(bool sig, bool filter) => filter ? " (" + L.T("apps.profile.filter_mark") + ")" : sig ? " (" + L.T("apps.profile.sig_mark") + ")" : "";
        if (tcp.Length > 0) parts.Add("TCP " + tcp + Mark(tcpSig, tcpFilter));
        if (udp.Length > 0) parts.Add("UDP " + udp + Mark(udpSig, udpFilter));
        if (addresses > 0 || !(tcpSig || udpSig)) parts.Add(L.T("apps.profile.addresses", new { n = addresses }));
        return string.Join(" · ", parts);
    }

    // ---------- профили ----------

    public ObservableCollection<ProfileRow> Profiles { get; } = [];
    public bool HasProfiles => Profiles.Count > 0;
    /// <summary>Есть профиль без своих параметров — только у таких есть выбор стратегии, и только тогда видна галка «Разные стратегии».</summary>
    public bool HasChoosable => Profiles.Any(p => !p.HasOwn);
    /// <summary>Хоть один профиль включён (для строки «Свои профили срабатывают первыми» у «Общего»).</summary>
    public bool AnyOn => Profiles.Any(p => p.Info.Enabled);

    bool _separate;
    /// <summary>
    /// «Разные стратегии» (у помощника, влияет на командную строку winws): включено — у строк выбор стратегии; выключено — все профили «По умолчанию»
    /// (игровая часть стратегии ПК), выбор у строк скрыт, но хранится.
    /// </summary>
    public bool Separate
    {
        get => _separate;
        set { if (value != _separate) _ = SetSeparateAsync(value); }
    }
    void SeparateIs(bool value)
    {
        if (!Set(ref _separate, value, nameof(Separate))) return;
        foreach (var r in Profiles) r.SeparateChanged();
    }

    /// <summary>Пункты выбора стратегии профиля: «По умолчанию», игровые части стратегий (кроме совпадающей со стратегией ПК). У профиля со своими параметрами выбора нет.</summary>
    internal IReadOnlyList<ChoiceItem> ChoicesFor(AppProfileInfo p)
    {
        var list = new List<ChoiceItem> { new(AppProfiles.Default, () => L.T("apps.profile.choice.default")) };
        foreach (var v in _variantsRaw.Where(v => v.Index > 0 && v.StrategyId.Length > 0)) list.Add(new(v.StrategyId, () => L.Relocalize(v.Title)));
        // Выбранная стратегия совпала параметрами со стратегией ПК (или её убрали из списка вариантов) — показать её всё равно, а не подменять выбор.
        var chosen = p.Strategy;
        if (list.All(c => c.Key != chosen) && AppProfiles.IsValidChoice(chosen) && chosen != AppProfiles.Own) list.Add(new(chosen, () => chosen));
        return list;
    }

    /// <summary>Тумблер строки: включить или выключить один профиль.</summary>
    public async Task SetEnabledAsync(string id, bool on)
    {
        if (await Need() is not { } c) { await RefreshAsync(); return; }
        await Guard(L.T(on ? "apps.busy.profile_on" : "apps.busy.profile_off"), () => c.AppProfileOptionsAsync(id, enabled: on));
        await AfterChange();
        await RefreshAsync();
    }

    /// <summary>Выбор стратегии у строки (только при «Разных стратегиях»).</summary>
    public async Task SetStrategyAsync(string id, string strategy)
    {
        if (await Need() is not { } c) { await RefreshAsync(); return; }
        await Guard(L.T("apps.busy.apply"), () => c.AppProfileOptionsAsync(id, strategy: strategy));
        await AfterChange();
        await RefreshAsync();
    }

    async Task SetSeparateAsync(bool on)
    {
        if (await Need() is not { } c) { Raise(nameof(Separate)); return; }
        await Guard(L.T("apps.busy.apply"), () => c.AppProfilesSeparateAsync(on));
        await AfterChange();
        await RefreshAsync();
        Raise(nameof(Separate));
    }

    // ---------- запись игры: карточка под списком по кнопке «Записать игру…» ----------

    bool _recordOpen;
    /// <summary>
    /// Карточка записи соединений игры открыта. Запись — шаг на вкладке, а не модальное окно: она длится минуты, пока человек в игре,
    /// и ведёт через несколько шагов («Готово» → «Заработало?» → «Включить оптимизацию»). Пока шаг не «Начать» — карточка открыта.
    /// </summary>
    public bool RecordOpen { get => _recordOpen; set => Set(ref _recordOpen, value); }
    /// <summary>«Записать игру…» — открыть карточку; окно прокручивает к ней.</summary>
    public ICommand RecordCmd => _recordCmd ??= new RelayCommand(() => { RecordOpen = true; RecordRequested?.Invoke(); });
    ICommand? _recordCmd;
    public event Action? RecordRequested;
    /// <summary>«Закрыть» карточку записи: итог прошлой записи не хранится — следующее открытие начинается с «Начать».</summary>
    public ICommand CloseRecordCmd => _closeRecordCmd ??= new RelayCommand(() =>
    {
        if (Step is AppsStep.Done or AppsStep.Failed) { Step = AppsStep.Idle; Status = ""; Detail = ""; }
        RecordOpen = false;
    }, () => ShowStart);
    ICommand? _closeRecordCmd;

    // ---------- окно «Новый профиль» ----------

    /// <summary>Окно «Новый профиль» над этой моделью (в тестах — подмена): закрывается по <see cref="AddClosed"/>.</summary>
    public Action<AppsViewModel> AddProfileDialog { get; set; } = ProfileAddView.ShowDialog;
    /// <summary>Окну «Новый профиль» — закрыться: профиль добавлен или «Отмена».</summary>
    public event Action? AddClosed;
    bool _added;
    public ICommand AddProfileCmd => _addCmd ??= new RelayCommand(OpenAddProfile);
    ICommand? _addCmd;
    public ICommand CancelAddCmd => _cancelAddCmd ??= new RelayCommand(() => AddClosed?.Invoke());
    ICommand? _cancelAddCmd;

    /// <summary>
    /// Открыть «Новый профиль» с пустыми полями. Не добавили (отмена, крестик) — введённое не хранится. «Вставить из буфера» со строкой профиля
    /// (<c>dpishell-game:1:…</c>) закрывает окно и открывает «Импорт профиля» с этой строкой.
    /// </summary>
    public void OpenAddProfile()
    {
        ClearImport();
        _added = false;
        _toCode = null;
        AddProfileDialog(this);
        if (!_added) ClearImport();
        if (_toCode is { } code) { _toCode = null; OpenImportCode(code); }
    }
    string? _toCode;

    void ClearImport()
    {
        ImportFilter = ImportParams = ImportSubnets = ImportTitle = "";
        ImportMessageIs(() => "", false);
    }

    // Варианты приходят от помощника (названия — «как у «general (ALT)»»): хранятся как есть, показываются на языке окна.
    IReadOnlyList<ProfileVariant> _variantsRaw = [], _variants = [];
    public IReadOnlyList<ProfileVariant> Variants { get => _variants; private set { _variantsRaw = value; _variants = LocalizeVariants(value); Raise(); } }
    static IReadOnlyList<ProfileVariant> LocalizeVariants(IReadOnlyList<ProfileVariant> list) => [.. list.Select(v => v with { Title = L.Relocalize(v.Title) })];

    /// <summary>Перечитать профили и варианты у помощника. Помощника нет — пусто.</summary>
    public async Task RefreshAsync()
    {
        if (_helper.Reader is not { } c) { SetProfiles([]); return; }
        try
        {
            var info = await c.AppProfilesAsync();
            Variants = info.Variants;
            SeparateIs(info.Separate);
            SetProfiles(info.Profiles);
        }
        catch (Exception e) when (e is HelperException or HelperUnavailableException) { SetProfiles([]); }
        Raise(nameof(OptimizationOff));
    }

    void SetProfiles(IReadOnlyList<AppProfileInfo> list)
    {
        Profiles.Clear();
        foreach (var p in list) Profiles.Add(new(p, this));
        Raise(nameof(HasProfiles));
        Raise(nameof(HasChoosable));
        Raise(nameof(AnyOn));
        Raise(nameof(ShowOffHint));
    }

    /// <summary>«Удалить»: профиль удаляется, оптимизация перезапускается без него.</summary>
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

    // ---------- импорт и экспорт (формат Flowseal: фильтр, параметры, подсети) ----------

    /// <summary>Имена файлов фейков из каталога стратегий (задаёт окно); профиль из импорта других файлов не принимает.</summary>
    public Func<IReadOnlySet<string>> KnownFakes { get; set; } = () => new HashSet<string>();
    public Action<string> CopyText { get; set; } = text => System.Windows.Clipboard.SetText(text);
    public Func<string?> ReadText { get; set; } = () => System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : null;
    public Action<string> OpenUrl { get; set; } = url => { UiGuard.CheckOpen(url); Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); };

    string _impTitle = "", _impFilter = "", _impParams = "", _impSubnets = "";
    readonly LiveText _impMsg = new();
    bool _impFailed;
    public string ImportTitle { get => _impTitle; set => Set(ref _impTitle, value); }
    public string ImportFilter { get => _impFilter; set { if (Set(ref _impFilter, value)) ImportInputChanged(); } }
    public string ImportParams { get => _impParams; set { if (Set(ref _impParams, value)) ImportInputChanged(); } }
    /// <summary>«Добавить» доступна, как только есть фильтр или параметры, — и после «Вставить из буфера», без ввода с клавиатуры.</summary>
    void ImportInputChanged() { Raise(nameof(CanImport)); CommandManager.InvalidateRequerySuggested(); }
    public string ImportSubnets { get => _impSubnets; set => Set(ref _impSubnets, value); }
    /// <summary>Итог или отказ последнего импорта; хранится правилом сборки — после смены языка читается заново.</summary>
    public string ImportMessage => _impMsg.Value;
    public bool HasImportMessage => ImportMessage.Length > 0;
    void ImportMessageIs(Func<string> build, bool failed)
    {
        ImportFailed = failed;
        _impMsg.Set(build);
        Raise(nameof(ImportMessage));
        Raise(nameof(HasImportMessage));
        Raise(nameof(ShowImportError));
        Raise(nameof(ShowImportResult));
    }
    /// <summary>Отказ — текстом в окне «Новый профиль», окно не закрывается.</summary>
    public bool ShowImportError => ImportFailed && HasImportMessage;
    /// <summary>Итог добавления (с пометками импорта) — под списком профилей.</summary>
    public bool ShowImportResult => !ImportFailed && HasImportMessage;
    /// <summary>Последнее сообщение — отказ, а не итог.</summary>
    public bool ImportFailed { get => _impFailed; private set => Set(ref _impFailed, value); }
    public bool CanImport => ImportParams.Trim().Length > 0 || ImportFilter.Trim().Length > 0;

    public ICommand ImportCmd => _importCmd ??= RelayCommand.Async(ImportAsync, () => CanImport);
    ICommand? _importCmd;
    public ICommand PasteAllCmd => _pasteCmd ??= new RelayCommand(PasteAll);
    ICommand? _pasteCmd;
    public ICommand OpenGamesCmd => _gamesCmd ??= new RelayCommand(() =>
    {
        try { OpenUrl(NetViewModel.GamesUrl); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Say(e.Message); }
    });
    ICommand? _gamesCmd;

    void Refuse(string text) => ImportMessageIs(() => text, true);
    void Refuse(Func<string> build) => ImportMessageIs(build, true);

    /// <summary>Вставить сразу все три куска из буфера (с заголовками, как их отдаёт «Скопировать для Flowseal»); строка профиля — в «Импорт профиля».</summary>
    void PasteAll()
    {
        var text = ReadText();
        if (string.IsNullOrWhiteSpace(text)) { Refuse(() => L.T("apps.import.clipboard_empty")); return; }
        if (ProfileCode.Looks(text)) { _toCode = text.Trim(); AddClosed?.Invoke(); return; }
        if (ProfileExchange.SplitCombined(text) is not { } t) { Refuse(() => L.T("apps.import.no_marks")); return; }
        ImportFilter = t.Filter; ImportParams = t.Params; ImportSubnets = t.Subnets;
        ImportMessageIs(() => "", false);
    }

    /// <summary>Разобрать три поля и сохранить профиль. Любое «не подошло» — отказ с причиной, ничего не записывается.</summary>
    public async Task ImportAsync()
    {
        string filter = ImportFilter, pars = ImportParams, subnets = ImportSubnets, rawTitle = ImportTitle.Trim();
        var fakes = KnownFakes();
        string Title() => rawTitle.Length > 0 ? rawTitle : L.T("apps.import.default_title");
        // Текст отказа строится заново на языке окна: разбор — чистая функция.
        var r = ProfileExchange.Import(Title(), filter, pars, subnets, fakes);
        if (r.Profile is not { } p) { Refuse(() => ProfileExchange.Import(Title(), filter, pars, subnets, fakes).Problem ?? ""); return; }
        if (await Need() is not { } c) return;
        if (Variants.Count == 0) await RefreshAsync();
        if (Variants.Count == 0) { Refuse(() => L.T("apps.err.no_variants")); return; }
        AppProfileInfo? info = null;
        try
        {
            if (!await Guard(L.T("apps.busy.save_profile"), async () =>
                    info = await c.AppProfileSetAsync(null, p.Title, p.TcpPorts, p.UdpPorts, p.Addresses, 0, p.ToSignature()))) return;
        }
        catch (HelperException e) { Refuse(e.Message); return; }
        await AfterChange();
        await RefreshAsync();
        var savedTitle = info?.Title;
        string Result()
        {
            var notes = ProfileExchange.Import(Title(), filter, pars, subnets, fakes).Profile?.Notes ?? [];
            return Added(savedTitle, true) + (notes.Count > 0 ? " " + string.Join(" ", notes) : "");
        }
        ImportFilter = ImportParams = ImportSubnets = ImportTitle = "";
        ImportMessageIs(Result, false);
        Say(L.T("apps.profile.saved", new { title = savedTitle }));
        _added = true;
        AddClosed?.Invoke();
    }

    /// <summary>Итог добавления одной строкой; со своими параметрами — что профиль идёт ими.</summary>
    static string Added(string? title, bool own) => L.T(own ? "apps.profile.added_own" : "apps.profile.added", new { title });

    // ---------- «Импорт…»: строка профиля dpishell-game:1:… с предпросмотром ----------

    /// <summary>Окно «Импорт профиля» над этой моделью (в тестах — подмена): закрывается по <see cref="CodeClosed"/>.</summary>
    public Action<AppsViewModel> ImportDialog { get; set; } = ProfileImportView.ShowDialog;
    public event Action? CodeClosed;
    public ICommand ImportCodeCmd => _importCodeCmd ??= new RelayCommand(() => OpenImportCode(""));
    ICommand? _importCodeCmd;
    public ICommand AddCodeCmd => _addCodeCmd ??= RelayCommand.Async(AddCodeAsync, () => _code is not null);
    ICommand? _addCodeCmd;
    public ICommand PasteCodeCmd => _pasteCodeCmd ??= new RelayCommand(() =>
    {
        var text = ReadText();
        if (string.IsNullOrWhiteSpace(text)) { CodeErrorIs(() => L.T("apps.import.clipboard_empty")); return; }
        CodeText = text.Trim();
    });
    ICommand? _pasteCodeCmd;
    public ICommand CancelCodeCmd => _cancelCodeCmd ??= new RelayCommand(() => CodeClosed?.Invoke());
    ICommand? _cancelCodeCmd;

    /// <summary>Открыть «Импорт профиля» (со строкой из «Новый профиль» или пустым). Закрыли без добавления — строка не хранится.</summary>
    public void OpenImportCode(string text)
    {
        CodeText = text;
        ImportDialog(this);
        CodeText = "";
    }

    string _codeText = "";
    AppProfile? _code;
    readonly LiveText _codePreview = new(), _codeError = new();
    /// <summary>Строка профиля: разбирается сразу при вводе — ниже видно, что внутри, или почему не подходит.</summary>
    public string CodeText
    {
        get => _codeText;
        set { if (Set(ref _codeText, value ?? "")) ParseCode(); }
    }
    public string CodePreview => _codePreview.Value;
    public bool HasCodePreview => _code is not null;
    public string CodeError => _codeError.Value;
    public bool HasCodeError => CodeError.Length > 0;

    void ParseCode()
    {
        _code = null;
        var text = _codeText;
        var fakes = KnownFakes();
        if (text.Trim().Length == 0) { _codePreview.Set(() => ""); CodeErrorIs(() => ""); }
        else
        {
            try
            {
                _code = ProfileCode.Decode(text, fakes.Contains);
                var p = _code;
                _codePreview.Set(() => CodeSummary(p));
                CodeErrorIs(() => "");
            }
            catch (StrategyFormatException)
            {
                _codePreview.Set(() => "");
                // Причина строится заново на языке окна: разбор — чистая функция.
                CodeErrorIs(() => { try { ProfileCode.Decode(text, fakes.Contains); return ""; } catch (StrategyFormatException e) { return e.Message; } });
            }
        }
        Raise(nameof(CodePreview)); Raise(nameof(HasCodePreview));
        CommandManager.InvalidateRequerySuggested();
    }

    void CodeErrorIs(Func<string> build) { _codeError.Set(build); Raise(nameof(CodeError)); Raise(nameof(HasCodeError)); }

    /// <summary>Что внутри строки: название, протоколы и порты, сигнатура или свой фильтр, параметры текстом, подсети — число и первые три.</summary>
    public static string CodeSummary(AppProfile p)
    {
        var lines = new List<string> { p.Title };
        var pars = new List<string>();
        foreach (var tcp in new[] { true, false })
        {
            var ports = tcp ? p.TcpPorts : p.UdpPorts;
            if (ports.Length == 0) continue;
            string proto = tcp ? "TCP" : "UDP", filter = tcp ? p.TcpCustomFilter : p.UdpCustomFilter, hex = tcp ? p.TcpPayloadHex : p.UdpPayloadHex, desync = tcp ? p.TcpDesync : p.UdpDesync;
            int len = tcp ? p.TcpPayloadLength : p.UdpPayloadLength;
            var match = filter.Length > 0 ? L.T("apps.code.filter", new { filter })
                : len > 0 || hex.Length > 0 ? L.T("apps.code.sig", new { what = string.Join(", ", new[] { len > 0 ? L.T("apps.code.len", new { n = len }) : "", hex.Length > 0 ? L.T("apps.code.hex", new { hex = Bytes(hex) }) : "" }.Where(x => x.Length > 0)) })
                : L.T("apps.code.by_addresses");
            lines.Add($"{proto} {ports} · {match}");
            pars.Add(proto + ": " + (desync.Length > 0 ? desync : L.T("apps.code.pc_strategy")));
        }
        // Своих параметров нет ни у одного протокола — одной строкой.
        if (p.HasOwn) lines.AddRange(pars); else lines.Add(L.T("apps.code.params_pc"));
        lines.Add(p.Addresses.Count == 0 ? L.T("apps.code.no_subnets")
            : L.T("apps.code.subnets", new { n = p.Addresses.Count, first = string.Join(", ", p.Addresses.Take(3)) + (p.Addresses.Count > 3 ? ", …" : "") }));
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Начало пакета по байтам, как в справке и сборках: «0000044d» → «00 00 04 4D».</summary>
    public static string Bytes(string hex)
    {
        var h = hex.Replace(" ", "").ToUpperInvariant();
        return string.Join(' ', Enumerable.Range(0, (h.Length + 1) / 2).Select(i => h.Substring(i * 2, Math.Min(2, h.Length - i * 2))));
    }

    /// <summary>«Добавить» в «Импорт профиля»: профиль уходит помощнику (он проверяет всё заново), окно закрывается; отказ — в окне.</summary>
    public async Task AddCodeAsync()
    {
        if (_code is not { } p || await Need() is not { } c) return;
        AppProfileInfo? info = null;
        string? refused = null;
        // Отказ помощника (его проверка строже окна или профилей уже восемь) — в окне импорта, как и ошибка разбора.
        if (!await Guard(L.T("apps.busy.save_profile"), async () =>
            {
                try { info = await c.AppProfileSetAsync(null, p.Title, p.TcpPorts, p.UdpPorts, p.Addresses, 0, ProfileCode.ToSignature(p)); }
                catch (HelperException e) { refused = e.Message; }
            })) return;
        if (refused is { } why) { CodeErrorIs(() => why); return; }
        await AfterChange();
        await RefreshAsync();
        var title = info?.Title; var own = p.HasOwn;
        ImportMessageIs(() => Added(title, own), false);
        Say(L.T("apps.profile.saved", new { title }));
        CodeClosed?.Invoke();
    }

    /// <summary>«Экспорт»: строка профиля в буфер — порты, сигнатура или фильтр, адреса и свои параметры; включён ли и выбранная стратегия не входят.</summary>
    public void ExportProfile(string id)
    {
        if (Profiles.FirstOrDefault(p => p.Id == id)?.Info is not { } i) return;
        var profile = new AppProfile
        {
            Id = i.Id, Title = i.Title, TcpPorts = i.TcpPorts, UdpPorts = i.UdpPorts, Addresses = [.. i.Addresses], TcpDesync = i.TcpOwn, UdpDesync = i.UdpOwn,
            TcpPayloadLength = i.TcpPayloadLength, TcpPayloadHex = i.TcpPayloadHex, UdpPayloadLength = i.UdpPayloadLength, UdpPayloadHex = i.UdpPayloadHex,
            TcpCustomFilter = i.TcpFilter, UdpCustomFilter = i.UdpFilter,
        };
        try { CopyText(ProfileCode.Encode(profile)); Say(L.T("apps.profile.exported", new { title = i.Title })); }
        catch (StrategyFormatException e) { Say(e.Message); }
        catch (System.Runtime.InteropServices.ExternalException) { Say(L.T("apps.import.copy_failed")); }
    }

    /// <summary>«Скопировать для Flowseal»: три куска профиля текстом (фильтр, параметры, подсети) — для сборки Flowseal; параметры — те, которыми профиль идёт сейчас.</summary>
    public void CopyProfile(string id)
    {
        var row = Profiles.FirstOrDefault(p => p.Id == id);
        if (row is null) return;
        var i = row.Info;
        if ((i.TcpPorts.Length > 0 && i.TcpDesync.Length == 0) || (i.UdpPorts.Length > 0 && i.UdpDesync.Length == 0)) { Say(L.T("apps.import.copy_old_helper")); return; }
        var profile = new AppProfile
        {
            Id = i.Id, Title = i.Title, TcpPorts = i.TcpPorts, UdpPorts = i.UdpPorts, Addresses = [.. i.Addresses], TcpDesync = i.TcpDesync, UdpDesync = i.UdpDesync,
            TcpPayloadLength = i.TcpPayloadLength, TcpPayloadHex = i.TcpPayloadHex, UdpPayloadLength = i.UdpPayloadLength, UdpPayloadHex = i.UdpPayloadHex,
            TcpCustomFilter = i.TcpFilter, UdpCustomFilter = i.UdpFilter,
        };
        try { CopyText(ProfileExchange.Combine(ProfileExchange.Export(profile))); Say(L.T("apps.import.copied", new { title = i.Title })); }
        catch (System.Runtime.InteropServices.ExternalException) { Say(L.T("apps.import.copy_failed")); }
    }

    // ---------- простой режим ----------

    bool _simple;
    RecordingResult? _last;          // последняя запись: «Подробнее» открывает её в списке с галочками
    ProfileDraft? _draft;
    string? _profileId;
    int _attempt, _variant;
    DateTime _recStarted;
    public RecordingResult? LastRecording => _last;

    /// <summary>Пока идёт запись: время и число подключений.</summary>
    public string RecText { get => _recText.Value; private set { _recText.Set(value); Raise(); } }

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
        StatusIs(() => L.T("apps.rec.status"));
        DetailIs(() => L.T("apps.rec.detail"));
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
        StatusIs(() => L.T("apps.busy.parse")); Detail = "";
        RecordingResult rec;
        try { rec = await c.RecStopAsync(); }
        catch (Exception e) when (e is HelperException or HelperUnavailableException) { Failed(Texts.Friendly(e)); return; }
        _last = rec;
        _draft = AppProfileBuilder.FromRecording(rec);
        if (_draft.IsEmpty)
        {
            var nothing = _draft;
            StatusIs(() => L.T("apps.rec.nothing"));
            DetailIs(() => L.T("apps.rec.nothing_detail") + Notes(nothing));
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
        StatusIs(() => L.T("apps.busy.apply_settings"));
        AppProfileInfo? info = null;
        var d = _draft;
        bool ok = await Guard(L.T("apps.busy.apply_settings"), async () =>
            info = await c.AppProfileSetAsync(_profileId, d.Title, d.TcpPorts, d.UdpPorts, d.Addresses, variant));
        if (!ok || info is null) { StatusIs(() => L.T("apps.err.apply")); DetailIs(() => L.T("apps.err.apply_detail")); Step = AppsStep.Failed; return; }
        _profileId = info.Id;
        await AfterChange();
        await RefreshAsync();
        var applied = info; var attemptNo = _attempt;
        StatusIs(() => L.T("apps.check.applied", new { title = applied.Title, summary = Summarize(applied) }));
        DetailIs(() => L.T("apps.check.attempt", new { attempt = attemptNo, max = MaxAttempts, variant = L.Relocalize(applied.VariantTitle) }) + Notes(d));
        Step = AppsStep.Check;
    }

    void Yes()
    {
        // Одна фраза вместо итога и жёлтой плашки; «Удалить» — в списке профилей выше.
        var savedTitle = _draft?.Title; var savedOff = OptimizationOff;
        string Saved() => L.T(savedOff ? "apps.profile.saved_off" : "apps.profile.saved", new { title = savedTitle ?? L.T("apps.profile.default_title") });
        var saved = Saved();
        StatusIs(Saved);
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
        var giveupAttempt = _attempt;
        StatusIs(() => L.Plural("apps.giveup", giveupAttempt));
        DetailIs(() => L.T("apps.giveup_detail"));
        Step = AppsStep.Failed;
    }

    void OpenDetail()
    {
        if (_last is not null) LoadRows(_last);
        DebugOpen = true;
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
            var shownSecs = secs; var flows = st.Flows;
            RecTextIs(() => L.T("apps.rec.progress", new { time = $"{(int)shownSecs / 60}:{(int)shownSecs % 60:00}", n = flows }));
            if (!st.Running)
                DetailIs(() => L.T("apps.rec.expired"));
            else if (!st.UdpRecorded && st.Notes.Count > 0)
            {
                var notes = st.Notes.ToList();
                DetailIs(() => string.Join(" ", notes.Select(x => L.Relocalize(x))));
            }
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

    readonly LiveText _preview = new();
    /// <summary>Что получится из отмеченного.</summary>
    public string Preview { get => _preview.Value; private set { _preview.Set(value); Raise(); } }

    /// <summary>Что не записалось (например, UDP без драйвера): из записанного, на языке окна.</summary>
    public string RecapNote => _last is not { } rec ? "" : string.Join(" ", rec.Notes.Select(x => L.Relocalize(x))) + (rec.Truncated ? " " + L.T("apps.debug.too_many") : "");

    /// <summary>Смена языка: таблица записанного собирается заново (причины «локальная сеть» и т. п.), строки шага — сами.</summary>
    protected override void OnLanguageChanged()
    {
        _status.Retranslate(); _detail.Retranslate(); _recText.Retranslate(); _codePreview.Retranslate(); _codeError.Retranslate();
        Raise(nameof(CodePreview)); Raise(nameof(CodeError));
        if (_variantsRaw.Count > 0)
        {
            var selected = SelectedVariant;
            _variants = LocalizeVariants(_variantsRaw);
            Raise(nameof(Variants));
            SelectedVariant = selected;
        }
        if (_last is { } rec && Rows.Count > 0)
        {
            var checkedFlows = Rows.Where(r => r.IsChecked).Select(r => r.Flow).ToHashSet();
            LoadRows(rec);
            foreach (var r in Rows) r.IsChecked = checkedFlows.Contains(r.Flow);
        }
    }
    public bool HasRecapNote => RecapNote.Length > 0;

    ProfileDraft? _advDraft;
    public bool CanSave => _advDraft is { IsEmpty: false } && ProfileTitle.Trim().Length > 0;

    /// <summary>Записанное — в таблицу. Галочки по умолчанию — как решила бы программа сама.</summary>
    public void LoadRows(RecordingResult rec)
    {
        _last = rec;
        Rows.Clear();
        // Причины «программа сама это убрала» — по-русски (так их хранит строка), на языке окна их показывает FlowRow.
        IReadOnlyDictionary<RecordedFlow, string?> sorted;
        using (L.Scope(L.Ru)) sorted = AppProfileBuilder.Sort(rec);
        foreach (var f in rec.Flows.OrderBy(f => sorted[f] is null ? 0 : 1).ThenBy(f => f.Process).ThenBy(f => f.Address))
        {
            var row = new FlowRow(f, sorted[f], sorted[f] is null);
            row.Changed += Recalc;
            Rows.Add(row);
        }
        Raise(nameof(RecapNote)); Raise(nameof(HasRecapNote));
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
        var draft = _advDraft;
        _preview.Set(() => draft.IsEmpty
            ? L.T("apps.debug.preview_empty")
            : Summarize(draft.TcpPorts, draft.UdpPorts, draft.Addresses.Count));
        Raise(nameof(Preview));
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
