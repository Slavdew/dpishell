using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.App;

public enum ProxyTarget { User, UserAdmin, Machine }

/// <summary>Строка списка своих прокси.</summary>
public sealed class ProxyRow(ProxyEntry entry) : Observable
{
    public ProxyEntry Entry { get; } = entry;
    public string Name => Entry.Name;
    public string Address => Entry.Server;
    public string Detail => string.Join(" · ", new[]
    {
        Entry.Bypass is { } b ? "исключения: " + b : null,
        Entry.Pac is { } p ? "автонастройка: " + p : null,
    }.Where(x => x is not null));
    public bool HasDetail => Detail.Length > 0;
}

/// <summary>
/// Вкладка «Прокси» (docs/windows/README.md, architecture.md «Системный прокси»): что стоит сейчас, свои прокси, «Включить» / «Очистить» /
/// «Вернуть прежний» тремя путями — для этого пользователя (окно само, права не нужны), для него же с правами администратора и для всей
/// машины (команды помощника), проверка «работает / не отвечает». Изменения пишутся в журнал (откат всего): пользователь — журнал окна,
/// машина — журнал помощника.
/// </summary>
public sealed class ProxyViewModel(HelperLink helper, WindowSettings settings) : Observable
{
    /// <summary>Реестр, WinHTTP и уведомление системы. По умолчанию — память (тесты настоящее не трогают); App ставит настоящие.</summary>
    public IProxyRegistry Registry { get; set; } = new MemoryProxyRegistry();
    public IWinHttpProxy WinHttp { get; set; } = new MemoryWinHttpProxy();
    public IProxyNotifier Notifier { get; set; } = new NullProxyNotifier();
    /// <summary>Журнал окна: записи «для этого пользователя».</summary>
    public IChangeRecorder Recorder { get; set; } = NullRecorder.Instance;

    public Func<Task<HelperClient?>> Need { get; set; } = () => Task.FromResult<HelperClient?>(null);
    public Func<string, Func<Task>, Task<bool>> Guard { get; set; } = async (_, work) => { await work(); return true; };
    public Func<bool> IsBusy { get; set; } = () => false;
    public Action<string> Say { get; set; } = _ => { };
    /// <summary>Проверка прокси; в тестах — подмена без сети.</summary>
    public Func<string, CancellationToken, Task<ProxyCheckResult>> CheckProxy { get; set; } = (s, ct) => ProxyChecker.CheckAsync(s, ct: ct);

    ProxyOverview? _now;
    ProxyBackup? _machinePrevious;

    public ObservableCollection<ProxyRow> Items { get; } = [];
    public ObservableCollection<string> Notes { get; } = [];
    public IReadOnlyList<string> Hints => ProxyHints.Short;
    public IReadOnlyList<string> HintsMore => ProxyHints.Details;
    /// <summary>Пример формата для пустого списка — только подсказка, в список не добавляется.</summary>
    public const string Example = "Clash Verge — 127.0.0.1:7897";

    ProxyManager Manager() => new(Registry, WinHttp) { Recorder = Recorder, Notifier = Notifier };

    /// <summary>Откат записи журнала окна (журнал пользователя).</summary>
    public async Task<UndoOutcome> UndoAsync(ChangeEntry e)
    {
        var o = await new ProxyUndoer(Registry, WinHttp, Notifier).UndoAsync(e, CancellationToken.None);
        await RefreshAsync();
        return o;
    }

    // ---------- что стоит сейчас ----------

    string _userLine = "", _machineLine = "", _winHttpLine = "";
    public string UserLine { get => _userLine; private set => Set(ref _userLine, value); }
    public string MachineLine { get => _machineLine; private set => Set(ref _machineLine, value); }
    public string WinHttpLine { get => _winHttpLine; private set => Set(ref _winHttpLine, value); }
    public bool HasNotes => Notes.Count > 0;
    public bool ListEmpty => Items.Count == 0;

    static string Line(ProxySettings s, bool autoDetect)
    {
        var parts = new List<string>();
        if (s.Enabled && s.Server is not null) parts.Add(s.Server);
        if (s.Pac is not null) parts.Add("автонастройка " + s.Pac);
        if (autoDetect) parts.Add("автоматически определять настройки");
        if (parts.Count == 0) return "выключен";
        if (s.Enabled && s.Server is not null && s.Bypass is not null) parts.Add("исключения: " + s.Bypass);
        return string.Join(" · ", parts);
    }

    public async Task RefreshAsync()
    {
        ProxyOverview now;
        try { now = await Task.Run(() => Manager().Overview()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            UserLine = "Не удалось прочитать настройки: " + Texts.Friendly(e);
            return;
        }
        if (helper.Reader is { } c && c.Compat.Supports("proxy_status"))
        {
            try { _machinePrevious = (await c.ProxyStatusAsync()).Previous; }
            catch (HelperUnavailableException e) { helper.MarkLost(e); }
            catch (HelperException) { }
        }
        Show(now);
    }

    /// <summary>Показать уже прочитанное состояние (ответ помощника или чтение реестра).</summary>
    public void Show(ProxyOverview now)
    {
        _now = now;
        UserLine = Line(now.User, now.UserAutoDetect);
        MachineLine = Line(now.Machine, now.MachineAutoDetect);
        WinHttpLine = now.WinHttp.IsDirect ? "напрямую" : now.WinHttp.Proxy + (now.WinHttp.Bypass is { } b ? " · исключения: " + b : "");
        Notes.Clear();
        if (now.MachineWide)
            Notes.Add("Настройки общие для всех пользователей (политика): Windows читает настройки машины, а пользовательские не использует.");
        if (now.PolicyBlocked)
            Notes.Add("Изменение прокси закреплено политикой (обычно администратором организации): наши правки могут не примениться или вернуться.");
        Raise(nameof(HasNotes)); Raise(nameof(CanRestore)); Raise(nameof(PreviousText)); Raise(nameof(HasPrevious));
        CommandManager.InvalidateRequerySuggested();
    }

    // ---------- куда применять ----------

    ProxyTarget _target = ProxyTarget.User;
    public ProxyTarget Target
    {
        get => _target;
        set
        {
            if (!Set(ref _target, value)) return;
            Raise(nameof(TargetUser)); Raise(nameof(TargetUserAdmin)); Raise(nameof(TargetMachine));
            Raise(nameof(CanRestore)); Raise(nameof(PreviousText)); Raise(nameof(HasPrevious));
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public bool TargetUser { get => Target == ProxyTarget.User; set { if (value) Target = ProxyTarget.User; } }
    public bool TargetUserAdmin { get => Target == ProxyTarget.UserAdmin; set { if (value) Target = ProxyTarget.UserAdmin; } }
    public bool TargetMachine { get => Target == ProxyTarget.Machine; set { if (value) Target = ProxyTarget.Machine; } }

    bool _alsoWinHttp;
    /// <summary>Для всей машины: ещё и прокси WinHTTP (для служб и программ, которые не берут системный).</summary>
    public bool AlsoWinHttp { get => _alsoWinHttp; set => Set(ref _alsoWinHttp, value); }

    public bool CanRestore => Previous is not null;
    public bool HasPrevious => Previous is not null;
    ProxySettings? Previous => Target == ProxyTarget.Machine ? _machinePrevious?.Machine : settings.ProxyPrevious;
    public string PreviousText => Previous is { } p ? "Прежний: " + Line(p, false) : "";

    // ---------- включить / очистить / вернуть ----------

    public ICommand EnableCmd => _enable ??= RelayCommand.Async(() => EnableAsync(Selected), () => Selected is not null && !IsBusy());
    ICommand? _enable;
    public ICommand ClearCmd => _clear ??= RelayCommand.Async(ClearAsync, () => !IsBusy());
    ICommand? _clear;
    public ICommand RestoreCmd => _restore ??= RelayCommand.Async(RestoreAsync, () => CanRestore && !IsBusy());
    ICommand? _restore;
    public ICommand RefreshCmd => _refresh ??= RelayCommand.Async(RefreshAsync);
    ICommand? _refresh;

    static string ShieldLessNote(ProxyOverview? now, ProxyTarget t) =>
        t != ProxyTarget.Machine && now is { MachineWide: true }
            ? " Сейчас настройки общие для всех (политика): для одного пользователя они не подействуют — выберите «Для всей машины»."
            : "";

    public async Task EnableAsync(ProxyRow? row)
    {
        if (row is null) return;
        var e = row.Entry;
        if (ProxyValues.Problem(e.Server, e.Bypass, e.Pac) is { } bad) { Say(bad); return; }
        var next = ProxyValues.Build(e.Server, e.Bypass, e.Pac);
        await Run("Включаю прокси…", async () =>
        {
            var r = await ApplyAsync(next, enable: true, e);
            Say(r.Message + (r.Ok ? ShieldLessNote(_now, Target) : ""));
        });
    }

    public async Task ClearAsync() => await Run("Выключаю прокси…", async () =>
    {
        var cur = ProxyRegistryIo.Read(Registry, ProxyKey.UserSettings);
        var r = await ApplyAsync(cur.Cleared(), enable: false, null);
        // «Прежний» запоминаем только для пользователя: у машины его хранит помощник.
        if (r.Ok && r.Changed && Target != ProxyTarget.Machine && cur.IsOn) Remember(cur);
        Say(r.Message);
    });

    public async Task RestoreAsync() => await Run("Возвращаю прежний прокси…", async () =>
    {
        if (Target == ProxyTarget.Machine)
        {
            if (await Need() is not { } c) return;
            if (!c.Compat.Supports("proxy_machine_restore")) { Say(OldHelper); return; }
            var r = await c.ProxyMachineRestoreAsync();
            _machinePrevious = r.Status.Previous;
            Notifier.Changed();
            Show(r.Status.Now);
            Say(r.Message);
            return;
        }
        if (settings.ProxyPrevious is not { } prev) return;
        var o = await ApplyAsync(prev, enable: true, null, exact: prev);
        if (o.Ok) Remember(null);
        Say(o.Message);
    });

    const string OldHelper = "Помощник старее окна и не умеет менять прокси — обновите его на вкладке «Информация и обновления».";

    void Remember(ProxySettings? s)
    {
        settings.ProxyPrevious = s;
        try { settings.Save(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        Raise(nameof(CanRestore)); Raise(nameof(PreviousText)); Raise(nameof(HasPrevious));
        CommandManager.InvalidateRequerySuggested();
    }

    async Task<ProxyOutcome> ApplyAsync(ProxySettings next, bool enable, ProxyEntry? entry, ProxySettings? exact = null)
    {
        try
        {
            switch (Target)
            {
                case ProxyTarget.User:
                {
                    var o = await Task.Run(() => Manager().ApplyUser(null, next, ChangeKinds.ProxyUser));
                    await RefreshAsync();
                    return o;
                }
                case ProxyTarget.UserAdmin:
                {
                    if (await Need() is not { } c) return new(false, "Нужен помощник — без него изменить прокси с правами администратора нельзя.");
                    if (!c.Compat.Supports("proxy_user_set")) return new(false, OldHelper);
                    var r = enable
                        ? await c.ProxyUserSetAsync(true, exact is not null ? (exact.Enabled ? exact.Server : null) : entry!.Server, exact is not null ? exact.Bypass : entry!.Bypass,
                            exact is not null ? exact.Pac : entry!.Pac)
                        : await c.ProxyUserSetAsync(false);
                    Notifier.Changed();   // окно в том же сеансе: пусть WinINET перечитает
                    _machinePrevious = r.Status.Previous;
                    Show(r.Status.Now);
                    return new(r.Ok, r.Message, r.Ok);
                }
                default:
                {
                    if (await Need() is not { } c) return new(false, "Нужен помощник — без него изменить прокси всей машины нельзя.");
                    if (!c.Compat.Supports("proxy_machine_set")) return new(false, OldHelper);
                    var r = enable
                        ? await c.ProxyMachineSetAsync(true, entry!.Server, entry.Bypass, entry.Pac, AlsoWinHttp)
                        : await c.ProxyMachineSetAsync(false, winhttp: AlsoWinHttp);
                    Notifier.Changed();
                    _machinePrevious = r.Status.Previous;
                    Show(r.Status.Now);
                    return new(r.Ok, r.Message, r.Ok);
                }
            }
        }
        catch (HelperUnavailableException e) { helper.MarkLost(e); return new(false, "Помощник не отвечает."); }
        catch (HelperException e) { return new(false, e.Message); }
    }

    async Task Run(string busyText, Func<Task> work)
    {
        if (IsBusy()) return;
        await Guard(busyText, work);
    }

    // ---------- проверка ----------

    bool _checking;
    public bool Checking { get => _checking; private set { if (Set(ref _checking, value)) CommandManager.InvalidateRequerySuggested(); } }
    string _checkText = "";
    public string CheckText { get => _checkText; private set { if (Set(ref _checkText, value)) Raise(nameof(HasCheck)); } }
    public bool HasCheck => CheckText.Length > 0;

    public ICommand CheckCmd => _check ??= RelayCommand.Async(CheckAsync, () => Selected is not null && !Checking);
    ICommand? _check;

    public async Task CheckAsync()
    {
        if (Selected is not { } row) return;
        Checking = true;
        CheckText = "Проверяю…";
        try
        {
            var r = await CheckProxy(row.Entry.Server, CancellationToken.None);
            CheckText = $"{row.Name}: {r.Summary}" + (r.Probes.Count > 0 && !r.Ok ? "\n" + string.Join("\n", r.Probes.Select(p => $"{p.Target} — {(p.Ok ? $"{p.Ms} мс" : p.Error)}")) : "");
        }
        finally { Checking = false; }
    }

    // ---------- свои прокси ----------

    public void LoadList()
    {
        Items.Clear();
        foreach (var e in settings.ProxyList) Items.Add(new ProxyRow(e));
        RaiseList();
    }

    void RaiseList() { Raise(nameof(ListEmpty)); CommandManager.InvalidateRequerySuggested(); }

    ProxyRow? _selected;
    public ProxyRow? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            if (value is not null) { EditName = value.Entry.Name; EditServer = value.Entry.Server; EditBypass = value.Entry.Bypass ?? ""; EditPac = value.Entry.Pac ?? ""; }
            FormError = "";
            Raise(nameof(IsSelected));
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public bool IsSelected => Selected is not null;

    string _editName = "", _editServer = "", _editBypass = "", _editPac = "", _formError = "";
    public string EditName { get => _editName; set => Set(ref _editName, value); }
    public string EditServer { get => _editServer; set => Set(ref _editServer, value); }
    public string EditBypass { get => _editBypass; set => Set(ref _editBypass, value); }
    public string EditPac { get => _editPac; set => Set(ref _editPac, value); }
    public string FormError { get => _formError; private set { if (Set(ref _formError, value)) Raise(nameof(HasFormError)); } }
    public bool HasFormError => FormError.Length > 0;

    public ICommand AddCmd => _add ??= new RelayCommand(() => SaveForm(replace: false));
    ICommand? _add;
    public ICommand SaveCmd => _save ??= new RelayCommand(() => SaveForm(replace: true), () => Selected is not null);
    ICommand? _save;
    public ICommand DeleteCmd => _delete ??= new RelayCommand(Delete, () => Selected is not null);
    ICommand? _delete;
    public ICommand ClearFormCmd => _clearForm ??= new RelayCommand(() => { Selected = null; EditName = EditServer = EditBypass = EditPac = ""; FormError = ""; });
    ICommand? _clearForm;

    /// <summary>Проверить форму и вернуть запись (нормальная запись значений) или текст ошибки.</summary>
    ProxyEntry? ReadForm(ProxyRow? replacing)
    {
        var name = EditName.Trim();
        if (name.Length == 0 || name.Length > 40 || name.Any(char.IsControl)) { FormError = "Название — от 1 до 40 знаков."; return null; }
        if (Items.Any(i => i != replacing && string.Equals(i.Name, name, StringComparison.CurrentCultureIgnoreCase))) { FormError = "Такое название уже есть."; return null; }
        if (string.IsNullOrWhiteSpace(EditServer)) { FormError = "Укажите адрес прокси, например 127.0.0.1:7897."; return null; }
        if (ProxyValues.Problem(EditServer, EditBypass, EditPac) is { } bad) { FormError = bad; return null; }
        return new ProxyEntry(name, ProxyValues.NormalizeServer(EditServer)!, ProxyValues.NormalizeBypass(EditBypass).Value, ProxyValues.NormalizePac(EditPac).Value);
    }

    void SaveForm(bool replace)
    {
        var target = replace ? Selected : null;
        if (ReadForm(target) is not { } entry) return;
        FormError = "";
        var row = new ProxyRow(entry);
        if (target is not null) Items[Items.IndexOf(target)] = row; else Items.Add(row);
        PersistList();
        Selected = row;
        Say(target is not null ? "Прокси изменён в списке." : "Прокси добавлен в список.");
    }

    void Delete()
    {
        if (Selected is not { } row) return;
        Items.Remove(row);
        Selected = null;
        EditName = EditServer = EditBypass = EditPac = "";
        PersistList();
        Say("Прокси убран из списка.");
    }

    void PersistList()
    {
        settings.ProxyList = [.. Items.Select(i => i.Entry)];
        try { settings.Save(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Say("Не удалось сохранить список: " + e.Message); }
        RaiseList();
    }
}
