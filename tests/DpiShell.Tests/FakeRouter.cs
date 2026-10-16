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

    // Подбор: select_progress идёт от running к best/restore_at
    public int SelectSteps { get; set; } = 3;
    int _selectStep = -1;

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
        if (BusyOn == method) { await Write(ctx, Result(0, """{"error":{"code":"busy","text":"Идёт подбор"}}""")); return; }

        await Write(ctx, Result(0, method switch
        {
            "status" => """
                { "api": 1, "version": "0.1.0", "engine": "zapret1",
                  "core": { "version": "v72.13", "installed": true, "hash_ok": true },
                  "enabled": true, "strategy": "alt10", "backups": ["alt9", "alt13"],
                  "last_check": { "at": "2026-10-16T21:00:00Z", "source": "timer", "ok": true,
                                  "groups": [ { "id": "youtube", "ok": true }, { "id": "discord", "ok": true } ] },
                  "fail_streak": 0,
                  "schedule": { "enabled": true, "hours": 6 },
                  "jobs": { "select": false, "check": false, "core_install": false },
                  "warnings": [ { "code": "flow_offload", "text": "Включён flow offloading" } ] }
                """,
            "strategies" => """{ "catalog_version": 3, "items": [ { "id": "alt10", "title": "general (ALT10)", "source": "catalog", "runs": 7, "passed": 6 } ] }""",
            "select_start" => Step0("""{ "started": true }"""),
            "select_progress" => Progress(),
            "targets_get" => """{ "items": [ { "url": "https://example.com/", "important": true } ] }""",
            "lists_get" => """{ "lines": ["example.com"] }""",
            "diag" => """{ "conflicts": [ { "kind": "service", "name": "zapret2", "running": true } ], "flow_offload": false, "ipv6_wan": false, "dns": { "spoofed": true, "checked_at": "2026-10-16T21:00:00Z" }, "overlay_free_kb": 18000 }""",
            "report" => """{ "text": "отчёт роутера" }""",
            "check_now" or "core_install" => """{ "started": true }""",
            _ => "{}",
        }));
    }

    string Step0(string payload) { _selectStep = -1; return payload; }

    /// <summary>Первые вызовы — running: true; последний — best и restore_at (§5.1).</summary>
    string Progress()
    {
        var step = Interlocked.Increment(ref _selectStep);
        if (step < SelectSteps)
            return $$"""
                { "running": true, "index": {{step + 1}}, "total": 20, "message": "alt3: YouTube",
                  "rows": [ { "id": "alt", "group_share": 1.0, "target_share": 0.88, "avg_s": 0.74, "error": null } ],
                  "finished_at": null, "best": null, "restore_at": null }
                """;
        return """
            { "running": false, "index": 20, "total": 20, "message": "",
              "rows": [ { "id": "alt", "group_share": 1.0, "target_share": 0.88, "avg_s": 0.74, "error": null } ],
              "finished_at": "2026-10-16T21:05:00Z", "best": "alt", "restore_at": "2026-10-16T21:15:00Z" }
            """;
    }

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
