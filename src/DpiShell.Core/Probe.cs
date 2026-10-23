using DpiShell.Core.Localization;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text.Json;

namespace DpiShell.Core;

/// <summary>
/// Цель проверки. handshake_only — достаточно TLS и заголовков ответа (у адреса нет стабильного большого тела).
/// Group — имя группы «Своих сайтов» (API 1.17); null — не из групп или «Прочее».
/// </summary>
public sealed record Target(string Url, int MinBytes = 0, bool HandshakeOnly = false, bool Required = true, string? Group = null);

/// <summary>Control — «Обычные сайты»: не цель подбора, а условие «стратегия их не ломает» (architecture.md «Оценка и порядок»).</summary>
/// <summary>
/// Extra — дополнительная группа (строка продвинутого режима, <see cref="AdvancedSites"/>): проверяется вместе со всеми, но в оценку стратегии
/// не входит и стратегию не бракует — по ней выбирается лучшая стратегия для строки.
/// </summary>
public sealed record TargetGroup(string Id, string Title, List<Target> Targets, bool Control = false, bool Extra = false, Dictionary<string, int>? Totals = null)
{
    /// <summary>Цель подбора: не контрольная и не дополнительная.</summary>
    public bool Goal => !Control && !Extra;
}

public sealed record TargetSet(List<TargetGroup> Groups)
{
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static TargetSet Load(string path) =>
        JsonSerializer.Deserialize<TargetSet>(File.ReadAllText(path), Json) ?? new([]);

    /// <summary>Группа «Свои сайты» из доменов/URL пользователя: каждый адрес обязателен, тело ≥ 1 байта не требуем.</summary>
    public static TargetGroup UserGroup(IEnumerable<string> entries) => new("user", L.T("select.group.user"),
        entries.Select(e => e.Trim()).Where(e => e.Length > 0)
            .Select(e => new Target(e.Contains("://") ? e : $"https://{e}/")).ToList());

    /// <summary>
    /// «Свои сайты» по группам: одна цель подбора «user», у каждого адреса — имя его группы. Из большой группы проверяется выборка
    /// (<see cref="SiteGroups.Sample"/>, одна и та же у всех стратегий); сколько адресов в группе всего — в <see cref="TargetGroup.Totals"/> (ключ — имя, «Прочее» — "").
    /// </summary>
    public static TargetGroup UserGroups(IEnumerable<SiteGroup> groups, int sample = SiteGroups.SampleSize)
    {
        var targets = new List<Target>();
        var totals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in groups)
        {
            var entries = g.Sites.Select(e => e.Trim()).Where(e => e.Length > 0).ToList();
            if (entries.Count == 0) continue;
            var name = SiteGroups.CleanName(g.Name);
            totals[name] = entries.Count;
            foreach (var e in SiteGroups.Sample(entries, sample))
                targets.Add(new Target(e.Contains("://") ? e : $"https://{e}/", Group: name.Length == 0 ? null : name));
        }
        return new("user", L.T("select.group.user"), targets, Totals: totals);
    }
}

/// <summary>Blocked — HTTP 451 «недоступно по требованию закона»: блокировка, а не сайт.</summary>
public enum ProbeOutcome { Ok, Timeout, ConnectFailed, TlsFailed, Reset, Truncated, BadStatus, ForeignRedirect, Error, Blocked }

public sealed record ProbeResult(Target Target, SslProtocols Tls, ProbeOutcome Outcome, double Seconds, long Bytes, string Detail)
{
    public bool Ok => Outcome == ProbeOutcome.Ok;
}

/// <summary>
/// Проверка «сайт реально открывается», а не «сервер что-то ответил»:
/// ТСПУ обычно пропускает рукопожатие и обрывает поток после ~16 КБ, поэтому тело читается целиком.
/// </summary>
public sealed class HttpProbe
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(6);
    public int MaxBytes { get; init; } = 1 << 20;
    public int Attempts { get; init; } = 2;
    /// <summary>Сколько редиректов внутри сайта пройти до конечной страницы; больше — провал.</summary>
    public int MaxRedirects { get; init; } = 5;

    /// <summary>Если задан — адреса целей берутся из DoH, а не из DNS системы (см. docs/research/dns.md). Не нашёл — системный DNS.</summary>
    public IAddressResolver? Resolver { get; init; }

    public static readonly SslProtocols[] DefaultTls = [SslProtocols.Tls13, SslProtocols.Tls12];

    /// <summary>Цель проверяется для каждой версии TLS; успех — только если прошли все версии.</summary>
    public async Task<IReadOnlyList<ProbeResult>> CheckAsync(Target t, IEnumerable<SslProtocols>? tls = null, CancellationToken ct = default)
    {
        var list = new List<ProbeResult>();
        foreach (var v in tls ?? DefaultTls)
        {
            ProbeResult? best = null;
            for (int i = 0; i < Attempts; i++)
            {
                var r = await OnceAsync(t, v, ct);
                if (r.Ok) { best = best is { Ok: true } && best.Seconds <= r.Seconds ? best : r; break; }
                best ??= r;
                if (i + 1 < Attempts) await Task.Delay(500, ct);
            }
            list.Add(best!);
        }
        return list;
    }

    public async Task<ProbeResult> OnceAsync(Target t, SslProtocols tls, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        // Новый обработчик на каждую попытку: никаких переиспользованных соединений между стратегиями.
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = ConnectTimeout,
            PooledConnectionLifetime = TimeSpan.Zero,
            AutomaticDecompression = DecompressionMethods.None,
            SslOptions = { EnabledSslProtocols = tls },
        };
        if (Resolver is { } resolver)
            handler.ConnectCallback = (ctx, token) => ConnectAsync(resolver, ctx.DnsEndPoint, token);
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ConnectTimeout + ReadTimeout);
        long bytes = 0;
        try
        {
            // Редиректы внутри сайта — до конечной страницы тем же клиентом (тот же DoH и версия TLS); судим по конечному ответу.
            var url = new Uri(t.Url);
            var host = url.Host;
            var chain = new List<string>();
            for (int hop = 0; ; hop++)
            {
                cts.CancelAfter(ConnectTimeout + ReadTimeout);   // у каждого перехода — свой срок
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0 Safari/537.36");
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                var code = (int)resp.StatusCode;

                if (t.HandshakeOnly)
                    return new(t, tls, ProbeOutcome.Ok, sw.Elapsed.TotalSeconds, 0, HandshakeText);

                string Chain(string end) => string.Concat(chain.Select(c => c + " → ")) + end;

                if (code is >= 300 and < 400)
                {
                    var loc = resp.Headers.Location;
                    var next = loc is null ? null : (loc.IsAbsoluteUri ? loc : new Uri(url, loc));
                    // Редирект на страницу-заглушку провайдера — признак блокировки.
                    if (next is null || !SameSite(host, next.Host))
                        return new(t, tls, ProbeOutcome.ForeignRedirect, sw.Elapsed.TotalSeconds, 0, Chain(L.T("core.probe.redirect_foreign", new { code, host = next?.Host ?? "?" })));
                    chain.Add($"{code} → {Short(next, url)}");
                    // Как на роутере: до страницы не дошли — провал, только если нужно тело (MinBytes); иначе сайт отвечает — Ok.
                    if (hop + 1 > MaxRedirects)
                        return t.MinBytes > 0
                            ? new(t, tls, ProbeOutcome.BadStatus, sw.Elapsed.TotalSeconds, 0, Chain(L.T("core.probe.too_many_redirects", new { max = MaxRedirects })))
                            : new(t, tls, ProbeOutcome.Ok, sw.Elapsed.TotalSeconds, 0, RespondsText);
                    url = next;
                    continue;
                }
                if (code == 451)
                    return new(t, tls, ProbeOutcome.Blocked, sw.Elapsed.TotalSeconds, 0, Chain(L.T("core.probe.blocked_451")));
                if (code is 403 or 404)
                    return new(t, tls, ProbeOutcome.BadStatus, sw.Elapsed.TotalSeconds, 0, Chain(L.T(code == 403 ? "core.probe.forbidden_403" : "core.probe.not_found_404")));
                if (code == 400 || code >= 500)
                    return new(t, tls, ProbeOutcome.BadStatus, sw.Elapsed.TotalSeconds, 0, Chain($"HTTP {code}"));

                var expected = resp.Content.Headers.ContentLength;
                await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
                var buf = new byte[16384];
                int n;
                while (bytes < MaxBytes && (n = await stream.ReadAsync(buf, cts.Token)) > 0) bytes += n;

                if (bytes < MaxBytes && expected is { } len && bytes < len)
                    return new(t, tls, ProbeOutcome.Truncated, sw.Elapsed.TotalSeconds, bytes, Chain(L.T("core.probe.truncated_of", new { code, bytes, total = len })));
                if (bytes < t.MinBytes)
                    return new(t, tls, ProbeOutcome.Truncated, sw.Elapsed.TotalSeconds, bytes, Chain(L.T("core.probe.truncated_min", new { code, bytes, min = t.MinBytes })));
                // Тело не нужно (MinBytes = 0), а сайт ответил переходами или кодом 4xx (антибот, 498…) — для проверки достаточно.
                if (t.MinBytes == 0 && (chain.Count > 0 || code >= 400))
                    return new(t, tls, ProbeOutcome.Ok, sw.Elapsed.TotalSeconds, bytes, RespondsText);
                return new(t, tls, ProbeOutcome.Ok, sw.Elapsed.TotalSeconds, bytes, Chain(chain.Count == 0 ? $"HTTP {code}, {Kb(bytes)}" : $"{code}, {Kb(bytes)}"));
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(t, tls, bytes > 0 ? ProbeOutcome.Truncated : ProbeOutcome.Timeout, sw.Elapsed.TotalSeconds, bytes,
                bytes > 0 ? L.T("core.probe.stream_cut", new { bytes }) : L.T("core.probe.timeout"));
        }
        catch (HttpRequestException e)
        {
            return new(t, tls, Classify(e), sw.Elapsed.TotalSeconds, bytes, Flatten(e));
        }
        catch (IOException e)
        {
            return new(t, tls, bytes > 0 ? ProbeOutcome.Truncated : ProbeOutcome.Reset, sw.Elapsed.TotalSeconds, bytes, Flatten(e));
        }
    }

    /// <summary>Служебный адрес (HandshakeOnly): страницы нет и не должно быть, важно само соединение.</summary>
    public static string HandshakeText => L.T("core.probe.handshake");
    /// <summary>Тело не требовалось, сервер ответил переходами или антибот-кодом.</summary>
    public static string RespondsText => L.T("core.probe.responds");

    static string Kb(long bytes) => bytes >= 1024 ? L.T("core.probe.kb", new { n = bytes / 1024 }) : L.T("core.probe.bytes", new { n = bytes });

    /// <summary>Куда ведёт переход: путь, если хост тот же; иначе хост и путь («www.youtube.com/»).</summary>
    static string Short(Uri next, Uri from) =>
        next.Host.Equals(from.Host, StringComparison.OrdinalIgnoreCase) && next.Scheme == from.Scheme ? next.PathAndQuery : next.Host + next.PathAndQuery;

    static async ValueTask<Stream> ConnectAsync(IAddressResolver resolver, DnsEndPoint ep, CancellationToken ct)
    {
        IPAddress[] addrs = IPAddress.TryParse(ep.Host, out var literal) ? [literal]
            : ep.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? [IPAddress.Loopback]
            : await resolver.ResolveAsync(ep.Host, ct);
        if (addrs.Length == 0)
            addrs = await Dns.GetHostAddressesAsync(ep.Host, System.Net.Sockets.AddressFamily.InterNetwork, ct);

        Exception? last = null;
        foreach (var ip in addrs.Take(3))
        {
            var s = new System.Net.Sockets.Socket(ip.AddressFamily, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await s.ConnectAsync(new IPEndPoint(ip, ep.Port), ct);
                return new System.Net.Sockets.NetworkStream(s, ownsSocket: true);
            }
            catch (Exception e) when (e is System.Net.Sockets.SocketException && !ct.IsCancellationRequested) { s.Dispose(); last = e; }
            catch { s.Dispose(); throw; }
        }
        throw last ?? new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound);
    }

    static ProbeOutcome Classify(HttpRequestException e)
    {
        for (Exception? x = e; x is not null; x = x.InnerException)
        {
            if (x is AuthenticationException) return ProbeOutcome.TlsFailed;
            if (x is System.Net.Sockets.SocketException se)
                return se.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionReset ? ProbeOutcome.Reset : ProbeOutcome.ConnectFailed;
            if (x is IOException) return ProbeOutcome.Reset;
        }
        return e.HttpRequestError switch
        {
            HttpRequestError.SecureConnectionError => ProbeOutcome.TlsFailed,
            HttpRequestError.ConnectionError => ProbeOutcome.ConnectFailed,
            HttpRequestError.NameResolutionError => ProbeOutcome.ConnectFailed,
            _ => ProbeOutcome.Error,
        };
    }

    static string Flatten(Exception e)
    {
        var parts = new List<string>();
        for (Exception? x = e; x is not null; x = x.InnerException) parts.Add(x.Message);
        return string.Join(" / ", parts.Distinct());
    }

    /// <summary>
    /// youtube.com ↔ www.youtube.com — один сайт (один хост — поддомен другого); youtube.com → warning.rt.ru — нет.
    /// Без списка публичных суффиксов: соседние bbc.co.uk и stub.co.uk не считаются одним сайтом. IP — только точное совпадение.
    /// </summary>
    public static bool SameSite(string a, string b)
    {
        a = a.ToLowerInvariant().TrimEnd('.');
        b = b.ToLowerInvariant().TrimEnd('.');
        if (a == b) return true;
        if (System.Net.IPAddress.TryParse(a, out _) || System.Net.IPAddress.TryParse(b, out _)) return false;
        if (a.EndsWith("." + b) || b.EndsWith("." + a)) return true;
        // Соседние поддомены (www.youtube.com → m.youtube.com), если общий родитель — не суффикс вида co.uk / com.ru.
        var pa = Parent(a); var pb = Parent(b);
        return pa is not null && pa == pb && !LooksLikeSuffix(pa);

        static string? Parent(string h) { var i = h.IndexOf('.'); return i > 0 && h.IndexOf('.', i + 1) > 0 ? h[(i + 1)..] : null; }
        static bool LooksLikeSuffix(string h)
        {
            var p = h.Split('.');
            return p.Length == 2 && p[1].Length == 2 && p[0].Length <= 3;
        }
    }
}
