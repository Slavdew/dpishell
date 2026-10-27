using System.IO;
using System.Windows.Input;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.App;

/// <summary>
/// Экран «Игры и IP-списки» (architecture.md «IP-списки и игровой режим»): игровой режим одним переключателем (решение 1), режим IP-списка
/// с «Все адреса — 10 минут» и обратным отсчётом, сведения о списке, обновление, свои подсети.
/// Окно без прав: состояние — из status помощника, все изменения — командами помощника под Guard главной модели
/// (одна операция за раз). Помощника нет — <see cref="Need"/> предложит установку и действие продолжится.
/// </summary>
public sealed class NetViewModel : Observable
{
    readonly HelperLink _helper;
    NetFeatures _f = Off;
    bool _reverting;
    bool _listsLoaded;

    static readonly NetFeatures Off = new(IpsetMode.Off, null, IpsetMode.Off, GameMode.Off, false, false, null);

    /// <summary>Guard главной модели: (текст «занято», работа) → удалось ли.</summary>
    public Func<string, Func<Task>, Task<bool>> Guard { get; set; } = async (_, w) => { await w(); return true; };
    /// <summary>Помощник для действия (с установкой по требованию). Null — не вышло, причина уже сказана.</summary>
    public Func<Task<HelperClient?>> Need { get; set; }
    public Action<string> Say { get; set; } = _ => { };
    /// <summary>Ошибка операции: если окно передало <see cref="Fail"/> — тостом, иначе как <see cref="Say"/>.</summary>
    public Action<string>? Fail { get; set; }
    void Failed(string text) => (Fail ?? Say)(text);
    public Func<string, bool> Confirm { get; set; } = _ => false;
    /// <summary>Выбор файла списка (в тестах подменяется). Null — отказались.</summary>
    public Func<string?> PickFile { get; set; } = () =>
    {
        var d = new Microsoft.Win32.OpenFileDialog { Title = L.T("apps.ipset.file.title"), Filter = L.T("apps.ipset.file.filter") };
        return d.ShowDialog() == true ? d.FileName : null;
    };
    /// <summary>Уведомление в трей: «Все адреса» закончились сами.</summary>
    public event Action<string>? Notify;
    /// <summary>Что-то поменялось в режимах — главной пересчитать состояние (и перечитать status у помощника).</summary>
    public Func<Task> AfterChange { get; set; }

    public NetViewModel(HelperLink helper)
    {
        _helper = helper;
        Need = () => Task.FromResult(helper.Client);
        AfterChange = () => helper.FetchAsync(full: false);
        UpdateListCmd = RelayCommand.Async(UpdateFromManifestAsync, () => !_busy);
        LoadFileCmd = RelayCommand.Async(UpdateFromFileAsync, () => !_busy);
        StopAnyCmd = RelayCommand.Async(StopAnyAsync, () => !_busy && AnyActive);
        TryAnyCmd = RelayCommand.Async(TryAnyAsync, () => !_busy && !AnyActive);
        SaveUserCmd = RelayCommand.Async(SaveUserAsync, () => !_busy);
        Refresh();
    }

    bool _listsOpen;
    /// <summary>Раскрыто «Дополнительно» (IP-списки, свои подсети). Раскрывается само, пока идут «Все адреса»: отсчёт должен быть виден.</summary>
    public bool ListsOpen { get => _listsOpen; set => Set(ref _listsOpen, value); }

    bool _busy;
    /// <summary>Своя операция экрана идёт (или главная занята) — кнопки не нажимаются.</summary>
    public bool IsBusy { get => _busy; set { if (Set(ref _busy, value)) { Raise(nameof(CanEdit)); Raise(nameof(GameCanToggle)); CommandManager.InvalidateRequerySuggested(); } } }
    public bool CanEdit => !_busy;

    /// <summary>Обновить экран по последнему ответу помощника (status и details); помощника нет — всё выключено.</summary>
    public void Refresh()
    {
        _f = _helper.Reader is not null ? _helper.Status?.Features ?? Off : Off;
        // Свои подсети — один раз, когда помощник их отдал: дальше это черновик пользователя до «Сохранить».
        if (!_listsLoaded && _helper.Reader is not null && _helper.Details is { } d)
        {
            _listsLoaded = true;
            UserText = string.Join(Environment.NewLine, d.UserIpset);
            ExcludeText = string.Join(Environment.NewLine, d.UserIpsetExclude);
        }
        if (AnyActive) ListsOpen = true;
        foreach (var p in new[] { nameof(IpsetOff), nameof(IpsetList), nameof(IpsetAny), nameof(AnyActive), nameof(AnyText), nameof(ListText),
                     nameof(HasList), nameof(GameOn), nameof(GameOff), nameof(GameProfiles), nameof(GameGeneral), nameof(GameCanToggle), nameof(GameStatus), nameof(GameHint),
                     nameof(GamePitfalls), nameof(UserHint) })
            Raise(p);
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Единое имя десятиминутной проверки по всем адресам (не режим наравне с «Только из списков»).</summary>
    public static string AnyName => L.T("apps.ipset.any_name");

    static string Title(IpsetMode m) => m switch { IpsetMode.List => L.T("apps.ipset.lists"), IpsetMode.Any => AnyName, _ => L.T("apps.ipset.off") };

    // ---------- режим IP-списка ----------

    public bool IpsetOff { get => _f.Ipset == IpsetMode.Off; set { if (value && !IpsetOff) _ = SetModeAsync(IpsetMode.Off); } }
    public bool IpsetList { get => _f.Ipset == IpsetMode.List; set { if (value && !IpsetList) _ = SetModeAsync(IpsetMode.List); } }
    public bool IpsetAny { get => _f.Ipset == IpsetMode.Any; set { if (value && !IpsetAny) _ = StartAnyAsync(); } }

    public bool AnyActive => _f.Ipset == IpsetMode.Any;

    /// <summary>«Все адреса: осталось 9:41 — потом сам вернётся режим «Только из списков».»</summary>
    public string AnyText
    {
        get
        {
            if (!AnyActive) return "";
            var left = (_f.AnyUntilUtc ?? DateTime.UtcNow) - DateTime.UtcNow;
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            return L.T("apps.ipset.any_text", new { time = $"{(int)left.TotalMinutes}:{left.Seconds:00}", mode = Title(_f.ReturnTo) });
        }
    }

    public ICommand StopAnyCmd { get; }
    public ICommand TryAnyCmd { get; }

    public static string TryAnyQuestion => L.T("apps.ipset.try_any_question");

    /// <summary>
    /// «Все адреса» — не третий режим наравне с рабочими, а проверка «поможет ли обход» с подтверждением (UX-аудит, п. 34):
    /// одним щелчком по переключателю ломались сайты на 10 минут.
    /// </summary>
    public async Task TryAnyAsync()
    {
        if (AnyActive || !Confirm(TryAnyQuestion)) return;
        await StartAnyAsync();
    }

    public Task SetModeAsync(IpsetMode mode)
    {
        var gameWasOn = _f.Game != GameMode.Off;
        return Run(L.T("apps.busy.apply"), c => c.SetIpsetModeAsync(mode),
            L.T(mode == IpsetMode.Off && gameWasOn ? "apps.ipset.game_off" : "common.ready"));
    }

    Task StartAnyAsync() => Run(L.T("apps.busy.apply"), c => c.StartAllAddressesAsync(), L.T("apps.ipset.any_started"));

    Task StopAnyAsync() => Run(L.T("apps.busy.revert_mode"), c => c.StopAllAddressesAsync(), L.T("common.ready"));

    /// <summary>Команда помощника под Guard; после — состояние заново (и при отказе: переключатели показывают правду).</summary>
    async Task Run(string busy, Func<HelperClient, Task> work, string done)
    {
        if (await Need() is not { } c) { Refresh(); return; }
        bool ok = await Guard(busy, () => work(c));
        await AfterChange();
        Refresh();
        if (ok) Say(done);
    }

    /// <summary>Раз в секунду из окна: обратный отсчёт и возврат, когда срок вышел.</summary>
    public void Tick()
    {
        if (!AnyActive) return;
        Raise(nameof(AnyText));
        if (_reverting || _f.AnyUntilUtc is { } until && until > DateTime.UtcNow) return;
        _ = RevertExpiredAsync();
    }

    /// <summary>Срок «Все адреса» вышел — помощник возвращает прежний режим (без вопросов: помощник уже стоит).</summary>
    public async Task RevertExpiredAsync()
    {
        if (_helper.Client is not { } c) return;
        _reverting = true;
        bool reverted = false;
        try
        {
            // Занята другая операция — Guard откажет, попробуем на следующем тике.
            await Guard(L.T("apps.busy.revert_mode"), async () => reverted = await c.StopAllAddressesAsync());
        }
        finally { _reverting = false; }
        await AfterChange();
        Refresh();
        if (!reverted) return;
        var msg = L.T("apps.ipset.any_ended", new { mode = Title(_f.Ipset) });
        Say(msg);
        Notify?.Invoke(msg);
    }

    // ---------- сведения о списке и обновление ----------

    public bool HasList => _f.List is not null;

    public string ListText => _f.List is { } m
        ? L.T("apps.ipset.list_info", new { lines = m.Lines.ToString("N0", L.Culture), date = L.Date((m.Updated ?? m.InstalledAt).ToLocalTime()), share = (m.Ipv4Share * 100).ToString("0.#", L.Culture) })
        : L.T("apps.ipset.list_missing");

    protected override void OnLanguageChanged() { Retranslate(ref _updateNote); Retranslate(ref _userMessage); }

    string _updateNote = "";
    /// <summary>Итог последнего обновления: что не вышло и когда пробовали.</summary>
    public string UpdateNote { get => _updateNote; private set { if (Set(ref _updateNote, value)) Raise(nameof(HasUpdateNote)); } }
    public bool HasUpdateNote => UpdateNote.Length > 0;

    public ICommand UpdateListCmd { get; }
    public ICommand LoadFileCmd { get; }

    /// <summary>По манифесту из поставки: сильно изменившийся список — вопрос «Применить?» и повтор с подтверждением.</summary>
    Task UpdateFromManifestAsync() => UpdateAsync(L.T("apps.busy.update_list"), (c, confirmed) => c.UpdateIpsetFromManifestAsync(confirmed, new Progress<string>(Say)));

    /// <summary>Файл читает окно (он в профиле пользователя), помощник получает содержимое и прогоняет валидатор — путей не принимает.</summary>
    Task UpdateFromFileAsync()
    {
        if (PickFile() is not { } file) return Task.CompletedTask;
        string content;
        try
        {
            if (new FileInfo(file).Length > IpsetValidator.MaxBytes) { Say(L.T("apps.ipset.file_big")); return Task.CompletedTask; }
            content = File.ReadAllText(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Failed(Texts.Friendly(e)); return Task.CompletedTask; }
        if (content.Length == 0) { Say(L.T("apps.ipset.file_empty")); return Task.CompletedTask; }
        return UpdateAsync(L.T("apps.busy.check_file"), (c, confirmed) => c.UpdateIpsetFromContentAsync(content, confirmed));
    }

    async Task UpdateAsync(string busy, Func<HelperClient, bool, Task<IpsetUpdateSummary>> update)
    {
        if (await Need() is not { } c) return;
        IpsetUpdateSummary? r = null;
        IsBusy = true;
        try
        {
            await Guard(busy, async () =>
            {
                r = await update(c, false);
                if (r.NeedsConfirm && Confirm(r.Message.EndsWith('?') ? r.Message : r.Message + L.T("apps.ipset.apply_suffix")))
                    r = await update(c, true);
            });
        }
        finally { IsBusy = false; }
        await AfterChange();
        Refresh();
        if (r is null) return;
        UpdateNote = r.Applied ? "" : r.NeedsConfirm ? L.T("apps.ipset.not_applied") : L.T("apps.ipset.last_try", new { message = r.Message, time = $"{L.Date(DateTime.Now)} {L.Time(DateTime.Now)}" });
        Say(r.Applied ? r.Message : r.NeedsConfirm ? L.T("apps.ipset.kept") : r.Message);
    }

    // ---------- свои подсети ----------

    string _userText = "", _excludeText = "";
    public string UserText { get => _userText; set => Set(ref _userText, value); }
    public string ExcludeText { get => _excludeText; set => Set(ref _excludeText, value); }

    string _userMessage = "";
    public string UserMessage { get => _userMessage; private set => Set(ref _userMessage, value); }

    public string UserHint => L.T(_f.Ipset == IpsetMode.List ? "apps.subnets.hint_active" : "apps.subnets.hint_inactive");

    public ICommand SaveUserCmd { get; }

    static List<string> Lines(string text) => text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    public async Task SaveUserAsync()
    {
        if (await Need() is not { } c) return;
        var notes = new List<string>();
        int saved = 0, excluded = 0;
        IsBusy = true;
        bool ok;
        try
        {
            ok = await Guard(L.T("apps.busy.save_subnets"), async () =>
            {
                foreach (var exclude in new[] { false, true })
                {
                    var lines = Lines(exclude ? ExcludeText : UserText);
                    var v = await c.WriteUserIpsetAsync(lines, exclude);
                    if (v.NeedsConfirm && Confirm(string.Join("\n", v.Confirm))) v = await c.WriteUserIpsetAsync(lines, exclude, confirmed: true);
                    if (!v.Accepted) { notes.Add(L.T(exclude ? "apps.subnets.exclude_failed" : "apps.subnets.user_failed", new { errors = string.Join(" ", v.Errors) })); continue; }
                    if (v.NeedsConfirm) { notes.Add(L.T(exclude ? "apps.subnets.exclude_unconfirmed" : "apps.subnets.user_unconfirmed")); continue; }
                    if (exclude) excluded = v.Entries; else saved = v.Entries;
                    if (v.Problems.Count > 0) notes.Add(L.T("apps.subnets.rejected", new { n = v.Dropped, problems = string.Join("; ", v.Problems.Take(5)) + (v.Problems.Count > 5 ? " …" : "") }));
                }
            });
        }
        finally { IsBusy = false; }
        if (!ok) return;
        // Сохранённое — в том виде, как его записал помощник (исправленные, без повторов).
        _listsLoaded = false;
        await _helper.FetchAsync(full: true);
        await AfterChange();
        Refresh();
        UserMessage = L.T("apps.subnets.saved", new { saved, excluded }) + (notes.Count > 0 ? " " + string.Join(" ", notes) : "");
        // Свои подсети действуют только в режиме «Только из списков»: не заставлять искать переключатель — предложить одной кнопкой.
        if (saved > 0 && _f.Ipset == IpsetMode.Off && Confirm(L.T("apps.subnets.enable_question")))
            await SetModeAsync(IpsetMode.List);
    }

    // ---------- игровой режим ----------

    /// <summary>Три выбора: «Выкл», «Свои профили» (работают только профили программ), «Общий» (широкий режим по IP-списку; профили тоже действуют).</summary>
    public bool GameOff { get => !_f.Game.IsGeneral() && _f.Game != GameMode.Profiles; set { if (value && !GameOff) _ = SetGameModeAsync(GameMode.Off); } }
    public bool GameProfiles { get => _f.Game == GameMode.Profiles; set { if (value && !GameProfiles) _ = SetGameModeAsync(GameMode.Profiles); } }
    public bool GameGeneral { get => _f.Game.IsGeneral(); set { if (value && !GameGeneral) _ = SetGameModeAsync(GameMode.TcpUdp); } }

    /// <summary>Включено ли что-нибудь (старое имя: раньше был один переключатель; Вкл = «Общий»).</summary>
    public bool GameOn
    {
        get => GameGeneral;
        set { if (value != GameOn) _ = SetGameModeAsync(value ? GameMode.TcpUdp : GameMode.Off); }
    }

    /// <summary>При «Все адреса» «Общий» включить нельзя (приманки во всё подряд); остальное — можно всегда.</summary>
    public bool GameCanToggle => !_busy && (GameGeneral || !AnyActive);
    public bool GameCanChoose => !_busy;

    public string GameStatus
    {
        get
        {
            var running = _helper.Status?.Enabled == true;
            if (_f.Game == GameMode.Profiles) return L.T(running ? "apps.game.status.profiles_running" : "apps.game.status.profiles_pending");
            if (!_f.Game.IsGeneral()) return L.T(AnyActive ? "apps.game.status.off_blocked" : "apps.game.status.off");
            if (_f.GameBlocked) return L.T("apps.game.status.paused");
            return _f.List is { } m
                ? L.T(running ? "apps.game.status.running_n" : "apps.game.status.pending_n", new { lines = m.Lines.ToString("N0", L.Culture) })
                : L.T(running ? "apps.game.status.running" : "apps.game.status.pending");
        }
    }

    /// <summary>Подводные камни выбранного режима: у «Общего» четыре пункта, у «Своих профилей» два; при «Выкл» — оба списка.</summary>
    public string GamePitfalls => _f.Game.IsGeneral() ? General() : _f.Game == GameMode.Profiles ? Own() : General() + "\n\n" + Own();
    static string General() => Lines(L.T("apps.game.pit.general_title"), L.T("apps.game.pit.general_1"), L.T("apps.game.pit.general_2"), L.T("apps.game.pit.general_3"), L.T("apps.game.pit.general_4"));
    static string Own() => Lines(L.T("apps.game.pit.own_title"), L.T("apps.game.pit.own_1"), L.T("apps.game.pit.own_2"), L.T("apps.game.pit.own_3"));
    static string Lines(string title, params string[] items) => title + "\n" + string.Join("\n", items.Select(i => "• " + i));

    /// <summary>«Подробнее» о игровом режиме: docs/windows/games.md (ru) или games.en.md на GitHub.</summary>
    public static string GamesUrl => MainViewModel.RepoUrl + "/blob/master/docs/windows/" + (string.Equals(L.Current, "ru", StringComparison.OrdinalIgnoreCase) ? "games.md" : "games.en.md");
    /// <summary>Открытие ссылки; в тестах подменяется (UiGuard не даёт открыть настоящую).</summary>
    public Action<string> OpenUrl { get; set; } = url => { UiGuard.CheckOpen(url); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); };
    public ICommand OpenGamesCmd => _openGamesCmd ??= new RelayCommand(() => { try { OpenUrl(GamesUrl); } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Say(e.Message); } });
    ICommand? _openGamesCmd;

    /// <summary>Предупреждение про античиты: общее для игрового режима и записи подключений.</summary>
    public static string AntiCheatNote => L.T("apps.anticheat");

    // Предупреждение про античиты — одно на вкладку, под заголовком «Игры и программы» (AppsViewModel.Anti).
    public string GameHint => L.T("apps.game.hint");

    public Task SetGameAsync(bool on) => SetGameModeAsync(on ? GameMode.TcpUdp : GameMode.Off);

    public async Task SetGameModeAsync(GameMode mode)
    {
        if (await Need() is not { } c) { Refresh(); return; }
        GameModeChange? change = null;
        IsBusy = true;
        bool ok;
        try
        {
            var busy = L.T(mode == GameMode.Off ? "apps.busy.game_off" : mode == GameMode.Profiles ? "apps.busy.game_profiles" : "apps.busy.game_on");
            ok = await Guard(busy, async () => change = await c.SetGameModeAsync(mode, new Progress<string>(Say)));
        }
        finally { IsBusy = false; }
        await AfterChange();
        Refresh();
        if (ok) Say(change?.Note ?? L.T("common.ready"));
    }
}
