using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using DpiShell.Core;

namespace DpiShell.App;

/// <summary>Вариант в списке «Стратегия» строки: «Авто», «Напрямую» или стратегия.</summary>
public sealed record AdvancedChoice(string Id, string Title);

/// <summary>Строка продвинутого режима как её отдали ПК (помощник) или роутер. Resolved — чем строка идёт сейчас (id стратегии).</summary>
public sealed record AdvancedRowState(IReadOnlyList<string> Domains, string Strategy, string? Resolved);

public sealed record AdvancedState(bool Enabled, string Others, IReadOnlyList<AdvancedRowState> Rows)
{
    public static readonly AdvancedState Off = new(false, AdvancedSites.OthersLists, []);
}

/// <summary>Итог сохранения: новое состояние или причина отказа (текст для карточки).</summary>
public sealed record AdvancedSaveResult(AdvancedState? State, string? Error);

/// <summary>Сайты из встроенных списков YouTube и Discord: строка продвинутого режима главнее списка — это стоит сказать.</summary>
public sealed class BuiltinLists(string listsDir)
{
    readonly Lazy<(string Name, HashSet<string> Domains)[]> _lists = new(() =>
        [("YouTube", Read(Path.Combine(listsDir, RoleLists.Google))), ("Discord", Read(Path.Combine(listsDir, RoleLists.Discord)))]);

    static HashSet<string> Read(string file)
    {
        try { return File.Exists(file) ? File.ReadLines(file).Select(l => l.Trim().ToLowerInvariant()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToHashSet() : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>«YouTube» / «Discord», если домен или его родитель есть в списке; иначе null.</summary>
    public string? Covering(string domain)
    {
        var d = domain.Trim().ToLowerInvariant();
        foreach (var (name, set) in _lists.Value)
            for (var s = d; s.Length > 0; s = s.Contains('.') ? s[(s.IndexOf('.') + 1)..] : "")
                if (set.Contains(s)) return name;
        return null;
    }
}

/// <summary>Строка в окне: сайты (по одному в строке или через запятую), стратегия, подсказка о том, чем она идёт сейчас.</summary>
public sealed class AdvancedRowVm : Observable
{
    readonly Action _edited;
    string _domains, _strategy, _hint = "";

    public AdvancedRowVm(string domains, string strategy, Action edited)
    {
        _domains = domains; _strategy = strategy; _edited = edited;
    }

    /// <summary>Что итог подбора и текущая стратегия (id) — для подсказки; сама подсказка пересчитывается владельцем.</summary>
    public string? Resolved { get; set; }

    public string DomainsText { get => _domains; set { if (Set(ref _domains, value)) _edited(); } }

    /// <summary>«auto», «direct» или id стратегии (ComboBox.SelectedValue).</summary>
    public string Strategy { get => _strategy; set { if (Set(ref _strategy, value)) _edited(); } }

    public string Hint { get => _hint; set { if (Set(ref _hint, value)) Raise(nameof(HasHint)); } }
    public bool HasHint => _hint.Length > 0;

    public static IReadOnlyList<string> Split(string text) =>
        text.Split(['\n', '\r', ',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public IReadOnlyList<string> Parsed => Split(_domains);
}

/// <summary>
/// «Продвинутый режим» на вкладке «Сайты» (docs/research/mixed-strategies.md §6): переключатель, строки «сайты → стратегия» (до 20 по 50 сайтов)
/// и выбор для остальных адресов. Один вид для ПК (помощник, <c>advanced_set</c>) и роутера (<c>advanced_set</c> договора §5.2); связь — делегатами.
/// Переключатель действует сразу, остальные правки — по «Применить»; выключен — строки хранятся и не применяются.
/// </summary>
public sealed class AdvancedViewModel : Observable
{
    public AdvancedViewModel(bool router)
    {
        IsRouter = router;
        AddRowCmd = new RelayCommand(AddRow, () => !_busy && Rows.Count < AdvancedSites.MaxRows);
        RemoveRowCmd = new RelayCommand(p => { if (p is AdvancedRowVm r) RemoveRow(r); }, _ => !_busy);
        ApplyCmd = RelayCommand.Async(() => ApplyAsync(), () => !_busy && Available && Dirty);
        DiscardCmd = new RelayCommand(Discard, () => !_busy && Dirty);
        RevertCmd = RelayCommand.Async(RevertAsync, () => !_busy && Available && CanRevert);
        Rows.CollectionChanged += (_, _) => { Raise(nameof(NoRows)); Raise(nameof(AddText)); Raise(nameof(StoredText)); Raise(nameof(ShowStored)); };
    }

    public bool NoRows => Rows.Count == 0;

    public bool IsRouter { get; }
    /// <summary>На ПК действия идут через помощника — у «Применить» щит.</summary>
    public bool ShowShield => !IsRouter;

    // ---------- связь (задаёт владелец) ----------

    /// <summary>Прочитать состояние; null — прочитать нельзя (причина в <see cref="Unavailable"/>).</summary>
    public Func<Task<AdvancedState?>> Load { get; set; } = () => Task.FromResult<AdvancedState?>(AdvancedState.Off);
    /// <summary>Сохранить (новое, прежнее). Отказ — текст в Error; сообщение «готово» ставит владелец.</summary>
    public Func<AdvancedState, AdvancedState, Task<AdvancedSaveResult>> Save { get; set; } = (s, _) => Task.FromResult(new AdvancedSaveResult(s, null));
    /// <summary>Стратегии для выбора (кроме «Авто» и «Напрямую»).</summary>
    public Func<IReadOnlyList<AdvancedChoice>> Strategies { get; set; } = () => [];
    /// <summary>Почему режим сейчас недоступен (старый роутер, старый помощник); null — доступен.</summary>
    public Func<string?> Unavailable { get; set; } = () => null;
    /// <summary>«Вернуть как было» — откат записи журнала; возвращает фразу для карточки.</summary>
    public Func<Task<string?>> Revert { get; set; } = () => Task.FromResult<string?>(null);
    public Func<bool> CanRevertNow { get; set; } = () => true;
    public BuiltinLists? Builtin { get; set; }
    /// <summary>Сайты, которые ведёт роутер при совместной работе (docs/research/mixed-strategies.md §7): строка с таким сайтом на ПК не применяется.</summary>
    public Func<IReadOnlyList<string>> RouterOwned { get; set; } = () => [];
    public Action<string> Say { get; set; } = _ => { };

    // ---------- состояние ----------

    AdvancedState _saved = AdvancedState.Off;
    bool _enabled, _loading, _busy, _dirty, _loaded;
    string _others = AdvancedSites.OthersLists;
    string _message = "";
    bool _error;

    public ObservableCollection<AdvancedRowVm> Rows { get; } = [];
    public ObservableCollection<AdvancedChoice> Choices { get; } = [];

    /// <summary>Состояние прочитано хотя бы раз.</summary>
    public bool Loaded => _loaded;

    public string? UnavailableText => Unavailable();
    public bool Available => UnavailableText is null;
    public bool NotAvailable => !Available;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            Raise(); Raise(nameof(ToggleText)); Raise(nameof(ShowEditor)); Raise(nameof(ShowStored)); Raise(nameof(StoredText));
            if (!_loading) _ = ApplyAsync(fromToggle: true);
        }
    }

    /// <summary>Подпись переключателя — состояние («Включено» / «Выключено»), как у остальных переключателей окна.</summary>
    public string ToggleText => L.T(_enabled ? "sites.adv.on" : "sites.adv.off");

    /// <summary>Редактор виден, пока режим включён; выключенный — одна строка «строк сохранено».</summary>
    public bool ShowEditor => _enabled && Available;
    public bool ShowStored => !_enabled && Available && Rows.Count > 0;
    public string StoredText => L.T("sites.adv.stored", new { n = Rows.Count });

    public bool OthersDirect { get => _others == AdvancedSites.OthersDirect; set { if (value) SetOthers(AdvancedSites.OthersDirect); } }
    public bool OthersLists { get => _others == AdvancedSites.OthersLists; set { if (value) SetOthers(AdvancedSites.OthersLists); } }
    public bool OthersAll { get => _others == AdvancedSites.OthersAll; set { if (value) SetOthers(AdvancedSites.OthersAll); } }

    void SetOthers(string v)
    {
        if (_others == v) return;
        _others = v;
        Raise(nameof(OthersDirect)); Raise(nameof(OthersLists)); Raise(nameof(OthersAll)); Raise(nameof(OthersHint));
        MarkDirty();
    }

    public string OthersHint => _others switch
    {
        AdvancedSites.OthersDirect => L.T("sites.adv.others_hint.direct"),
        AdvancedSites.OthersAll => L.T(IsRouter ? "sites.adv.others_hint.all_router" : "sites.adv.others_hint.all_pc"),
        _ => L.T("sites.adv.others_hint.lists"),
    };

    public bool Busy { get => _busy; private set { if (Set(ref _busy, value)) { Raise(nameof(CanEdit)); CommandManager.InvalidateRequerySuggested(); } } }
    public bool CanEdit => !_busy;

    public bool Dirty { get => _dirty; private set { if (Set(ref _dirty, value)) CommandManager.InvalidateRequerySuggested(); } }
    void MarkDirty() { if (!_loading) Dirty = true; }

    public string Message { get => _message; private set { if (Set(ref _message, value)) Raise(nameof(HasMessage)); } }
    public bool HasMessage => _message.Length > 0;
    /// <summary>Сообщение — отказ (красным), а не сводка.</summary>
    public bool MessageIsError { get => _error; private set => Set(ref _error, value); }

    /// <summary>Сводка сохранённого: «Строк: 3 · остальные сайты: только из списков».</summary>
    public string Summary => !_saved.Enabled ? L.T("sites.adv.summary.off")
        : L.T(_saved.Others switch
        {
            AdvancedSites.OthersDirect => "sites.adv.summary.direct", AdvancedSites.OthersAll => "sites.adv.summary.all", _ => "sites.adv.summary.lists",
        }, new { n = _saved.Rows.Count });

    public ICommand AddRowCmd { get; }
    public ICommand RemoveRowCmd { get; }
    public ICommand ApplyCmd { get; }
    public ICommand DiscardCmd { get; }
    public ICommand RevertCmd { get; }
    public bool CanRevert => CanRevertNow() && (_saved.Enabled || _saved.Rows.Count > 0);

    public string AddText => Rows.Count >= AdvancedSites.MaxRows ? L.T("sites.adv.add_max", new { n = AdvancedSites.MaxRows }) : L.T("sites.adv.add");

    // ---------- чтение ----------

    /// <summary>Перечитать состояние. Правки пользователя, которые ещё не применены, не затираются.</summary>
    public async Task RefreshAsync(bool force = false)
    {
        if (_busy || (_dirty && !force)) { RaiseAvailability(); return; }
        AdvancedState? state;
        try { state = await Load(); }
        catch (Exception e) when (e is not OutOfMemoryException) { state = null; Message = Texts.Friendly(e); MessageIsError = true; }
        RaiseAvailability();
        if (state is null) { RebuildChoices(); return; }
        Take(state);
    }

    void RaiseAvailability()
    {
        Raise(nameof(UnavailableText)); Raise(nameof(Available)); Raise(nameof(NotAvailable)); Raise(nameof(ShowEditor)); Raise(nameof(ShowStored));
    }

    /// <summary>Принять состояние как сохранённое: переключатель, «остальные», строки с подсказками.</summary>
    void Take(AdvancedState s)
    {
        _loading = true;
        try
        {
            _saved = s; _loaded = true;
            _enabled = s.Enabled; _others = AdvancedSites.IsOthers(s.Others) ? s.Others : AdvancedSites.OthersLists;
            RebuildChoices(s);
            Rows.Clear();
            foreach (var r in s.Rows) Rows.Add(new AdvancedRowVm(string.Join(Environment.NewLine, r.Domains), r.Strategy, MarkDirty) { Resolved = r.Resolved });
            RefreshHints();
            Dirty = false;
        }
        finally { _loading = false; }
        Raise(nameof(Enabled)); Raise(nameof(ToggleText)); Raise(nameof(ShowEditor)); Raise(nameof(ShowStored)); Raise(nameof(StoredText)); Raise(nameof(Summary)); Raise(nameof(AddText));
        Raise(nameof(OthersDirect)); Raise(nameof(OthersLists)); Raise(nameof(OthersAll)); Raise(nameof(OthersHint)); Raise(nameof(CanRevert));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Список «Стратегия»: Авто, Напрямую, стратегии; стратегия строки, которой уже нет, остаётся видимой.</summary>
    void RebuildChoices(AdvancedState? s = null)
    {
        List<AdvancedChoice> list = [new(AdvancedSites.Auto, L.T("sites.adv.choice.auto")), new(AdvancedSites.Direct, L.T("sites.adv.choice.direct")), .. Strategies()];
        foreach (var id in (s ?? _saved).Rows.Select(r => r.Strategy).Concat(Rows.Select(r => r.Strategy)).Distinct())
            if (list.All(c => c.Id != id)) list.Add(new(id, L.T("sites.adv.choice.missing", new { id })));
        if (Choices.Select(c => c).SequenceEqual(list)) return;
        Choices.Clear();
        foreach (var c in list) Choices.Add(c);
    }

    string TitleOf(string id) => Choices.FirstOrDefault(c => c.Id == id)?.Title ?? id;

    /// <summary>Подсказки под строками: чем идёт «Авто», что значит «Напрямую», какие сайты уже есть в списках.</summary>
    public void RefreshHints()
    {
        foreach (var r in Rows)
        {
            var parts = new List<string>();
            switch (r.Strategy)
            {
                case AdvancedSites.Auto:
                    parts.Add(r.Resolved is { } id ? L.T("sites.adv.hint.auto_now", new { strategy = TitleOf(id) }) : L.T("sites.adv.hint.auto_pending"));
                    break;
                case AdvancedSites.Direct:
                    parts.Add(L.T("sites.adv.hint.direct"));
                    break;
            }
            var routerOwned = RouterOwned();
            var theirs = routerOwned.Count == 0 ? [] : r.Parsed.Where(d => AdvancedSites.Domain(d) is { } n && JointRules.OwnedBy(n, routerOwned)).ToList();
            if (theirs.Count > 0)
                parts.Add(L.T("sites.adv.hint.router_owned", new { sites = string.Join(", ", theirs.Take(3)) }));
            else if (Builtin is { } b)
            {
                var covered = r.Parsed.Select(d => (d, list: AdvancedSites.Domain(d) is { } n ? b.Covering(n) : null)).FirstOrDefault(x => x.list is not null);
                if (covered.list is not null) parts.Add(L.T("sites.adv.hint.builtin", new { site = covered.d, list = covered.list }));
            }
            // Несколько фраз — каждая с точкой («Авто — сейчас X. youtube.com — ведёт роутер, …»).
            r.Hint = string.Join(" ", parts.Count > 1 ? parts.Select(p => p.EndsWith('.') ? p : p + ".") : parts);
        }
    }

    // ---------- правка ----------

    void AddRow()
    {
        Rows.Add(new AdvancedRowVm("", AdvancedSites.Auto, MarkDirty) { });
        RefreshHints();
        MarkDirty();
        Raise(nameof(AddText)); Raise(nameof(StoredText));
        CommandManager.InvalidateRequerySuggested();
    }

    void RemoveRow(AdvancedRowVm r)
    {
        Rows.Remove(r);
        MarkDirty();
        Raise(nameof(AddText)); Raise(nameof(StoredText));
        CommandManager.InvalidateRequerySuggested();
    }

    void Discard()
    {
        Message = ""; MessageIsError = false;
        Take(_saved);
    }

    /// <summary>Черновик → состояние для отправки. Пустые строки отбрасываются; сайты приводятся к нормальному виду; пока что-то не так — причина.</summary>
    public (AdvancedState? State, string? Problem) Draft()
    {
        var rows = new List<AdvancedRowState>();
        int n = 0;
        foreach (var r in Rows)
        {
            n++;
            var raw = r.Parsed;
            if (raw.Count == 0) continue;   // пустая строка — не ошибка, просто не сохраняется
            var (domains, problem) = AdvancedSites.Domains(raw, n - 1);
            if (problem is not null) return (null, problem);
            rows.Add(new(domains!, r.Strategy, null));
        }
        if (rows.Count > AdvancedSites.MaxRows) return (null, L.T("sites.adv.err.too_many_rows", new { max = AdvancedSites.MaxRows }));
        return (new(_enabled, _others, rows), null);
    }

    /// <summary>«Применить» (или переключатель). Переключатель при отказе возвращается на место.</summary>
    public async Task ApplyAsync(bool fromToggle = false)
    {
        if (_busy) { if (fromToggle) RevertToggle(); return; }
        var (state, problem) = Draft();
        if (state is null)
        {
            Message = problem ?? L.T("sites.adv.err.parse"); MessageIsError = true;
            if (fromToggle) RevertToggle();
            return;
        }
        Busy = true; Message = L.T(fromToggle && state.Enabled ? "sites.adv.busy.enable" : "sites.adv.busy.apply"); MessageIsError = false;
        AdvancedSaveResult res;
        try { res = await Save(state, _saved); }
        catch (Exception e) when (e is not OutOfMemoryException) { res = new(null, Texts.Friendly(e)); }
        finally { Busy = false; }
        if (res.State is null)
        {
            Message = res.Error is { Length: > 0 } err ? err : L.T("sites.adv.err.not_applied"); MessageIsError = true;
            if (fromToggle) RevertToggle();
            return;
        }
        Take(res.State);
        var done = res.State.Enabled
            ? L.T("sites.adv.done.on", new { n = res.State.Rows.Count })
            : L.T("sites.adv.done.off");
        Message = res.Error is { Length: > 0 } warn ? warn : done; MessageIsError = false;
        Say(done);
    }

    void RevertToggle()
    {
        _loading = true;
        _enabled = _saved.Enabled;
        _loading = false;
        Raise(nameof(Enabled)); Raise(nameof(ToggleText)); Raise(nameof(ShowEditor)); Raise(nameof(ShowStored));
    }

    /// <summary>Итог отката одной записи журнала — фраза для карточки.</summary>
    public static string UndoText(UndoItem? u) => u is null ? L.T("sites.adv.undo.none") : u.Status switch
    {
        UndoStatus.Restored => L.T("sites.adv.undo.restored", new { note = u.Note }),
        UndoStatus.AlreadyBack => L.T("sites.adv.undo.already"),
        UndoStatus.ChangedByOthers => L.T("sites.adv.undo.changed", new { note = u.Note }),
        _ => L.T("sites.adv.undo.failed", new { note = u.Note }),
    };

    async Task RevertAsync()
    {
        Busy = true; Message = L.T("sites.adv.busy.revert"); MessageIsError = false;
        string? text;
        try { text = await Revert(); }
        catch (Exception e) when (e is not OutOfMemoryException) { text = Texts.Friendly(e); }
        finally { Busy = false; }
        await RefreshAsync(force: true);
        Message = text ?? L.T("sites.adv.nothing_to_revert"); MessageIsError = false;
        if (text is not null) Say(text);
    }
}
