using DpiShell.Core.Localization;
using System.Net;

namespace DpiShell.Core;

/// <summary>
/// Вердикт для строки «адрес имя» (ТЗ 12). Файл hosts при примерке не меняется.
/// </summary>
public enum HostsTryVerdict
{
    /// <summary>Через DNS сайт не открывается, а по адресу из строки — да, сертификат верный: строка чинит.</summary>
    Helps,
    /// <summary>Через DNS сайт и так открывается.</summary>
    NotNeeded,
    /// <summary>Через DNS сайт открывается, а строка ведёт на заглушку или нерабочий адрес: после неё сайт перестанет открываться.</summary>
    Breaks,
    /// <summary>Не открывается ни через DNS, ни по адресу: нужна оптимизация соединения, а не hosts.</summary>
    Useless,
    /// <summary>Сертификат по адресу выдан не этому сайту.</summary>
    Dangerous,
    /// <summary>Заглушка (0.0.0.0, 127.*) для сайта, который и так не открывался: блокировка, ничего не меняет.</summary>
    Blocks,
}

/// <summary>Как адрес из строки соотносится с ответом DNS.</summary>
public enum DnsMatch { Same, SameNet, Differs, Unknown }

public sealed record HostsTryItem(string Host, string Ip, int Line = 0, string? LineId = null);

/// <param name="Line">Номер строки в hosts (для аудита), 0 — строка из вставки.</param>
/// <param name="LineId">«номер:хеш» строки в hosts (для аудита) — по нему строку называют помощнику.</param>
public sealed record HostsTryRow(
    string Host, string Ip, HostsTryVerdict Verdict, EndpointCheck? Live, string? OwnerName,
    DnsMatch Match, DnsState Dns, string DnsDetail, int Line = 0, string? LineId = null)
{
    public HostsEntry Entry => new(Host, Ip);
    /// <summary>Только такие строки можно записать в свои.</summary>
    public bool Writable => Verdict == HostsTryVerdict.Helps;
    /// <summary>Строки, которые мешают: их предлагают закомментировать (решает человек).</summary>
    public bool Problem => Verdict is HostsTryVerdict.Breaks or HostsTryVerdict.Dangerous;
}

public sealed record HostsTryReport(IReadOnlyList<HostsTryRow> Rows, int Skipped, bool Truncated, int Total, DateTime At)
{
    public IReadOnlyList<HostsEntry> Writable => Rows.Where(r => r.Writable).Select(r => r.Entry).ToList();
    public IReadOnlyList<string> ProblemIds => Rows.Where(r => r.Problem && r.LineId is not null).Select(r => r.LineId!).Distinct().ToList();
}

public sealed record HostsPasteResult(IReadOnlyList<HostsEntry> Entries, int SkippedLines, bool Truncated);

/// <summary>Строки в формате hosts из гайда → пары «имя — адрес». Адрес может быть и заглушкой (0.0.0.0): её тоже примеряют.</summary>
public static class HostsPaste
{
    /// <summary>
    /// Столько строк берём из вставки и столько своих строк можно записать. Списки для hosts бывают и длиннее 500 (владелец 29.10) —
    /// окно примеряет их само партиями по <see cref="MaxBatch"/>.
    /// </summary>
    public const int MaxEntries = 5000;
    /// <summary>Столько строк примеряет одна команда помощника (hosts_try, hosts_comment, проверка своего hosts).</summary>
    public const int MaxBatch = 500;

    public static HostsPasteResult Parse(string text)
    {
        var seen = new HashSet<HostsEntry>();
        var list = new List<HostsEntry>();
        int skipped = 0;
        bool truncated = false;
        foreach (var raw in text.Split(['\r', '\n']))
        {
            var line = raw;
            var hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            if (parts.Length < 2 || parts[0].Contains('%') || !IPAddress.TryParse(parts[0], out var ip)) { skipped++; continue; }
            bool any = false;
            foreach (var token in parts.Skip(1))
            {
                if (!TryName(token, out var host)) continue;
                any = true;
                var e = new HostsEntry(host, ip.ToString());
                if (!seen.Add(e)) continue;
                if (list.Count >= MaxEntries) { truncated = true; continue; }
                list.Add(e);
            }
            if (!any) skipped++;
        }
        return new HostsPasteResult(list, skipped, truncated);
    }

    /// <summary>Имя сайта для строки hosts: с точкой, без маски, порта и пути.</summary>
    public static bool TryName(string token, out string host)
    {
        host = "";
        if (token.Contains(':') || token.Contains('/') || token.Contains('*')) return false;
        var h = HostsInput.CleanHost(token);
        if (h is null) return false;
        host = h;
        return true;
    }

    /// <summary>
    /// «адрес имя» как её присылает окно помощнику. Строго: ровно два слова, адрес разбирается как IP,
    /// имя — как имя сайта. <paramref name="publicOnly"/> — адрес обязан быть публичным (<see cref="HostsRules.TryParsePublic"/>).
    /// </summary>
    public static bool TryEntry(string s, bool publicOnly, out HostsEntry entry)
    {
        entry = new HostsEntry("", "");
        var parts = s.Split(' ');
        if (parts.Length != 2) return false;
        if (publicOnly)
        {
            if (!HostsRules.TryParsePublic(parts[0], out var pub)) return false;
            if (!TryName(parts[1], out var h1)) return false;
            entry = new HostsEntry(h1, pub.ToString());
            return true;
        }
        if (parts[0].Contains('%') || !IPAddress.TryParse(parts[0], out var ip) || !TryName(parts[1], out var h)) return false;
        entry = new HostsEntry(h, ip.ToString());
        return true;
    }
}

/// <summary>
/// Примерка строк (ТЗ 12, вердикты): для каждой пары — живая проверка адреса (<see cref="IEndpointProbe"/>: TLS с SNI имени и обычная проверка
/// сертификата), владелец сети (RDAP, вторичный признак), что говорит DNS мимо hosts (<see cref="IHostsDns"/>) и открывается ли по его адресам.
/// </summary>
public sealed class HostsTryout(IEndpointProbe probe, IHostsDns dns, IRdapClient? rdap, int parallel = 12)
{
    readonly SemaphoreSlim _gate = new(Math.Max(1, parallel));

    async Task<T> Gated<T>(Func<Task<T>> f, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await f(); }
        finally { _gate.Release(); }
    }

    sealed record DnsAnswer(DnsState State, string Detail, IReadOnlyList<IPAddress> Addresses);

    public async Task<HostsTryReport> TryAsync(IReadOnlyList<HostsTryItem> items, int skipped = 0, bool truncated = false, int? total = null,
        IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        var dnsCache = new Dictionary<string, Task<DnsAnswer>>(StringComparer.OrdinalIgnoreCase);
        var rdapCache = new Dictionary<string, Task<RdapOwner?>>();
        var jobs = new List<Task<HostsTryRow>>();
        foreach (var item in items)
        {
            var host = HostsRules.Normalize(item.Host);
            if (!dnsCache.TryGetValue(host, out var dnsTask)) dnsCache[host] = dnsTask = DnsAsync(host, ct);
            jobs.Add(RowAsync(item with { Host = host }, dnsTask, rdapCache, ct));
        }
        var rows = new List<HostsTryRow>();
        int done = 0;
        foreach (var j in jobs)
        {
            rows.Add(await j);
            progress?.Report((++done, jobs.Count));
        }
        return new HostsTryReport(rows, skipped, truncated, total ?? rows.Count, DateTime.Now);
    }

    async Task<HostsTryRow> RowAsync(HostsTryItem item, Task<DnsAnswer> dnsTask, Dictionary<string, Task<RdapOwner?>> rdapCache, CancellationToken ct)
    {
        var d = await dnsTask;
        if (!IPAddress.TryParse(item.Ip, out var ip))
            return new(item.Host, item.Ip, HostsTryVerdict.Useless, null, null, DnsMatch.Unknown, d.State, d.Detail, item.Line, item.LineId);
        var match = Match(ip, d.Addresses);

        // Заглушка или частная сеть: наружу такой адрес не ведёт, проверять нечего.
        if (!HostsRules.IsPublicAddress(ip))
        {
            var v = d.State == DnsState.Works ? HostsTryVerdict.Breaks : HostsTryVerdict.Blocks;
            return new(item.Host, item.Ip, v, null, null, match, d.State, d.Detail, item.Line, item.LineId);
        }

        var live = await Gated(() => probe.CheckAsync(item.Host, ip, ct), ct);
        string? owner = null;
        if (rdap is not null && (live.Ok || live.Outcome == EndpointOutcome.BadCertificate))
        {
            Task<RdapOwner?> t;
            lock (rdapCache)
                if (!rdapCache.TryGetValue(ip.ToString(), out t!)) rdapCache[ip.ToString()] = t = rdap.LookupAsync(ip, ct);
            owner = (await t)?.ToString();
        }
        var verdict =
            live.Outcome == EndpointOutcome.BadCertificate ? HostsTryVerdict.Dangerous
            : live.Ok ? (d.State == DnsState.Works ? HostsTryVerdict.NotNeeded : HostsTryVerdict.Helps)
            : d.State == DnsState.Works ? HostsTryVerdict.Breaks
            : HostsTryVerdict.Useless;
        return new(item.Host, item.Ip, verdict, live, owner, match, d.State, d.Detail, item.Line, item.LineId);
    }

    static DnsMatch Match(IPAddress ip, IReadOnlyList<IPAddress> answers)
    {
        if (answers.Count == 0) return DnsMatch.Unknown;
        if (answers.Any(a => a.Equals(ip))) return DnsMatch.Same;
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
            answers.Any(a => a.AddressFamily == ip.AddressFamily && a.GetAddressBytes().Take(3).SequenceEqual(ip.GetAddressBytes().Take(3))))
            return DnsMatch.SameNet;
        return DnsMatch.Differs;
    }

    /// <summary>
    /// Что даёт DNS мимо hosts: те же проверки к первым трём адресам ответа. Хоть один работает — DNS «работает».
    /// DoH молчит — «не проверен» (строку тогда не называем лишней и не называем вредной).
    /// </summary>
    async Task<DnsAnswer> DnsAsync(string host, CancellationToken ct)
    {
        IPAddress[] addrs;
        try { addrs = await Gated(() => dns.ResolveAsync(host, ct), ct); }
        catch (Exception e) when (e is not OperationCanceledException) { addrs = []; }
        if (addrs.Length == 0) return new(DnsState.Unknown, L.T("core.hosts.check.dns_silent"), []);
        var answered = addrs;
        addrs = [.. addrs.Where(HostsRules.IsPublicAddress)];
        if (addrs.Length == 0) return new(DnsState.Fails, L.T("core.hosts.check.dns_stub", new { ip = answered[0] }), []);
        var checks = await Task.WhenAll(addrs.Take(3).Select(a => Gated(() => probe.CheckAsync(host, a, ct), ct)));
        var ok = addrs.Zip(checks).FirstOrDefault(x => x.Second.Ok);
        return ok.First is not null
            ? new(DnsState.Works, $"{ok.First}: {ok.Second.Detail}", addrs)
            : new(DnsState.Fails, $"{addrs[0]}: {checks[0].Detail}", addrs);
    }
}
