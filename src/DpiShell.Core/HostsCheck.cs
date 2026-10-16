using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;

namespace DpiShell.Core;

public enum EndpointOutcome { Ok, NoConnect, Timeout, BadCertificate, TlsFailed, BadStatus }

/// <summary>Итог живой проверки «домен на этом адресе»: TLS с SNI домена, валидный сертификат, HTTP-ответ.</summary>
public sealed record EndpointCheck(EndpointOutcome Outcome, double Seconds, string Detail)
{
    public bool Ok => Outcome == EndpointOutcome.Ok;
}

public interface IEndpointProbe
{
    Task<EndpointCheck> CheckAsync(string host, IPAddress ip, CancellationToken ct = default);
}

/// <summary>
/// TCP к адресу (имя не разрешается), TLS с SNI = домен, обычная проверка цепочки и имени .NET (колбэк лишь запоминает
/// причину отказа и принимает только SslPolicyErrors.None — ровно как проверка по умолчанию), HEAD / с кодом &lt; 500.
/// Это доказывает, что узел по этому адресу законно обслуживает домен (ТЗ v2 §3 п. 3).
/// </summary>
public sealed class TlsEndpointProbe : IEndpointProbe
{
    public int Port { get; init; } = 443;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    public async Task<EndpointCheck> CheckAsync(string host, IPAddress ip, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        SslPolicyErrors policy = SslPolicyErrors.None;
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = Timeout,
            PooledConnectionLifetime = TimeSpan.Zero,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectCallback = async (_, token) =>
            {
                var s = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try { await s.ConnectAsync(new IPEndPoint(ip, Port), token); return new NetworkStream(s, ownsSocket: true); }
                catch { s.Dispose(); throw; }
            },
            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, _, _, errors) => { policy = errors; return errors == SslPolicyErrors.None; },
            },
        };
        using var http = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try
        {
            var url = Port == 443 ? $"https://{host}/" : $"https://{host}:{Port}/";
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0 Safari/537.36");
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var code = (int)resp.StatusCode;
            return code < 500
                ? new(EndpointOutcome.Ok, sw.Elapsed.TotalSeconds, $"HTTP {code}")
                : new(EndpointOutcome.BadStatus, sw.Elapsed.TotalSeconds, $"HTTP {code}");
        }
        catch (Exception e) when (policy != SslPolicyErrors.None && e is HttpRequestException or AuthenticationException or IOException)
        {
            return new(EndpointOutcome.BadCertificate, sw.Elapsed.TotalSeconds,
                policy.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) ? "чужой сертификат" : "сертификат не прошёл проверку");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(EndpointOutcome.Timeout, sw.Elapsed.TotalSeconds, "не отвечает");
        }
        catch (HttpRequestException e)
        {
            for (Exception? x = e; x is not null; x = x.InnerException)
            {
                if (x is AuthenticationException) return new(EndpointOutcome.TlsFailed, sw.Elapsed.TotalSeconds, "TLS не установлен");
                if (x is SocketException se) return new(EndpointOutcome.NoConnect, sw.Elapsed.TotalSeconds, $"не отвечает ({se.SocketErrorCode})");
            }
            return new(e.HttpRequestError == HttpRequestError.SecureConnectionError ? EndpointOutcome.TlsFailed : EndpointOutcome.NoConnect,
                sw.Elapsed.TotalSeconds, e.Message);
        }
        catch (IOException e)
        {
            return new(EndpointOutcome.TlsFailed, sw.Elapsed.TotalSeconds, $"соединение оборвано ({e.Message})");
        }
    }
}

/// <summary>Адреса домена «как через DNS», но мимо hosts (иначе перепроверка увидит собственную запись).</summary>
public interface IHostsDns
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct = default);
}

/// <summary>DoH по IP (8.8.8.8, 1.1.1.1) — hosts не читает.</summary>
public sealed class DohHostsDns(DohResolver doh) : IHostsDns
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct = default) => doh.ResolveAsync(host, ct);
}

/// <summary>Владелец сети по RDAP: handle, name и handle связанных организаций (registrant).</summary>
public sealed record RdapOwner(string? Handle, string? Name, IReadOnlyList<string> Entities)
{
    public bool Matches(IEnumerable<string> anyOf) => anyOf.Any(want =>
        new[] { Handle, Name }.Concat(Entities).Any(v => v is not null && v.Contains(want, StringComparison.OrdinalIgnoreCase)));

    public override string ToString() => Name ?? Handle ?? Entities.FirstOrDefault() ?? "?";
}

public interface IRdapClient
{
    /// <summary>null — RDAP недоступен (проверка «не выполнена»).</summary>
    Task<RdapOwner?> LookupAsync(IPAddress ip, CancellationToken ct = default);
}

public sealed class RdapClient : IRdapClient, IDisposable
{
    readonly HttpClient _http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) }) { Timeout = TimeSpan.FromSeconds(5) };

    public async Task<RdapOwner?> LookupAsync(IPAddress ip, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://rdap.org/ip/{ip}");
            req.Headers.Accept.ParseAdd("application/rdap+json");
            using var resp = await _http.SendAsync(req, ct);
            return resp.IsSuccessStatusCode ? Parse(await resp.Content.ReadAsStringAsync(ct)) : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested) { return null; }
    }

    public static RdapOwner? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.ValueKind != JsonValueKind.Object) return null;
        string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var entities = new List<string>();
        if (r.TryGetProperty("entities", out var ents) && ents.ValueKind == JsonValueKind.Array)
            foreach (var e in ents.EnumerateArray())
                if (Str(e, "handle") is { } h) entities.Add(h);
        var owner = new RdapOwner(Str(r, "handle"), Str(r, "name"), entities);
        return owner.Handle is null && owner.Name is null && entities.Count == 0 ? null : owner;
    }

    public void Dispose() => _http.Dispose();
}

public enum HostsVerdict
{
    /// <summary>Адрес рабочий, сертификат валиден, а через DNS домен не открывается — запись чинит.</summary>
    Install,
    /// <summary>Через DNS домен и так работает — запись не нужна.</summary>
    NotNeeded,
    /// <summary>Адрес не отвечает / TLS не установлен / HTTP ≥ 500.</summary>
    Unreachable,
    /// <summary>Сертификат не на этот домен или не проходит проверку.</summary>
    BadCertificate,
    /// <summary>RDAP: сеть принадлежит не тому, кого ждём.</summary>
    OwnerMismatch,
    /// <summary>Домен вне белого списка или адрес не публичный — даже не проверяли.</summary>
    Rejected,
}

public enum DnsState { Works, Fails, Unknown }
public enum OwnerState { Match, Mismatch, Unknown }

/// <summary>Строка таблицы «Домен | Адрес | Владелец | Проверка | Сейчас».</summary>
public sealed record HostsCheckRow(
    string Host, string Ip, HostsVerdict Verdict, EndpointCheck? Live,
    OwnerState Owner, string? OwnerName, DnsState Dns, string DnsDetail)
{
    public HostsEntry Entry => new(Host, Ip);
    public bool Install => Verdict == HostsVerdict.Install;
}

public sealed record HostsCheckReport(IReadOnlyList<HostsCheckRow> Rows, DateTime At)
{
    public IReadOnlyList<HostsEntry> ToInstall => Rows.Where(r => r.Install).Select(r => r.Entry).ToList();
    /// <summary>Нормальный исход: всё и так работает или кандидаты не подходят — писать нечего.</summary>
    public bool NothingToFix => ToInstall.Count == 0;
}

/// <summary>
/// Проверка кандидатов по ТЗ v2 §3: белый список и публичный адрес → живая проверка адреса → RDAP-владелец (вторичный признак:
/// недоступен — не мешает) → сравнение с адресами из DoH: ставим, только если через DNS домен не работает.
/// </summary>
public sealed class HostsVerifier(IEndpointProbe probe, IHostsDns dns, IRdapClient? rdap, int parallel = 12)
{
    readonly SemaphoreSlim _gate = new(Math.Max(1, parallel));

    async Task<T> Gated<T>(Func<Task<T>> f, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await f(); }
        finally { _gate.Release(); }
    }

    public async Task<HostsCheckReport> CheckAsync(IEnumerable<HostsCandidate> candidates, IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        var list = candidates.ToList();
        var dnsCache = new Dictionary<string, Task<(DnsState, string)>>(StringComparer.OrdinalIgnoreCase);
        var rdapCache = new Dictionary<string, Task<RdapOwner?>>();
        var jobs = new List<Task<HostsCheckRow>>();
        int total = list.Sum(c => c.Ips.Count), done = 0;

        foreach (var c in list)
        {
            var host = HostsRules.Normalize(c.Host);
            foreach (var ipText in c.Ips.Distinct())
            {
                if (!HostsRules.IsAllowedHost(host) || !HostsRules.TryParsePublic(ipText, out var ip))
                {
                    jobs.Add(Task.FromResult(new HostsCheckRow(host, ipText, HostsVerdict.Rejected, null, OwnerState.Unknown, null, DnsState.Unknown, "")));
                    continue;
                }
                if (!dnsCache.TryGetValue(host, out var dnsTask))
                    dnsCache[host] = dnsTask = DnsAsync(host, ct);
                jobs.Add(RowAsync(host, ip, c.OwnerAnyOf, dnsTask, rdapCache, ct));
            }
        }
        var rows = new List<HostsCheckRow>();
        foreach (var j in jobs)
        {
            rows.Add(await j);
            progress?.Report((++done, total));
        }
        return new HostsCheckReport(rows, DateTime.Now);
    }

    async Task<HostsCheckRow> RowAsync(string host, IPAddress ip, List<string> ownerAnyOf, Task<(DnsState, string)> dnsTask,
        Dictionary<string, Task<RdapOwner?>> rdapCache, CancellationToken ct)
    {
        var live = await Gated(() => probe.CheckAsync(host, ip, ct), ct);
        var (dnsState, dnsDetail) = await dnsTask;
        if (!live.Ok)
            return new(host, ip.ToString(), live.Outcome == EndpointOutcome.BadCertificate ? HostsVerdict.BadCertificate : HostsVerdict.Unreachable,
                live, OwnerState.Unknown, null, dnsState, dnsDetail);

        RdapOwner? owner = null;
        if (rdap is not null)
        {
            Task<RdapOwner?> t;
            lock (rdapCache)
                if (!rdapCache.TryGetValue(ip.ToString(), out t!)) rdapCache[ip.ToString()] = t = rdap.LookupAsync(ip, ct);
            owner = await t;
        }
        var ownerState = owner is null ? OwnerState.Unknown : owner.Matches(ownerAnyOf) ? OwnerState.Match : OwnerState.Mismatch;
        var verdict = ownerState == OwnerState.Mismatch ? HostsVerdict.OwnerMismatch
            : dnsState == DnsState.Works ? HostsVerdict.NotNeeded
            : HostsVerdict.Install;
        return new(host, ip.ToString(), verdict, live, ownerState, owner?.ToString(), dnsState, dnsDetail);
    }

    /// <summary>
    /// Тот же тест к адресам из DoH (до трёх). Работает хоть один — DNS «работает», запись не нужна (hosts чинит, а не дублирует).
    /// DoH не читает hosts, поэтому перепроверка не видит собственную запись. DoH молчит — «не проверен», запись допускается:
    /// адрес-кандидат уже прошёл проверку сертификата.
    /// </summary>
    async Task<(DnsState, string)> DnsAsync(string host, CancellationToken ct)
    {
        IPAddress[] addrs;
        try { addrs = await dns.ResolveAsync(host, ct); }
        catch (Exception e) when (e is not OperationCanceledException) { addrs = []; }
        if (addrs.Length == 0) return (DnsState.Unknown, "DNS не ответил");
        var checks = await Task.WhenAll(addrs.Take(3).Select(a => Gated(() => probe.CheckAsync(host, a, ct), ct)));
        var ok = addrs.Zip(checks).FirstOrDefault(x => x.Second.Ok);
        return ok.First is not null
            ? (DnsState.Works, $"{ok.First}: {ok.Second.Detail}")
            : (DnsState.Fails, $"{addrs[0]}: {checks[0].Detail}");
    }
}
