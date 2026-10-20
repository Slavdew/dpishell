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

public sealed record ChangeRow(string Title, string When);

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

    /// <summary>Перечитать оба журнала.</summary>
    public async Task RefreshAsync()
    {
        var rows = new List<(DateTime At, string Title)>();
        HelperNote = "";
        if (helper.Reader is null)
            HelperNote = helper.State == HelperState.NotInstalled
                ? L.T("settings.changes.helper_missing")
                : L.T("settings.changes.helper_lost");
        else if (!CanHelper("changes_list"))
            HelperNote = L.T("settings.changes.helper_old");
        else
        {
            try { rows.AddRange((await helper.Reader.ChangesListAsync()).Items.Select(i => (i.At, i.Title))); }
            catch (HelperUnavailableException e) { helper.MarkLost(e); HelperNote = L.T("settings.changes.helper_lost"); }
            catch (HelperException e) { HelperNote = e.Message; }
        }
        rows.AddRange(window.List().Items.Select(i => (i.At, i.Title)));
        Items.Clear();
        foreach (var (at, title) in rows.OrderByDescending(r => r.At)) Items.Add(new(title, When(at)));
        RaiseShape();
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
        await RefreshAsync();
        Say(Summary);
        return true;
    }

    public void Show(IReadOnlyList<UndoItem> items)
    {
        Report.Clear();
        foreach (var i in items.OrderByDescending(i => i.At))
            Report.Add(new(i.Title, i.Status switch
            {
                UndoStatus.Restored => L.T("settings.changes.status_restored"),
                UndoStatus.AlreadyBack => L.T("settings.changes.status_same"),
                UndoStatus.ChangedByOthers => L.T("settings.changes.status_skipped"),
                _ => L.T("settings.changes.status_failed"),
            }, i.Note, i.Status switch { UndoStatus.Restored => "ok", UndoStatus.AlreadyBack => "same", UndoStatus.ChangedByOthers => "skip", _ => "fail" }));
        int Count(UndoStatus s) => items.Count(i => i.Status == s);
        Summary = items.Count == 0 ? L.T("settings.changes.summary_none")
            : L.T("settings.changes.summary", new
            {
                restored = Count(UndoStatus.Restored), same = Count(UndoStatus.AlreadyBack),
                skipped = Count(UndoStatus.ChangedByOthers), failed = Count(UndoStatus.Failed),
            });
        Question = Count(UndoStatus.ChangedByOthers) > 0
            ? L.T("settings.changes.question")
            : "";
        RaiseShape();
    }

    /// <summary>Есть что откатить (для предложения при удалении помощника).</summary>
    public int Count => Items.Count;
}
