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
        if (!Connected) return UndoOutcome.Fail("роутер не подключён — подключитесь на вкладке «Роутер» и отмените снова");
        RouterDnsStatus st;
        try { st = await Call(c => c.DnsStatusAsync()); }
        catch (RouterException ex) { return UndoOutcome.Fail(RouterTexts.Friendly(ex)); }
        var ours = e.After?["preset"]?.GetValue<string>();
        bool provider = st.Preset == "provider" || (st.Preset is null && st.Mode != "doh");
        if (provider) return UndoOutcome.Already("DNS сети на роутере уже как у провайдера");
        bool mine = ours is null ? st.Mode == "doh" : st.Preset == ours || ours == "custom";
        if (!mine) return UndoOutcome.Changed("DNS сети на роутере изменён после DpiShell");
        await DnsRestoreAsync();
        var job = _dnsStatus?.Job;
        return job is { Action: "dns_restore", Ok: true } ? UndoOutcome.Restored("DNS сети на роутере — как было")
            : UndoOutcome.Fail(job?.Message is { Length: > 0 } m ? m : "роутер не подтвердил возврат DNS");
    }

    /// <summary>Откат «оптимизация включена на роутере»: выключить, если сейчас включена.</summary>
    public async Task<UndoOutcome> UndoEnableAsync(ChangeEntry e)
    {
        if (!Connected) return UndoOutcome.Fail("роутер не подключён — подключитесь на вкладке «Роутер» и отмените снова");
        try { await RefreshStatusCoreAsync(); }
        catch (RouterException ex) { return UndoOutcome.Fail(RouterTexts.Friendly(ex)); }
        if (!RouterEnabled) return UndoOutcome.Already("оптимизация на роутере уже выключена");
        return await DisableAsync() ? UndoOutcome.Restored("оптимизация на роутере выключена") : UndoOutcome.Fail("роутер не выключил оптимизацию");
    }
}
