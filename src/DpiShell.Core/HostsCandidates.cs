using DpiShell.Core.Localization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DpiShell.Core;

/// <summary>Кандидат для «Починить адреса» (architecture.md «Починить адреса»): домен и адреса, которые могут его чинить, и чьи это адреса по RDAP.</summary>
public sealed record HostsCandidate(
    string Host,
    List<string> Ips,
    [property: JsonPropertyName("owner_any_of")] List<string> OwnerAnyOf,
    string Reason,
    string Since,
    string Source);

public sealed record HostsCandidateFile(int Version, string Source, [property: JsonPropertyName("source_sha256")] string SourceSha256, List<HostsCandidate> Candidates);

/// <summary>Одна строка hosts: «адрес домен».</summary>
public sealed record HostsEntry(string Host, string Ip)
{
    public override string ToString() => $"{Ip} {Host}";
}

/// <summary>Какие домены и адреса вообще допустимы в нашем блоке hosts.</summary>
public static class HostsRules
{
    /// <summary>Белый список architecture.md «Починить адреса». «*.x» — только поддомены x. Домен вне списка не попадает в hosts ни при каких данных.</summary>
    public static readonly IReadOnlyList<string> Whitelist =
    [
        "github.com", "*.githubusercontent.com",
        "telegram.org", "*.telegram.org", "t.me", "telegram.me", "telegram.dog", "telegram.space",
        "tg.dev", "*.tg.dev", "telesco.pe", "*.telesco.pe",
        "discord.com", "updates.discord.com",
    ];

    public static string Normalize(string host) => host.Trim().TrimEnd('.').ToLowerInvariant();

    public static bool IsAllowedHost(string host)
    {
        var h = Normalize(host);
        if (h.Length == 0 || h.Length > 253 || h.StartsWith('.') || h.Contains("..") ||
            h.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))) return false;
        foreach (var w in Whitelist)
        {
            if (w.StartsWith("*."))
            {
                var suffix = w[1..];   // ".githubusercontent.com"
                if (h.EndsWith(suffix, StringComparison.Ordinal) && h.Length > suffix.Length) return true;
            }
            else if (h == w) return true;
        }
        return false;
    }

    /// <summary>Публичный адрес: не частный, не loopback, не 0.0.0.0/8, не link-local, не CGNAT, не multicast/резерв.</summary>
    public static bool IsPublicAddress(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return !(b[0] is 0 or 10 or 127 || b[0] >= 224
                || b[0] == 172 && b[1] is >= 16 and <= 31
                || b[0] == 192 && b[1] == 168
                || b[0] == 169 && b[1] == 254
                || b[0] == 100 && b[1] is >= 64 and <= 127);
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv4MappedToIPv6 || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.IsIPv6UniqueLocal) return false;
            if (ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6Loopback)) return false;
            var b = ip.GetAddressBytes();
            return (b[0] & 0xE0) == 0x20;   // только глобальные юникаст 2000::/3
        }
        return false;
    }

    public static bool TryParsePublic(string s, out IPAddress ip)
    {
        ip = IPAddress.None;
        if (s.Contains('%') || !IPAddress.TryParse(s.Trim(), out var parsed) || !IsPublicAddress(parsed)) return false;
        ip = parsed;
        return true;
    }
}

public static class HostsCandidates
{
    public const string FileName = "hosts-candidates.json";
    public const string HashFileName = "hosts-candidates.sha256";

    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, ReadCommentHandling = JsonCommentHandling.Skip };

    /// <summary>
    /// Кандидаты из поставки (data\hosts\). Хеш файла сверяется с hosts-candidates.sha256 (sha256 текста с переводами строк LF —
    /// так хеш не ломает git с autocrlf). Не совпал или есть запись вне белого списка — исключение, функция не работает.
    /// </summary>
    public static IReadOnlyList<HostsCandidate> LoadSeed(AppPaths paths)
    {
        var dir = Path.Combine(paths.Seed, "hosts");
        var file = Path.Combine(dir, FileName);
        var hashFile = Path.Combine(dir, HashFileName);
        var expected = File.ReadAllText(hashFile).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return Load(file, expected);
    }

    public static IReadOnlyList<HostsCandidate> Load(string file, string? expectedSha256)
    {
        var bytes = File.ReadAllBytes(file);
        if (expectedSha256 is not null)
        {
            var actual = Sha256Lf(bytes);
            if (!actual.Equals(expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(L.T("core.hosts.cand_tampered", new { actual = actual[..12], expected = expectedSha256.Trim()[..Math.Min(12, expectedSha256.Trim().Length)] }));
        }
        return Parse(Encoding.UTF8.GetString(bytes));
    }

    public static IReadOnlyList<HostsCandidate> Parse(string json)
    {
        var doc = JsonSerializer.Deserialize<HostsCandidateFile>(json, Json) ?? throw new InvalidDataException(L.T("core.hosts.cand_empty"));
        var list = (doc.Candidates ?? []).Select(c => c with { Host = HostsRules.Normalize(c.Host ?? "") }).ToList();
        var errors = Validate(list);
        if (errors.Count > 0) throw new InvalidDataException(L.T("core.hosts.cand_invalid", new { errors = string.Join("; ", errors) }));
        return list;
    }

    /// <summary>Каждая запись: домен из белого списка, адреса публичные, есть ожидаемый владелец. Пустой результат — всё в порядке.</summary>
    public static List<string> Validate(IEnumerable<HostsCandidate> list)
    {
        var errors = new List<string>();
        foreach (var c in list)
        {
            if (!HostsRules.IsAllowedHost(c.Host)) errors.Add(L.T("core.hosts.cand.not_allowed", new { host = c.Host }));
            if (c.Ips is not { Count: > 0 }) errors.Add(L.T("core.hosts.cand.no_addresses", new { host = c.Host }));
            foreach (var ip in c.Ips ?? [])
                if (!HostsRules.TryParsePublic(ip, out _)) errors.Add(L.T("core.hosts.cand.not_public", new { host = c.Host, ip }));
            if (c.OwnerAnyOf is not { Count: > 0 } || c.OwnerAnyOf.Any(string.IsNullOrWhiteSpace)) errors.Add(L.T("core.hosts.cand.no_owner", new { host = c.Host }));
        }
        return errors;
    }

    /// <summary>sha256 содержимого с CRLF → LF (hex, строчные).</summary>
    public static string Sha256Lf(byte[] bytes)
    {
        var lf = new List<byte>(bytes.Length);
        for (int i = 0; i < bytes.Length; i++)
            if (!(bytes[i] == '\r' && i + 1 < bytes.Length && bytes[i + 1] == '\n')) lf.Add(bytes[i]);
        return Convert.ToHexStringLower(SHA256.HashData(lf.ToArray()));
    }
}
