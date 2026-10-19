using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace DpiShell.Core;

/// <summary>
/// Настройки системного прокси WinINET одной ветки реестра: ProxyEnable, ProxyServer, ProxyOverride, AutoConfigURL.
/// Пустое значение — null (в реестре значения нет). PAC работает независимо от <see cref="Enabled"/> (так читает WinINET).
/// </summary>
public sealed record ProxySettings(bool Enabled, string? Server, string? Bypass, string? Pac)
{
    public static readonly ProxySettings Off = new(false, null, null, null);

    /// <summary>Что-то включено: постоянный прокси или адрес автонастройки.</summary>
    public bool IsOn => Enabled && Server is not null || Pac is not null;

    /// <summary>«Очистить»: прокси и PAC выключены, введённый адрес и исключения остаются в реестре (как делает сама Windows).</summary>
    public ProxySettings Cleared() => this with { Enabled = false, Pac = null };
}

/// <summary>Разобранный адрес прокси из ProxyServer: <c>host:port</c> или <c>схема=host:port;…</c>.</summary>
/// <param name="ByScheme">Схема (http, https, ftp, socks) → host:port. Для записи без схем — один ключ "*".</param>
public sealed record ProxyServerSpec(IReadOnlyDictionary<string, string> ByScheme)
{
    public bool Plain => ByScheme.ContainsKey("*");

    /// <summary>Нормальная запись для реестра.</summary>
    public string Text => Plain ? ByScheme["*"] : string.Join(';', ByScheme.Select(p => $"{p.Key}={p.Value}"));

    /// <summary>Какой адрес использовать для проверки: https, затем http, затем общий, затем socks. Вид — "http" или "socks5".</summary>
    public (string Kind, string HostPort)? ForCheck()
    {
        foreach (var k in new[] { "https", "http", "*" })
            if (ByScheme.TryGetValue(k, out var v)) return ("http", v);
        return ByScheme.TryGetValue("socks", out var s) ? ("socks5", s) : null;
    }
}

/// <summary>
/// Белый список значений прокси (никаких произвольных путей реестра и команд): адрес — хост:порт или схема=хост:порт;…,
/// исключения — имена и маски, PAC — только http(s). Все функции возвращают нормальную запись или null.
/// </summary>
public static partial class ProxyValues
{
    public const int MaxServerLength = 300;
    public const int MaxBypassEntries = 100;
    public const int MaxPacLength = 500;
    public static readonly string[] Schemes = ["http", "https", "ftp", "socks"];

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9\-]{0,61}[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9\-]{0,61}[A-Za-z0-9])?)*$")]
    private static partial Regex HostRx();

    [GeneratedRegex(@"^(<local>|<-loopback>|[A-Za-z0-9*._\-:\[\]/]{1,100})$")]
    private static partial Regex BypassRx();

    /// <summary>host:port → (host, port). Хост: имя, IPv4 или [IPv6]; порт 1–65535.</summary>
    public static bool TryHostPort(string? text, out string host, out int port)
    {
        host = ""; port = 0;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 120) return false;
        text = text.Trim();
        int colon = text.LastIndexOf(':');
        if (colon <= 0 || colon == text.Length - 1) return false;
        var h = text[..colon];
        var p = text[(colon + 1)..];
        if (p.Length > 5 || !p.All(char.IsAsciiDigit) || !int.TryParse(p, out port) || port is < 1 or > 65535) return false;
        if (h.StartsWith('['))
        {
            if (!h.EndsWith(']') || !IPAddress.TryParse(h[1..^1], out var v6) || v6.AddressFamily != AddressFamily.InterNetworkV6) return false;
            host = "[" + v6 + "]";
            return true;
        }
        if (h.All(c => char.IsAsciiDigit(c) || c == '.'))
        {
            var parts = h.Split('.');
            if (parts.Length != 4 || parts.Any(x => x.Length is 0 or > 3 || !int.TryParse(x, out var n) || n > 255)) return false;
            host = string.Join('.', parts.Select(int.Parse));
            return true;
        }
        if (h.Length > 253 || !HostRx().IsMatch(h)) return false;
        host = h.ToLowerInvariant();
        return true;
    }

    /// <summary>Разобрать адрес прокси; null — запись не подходит.</summary>
    public static ProxyServerSpec? ParseServer(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxServerLength) return null;
        var map = new Dictionary<string, string>();
        var parts = text.Trim().Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Length > Schemes.Length) return null;
        foreach (var part in parts)
        {
            string scheme = "*", hp = part;
            int eq = part.IndexOf('=');
            if (eq >= 0)
            {
                scheme = part[..eq].Trim().ToLowerInvariant();
                hp = part[(eq + 1)..];
                if (!Schemes.Contains(scheme)) return null;
            }
            if (!TryHostPort(hp, out var host, out var port)) return null;
            if (!map.TryAdd(scheme, $"{host}:{port}")) return null;
        }
        if (map.ContainsKey("*") && map.Count > 1) return null;   // «host:port» и «схема=…» вместе не бывают
        return new ProxyServerSpec(map);
    }

    public static string? NormalizeServer(string? text) => ParseServer(text)?.Text;

    /// <summary>
    /// Исключения: разделители — ; , пробел, перевод строки. Допустимы <c>&lt;local&gt;</c>, <c>&lt;-loopback&gt;</c> и имена/маски/адреса
    /// из букв, цифр и <c>* . _ - : [ ] /</c>. Пусто — (null, true); ошибка — (null, false).
    /// </summary>
    public static (string? Value, bool Ok) NormalizeBypass(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, true);
        var list = new List<string>();
        foreach (var raw in text.Split([';', ',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var t = raw.Trim();
            if (!BypassRx().IsMatch(t)) return (null, false);
            if (!list.Contains(t, StringComparer.OrdinalIgnoreCase)) list.Add(t);
        }
        if (list.Count > MaxBypassEntries) return (null, false);
        return (list.Count == 0 ? null : string.Join(';', list), true);
    }

    /// <summary>Адрес скрипта автонастройки: только http(s), без логина и пароля, без пробелов и кавычек.</summary>
    public static (string? Value, bool Ok) NormalizePac(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, true);
        var t = text.Trim();
        if (t.Length > MaxPacLength || t.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '"' or '\\' or '<' or '>' or '|' or '^' or '`' or '\''))
            return (null, false);
        if (!Uri.TryCreate(t, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") || u.Host.Length == 0 || u.UserInfo.Length > 0)
            return (null, false);
        return (t, true);
    }

    /// <summary>Проверка полного набора «адрес + исключения + PAC» для записи. Ошибка — текст для человека.</summary>
    public static string? Problem(string? server, string? bypass, string? pac, bool needServerOrPac = true)
    {
        if (!string.IsNullOrWhiteSpace(server) && ParseServer(server) is null)
            return "Адрес прокси — в виде хост:порт, например 127.0.0.1:7897 (или http=хост:порт;https=хост:порт).";
        if (!NormalizeBypass(bypass).Ok) return "Исключения — имена и маски через «;», например <local>;*.example.com.";
        if (!NormalizePac(pac).Ok) return "Адрес автонастройки — ссылка http:// или https://.";
        if (needServerOrPac && string.IsNullOrWhiteSpace(server) && string.IsNullOrWhiteSpace(pac)) return "Укажите адрес прокси или адрес автонастройки.";
        return null;
    }

    /// <summary>Привести значения к нормальной записи (после <see cref="Problem"/> без ошибок).</summary>
    public static ProxySettings Build(string? server, string? bypass, string? pac, bool enable = true)
    {
        var s = NormalizeServer(server);
        return new ProxySettings(enable && s is not null, s, NormalizeBypass(bypass).Value, NormalizePac(pac).Value);
    }

    /// <summary>Короткое описание для человека: «127.0.0.1:7897», «автонастройка …», «выключен».</summary>
    public static string Describe(ProxySettings s)
    {
        var parts = new List<string>();
        if (s.Enabled && s.Server is not null) parts.Add(s.Server);
        if (s.Pac is not null) parts.Add("автонастройка " + s.Pac);
        return parts.Count == 0 ? "выключен" : string.Join(" · ", parts);
    }
}

/// <summary>Прокси WinHTTP по умолчанию (для служб): null в <see cref="Proxy"/> — прямое соединение.</summary>
public sealed record WinHttpInfo(string? Proxy, string? Bypass)
{
    public static readonly WinHttpInfo Direct = new(null, null);
    public bool IsDirect => Proxy is null;

    /// <summary>WinHTTP умеет только постоянный прокси и исключения: из набора берётся ровно это.</summary>
    public static WinHttpInfo From(ProxySettings s) => s.Enabled && s.Server is not null ? new(s.Server, s.Bypass) : Direct;
}

/// <summary>
/// Подсказки о подводных камнях (вкладка «Прокси»): <see cref="Short"/> — на виду, простыми словами;
/// <see cref="Details"/> — под «Подробнее», с техническими названиями. Формулировки осторожные: «обычно», «может».
/// </summary>
public static class ProxyHints
{
    public static readonly IReadOnlyList<string> Short =
    [
        "Браузеры обычно берут системный прокси; Firefox — по своей настройке.",
        "Программы со своим прокси (мессенджеры, игры, торренты) системный могут не учитывать.",
        "После смены прокси часть программ нужно перезапустить.",
        "Автонастройка (PAC), если она включена, действует раньше обычного прокси.",
    ];

    public static readonly IReadOnlyList<string> Details =
    [
        "Приложения из Microsoft Store могут не ходить на 127.0.0.1: для них нужно разрешить «loopback» (CheckNetIsolation LoopbackExempt).",
        "Службы Windows и часть программ берут прокси WinHTTP, а не системный: его можно задать отдельной галочкой.",
        "Программы на .NET читают прокси при запуске: перезапустите их после смены.",
        "Строка socks=… в системном прокси браузеры могут понять как SOCKS4; для SOCKS5 надёжнее http-порт того же прокси.",
        "Адреса из исключений (например <local>) идут напрямую; 127.0.0.1 WinINET не отправляет через прокси сам, если не указано <-loopback>.",
    ];

    public static IReadOnlyList<string> All => [.. Short, .. Details];
}
