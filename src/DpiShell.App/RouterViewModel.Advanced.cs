using System.IO;
using DpiShell.Core;
using DpiShell.Core.Router;

namespace DpiShell.App;

/// <summary>
/// Продвинутый режим на роутере (вкладка «Сайты» → «На роутере», договор §5.1/§5.2 <c>advanced_get</c>/<c>advanced_set</c>, роутер 0.2.0-r9):
/// строки «сайты → стратегия» и режим остальных адресов роутер собирает и проверяет сам. Сохранение — на пробу, как смена стратегии
/// (<c>confirm_s</c>): проверка с этого ПК решает, оставить ли; не подтвердили — роутер вернёт и прежние строки.
/// </summary>
public sealed partial class RouterViewModel
{
    /// <summary>Карточка «Продвинутый режим» для роутера; у ПК своя — <see cref="MainViewModel.AdvancedPc"/>.</summary>
    public AdvancedViewModel Advanced { get; private set; } = null!;

    bool _advancedMissing;
    string? _lastFailure;
    /// <summary>Что было до сохранённого на пробу: «Вернуть как было» на плашке возвращает и строки.</summary>
    AdvancedState? _advancedBeforeTrial;
    Action? _advancedRevertJournal;

    void InitAdvanced()
    {
        Advanced = new AdvancedViewModel(router: true)
        {
            Load = LoadAdvancedAsync,
            Save = SaveAdvancedAsync,
            Strategies = () => [.. Strategies.Select(s => new AdvancedChoice(s.Id, s.Title))],
            Unavailable = () => !Connected ? L.T("router.adv.connect")
                : _advancedMissing || Compat?.CanCall("advanced_set") == false
                    ? L.T("router.adv.missing") : null,
            CanRevertNow = () => Connected,
            Builtin = new BuiltinLists(Path.Combine(_shell.Paths.Seed, "lists")),
            Say = s => Say(s),
        };
    }

    static AdvancedState ToState(RouterAdvanced a) => new(a.Enabled, a.Others, [.. a.Rows.Select(r => new AdvancedRowState(r.Domains, r.Strategy, r.Resolved))]);

    static List<RouterAdvancedRow> ToRows(AdvancedState s) => [.. s.Rows.Select(r => new RouterAdvancedRow([.. r.Domains], r.Strategy))];

    static AdvancedConfig ToConfig(AdvancedState s) => new()
    {
        Enabled = s.Enabled, Others = s.Others,
        Rows = [.. s.Rows.Select(r => new AdvancedRow { Domains = [.. r.Domains], Strategy = r.Strategy })],
    };

    static AdvancedState ToState(AdvancedConfig c) =>
        new(c.Enabled, c.Others, [.. c.Rows.Select(r => new AdvancedRowState(r.Domains, r.Strategy, null))]);

    async Task<AdvancedState?> LoadAdvancedAsync()
    {
        if (!Connected) return null;
        if (Compat?.CanCall("advanced_get") == false) { _advancedMissing = true; return null; }
        try
        {
            var a = await Call(c => c.AdvancedGetAsync());
            _advancedMissing = false;
            return ToState(a);
        }
        catch (RouterException e) when (e.Failure == RouterFailure.NotSupported) { _advancedMissing = true; return null; }
    }

    async Task<AdvancedSaveResult> SaveAdvancedAsync(AdvancedState next, AdvancedState prev)
    {
        if (!Connected) return new(null, L.T("router.adv.connect_first"));
        // Журнал отката: первая запись — ДО действия («было … стало …»); последующие правят «стало» уже после успеха.
        var after = ToConfig(next).ToJson();
        var open = Recorder.Active(ChangeKinds.RouterAdvanced).FirstOrDefault();
        string rec = open is null
            ? Recorder.Begin(ChangeKinds.RouterAdvanced, AdvancedTitle(next), ToConfig(prev).ToJson(), after)
            : "";
        var rows = ToRows(next);
        _lastFailure = null;
        _advancedBeforeTrial = prev;
        if (!await TrialAsync(L.T("router.apply.busy"), (c, confirmS) => c.AdvancedSetAsync(next.Enabled, next.Others, rows, confirmS), null))
        {
            _advancedBeforeTrial = null;
            if (rec.Length > 0) Recorder.Abort(rec);
            return new(null, AdvancedFailure(_lastFailure));
        }
        if (rec.Length > 0)
        {
            Recorder.Done(rec);
            _advancedRevertJournal = () => Recorder.Close(ChangeKinds.RouterAdvanced, L.T("router.adv.journal_restore"));
        }
        else
        {
            var was = open!.After;
            if (AdvancedConfig.FromJson(open.Before).SameChoice(ToConfig(next)))
                Recorder.Close(ChangeKinds.RouterAdvanced, L.T("router.adv.journal_revert"));
            else
                Recorder.Update(open.Id, after);
            _advancedRevertJournal = () => Recorder.Update(open.Id, was);
        }
        if (_status?.PendingConfirm is null) { _advancedBeforeTrial = null; _advancedRevertJournal = null; }
        try { return new(ToState(await Call(c => c.AdvancedGetAsync())), null); }
        catch (RouterException e) { return new(next, L.T("router.adv.saved_unread", new { error = RouterTexts.Friendly(e) })); }
    }

    static string AdvancedTitle(AdvancedState s) =>
        L.T(s.Enabled ? "router.adv.title_on" : "router.adv.title_off", new { count = s.Rows.Count });

    static string AdvancedFailure(string? text) => text switch
    {
        null or "" => L.T("router.adv.save_failed"),
        _ => text,
    };

    /// <summary>
    /// «Вернуть как было» на плашке пробы, если она от продвинутого режима: прежние строки возвращаются тоже (сам роутер делает это по таймеру).
    /// Вызывается внутри Guard плашки, перед выключением или возвратом стратегии.
    /// </summary>
    async Task RevertAdvancedTrialAsync()
    {
        if (_advancedBeforeTrial is not { } was) return;
        _advancedBeforeTrial = null;
        try { await Call(c => c.AdvancedSetAsync(was.Enabled, was.Others, ToRows(was))); }
        catch (RouterException) { return; }   // роутер сам вернёт по сроку; плашку это не должно ломать
        _advancedRevertJournal?.Invoke();
        _advancedRevertJournal = null;
        _ = Advanced.RefreshAsync(force: true);
    }

    /// <summary>Откат записи журнала окна: вернуть прежние строки на роутере, если сейчас там то, что поставили мы.</summary>
    public async Task<UndoOutcome> UndoAdvancedAsync(ChangeEntry e)
    {
        if (!Connected) return UndoOutcome.Fail(L.T("router.undo.not_connected"));
        RouterAdvanced cur;
        try { cur = await Call(c => c.AdvancedGetAsync()); }
        catch (RouterException ex) { return UndoOutcome.Fail(RouterTexts.Friendly(ex)); }
        var was = AdvancedConfig.FromJson(e.Before);
        var ours = AdvancedConfig.FromJson(e.After);
        var now = ToConfig(ToState(cur));
        if (now.SameChoice(was)) return UndoOutcome.Already(L.T("router.adv.undo_already"));
        if (!now.SameChoice(ours)) return UndoOutcome.Changed(L.T("router.adv.undo_changed"));
        try { await Call(c => c.AdvancedSetAsync(was.Enabled, was.Others, ToRows(ToState(was)))); }
        catch (RouterException ex) { return UndoOutcome.Fail(RouterTexts.Friendly(ex)); }
        _ = Advanced.RefreshAsync(force: true);
        return UndoOutcome.Restored(was.Enabled ? L.T("router.adv.undo_restored_on", new { count = was.Rows.Count }) : L.T("router.adv.undo_restored_off"));
    }
}
