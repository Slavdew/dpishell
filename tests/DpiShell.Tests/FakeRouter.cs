using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace DpiShell.Tests;

/// <summary>
/// Роутер с DpiShell в памяти — по docs/dev/router-api.md (API 1). Ответы — примеры из §3–5 дословно;
/// поведение для случаев §7 включается полями. HttpListener на localhost работает без прав администратора.
/// </summary>
public sealed partial class FakeRouter : IDisposable
{
    HttpListener _l = new();
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
    /// <summary>rpcd перезапускается после установки/обновления: столько следующих вызовов dpishell — «объекта нет» (ubus 4).</summary>
    public int RpcdRestartCalls { get; set; }
    /// <summary>Столько вызовов dpishell «объекта нет» сразу после успешного обновления.</summary>
    public int RpcdRestartAfterUpdate { get; set; }
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
    /// <summary>Ответ security_check (§5.1, с 0.2.0-r13): незнакомый id и состояние — для проверки, что окно их переживает.</summary>
    public string SecurityJson { get; set; } = """{ "checks": [ { "id": "root_password", "state": "ok" }, { "id": "http_login", "state": "warn", "detail": "https_optional" }, { "id": "port_forwards", "state": "warn", "params": { "n": 2 } }, { "id": "dns_rebind", "state": "unknown" }, { "id": "future_check", "state": "warn" }, { "id": "upnp", "state": "strange" } ], "checked_at": "2026-10-22T20:00:00Z" }""";
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
    public string UpdateCheckedAt { get; set; } = "2026-10-17T14:00:00Z";
    /// <summary>Роутер не достаёт витрину: update_install feed заканчивается ошибкой.</summary>
    public bool FeedUnreachable { get; set; }
    /// <summary>Пакеты уже положены в /tmp/dpishell-update (иначе update_install local — invalid).</summary>
    public bool LocalUpdateReady { get; set; }
    /// <summary>status.update.local_signed (r12): роутер сам проверяет release.json(.sig) из папки обновления.</summary>
    public bool LocalSigned { get; set; }
    public int UpdateCheckCalls { get; private set; }
    /// <summary>Шаги обновления по опросам (job.step и хвост журнала log), затем итог с step: done; null — старый роутер: без шагов, один опрос «идёт».</summary>
    public string[]? UpdateStepsOnRouter { get; set; }
    /// <summary>Задание обновления висит в running и итог не пишет.</summary>
    public bool UpdateHangs { get; set; }
    /// <summary>При зависании пакет уже новый (версия сменилась).</summary>
    public bool UpdateHangNewVersion { get; set; }
    public bool? UninstallKeepSettings { get; private set; }
    /// <summary>После uninstall объект dpishell пропадает — все вызовы отвечают 4.</summary>
    public bool Uninstalled { get; private set; }
    /// <summary>После uninstall (через API) — тест убирает status.json и кладёт итог в /tmp/dpishell-uninstall.json подделки SSH.</summary>
    public Action? OnUninstall { get; set; }

    /// <summary>Пакет удалили: status.json пропал, объект dpishell не отвечает (как после uninstall.sh по SSH).</summary>
    public void Remove() { Uninstalled = true; StatusJson = null; }

    /// <summary>Пакет поставили заново (dpishell-install.sh): status.json и объект dpishell снова есть.</summary>
    public void Reinstall(string? statusJson = null)
    {
        Uninstalled = false;
        StatusJson = statusJson ?? $$"""{ "product": "dpishell-router", "api": {{StatusApi}}, "version": "{{RouterVersion}}", "engine": "zapret1", "core": "v72.13", "enabled": false, "strategy": "alt10", "catalog_version": 3 }""";
        Enabled = false;
    }

    /// <summary>
    /// status.core.installed_version (роутер 0.2.0-r8): null — поля нет (старый роутер); "none" — ядро не установлено
    /// (installed: false, installed_version: null); иначе версия, установленная в /opt/zapret.
    /// </summary>
    public string? CoreInstalledVersion { get; set; }
    /// <summary>status.packages (роутер 0.2.0-r8): null — поля нет (старый роутер); иначе версии пакетов, у luci пусто — «luci-app-dpishell»: null.</summary>
    public (string Dpishell, string? Luci)? Packages { get; set; }

    string CoreInstalledField() => CoreInstalledVersion is null ? "" : $", \"installed_version\": {(CoreInstalledVersion == "none" ? "null" : $"\"{CoreInstalledVersion}\"")}";
    string PackagesField() => Packages is not { } p ? ""
        : $" \"packages\": {{ \"dpishell\": \"{p.Dpishell}\", \"luci-app-dpishell\": {(p.Luci is null ? "null" : $"\"{p.Luci}\"")} }},";

    /// <summary>Как роутер 0.2.0-r8: версии пакетов и установленное ядро в status.</summary>
    public FakeRouter AsR8(string dpishell = "0.2.0-r8", string? luci = "0.2.0-r5", string core = "v72.13")
    {
        RouterVersion = dpishell; Packages = (dpishell, luci); CoreInstalledVersion = core;
        return this;
    }

    string? _updateFrom;
    int _updatePolls;
    string? _updateJob;

    /// <summary>Как роутер 0.2.0: api "1.0", methods (все, что знает подделка), min_app, update.</summary>
    public FakeRouter As020()
    {
        StatusApi = "\"1.0\""; RouterVersion = "0.2.0-r3"; MinApp = "0.9.0"; UpdateFieldOn = true; KillSwitch = true;
        MethodsJson = Methods020;
        return this;
    }

    /// <summary>status.methods роутера 0.2.0 — все методы, что знает подделка.</summary>
    public const string Methods020 = """["advanced_get","advanced_set","apply","check_now","confirm","conflict_stop","core_install","diag","disable","dns_analyze","dns_enable","dns_presets","dns_restore","dns_set","dns_status","enable","flow_offload_fix","ipv6_set","lists_get","lists_set","mode_set","pc_link_del","pc_link_get","pc_link_set","report","schedule_set","select_cancel","select_progress","select_start","site_check","site_check_result","status","strategies","strategies_user_set","strategy_set","targets_get","targets_set","uninstall","update_check","update_install"]""";

    /// <summary>status.methods роутера 0.2.0 до совместной работы (без pc_link_*).</summary>
    public const string MethodsNoPcLink = """["advanced_get","advanced_set","apply","check_now","confirm","conflict_stop","core_install","diag","disable","dns_analyze","dns_enable","dns_presets","dns_restore","dns_set","dns_status","enable","flow_offload_fix","ipv6_set","lists_get","lists_set","mode_set","report","schedule_set","select_cancel","select_progress","select_start","site_check","site_check_result","status","strategies","strategies_user_set","strategy_set","targets_get","targets_set","uninstall","update_check","update_install"]""";

    // ---------- аварийный выключатель (§5.1, §5.2): проба с confirm, last_off, сторож ----------

    /// <summary>Отдавать pending_confirm, last_off, watchdog и schedule.watchdog (роутер 0.2.0 со сторожем). Без него — как старый роутер.</summary>
    public bool KillSwitch { get; set; }
    /// <summary>Включение ждёт confirm: сколько секунд осталось (left_s) и что роутер вернёт сам (off или id стратегии).</summary>
    public (int LeftS, string RevertTo)? Pending { get; set; }
    public int ConfirmCalls { get; private set; }
    /// <summary>confirm_s последнего enable/strategy_set/apply (null — не передан).</summary>
    public int? LastConfirmS { get; private set; }
    public bool WatchdogEnabled { get; set; } = true;
    public bool WatchdogOk { get; set; } = true;
    public string WatchdogCheckedAt { get; set; } = "2026-10-19T12:00:01Z";
    /// <summary>status.last_off как JSON (null — поля нет / null).</summary>
    public string? LastOffJson { get; set; }
    /// <summary>Предупреждение выключения (watchdog_off, button_off, confirm_reverted) — до следующего включения.</summary>
    string? _offWarning;

    /// <summary>Время пробы вышло без confirm — роутер вернул прежнее сам (как по таймеру).</summary>
    public void ExpireConfirm()
    {
        if (Pending is not { } p) return;
        Pending = null;
        if (_advancedBefore is { } adv) { (AdvancedEnabled, AdvancedOthers, AdvancedRows) = adv; _advancedBefore = null; }
        if (p.RevertTo == "off")
        {
            Enabled = false;
            LastOffJson = """{ "by": "revert", "at": "2026-10-19T12:03:00Z", "text": "Включение не подтвердили — оптимизация выключена", "targets": [] }""";
        }
        else Strategy = p.RevertTo;
        _offWarning = """{ "code": "confirm_reverted", "text": "Включение не подтвердили — роутер вернул прежнее" }""";
    }

    /// <summary>Сторож: с обходом интернет пропал, без него есть — обход выключен (targets — что не открылось).</summary>
    public void WatchdogTrip(string at = "2026-10-19T11:32:00Z")
    {
        Enabled = false; Pending = null; WatchdogOk = false; WatchdogCheckedAt = at;
        LastOffJson = $$"""
            { "by": "watchdog", "at": "{{at}}", "text": "С оптимизацией интернет пропал, без оптимизации есть",
              "targets": [ { "url": "https://www.google.com/generate_204", "detail": "таймаут 5 с" }, { "url": "https://ya.ru/", "detail": "сброс соединения" } ] }
            """;
        _offWarning = """{ "code": "watchdog_off", "text": "Сторож выключил оптимизацию: с ним пропал интернет" }""";
    }

    /// <summary>status.watchdog.select как JSON (роутер 0.2.0-r8); null — поля нет (старый роутер). "null" — подбора после сторожа не было.</summary>
    public string? WatchdogSelectJson { get; set; }

    /// <summary>После срабатывания сторожа роутер сам подбирает (started_by: watchdog): идёт, ok: false.</summary>
    public void WatchdogSelectRunning()
    {
        WatchdogSelectJson = """{ "at": "2026-10-19T11:32:30Z", "ok": false, "best": null }""";
        SelectRunningJob = true; StartedBy = "watchdog";
    }

    /// <summary>Подбор после сторожа нашёл и включил стратегию (auto_selected); last_off у включённого — null.</summary>
    public void WatchdogSelectFound(string best = "alt10")
    {
        WatchdogSelectJson = $$"""{ "at": "2026-10-19T11:35:00Z", "ok": true, "best": "{{best}}" }""";
        SelectRunningJob = false; Enabled = true; Strategy = best;
        _offWarning = $$"""{ "code": "auto_selected", "text": "Роутер подобрал и включил {{best}}" }""";
    }

    /// <summary>Подбор после сторожа ничего не нашёл: обход выключен, warning watchdog_select_failed.</summary>
    public void WatchdogSelectFailed()
    {
        WatchdogSelectJson = """{ "at": "2026-10-19T11:36:00Z", "ok": false, "best": null }""";
        SelectRunningJob = false; Enabled = false;
        _offWarning = """{ "code": "watchdog_select_failed", "text": "Подбор после сторожа не нашёл стратегию — оптимизация выключена" }""";
    }

    /// <summary>Короткое нажатие кнопки reset на корпусе — обход выключен.</summary>
    public void ButtonOff()
    {
        Enabled = false; Pending = null;
        LastOffJson = """{ "by": "button", "at": "2026-10-19T11:40:00Z", "text": "Выключено кнопкой на корпусе", "targets": [] }""";
        _offWarning = """{ "code": "button_off", "text": "Оптимизация выключена кнопкой на корпусе" }""";
    }

    /// <summary>enable / strategy_set / apply с confirm_s при включённом сторожевом договоре — на пробу.</summary>
    void Trial(JsonObject args, bool wasEnabled, string wasStrategy)
    {
        LastConfirmS = args["confirm_s"]?.GetValue<int>();
        if (!KillSwitch || LastConfirmS is not { } s) return;
        if (!wasEnabled) Pending = (s, "off");
        else if (wasStrategy != Strategy) Pending = (s, wasStrategy);
    }

    void OnEnabled() { LastOffJson = null; _offWarning = null; }

    string KillSwitchFields()
    {
        if (!KillSwitch) return "";
        var pending = Pending is { } p
            ? $$"""{ "until": "2026-10-19T12:03:00Z", "left_s": {{p.LeftS}}, "revert_to": "{{p.RevertTo}}" }"""
            : "null";
        return $$"""
            , "pending_confirm": {{pending}}, "last_off": {{(Enabled ? "null" : LastOffJson ?? "null")}},
              "watchdog": { "enabled": {{(WatchdogEnabled ? "true" : "false")}}, "checked_at": "{{WatchdogCheckedAt}}", "ok": {{(WatchdogOk ? "true" : "false")}}, "interval_min": 5{{(WatchdogSelectJson is null ? "" : $", \"select\": {WatchdogSelectJson}")}} }
            """;
    }

    string Warnings()
    {
        if (_offWarning is null) return WarningsJson;
        var rest = WarningsJson.Trim();
        return rest == "[]" ? $"[{_offWarning}]" : $"[{_offWarning}, {rest[1..]}";
    }

    string Contract020() =>
        (MinApp is null ? "" : $" \"min_app\": \"{MinApp}\",") + (MethodsJson is null ? "" : $" \"methods\": {MethodsJson},");

    /// <summary>Перед каждым status: первый опрос после update_install — задание идёт, второй — готово (версия сменилась).</summary>
    void AdvanceUpdate()
    {
        _updateRunning = false;
        if (_updateFrom is not { } from) return;
        var steps = UpdateStepsOnRouter ?? [null!];
        int n = _updatePolls++;
        string StepField(string? step) => step is null ? "" : $$""", "step": "{{step}}", "log": [{{string.Join(", ", steps.Take(n + 1).Select(x => $"\"# {x}\""))}}]""";
        if (UpdateHangs || n < steps.Length)
        {
            _updateRunning = true;
            if (UpdateHangs && UpdateHangNewVersion) { RouterVersion = UpdateVersion; UpdateAvailable = false; }
            _updateJob = $$"""{ "action": "update", "from": "{{from}}", "running": true, "ok": null, "message": "Обновление", "at": null{{StepField(steps[Math.Min(n, steps.Length - 1)])}} }""";
            return;
        }
        _updateFrom = null;
        if (from == "feed" && FeedUnreachable)
        {
            _updateJob = $$"""{ "action": "update", "from": "feed", "running": false, "ok": false, "message": "Витрина недоступна", "at": "2026-10-17T14:05:00Z" }""";
            return;
        }
        if (_rollbackTarget is { } rt) { RollbackDone(rt, from); return; }
        RouterVersion = UpdateVersion; UpdateAvailable = false;
        RpcdRestartCalls = RpcdRestartAfterUpdate;   // после обновления rpcd перезапускается — dpishell ненадолго пропадает
        _updateJob = $$"""{ "action": "update", "from": "{{from}}", "running": false, "ok": true, "message": "Обновлено до {{UpdateVersion}}", "at": "2026-10-17T14:05:00Z"{{(UpdateStepsOnRouter is null ? "" : ", \"step\": \"done\"")}} }""";
    }

    bool _updateRunning;

    string UpdateField()
    {
        if (!UpdateFieldOn) return "";
        var notes = UpdateAvailable ? """["Глубокий поиск стратегий на самом роутере"]""" : "[]";
        return $$""", "update": { "available": {{(UpdateAvailable ? "true" : "false")}}, "version": "{{UpdateVersion}}", "notes": {{notes}}, "checked_at": "{{UpdateCheckedAt}}", "job": {{_updateJob ?? "null"}}{{(LocalSigned ? ", \"local_signed\": true" : "")}} }""";
    }

    public CallLog Calls { get; } = new();
    public List<string> RawBodies { get; } = [];
    readonly HashSet<string> _sessions = [];
    int _logins;
    public int Logins => _logins;

    public FakeRouter()
    {
        // Свободный порт: занимаем и отпускаем. Между «отпустили» и Start его может занять другой тест или другой прогон — берём другой.
        for (int attempt = 0; ; attempt++)
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Address = $"localhost:{port}";
            HttpPort = port;
            if (attempt > 0) { _l.Close(); _l = new HttpListener(); }   // после неудачного Start слушатель закрыт — нужен новый
            _l.Prefixes.Add($"http://localhost:{port}/");
            try { _l.Start(); break; }
            catch (HttpListenerException) when (attempt < 20) { }
        }
        _loop = Task.Run(LoopAsync);
    }

    async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _l.GetContextAsync(); } catch { return; }
            if (PlainRefused(ctx)) continue;
            try { if (!PlainRedirected(ctx)) await HandleAsync(ctx); }
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
        bool restarting = false;
        if (obj == "dpishell") lock (_sessions) if (RpcdRestartCalls > 0) { RpcdRestartCalls--; restarting = true; }
        if (obj != "dpishell" || MissingMethods.Contains(method) || Uninstalled || restarting) { await Write(ctx, Result(4)); return; }
        if (InvalidOn is { } inv && inv.Method == method)
        {
            await Write(ctx, Result(0, $$$"""{"error":{"code":"invalid","text":"Неверное значение","field":"{{{inv.Field}}}"}}"""));
            return;
        }
        if (ConflictOnEnable && method is "enable" or "select_start")
        {
            await Write(ctx, Result(0, """{"error":{"code":"conflict","text":"Работает чужой оптимизация"}}"""));
            return;
        }
        if (BusyOn == method) { await Write(ctx, Result(0, """{"error":{"code":"busy","text":"Идёт подбор"}}""")); return; }
        if (method == "select_progress" && Interlocked.Decrement(ref _progressErrors) >= 0)
        {
            await Write(ctx, Result(0, """{"error":{"code":"internal","text":"Подбор ещё не поднялся"}}"""));
            return;
        }
        if (method == "select_progress" && Interlocked.Decrement(ref _progressZeros) >= 0)
        {
            await Write(ctx, Result(0, """{ "running": true, "started_by": "app", "depth": "main", "index": 0, "total": 0, "eta_s": null, "message": "", "rows": [], "finished_at": null, "cancelled": false, "best": null, "applied": false, "backups": [] }"""));
            return;
        }

        if (method == "status") AdvanceUpdate();
        await Write(ctx, Result(0, method switch
        {
            "status" => $$"""
                { "api": {{StatusApi}}, "version": "{{RouterVersion}}", "engine": "zapret1",{{Contract020()}}
                  "core": { "version": "v72.13", "installed": {{(CoreInstalledVersion == "none" ? "false" : "true")}}, "hash_ok": true{{CoreInstalledField()}} },{{PackagesField()}}
                  "enabled": {{(Enabled ? "true" : "false")}}, "mode": "{{Mode}}", "strategy": "{{Strategy}}", "backups": ["alt9", "alt13"], "needs_tcp_timestamps": {{(NeedsTcpTimestamps ? "true" : "false")}},
                  "last_check": { "at": "{{CheckedAt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture)}}", "source": "timer", "ok": true,
                                  "groups": [ { "id": "youtube", "ok": true }, { "id": "discord", "ok": true } ] },
                  "fail_streak": 0,
                  "schedule": { "enabled": {{(ScheduleEnabled ? "true" : "false")}}, "hours": {{ScheduleHours.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "auto_select": {{(AutoSelect ? "true" : "false")}}{{(KillSwitch ? $", \"watchdog\": {(WatchdogEnabled ? "true" : "false")}" : "")}} },
                  "jobs": { "select": {{(SelectRunningJob ? "true" : "false")}}, "check": false, "core_install": false, "dns_analyze": false, "site_check": false, "dns": false, "update": {{(_updateRunning ? "true" : "false")}} },
                  "warnings": {{Warnings()}}{{UpdateField()}}{{KillSwitchFields()}}{{AdvancedField()}}{{PcLinksField()}}{{RollbackField()}} }
                """,
            "strategies" => StrategiesJson,
            "advanced_get" => AdvancedGet(),
            "advanced_set" => AdvancedSet(args),
            "enable" => Do(() => { var was = Enabled; Enabled = true; OnEnabled(); Trial(args, was, Strategy); }),
            "disable" => Do(() => { Enabled = false; Pending = null; }),
            "confirm" => Do(() => { ConfirmCalls++; Pending = null; _advancedBefore = null; }),
            "strategy_set" => Do(() => { var was = Strategy; Strategy = args["id"]!.GetValue<string>(); Trial(args, Enabled, was); }),
            "check_now" => Do(() => CheckedAt = CheckedAt.AddMinutes(1), """{ "started": true }"""),
            // §9 (2026-10-17): только «Свои сайты», а их список пуст — invalid, field groups.
            "select_start" when args["groups"] is System.Text.Json.Nodes.JsonArray g && g.Count == 1 && g[0]!.GetValue<string>() == "user" && UserTargets.Count == 0 =>
                """{"error":{"code":"invalid","text":"Список своих сайтов на роутере пуст — добавьте адреса или выберите другие группы","field":"groups"}}""",
            "select_start" => Step0("""{ "started": true }"""),
            "targets_set" => Do(() => UserTargets = args["items"]!.AsArray().Select(x => (x!["url"]!.GetValue<string>(), x["important"]?.GetValue<bool>() ?? false)).ToList()),
            "select_cancel" => Do(() => { _cancelled = true; SelectRunningJob = false; }),
            "apply" => Do(() => { var was = Strategy; Strategy = args["id"]!.GetValue<string>(); Trial(args, Enabled, was); }),
            "schedule_set" => Do(() =>
            {
                ScheduleEnabled = args["enabled"]!.GetValue<bool>(); ScheduleHours = args["hours"]!.GetValue<double>();
                AutoSelect = args["auto_select"]?.GetValue<bool>() ?? false;
                if (args["watchdog"] is { } wd) WatchdogEnabled = wd.GetValue<bool>();
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
                : args["name"]?.GetValue<string>() is "google" or "discord" or "general" && !BuiltinListsOn
                    ? """{"error":{"code":"invalid","text":"Нет такого списка","field":"name"}}"""
                : args["name"]?.GetValue<string>() is { } builtin && Builtin.TryGetValue(builtin, out var bl)
                    ? $$"""{ "lines": {{System.Text.Json.JsonSerializer.Serialize(bl)}}, "editable": false }"""
                : $$"""{ "lines": {{System.Text.Json.JsonSerializer.Serialize(Lists.GetValueOrDefault(args["name"]!.GetValue<string>()) ?? ["example.com"])}}, "editable": true }""",
            "pc_link_set" => PcLinkSet(args),
            "pc_link_del" => Do(() => { PcLinkDelCalls++; Links.Remove(args["id"]!.GetValue<string>()); }),
            "pc_link_get" => PcLinkGet(),
            "diag" => $$"""{ "conflicts": {{ConflictsJson}}, "flow_offload": {{(FlowOffloadFixed ? "false" : "true")}}, "flowoffload": "{{(FlowOffloadFixed ? "software" : "none")}}","ipv6_wan": true, "dns": { "spoofed": true, "checked_at": "2026-10-16T21:00:00Z" }, "overlay_free_kb": 18000 }""",
            "report" => """{ "text": "отчёт роутера" }""",
            "security_check" => SecurityJson,
            "changes_list" => ChangesList(),
            "changes_undo" => ChangesUndo(args),
            "changes_undo_all" => ChangesUndo(null),
            "update_rollback" => RollbackCall(args),
            "core_install" => """{ "started": true }""",
            // Ядро договора §5.4.
            "uninstall" => Do(() => { UninstallKeepSettings = args["keep_settings"]?.GetValue<bool>(); Uninstalled = true; OnUninstall?.Invoke(); }, """{ "started": true }"""),
            "update_install" when args["from"]?.GetValue<string>() is not ("feed" or "local") =>
                """{"error":{"code":"invalid","text":"from: feed или local","field":"from"}}""",
            "update_install" when args["from"]!.GetValue<string>() == "local" && !LocalUpdateReady =>
                """{"error":{"code":"invalid","text":"Нет пакетов в /tmp/dpishell-update","field":"from"}}""",
            "update_install" => Do(() => { _updateFrom = args["from"]!.GetValue<string>(); _updatePolls = 0; }, """{ "started": true }"""),
            "update_check" => Do(() => { UpdateCheckCalls++; UpdateCheckedAt = "2026-10-17T15:00:00Z"; }, """{ "started": true }"""),
            _ => "{}",
        }));
    }

    static string Do(Action a, string payload = "{}") { a(); return payload; }

    /// <summary>«Свои сайты» роутера (targets_get/targets_set); по умолчанию — пример §5.1.</summary>
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

    // ---------- продвинутый режим (§5.1 advanced_get, §5.2 advanced_set; роутер 0.2.0-r9) ----------

    /// <summary>Отдавать status.advanced (роутер r9).</summary>
    public bool AdvancedField_On { get; set; }
    public bool AdvancedEnabled { get; private set; }
    public string AdvancedOthers { get; private set; } = "lists";
    public List<(List<string> Domains, string Strategy)> AdvancedRows { get; private set; } = [];
    /// <summary>Что advanced_set получил последним (JSON args) — для проверки того, что ушло на роутер.</summary>
    public JsonObject? LastAdvancedSet { get; private set; }
    (bool Enabled, string Others, List<(List<string> Domains, string Strategy)> Rows)? _advancedBefore;

    string AdvancedField() => AdvancedField_On ? $", \"advanced\": {{ \"enabled\": {(AdvancedEnabled ? "true" : "false")}, \"rows\": {AdvancedRows.Count} }}" : "";

    string AdvancedGet()
    {
        var rows = AdvancedRows.Select(r => new JsonObject
        {
            ["domains"] = new JsonArray(r.Domains.Select(d => (JsonNode)d).ToArray()), ["strategy"] = r.Strategy,
            ["resolved"] = r.Strategy == "direct" ? null : r.Strategy == "auto" ? Strategy : r.Strategy,
        });
        return new JsonObject { ["enabled"] = AdvancedEnabled, ["others"] = AdvancedOthers, ["rows"] = new JsonArray(rows.ToArray()) }.ToJsonString();
    }

    static string AdvancedInvalid(string field, string text) =>
        new JsonObject { ["error"] = new JsonObject { ["code"] = "invalid", ["text"] = text, ["field"] = field } }.ToJsonString();

    static readonly System.Text.RegularExpressions.Regex DomainRx = new(@"^([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$");

    string AdvancedSet(JsonObject args)
    {
        if (args["enabled"] is not JsonValue ev || !ev.TryGetValue<bool>(out var enabled)) return AdvancedInvalid("enabled", "enabled: true/false");
        var others = args["others"]?.GetValue<string>();
        if (others is not ("direct" or "lists" or "all")) return AdvancedInvalid("others", "Остальные адреса: direct, lists или all");
        if (args["rows"] is not JsonArray rows || rows.Count > 20) return AdvancedInvalid("rows", "rows: список, не больше 20 строк");
        var known = System.Text.Json.JsonDocument.Parse(StrategiesJson).RootElement.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetString()).ToHashSet();
        var list = new List<(List<string>, string)>();
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i] is not JsonObject r || r["domains"] is not JsonArray ds || ds.Count is 0 or > 50) return AdvancedInvalid($"rows[{i}].domains", "В строке от 1 до 50 сайтов");
            var doms = new List<string>();
            for (int j = 0; j < ds.Count; j++)
            {
                var d = ds[j]?.GetValue<string>() ?? "";
                if (!DomainRx.IsMatch(d)) return AdvancedInvalid($"rows[{i}].domains[{j}]", "Домен вида example.com");
                doms.Add(d);
            }
            var sid = r["strategy"]?.GetValue<string>() ?? "auto";
            if (sid is not ("auto" or "direct") && !known.Contains(sid)) return AdvancedInvalid($"rows[{i}].strategy", "Стратегия: auto, direct или id из списка");
            list.Add((doms, sid));
        }
        LastAdvancedSet = (JsonObject)args.DeepClone();
        var before = (AdvancedEnabled, AdvancedOthers, AdvancedRows);
        AdvancedEnabled = enabled; AdvancedOthers = others; AdvancedRows = list;
        LastConfirmS = args["confirm_s"]?.GetValue<int>();
        if (Enabled && KillSwitch && LastConfirmS is { } s) { Pending = (s, Strategy); _advancedBefore ??= before; }
        return "{}";
    }

    // ---------- совместная работа (§5.2 pc_link_set/del/get, §5.1 lists_get встроенные; роутер 0.2.0-r9) ----------

    /// <summary>Отдавать status.pc_links (роутер r9).</summary>
    public bool PcLinksField_On { get; set; } = true;
    /// <summary>lists_get отдаёт встроенные google, discord, general (r9); иначе — invalid, как роутер до r9.</summary>
    public bool BuiltinListsOn { get; set; } = true;
    /// <summary>Встроенные списки ролей роутера (только чтение).</summary>
    public Dictionary<string, List<string>> Builtin { get; } = new()
    {
        ["google"] = ["youtube.com", "googlevideo.com", "ytimg.com", "play.google.com", "google.ru"],
        ["discord"] = ["discord.com", "discord.gg", "discordapp.com", "discord.media"],
        ["general"] = ["rutracker.org", "example.net"],
    };

    public sealed class PcLinkRow
    {
        public string Id { get; set; } = "";
        public List<string> Macs { get; set; } = [];
        public List<string> Domains { get; set; } = [];
        public List<string> Ignored { get; set; } = [];
        public DateTime ExpiresAt { get; set; }
        public bool Expired { get; set; }
    }

    /// <summary>Связки ПК на роутере по id.</summary>
    public Dictionary<string, PcLinkRow> Links { get; } = [];
    public int PcLinkSetCalls { get; private set; }
    public int PcLinkDelCalls { get; private set; }
    /// <summary>Последние аргументы pc_link_set — как пришли на роутер.</summary>
    public JsonObject? LastPcLinkSet { get; private set; }
    /// <summary>Роутер отказывает в pc_link_set (failed) — продление не удаётся.</summary>
    public bool PcLinkSetFails { get; set; }
    /// <summary>Срок связки с момента pc_link_set (на стенде — 24 ч).</summary>
    public TimeSpan PcLinkTtl { get; set; } = TimeSpan.FromHours(24);
    /// <summary>Читать ли status.pc_links как число связок.</summary>
    string PcLinksField() => PcLinksField_On ? $", \"pc_links\": {Links.Count}" : "";

    /// <summary>Домены, которыми владеет роутер: встроенные google и discord и строки его продвинутого режима (договор §5.2 pc_link_set).</summary>
    IEnumerable<string> RouterOwnedNames() => Builtin["google"].Concat(Builtin["discord"]).Concat(AdvancedEnabled ? AdvancedRows.SelectMany(r => r.Domains) : []);

    static bool Related(string a, string b) => a == b || a.EndsWith("." + b) || b.EndsWith("." + a);

    string PcLinkSet(JsonObject args)
    {
        PcLinkSetCalls++;
        LastPcLinkSet = (JsonObject)args.DeepClone();
        if (PcLinkSetFails) return """{"error":{"code":"failed","text":"Не удалось сохранить связку"}}""";
        var id = args["id"]?.GetValue<string>() ?? "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Za-z0-9-]{8,64}$")) return AdvancedInvalid("id", "id: 8–64 знака");
        if (args["macs"] is not JsonArray macs || macs.Count is 0 or > 8) return AdvancedInvalid("macs", "macs: от 1 до 8 адресов");
        for (int i = 0; i < macs.Count; i++)
            if (!System.Text.RegularExpressions.Regex.IsMatch(macs[i]?.GetValue<string>() ?? "", "^([0-9a-fA-F]{2}[:-]){5}[0-9a-fA-F]{2}$")) return AdvancedInvalid($"macs[{i}]", "MAC вида aa:bb:cc:dd:ee:ff");
        if (args["domains"] is not JsonArray ds || ds.Count > 500) return AdvancedInvalid("domains", "domains: до 500 имён");
        var doms = new List<string>();
        for (int i = 0; i < ds.Count; i++)
        {
            var d = ds[i]?.GetValue<string>() ?? "";
            if (!DomainRx.IsMatch(d)) return AdvancedInvalid($"domains[{i}]", "Домен вида example.com");
            doms.Add(d);
        }
        var owned = RouterOwnedNames().ToList();
        var ignored = doms.Where(d => owned.Any(o => Related(d, o))).ToList();
        if (!Links.TryGetValue(id, out var row) && Links.Count >= 4) return AdvancedInvalid("id", "Не больше 4 ПК");
        row ??= Links[id] = new PcLinkRow { Id = id };
        row.Macs = [.. macs.Select(m => m!.GetValue<string>().ToLowerInvariant().Replace('-', ':'))];
        row.Domains = [.. doms.Where(d => !ignored.Contains(d))];
        row.Ignored = ignored;
        row.ExpiresAt = DateTime.UtcNow + PcLinkTtl;
        row.Expired = false;
        return new JsonObject { ["expires_at"] = row.ExpiresAt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), ["ignored"] = new JsonArray(ignored.Select(x => (JsonNode)x).ToArray()) }.ToJsonString();
    }

    string PcLinkGet()
    {
        var items = Links.Values.Select(r => new JsonObject
        {
            ["id"] = r.Id, ["macs"] = new JsonArray(r.Macs.Select(m => (JsonNode)m).ToArray()), ["domains"] = r.Domains.Count, ["ignored"] = r.Ignored.Count,
            ["seen_at"] = "2026-10-20T12:00:00Z", ["expires_at"] = r.ExpiresAt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            ["active"] = !r.Expired && r.ExpiresAt > DateTime.UtcNow && Enabled,
        });
        return new JsonObject { ["items"] = new JsonArray(items.ToArray()) }.ToJsonString();
    }

    /// <summary>Связка без продления 24 ч перестала действовать (active: false).</summary>
    public void ExpireLinks() { foreach (var r in Links.Values) r.Expired = true; }

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
    int _progressErrors, _progressZeros;
    /// <summary>Первые n ответов select_progress — ошибка (подбор ещё не поднялся).</summary>
    public int ProgressErrors { set => _progressErrors = value; }
    /// <summary>Первые n ответов select_progress — «идёт, 0 из 0» (как роутер r7).</summary>
    public int ProgressZeros { set => _progressZeros = value; }
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
        StopTls();
        _l.Stop();
        _l.Close();
    }
}

/// <summary>Журнал вызовов фейка: пишет поток сервера, читает тест — перебор идёт по снимку под замком (иначе «Collection was modified» под нагрузкой).</summary>
public sealed class CallLog : IList<JsonObject>
{
    readonly List<JsonObject> _l = [];
    List<JsonObject> Snap() { lock (this) return [.. _l]; }
    public IEnumerator<JsonObject> GetEnumerator() => Snap().GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    public void Add(JsonObject item) { lock (this) _l.Add(item); }
    public void Clear() { lock (this) _l.Clear(); }
    public bool Contains(JsonObject item) { lock (this) return _l.Contains(item); }
    public void CopyTo(JsonObject[] array, int arrayIndex) { lock (this) _l.CopyTo(array, arrayIndex); }
    public bool Remove(JsonObject item) { lock (this) return _l.Remove(item); }
    public int Count { get { lock (this) return _l.Count; } }
    public bool IsReadOnly => false;
    public int IndexOf(JsonObject item) { lock (this) return _l.IndexOf(item); }
    public void Insert(int index, JsonObject item) { lock (this) _l.Insert(index, item); }
    public void RemoveAt(int index) { lock (this) _l.RemoveAt(index); }
    public JsonObject this[int index] { get { lock (this) return _l[index]; } set { lock (this) _l[index] = value; } }
}
