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

/// <param name="Mark">ok — вернул, same — уже было, skip — изменено после нас, fail — не смог.</param>
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
    static readonly CultureInfo Ru = new("ru-RU");

    public ObservableCollection<ChangeRow> Items { get; } = [];
    public ObservableCollection<ChangeReportRow> Report { get; } = [];

    /// <summary>Вопрос «Да/Нет» (в тестах подменяется).</summary>
    public Func<string, bool> Confirm { get; set; } = _ => true;
    public Action<string> Say { get; set; } = _ => { };
    /// <summary>Одна операция за раз под строкой состояния окна (MainViewModel.Guard).</summary>
    public Func<string, Func<Task>, Task<bool>> Guard { get; set; } = async (_, work) => { await work(); return true; };

    public const string Promise = "Вернутся значения, которые были до DpiShell; ваши более поздние правки не трогаем.";

    public ChangeUndoEngine WindowEngine => window;

    bool CanHelper(string cmd) => helper.Reader is { } c && c.Compat.Supports(cmd);

    string _helperNote = "";
    /// <summary>Почему часть записей не видна (помощник не установлен, не отвечает, старый).</summary>
    public string HelperNote { get => _helperNote; private set { if (Set(ref _helperNote, value)) Raise(nameof(HasHelperNote)); } }
    public bool HasHelperNote => HelperNote.Length > 0;

    public bool HasItems => Items.Count > 0;
    public bool ShowEmpty => Items.Count == 0 && Report.Count == 0;
    public bool HasReport => Report.Count > 0;
    public string EmptyText => "Пока ничего: DpiShell ещё не меняла настройки этого компьютера.";

    string _summary = "";
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    string _question = "";
    /// <summary>Вопрос в отчёте: что делать с пунктами, которые изменены после DpiShell.</summary>
    public string Question { get => _question; private set { if (Set(ref _question, value)) Raise(nameof(HasQuestion)); } }
    public bool HasQuestion => Question.Length > 0;

    public ICommand UndoAllCmd => _undoAll ??= RelayCommand.Async(() => UndoAllAsync(), () => HasItems);
    ICommand? _undoAll;

    static string When(DateTime utc) => utc.ToLocalTime().ToString("d MMMM yyyy, HH:mm", Ru);

    /// <summary>Перечитать оба журнала.</summary>
    public async Task RefreshAsync()
    {
        var rows = new List<(DateTime At, string Title)>();
        HelperNote = "";
        if (helper.Reader is null)
            HelperNote = helper.State == HelperState.NotInstalled
                ? "Помощник не установлен — записи о системных настройках он хранит сам."
                : "Помощник не отвечает — его записи сейчас не видны.";
        else if (!CanHelper("changes_list"))
            HelperNote = "Помощник старее окна и не ведёт журнал — обновите его на вкладке «Информация и обновления».";
        else
        {
            try { rows.AddRange((await helper.Reader.ChangesListAsync()).Items.Select(i => (i.At, i.Title))); }
            catch (HelperUnavailableException e) { helper.MarkLost(e); HelperNote = "Помощник не отвечает — его записи сейчас не видны."; }
            catch (HelperException e) { HelperNote = e.Message; }
        }
        rows.AddRange(window.List().Items.Select(i => (i.At, i.Title)));
        Items.Clear();
        foreach (var (at, title) in rows.OrderByDescending(r => r.At)) Items.Add(new(title, When(at)));
        RaiseShape();
    }

    void RaiseShape()
    {
        Raise(nameof(HasItems)); Raise(nameof(ShowEmpty)); Raise(nameof(HasReport));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>«Отменить всё»: вопрос, откат, отчёт. <paramref name="ask"/> = false — вопрос уже задан (при удалении помощника).</summary>
    public async Task<bool> UndoAllAsync(bool ask = true)
    {
        if (ask && !Confirm("Отменить всё, что сделала DpiShell на этом компьютере?\n\n" + Promise)) return false;
        UndoReport? mine = null;
        var all = new List<UndoItem>();
        bool ran = await Guard("Отменяю изменения…", async () =>
        {
            if (CanHelper("changes_undo_all"))
            {
                try { mine = await helper.Reader!.ChangesUndoAllAsync(); all.AddRange(mine.Items); }
                catch (HelperUnavailableException e) { helper.MarkLost(e); all.Add(new("", "Записи помощника", DateTime.UtcNow, UndoStatus.Failed, "помощник не отвечает")); }
                catch (HelperException e) { all.Add(new("", "Записи помощника", DateTime.UtcNow, UndoStatus.Failed, e.Message)); }
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
                UndoStatus.Restored => "Вернул",
                UndoStatus.AlreadyBack => "Уже было как раньше",
                UndoStatus.ChangedByOthers => "Не тронул: изменено после DpiShell",
                _ => "Не смог",
            }, i.Note, i.Status switch { UndoStatus.Restored => "ok", UndoStatus.AlreadyBack => "same", UndoStatus.ChangedByOthers => "skip", _ => "fail" }));
        int Count(UndoStatus s) => items.Count(i => i.Status == s);
        Summary = items.Count == 0 ? "Отменять было нечего."
            : $"Вернул: {Count(UndoStatus.Restored)} · уже было как раньше: {Count(UndoStatus.AlreadyBack)} · не тронул: {Count(UndoStatus.ChangedByOthers)} · не смог: {Count(UndoStatus.Failed)}";
        Question = Count(UndoStatus.ChangedByOthers) > 0
            ? "Пункты «не тронул» вы или другая программа изменили после DpiShell. Если их тоже нужно вернуть, сделайте это вручную: что из этого важнее, решаете вы."
            : "";
        RaiseShape();
    }

    /// <summary>Есть что откатить (для предложения при удалении помощника).</summary>
    public int Count => Items.Count;
}
