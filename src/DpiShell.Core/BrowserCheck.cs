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

    /// <summary>Подмена — только явная (<see cref="BrowserCheck.IsSpoof"/>): чужой адрес, заглушка или чужой сертификат.</summary>
    public bool Spoofed => !Ok && !FromHosts && BrowserCheck.IsSpoof(System, Probes);

    /// <summary>
    /// Почему не откроется по пути Windows: подмена адреса, запись в hosts или просто «через DNS Windows не открылось»
    /// (браузер при этом может ходить через свой защищённый DNS, а CDN законно отдаёт разным DNS разные адреса).
    /// </summary>
    public string Reason => FromHosts ? HostsReason : Spoofed ? DnsReason : WindowsOnlyReason;

    public const string DnsReason = "DNS провайдера подменяет адрес";
    public const string HostsReason = "запись в hosts ведёт на неработающий адрес";
    public const string WindowsOnlyReason = "Программы, которые берут адрес у Windows (Discord, игры, часть браузеров), сюда не достучатся";
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

    /// <summary>
    /// Явная подмена: адрес из DNS системы — не интернетный (заглушка, частная или зарезервированная сеть), провайдер ответил
    /// страницей-заглушкой или сертификат по адресу не для этого сайта. Просто другой публичный адрес — не подмена.
    /// </summary>
    public static bool IsSpoof(IReadOnlyCollection<IPAddress> system, IReadOnlyCollection<ProbeResult> probes) =>
        system.Any(IsNonPublic)
        || probes.Any(p => p.Outcome == ProbeOutcome.ForeignRedirect
            || p.Outcome == ProbeOutcome.TlsFailed && (p.Detail.Contains("certificate", StringComparison.OrdinalIgnoreCase) || p.Detail.Contains("сертификат", StringComparison.OrdinalIgnoreCase)));

    /// <summary>0.x, 10.x, 127.x, 169.254.x, 172.16/12, 192.168.x, 100.64/10, 192.0.0/24, 192.0.2/24, 198.18/15, 198.51.100/24, 203.0.113/24, 224+.</summary>
    public static bool IsNonPublic(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.IPv6Any);
        var b = ip.GetAddressBytes();
        return b[0] is 0 or 10 or 127 or >= 224
            || b[0] == 169 && b[1] == 254
            || b[0] == 172 && b[1] is >= 16 and <= 31
            || b[0] == 192 && b[1] == 168
            || b[0] == 100 && b[1] is >= 64 and <= 127
            || b[0] == 192 && b[1] == 0 && b[2] is 0 or 2
            || b[0] == 198 && b[1] is 18 or 19
            || b[0] == 198 && b[1] == 51 && b[2] == 100
            || b[0] == 203 && b[1] == 0 && b[2] == 113;
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
