using DpiShell.Core.Localization;
namespace DpiShell.Core;

public enum UndoStatus
{
    /// <summary>Вернули прежнее значение.</summary>
    Restored,
    /// <summary>Уже стоит как раньше — делать нечего.</summary>
    AlreadyBack,
    /// <summary>Сейчас стоит не то, что ставили мы (кто-то изменил после нас) — не трогаем, спрашиваем в отчёте.</summary>
    ChangedByOthers,
    /// <summary>Не смогли; причина в тексте. Запись остаётся — можно повторить.</summary>
    Failed,
}

public sealed record UndoOutcome(UndoStatus Status, string Note)
{
    public static UndoOutcome Restored(string note = "") => new(UndoStatus.Restored, note);
    public static UndoOutcome Already(string note = "") => new(UndoStatus.AlreadyBack, note);
    public static UndoOutcome Changed(string note) => new(UndoStatus.ChangedByOthers, note);
    public static UndoOutcome Fail(string note) => new(UndoStatus.Failed, note);
}

/// <summary>
/// Откат одного вида изменения. Возвращает значение, только если сейчас стоит то, что ставили мы (<see cref="ChangeEntry.After"/>);
/// иначе <see cref="UndoStatus.ChangedByOthers"/>. Если у функции уже есть свой «Вернуть как было» — обработчик вызывает его, а не пишет заново.
/// Новый вид изменения = константа в <see cref="ChangeKinds"/> + обработчик + <see cref="ChangeUndoEngine.Register"/>.
/// </summary>
public interface IChangeUndoer
{
    IReadOnlyCollection<string> Kinds { get; }
    Task<UndoOutcome> UndoAsync(ChangeEntry entry, CancellationToken ct);
}

/// <summary>Строка списка «Что сделала DpiShell». <paramref name="Group"/> — запись сделана внутри другого действия (загрузка настроек, API 1.15): id его записи.</summary>
public sealed record ChangeInfo(string Id, DateTime At, string Kind, string Title, bool Undoable, string? Group = null);

public sealed record ChangesList(IReadOnlyList<ChangeInfo> Items);

public sealed record UndoItem(string Id, string Title, DateTime At, UndoStatus Status, string Note);

public sealed record UndoReport(IReadOnlyList<UndoItem> Items)
{
    public int Count(UndoStatus s) => Items.Count(i => i.Status == s);
    public bool AllDone => Items.All(i => i.Status != UndoStatus.Failed);
}

/// <summary>Список и откат записей журнала: «Отменить всё» — по порядку, обратному записи; отчёт по пунктам.</summary>
public sealed class ChangeUndoEngine(ChangeJournal journal)
{
    readonly Dictionary<string, IChangeUndoer> _byKind = [];

    public ChangeJournal Journal => journal;

    public void Register(IChangeUndoer undoer)
    {
        foreach (var k in undoer.Kinds) _byKind[k] = undoer;
    }

    public bool Knows(string kind) => _byKind.ContainsKey(kind);

    public ChangesList List() =>
        new([.. journal.Active().Select(e => new ChangeInfo(e.Id, e.At, e.Kind, e.Title, Knows(e.Kind), e.Group))]);

    public async Task<UndoReport> UndoAllAsync(CancellationToken ct = default)
    {
        var items = new List<UndoItem>();
        foreach (var e in journal.Active().Reverse())
        {
            ct.ThrowIfCancellationRequested();
            items.Add(await UndoFreshAsync(e, ct));
        }
        return new(items);
    }

    public async Task<UndoReport> UndoAsync(string id, CancellationToken ct = default)
    {
        var e = journal.Active().FirstOrDefault(x => x.Id == id) ?? throw new InvalidOperationException(L.T("core.undo.no_entry"));
        return new([await UndoOneAsync(e, ct)]);
    }

    /// <summary>
    /// Откатить действие вместе с записями, сделанными внутри него (<see cref="ChangeEntry.Group"/>): сначала они — в порядке, обратном записи, затем оно само.
    /// Остальной журнал не трогается.
    /// </summary>
    public async Task<UndoReport> UndoGroupAsync(string id, CancellationToken ct = default)
    {
        var active = journal.Active();
        var head = active.FirstOrDefault(x => x.Id == id) ?? throw new InvalidOperationException(L.T("core.undo.no_entry"));
        var items = new List<UndoItem>();
        foreach (var e in active.Where(x => x.Group == id).Reverse())
        {
            ct.ThrowIfCancellationRequested();
            items.Add(await UndoFreshAsync(e, ct));
        }
        items.Add(await UndoFreshAsync(head, ct));
        return new(items);
    }

    /// <summary>
    /// Откат по списку, снятому до начала: запись перечитывается перед своим шагом — откат более поздней мог уже поправить её «что поставили»
    /// (загрузка настроек возвращает продвинутый режим через ту же открытую запись) или закрыть её (вернулись к её началу) — тогда «уже как было».
    /// </summary>
    async Task<UndoItem> UndoFreshAsync(ChangeEntry e, CancellationToken ct)
    {
        var now = journal.Active().FirstOrDefault(x => x.Id == e.Id);
        return now is null ? new(e.Id, e.Title, e.At, UndoStatus.AlreadyBack, "") : await UndoOneAsync(now, ct);
    }

    async Task<UndoItem> UndoOneAsync(ChangeEntry e, CancellationToken ct)
    {
        UndoOutcome o;
        if (!_byKind.TryGetValue(e.Kind, out var u))
            o = UndoOutcome.Fail(L.T("core.undo.other_version"));
        else
        {
            try { o = await u.UndoAsync(e, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { o = UndoOutcome.Fail(string.IsNullOrWhiteSpace(ex.Message) ? L.T("core.undo.error") : ex.Message); }
        }
        switch (o.Status)
        {
            case UndoStatus.Restored: journal.MarkUndone(e.Id, "restored", o.Note); break;
            case UndoStatus.AlreadyBack: journal.MarkUndone(e.Id, "already", o.Note); break;
            case UndoStatus.ChangedByOthers: journal.MarkUndone(e.Id, "skipped", o.Note); break;
        }
        return new(e.Id, e.Title, e.At, o.Status, o.Note);
    }
}
