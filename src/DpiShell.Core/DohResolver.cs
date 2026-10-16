using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace DpiShell.Core;

/// <summary>
/// Резолв через DNS-over-HTTPS по IP-адресу сервера (сертификаты dns.google и 1.1.1.1 выписаны и на IP).
/// Нужен, чтобы проверка стратегий не зависела от DNS системы: провайдер подменяет даже ответы 8.8.8.8 по UDP.
/// </summary>
public sealed class DohResolver : IDisposable
{
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
    /// <summary>Система не нашла имя или вернула адреса-заглушки, а DoH — нашёл. Разные адреса CDN подменой не считаются.</summary>
    public bool Spoofed => Doh.Length > 0 && (System.Length == 0 || System.All(IsBogus));

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
