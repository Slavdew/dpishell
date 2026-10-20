using System.Text.Json.Nodes;
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
