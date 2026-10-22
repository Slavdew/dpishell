using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using System.Windows.Input;
using DpiShell.Core;
using DpiShell.Core.Router;

namespace DpiShell.App;

/// <summary>
/// Откат всего (журнал окна): что окно делало на роутере. Откат вызывает готовые функции роутера («DNS как было», «Выключить оптимизацию»),
/// а не пишет роутеру заново; работает, когда роутер подключён.
/// </summary>
public sealed partial class RouterViewModel
{
    /// <summary>Журнал окна (%LocalAppData%\DpiShell\changes-window.jsonl); по умолчанию записи нет.</summary>
    public IChangeRecorder Recorder { get; set; } = NullRecorder.Instance;

    string BeginRouter(string kind, string title, JsonNode? after)
    {
        if (Recorder.Active(kind).FirstOrDefault() is { } open) { Recorder.Update(open.Id, after); return ""; }
        return Recorder.Begin(kind, title, new JsonObject { ["router"] = "как до DpiShell" }, after);
    }

    /// <summary>Откат «DNS для всей сети»: роутер возвращает DNS провайдера, если сейчас включён тот вариант, что включали мы.</summary>
    public async Task<UndoOutcome> UndoDnsAsync(ChangeEntry e)
    {
        if (!Connected) return UndoOutcome.Fail(L.T("router.undo.not_connected"));
        RouterDnsStatus st;
        try { st = await Call(c => c.DnsStatusAsync()); }
        catch (RouterException ex) { return UndoOutcome.Fail(RouterTexts.Friendly(ex)); }
        var ours = e.After?["preset"]?.GetValue<string>();
        bool provider = st.Preset == "provider" || (st.Preset is null && st.Mode != "doh");
        if (provider) return UndoOutcome.Already(L.T("router.undo.dns_already"));
        bool mine = ours is null ? st.Mode == "doh" : st.Preset == ours || ours == "custom";
        if (!mine) return UndoOutcome.Changed(L.T("router.undo.dns_changed"));
        await DnsRestoreAsync();
        var job = _dnsStatus?.Job;
        return job is { Action: "dns_restore", Ok: true } ? UndoOutcome.Restored(L.T("router.undo.dns_restored"))
            : UndoOutcome.Fail(job?.Message is { Length: > 0 } m ? m : L.T("router.undo.dns_failed"));
    }

    // ---------- «Изменения DpiShell на роутере» (router-api §5.2 changes_list, роутер с 0.2.0-r16) ----------
    // Тот же список, что на странице роутера («Диагностика»): что пакет поменял на роутере, у пункта — «Отменить» или почему нельзя.
    // Роутер без метода (до r16) — карточки нет. Отмена идёт на роутере фоном; окно ждёт итог и показывает его по пунктам, как на ПК.

    RouterChangesList? _routerChanges;
    public ObservableCollection<ChangeRow> RouterChanges { get; } = [];
    public ObservableCollection<ChangeReportRow> RouterChangesReport { get; } = [];
    public bool RouterChangesShown => _routerChanges is not null && HasChangesMethod;
    public bool RouterChangesEmpty => RouterChangesShown && RouterChanges.Count == 0;
    public bool RouterCanUndoAll => RouterChanges.Any(r => r.HasAction);
    public bool HasRouterChangesReport => RouterChangesReport.Count > 0;
    string _routerChangesSummary = "";
    public string RouterChangesSummary { get => _routerChangesSummary; private set => Set(ref _routerChangesSummary, value); }
    bool HasChangesMethod => _status?.Methods?.Contains("changes_list") == true && !Blocked;

    public ICommand RouterUndoAllCmd => _routerUndoAll ??= RelayCommand.Async(() => UndoRouterChangesAsync(null), () => RouterCanUndoAll && Connected && !Busy);
    ICommand? _routerUndoAll;

    /// <summary>Ждать итог отмены не дольше (DNS возвращается до минуты).</summary>
    public TimeSpan ChangesUndoTimeout { get; set; } = TimeSpan.FromMinutes(3);

    void RaiseRouterChanges()
    {
        Raise(nameof(RouterChangesShown)); Raise(nameof(RouterChangesEmpty)); Raise(nameof(RouterCanUndoAll)); Raise(nameof(HasRouterChangesReport));
        CommandManager.InvalidateRequerySuggested();
    }

    async Task LoadRouterChangesAsync()
    {
        // Отказ справочного списка диагностику не роняет: карточка просто не показывается.
        try { _routerChanges = HasChangesMethod ? await Call(c => c.ChangesListAsync()) : null; }
        catch (RouterException) { _routerChanges = null; }
        FillRouterChanges();
    }

    static string Phrase(string? key, Dictionary<string, System.Text.Json.JsonElement>? ps, string? text) =>
        RouterPhrase.ByKey(key, ps) ?? text ?? "";

    void FillRouterChanges()
    {
        RouterChanges.Clear();
        foreach (var it in _routerChanges?.Items ?? [])
        {
            var title = Phrase(it.Key, it.Params, it.Text ?? it.Id);
            var when = it.At is { } at ? L.LongDate(at.ToLocalTime()) + ", " + L.Time(at.ToLocalTime()) : "";
            bool can = it.Undo == "yes";
            var note = can ? (it.Risk == "link" ? L.T("router.changes.row.link") : "") : Phrase(it.NoteKey, null, it.Note ?? L.T("settings.changes.row.only_all"));
            var row = new ChangeRow(it.Id, title, when, it.Kind ?? "", false) { Action = can ? L.T("settings.changes.row.undo") : "", Note = note };
            row.UndoCmd = RelayCommand.Async(() => UndoRouterChangesAsync(it), () => row.HasAction && Connected && !Busy);
            RouterChanges.Add(row);
        }
        RaiseRouterChanges();
    }

    RouterChangesJob? _routerChangesJob;

    void ShowRouterChangesReport(RouterChangesJob? job)
    {
        _routerChangesJob = job;
        RouterChangesReport.Clear();
        int restored = 0, same = 0, skipped = 0, failed = 0;
        foreach (var r in job?.Items ?? [])
        {
            var (status, mark) = r.Status switch
            {
                "restored" => (L.T("settings.changes.status_restored"), "ok"),
                "same" => (L.T("settings.changes.status_same"), "same"),
                "skipped" => (L.T("settings.changes.status_skipped"), "skip"),
                _ => (L.T("settings.changes.status_failed"), "fail"),
            };
            _ = mark switch { "ok" => restored++, "same" => same++, "skip" => skipped++, _ => failed++ };
            RouterChangesReport.Add(new(Phrase(r.Key, r.Params, r.Text ?? r.Id), status, r.Note is { Length: > 0 } n ? Phrase(r.NoteKey, null, n) : "", mark));
        }
        if (job?.Error is { Length: > 0 } e)
        {
            failed++;
            RouterChangesReport.Add(new(L.T("router.changes.title"), L.T("settings.changes.status_failed"), e, "fail"));
        }
        RouterChangesSummary = RouterChangesReport.Count == 0 ? L.T("settings.changes.summary_none")
            : L.T("settings.changes.summary", new { restored, same, skipped, failed });
        RaiseRouterChanges();
    }

    /// <summary>«Отменить» у пункта списка по его id (как кнопка в строке); нет такого — false.</summary>
    public Task<bool> UndoRouterChangeAsync(string id) =>
        _routerChanges?.Items?.FirstOrDefault(x => x.Id == id) is { Undo: "yes" } it ? UndoRouterChangesAsync(it) : Task.FromResult(false);

    /// <summary>
    /// «Отменить» у пункта (<paramref name="item"/>) или «Отменить всё» (null): вопрос — с предупреждением, если связь может прерваться, —
    /// задание на роутере, ожидание итога (пока DNS или межсетевой экран перезапускаются, роутер может не отвечать), отчёт по пунктам.
    /// </summary>
    public async Task<bool> UndoRouterChangesAsync(RouterChangeItem? item)
    {
        var items = _routerChanges?.Items ?? [];
        bool risky = item is not null ? item.Risk == "link" : items.Any(x => x.Undo == "yes" && x.Risk == "link");
        var q = (item is not null
            ? L.T("router.changes.confirm_one", new { title = Phrase(item.Key, item.Params, item.Text ?? item.Id) })
            : L.T("router.changes.confirm_all")) + (risky ? "\n\n" + L.T("router.changes.link_warn") : "");
        if (!Confirm(q)) return false;
        RouterChangesJob? job = null;
        var before = _routerChanges?.Job;
        bool ran = await Guard(L.T("settings.changes.busy"), async () =>
        {
            await Call(c => item is null ? c.ChangesUndoAllAsync() : c.ChangesUndoAsync(item.Id));
            var started = DateTime.UtcNow;
            while (DateTime.UtcNow - started < ChangesUndoTimeout)
            {
                await Task.Delay(PollInterval);
                RouterChangesList l;
                try { l = await Call(c => c.ChangesListAsync()); }
                catch (RouterException e) when (Restarting(e)) { continue; }   // DNS или межсетевой экран перезапускаются
                _routerChanges = l;
                if (l.Job is { Running: false } j && (before is null || j.StartedAt != before.StartedAt || j.Id != before.Id)) { job = j; break; }
            }
            if (job is null) throw new RouterException(RouterFailure.NoResponse, L.T("router.update.too_long"));
        }, changes: false);
        FillRouterChanges();
        if (!ran) return false;
        ShowRouterChangesReport(job);
        try { await RefreshStatusCoreAsync(); } catch (RouterException) { }
        Say(RouterChangesSummary);
        return true;
    }

    /// <summary>Откат «оптимизация включена на роутере»: выключить, если сейчас включена.</summary>
    public async Task<UndoOutcome> UndoEnableAsync(ChangeEntry e)
    {
        if (!Connected) return UndoOutcome.Fail(L.T("router.undo.not_connected"));
        try { await RefreshStatusCoreAsync(); }
        catch (RouterException ex) { return UndoOutcome.Fail(RouterTexts.Friendly(ex)); }
        if (!RouterEnabled) return UndoOutcome.Already(L.T("router.undo.enable_already"));
        return await DisableAsync() ? UndoOutcome.Restored(L.T("router.undo.enable_restored")) : UndoOutcome.Fail(L.T("router.undo.enable_failed"));
    }
}
