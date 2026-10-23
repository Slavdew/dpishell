using DpiShell.Core.Localization;
using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DpiShell.Core.Router;

/// <summary>
/// Управление DpiShell на роутере по договору docs/dev/router-api.md (API 1): JSON-RPC 2.0 через rpcd/ubus,
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
    readonly IRouterHostKeyStore? _pins;
    Uri _base;
    string? _pinId, _seenCert, _lostPin;
    bool _httpsWorked, _httpsTried, _plainAllowed;
    RouterCertChange? _certChanged;
    string? _session;
    Func<string>? _password;
    int _id;

    /// <param name="address">IP или имя роутера без схемы и пути (договор §2.1); для тестов — «127.0.0.1:порт».</param>
    /// <param name="pins">
    /// Отпечатки сертификатов HTTPS (тот же router-hostkeys.json, что у SSH). Без него самоподписанный сертификат роутера не принимается —
    /// окно работает только по HTTP, как раньше.
    /// </param>
    /// <param name="httpsPort">Куда идти по HTTPS, если HTTP не отвечает (у uhttpd — 443).</param>
    public RouterClient(string address, HttpClient? http = null, IRouterHostKeyStore? pins = null, int httpsPort = 443)
    {
        Address = address.Trim().TrimEnd('/');
        if (Address.Contains("://") || Address.Contains('/')) throw new ArgumentException(L.T("core.rt.arg.address"));
        _pins = pins;
        HttpsPort = httpsPort;
        _base = new Uri($"http://{Address}/");
        _ownsHttp = http is null;
        if (http is null)
        {
            // Перенаправления — сами: POST /ubus при 301 стал бы GET, а переход на HTTPS — только на тот же роутер.
            var h = new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(3), AllowAutoRedirect = false };
            h.SslOptions.RemoteCertificateValidationCallback = CheckCertificate;
            http = new HttpClient(h);
            // Роутер уже входили по HTTPS (отпечаток закреплён) — сразу туда: без лишнего перенаправления и без пароля по HTTP.
            if (pins?.Get(RouterCert.PinId(_base.Host, httpsPort)) is not null) UseHttps(HttpsUri(httpsPort));
        }
        _http = http;
    }

    public string Address { get; }
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(10);   // §4: таймаут HTTP 10 с
    public TimeSpan DetectTimeout { get; init; } = TimeSpan.FromSeconds(1);  // §2.2: 1 с
    /// <summary>Сколько ждать HTTPS перед входом, когда HTTP отвечает без перенаправления (закрытый 443 у Windows — отказ ~1–2 с).</summary>
    public TimeSpan HttpsTryTimeout { get; init; } = TimeSpan.FromSeconds(1);

    // ---------- HTTP или HTTPS ----------

    public int HttpsPort { get; }

    /// <summary>Окно говорит с роутером по HTTPS (перенаправление с HTTP или HTTP закрыт).</summary>
    public bool Https => _base.Scheme == Uri.UriSchemeHttps;

    /// <summary>Отпечаток сертификата HTTPS, с которым идёт работа (null — HTTP или ещё не подключались).</summary>
    public string? CertificateFingerprint => Https ? _seenCert : null;

    Uri HttpsUri(int port) => new UriBuilder(Uri.UriSchemeHttps, _base.Host, port, "/").Uri;
    Uri HttpUri() => new($"http://{Address}/");

    void UseHttps(Uri https)
    {
        _base = https;
        _pinId = RouterCert.PinId(https.Host, https.Port);
    }

    /// <summary>Рукопожатие TLS: закреплённый отпечаток — только он; первого входа ещё не было — любой (запомнится после входа).</summary>
    bool CheckCertificate(object sender, System.Security.Cryptography.X509Certificates.X509Certificate? cert,
        System.Security.Cryptography.X509Certificates.X509Chain? chain, System.Net.Security.SslPolicyErrors errors)
    {
        if (cert is null || _pinId is not { } id) return false;
        var fp = RouterCert.Fingerprint(cert);
        _seenCert = fp;
        var stored = _pins?.Get(id);
        if (RouterCert.Accept(stored, fp, errors, _pins is not null)) return true;
        if (stored is not null) _certChanged = new(id, _base.Host, fp);
        return false;
    }

    /// <summary>Первый вход по HTTPS прошёл — закрепить сертификат (как ключ SSH: это роутер, к которому пользователь знает пароль).</summary>
    void PinIfFirst()
    {
        if (Https && _pins is not null && _pinId is { } id && _seenCert is { } fp && _pins.Get(id) is null) _pins.Set(id, fp);
    }

    static bool IsRedirect(HttpResponseMessage r) => (int)r.StatusCode is 301 or 302 or 303 or 307 or 308;

    /// <summary>Перенаправление на https:// того же роутера (порт — из адреса перенаправления), иначе null.</summary>
    Uri? HttpsRedirect(HttpResponseMessage r)
    {
        if (!IsRedirect(r) || r.Headers.Location is not { IsAbsoluteUri: true } loc) return null;
        return loc.Scheme == Uri.UriSchemeHttps && string.Equals(loc.Host, _base.Host, StringComparison.OrdinalIgnoreCase) ? HttpsUri(loc.Port) : null;
    }

    /// <summary>
    /// Запрос к роутеру. По HTTP: перенаправление на HTTPS того же роутера или молчание HTTP при отвечающем HTTPS — дальше по HTTPS.
    /// По HTTPS не отвечает (HTTPS на роутере выключили) — снова HTTP. Сертификат не совпал с закреплённым —
    /// <see cref="RouterFailure.CertificateChanged"/>; остальные сбои — как раньше (HttpRequestException).
    /// Со своим HttpClient (тесты) — как он настроен, без переходов.
    /// <paramref name="secret"/> — запрос с паролем или сессией (/ubus): если HTTPS роутера закреплён, а отвечает только HTTP, он не уходит —
    /// <see cref="RouterFailure.HttpsLost"/>, пока закрепление не снято (решение пользователя).
    /// </summary>
    async Task<HttpResponseMessage> SendAsync(Func<Uri, HttpRequestMessage> make, string path, CancellationToken ct, bool secret = false)
    {
        if (secret && HttpsLost is { } lost) throw HttpsLostError(lost);
        // Пароль и сессия по HTTP — только если HTTPS нет: HTTP отвечает без перенаправления, а HTTPS открыт — сначала HTTPS
        // (первый вход: сертификат закрепится после входа). Пробуем один раз за подключение.
        if (secret && !Https && _ownsHttp && !_httpsTried)
        {
            _httpsTried = true;
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probe.CancelAfter(HttpsTryTimeout);
            var (httpBase, httpPin) = (_base, _pinId);
            _seenCert = null;
            try { return await SwitchAsync(HttpsUri(HttpsPort), make, path, probe.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && _seenCert is not null)
            {
                // TLS ответил, но медленно (слабый роутер): HTTPS есть — ждём его, а не уходим на HTTP.
                return await SwitchAsync(HttpsUri(HttpsPort), make, path, ct);
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                (_base, _pinId) = (httpBase, httpPin);
            }
        }
        HttpResponseMessage resp;
        try { resp = await SendToAsync(_base, make, path, ct); }
        catch (HttpRequestException first) when (_ownsHttp && !ct.IsCancellationRequested)
        {
            var (prevBase, prevPin) = (_base, _pinId);
            try
            {
                if (Https)
                {
                    _base = HttpUri(); _pinId = null;
                    // Закреплён или уже работал в этом подключении (первый вход: поиск по HTTPS, вход ещё нет) — без согласия пароль по HTTP не идёт.
                    if (prevPin is not null && (_pins?.Get(prevPin) is not null || _httpsWorked)) _lostPin = prevPin;
                    if (secret && HttpsLost is { } l) { await ProbeHttpAsync(ct); throw HttpsLostError(l); }
                    return await SendToAsync(_base, make, path, ct);
                }
                return await SwitchAsync(HttpsUri(HttpsPort), make, path, ct);
            }
            catch (HttpRequestException)
            {
                (_base, _pinId) = (prevBase, prevPin);
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(first);
                throw;
            }
        }
        if (!Https && _ownsHttp && HttpsRedirect(resp) is { } target)
        {
            resp.Dispose();
            return await SwitchAsync(target, make, path, ct);
        }
        return resp;
    }

    /// <summary>
    /// HTTPS закреплён, а роутер отвечает только по HTTP (пароль по HTTP не уходит без согласия). Закрепление сняли (<see cref="IRouterHostKeyStore.Remove"/>) — null.
    /// </summary>
    public string? HttpsLost => !Https && _lostPin is { } p && (_pins?.Get(p) is not null || _httpsWorked && !_plainAllowed) ? p : null;

    /// <summary>Пользователь согласился на HTTP после пропажи HTTPS (вместе со снятием закрепления).</summary>
    public void AllowPlainHttp() => _plainAllowed = true;

    static RouterException HttpsLostError(string pin) => new(RouterFailure.HttpsLost, L.T("core.rt.err.https_lost")) { LostHttpsPin = pin };

    /// <summary>HTTPS молчит — отвечает ли HTTP вообще (без пароля: GET status.json). Не отвечает — обычный «недоступен».</summary>
    async Task ProbeHttpAsync(CancellationToken ct)
    {
        using var r = await SendToAsync(_base, u => new HttpRequestMessage(HttpMethod.Get, u), "dpishell/status.json", ct);
    }

    async Task<HttpResponseMessage> SwitchAsync(Uri https, Func<Uri, HttpRequestMessage> make, string path, CancellationToken ct)
    {
        var (prevBase, prevPin) = (_base, _pinId);
        UseHttps(https);
        try { return await SendToAsync(https, make, path, ct); }
        catch (HttpRequestException)
        {
            (_base, _pinId) = (prevBase, prevPin);
            throw;
        }
    }

    async Task<HttpResponseMessage> SendToAsync(Uri baseUri, Func<Uri, HttpRequestMessage> make, string path, CancellationToken ct)
    {
        _certChanged = null;
        using var req = make(new Uri(baseUri, path));
        try
        {
            var r = await _http.SendAsync(req, ct);
            if (baseUri.Scheme == Uri.UriSchemeHttps) _httpsWorked = true;
            return r;
        }
        catch (HttpRequestException) when (_certChanged is { } changed)
        {
            // Не HttpRequestException: перебирать HTTP/HTTPS дальше нельзя — пользователь должен решить.
            throw new RouterException(RouterFailure.CertificateChanged, L.T("core.rt.err.cert_changed")) { Certificate = changed };
        }
    }

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
            using var resp = await SendAsync(u => new HttpRequestMessage(HttpMethod.Get, u), "dpishell/status.json", cts.Token);
            if (!resp.IsSuccessStatusCode) return new(Address, RouterFound.NotFound, null);
            var st = await resp.Content.ReadFromJsonAsync<RouterPublicStatus>(Json, cts.Token);
            if (st?.Product != "dpishell-router") return new(Address, RouterFound.NotFound, null);
            // Без поля api — пакет старше договора (router-api §2).
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
        if (res.Code == 6) throw new RouterException(RouterFailure.AccessDenied, L.T("core.rt.err.bad_password"), ubusCode: 6);
        EnsureOk(res, "login");
        _session = res.Body?["ubus_rpc_session"]?.GetValue<string>()
                   ?? throw new RouterException(RouterFailure.Protocol, L.T("core.rt.err.no_session"));
        PinIfFirst();
    }

    public bool LoggedIn => _session is not null;

    // ---------- §4 вызов ----------

    async Task<JsonObject?> CallAsync(string method, JsonObject? args, CancellationToken ct)
    {
        if (_session is null) throw new RouterException(RouterFailure.AccessDenied, L.T("core.rt.err.no_login"));
        var res = await RpcAsync(_session, "dpishell", method, args ?? new JsonObject(), ct);
        if (res.Code == 6 && _password is not null)
        {
            // Сессия истекла — войти заново один раз (§3).
            await LoginAsync(_password, ct);
            res = await RpcAsync(_session!, "dpishell", method, args ?? new JsonObject(), ct);
            if (res.Code == 6) throw new RouterException(RouterFailure.AccessDenied, L.T("core.rt.err.bad_password_or_session"), ubusCode: 6);
        }
        EnsureOk(res, method);
        if (res.Body?["error"] is JsonObject err)
        {
            var ps = err["params"] is JsonObject po ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(po.ToJsonString()) : null;
            var e = new RouterAppError(err["code"]?.GetValue<string>() ?? "failed", err["text"]?.GetValue<string>(), err["field"]?.GetValue<string>(),
                err["key"]?.GetValue<string>(), ps);
            // Новый роутер присылает ключ: текст собирается на языке окна; старый — только готовый русский text.
            throw new RouterException(RouterFailure.App, RouterPhrase.ByKey(e.Key, e.Params) ?? e.Text ?? AppErrorText(e.Code), e);
        }
        return res.Body;
    }

    async Task<T> CallAsync<T>(string method, JsonObject? args, CancellationToken ct)
    {
        var body = await CallAsync(method, args, ct);
        try { return body.Deserialize<T>(Json) ?? throw new JsonException("пустой ответ"); }
        catch (JsonException e) { throw new RouterException(RouterFailure.Protocol, L.T("core.rt.err.bad_reply", new { method, error = e.Message })); }
    }

    static void EnsureOk((int Code, JsonObject? Body) res, string method)
    {
        switch (res.Code)
        {
            case 0: return;
            case 2: throw new RouterException(RouterFailure.InvalidArgs, L.T("core.rt.err.bad_args", new { method }), ubusCode: 2);
            case 4: throw new RouterException(RouterFailure.NotSupported, L.T("core.rt.err.not_supported"), ubusCode: 4);
            case 6: throw new RouterException(RouterFailure.AccessDenied, L.T("core.rt.err.no_access"), ubusCode: 6);
            case 5 or 7: throw new RouterException(RouterFailure.NoResponse, L.T("core.rt.err.timeout"), ubusCode: res.Code);
            default: throw new RouterException(RouterFailure.Protocol, L.T("core.rt.err.code", new { code = res.Code, method }), ubusCode: res.Code);
        }
    }

    static string AppErrorText(string code) => code switch
    {
        "busy" => L.T("core.rt.app.busy"),
        "not_installed" => L.T("core.rt.app.not_installed"),
        "invalid" => L.T("core.rt.app.invalid"),
        "conflict" => L.T("core.rt.app.conflict"),
        "core_mismatch" => L.T("core.rt.app.core_mismatch"),
        _ => L.T("core.rt.app.other"),
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
            var json = req.ToJsonString();
            using var resp = await SendAsync(u => new HttpRequestMessage(HttpMethod.Post, u) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") },
                "ubus", cts.Token, secret: true);
            if (!resp.IsSuccessStatusCode) throw new RouterException(RouterFailure.Unreachable, L.T("core.rt.err.http", new { code = (int)resp.StatusCode }));
            var root = JsonNode.Parse(await resp.Content.ReadAsStringAsync(cts.Token)) as JsonObject
                       ?? throw new RouterException(RouterFailure.Protocol, L.T("core.rt.err.not_json"));
            if (root["error"] is JsonObject rpcErr)
                return rpcErr["code"]?.GetValue<int>() == -32002 ? (6, null)
                    : throw new RouterException(RouterFailure.Protocol, L.T("core.rt.err.rpc", new { message = rpcErr["message"]?.GetValue<string>() ?? L.T("core.rt.err.rpc_default") }));
            if (root["result"] is not JsonArray result || result.Count == 0)
                throw new RouterException(RouterFailure.Protocol, L.T("core.rt.err.no_result"));
            return (result[0]!.GetValue<int>(), result.Count > 1 ? result[1] as JsonObject : null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // В тексте нет ни запроса, ни пароля — только что роутер недоступен.
            throw new RouterException(RouterFailure.Unreachable, L.T("core.rt.err.unreachable"));
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            throw new RouterException(RouterFailure.Protocol, L.T("core.rt.err.contract"));
        }
    }

    // ---------- §5.1 чтение ----------

    public Task<RouterStatus> StatusAsync(CancellationToken ct = default) => CallAsync<RouterStatus>("status", null, ct);
    public Task<RouterStrategies> StrategiesAsync(CancellationToken ct = default) => CallAsync<RouterStrategies>("strategies", null, ct);
    public Task<RouterSelectProgress> SelectProgressAsync(CancellationToken ct = default) => CallAsync<RouterSelectProgress>("select_progress", null, ct);
    /// <summary>
    /// Ход подбора со строками rows с номера since (роутер 0.2.1-r2, §9): звать, только если прошлый ответ был с rows_total —
    /// старый роутер параметров не объявлял. run — Run прошлого ответа: другой подбор — роутер отдаст строки с 0 (rows_from = 0).
    /// </summary>
    public Task<RouterSelectProgress> SelectProgressAsync(int since, long? run, CancellationToken ct = default) =>
        CallAsync<RouterSelectProgress>("select_progress", run is { } r ? new JsonObject { ["since"] = since, ["run"] = r } : new JsonObject { ["since"] = since }, ct);
    public Task<RouterTargets> TargetsGetAsync(CancellationToken ct = default) => CallAsync<RouterTargets>("targets_get", null, ct);
    /// <summary>
    /// name: user-general, user-exclude, exclude (стандартные исключения, только чтение) или встроенный список роли — google, discord, general
    /// (роутер 0.2.0-r9, только чтение: по ним ПК понимает, какие адреса ведёт роутер).
    /// </summary>
    public Task<RouterLines> ListsGetAsync(string name, CancellationToken ct = default) =>
        CallAsync<RouterLines>("lists_get", new JsonObject { ["name"] = name is "exclude" or "google" or "discord" or "general" ? name : CheckListName(name) }, ct);

    // ---------- совместная работа с ПК (§5.2 pc_link_*, роутер 0.2.0-r9) ----------

    /// <summary>Методы договора, без которых совместная работа невозможна.</summary>
    public static readonly IReadOnlyList<string> PcLinkMethods = ["pc_link_set", "pc_link_del", "pc_link_get"];

    static readonly System.Text.RegularExpressions.Regex PcIdRx = new(@"^[A-Za-z0-9-]{8,64}\z");

    /// <summary>MAC в виде aa:bb:cc:dd:ee:ff (из «-» или «:», любой регистр); не MAC — null.</summary>
    public static string? NormalizeMac(string? mac)
    {
        if (mac is null) return null;
        var hex = mac.Trim().Replace("-", "").Replace(":", "").ToLowerInvariant();
        return hex.Length == 12 && hex.All(Uri.IsHexDigit) ? string.Join(':', Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2))) : null;
    }

    /// <summary>
    /// Связка этого ПК: роутер не трогает <paramref name="domains"/> (сайты ПК и имена его приманок) для устройства с этими MAC и продлевает срок
    /// (повтор того же <paramref name="id"/> — обновление). Домены роутера (YouTube, Discord, строки продвинутого режима) роутер не берёт — они в ответе <c>ignored</c>.
    /// </summary>
    public Task<RouterPcLinkSet> PcLinkSetAsync(string id, IReadOnlyList<string> macs, IReadOnlyList<string> domains, CancellationToken ct = default)
    {
        if (!PcIdRx.IsMatch(id)) throw new ArgumentException(L.T("core.rt.arg.pc_id"));
        var m = macs.Select(NormalizeMac).ToList();
        if (m.Count is 0 or > 8 || m.Any(x => x is null)) throw new ArgumentException(L.T("core.rt.arg.pc_mac"));
        if (domains.Count > 500 || !domains.All(AdvancedSites.IsDomain)) throw new ArgumentException(L.T("core.rt.arg.pc_domains"));
        return CallAsync<RouterPcLinkSet>("pc_link_set", new JsonObject
        {
            ["id"] = id,
            ["macs"] = new JsonArray(m.Select(x => (JsonNode)x!).ToArray()),
            ["domains"] = new JsonArray(domains.Select(d => (JsonNode)d).ToArray()),
        }, ct);
    }

    /// <summary>Снять связку (неизвестный id — тоже успех).</summary>
    public Task PcLinkDelAsync(string id, CancellationToken ct = default)
    {
        if (!PcIdRx.IsMatch(id)) throw new ArgumentException(L.T("core.rt.arg.pc_id"));
        return CallAsync("pc_link_del", new JsonObject { ["id"] = id }, ct);
    }

    public Task<RouterPcLinks> PcLinkGetAsync(CancellationToken ct = default) => CallAsync<RouterPcLinks>("pc_link_get", null, ct);
    public Task<RouterSiteCheckResult> SiteCheckResultAsync(CancellationToken ct = default) => CallAsync<RouterSiteCheckResult>("site_check_result", null, ct);
    public Task<RouterDnsStatus> DnsStatusAsync(CancellationToken ct = default) => CallAsync<RouterDnsStatus>("dns_status", null, ct);
    public Task<RouterDiag> DiagAsync(CancellationToken ct = default) => CallAsync<RouterDiag>("diag", null, ct);
    public Task<RouterReport> ReportAsync(CancellationToken ct = default) => CallAsync<RouterReport>("report", null, ct);
    /// <summary>«Безопасность роутера» — справочно, только чтение (с 0.2.0-r13). Звать, только если метод есть в status.methods.</summary>
    public Task<RouterSecurityReport> SecurityCheckAsync(CancellationToken ct = default) => CallAsync<RouterSecurityReport>("security_check", null, ct);

    // ---------- §5.2 действия ----------

    /// <param name="confirmS">Включение на пробу (60–600 с): без <see cref="ConfirmAsync"/> за это время роутер вернёт прежнее (§5.2). null — как раньше.</param>
    public Task EnableAsync(int? confirmS = null, CancellationToken ct = default) => CallAsync("enable", WithConfirm(new JsonObject(), confirmS), ct);
    public Task DisableAsync(CancellationToken ct = default) => CallAsync("disable", null, ct);
    public Task StrategySetAsync(string id, int? confirmS = null, CancellationToken ct = default) =>
        CallAsync("strategy_set", WithConfirm(new JsonObject { ["id"] = id }, confirmS), ct);
    /// <summary>«Всё работает» — снять pending_confirm; без ожидания — тоже без ошибки (§5.2).</summary>
    public Task ConfirmAsync(CancellationToken ct = default) => CallAsync("confirm", null, ct);

    /// <summary>Срок пробы по договору — 60–600 с.</summary>
    public const int MinConfirmS = 60, MaxConfirmS = 600;

    static JsonObject WithConfirm(JsonObject args, int? confirmS)
    {
        if (confirmS is not { } s) return args;
        if (s is < MinConfirmS or > MaxConfirmS) throw new ArgumentOutOfRangeException(nameof(confirmS), L.T("core.rt.arg.confirm_s"));
        args["confirm_s"] = s;
        return args;
    }
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
    public Task ApplyAsync(string id, IEnumerable<string> backups, int? confirmS = null, CancellationToken ct = default) =>
        CallAsync("apply", WithConfirm(new JsonObject { ["id"] = id, ["backups"] = new JsonArray(backups.Select(b => (JsonNode)b).ToArray()) }, confirmS), ct);

    /// <param name="autoSelect">При неудаче проверки и исчерпанных запасных роутер сам запускает быстрый подбор (§9, вопрос 7).</param>
    /// <param name="watchdog">Сторож: выключать обход, если с ним пропал интернет (§5.2); null — не передавать (старый роутер).</param>
    public Task ScheduleSetAsync(bool enabled, double hours, bool autoSelect = false, bool? watchdog = null, CancellationToken ct = default)
    {
        // §9: добавлено 0.5 (раз в 30 мин).
        if (hours is not (0.5 or 1 or 3 or 6 or 12 or 24)) throw new ArgumentOutOfRangeException(nameof(hours), L.T("core.rt.arg.hours"));
        var args = new JsonObject { ["enabled"] = enabled, ["hours"] = hours, ["auto_select"] = autoSelect };
        if (watchdog is { } w) args["watchdog"] = w;
        return CallAsync("schedule_set", args, ct);
    }

    /// <summary>«Все сайты, кроме исключений» (all) / «По спискам» (lists); роутер перезапускает zapret и проверяет с контрольной группой.</summary>
    public Task<RouterStarted> ModeSetAsync(bool allSites, CancellationToken ct = default) =>
        CallAsync<RouterStarted>("mode_set", new JsonObject { ["mode"] = allSites ? "all" : "lists" }, ct);

    /// <summary>«Сайт не открывается с обходом»: проверка с обходом и без, итог — <see cref="SiteCheckResultAsync"/>. Голое имя роутер дополнит сам.</summary>
    public Task<RouterStarted> SiteCheckAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2048 || url.Any(char.IsWhiteSpace)) throw new ArgumentException(L.T("core.rt.arg.site_url"));
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
            throw new ArgumentException(L.T("core.rt.arg.dns_unknown"));
        return CallAsync<RouterStarted>("dns_set", new JsonObject { ["preset"] = preset }, ct);
    }

    /// <summary>Свой вариант: с шаблоном DoH адреса уходят как bootstrap, без него — как обычный DNS (plain).</summary>
    public Task<RouterStarted> DnsSetCustomAsync(RouterDnsCustom custom, CancellationToken ct = default)
    {
        if (!DnsValues.IsTitle(custom.Title)) throw new ArgumentException(L.T("core.rt.arg.dns_title"));
        if (custom.Doh is not null && !DnsValues.IsDohTemplate(custom.Doh)) throw new ArgumentException(L.T("core.rt.arg.dns_doh"));
        static JsonArray? Ips(List<string>? l)
        {
            if (l is null || l.Count == 0) return null;
            if (l.Count > DnsValues.MaxServers) throw new ArgumentException(L.T("core.rt.arg.dns_max", new { max = DnsValues.MaxServers }));
            return new JsonArray(l.Select(ip => (JsonNode)(DnsValues.NormalizeIp(ip) ?? throw new ArgumentException(L.T("core.rt.arg.dns_not_ip", new { ip })))).ToArray());
        }
        var c = new JsonObject { ["title"] = custom.Title.Trim() };
        if (custom.Doh is { } d) c["doh"] = d;
        if (Ips(custom.Bootstrap) is { } b) c["bootstrap"] = b;
        if (Ips(custom.Plain) is { } p) c["plain"] = p;
        if (custom.Doh is null && c["plain"] is null) throw new ArgumentException(L.T("core.rt.arg.dns_need"));
        return CallAsync<RouterStarted>("dns_set", new JsonObject { ["custom"] = c }, ct);
    }

    /// <summary>Продвинутый режим «сайты → стратегия» (§5.1, роутер 0.2.0-r9).</summary>
    public Task<RouterAdvanced> AdvancedGetAsync(CancellationToken ct = default) => CallAsync<RouterAdvanced>("advanced_get", null, ct);

    /// <summary>
    /// Сохранить продвинутый режим: до 20 строк, в строке 1–50 сайтов (строгий шаблон имени) и стратегия auto | direct | id. При включённой оптимизации роутер перезапускается;
    /// с <paramref name="confirmS"/> — на пробу, откат вернёт и прежние строки (§5.2). Итог подбора (resolved) окно не присылает.
    /// </summary>
    public Task AdvancedSetAsync(bool enabled, string others, IReadOnlyList<RouterAdvancedRow> rows, int? confirmS = null, CancellationToken ct = default)
    {
        if (!AdvancedSites.IsOthers(others)) throw new ArgumentException(L.T("core.rt.arg.others"));
        if (rows.Count > AdvancedSites.MaxRows) throw new ArgumentException(L.T("core.rt.arg.rows_max", new { max = AdvancedSites.MaxRows }));
        var list = new JsonArray();
        foreach (var r in rows)
        {
            if (r.Domains.Count is 0 or > AdvancedSites.MaxDomains || !r.Domains.All(AdvancedSites.IsDomain)) throw new ArgumentException(L.T("core.rt.arg.row_domains"));
            if (!AdvancedSites.IsStrategyChoice(r.Strategy)) throw new ArgumentException(L.T("core.rt.arg.row_strategy"));
            list.Add(new JsonObject { ["domains"] = new JsonArray(r.Domains.Select(d => (JsonNode)d).ToArray()), ["strategy"] = r.Strategy });
        }
        return CallAsync("advanced_set", WithConfirm(new JsonObject { ["enabled"] = enabled, ["others"] = others, ["rows"] = list }, confirmS), ct);
    }

    public Task TargetsSetAsync(IReadOnlyList<RouterTarget> items, CancellationToken ct = default)
    {
        if (items.Count > 50) throw new ArgumentException(L.T("core.rt.arg.own_max"));
        return CallAsync("targets_set", new JsonObject { ["items"] = JsonSerializer.SerializeToNode(items, Json) }, ct);
    }

    public Task ListsSetAsync(string name, IReadOnlyList<string> lines, CancellationToken ct = default)
    {
        if (lines.Count > 5000) throw new ArgumentException(L.T("core.rt.arg.list_max"));
        return CallAsync("lists_set", new JsonObject { ["name"] = CheckListName(name), ["lines"] = new JsonArray(lines.Select(l => (JsonNode)l).ToArray()) }, ct);
    }

    /// <summary>Названия чистятся здесь же: роутер r12 на &lt; &gt; &amp; " ' отвечает invalid (field items[N].title).</summary>
    public Task StrategiesUserSetAsync(IReadOnlyList<RouterUserStrategy> items, CancellationToken ct = default) =>
        CallAsync("strategies_user_set", new JsonObject
        {
            ["items"] = JsonSerializer.SerializeToNode(items.Select(i => i with { Title = CleanTitle(i.Title, i.Id) }).ToList(), Json)
        }, ct);

    /// <summary>Название для роутера: без &lt; &gt; &amp; " ', до 80 символов; пустое после очистки — <paramref name="fallback"/>.</summary>
    public static string CleanTitle(string? title, string fallback)
    {
        var t = new string((title ?? "").Where(c => c is not ('<' or '>' or '&' or '"' or '\'')).ToArray()).Trim();
        if (t.Length > 80) t = t[..80].TrimEnd();
        return t.Length == 0 ? fallback : t;
    }

    public Task ConflictStopAsync(string name, CancellationToken ct = default) => CallAsync("conflict_stop", new JsonObject { ["name"] = name }, ct);
    /// <summary>Разгрузку (flow offloading) из межсетевого экрана передать zapret: та же скорость, пакеты обхода — мимо разгрузки (§9).</summary>
    public Task<RouterFlowOffloadFix> FlowOffloadFixAsync(CancellationToken ct = default) => CallAsync<RouterFlowOffloadFix>("flow_offload_fix", null, ct);
    /// <summary>Обход по IPv6; при включённом обходе роутер перезапускает zapret (§9).</summary>
    public Task Ipv6SetAsync(bool enabled, CancellationToken ct = default) => CallAsync("ipv6_set", new JsonObject { ["enabled"] = enabled }, ct);
    public Task<RouterStarted> CoreInstallAsync(CancellationToken ct = default) => CallAsync<RouterStarted>("core_install", null, ct);

    // ---------- ядро договора §5.4: удаление и обновление ----------

    /// <summary>
    /// Удалить DpiShell (фоново): вернуть изменённое вне своих файлов, удалить пакеты и ядро. keepSettings: false — ещё /etc/dpishell, ключ и строку витрины.
    /// Объект dpishell по ходу исчезает (дальше — код 4); итог — /tmp/dpishell-uninstall.json, читать <see cref="RouterSsh.ReadUninstallResultAsync"/>.
    /// </summary>
    public Task<RouterStarted> UninstallAsync(bool keepSettings, CancellationToken ct = default) =>
        CallAsync<RouterStarted>("uninstall", new JsonObject { ["keep_settings"] = keepSettings }, ct);

    /// <summary>Обновить DpiShell (фоново, итог — status.update.job). Local — пакеты уже в /tmp/dpishell-update (<see cref="RouterSsh.UploadUpdateAsync"/>).</summary>
    public Task<RouterStarted> UpdateInstallAsync(RouterUpdateSource from, CancellationToken ct = default) =>
        CallAsync<RouterStarted>("update_install", new JsonObject { ["from"] = from == RouterUpdateSource.Local ? "local" : "feed" }, ct);

    /// <summary>Проверить новую версию сейчас (итог — status.update).</summary>
    public Task<RouterStarted> UpdateCheckAsync(CancellationToken ct = default) => CallAsync<RouterStarted>("update_check", null, ct);

    /// <summary>Откат версии пакета (router-api §5.2, роутер с 0.2.0-r16): saved — пакеты из флеша роутера, иначе — из /tmp/dpishell-update (кладёт окно по SSH).</summary>
    public Task<RouterStarted> UpdateRollbackAsync(bool saved, CancellationToken ct = default) =>
        CallAsync<RouterStarted>("update_rollback", new JsonObject { ["from"] = saved ? "saved" : "local" }, ct);

    /// <summary>«Изменения DpiShell на роутере» (router-api §5.2, роутер с 0.2.0-r16): пункты и итог последней отмены.</summary>
    public Task<RouterChangesList> ChangesListAsync(CancellationToken ct = default) => CallAsync<RouterChangesList>("changes_list", null, ct);

    /// <summary>Отменить пункт (фоновое задание; итог — changes_list.job).</summary>
    public Task<RouterStarted> ChangesUndoAsync(string id, CancellationToken ct = default) =>
        CallAsync<RouterStarted>("changes_undo", new JsonObject { ["id"] = id }, ct);

    /// <summary>«Отменить всё» на роутере (фоновое задание; итог — changes_list.job).</summary>
    public Task<RouterStarted> ChangesUndoAllAsync(CancellationToken ct = default) => CallAsync<RouterStarted>("changes_undo_all", null, ct);

    static string CheckListName(string name) =>
        name is "user-general" or "user-exclude" ? name : throw new ArgumentException(L.T("core.rt.arg.list_name"));

    /// <summary>
    /// Найденная на ПК стратегия → формат роутера (§5.2, §5.3): id по шаблону <c>^u-[a-z0-9-]{1,48}$</c>, название до 80 символов,
    /// роли как есть — неизвестную роль роутер вернёт как invalid с field, тогда предлагаем обновить пакет.
    /// </summary>
    public static RouterUserStrategy ToRouter(Strategy s, bool withDiscordTcp = true)
    {
        var body = new string(s.Id.ToLowerInvariant().Select(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' ? c : '-').ToArray());
        if (body.StartsWith("u-")) body = body[2..];
        body = body.Trim('-');
        if (body.Length == 0) body = "x";
        if (body.Length > 48) body = body[..48].TrimEnd('-');
        var title = CleanTitle(s.Title, "u-" + body);
        var roles = new Dictionary<string, string>(s.Roles);
        // Роутер с catalog_version < 3 роли discord_tcp не знает (invalid), и частей (parts) тоже: шлём вариант без них (§5.3).
        if (!withDiscordTcp) roles.Remove("discord_tcp");
        var parts = withDiscordTcp && s.Parts is { Count: > 0 } p
            ? p.Where(x => MixBuilder.PartOrder.Contains(x.Key) && PartIdRx.IsMatch(x.Value)).ToDictionary(x => x.Key, x => x.Value)
            : null;
        return new("u-" + body, title, roles, parts is { Count: > 0 } ? parts : null);
    }

    static readonly System.Text.RegularExpressions.Regex PartIdRx = new(@"^[A-Za-z0-9_.-]{1,64}\z");

    /// <summary>Версия каталога роутера, с которой он знает роль discord_tcp и поле parts (договор §5.3, роутер 0.2.0-r9).</summary>
    public const int DiscordTcpCatalogVersion = 3;

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
