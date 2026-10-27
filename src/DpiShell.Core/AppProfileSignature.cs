using System.Text;
using System.Text.RegularExpressions;
using DpiShell.Core.Localization;

namespace DpiShell.Core;

/// <summary>
/// Сигнатура пакета в профиле программы: длина полезной нагрузки и её начало. Фильтр WinDivert собирается только из этих полей
/// (текст фильтра от окна не принимается), пишется в файл рядом со списком адресов профиля и подаётся winws как <c>--wf-raw-part=@файл</c>.
/// </summary>
public static partial class AppProfiles
{
    /// <summary>Предел полезной нагрузки (MTU).</summary>
    public const int MaxPayloadLength = 1500;
    /// <summary>Сколько байт начала нагрузки можно задать.</summary>
    public const int MaxSignatureBytes = 16;
    /// <summary>Предел итогового фильтра WinDivert у winws — 16 КБ; часть уходит на общий фильтр портов, остальное — на наши части.</summary>
    public const int WinDivertFilterLimit = 16 * 1024;
    public const int MaxFilterBytes = 12 * 1024;

    [GeneratedRegex(@"^([0-9a-f]{2}){1,16}\z")] private static partial Regex HexRx();

    /// <summary>У протокола профиля есть узкий фильтр WinDivert: сигнатура пакета или свой фильтр (порты тогда в общий фильтр не попадают, адреса необязательны).</summary>
    public static bool HasSignature(AppProfile p, bool tcp) =>
        tcp ? p.TcpPayloadLength > 0 || p.TcpPayloadHex.Length > 0 || p.TcpCustomFilter.Length > 0
            : p.UdpPayloadLength > 0 || p.UdpPayloadHex.Length > 0 || p.UdpCustomFilter.Length > 0;

    /// <summary>Что не так с сигнатурой одного протокола; null — в порядке (или не задана). <paramref name="present"/> — протокол в профиле есть.</summary>
    public static string? SignatureProblem(int length, string hex, bool present)
    {
        if (length == 0 && hex.Length == 0) return null;
        if (!present) return L.T("core.profile.sig_no_ports");
        if (length is < 0 or > MaxPayloadLength) return L.T("core.profile.sig_length", new { max = MaxPayloadLength });
        if (hex.Length > 0 && !HexRx().IsMatch(hex)) return L.T("core.profile.sig_hex", new { max = MaxSignatureBytes });
        if (length > 0 && hex.Length / 2 > length) return L.T("core.profile.sig_longer");
        return null;
    }

    /// <summary>Имя файла фильтра протокола профиля (в lists\, под маской app-*.txt: лишние чистятся вместе с адресами).</summary>
    public static string FilterFileName(string id, bool tcp) => $"app-{id}-{(tcp ? "tcp" : "udp")}.filter.txt";

    /// <summary>
    /// Фильтр WinDivert для протокола профиля или null, если сигнатуры нет. Вид: <c>outbound and tcp.DstPort = 12001 and tcp.PayloadLength = 79 and tcp.Payload32[0] = 0x0000044d</c>;
    /// диапазон — <c>&gt;= and &lt;=</c>, несколько диапазонов — через <c>or</c> в скобках, хвост короче 4 байт — по одному байту.
    /// </summary>
    public static string? Filter(AppProfile p, bool tcp)
    {
        if (!HasSignature(p, tcp)) return null;
        if ((tcp ? p.TcpCustomFilter : p.UdpCustomFilter) is { Length: > 0 } custom) return CustomFilter.Problem(custom, tcp) is null ? custom.Trim() : null;
        string ports = tcp ? p.TcpPorts : p.UdpPorts, hex = tcp ? p.TcpPayloadHex : p.UdpPayloadHex;
        int length = tcp ? p.TcpPayloadLength : p.UdpPayloadLength;
        if (ports.Length == 0 || SignatureProblem(length, hex, true) is not null) return null;
        string pr = tcp ? "tcp" : "udp";
        var elems = ports.Split(',').Select(r =>
        {
            var e = r.Split('-');
            return e.Length == 2 ? $"{pr}.DstPort >= {e[0]} and {pr}.DstPort <= {e[1]}" : $"{pr}.DstPort = {e[0]}";
        }).ToList();
        var sb = new StringBuilder("outbound and ");
        sb.Append(elems.Count == 1 ? elems[0] : "(" + string.Join(" or ", elems.Select(e => e.Contains(" and ") ? "(" + e + ")" : e)) + ")");
        if (length > 0) sb.Append($" and {pr}.PayloadLength = {length}");
        var bytes = Convert.FromHexString(hex);
        int i = 0;
        for (; i + 4 <= bytes.Length; i += 4)
            sb.Append($" and {pr}.Payload32[{i / 4}] = 0x{Convert.ToHexString(bytes, i, 4).ToLowerInvariant()}");
        for (; i < bytes.Length; i++)
            sb.Append($" and {pr}.Payload[{i}] = 0x{bytes[i]:x2}");
        return sb.ToString();
    }

    /// <summary>Сумма фильтров профилей, которые winws получит через <c>--wf-raw-part</c> (с переводом строки в конце каждого).</summary>
    public static int FiltersSize(IEnumerable<AppProfile> profiles) =>
        profiles.Sum(p => (Filter(p, true)?.Length + 1 ?? 0) + (Filter(p, false)?.Length + 1 ?? 0));

    /// <summary>Не вышел ли набор профилей за предел фильтра WinDivert; null — в пределах.</summary>
    public static string? TotalProblem(IEnumerable<AppProfile> profiles) =>
        FiltersSize(profiles) > MaxFilterBytes ? L.T("core.profile.filter_limit", new { kb = MaxFilterBytes / 1024 }) : null;

    /// <summary>Файл фильтра: одна строка и перевод строки.</summary>
    public static string FilterFileText(string filter) => filter + "\n";
}

/// <summary>
/// Что окно добавляет к профилю сверх портов и адресов (API 1.22): сигнатура пакета и, у профиля из импорта, свои параметры десинхронизации
/// (null — взять из выбранного варианта). Проверяется в <see cref="AppProfiles.Problem"/>: помощник окну не доверяет.
/// </summary>
public sealed record ProfileSignature(int TcpLength = 0, string TcpHex = "", int UdpLength = 0, string UdpHex = "", string? TcpDesync = null, string? UdpDesync = null,
    string TcpFilter = "", string UdpFilter = "")
{
    public static readonly ProfileSignature None = new();
    public bool IsEmpty => this == None;
}
