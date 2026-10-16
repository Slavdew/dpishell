using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace DpiShell.Tests;

/// <summary>
/// Роутер с DpiShell в памяти — по docs/07-router-api.md (API 1). Ответы — примеры из §3–5 дословно;
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
        if (obj != "dpishell" || MissingMethods.Contains(method)) { await Write(ctx, Result(4)); return; }
        if (InvalidOn is { } inv && inv.Method == method)
        {
            await Write(ctx, Result(0, $$$"""{"error":{"code":"invalid","text":"Неверное значение","field":"{{{inv.Field}}}"}}"""));
            return;
        }
        if (BusyOn == method) { await Write(ctx, Result(0, """{"error":{"code":"busy","text":"Идёт подбор"}}""")); return; }

        await Write(ctx, Result(0, method switch
        {
            "status" => $$"""
                { "api": 1, "version": "0.1.0", "engine": "zapret1",
                  "core": { "version": "v72.13", "installed": true, "hash_ok": true },
                  "enabled": {{(Enabled ? "true" : "false")}}, "mode": "{{Mode}}", "strategy": "{{Strategy}}", "backups": ["alt9", "alt13"], "needs_tcp_timestamps": {{(NeedsTcpTimestamps ? "true" : "false")}},
                  "last_check": { "at": "{{CheckedAt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture)}}", "source": "timer", "ok": true,
                                  "groups": [ { "id": "youtube", "ok": true }, { "id": "discord", "ok": true } ] },
                  "fail_streak": 0,
                  "schedule": { "enabled": {{(ScheduleEnabled ? "true" : "false")}}, "hours": {{ScheduleHours.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "auto_select": {{(AutoSelect ? "true" : "false")}} },
                  "jobs": { "select": {{(SelectRunningJob ? "true" : "false")}}, "check": false, "core_install": false, "dns_analyze": false, "site_check": false, "dns": false },
                  "warnings": {{WarningsJson}} }
                """,
            "strategies" => StrategiesJson,
            "enable" => Do(() => Enabled = true),
            "disable" => Do(() => Enabled = false),
            "strategy_set" => Do(() => Strategy = args["id"]!.GetValue<string>()),
            "check_now" => Do(() => CheckedAt = CheckedAt.AddMinutes(1), """{ "started": true }"""),
            "select_start" => Step0("""{ "started": true }"""),
            "select_cancel" => Do(() => { _cancelled = true; SelectRunningJob = false; }),
            "apply" => Do(() => Strategy = args["id"]!.GetValue<string>()),
            "schedule_set" => Do(() =>
            {
                ScheduleEnabled = args["enabled"]!.GetValue<bool>(); ScheduleHours = args["hours"]!.GetValue<double>();
                AutoSelect = args["auto_select"]?.GetValue<bool>() ?? false;
            }),
            "mode_set" => Do(() => Mode = args["mode"]!.GetValue<string>(), """{ "started": true }"""),
            "site_check" => Do(() => SiteCheckUrl = args["url"]!.GetValue<string>(), """{ "started": true }"""),
            "site_check_result" => $$"""
                { "running": false, "url": "{{SiteCheckUrl}}", "with_bypass": { "ok": false, "detail": "сброс после 16 КБ" },
                  "without_bypass": { "ok": true, "detail": "" }, "suggest_exclude": true }
                """,
            "dns_status" => """
                { "mode": "provider", "spoofed": true, "checked_at": "2026-10-17T10:00:00Z",
                  "servers": [ { "name": "Google", "doh_ok": true, "ms": 38 }, { "name": "Cloudflare", "doh_ok": true, "ms": 41 } ],
                  "fallback_active": false, "job": { "action": "dns_enable", "running": false, "ok": true, "message": "Готово", "at": "2026-10-17T10:01:00Z" } }
                """,
            "dns_analyze" or "dns_enable" or "dns_restore" => """{ "started": true }""",
            "select_progress" => Progress(),
            "targets_get" => """{ "items": [ { "url": "https://example.com/", "important": true } ] }""",
            "lists_get" => args["name"]?.GetValue<string>() == "exclude"
                ? """{ "lines": ["gosuslugi.ru"], "editable": false }"""
                : """{ "lines": ["example.com"], "editable": true }""",
            "diag" => """{ "conflicts": [ { "kind": "service", "name": "zapret2", "running": true } ], "flow_offload": false, "ipv6_wan": false, "dns": { "spoofed": true, "checked_at": "2026-10-16T21:00:00Z" }, "overlay_free_kb": 18000 }""",
            "report" => """{ "text": "отчёт роутера" }""",
            "core_install" => """{ "started": true }""",
            _ => "{}",
        }));
    }

    static string Do(Action a, string payload = "{}") { a(); return payload; }

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
    /// <summary>Подбор сам включил лучшую (06-router-spec §3.3).</summary>
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
