using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace DpiShell.Core;

/// <summary>
/// Резолв через DNS-over-HTTPS по IP-адресу сервера (сертификаты dns.google и 1.1.1.1 выписаны и на IP).
/// Нужен, чтобы проверка стратегий не зависела от DNS системы: провайдер подменяет даже ответы 8.8.8.8 по UDP.
/// </summary>
public sealed class DohResolver : IAddressResolver, IResolverSource, IDisposable
{
    /// <summary>Сколько имён подряд-вообще не нашли ни одного отвечающего DoH-сервера, чтобы считать защищённый DNS недоступным.</summary>
    const int GiveUpAfter = 3;
    int _failedHosts;

    /// <summary>Хоть для одного имени ни один DoH-сервер не ответил: проверка взяла его адрес у DNS системы (<see cref="Probe"/> переходит на него сама).</summary>
    public bool FellBack => Volatile.Read(ref _failedHosts) > 0;

    /// <summary>Защищённый DNS не отвечает (три имени без единого ответа): дальше не ждём таймауты, адреса берёт система.</summary>
    public bool Unreachable => Volatile.Read(ref _failedHosts) >= GiveUpAfter;

    public string AddressSource => FellBack ? CheckDns.SourceFallback : CheckDns.SourceDoh;

    static readonly string[] Endpoints =
    [
        "https://8.8.8.8/resolve?name={0}&type=A",
        "https://1.1.1.1/dns-query?name={0}&type=A",
    ];

    readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(3) })
    { Timeout = TimeSpan.FromSeconds(5) };
    readonly ConcurrentDictionary<string, Task<IPAddress[]>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct = default) =>
        _cache.GetOrAdd(host, h => QueryAsync(h, CancellationToken.None)).WaitAsync(ct);

    /// <summary>Пустой массив — ни один DoH-сервер не ответил или имени нет.</summary>
    async Task<IPAddress[]> QueryAsync(string host, CancellationToken ct)
    {
        if (Unreachable) return [];
        foreach (var ep in Endpoints)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, string.Format(ep, Uri.EscapeDataString(host)));
                    req.Headers.Accept.ParseAdd("application/dns-json");
                    using var resp = await _http.SendAsync(req, ct);
                    if (!resp.IsSuccessStatusCode) break;
                    return Parse(await resp.Content.ReadAsStringAsync(ct));
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested) { }
            }
        }
        Interlocked.Increment(ref _failedHosts);
        return [];
    }

    /// <summary>Ответ JSON API (общий формат Google и Cloudflare): только A-записи, CNAME пропускаются.</summary>
    public static IPAddress[] Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("Answer", out var answer)) return [];
        return answer.EnumerateArray()
            .Where(a => a.GetProperty("type").GetInt32() == 1)
            .Select(a => IPAddress.TryParse(a.GetProperty("data").GetString(), out var ip) ? ip : null)
            .Where(ip => ip is { AddressFamily: AddressFamily.InterNetwork })
            .Select(ip => ip!).ToArray();
    }

    public void Dispose() => _http.Dispose();
}

public sealed record DnsComparison(string Host, IPAddress[] System, IPAddress[] Doh, string? SystemError)
{
    /// <summary>Заглушка стоит в hosts самого пользователя (блокировщик рекламы): причина — файл, а не провайдер.</summary>
    public bool FromHosts { get; init; }

    /// <summary>
    /// Система вернула только адреса-заглушки, а DoH нашёл настоящий. Разные адреса CDN подменой не считаются; пустой ответ системы — тоже
    /// (DNS ещё не поднялся после старта, сна или смены сети: это «нет ответа», а не «ответ подменили»).
    /// </summary>
    public bool Spoofed => !FromHosts && Doh.Length > 0 && System.Length > 0 && System.All(IsBogus);

    /// <summary>Система ничего не ответила, а DoH ответил: подменой не считается, в таблице видно отдельно.</summary>
    public bool SystemSilent => Doh.Length > 0 && System.Length == 0;

    static bool IsBogus(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return ip.AddressFamily == AddressFamily.InterNetwork &&
               (b[0] is 0 or 127 or 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] is >= 16 and <= 31);
    }
}

public static class DnsCheck
{
    public static async Task<DnsComparison> CompareAsync(string host, DohResolver doh, CancellationToken ct = default)
    {
        IPAddress[] sys = []; string? err = null;
        try { sys = (await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct)); }
        catch (SocketException e) { err = e.Message; }
        return new(host, sys, await doh.ResolveAsync(host, ct), err);
    }
}

/// <summary>
/// «Сравнить» в выборе DNS: время ответа защищённого DNS с этого компьютера — запрос RFC 8484 (GET ?dns=, A-запись известного имени).
/// Меряется второй запрос по уже открытому соединению: первый включает установку TLS и сильно зависит от случая.
/// </summary>
public static class DohPing
{
    /// <summary>Имя для запроса: есть всегда и не блокируется.</summary>
    public const string Name = "example.com";

    /// <summary>DNS-запрос A для имени в формате RFC 1035 (id 0 — как советует RFC 8484 для кэша), base64url без «=».</summary>
    public static string Query(string name)
    {
        var bytes = new List<byte> { 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 };   // id, RD, один вопрос
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
        }
        bytes.AddRange(new byte[] { 0, 0, 1, 0, 1 });   // конец имени, тип A, класс IN
        return Convert.ToBase64String(bytes.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Миллисекунды ответа; null — сервер не ответил DNS-ответом за отведённое время.</summary>
    public static async Task<double?> MeasureAsync(string template, HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        using var http = new HttpClient(handler ?? new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(3) }, disposeHandler: handler is null)
            { Timeout = TimeSpan.FromSeconds(5) };
        var url = template + (template.Contains('?') ? "&" : "?") + "dns=" + Query(Name);
        double? last = null;
        for (int i = 0; i < 2; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Accept.ParseAdd("application/dns-message");
                using var resp = await http.SendAsync(req, ct);
                var body = await resp.Content.ReadAsByteArrayAsync(ct);
                // Ответ DNS — с битом QR; иначе это не DNS (страница-заглушка, ошибка).
                if (!resp.IsSuccessStatusCode || body.Length < 12 || (body[2] & 0x80) == 0) return null;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { return null; }
            last = sw.Elapsed.TotalMilliseconds;   // второй запрос — по открытому соединению
        }
        return last;
    }
}
