using System.Diagnostics;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DpiShell.Core;

/// <summary>
/// Настоящая смена DNS (помощник, SYSTEM). Без PowerShell и без строк-скриптов:
/// <list type="bullet">
/// <item>адреса IPv4 — WMI <c>Win32_NetworkAdapterConfiguration.SetDNSServerSearchOrder</c> (пусто — от DHCP);</item>
/// <item>адреса IPv6 — <c>netsh interface ipv6 set|add dnsservers</c> (у WMI нет IPv6), номер интерфейса — число;</item>
/// <item>шаблон DoH — <c>netsh dnsclient add|set|delete encryption</c> (документировано Microsoft);</item>
/// <item>DoH на адаптере — реестр <c>Dnscache\InterfaceSpecificParameters\{GUID}\DohInterfaceSettings\Doh|Doh6\&lt;адрес&gt;</c>
/// (DohFlags, DohTemplate) — как его пишут «Параметры» Windows 11.</item>
/// </list>
/// netsh вызывается без оболочки: аргументы — отдельные элементы ArgumentList, собраны только из проверенных
/// <see cref="DnsValues"/> адресов, шаблонов и чисел.
/// </summary>
public sealed class WindowsDnsSystem : IDnsSystem
{
    const string Tcpip4 = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\";
    const string Tcpip6 = @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces\";
    const string DnsIf = @"SYSTEM\CurrentControlSet\Services\Dnscache\InterfaceSpecificParameters\";
    const string WellKnown = @"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters\DohWellKnownServers";

    /// <summary>Встроенный DoH — с Windows 11 (сборка 22000).</summary>
    public bool DohSupported => Environment.OSVersion.Version.Build >= 22000;

    public IReadOnlyList<NetAdapter> Adapters() =>
        NetworkInterface.GetAllNetworkInterfaces().Select(n =>
        {
            var ip = n.GetIPProperties();
            bool gw = ip.GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any));
            int v6 = 0;
            if (n.Supports(NetworkInterfaceComponent.IPv6))
            {
                try { v6 = ip.GetIPv6Properties().Index; } catch (NetworkInformationException) { }
            }
            var kind = n.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Wireless80211 => NetAdapterKind.Wifi,
                NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
                    or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => NetAdapterKind.Ethernet,
                _ => NetAdapterKind.Other,
            };
            return new NetAdapter(n.Id, n.Name, n.Description, kind, n.OperationalStatus == OperationalStatus.Up, gw, v6);
        }).ToList();

    static string Key(NetAdapter a) => Guid.Parse(a.Id).ToString("B");

    public AdapterDns Read(NetAdapter a)
    {
        var id = Key(a);
        var v4 = Servers(Tcpip4 + id);
        var v6 = Servers(Tcpip6 + id);
        var doh = new Dictionary<string, DohSetting>();
        foreach (var family in new[] { "Doh", "Doh6" })
        {
            using var k = Registry.LocalMachine.OpenSubKey(DnsIf + id + @"\DohInterfaceSettings\" + family);
            if (k is null) continue;
            foreach (var ip in k.GetSubKeyNames())
            {
                using var s = k.OpenSubKey(ip);
                if (s is null || DnsValues.NormalizeIp(ip) is not { } n) continue;
                var flags = s.GetValue("DohFlags") switch { long l => (ulong)l, int i => (ulong)i, _ => 0UL };
                doh[n] = new DohSetting(flags, s.GetValue("DohTemplate") as string);
            }
        }
        return new AdapterDns(v4.Count == 0, v4, v6.Count == 0, v6, doh);
    }

    static List<string> Servers(string key)
    {
        using var k = Registry.LocalMachine.OpenSubKey(key);
        var raw = k?.GetValue("NameServer") as string ?? "";
        return raw.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries).Select(DnsValues.NormalizeIp).OfType<string>().ToList();
    }

    public void Write(NetAdapter a, AdapterDns dns)
    {
        var id = Key(a);
        // Сначала DoH на адаптере, потом адреса: смена адресов заставляет DNS-клиент перечитать настройки интерфейса.
        using (var root = Registry.LocalMachine.CreateSubKey(DnsIf + id + @"\DohInterfaceSettings"))
        {
            foreach (var family in new[] { "Doh", "Doh6" })
            {
                using var k = root.CreateSubKey(family);
                foreach (var old in k.GetSubKeyNames()) k.DeleteSubKeyTree(old, throwOnMissingSubKey: false);
                foreach (var (ip, s) in dns.Doh.Where(kv => DnsValues.IsIpv6(kv.Key) == (family == "Doh6")))
                {
                    if (DnsValues.NormalizeIp(ip) is not { } n) continue;
                    using var e = k.CreateSubKey(n);
                    e.SetValue("DohFlags", unchecked((long)s.Flags), RegistryValueKind.QWord);
                    if (s.Template is { } t && DnsValues.IsDohTemplate(t)) e.SetValue("DohTemplate", t, RegistryValueKind.String);
                }
            }
        }
        SetIpv4(id, dns.Ipv4Auto ? [] : dns.Ipv4);
        if (a.Ipv6Index > 0) SetIpv6(a.Ipv6Index, dns.Ipv6Auto ? [] : dns.Ipv6);
    }

    static void SetIpv4(string guid, IReadOnlyList<string> servers)
    {
        var list = servers.Select(DnsValues.NormalizeIp).OfType<string>().Where(s => !DnsValues.IsIpv6(s)).ToArray();
        // guid — из Guid.Parse: в запрос попадают только шестнадцатеричные цифры, дефисы и скобки.
        using var q = new ManagementObjectSearcher(@"root\cimv2", $"SELECT * FROM Win32_NetworkAdapterConfiguration WHERE SettingID='{guid.ToUpperInvariant()}'");
        using var found = q.Get();
        var mo = found.Cast<ManagementObject>().FirstOrDefault() ?? throw new InvalidOperationException("Адаптер не найден в WMI.");
        using (mo)
        {
            using var p = mo.GetMethodParameters("SetDNSServerSearchOrder");
            p["DNSServerSearchOrder"] = list.Length == 0 ? null : list;
            using var r = mo.InvokeMethod("SetDNSServerSearchOrder", p, null);
            var code = Convert.ToUInt32(r["ReturnValue"]);
            if (code is not (0 or 1)) throw new InvalidOperationException($"Windows не приняла адреса DNS (код {code}).");
        }
    }

    static void SetIpv6(int index, IReadOnlyList<string> servers)
    {
        var list = servers.Select(DnsValues.NormalizeIp).OfType<string>().Where(DnsValues.IsIpv6).ToList();
        var name = "name=" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (list.Count == 0) { Netsh("interface", "ipv6", "set", "dnsservers", name, "source=dhcp"); return; }
        Netsh("interface", "ipv6", "set", "dnsservers", name, "source=static", "address=" + list[0], "register=primary", "validate=no");
        for (int i = 1; i < list.Count; i++)
            Netsh("interface", "ipv6", "add", "dnsservers", name, "address=" + list[i], "index=" + (i + 1), "validate=no");
    }

    public IReadOnlyDictionary<string, string> KnownDoh()
    {
        var map = new Dictionary<string, string>();
        using var k = Registry.LocalMachine.OpenSubKey(WellKnown);
        if (k is null) return map;
        foreach (var ip in k.GetSubKeyNames())
        {
            using var s = k.OpenSubKey(ip);
            if (s?.GetValue("Template") is string t && DnsValues.NormalizeIp(ip) is { } n) map[n] = t;
        }
        return map;
    }

    public bool RegisterDoh(string ip, string template, bool fallback)
    {
        if (DnsValues.NormalizeIp(ip) is not { } n || n != ip || !DnsValues.IsDohTemplate(template)) throw new ArgumentException("Недопустимый адрес или шаблон.");
        // autoupgrade=no: шаблон только известен системе, включается на адаптере (DohFlags) — как у встроенных.
        string[] args = ["dnsclient", "", "encryption", "server=" + n, "dohtemplate=" + template, "autoupgrade=no", "udpfallback=" + (fallback ? "yes" : "no")];
        args[1] = "add";
        if (TryNetsh(args)) return true;
        args[1] = "set";   // уже есть (добавлен раньше) — обновить
        Netsh(args);
        return false;
    }

    public void UnregisterDoh(string ip)
    {
        if (DnsValues.NormalizeIp(ip) is not { } n || n != ip) throw new ArgumentException("Недопустимый адрес.");
        TryNetsh("dnsclient", "delete", "encryption", "server=" + n, "protocol=doh");
    }

    [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
    static extern bool DnsFlushResolverCacheNative();

    public void FlushCache()
    {
        try { DnsFlushResolverCacheNative(); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
    }

    public async Task<bool> ResolvesAsync(CancellationToken ct)
    {
        foreach (var host in new[] { "example.com", "www.wikipedia.org" })
        {
            using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
            t.CancelAfter(TimeSpan.FromSeconds(4));
            try { if ((await Dns.GetHostAddressesAsync(host, t.Token)).Length > 0) return true; }
            catch (Exception e) when (e is System.Net.Sockets.SocketException or OperationCanceledException && !ct.IsCancellationRequested) { }
        }
        return false;
    }

    static void Netsh(params string[] args)
    {
        if (!TryNetsh(args)) throw new InvalidOperationException("Windows не приняла настройку DNS (netsh " + string.Join(' ', args.Take(4)) + ").");
    }

    static bool TryNetsh(params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("netsh не запустился.");
        _ = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(15_000)) { try { p.Kill(); } catch (InvalidOperationException) { } return false; }
        return p.ExitCode == 0;
    }
}
