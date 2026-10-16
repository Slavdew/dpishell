using System.Windows.Input;
using DpiShell.Core;

namespace DpiShell.App;

/// <summary>
/// Экран «Игры и IP-списки» (09-features-spec §1–2): игровой режим одним переключателем (решение 1), режим IP-списка
/// с «Все адреса — 10 минут» и обратным отсчётом, сведения о списке, обновление, свои подсети.
/// Всё, что трогает службу, — через Guard главной модели: одна операция за раз, «Применяю… / Готово».
/// </summary>
public sealed class NetViewModel : Observable
{
    readonly Shell _shell;
    NetFeatures _f;
    bool _reverting;

    /// <summary>Guard главной модели: (текст «занято», работа) → удалось ли.</summary>
    public Func<string, Func<Task>, Task<bool>> Guard { get; set; } = async (_, w) => { await w(); return true; };
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
    /// <summary>Что-то поменялось в режимах — главной пересчитать состояние службы.</summary>
    public Func<Task> AfterChange { get; set; } = () => Task.CompletedTask;

    public NetViewModel(Shell shell)
    {
        _shell = shell;
        _f = SafeFeatures();
        UpdateListCmd = RelayCommand.Async(UpdateFromManifestAsync, () => !_busy);
        LoadFileCmd = RelayCommand.Async(UpdateFromFileAsync, () => !_busy);
        StopAnyCmd = RelayCommand.Async(StopAnyAsync, () => !_busy && AnyActive);
        SaveUserCmd = RelayCommand.Async(SaveUserAsync, () => !_busy);
        _userText = string.Join(Environment.NewLine, SafeRead(IpLists.UserFile));
        _excludeText = string.Join(Environment.NewLine, SafeRead(IpLists.UserExcludeFile));
    }

    NetFeatures SafeFeatures()
    {
        try { return _shell.Features; }
        catch (Exception) { return new(IpsetMode.Off, null, IpsetMode.Off, GameMode.Off, false, false, null); }
    }

    IReadOnlyList<string> SafeRead(string file)
    {
        try { return _shell.IpLists.Read(file); } catch (Exception) { return []; }
    }

    bool _busy;
    /// <summary>Своя операция экрана идёт (или главная занята) — кнопки не нажимаются.</summary>
    public bool IsBusy { get => _busy; set { if (Set(ref _busy, value)) { Raise(nameof(CanEdit)); Raise(nameof(GameCanToggle)); CommandManager.InvalidateRequerySuggested(); } } }
    public bool CanEdit => !_busy;

    /// <summary>Перечитать состояние (state.json и meta списка) и обновить всё на экране.</summary>
    public void Refresh()
    {
        _f = SafeFeatures();
        foreach (var p in new[] { nameof(IpsetOff), nameof(IpsetList), nameof(IpsetAny), nameof(AnyActive), nameof(AnyText), nameof(ListText),
                     nameof(HasList), nameof(GameOn), nameof(GameCanToggle), nameof(GameStatus), nameof(GameHint), nameof(UserHint) })
            Raise(p);
        CommandManager.InvalidateRequerySuggested();
    }

    static string Title(IpsetMode m) => m switch { IpsetMode.List => "По списку", IpsetMode.Any => "Все адреса", _ => "Выкл" };

    // ---------- режим IP-списка ----------

    public bool IpsetOff { get => _f.Ipset == IpsetMode.Off; set { if (value && !IpsetOff) _ = SetModeAsync(IpsetMode.Off); } }
    public bool IpsetList { get => _f.Ipset == IpsetMode.List; set { if (value && !IpsetList) _ = SetModeAsync(IpsetMode.List); } }
    public bool IpsetAny { get => _f.Ipset == IpsetMode.Any; set { if (value && !IpsetAny) _ = StartAnyAsync(); } }

    public bool AnyActive => _f.Ipset == IpsetMode.Any;

    /// <summary>«Все адреса: осталось 9:41 — потом сам вернётся режим «По списку».»</summary>
    public string AnyText
    {
        get
        {
            if (!AnyActive) return "";
            var left = (_shell.State.IpsetAnyUntil ?? DateTime.UtcNow) - DateTime.UtcNow;
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            return $"Все адреса: осталось {(int)left.TotalMinutes}:{left.Seconds:00}. Потом сам вернётся режим «{Title(_f.ReturnTo)}».";
        }
    }

    public ICommand StopAnyCmd { get; }

    public Task SetModeAsync(IpsetMode mode) => Run("Применяю…", () => _shell.SetIpsetMode(mode),
        mode == IpsetMode.Off && _f.Game != GameMode.Off ? "Готово. Игровой режим выключен: без IP-списка он не работает." : "Готово.");

    Task StartAnyAsync() => Run("Применяю…", _shell.StartAllAddresses, "Все адреса — на 10 минут, потом режим вернётся сам.");

    Task StopAnyAsync() => Run("Возвращаю обычный режим…", () => _shell.StopAllAddresses(), "Готово.");

    /// <summary>Синхронная операция Core в фоне под Guard; после — состояние заново (и при отказе: переключатели показывают правду).</summary>
    async Task Run(string busy, Action work, string done)
    {
        bool ok = await Guard(busy, () => Task.Run(work));
        Refresh();
        if (ok) Say(done);
        await AfterChange();
    }

    /// <summary>Раз в секунду из окна: обратный отсчёт и возврат, когда срок вышел (RevertExpiredAllAddresses).</summary>
    public void Tick()
    {
        if (!AnyActive) return;
        Raise(nameof(AnyText));
        if (_reverting || _shell.State.IpsetAnyUntil is { } until && until > DateTime.UtcNow) return;
        _ = RevertExpiredAsync();
    }

    public async Task RevertExpiredAsync()
    {
        _reverting = true;
        bool reverted = false;
        try
        {
            // Занята другая операция — Guard откажет, попробуем на следующем тике.
            await Guard("Возвращаю обычный режим…", () => Task.Run(() => reverted = _shell.RevertExpiredAllAddresses()));
        }
        finally { _reverting = false; }
        Refresh();
        if (!reverted) return;
        var msg = $"10 минут «Все адреса» прошли — вернулся режим «{Title(_f.Ipset)}».";
        Say(msg);
        Notify?.Invoke(msg);
        await AfterChange();
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
    Task UpdateFromManifestAsync() => UpdateAsync("Обновляю IP-список…", confirmed => _shell.UpdateIpsetFromManifestAsync(confirmed, new Progress<string>(Say)));

    Task UpdateFromFileAsync()
    {
        if (PickFile() is not { } file) return Task.CompletedTask;
        return UpdateAsync("Проверяю список из файла…", confirmed => Task.FromResult(_shell.UpdateIpsetFromFile(file, confirmed)));
    }

    async Task UpdateAsync(string busy, Func<bool, Task<IpsetUpdateResult>> update)
    {
        IpsetUpdateResult? r = null;
        IsBusy = true;
        try
        {
            await Guard(busy, async () =>
            {
                r = await Task.Run(() => update(false));
                if (r.NeedsConfirm && Confirm(r.Message.EndsWith('?') ? r.Message : r.Message + "\n\nПрименить?"))
                    r = await Task.Run(() => update(true));
            });
        }
        finally { IsBusy = false; }
        Refresh();
        if (r is null) return;
        UpdateNote = r.Applied ? "" : r.NeedsConfirm ? "Новый список не применён — оставлен прежний." : $"{r.Message} Последняя попытка: {DateTime.Now:dd.MM.yyyy HH:mm}.";
        Say(r.Applied ? r.Message : r.NeedsConfirm ? "Оставлен прежний IP-список." : r.Message);
    }

    // ---------- свои подсети ----------

    string _userText, _excludeText;
    public string UserText { get => _userText; set => Set(ref _userText, value); }
    public string ExcludeText { get => _excludeText; set => Set(ref _excludeText, value); }

    string _userMessage = "";
    public string UserMessage { get => _userMessage; private set => Set(ref _userMessage, value); }

    public string UserHint => _f.Ipset == IpsetMode.List
        ? "Подсеть на строку, например 203.0.113.0/24 или один адрес. Действуют сразу."
        : "Подсеть на строку, например 203.0.113.0/24 или один адрес. Действуют в режиме «По списку».";

    public ICommand SaveUserCmd { get; }

    static List<string> Lines(string text) => text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    public async Task SaveUserAsync()
    {
        var notes = new List<string>();
        int saved = 0, excluded = 0;
        foreach (var exclude in new[] { false, true })
        {
            var lines = Lines(exclude ? ExcludeText : UserText);
            var v = _shell.WriteUserIpset(lines, exclude);
            if (v.NeedsConfirm && Confirm(string.Join("\n", v.Confirm))) v = _shell.WriteUserIpset(lines, exclude, confirmed: true);
            var what = exclude ? "Исключения" : "Свои подсети";
            if (!v.Accepted) { notes.Add($"{what} не сохранены: {string.Join(" ", v.Errors)}"); continue; }
            if (v.NeedsConfirm) { notes.Add($"{what} не сохранены."); continue; }
            if (exclude) { excluded = v.Entries.Count; ExcludeText = string.Join(Environment.NewLine, v.Entries); }
            else { saved = v.Entries.Count; UserText = string.Join(Environment.NewLine, v.Entries); }
            if (v.Problems.Count > 0) notes.Add($"Не приняты ({v.Dropped}): {string.Join("; ", v.Problems.Take(5))}{(v.Problems.Count > 5 ? " …" : "")}");
        }
        UserMessage = $"Сохранено: своих подсетей {saved}, исключений {excluded}." + (notes.Count > 0 ? " " + string.Join(" ", notes) : "");
        // Свои подсети действуют только «По списку»: не заставлять искать переключатель — предложить одной кнопкой.
        if (saved > 0 && _f.Ipset == IpsetMode.Off && Confirm("Свои подсети действуют только в режиме «По списку». Включить его?"))
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
            return _shell.State.Enabled ? list : list.Replace("Работает", "Включится вместе с обходом,") ;
        }
    }

    public string GameHint => "Если игра или программа после включения стала работать хуже — выключите.";

    public async Task SetGameAsync(bool on)
    {
        GameModeChange? change = null;
        IsBusy = true;
        bool ok;
        try
        {
            ok = await Guard(on ? "Включаю игровой режим…" : "Выключаю игровой режим…", async () =>
                change = await Task.Run(() => _shell.SetGameModeAsync(on ? GameMode.TcpUdp : GameMode.Off, new Progress<string>(Say))));
        }
        finally { IsBusy = false; }
        Refresh();
        if (ok) Say(change?.Note ?? "Готово.");
        await AfterChange();
    }
}
