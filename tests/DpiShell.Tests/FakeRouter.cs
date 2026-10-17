using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace DpiShell.Tests;

/// <summary>
/// Роутер с DpiShell в памяти — по docs/dev/router-api.md (API 1). Ответы — примеры из §3–5 дословно;
/// поведение для случаев §7 включается полями. HttpListener на localhost работает без прав администратора.
/// </summary>
public sealed class FakeRouter : IDisposable
{
    readonly HttpListener _l = new();
    readonly CancellationTokenSource _cts = new();
    readonly Task _loop;

    public string Address { get; }
    public string Password { get; set; } = "secret";

    // §7: случаи
    public string? StatusJson { get; set; } =
        """{ "product": "dpishell-router", "api": 1, "version": "0.1.0", "engine": "zapret1", "core": "v72.13", "enabled": true, "strategy": "alt10", "catalog_version": 3, "checked_at": "2026-10-16T21:00:00Z", "last_check_ok": true }""";
    public bool ExpireSessionOnce { get; set; }
    public bool UseRpcAccessDeniedError { get; set; }
    public HashSet<string> MissingMethods { get; } = [];
    public string? BusyOn { get; set; }
    /// <summary>Метод → error.field для ответа invalid (§4).</summary>
    public (string Method, string Field)? InvalidOn { get; set; }

    // Подбор: select_progress идёт от running к best + applied (§5.1, редакция §9)
    public int SelectSteps { get; set; } = 3;
    int _selectStep = -1;
    /// <summary>Строки таблицы подбора (JSON-массив); по умолчанию — пример §5.1.</summary>
    public string SelectRowsJson { get; set; } = """[ { "id": "alt", "title": "general (ALT)", "group_share": 1.0, "target_share": 0.88, "avg_s": 0.74, "error": null } ]""";
    public string BestId { get; set; } = "alt";

    // Состояние для status: меняется enable/disable/strategy_set/check_now (по умолчанию — пример §5.1).
    public bool Enabled { get; set; } = true;
    public string Strategy { get; set; } = "alt10";
    public string Mode { get; set; } = "all";
    public bool NeedsTcpTimestamps { get; set; } = true;
    public bool AutoSelect { get; set; }
    public string SiteCheckUrl { get; set; } = "";
    public DateTime CheckedAt { get; set; } = new(2026, 10, 16, 21, 0, 0, DateTimeKind.Utc);
    public bool SelectRunningJob { get; set; }
    public bool ScheduleEnabled { get; set; } = true;
    public double ScheduleHours { get; set; } = 1;
    public string StrategiesJson { get; set; } =
        """{ "catalog_version": 3, "items": [ { "id": "alt10", "title": "general (ALT10)", "source": "catalog", "runs": 7, "passed": 6 } ] }""";
    public string WarningsJson { get; set; } = """[ { "code": "flow_offload", "text": "Включён flow offloading" } ]""";

    // «Сайт не открывается»: столько опросов site_check_result отвечают running: true (§5.1).
    public int SiteCheckRunningPolls { get; set; }
    int _sitePolls;
    public bool SiteWithBypassOk { get; set; }
    public bool SiteWithoutBypassOk { get; set; } = true;

    // DNS (§5.1 dns_status): задания фоновые, столько опросов job.running: true; итог — ok и mode.
    public string DnsMode { get; set; } = "provider";
    public bool DnsSpoofed { get; set; } = true;
    public int DnsJobRunningPolls { get; set; } = 1;
    public bool DnsJobOk { get; set; } = true;
    string _dnsAction = "dns_enable";
    DateTime _dnsAt = new(2026, 10, 17, 10, 1, 0, DateTimeKind.Utc);
    int _dnsPolls = int.MaxValue;
    string? _dnsTarget;
    /// <summary>Пишется ли job.at (в §5.1 его нет, роутер отдаёт — §9 не упоминает).</summary>
    public bool DnsJobAt { get; set; } = true;

    /// <summary>§9: enable/select_start при чужом обходе — error: conflict.</summary>
    public bool ConflictOnEnable { get; set; }
    public string ConflictsJson { get; set; } = """[ { "kind": "service", "name": "zapret2", "running": true } ]""";
    public bool FlowOffloadFixed { get; private set; }
    public bool? Ipv6Enabled { get; private set; }

    // С 0.2.0 (§5.4): api строкой, methods, min_app, update. По умолчанию — как старый роутер 0.1.0 (api: 1, без новых полей).
    /// <summary>Значение api в status как JSON: 1 (до 0.2.0) или "1.0".</summary>
    public string StatusApi { get; set; } = "1";
    public string RouterVersion { get; set; } = "0.1.0";
    /// <summary>JSON-массив status.methods; null — поля нет.</summary>
    public string? MethodsJson { get; set; }
    public string? MinApp { get; set; }
    /// <summary>Отдавать status.update.</summary>
    public bool UpdateFieldOn { get; set; }
    public bool UpdateAvailable { get; set; } = true;
    public string UpdateVersion { get; set; } = "0.2.1-r1";
    /// <summary>Пакеты уже положены в /tmp/dpishell-update (иначе update_install local — invalid).</summary>
    public bool LocalUpdateReady { get; set; }
    public int UpdateCheckCalls { get; private set; }
    public bool? UninstallKeepSettings { get; private set; }
    /// <summary>После uninstall объект dpishell пропадает — все вызовы отвечают 4.</summary>
    public bool Uninstalled { get; private set; }
    string? _updateFrom;
    int _updatePolls;
    string? _updateJob;

    /// <summary>Как роутер 0.2.0: api "1.0", methods (все, что знает подделка), min_app, update.</summary>
    public FakeRouter As020()
    {
        StatusApi = "\"1.0\""; RouterVersion = "0.2.0-r3"; MinApp = "1.0.0"; UpdateFieldOn = true;
        MethodsJson = """["apply","check_now","conflict_stop","core_install","diag","disable","dns_analyze","dns_enable","dns_presets","dns_restore","dns_set","dns_status","enable","flow_offload_fix","ipv6_set","lists_get","lists_set","mode_set","report","schedule_set","select_cancel","select_progress","select_start","site_check","site_check_result","status","strategies","strategies_user_set","strategy_set","targets_get","targets_set","uninstall","update_check","update_install"]""";
        return this;
    }

    string Contract020() =>
        (MinApp is null ? "" : $" \"min_app\": \"{MinApp}\",") + (MethodsJson is null ? "" : $" \"methods\": {MethodsJson},");

    /// <summary>Перед каждым status: первый опрос после update_install — задание идёт, второй — готово (версия сменилась).</summary>
    void AdvanceUpdate()
    {
        _updateRunning = false;
        if (_updateFrom is not { } from) return;
        if (_updatePolls++ == 0)
        {
            _updateRunning = true;
            _updateJob = $$"""{ "action": "update", "from": "{{from}}", "running": true, "ok": null, "message": "Обновление", "at": null }""";
            return;
        }
        RouterVersion = UpdateVersion; UpdateAvailable = false; _updateFrom = null;
        _updateJob = $$"""{ "action": "update", "from": "{{from}}", "running": false, "ok": true, "message": "Обновлено до {{UpdateVersion}}", "at": "2026-10-17T14:05:00Z" }""";
    }

    bool _updateRunning;

    string UpdateField()
    {
        if (!UpdateFieldOn) return "";
        var notes = UpdateAvailable ? """["Глубокий поиск стратегий на самом роутере"]""" : "[]";
        return $$""", "update": { "available": {{(UpdateAvailable ? "true" : "false")}}, "version": "{{UpdateVersion}}", "notes": {{notes}}, "checked_at": "2026-10-17T14:00:00Z", "job": {{_updateJob ?? "null"}} }""";
    }

    public List<JsonObject> Calls { get; } = [];
    public List<string> RawBodies { get; } = [];
    readonly HashSet<string> _sessions = [];
    int _logins;
    public int Logins => _logins;

    public FakeRouter()
    {
        // Свободный порт: занимаем и отпускаем.
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Address = $"localhost:{port}";
        _l.Prefixes.Add($"http://localhost:{port}/");
        _l.Start();
        _loop = Task.Run(LoopAsync);
    }

    async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _l.GetContextAsync(); } catch { return; }
            try { await HandleAsync(ctx); }
            catch { ctx.Response.StatusCode = 500; }
            finally { ctx.Response.Close(); }
        }
    }

    async Task HandleAsync(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url!.AbsolutePath;
        if (ctx.Request.HttpMethod == "GET" && path == "/dpishell/status.json")
        {
            if (StatusJson is null) { ctx.Response.StatusCode = 404; return; }
            await Write(ctx, StatusJson);
            return;
        }
        if (ctx.Request.HttpMethod != "POST" || path != "/ubus") { ctx.Response.StatusCode = 404; return; }

        var body = await new StreamReader(ctx.Request.InputStream, Encoding.UTF8).ReadToEndAsync();
        lock (RawBodies) RawBodies.Add(body);
        var req = JsonNode.Parse(body)!.AsObject();
        lock (Calls) Calls.Add(req);
        var id = req["id"]!.GetValue<int>();
        var p = req["params"]!.AsArray();
        string session = p[0]!.GetValue<string>(), obj = p[1]!.GetValue<string>(), method = p[2]!.GetValue<string>();
        var args = p[3] as JsonObject ?? new JsonObject();

        string Result(int code, string? payload = null) =>
            $$"""{"jsonrpc":"2.0","id":{{id}},"result":[{{code}}{{(payload is null ? "" : "," + payload)}}]}""";

        if (obj == "session" && method == "login")
        {
            if (args["password"]?.GetValue<string>() != Password) { await Write(ctx, Result(6)); return; }
            var s = $"c1ed6c7b{Interlocked.Increment(ref _logins):D24}";
            lock (_sessions) _sessions.Add(s);
            await Write(ctx, Result(0, $$"""{"ubus_rpc_session":"{{s}}","timeout":600,"expires":600}"""));
            return;
        }

        bool valid; lock (_sessions) valid = _sessions.Contains(session);
        if (ExpireSessionOnce && valid) { ExpireSessionOnce = false; lock (_sessions) _sessions.Clear(); valid = false; }
        if (!valid)
        {
            await Write(ctx, UseRpcAccessDeniedError
                ? $$$"""{"jsonrpc":"2.0","id":{{{id}}},"error":{"code":-32002,"message":"Access denied"}}"""
                : Result(6));
            return;
        }
        if (obj != "dpishell" || MissingMethods.Contains(method) || Uninstalled) { await Write(ctx, Result(4)); return; }
        if (InvalidOn is { } inv && inv.Method == method)
        {
            await Write(ctx, Result(0, $$$"""{"error":{"code":"invalid","text":"Неверное значение","field":"{{{inv.Field}}}"}}"""));
            return;
        }
        if (ConflictOnEnable && method is "enable" or "select_start")
        {
            await Write(ctx, Result(0, """{"error":{"code":"conflict","text":"Работает чужой обход"}}"""));
            return;
        }
        if (BusyOn == method) { await Write(ctx, Result(0, """{"error":{"code":"busy","text":"Идёт подбор"}}""")); return; }

        if (method == "status") AdvanceUpdate();
        await Write(ctx, Result(0, method switch
        {
            "status" => $$"""
                { "api": {{StatusApi}}, "version": "{{RouterVersion}}", "engine": "zapret1",{{Contract020()}}
                  "core": { "version": "v72.13", "installed": true, "hash_ok": true },
                  "enabled": {{(Enabled ? "true" : "false")}}, "mode": "{{Mode}}", "strategy": "{{Strategy}}", "backups": ["alt9", "alt13"], "needs_tcp_timestamps": {{(NeedsTcpTimestamps ? "true" : "false")}},
                  "last_check": { "at": "{{CheckedAt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture)}}", "source": "timer", "ok": true,
                                  "groups": [ { "id": "youtube", "ok": true }, { "id": "discord", "ok": true } ] },
                  "fail_streak": 0,
                  "schedule": { "enabled": {{(ScheduleEnabled ? "true" : "false")}}, "hours": {{ScheduleHours.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "auto_select": {{(AutoSelect ? "true" : "false")}} },
                  "jobs": { "select": {{(SelectRunningJob ? "true" : "false")}}, "check": false, "core_install": false, "dns_analyze": false, "site_check": false, "dns": false, "update": {{(_updateRunning ? "true" : "false")}} },
                  "warnings": {{WarningsJson}}{{UpdateField()}} }
                """,
            "strategies" => StrategiesJson,
            "enable" => Do(() => Enabled = true),
            "disable" => Do(() => Enabled = false),
            "strategy_set" => Do(() => Strategy = args["id"]!.GetValue<string>()),
            "check_now" => Do(() => CheckedAt = CheckedAt.AddMinutes(1), """{ "started": true }"""),
            // §9 (2026-10-17): только «Свои адреса», а их список пуст — invalid, field groups.
            "select_start" when args["groups"] is System.Text.Json.Nodes.JsonArray g && g.Count == 1 && g[0]!.GetValue<string>() == "user" && UserTargets.Count == 0 =>
                """{"error":{"code":"invalid","text":"Список своих адресов на роутере пуст — добавьте адреса или выберите другие группы","field":"groups"}}""",
            "select_start" => Step0("""{ "started": true }"""),
            "targets_set" => Do(() => UserTargets = args["items"]!.AsArray().Select(x => (x!["url"]!.GetValue<string>(), x["important"]?.GetValue<bool>() ?? false)).ToList()),
            "select_cancel" => Do(() => { _cancelled = true; SelectRunningJob = false; }),
            "apply" => Do(() => Strategy = args["id"]!.GetValue<string>()),
            "schedule_set" => Do(() =>
            {
                ScheduleEnabled = args["enabled"]!.GetValue<bool>(); ScheduleHours = args["hours"]!.GetValue<double>();
                AutoSelect = args["auto_select"]?.GetValue<bool>() ?? false;
            }),
            "mode_set" => Do(() => Mode = args["mode"]!.GetValue<string>(), """{ "started": true }"""),
            "site_check" => Do(() => { SiteCheckUrl = args["url"]!.GetValue<string>(); _sitePolls = 0; }, """{ "started": true }"""),
            "site_check_result" => $$"""
                { "running": {{(_sitePolls++ < SiteCheckRunningPolls ? "true" : "false")}}, "url": "{{SiteCheckUrl}}",
                  "with_bypass": { "ok": {{(SiteWithBypassOk ? "true" : "false")}}, "detail": "{{(SiteWithBypassOk ? "" : "сброс после 16 КБ")}}" },
                  "without_bypass": { "ok": {{(SiteWithoutBypassOk ? "true" : "false")}}, "detail": "" },
                  "suggest_exclude": {{(!SiteWithBypassOk && SiteWithoutBypassOk ? "true" : "false")}} }
                """,
            "dns_status" => DnsStatus(),
            "dns_presets" => $$"""
                { "presets": [
                    { "id": "provider", "title": "Как у провайдера", "note": null, "resolvers": [], "plain": [] },
                    { "id": "default", "title": "Google + Cloudflare", "note": null, "resolvers": [ { "doh": "https://dns.google/dns-query", "bootstrap": ["8.8.8.8", "8.8.4.4"] }, { "doh": "https://cloudflare-dns.com/dns-query", "bootstrap": ["1.1.1.1", "1.0.0.1"] } ], "plain": [] },
                    { "id": "google", "title": "Google", "note": null, "resolvers": [ { "doh": "https://dns.google/dns-query", "bootstrap": ["8.8.8.8", "8.8.4.4"] } ], "plain": [] },
                    { "id": "yandex", "title": "Яндекс", "note": "российский: применяет реестр блокировок", "resolvers": [ { "doh": "https://common.dot.dns.yandex.net/dns-query", "bootstrap": ["77.88.8.8", "77.88.8.1"] } ], "plain": ["77.88.8.8", "77.88.8.1"] } ],
                  "current": "{{DnsPreset}}", "custom": {{DnsCustomJson ?? "null"}} }
                """,
            "dns_set" => DnsSet(args),
            "dns_analyze" or "dns_enable" or "dns_restore" => Do(() =>
            {
                _dnsAction = method; _dnsPolls = 0; _dnsAt = _dnsAt.AddMinutes(1);
                _dnsTarget = method switch { "dns_enable" => "doh", "dns_restore" => "provider", _ => null };
            }, """{ "started": true }"""),
            "flow_offload_fix" => Do(() => { FlowOffloadFixed = true; WarningsJson = "[]"; }, """{ "changed": true, "flowoffload": "software" }"""),
            "ipv6_set" => Do(() => { Ipv6Enabled = args["enabled"]!.GetValue<bool>(); WarningsJson = "[]"; }),
            "lists_set" => Do(() => Lists[args["name"]!.GetValue<string>()] = args["lines"]!.AsArray().Select(x => x!.GetValue<string>()).ToList()),
            "select_progress" => Progress(),
            "targets_get" => System.Text.Json.JsonSerializer.Serialize(new { items = UserTargets.Select(t => new { url = t.Url, important = t.Important }) }),
            "lists_get" => args["name"]?.GetValue<string>() == "exclude"
                ? """{ "lines": ["gosuslugi.ru"], "editable": false }"""
                : $$"""{ "lines": {{System.Text.Json.JsonSerializer.Serialize(Lists.GetValueOrDefault(args["name"]!.GetValue<string>()) ?? ["example.com"])}}, "editable": true }""",
            "diag" => $$"""{ "conflicts": {{ConflictsJson}}, "flow_offload": {{(FlowOffloadFixed ? "false" : "true")}}, "flowoffload": "{{(FlowOffloadFixed ? "software" : "none")}}","ipv6_wan": true, "dns": { "spoofed": true, "checked_at": "2026-10-16T21:00:00Z" }, "overlay_free_kb": 18000 }""",
            "report" => """{ "text": "отчёт роутера" }""",
            "core_install" => """{ "started": true }""",
            // Ядро договора §5.4.
            "uninstall" => Do(() => { UninstallKeepSettings = args["keep_settings"]?.GetValue<bool>(); Uninstalled = true; }, """{ "started": true }"""),
            "update_install" when args["from"]?.GetValue<string>() is not ("feed" or "local") =>
                """{"error":{"code":"invalid","text":"from: feed или local","field":"from"}}""",
            "update_install" when args["from"]!.GetValue<string>() == "local" && !LocalUpdateReady =>
                """{"error":{"code":"invalid","text":"Нет пакетов в /tmp/dpishell-update","field":"from"}}""",
            "update_install" => Do(() => { _updateFrom = args["from"]!.GetValue<string>(); _updatePolls = 0; }, """{ "started": true }"""),
            "update_check" => Do(() => UpdateCheckCalls++, """{ "started": true }"""),
            _ => "{}",
        }));
    }

    static string Do(Action a, string payload = "{}") { a(); return payload; }

    /// <summary>«Свои адреса» роутера (targets_get/targets_set); по умолчанию — пример §5.1.</summary>
    public List<(string Url, bool Important)> UserTargets { get; set; } = [("https://example.com/", true)];

    /// <summary>Свои списки после lists_set (до него — пример §5.1).</summary>
    public Dictionary<string, List<string>> Lists { get; } = [];

    // Варианты DNS (§5.1 dns_presets/dns_set, §9 2026-10-17).
    public string DnsPreset { get; set; } = "provider";
    public string? DnsCustomJson { get; set; }
    string? _presetTarget, _customTarget;
    static readonly string[] PresetIds = ["provider", "default", "google", "yandex"];

    string DnsSet(JsonObject args)
    {
        static string Invalid(string field, string text) =>
            new JsonObject { ["error"] = new JsonObject { ["code"] = "invalid", ["text"] = text, ["field"] = field } }.ToJsonString();
        if (args["custom"] is JsonObject c)
        {
            if (c["title"] is null || (c["doh"] is null && c["plain"] is null)) return Invalid("custom", "Нужен doh или plain");
            _presetTarget = "custom"; _customTarget = c.ToJsonString();
            _dnsTarget = c["doh"] is null ? "plain" : "doh";
        }
        else if (args["preset"]?.GetValue<string>() is { } p && (PresetIds.Contains(p) || p == "custom" && DnsCustomJson is not null))
        {
            _presetTarget = p; _customTarget = DnsCustomJson;
            _dnsTarget = p == "provider" ? "provider" : "doh";
        }
        else return Invalid("preset", "Нет такого варианта");
        _dnsAction = "dns_set"; _dnsPolls = 0; _dnsAt = _dnsAt.AddMinutes(1);
        return """{ "started": true }""";
    }

    string DnsStatus()
    {
        bool running = _dnsPolls < DnsJobRunningPolls;
        if (_dnsPolls != int.MaxValue) _dnsPolls++;
        if (!running && _dnsTarget is { } m && DnsJobOk)
        {
            DnsMode = m; _dnsTarget = null;
            DnsPreset = _presetTarget ?? (m == "provider" ? "provider" : "default");
            if (_presetTarget == "custom") DnsCustomJson = _customTarget;
            _presetTarget = null;
        }
        var at = DnsJobAt ? $", \"at\": \"{_dnsAt:yyyy-MM-dd'T'HH:mm:ss'Z'}\"" : "";
        return $$"""
            { "mode": "{{DnsMode}}", "preset": "{{DnsPreset}}", "custom": {{DnsCustomJson ?? "null"}}, "spoofed": {{(DnsSpoofed ? "true" : "false")}}, "checked_at": "2026-10-17T10:00:00Z",
              "servers": [ { "id": "google", "name": "Google", "doh_ok": true, "ms": 38 }, { "id": "cloudflare", "name": "Cloudflare", "doh_ok": true, "ms": 41 } ],
              "fallback_active": false,
              "job": { "action": "{{_dnsAction}}", "running": {{(running ? "true" : "false")}}, "ok": {{(running ? "null" : DnsJobOk ? "true" : "false")}}, "message": "{{(running ? "" : DnsJobOk ? "Готово" : "Не удалось установить пакет")}}"{{at}} } }
            """;
    }

    string Step0(string payload) { _selectStep = -1; _cancelled = false; return payload; }

    /// <summary>Первые вызовы — running: true; последний — best, applied: true и backups (§5.1, редакция §9). После select_cancel — cancelled.</summary>
    string Progress()
    {
        var step = Interlocked.Increment(ref _selectStep);
        if (_cancelled)
            return $$"""
                { "running": false, "started_by": "{{StartedBy}}", "depth": "main", "index": {{step}}, "total": 20, "eta_s": null,
                  "message": "", "rows": {{SelectRowsJson}},
                  "finished_at": "2026-10-16T21:05:00Z", "cancelled": true, "best": null, "applied": false, "backups": [] }
                """;
        if (step < SelectSteps)
            return $$"""
                { "running": true, "started_by": "{{StartedBy}}", "depth": "main", "index": {{step + 1}}, "total": 20, "eta_s": 95,
                  "message": "alt3: YouTube",
                  "rows": {{SelectRowsJson}},
                  "finished_at": null, "cancelled": false, "best": null, "applied": false, "backups": [] }
                """;
        if (Applied) { Strategy = BestId; SelectRunningJob = false; }
        return $$"""
            { "running": false, "started_by": "{{StartedBy}}", "depth": "main", "index": 20, "total": 20, "eta_s": null,
              "message": "", "rows": {{SelectRowsJson}},
              "finished_at": "2026-10-16T21:05:00Z", "cancelled": false, "best": "{{BestId}}", "applied": {{(Applied ? "true" : "false")}}, "backups": {{BackupsJson}} }
            """;
    }

    bool _cancelled;
    public string StartedBy { get; set; } = "app";
    /// <summary>Подбор сам включил лучшую (architecture.md «Подбор на роутере»).</summary>
    public bool Applied { get; set; } = true;
    public string BackupsJson { get; set; } = """["alt9", "alt13"]""";

    static async Task Write(HttpListenerContext ctx, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _l.Stop();
        _l.Close();
    }
}
