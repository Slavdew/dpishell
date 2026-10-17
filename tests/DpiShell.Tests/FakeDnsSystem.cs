using DpiShell.Core;

namespace DpiShell.Tests;

/// <summary>Сеть и DNS машины в памяти: адаптеры, их DNS, известные шаблоны DoH. Настоящие реестр и сеть не трогаются.</summary>
public sealed class FakeDnsSystem : IDnsSystem
{
    public static NetAdapter Ethernet => new("{11111111-1111-1111-1111-111111111111}", "Ethernet", "Realtek PCIe 2.5GbE Family Controller", NetAdapterKind.Ethernet, true, true, 12);
    public static NetAdapter Wifi => new("{22222222-2222-2222-2222-222222222222}", "Wi-Fi", "Intel(R) Wi-Fi 6 AX201", NetAdapterKind.Wifi, true, true, 0);
    public static NetAdapter Radmin => new("{33333333-3333-3333-3333-333333333333}", "Radmin VPN", "Famatech Radmin VPN Ethernet Adapter", NetAdapterKind.Ethernet, true, true, 5);
    public static NetAdapter HyperV => new("{44444444-4444-4444-4444-444444444444}", "vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter", NetAdapterKind.Ethernet, true, true, 7);
    public static NetAdapter WireGuard => new("{55555555-5555-5555-5555-555555555555}", "wg0", "WireGuard Tunnel", NetAdapterKind.Other, true, true, 9);
    public static NetAdapter Cable => new("{66666666-6666-6666-6666-666666666666}", "Ethernet 2", "Intel I219", NetAdapterKind.Ethernet, false, false, 3);

    public bool DohSupported { get; set; } = true;
    public List<NetAdapter> List { get; } = [Ethernet, Wifi, Radmin, HyperV, WireGuard, Cable];
    public Dictionary<string, AdapterDns> Dns { get; } = [];
    /// <summary>Как в Windows 11: встроенные шаблоны.</summary>
    public Dictionary<string, string> Known { get; } = new()
    {
        ["8.8.8.8"] = "https://dns.google/dns-query", ["8.8.4.4"] = "https://dns.google/dns-query",
        ["1.1.1.1"] = "https://cloudflare-dns.com/dns-query", ["1.0.0.1"] = "https://cloudflare-dns.com/dns-query",
        ["9.9.9.9"] = "https://dns.quad9.net/dns-query", ["149.112.112.112"] = "https://dns.quad9.net/dns-query",
        ["2001:4860:4860::8888"] = "https://dns.google/dns-query", ["2001:4860:4860::8844"] = "https://dns.google/dns-query",
        ["2606:4700:4700::1111"] = "https://cloudflare-dns.com/dns-query", ["2606:4700:4700::1001"] = "https://cloudflare-dns.com/dns-query",
        ["2620:fe::fe"] = "https://dns.quad9.net/dns-query", ["2620:fe::9"] = "https://dns.quad9.net/dns-query",
    };
    public Dictionary<string, (string Template, bool Fallback)> Registered { get; } = [];
    public List<string> Log { get; } = [];
    /// <summary>Отвечает ли DNS после смены (false — защищённый «не поднялся»).</summary>
    public Func<bool> Resolves { get; set; } = () => true;

    public IReadOnlyList<NetAdapter> Adapters() => List;

    public AdapterDns Read(NetAdapter a) => Dns.TryGetValue(a.Id, out var d) ? d : AdapterDns.Automatic;

    public void Write(NetAdapter a, AdapterDns dns)
    {
        Log.Add($"write {a.Name}");
        Dns[a.Id] = dns;
    }

    public IReadOnlyDictionary<string, string> KnownDoh() => Known;

    public bool RegisterDoh(string ip, string template, bool fallback)
    {
        Log.Add($"register {ip}");
        bool added = Registered.TryAdd(ip, (template, fallback));
        Registered[ip] = (template, fallback);
        return added;
    }

    public void UnregisterDoh(string ip) { Log.Add($"unregister {ip}"); Registered.Remove(ip); }
    public void FlushCache() => Log.Add("flush");
    public Task<bool> ResolvesAsync(CancellationToken ct) => Task.FromResult(Resolves());
}
