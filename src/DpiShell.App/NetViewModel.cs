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
    public Func<string, bool> Confirm { get; set; } = _ => false;
    /// <summary>Выбор файла списка (в тестах подменяется). Null — отказались.</summary>
    public Func<string?> PickFile { get; set; } = () =>
    {
        var d = new Microsoft.Win32.OpenFileDialog { Title = "IP-список", Filter = "Текстовые файлы (*.txt)|*.txt|Все файлы|*.*" };
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
        foreach (var p in new[] { nameof(IpsetOff), nameof(IpsetList), nameof(IpsetAny), nameof(AnyActive), nameof(AnyText), nameof(ListText),
                     nameof(HasList), nameof(GameOn), nameof(GameCanToggle), nameof(GameStatus), nameof(GameHint), nameof(UserHint) })
            Raise(p);
        CommandManager.InvalidateRequerySuggested();
    }

    static string Title(IpsetMode m) => m switch { IpsetMode.List => "Только из списков", IpsetMode.Any => "Все адреса", _ => "Выкл" };

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
            return $"Все адреса: осталось {(int)left.TotalMinutes}:{left.Seconds:00}. Потом сам вернётся режим «{Title(_f.ReturnTo)}».";
        }
    }

    public ICommand StopAnyCmd { get; }
    public ICommand TryAnyCmd { get; }

    public const string TryAnyQuestion =
        "На 10 минут обход включится для любого соединения — так видно, поможет ли он вообще. "
        + "Многие сайты в это время могут работать с ошибками; потом режим вернётся сам. Включить?";

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
        return Run("Применяю…", c => c.SetIpsetModeAsync(mode),
            mode == IpsetMode.Off && gameWasOn ? "Готово. Игровой режим выключен: без IP-списка он не работает." : "Готово.");
    }

    Task StartAnyAsync() => Run("Применяю…", c => c.StartAllAddressesAsync(), "Все адреса — на 10 минут, потом режим вернётся сам.");

    Task StopAnyAsync() => Run("Возвращаю обычный режим…", c => c.StopAllAddressesAsync(), "Готово.");

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
            await Guard("Возвращаю обычный режим…", async () => reverted = await c.StopAllAddressesAsync());
        }
        finally { _reverting = false; }
        await AfterChange();
        Refresh();
        if (!reverted) return;
        var msg = $"10 минут «Все адреса» прошли — вернулся режим «{Title(_f.Ipset)}».";
        Say(msg);
        Notify?.Invoke(msg);
    }

    // ---------- сведения о списке и обновление ----------

    public bool HasList => _f.List is not null;

    public string ListText => _f.List is { } m
        ? $"{m.Lines:N0} подсетей, обновлён {(m.Updated ?? m.InstalledAt).ToLocalTime():dd.MM.yyyy}, покрывает {m.Ipv4Share * 100:0.#} % адресов интернета"
        : "Список не загружен. «Обновить список» скачает его.";

    string _updateNote = "";
    /// <summary>Итог последнего обновления: что не вышло и когда пробовали.</summary>
    public string UpdateNote { get => _updateNote; private set { if (Set(ref _updateNote, value)) Raise(nameof(HasUpdateNote)); } }
    public bool HasUpdateNote => UpdateNote.Length > 0;

    public ICommand UpdateListCmd { get; }
    public ICommand LoadFileCmd { get; }

    /// <summary>По манифесту из поставки: сильно изменившийся список — вопрос «Применить?» и повтор с подтверждением.</summary>
    Task UpdateFromManifestAsync() => UpdateAsync("Обновляю IP-список…", (c, confirmed) => c.UpdateIpsetFromManifestAsync(confirmed, new Progress<string>(Say)));

    /// <summary>Файл читает окно (он в профиле пользователя), помощник получает содержимое и прогоняет валидатор — путей не принимает.</summary>
    Task UpdateFromFileAsync()
    {
        if (PickFile() is not { } file) return Task.CompletedTask;
        string content;
        try
        {
            if (new FileInfo(file).Length > IpsetValidator.MaxBytes) { Say("Файл слишком большой для IP-списка."); return Task.CompletedTask; }
            content = File.ReadAllText(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Say(Texts.Friendly(e)); return Task.CompletedTask; }
        if (content.Length == 0) { Say("Файл пустой."); return Task.CompletedTask; }
        return UpdateAsync("Проверяю список из файла…", (c, confirmed) => c.UpdateIpsetFromContentAsync(content, confirmed));
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
                if (r.NeedsConfirm && Confirm(r.Message.EndsWith('?') ? r.Message : r.Message + "\n\nПрименить?"))
                    r = await update(c, true);
            });
        }
        finally { IsBusy = false; }
        await AfterChange();
        Refresh();
        if (r is null) return;
        UpdateNote = r.Applied ? "" : r.NeedsConfirm ? "Новый список не применён — оставлен прежний." : $"{r.Message} Последняя попытка: {DateTime.Now:dd.MM.yyyy HH:mm}.";
        Say(r.Applied ? r.Message : r.NeedsConfirm ? "Оставлен прежний IP-список." : r.Message);
    }

    // ---------- свои подсети ----------

    string _userText = "", _excludeText = "";
    public string UserText { get => _userText; set => Set(ref _userText, value); }
    public string ExcludeText { get => _excludeText; set => Set(ref _excludeText, value); }

    string _userMessage = "";
    public string UserMessage { get => _userMessage; private set => Set(ref _userMessage, value); }

    public string UserHint => _f.Ipset == IpsetMode.List
        ? "Подсеть на строку, например 203.0.113.0/24 или один адрес. Действуют сразу после «Сохранить»."
        : "Подсеть на строку, например 203.0.113.0/24 или один адрес. Действуют в режиме «Только из списков».";

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
            ok = await Guard("Сохраняю подсети…", async () =>
            {
                foreach (var exclude in new[] { false, true })
                {
                    var lines = Lines(exclude ? ExcludeText : UserText);
                    var v = await c.WriteUserIpsetAsync(lines, exclude);
                    if (v.NeedsConfirm && Confirm(string.Join("\n", v.Confirm))) v = await c.WriteUserIpsetAsync(lines, exclude, confirmed: true);
                    var what = exclude ? "Исключения" : "Свои подсети";
                    if (!v.Accepted) { notes.Add($"{what} не сохранены: {string.Join(" ", v.Errors)}"); continue; }
                    if (v.NeedsConfirm) { notes.Add($"{what} не сохранены."); continue; }
                    if (exclude) excluded = v.Entries; else saved = v.Entries;
                    if (v.Problems.Count > 0) notes.Add($"Не приняты ({v.Dropped}): {string.Join("; ", v.Problems.Take(5))}{(v.Problems.Count > 5 ? " …" : "")}");
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
        UserMessage = $"Сохранено: своих подсетей {saved}, исключений {excluded}." + (notes.Count > 0 ? " " + string.Join(" ", notes) : "");
        // Свои подсети действуют только в режиме «Только из списков»: не заставлять искать переключатель — предложить одной кнопкой.
        if (saved > 0 && _f.Ipset == IpsetMode.Off && Confirm("Свои подсети действуют только в режиме «Только из списков». Включить его?"))
            await SetModeAsync(IpsetMode.List);
    }

    // ---------- игровой режим ----------

    /// <summary>Один переключатель (решение 1): Вкл = TCP и UDP, порты не показываем.</summary>
    public bool GameOn
    {
        get => _f.Game != GameMode.Off;
        set { if (value != GameOn) _ = SetGameAsync(value); }
    }

    /// <summary>При «Все адреса» включить нельзя (приманки во всё подряд); выключить — можно всегда.</summary>
    public bool GameCanToggle => !_busy && (GameOn || !AnyActive);

    public string GameStatus
    {
        get
        {
            if (_f.Game == GameMode.Off) return AnyActive ? "Выключен. Недоступен, пока включены «Все адреса»." : "Выключен";
            if (_f.GameBlocked) return "Не действует, пока включены «Все адреса» — вернётся сам вместе с обычным режимом.";
            var list = _f.List is { } m ? $"Работает по {m.Lines:N0} подсетям из IP-списка (обновлён {(m.Updated ?? m.InstalledAt).ToLocalTime():dd.MM.yyyy})" : "Работает по IP-списку";
            return _helper.Status?.Enabled == true ? list : list.Replace("Работает", "Включится вместе с обходом,");
        }
    }

    public string GameHint => "Если игра или программа после включения стала работать хуже — выключите.";

    public async Task SetGameAsync(bool on)
    {
        if (await Need() is not { } c) { Refresh(); return; }
        GameModeChange? change = null;
        IsBusy = true;
        bool ok;
        try
        {
            ok = await Guard(on ? "Включаю игровой режим…" : "Выключаю игровой режим…", async () =>
                change = await c.SetGameModeAsync(on ? GameMode.TcpUdp : GameMode.Off, new Progress<string>(Say)));
        }
        finally { IsBusy = false; }
        await AfterChange();
        Refresh();
        if (ok) Say(change?.Note ?? "Готово.");
    }
}
