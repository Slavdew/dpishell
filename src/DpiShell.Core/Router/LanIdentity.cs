using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace DpiShell.Core.Router;

/// <summary>
/// Опознать роутер в своей сети без входа: MAC по ARP (iphlpapi SendARP, прав не нужно). По нему окно отличает
/// «тот же роутер, он просто не отвечает» (обновление, перезагрузка) от чужой сети с тем же адресом шлюза.
/// </summary>
public static class LanIdentity
{
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    static extern int SendARP(int destIp, int srcIp, byte[] mac, ref int length);

    /// <summary>MAC устройства в своей сети («AABBCCDDEEFF»); null — адрес не IPv4, не в этой сети или не ответил.</summary>
    public static string? Mac(string address)
    {
        if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return null;
        var mac = new byte[6];
        int length = mac.Length;
        try
        {
            if (SendARP(BitConverter.ToInt32(ip.GetAddressBytes(), 0), 0, mac, ref length) != 0 || length == 0) return null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
        return Convert.ToHexString(mac, 0, Math.Min(length, mac.Length));
    }

    /// <summary>
    /// MAC адаптера этого ПК, через который он ходит к роутеру (совместная работа, роутер различает ПК по MAC). Берётся текущий активный адаптер:
    /// Windows может случайно менять MAC Wi-Fi по сетям, поэтому запоминать его нельзя — окно спрашивает его каждый раз.
    /// Вид «aa:bb:cc:dd:ee:ff»; null — не определить (нет маршрута, петля, адаптер без MAC).
    /// </summary>
    public static string? OwnMac(string address)
    {
        try
        {
            if (!IPAddress.TryParse(address, out var ip))
                ip = Dns.GetHostAddresses(address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (ip is null || IPAddress.IsLoopback(ip)) return null;
            using var s = new Socket(ip.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(new IPEndPoint(ip, 80));   // UDP: пакетов не уходит, выбирается только маршрут и адрес отправителя
            if ((s.LocalEndPoint as IPEndPoint)?.Address is not { } local) return null;
            var nic = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                                     && n.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(local)));
            return RouterClient.NormalizeMac(nic?.GetPhysicalAddress().ToString());
        }
        catch (Exception e) when (e is SocketException or System.Net.NetworkInformation.NetworkInformationException or ArgumentException) { return null; }
    }
}
