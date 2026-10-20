using DpiShell.Core.Localization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace DpiShell.Core;

/// <summary>Подсеть IPv4 или IPv6. Адрес хранится числом (IPv4 — в младших 32 битах), host-биты обнулены.</summary>
public readonly record struct Cidr(bool V6, UInt128 Addr, int Len)
{
    public int Bits => V6 ? 128 : 32;

    UInt128 Mask(int len) => len == 0 ? UInt128.Zero : (UInt128.MaxValue << (Bits - len)) & (V6 ? UInt128.MaxValue : uint.MaxValue);

    /// <summary>Пересекаются ли подсети: у CIDR это значит, что одна содержит другую.</summary>
    public bool Overlaps(Cidr o)
    {
        if (V6 != o.V6) return false;
        var m = Mask(Math.Min(Len, o.Len));
        return (Addr & m) == (o.Addr & m);
    }

    /// <summary>Сколько адресов в подсети.</summary>
    public UInt128 Size => UInt128.One << (Bits - Len);

    public override string ToString()
    {
        var bytes = new byte[V6 ? 16 : 4];
        var a = Addr;
        for (int i = bytes.Length - 1; i >= 0; i--) { bytes[i] = (byte)(a & 0xFF); a >>= 8; }
        return $"{new IPAddress(bytes)}/{Len}";
    }

    // IPv4 строго четыре десятичных числа без ведущих нулей: «010.1.1.1» иначе прочитается как восьмеричное.
    static readonly Regex V4Rx = new(@"^(0|[1-9][0-9]{0,2})(\.(0|[1-9][0-9]{0,2})){3}$");
    static readonly Regex LenRx = new(@"^[0-9]{1,3}$");

    /// <summary>Разбор «адрес/длина». <paramref name="fixedHost"/> — в адресе были host-биты, они обнулены.</summary>
    public static bool TryParse(string s, bool requireLen, out Cidr c, out bool fixedHost, out string? error)
    {
        c = default; fixedHost = false; error = null;
        var parts = s.Split('/');
        if (parts.Length > 2) { error = L.T("core.ipset.not_subnet"); return false; }
        var ip = parts[0];
        bool v6 = ip.Contains(':');
        if (v6 ? ip.Contains('%') : !V4Rx.IsMatch(ip)) { error = L.T("core.ipset.not_address"); return false; }
        if (!IPAddress.TryParse(ip, out var addr) || addr.AddressFamily != (v6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork))
        { error = L.T("core.ipset.not_address"); return false; }
        int bits = v6 ? 128 : 32, len = bits;
        if (parts.Length == 2)
        {
            if (!LenRx.IsMatch(parts[1]) || (len = int.Parse(parts[1])) > bits) { error = L.T("core.ipset.bad_prefix"); return false; }
        }
        else if (requireLen) { error = L.T("core.ipset.no_prefix"); return false; }

        UInt128 n = 0;
        foreach (var b in addr.GetAddressBytes()) n = (n << 8) | b;
        var raw = new Cidr(v6, n, len);
        c = raw with { Addr = n & raw.Mask(len) };
        fixedHost = c.Addr != n;
        return true;
    }

    public static Cidr Parse(string s) =>
        TryParse(s, false, out var c, out _, out var e) ? c : throw new FormatException($"{s}: {e}");
}

public enum IpsetKind
{
    /// <summary>Общий список (ipset-all.txt): все правила §2, включая размер и долю IPv4.</summary>
    Main,
    /// <summary>Свои подсети пользователя: правила строк, лимит 10 000, широкие подсети — с подтверждением.</summary>
    User,
}

/// <param name="Entries">Принятые подсети в каноническом виде, без дублей, в исходном порядке.</param>
/// <param name="Fixed">Строк с обнулёнными host-битами («исправлено»).</param>
/// <param name="Dropped">Отброшенных строк (ошибка формата, служебная сеть, слишком широкая подсеть).</param>
/// <param name="Problems">Примеры отброшенных строк с причиной (до 20).</param>
/// <param name="Errors">Причины отказа списку целиком. Пусто — список можно применять.</param>
/// <param name="Confirm">Что требует подтверждения пользователя (сильное изменение, очень широкие свои подсети).</param>
/// <param name="Ipv4Share">Доля всех адресов IPv4, которую покрывает список (пересечения считаются один раз).</param>
/// <param name="Added">Новых подсетей относительно прошлой версии.</param>
/// <param name="Removed">Убранных подсетей относительно прошлой версии.</param>
public sealed record IpsetVerdict(
    IReadOnlyList<string> Entries, int Fixed, int Dropped, int Duplicates,
    IReadOnlyList<string> Problems, IReadOnlyList<string> Errors, IReadOnlyList<string> Confirm,
    int Ipv4, int Ipv6, double Ipv4Share, int Added, int Removed)
{
    public bool Accepted => Errors.Count == 0;
    public bool NeedsConfirm => Accepted && Confirm.Count > 0;
}

/// <summary>
/// Проверка IP-списков (architecture.md «IP-списки и игровой режим»): один код для подготовки версии (тест на data/) и для загрузки на машине.
/// </summary>
public static class IpsetValidator
{
    public const int MinLines = 1_000, MaxLines = 100_000, MaxUserLines = 10_000;
    public const long MinBytes = 50 * 1024, MaxBytes = 4 * 1024 * 1024;
    public const double MaxIpv4Share = 0.15, MaxChange = 0.40, MaxDroppedShare = 0.01;
    public const int MinLenV4 = 8, MinLenV6 = 19, UserConfirmLenV4 = 16;

    /// <summary>Служебные сети (Name — код: подпись по ключу core.ipset.net.*, <see cref="NetName"/>): приманки туда — это локальная сеть, мультикаст, loopback. Совпадает с ipset-exclude.txt + резерв.</summary>
    public static readonly IReadOnlyList<(Cidr Net, string Name)> Forbidden =
    [
        .. new[]
        {
            ("0.0.0.0/8", "this"), ("10.0.0.0/8", "private"), ("100.64.0.0/10", "cgnat"),
            ("127.0.0.0/8", "loopback"), ("169.254.0.0/16", "linklocal"), ("172.16.0.0/12", "private"),
            ("192.168.0.0/16", "private"), ("224.0.0.0/4", "multicast"), ("240.0.0.0/4", "reserved"),
            ("::/128", "empty"), ("::1/128", "loopback"), ("::ffff:0:0/96", "v4in6"),
            ("fc00::/7", "private"), ("fe80::/10", "linklocal"), ("ff00::/8", "multicast"),
        }.Select(x => (Cidr.Parse(x.Item1), x.Item2)),
    ];

    /// <summary>Подпись служебной сети по коду из <see cref="Forbidden"/>.</summary>
    public static string NetName(string code) => code switch
    {
        "this" => L.T("core.ipset.net.this"), "private" => L.T("core.ipset.net.private"), "cgnat" => L.T("core.ipset.net.cgnat"),
        "loopback" => L.T("core.ipset.net.loopback"), "linklocal" => L.T("core.ipset.net.linklocal"), "multicast" => L.T("core.ipset.net.multicast"),
        "reserved" => L.T("core.ipset.net.reserved"), "empty" => L.T("core.ipset.net.empty"), _ => L.T("core.ipset.net.v4in6"),
    };

    public static IpsetVerdict ValidateFile(string path, IpsetKind kind, IReadOnlyCollection<string>? previous = null)
    {
        var size = new FileInfo(path).Length;
        // Огромный файл даже не читаем: это точно не наш список.
        if (size > MaxBytes)
            return Reject(L.T("core.ipset.too_big_mb", new { size = size / 1024 / 1024, max = MaxBytes / 1024 / 1024 }));
        return Validate(File.ReadAllText(path), kind, previous, size);
    }

    static IpsetVerdict Reject(string error) => new([], 0, 0, 0, [], [error], [], 0, 0, 0, 0, 0);

    /// <param name="previous">Прошлая версия (канонические строки): для лимита изменений. Null или меньше 1 000 строк — не сравниваем.</param>
    /// <param name="bytes">Размер файла; null — по длине текста.</param>
    public static IpsetVerdict Validate(string text, IpsetKind kind, IReadOnlyCollection<string>? previous = null, long? bytes = null)
    {
        var entries = new List<string>(); var seen = new HashSet<string>();
        var nets = new List<Cidr>(); var problems = new List<string>(); var confirm = new List<string>();
        int fixedN = 0, dropped = 0, dups = 0, wideUser = 0, lineNo = 0;

        void Drop(string raw, string why)
        {
            dropped++;
            if (problems.Count < 20) problems.Add(L.T("core.ipset.line_problem", new { n = lineNo, raw = raw.Length > 60 ? raw[..60] + "…" : raw, why }));
        }

        foreach (var line in text.Split('\n'))
        {
            lineNo++;
            var raw = line.Trim().TrimStart('﻿');
            if (raw.Length == 0 || raw.StartsWith('#')) continue;
            // Общий список — строго «адрес/длина», как у Flowseal; свои подсети можно писать и одиночным адресом.
            if (!Cidr.TryParse(raw, requireLen: kind == IpsetKind.Main, out var c, out var fixedHost, out var err)) { Drop(raw, err!); continue; }
            if (c.Len < (c.V6 ? MinLenV6 : MinLenV4)) { Drop(raw, L.T("core.ipset.too_wide", new { len = c.Len })); continue; }
            var bad = Forbidden.FirstOrDefault(f => f.Net.Overlaps(c));
            if (bad.Name is not null) { Drop(raw, L.T("core.ipset.service_net", new { name = NetName(bad.Name), net = bad.Net })); continue; }
            if (fixedHost) fixedN++;
            var canon = c.ToString();
            if (!seen.Add(canon)) { dups++; continue; }
            if (kind == IpsetKind.User && !c.V6 && c.Len < UserConfirmLenV4) wideUser++;
            entries.Add(canon); nets.Add(c);
        }

        int v4 = nets.Count(n => !n.V6), v6 = nets.Count - v4;
        double share = (double)Ipv4Coverage(nets) / 4294967296.0;
        var errors = new List<string>();
        long size = bytes ?? System.Text.Encoding.UTF8.GetByteCount(text);

        if (kind == IpsetKind.Main)
        {
            if (entries.Count < MinLines) errors.Add(L.T("core.ipset.too_few", new { n = entries.Count, min = MinLines }));
            if (entries.Count > MaxLines) errors.Add(L.T("core.ipset.too_many", new { n = entries.Count, max = MaxLines }));
            if (size < MinBytes) errors.Add(L.T("core.ipset.too_small_kb", new { size = size / 1024, min = MinBytes / 1024 }));
            if (size > MaxBytes) errors.Add(L.T("core.ipset.too_big_mb", new { size = size / 1024 / 1024, max = MaxBytes / 1024 / 1024 }));
            if (share > MaxIpv4Share) errors.Add(L.T("core.ipset.too_much_ipv4", new { share = share.ToString("P1"), max = MaxIpv4Share.ToString("P0") }));
            if (dropped > (entries.Count + dropped) * MaxDroppedShare)
                errors.Add(L.T("core.ipset.many_bad", new { dropped, total = entries.Count + dropped }));
        }
        else
        {
            if (entries.Count > MaxUserLines) errors.Add(L.T("core.ipset.too_many_own", new { n = entries.Count, max = MaxUserLines }));
            if (wideUser > 0) confirm.Add(L.T("core.ipset.wide_confirm", new { n = wideUser }));
        }

        int added = 0, removed = 0;
        if (previous is { Count: >= MinLines } && kind == IpsetKind.Main)
        {
            var prev = previous as IReadOnlySet<string> ?? previous.ToHashSet();
            added = entries.Count(e => !prev.Contains(e));
            removed = prev.Count - (entries.Count - added);
            if ((double)(added + removed) / prev.Count > MaxChange)
                confirm.Add(L.T("core.ipset.changed_confirm", new { added, removed }));
        }
        return new(entries, fixedN, dropped, dups, problems, errors, confirm, v4, v6, share, added, removed);
    }

    /// <summary>Сколько адресов IPv4 покрывают подсети (объединение диапазонов).</summary>
    static ulong Ipv4Coverage(List<Cidr> nets)
    {
        ulong total = 0, curStart = 0, curEnd = 0; bool open = false;
        foreach (var (start, end) in nets.Where(n => !n.V6)
                     .Select(n => ((ulong)n.Addr, (ulong)n.Addr + (ulong)n.Size)).OrderBy(r => r.Item1))
        {
            if (open && start <= curEnd) { if (end > curEnd) curEnd = end; continue; }
            if (open) total += curEnd - curStart;
            (curStart, curEnd, open) = (start, end, true);
        }
        if (open) total += curEnd - curStart;
        return total;
    }
}
