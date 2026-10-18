using System.Net;
using System.Net.Sockets;

namespace DpiShell.Core;

/// <summary>Откуда проверка берёт адрес сайта: DoH (<see cref="DohResolver"/>) или DNS системы (<see cref="SystemResolver"/>).</summary>
public interface IAddressResolver
{
    /// <summary>IPv4-адреса имени; пусто — имени нет или DNS не ответил.</summary>
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct = default);
}

/// <summary>DNS системы — путь браузера: hosts, затем DNS адаптера (провайдера, если не меняли).</summary>
public sealed class SystemResolver : IAddressResolver
{
    public static readonly SystemResolver Instance = new();

    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct = default)
    {
        try { return await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct); }
        catch (SocketException) { return []; }
    }
}

/// <summary>
/// Проверка по пути браузера: адрес из DNS системы. Ok — открылось и так. HostsIp — адрес взят из записи hosts.
/// Пустой System — DNS системы имени не нашёл (проверять нечего, браузер не откроет).
/// </summary>
public sealed record BrowserPath(IPAddress[] System, IPAddress[] Doh, bool Ok, string? HostsIp, IReadOnlyList<ProbeResult> Probes)
{
    public bool FromHosts => HostsIp is not null;

    /// <summary>Почему браузер не откроет: «DNS провайдера подменяет адрес» или «запись в hosts ведёт на неработающий адрес».</summary>
    public string Reason => FromHosts ? HostsReason : DnsReason;

    public const string DnsReason = "DNS провайдера подменяет адрес";
    public const string HostsReason = "запись в hosts ведёт на неработающий адрес";
}

/// <summary>
/// Проверка по DoH честна для стратегии, но браузер берёт адрес у DNS системы (architecture.md «Проверка одной цели», п. 6).
/// Где адреса расходятся, цель, открывшаяся по DoH, проверяется ещё раз по адресу из DNS системы — по очереди, только они.
/// Только для «Проверить сейчас» и проверки после «Включить»: подбор и фоновые проверки не трогаются.
/// </summary>
public static class BrowserCheck
{
    /// <summary>
    /// Один и тот же адрес у DNS системы и DoH: общий адрес или общая подсеть /24 (соседние узлы CDN — не подмена).
    /// DoH не ответил — сравнивать не с чем: проверка и так шла через DNS системы.
    /// </summary>
    public static bool SameAddresses(IReadOnlyCollection<IPAddress> system, IReadOnlyCollection<IPAddress> doh)
    {
        if (doh.Count == 0) return true;
        if (system.Count == 0) return false;
        var nets = doh.Select(Net24).ToHashSet();
        return system.Any(ip => nets.Contains(Net24(ip)));
    }

    static string Net24(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return ip.AddressFamily == AddressFamily.InterNetwork ? $"{b[0]}.{b[1]}.{b[2]}" : ip.ToString();
    }

    /// <summary>
    /// Дописать к открывшимся целям проверку по DNS системы, где адреса расходятся. probe — та же проверка цели, но с заданным
    /// резолвером; hostsIp — адрес имени из hosts (null — записи нет).
    /// </summary>
    public static async Task<IReadOnlyList<TargetVerdict>> AddAsync(
        IReadOnlyList<TargetVerdict> verdicts, IAddressResolver system, IAddressResolver doh,
        Func<Target, IAddressResolver, CancellationToken, Task<IReadOnlyList<ProbeResult>>> probe,
        Func<string, string?> hostsIp, CancellationToken ct = default)
    {
        var list = verdicts.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            var v = list[i];
            if (!v.Ok || !Uri.TryCreate(v.Target.Url, UriKind.Absolute, out var u) || IPAddress.TryParse(u.Host, out _)) continue;
            var sys = await system.ResolveAsync(u.Host, ct);
            var viaDoh = await doh.ResolveAsync(u.Host, ct);
            if (SameAddresses(sys, viaDoh)) continue;
            var hosts = hostsIp(u.Host) is { } h && sys.Any(ip => ip.ToString() == h) ? h : null;
            if (sys.Length == 0) { list[i] = v with { Browser = new(sys, viaDoh, false, hosts, []) }; continue; }
            var probes = await probe(v.Target, system, ct);
            list[i] = v with { Browser = new(sys, viaDoh, probes.All(p => p.Ok), hosts, probes) };
        }
        return list;
    }

    /// <summary>Адрес имени из hosts системы (первая подходящая строка — как у Windows); файла нет или не читается — null.</summary>
    public static Func<string, string?> SystemHosts()
    {
        Dictionary<string, string> map = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = HostsFileStore.SystemHostsPath;
            if (File.Exists(path))
                foreach (var e in HostsText.Decode(File.ReadAllBytes(path)).Entries()) map.TryAdd(e.Host, e.Ip);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return host => map.GetValueOrDefault(HostsRules.Normalize(host));
    }
}
