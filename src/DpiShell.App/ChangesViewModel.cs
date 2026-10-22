using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.App;

/// <summary>Откат записи журнала окна готовой функцией (автозапуск, роутер).</summary>
public sealed class DelegateUndoer(IReadOnlyCollection<string> kinds, Func<ChangeEntry, Task<UndoOutcome>> undo) : IChangeUndoer
{
    public IReadOnlyCollection<string> Kinds { get; } = kinds;
    public Task<UndoOutcome> UndoAsync(ChangeEntry entry, CancellationToken ct) => undo(entry);
}

/// <summary>
/// Пункт списка «Изменения DpiShell». <see cref="Action"/> — подпись кнопки отмены этого пункта («Отменить», «Отменить загрузку»); пусто — отдельно
/// отменить нельзя, причина — в <see cref="Note"/> (решение — <see cref="ChangeUndoPolicy"/>).
/// </summary>
public sealed class ChangeRow(string id, string title, string when, string kind, bool fromHelper)
{
    public string Id { get; } = id;
    public string Title { get; } = title;
    public string When { get; } = when;
    public string Kind { get; } = kind;
    /// <summary>Запись журнала помощника (иначе — окна).</summary>
    public bool FromHelper { get; } = fromHelper;
    public string Action { get; init; } = "";
    public string Note { get; init; } = "";
    public bool HasAction => Action.Length > 0;
    public bool HasNote => Note.Length > 0;
    public ICommand? UndoCmd { get; set; }
}

/// <param name="Mark">ok — возвращено, same — уже было, skip — изменено после нас, fail — не удалось.</param>
public sealed record ChangeReportRow(string Title, string Status, string Note, string Mark)
{
    public bool HasNote => Note.Length > 0;
}

/// <summary>
/// «Отменить всё, что сделала DpiShell» (Настройки): список из журналов — помощника (системные изменения) и окна (автозапуск, роутер) —
/// и откат по порядку, обратному записи, с отчётом по пунктам. Значения возвращаются, только если сейчас стоит то, что ставили мы.
/// </summary>
public sealed class ChangesViewModel(HelperLink helper, ChangeUndoEngine window) : Observable
{

    public ObservableCollection<ChangeRow> Items { get; } = [];
    public ObservableCollection<ChangeReportRow> Report { get; } = [];

    /// <summary>Вопрос «Да/Нет» (в тестах подменяется).</summary>
    public Func<string, bool> Confirm { get; set; } = _ => true;
    public Action<string> Say { get; set; } = _ => { };
    /// <summary>Одна операция за раз под строкой состояния окна (MainViewModel.Guard).</summary>
    public Func<string, Func<Task>, Task<bool>> Guard { get; set; } = async (_, work) => { await work(); return true; };

    /// <summary>Заголовок свёрнутого списка: «5 пунктов».</summary>
    public string CountText => L.Plural("settings.changes.count", Items.Count);

    bool _listOpen;
    /// <summary>Список пунктов раскрыт (по умолчанию свёрнут; «Отменить всё» видна и так).</summary>
    public bool ListOpen { get => _listOpen; set => Set(ref _listOpen, value); }

    public ChangeUndoEngine WindowEngine => window;

    bool CanHelper(string cmd) => helper.Reader is { } c && c.Compat.Supports(cmd);

    string _helperNote = "";
    /// <summary>Почему часть записей не видна (помощник не установлен, не отвечает, старый).</summary>
    public string HelperNote { get => _helperNote; private set { if (Set(ref _helperNote, value)) Raise(nameof(HasHelperNote)); } }
    public bool HasHelperNote => HelperNote.Length > 0;

    public bool HasItems => Items.Count > 0;
    public bool ShowEmpty => Items.Count == 0 && Report.Count == 0;
    public bool HasReport => Report.Count > 0;
    public string EmptyText => L.T("settings.changes.empty");

    string _summary = "";
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    string _question = "";
    /// <summary>Вопрос в отчёте: что делать с пунктами, которые изменены после DpiShell.</summary>
    public string Question { get => _question; private set { if (Set(ref _question, value)) Raise(nameof(HasQuestion)); } }
    public bool HasQuestion => Question.Length > 0;

    public ICommand UndoAllCmd => _undoAll ??= RelayCommand.Async(() => UndoAllAsync(), () => HasItems);
    ICommand? _undoAll;

    static string When(DateTime utc) => L.LongDate(utc.ToLocalTime()) + ", " + L.Time(utc.ToLocalTime());

    /// <summary>После отмены (всего или пункта): окно перечитывает то, что показывает из данных помощника.</summary>
    public Func<Task> AfterUndo { get; set; } = () => Task.CompletedTask;

    /// <summary>Перечитать оба журнала.</summary>
    public async Task RefreshAsync()
    {
        var rows = new List<(ChangeInfo Info, bool Helper)>();
        HelperNote = "";
        if (helper.Reader is null)
            HelperNote = helper.State == HelperState.NotInstalled
                ? L.T("settings.changes.helper_missing")
                : L.T("settings.changes.helper_lost");
        else if (!CanHelper("changes_list"))
            HelperNote = L.T("settings.changes.helper_old");
        else
        {
            try { rows.AddRange((await helper.Reader.ChangesListAsync()).Items.Select(i => (i, true))); }
            catch (HelperUnavailableException e) { helper.MarkLost(e); HelperNote = L.T("settings.changes.helper_lost"); }
            catch (HelperException e) { HelperNote = e.Message; }
        }
        rows.AddRange(window.List().Items.Select(i => (i, false)));
        _rows = [.. rows.OrderByDescending(r => r.Info.At)];
        FillItems();
        RaiseShape();
    }

    // Записи журнала хранятся текстом на языке записи (помощник — по-русски): на языке окна их показывает L.Relocalize.
    List<(ChangeInfo Info, bool Helper)> _rows = [];
    IReadOnlyList<UndoItem>? _lastReport;

    void FillItems()
    {
        Items.Clear();
        // Решение по каждому журналу отдельно: «более поздний того же вида» и «внутри загрузки» — в пределах одного журнала.
        var policy = ChangeUndoPolicy.Decide([.. _rows.Where(r => r.Helper).Select(r => r.Info)])
            .Concat(ChangeUndoPolicy.Decide([.. _rows.Where(r => !r.Helper).Select(r => r.Info)]))
            .ToDictionary(x => x.Key, x => x.Value);
        foreach (var (info, fromHelper) in _rows)
        {
            var (action, note) = Describe(policy.GetValueOrDefault(info.Id, SingleUndo.Unknown), fromHelper);
            var row = new ChangeRow(info.Id, L.Relocalize(info.Title), When(info.At), info.Kind, fromHelper) { Action = action, Note = note };
            row.UndoCmd = RelayCommand.Async(() => UndoOneAsync(row), () => row.HasAction);
            Items.Add(row);
        }
    }

    /// <summary>Подпись кнопки и причина, если отдельно отменить нельзя.</summary>
    (string Action, string Note) Describe(SingleUndo p, bool fromHelper)
    {
        if (fromHelper && p is SingleUndo.Yes && !CanHelper("changes_undo")) return ("", L.T("settings.changes.row.old_helper"));
        return p switch
        {
            SingleUndo.Yes => (L.T("settings.changes.row.undo"), ""),
            SingleUndo.Group when CanHelper("changes_undo_group") => (L.T("settings.changes.row.undo_import"), ""),
            SingleUndo.Group => ("", L.T("settings.changes.row.import_old_helper")),
            SingleUndo.InGroup => ("", L.T("settings.changes.row.in_import")),
            SingleUndo.NewerFirst => ("", L.T("settings.changes.row.newer_first")),
            SingleUndo.OnlyAll => ("", L.T("settings.changes.row.only_all")),
            _ => ("", L.T("settings.changes.row.unknown")),
        };
    }

    /// <summary>Помощник умеет отменять загрузку настроек отдельно (API 1.15).</summary>
    public bool CanUndoImport => CanHelper("changes_undo_group");

    /// <summary>
    /// «Отменить» у пункта: вопрос, откат одной записи (загрузка настроек — вместе со своими пунктами), отчёт. Остальные пункты не трогаются.
    /// </summary>
    public async Task<bool> UndoOneAsync(ChangeRow row)
    {
        if (!row.HasAction) return false;
        bool import = row.Kind == ChangeKinds.SettingsImport;
        if (!Confirm(L.T(import ? "settings.changes.confirm_import" : "settings.changes.confirm_one", new { title = row.Title }))) return false;
        return await RunOneAsync(row.FromHelper, row.Id, import);
    }

    /// <summary>«Отменить загрузку» из окна загрузки настроек: та же отмена по номеру записи помощника (без списка).</summary>
    public async Task<bool> UndoImportAsync(string journalId)
    {
        if (!CanUndoImport) return false;
        if (!Confirm(L.T("settings.changes.confirm_import", new { title = L.T("settings.changes.import_title") }))) return false;
        return await RunOneAsync(true, journalId, true);
    }

    async Task<bool> RunOneAsync(bool fromHelper, string id, bool group)
    {
        var all = new List<UndoItem>();
        bool ran = await Guard(L.T("settings.changes.busy"), async () =>
        {
            if (!fromHelper) { all.AddRange((await window.UndoAsync(id)).Items); return; }
            try { all.AddRange((group ? await helper.Reader!.ChangesUndoGroupAsync(id) : await helper.Reader!.ChangesUndoAsync(id)).Items); }
            catch (HelperUnavailableException e) { helper.MarkLost(e); all.Add(new("", L.T("settings.changes.helper_entries"), DateTime.UtcNow, UndoStatus.Failed, L.T("settings.changes.helper_entries_lost"))); }
            catch (HelperException e) { all.Add(new("", L.T("settings.changes.helper_entries"), DateTime.UtcNow, UndoStatus.Failed, e.Message)); }
        });
        if (!ran) return false;
        Show(all);
        await RefreshAsync();
        await AfterUndo();
        Say(Summary);
        return true;
    }

    /// <summary>Смена языка: список пунктов и отчёт собираются заново из данных.</summary>
    protected override void OnLanguageChanged()
    {
        Retranslate(ref _helperNote);
        if (Items.Count > 0) FillItems();
        if (_lastReport is { } r) Show(r);
    }

    void RaiseShape()
    {
        Raise(nameof(HasItems)); Raise(nameof(ShowEmpty)); Raise(nameof(HasReport)); Raise(nameof(CountText));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>«Отменить всё»: вопрос, откат, отчёт. <paramref name="ask"/> = false — вопрос уже задан (при удалении помощника).</summary>
    public async Task<bool> UndoAllAsync(bool ask = true)
    {
        if (ask && !Confirm(L.T("settings.changes.confirm"))) return false;
        UndoReport? mine = null;
        var all = new List<UndoItem>();
        bool ran = await Guard(L.T("settings.changes.busy"), async () =>
        {
            if (CanHelper("changes_undo_all"))
            {
                try { mine = await helper.Reader!.ChangesUndoAllAsync(); all.AddRange(mine.Items); }
                catch (HelperUnavailableException e) { helper.MarkLost(e); all.Add(new("", L.T("settings.changes.helper_entries"), DateTime.UtcNow, UndoStatus.Failed, L.T("settings.changes.helper_entries_lost"))); }
                catch (HelperException e) { all.Add(new("", L.T("settings.changes.helper_entries"), DateTime.UtcNow, UndoStatus.Failed, e.Message)); }
            }
            all.AddRange((await window.UndoAllAsync()).Items);
        });
        if (!ran) return false;
        Show(all);
        _lastAllReport = all;
        await RefreshAsync();
        await AfterUndo();
        Say(Summary);
        return true;
    }

    List<UndoItem>? _lastAllReport;
    /// <summary>Итог последнего «Отменить всё» (для удаления с ПК: всё ли отменилось).</summary>
    public IReadOnlyList<UndoItem> LastUndoAll => _lastAllReport ?? [];

    public void Show(IReadOnlyList<UndoItem> items)
    {
        _lastReport = items;
        Report.Clear();
        foreach (var i in items.OrderByDescending(i => i.At))
            Report.Add(new(L.Relocalize(i.Title), i.Status switch
            {
                UndoStatus.Restored => L.T("settings.changes.status_restored"),
                UndoStatus.AlreadyBack => L.T("settings.changes.status_same"),
                UndoStatus.ChangedByOthers => L.T("settings.changes.status_skipped"),
                _ => L.T("settings.changes.status_failed"),
            }, L.Relocalize(i.Note), i.Status switch { UndoStatus.Restored => "ok", UndoStatus.AlreadyBack => "same", UndoStatus.ChangedByOthers => "skip", _ => "fail" }));
        int Count(UndoStatus s) => items.Count(i => i.Status == s);
        Summary = items.Count == 0 ? L.T("settings.changes.summary_none")
            : L.T("settings.changes.summary", new
            {
                restored = Count(UndoStatus.Restored), same = Count(UndoStatus.AlreadyBack),
                skipped = Count(UndoStatus.ChangedByOthers), failed = Count(UndoStatus.Failed),
            });
        Question = string.Join(" ", new[]
        {
            Count(UndoStatus.ChangedByOthers) > 0 ? L.T("settings.changes.question") : "",
            Count(UndoStatus.Failed) > 0 ? L.T("settings.changes.question_failed") : "",
        }.Where(s => s.Length > 0));
        RaiseShape();
    }

    /// <summary>Есть что откатить (для предложения при удалении помощника).</summary>
    public int Count => Items.Count;
}
