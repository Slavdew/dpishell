using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DpiShell.Core.Router;

/// <summary>
/// Управление DpiShell на роутере по договору docs/07-router-api.md (API 1): JSON-RPC 2.0 через rpcd/ubus,
/// те же методы, что вызывает страница в меню роутера. Пароль сюда приходит открытым только в памяти —
/// хранение через <see cref="RouterSecrets"/>, в логи и исключения не попадает.
/// </summary>
public sealed class RouterClient : IDisposable
{
    /// <summary>Мажор договора, который понимает приложение; минор — сколько добавлений оно знает (разница — предупреждение, не запрет).</summary>
    public static readonly ApiVersion SupportedApi = new(1, 0);
    const string NullSession = "00000000000000000000000000000000";

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    readonly HttpClient _http;
    readonly bool _ownsHttp;
    readonly Uri _base;
    string? _session;
    Func<string>? _password;
    int _id;

    /// <param name="address">IP или имя роутера без схемы и пути (договор §2.1); для тестов — «127.0.0.1:порт».</param>
    public RouterClient(string address, HttpClient? http = null)
    {
        Address = address.Trim().TrimEnd('/');
        if (Address.Contains("://") || Address.Contains('/')) throw new ArgumentException("Адрес роутера — без схемы и пути, например 192.168.1.1");
        _base = new Uri($"http://{Address}/");
        _ownsHttp = http is null;
        _http = http ?? new HttpClient(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(3) });
    }

    public string Address { get; }
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(10);   // §4: таймаут HTTP 10 с
    public TimeSpan DetectTimeout { get; init; } = TimeSpan.FromSeconds(1);  // §2.2: 1 с

    // ---------- §2 поиск ----------

    /// <summary>Основной шлюз IPv4 адаптера, через который идёт интернет (договор §2.1).</summary>
    public static string? DefaultGateway()
    {
        IPAddress? outbound = null;
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(new IPEndPoint(IPAddress.Parse("1.1.1.1"), 53));
            outbound = (s.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (SocketException) { }
        var adapters = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up).ToList();
        var preferred = adapters.Where(n => outbound is not null && n.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(outbound)))
            .Concat(adapters);
        foreach (var n in preferred)
        {
            var gw = n.GetIPProperties().GatewayAddresses.Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
            if (gw is not null) return gw.ToString();
        }
        return null;
    }

    /// <summary>GET /dpishell/status.json без входа: есть ли DpiShell и совместим ли API.</summary>
    public async Task<RouterDetection> DetectAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(DetectTimeout);
            using var resp = await _http.GetAsync(new Uri(_base, "dpishell/status.json"), cts.Token);
            if (!resp.IsSuccessStatusCode) return new(Address, RouterFound.NotFound, null);
            var st = await resp.Content.ReadFromJsonAsync<RouterPublicStatus>(Json, cts.Token);
            if (st?.Product != "dpishell-router") return new(Address, RouterFound.NotFound, null);
            // Без поля api — пакет старше договора (вопрос 1 в 07-router-api.md).
            // Совместимость решает мажор; минор только для предупреждения и списка методов.
            var found = st.Api is not { } api || api.Major < SupportedApi.Major ? RouterFound.RouterTooOld
                      : api.Major > SupportedApi.Major ? RouterFound.AppTooOld : RouterFound.Ok;
            return new(Address, found, st);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException && !ct.IsCancellationRequested)
        {
            return new(Address, RouterFound.NotFound, null);
        }
    }

    // ---------- §3 вход ----------

    /// <summary>Вход. Пароль передаётся функцией, чтобы держать его открытым как можно меньше и войти заново при коде 6.</summary>
    public async Task LoginAsync(Func<string> password, CancellationToken ct = default)
    {
        _password = password;
        _session = null;
        var res = await RpcAsync(NullSession, "session", "login",
            new JsonObject { ["username"] = "root", ["password"] = password(), ["timeout"] = 600 }, ct);
        if (res.Code == 6) throw new RouterException(RouterFailure.AccessDenied, "Неверный пароль роутера", ubusCode: 6);
        EnsureOk(res, "login");
        _session = res.Body?["ubus_rpc_session"]?.GetValue<string>()
                   ?? throw new RouterException(RouterFailure.Protocol, "Роутер не вернул сессию");
    }

    public bool LoggedIn => _session is not null;

    // ---------- §4 вызов ----------

    async Task<JsonObject?> CallAsync(string method, JsonObject? args, CancellationToken ct)
    {
        if (_session is null) throw new RouterException(RouterFailure.AccessDenied, "Нет входа на роутер");
        var res = await RpcAsync(_session, "dpishell", method, args ?? new JsonObject(), ct);
        if (res.Code == 6 && _password is not null)
        {
            // Сессия истекла — войти заново один раз (§3).
            await LoginAsync(_password, ct);
            res = await RpcAsync(_session!, "dpishell", method, args ?? new JsonObject(), ct);
            if (res.Code == 6) throw new RouterException(RouterFailure.AccessDenied, "Неверный пароль или сессия роутера", ubusCode: 6);
        }
        EnsureOk(res, method);
        if (res.Body?["error"] is JsonObject err)
        {
            var e = new RouterAppError(err["code"]?.GetValue<string>() ?? "failed", err["text"]?.GetValue<string>(), err["field"]?.GetValue<string>());
            throw new RouterException(RouterFailure.App, e.Text ?? AppErrorText(e.Code), e);
        }
        return res.Body;
    }

    async Task<T> CallAsync<T>(string method, JsonObject? args, CancellationToken ct)
    {
        var body = await CallAsync(method, args, ct);
        try { return body.Deserialize<T>(Json) ?? throw new JsonException("пустой ответ"); }
        catch (JsonException e) { throw new RouterException(RouterFailure.Protocol, $"Ответ роутера на {method} не по договору: {e.Message}"); }
    }

    static void EnsureOk((int Code, JsonObject? Body) res, string method)
    {
        switch (res.Code)
        {
            case 0: return;
            case 2: throw new RouterException(RouterFailure.InvalidArgs, $"Роутер: неверные аргументы ({method})", ubusCode: 2);
            case 4: throw new RouterException(RouterFailure.NotSupported, "На роутере нет нужной функции — обновите пакет DpiShell на роутере", ubusCode: 4);
            case 6: throw new RouterException(RouterFailure.AccessDenied, "Нет доступа к роутеру", ubusCode: 6);
            case 5 or 7: throw new RouterException(RouterFailure.NoResponse, "Роутер не ответил вовремя", ubusCode: res.Code);
            default: throw new RouterException(RouterFailure.Protocol, $"Роутер вернул код {res.Code} ({method})", ubusCode: res.Code);
        }
    }

    static string AppErrorText(string code) => code switch
    {
        "busy" => "Роутер занят: идёт подбор или проверка",
        "not_installed" => "На роутере не загружено ядро обхода",
        "invalid" => "Роутер отклонил данные",
        "conflict" => "На роутере работает другой обход",
        "core_mismatch" => "Файлы ядра на роутере повреждены",
        _ => "Ошибка на роутере",
    };

    /// <summary>JSON-RPC «call» → (код ubus, тело). Ошибку JSON-RPC «Access denied» (-32002, так отвечает uhttpd-mod-ubus) считаем кодом 6 — вопрос 2.</summary>
    async Task<(int Code, JsonObject? Body)> RpcAsync(string session, string obj, string method, JsonObject args, CancellationToken ct)
    {
        var req = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = Interlocked.Increment(ref _id),
            ["method"] = "call",
            ["params"] = new JsonArray(session, obj, method, args),
        };
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(CallTimeout);
            using var content = new StringContent(req.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(new Uri(_base, "ubus"), content, cts.Token);
            if (!resp.IsSuccessStatusCode) throw new RouterException(RouterFailure.Unreachable, $"Роутер ответил HTTP {(int)resp.StatusCode}");
            var root = JsonNode.Parse(await resp.Content.ReadAsStringAsync(cts.Token)) as JsonObject
                       ?? throw new RouterException(RouterFailure.Protocol, "Ответ роутера — не JSON-объект");
            if (root["error"] is JsonObject rpcErr)
                return rpcErr["code"]?.GetValue<int>() == -32002 ? (6, null)
                    : throw new RouterException(RouterFailure.Protocol, "Роутер: " + (rpcErr["message"]?.GetValue<string>() ?? "ошибка JSON-RPC"));
            if (root["result"] is not JsonArray result || result.Count == 0)
                throw new RouterException(RouterFailure.Protocol, "Ответ роутера без result");
            return (result[0]!.GetValue<int>(), result.Count > 1 ? result[1] as JsonObject : null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // В тексте нет ни запроса, ни пароля — только что роутер недоступен.
            throw new RouterException(RouterFailure.Unreachable, "Роутер недоступен");
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            throw new RouterException(RouterFailure.Protocol, "Ответ роутера не по договору");
        }
    }

    // ---------- §5.1 чтение ----------

    public Task<RouterStatus> StatusAsync(CancellationToken ct = default) => CallAsync<RouterStatus>("status", null, ct);
    public Task<RouterStrategies> StrategiesAsync(CancellationToken ct = default) => CallAsync<RouterStrategies>("strategies", null, ct);
    public Task<RouterSelectProgress> SelectProgressAsync(CancellationToken ct = default) => CallAsync<RouterSelectProgress>("select_progress", null, ct);
    public Task<RouterTargets> TargetsGetAsync(CancellationToken ct = default) => CallAsync<RouterTargets>("targets_get", null, ct);
    /// <summary>name: user-general, user-exclude или exclude (стандартные исключения, только чтение).</summary>
    public Task<RouterLines> ListsGetAsync(string name, CancellationToken ct = default) =>
        CallAsync<RouterLines>("lists_get", new JsonObject { ["name"] = name == "exclude" ? name : CheckListName(name) }, ct);
    public Task<RouterSiteCheckResult> SiteCheckResultAsync(CancellationToken ct = default) => CallAsync<RouterSiteCheckResult>("site_check_result", null, ct);
    public Task<RouterDnsStatus> DnsStatusAsync(CancellationToken ct = default) => CallAsync<RouterDnsStatus>("dns_status", null, ct);
    public Task<RouterDiag> DiagAsync(CancellationToken ct = default) => CallAsync<RouterDiag>("diag", null, ct);
    public Task<RouterReport> ReportAsync(CancellationToken ct = default) => CallAsync<RouterReport>("report", null, ct);

    // ---------- §5.2 действия ----------

    public Task EnableAsync(CancellationToken ct = default) => CallAsync("enable", null, ct);
    public Task DisableAsync(CancellationToken ct = default) => CallAsync("disable", null, ct);
    public Task StrategySetAsync(string id, CancellationToken ct = default) => CallAsync("strategy_set", new JsonObject { ["id"] = id }, ct);
    public Task<RouterStarted> CheckNowAsync(CancellationToken ct = default) => CallAsync<RouterStarted>("check_now", null, ct);

    public Task<RouterStarted> SelectStartAsync(IEnumerable<string> groups, bool full, bool deep, CancellationToken ct = default) =>
        CallAsync<RouterStarted>("select_start", new JsonObject
        {
            ["groups"] = new JsonArray(groups.Select(g => (JsonNode)g).ToArray()),
            ["mode"] = full ? "full" : "quick",
            ["depth"] = deep ? "deep" : "main",
        }, ct);

    public Task SelectCancelAsync(CancellationToken ct = default) => CallAsync("select_cancel", null, ct);

    /// <summary>`apply` (редакция §9): выбрать другую стратегию из итога подбора вместо автоматически включённой.</summary>
    public Task ApplyAsync(string id, IEnumerable<string> backups, CancellationToken ct = default) =>
        CallAsync("apply", new JsonObject { ["id"] = id, ["backups"] = new JsonArray(backups.Select(b => (JsonNode)b).ToArray()) }, ct);

    /// <param name="autoSelect">При неудаче проверки и исчерпанных запасных роутер сам запускает быстрый подбор (§9, вопрос 7).</param>
    public Task ScheduleSetAsync(bool enabled, double hours, bool autoSelect = false, CancellationToken ct = default)
    {
        // §9: добавлено 0.5 (раз в 30 мин).
        if (hours is not (0.5 or 1 or 3 or 6 or 12 or 24)) throw new ArgumentOutOfRangeException(nameof(hours), "Часы проверки: 0.5, 1, 3, 6, 12 или 24");
        return CallAsync("schedule_set", new JsonObject { ["enabled"] = enabled, ["hours"] = hours, ["auto_select"] = autoSelect }, ct);
    }

    /// <summary>«Все сайты, кроме исключений» (all) / «По спискам» (lists); роутер перезапускает zapret и проверяет с контрольной группой.</summary>
    public Task<RouterStarted> ModeSetAsync(bool allSites, CancellationToken ct = default) =>
        CallAsync<RouterStarted>("mode_set", new JsonObject { ["mode"] = allSites ? "all" : "lists" }, ct);

    /// <summary>«Сайт не открывается с обходом»: проверка с обходом и без, итог — <see cref="SiteCheckResultAsync"/>. Голое имя роутер дополнит сам.</summary>
    public Task<RouterStarted> SiteCheckAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2048 || url.Any(char.IsWhiteSpace)) throw new ArgumentException("Адрес сайта — одной строкой без пробелов");
        return CallAsync<RouterStarted>("site_check", new JsonObject { ["url"] = url.Trim() }, ct);
    }

    public Task<RouterStarted> DnsAnalyzeAsync(CancellationToken ct = default) => CallAsync<RouterStarted>("dns_analyze", null, ct);
    public Task<RouterStarted> DnsEnableAsync(CancellationToken ct = default) => CallAsync<RouterStarted>("dns_enable", null, ct);
    public Task<RouterStarted> DnsRestoreAsync(CancellationToken ct = default) => CallAsync<RouterStarted>("dns_restore", null, ct);

    /// <summary>Варианты DNS для всей сети (§5.1, §9 2026-10-17).</summary>
    public Task<RouterDnsPresets> DnsPresetsAsync(CancellationToken ct = default) => CallAsync<RouterDnsPresets>("dns_presets", null, ct);

    /// <summary>Включить готовый вариант (фоново, итог — dns_status.job с action dns_set; не заработал — роутер вернёт прежний).</summary>
    public Task<RouterStarted> DnsSetAsync(string preset, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(preset) || preset.Length > 32 || !preset.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'))
            throw new ArgumentException("Нет такого варианта DNS");
        return CallAsync<RouterStarted>("dns_set", new JsonObject { ["preset"] = preset }, ct);
    }

    /// <summary>Свой вариант: с шаблоном DoH адреса уходят как bootstrap, без него — как обычный DNS (plain).</summary>
    public Task<RouterStarted> DnsSetCustomAsync(RouterDnsCustom custom, CancellationToken ct = default)
    {
        if (!DnsValues.IsTitle(custom.Title)) throw new ArgumentException("Название варианта — до 40 символов, без кавычек");
        if (custom.Doh is not null && !DnsValues.IsDohTemplate(custom.Doh)) throw new ArgumentException("Защищённый DNS — адрес вида https://сервер/путь");
        static JsonArray? Ips(List<string>? l)
        {
            if (l is null || l.Count == 0) return null;
            if (l.Count > DnsValues.MaxServers) throw new ArgumentException("Не больше 4 адресов");
            return new JsonArray(l.Select(ip => (JsonNode)(DnsValues.NormalizeIp(ip) ?? throw new ArgumentException($"«{ip}» — не адрес IPv4/IPv6"))).ToArray());
        }
        var c = new JsonObject { ["title"] = custom.Title.Trim() };
        if (custom.Doh is { } d) c["doh"] = d;
        if (Ips(custom.Bootstrap) is { } b) c["bootstrap"] = b;
        if (Ips(custom.Plain) is { } p) c["plain"] = p;
        if (custom.Doh is null && c["plain"] is null) throw new ArgumentException("Нужен защищённый DNS или адреса");
        return CallAsync<RouterStarted>("dns_set", new JsonObject { ["custom"] = c }, ct);
    }

    public Task TargetsSetAsync(IReadOnlyList<RouterTarget> items, CancellationToken ct = default)
    {
        if (items.Count > 50) throw new ArgumentException("На роутере — не больше 50 своих адресов");
        return CallAsync("targets_set", new JsonObject { ["items"] = JsonSerializer.SerializeToNode(items, Json) }, ct);
    }

    public Task ListsSetAsync(string name, IReadOnlyList<string> lines, CancellationToken ct = default)
    {
        if (lines.Count > 5000) throw new ArgumentException("В списке на роутере — не больше 5000 строк");
        return CallAsync("lists_set", new JsonObject { ["name"] = CheckListName(name), ["lines"] = new JsonArray(lines.Select(l => (JsonNode)l).ToArray()) }, ct);
    }

    public Task StrategiesUserSetAsync(IReadOnlyList<RouterUserStrategy> items, CancellationToken ct = default) =>
        CallAsync("strategies_user_set", new JsonObject { ["items"] = JsonSerializer.SerializeToNode(items, Json) }, ct);

    public Task ConflictStopAsync(string name, CancellationToken ct = default) => CallAsync("conflict_stop", new JsonObject { ["name"] = name }, ct);
    /// <summary>Разгрузку (flow offloading) из межсетевого экрана передать zapret: та же скорость, пакеты обхода — мимо разгрузки (§9).</summary>
    public Task<RouterFlowOffloadFix> FlowOffloadFixAsync(CancellationToken ct = default) => CallAsync<RouterFlowOffloadFix>("flow_offload_fix", null, ct);
    /// <summary>Обход по IPv6; при включённом обходе роутер перезапускает zapret (§9).</summary>
    public Task Ipv6SetAsync(bool enabled, CancellationToken ct = default) => CallAsync("ipv6_set", new JsonObject { ["enabled"] = enabled }, ct);
    public Task<RouterStarted> CoreInstallAsync(CancellationToken ct = default) => CallAsync<RouterStarted>("core_install", null, ct);

    static string CheckListName(string name) =>
        name is "user-general" or "user-exclude" ? name : throw new ArgumentException("Список роутера: user-general или user-exclude");

    /// <summary>
    /// Найденная на ПК стратегия → формат роутера (§5.2, §5.3): id по шаблону <c>^u-[a-z0-9-]{1,48}$</c>, название до 80 символов,
    /// роли как есть — неизвестную роль роутер вернёт как invalid с field, тогда предлагаем обновить пакет.
    /// </summary>
    public static RouterUserStrategy ToRouter(Strategy s)
    {
        var body = new string(s.Id.ToLowerInvariant().Select(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' ? c : '-').ToArray());
        if (body.StartsWith("u-")) body = body[2..];
        body = body.Trim('-');
        if (body.Length == 0) body = "x";
        if (body.Length > 48) body = body[..48].TrimEnd('-');
        var title = s.Title.Length > 80 ? s.Title[..80] : s.Title;
        return new("u-" + body, title, new Dictionary<string, string>(s.Roles));
    }

    // ---------- §2.3 поиск за вторым роутером ----------

    /// <summary>
    /// Куда стучаться за status.json: шлюз, затем до 3 частных адресов на пути наружу (как `tracert -d -h 4`:
    /// эхо-запросы с TTL 1..4, адрес ответившего узла). Только 10/8, 172.16/12, 192.168/16.
    /// </summary>
    public static async Task<IReadOnlyList<string>> CandidateAddressesAsync(CancellationToken ct = default)
    {
        var list = new List<string>();
        if (DefaultGateway() is { } gw) list.Add(gw);
        using var ping = new Ping();
        for (int ttl = 1; ttl <= 4 && list.Count < 4; ttl++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var r = await ping.SendPingAsync(IPAddress.Parse("1.1.1.1"), TimeSpan.FromSeconds(1), new byte[8], new PingOptions(ttl, true), ct);
                if (r.Address is { AddressFamily: AddressFamily.InterNetwork } a && IsPrivate(a) && !list.Contains(a.ToString()))
                    list.Add(a.ToString());
                if (r.Status == IPStatus.Success) break;   // дошли наружу — дальше частных узлов нет
            }
            catch (PingException) { }
        }
        return list;
    }

    public static bool IsPrivate(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168);
    }

    public void Dispose()
    {
        _password = null;
        if (_ownsHttp) _http.Dispose();
    }
}
