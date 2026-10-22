using System.Text.Json.Nodes;

namespace DpiShell.Tests;

/// <summary>
/// «Изменения DpiShell на роутере» (changes_list / changes_undo / changes_undo_all) и откат версии (update_rollback, status.rollback) —
/// роутер с 0.2.0-r16 (router-api.md §5.2). По умолчанию подделка — роутер без них.
/// </summary>
public sealed partial class FakeRouter
{
    /// <summary>status.methods роутера 0.2.0-r16: Methods020 + changes_* и update_rollback.</summary>
    public static readonly string MethodsR16 = Methods020.Replace("\"status\"", "\"changes_list\",\"changes_undo\",\"changes_undo_all\",\"status\",\"update_rollback\"");

    /// <summary>Пункты changes_list (id, kind, undo yes|no, risk link) — как роутер их отдаёт; отмена убирает пункт.</summary>
    public List<JsonObject> ChangeItems { get; } =
    [
        Item("enable", "enable", "router.changes.item.enable", new JsonObject(), "Оптимизация включена: nfqws, правила nft, автозапуск"),
        Item("list:user-general", "list", "router.changes.item.list",
            new JsonObject { ["name"] = new JsonObject { ["key"] = "router.changes.list.user_general", ["params"] = new JsonObject(), ["text"] = "Свои сайты" }, ["n"] = 2 },
            "Свои сайты: строк 2"),
        Item("dns", "dns", "router.changes.item.dns", new JsonObject { ["preset"] = "google" }, "DNS для всей сети", risk: "link"),
        Item("future", "future_kind", "router.changes.item.future", new JsonObject(), "Что-то из версии новее окна"),
        Item("core", "core", "router.changes.item.core", new JsonObject { ["version"] = "v72.13" }, "Ядро zapret в /opt/zapret",
            undo: "no", noteKey: "router.changes.row.core", note: "Убирается при удалении DpiShell"),
    ];

    static JsonObject Item(string id, string kind, string key, JsonObject ps, string text, string undo = "yes", string? risk = null, string? noteKey = null, string? note = null)
    {
        var o = new JsonObject { ["id"] = id, ["kind"] = kind, ["at"] = "2026-10-22T20:00:00Z", ["key"] = key, ["params"] = ps, ["text"] = text, ["undo"] = undo, ["risk"] = risk };
        if (noteKey is not null) { o["note_key"] = noteKey; o["note"] = note; }
        return o;
    }

    /// <summary>Столько опросов changes_list после отмены задание ещё идёт.</summary>
    public int ChangesJobRunningPolls { get; set; } = 1;
    /// <summary>Столько опросов changes_list после отмены роутер «не отвечает» (перезапуск DNS / межсетевого экрана).</summary>
    public int ChangesUnreachablePolls { get; set; }
    /// <summary>Пункт с этим id отменяется с ошибкой (status: failed, note).</summary>
    public string? ChangeFailId { get; set; }
    public List<string> ChangesUndone { get; } = [];
    int _changesPolls = int.MaxValue, _jobNo;
    JsonObject? _changesJob;

    string ChangesList()
    {
        if (_changesJob is { } j && _changesPolls != int.MaxValue)
        {
            if (_changesPolls++ < ChangesJobRunningPolls) j["running"] = true;
            else if (j["running"]!.GetValue<bool>())
            {
                j["running"] = false;
                var results = new JsonArray();
                foreach (var it in ToUndo())
                {
                    bool fail = it["id"]!.GetValue<string>() == ChangeFailId;
                    var r = new JsonObject { ["id"] = it["id"]!.DeepClone(), ["kind"] = it["kind"]!.DeepClone(), ["key"] = it["key"]!.DeepClone(),
                        ["params"] = it["params"]!.DeepClone(), ["text"] = it["text"]!.DeepClone(), ["status"] = fail ? "failed" : "restored" };
                    if (fail) r["note"] = "DNS роутера не отвечает после возврата — перезагрузите роутер";
                    else { ChangeItems.Remove(it); ChangesUndone.Add(it["id"]!.GetValue<string>()); }
                    results.Add(r);
                }
                j["items"] = results;
                j["ok"] = results.All(r => r!["status"]!.GetValue<string>() != "failed");
                j["at"] = "2026-10-22T20:10:00Z";
            }
        }
        return new JsonObject { ["items"] = new JsonArray(ChangeItems.Select(x => (JsonNode)x.DeepClone()).ToArray()), ["job"] = _changesJob?.DeepClone() }.ToJsonString();
    }

    /// <summary>Что отменит текущее задание: пункт или все, где undo — yes.</summary>
    IEnumerable<JsonObject> ToUndo()
    {
        var id = _changesJob!["id"]!.GetValue<string>();
        return ChangeItems.Where(x => x["undo"]!.GetValue<string>() == "yes" && (id == "all" || x["id"]!.GetValue<string>() == id)).ToList();
    }

    string ChangesUndo(JsonObject? args)
    {
        var id = args is null ? "all" : args["id"]?.GetValue<string>() ?? "";
        if (args is not null && !ChangeItems.Any(x => x["id"]!.GetValue<string>() == id))
            return """{"error":{"code":"invalid","text":"Нет такого пункта — список изменился","field":"id","key":"router.changes.gone","params":{}}}""";
        _changesJob = new JsonObject { ["id"] = id, ["running"] = true, ["started_at"] = $"2026-10-22T20:0{++_jobNo % 10}:00Z", ["items"] = new JsonArray() };
        _changesPolls = 0;
        RpcdRestartCalls = ChangesUnreachablePolls;
        return """{ "started": true }""";
    }

    // ---------- откат версии ----------

    /// <summary>status.rollback: (версия, сохранены ли пакеты во флеше роутера); null — поля нет (старый роутер или отката нет).</summary>
    public (string Version, bool Saved)? Rollback { get; set; }
    /// <summary>from последнего update_rollback (saved | local).</summary>
    public string? RollbackFrom { get; private set; }
    string? _rollbackTarget;

    string RollbackField() => Rollback is not { } r ? ""
        : $$""", "rollback": { "version": "{{r.Version}}", "saved": {{(r.Saved ? "true" : "false")}}, "reason": {{(r.Saved ? "null" : "\"not_saved\"")}} }""";

    string RollbackCall(JsonObject args)
    {
        var from = args["from"]?.GetValue<string>();
        if (from is not ("saved" or "local")) return """{"error":{"code":"invalid","text":"from: saved или local","field":"from"}}""";
        if (from == "saved" && Rollback is not { Saved: true })
            return """{"error":{"code":"invalid","text":"Прежние пакеты на роутере не сохранены — откат через окно ПК","field":"from","key":"router.rollback.not_saved","params":{}}}""";
        if (from == "local" && !LocalUpdateReady) return """{"error":{"code":"invalid","text":"Нет пакетов в /tmp/dpishell-update","field":"from"}}""";
        RollbackFrom = from;
        _rollbackTarget = LocalRollbackVersion ?? Rollback?.Version;
        _updateFrom = from; _updatePolls = 0;
        return """{ "started": true }""";
    }

    /// <summary>Версия пакетов, которые окно положило для отката через ПК (иначе — status.rollback.version).</summary>
    public string? LocalRollbackVersion { get; set; }

    void RollbackDone(string version, string from)
    {
        _rollbackTarget = null;
        RouterVersion = version;
        if (Packages is { } p) Packages = (version, p.Luci);
        Rollback = null;
        UpdateAvailable = true;
        RpcdRestartCalls = RpcdRestartAfterUpdate;
        _updateJob = $$"""{ "action": "rollback", "from": "{{from}}", "running": false, "ok": true, "message": "Возвращена версия {{version}}", "at": "2026-10-22T20:15:00Z", "step": "done" }""";
    }
}
