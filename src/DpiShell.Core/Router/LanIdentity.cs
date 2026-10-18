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
}
