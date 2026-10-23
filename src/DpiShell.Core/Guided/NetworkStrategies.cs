using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DpiShell.Core;

/// <summary>Идентификатор текущей сети: короткий хеш (шлюз + MAC шлюза). Самих адресов нигде не хранится и в отчёты не попадает.</summary>
public interface INetworkIdentity
{
    string? CurrentId();
}

public sealed class WindowsNetworkIdentity : INetworkIdentity
{
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    static extern int SendARP(uint destIp, uint srcIp, byte[] mac, ref int macLen);

    public string? CurrentId()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                         .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                         .OrderBy(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 ? 0 : 1))
            {
                var d = nic.Description;
                if (new[] { "Virtual", "VPN", "TAP", "TUN", "Hyper-V", "VMware", "Wintun", "WireGuard" }.Any(x => d.Contains(x, StringComparison.OrdinalIgnoreCase))) continue;
                var gw = nic.GetIPProperties().GatewayAddresses
                    .Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !a.Equals(System.Net.IPAddress.Any));
                if (gw is null) continue;
                return Hash(gw.ToString(), Mac(gw), nic.NetworkInterfaceType.ToString());
            }
        }
        catch (Exception e) when (e is NetworkInformationException or InvalidOperationException) { }
        return null;
    }

    static string Mac(System.Net.IPAddress gw)
    {
        try
        {
            var mac = new byte[6]; int len = 6;
            return SendARP(BitConverter.ToUInt32(gw.GetAddressBytes(), 0), 0, mac, ref len) == 0 ? Convert.ToHexString(mac, 0, Math.Min(len, 6)) : "";
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return ""; }
    }

    public static string Hash(params string[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", parts))))[..16].ToLowerInvariant();
}

/// <summary>Стратегия по сетям: «в этой сети сайты открылись на такой-то стратегии». Файл — в папке окна; путь null — только в памяти (тесты).</summary>
public sealed class NetworkStrategies(string? path = null)
{
    public const int MaxEntries = 30;
    public sealed record Entry(string Strategy, DateTime Seen);

    readonly Dictionary<string, Entry> _map = Load(path);

    public int Count => _map.Count;

    public string? StrategyFor(string networkId) => _map.TryGetValue(networkId, out var e) ? e.Strategy : null;

    public void Remember(string networkId, string strategy)
    {
        if (_map.TryGetValue(networkId, out var old) && old.Strategy == strategy) return;
        _map[networkId] = new(strategy, DateTime.UtcNow);
        while (_map.Count > MaxEntries) _map.Remove(_map.OrderBy(p => p.Value.Seen).First().Key);
        Save();
    }

    public void Forget(string networkId) { if (_map.Remove(networkId)) Save(); }

    static Dictionary<string, Entry> Load(string? path)
    {
        try { if (path is not null && File.Exists(path)) return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path)) ?? []; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return [];
    }

    void Save()
    {
        if (path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(_map));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
