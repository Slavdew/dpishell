using DpiShell.Core.Localization;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace DpiShell.Core;

/// <summary>
/// «Подобрать адреса» (ТЗ 12): из текста в формате hosts берутся только имена, адреса из строк отбрасываются.
/// Для каждого имени опрашиваются DNS-серверы, каждый полученный адрес проверяется живым подключением
/// (<see cref="TlsEndpointProbe"/>: TLS с SNI домена и верным сертификатом). Найти адрес мало — провайдер тоже его даёт, только заблокированный.
/// </summary>
public static class HostsInput
{
    /// <summary>Имена по порядку первого появления, без повторов. Адреса, комментарии, localhost и мусор отбрасываются.</summary>
    public static IReadOnlyList<string> ParseHosts(string text)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw;
            var hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            foreach (var token in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (IPAddress.TryParse(token.Split('%')[0], out _)) continue;
                var host = CleanHost(token);
                if (host is not null && seen.Add(host)) list.Add(host);
            }
        }
        return list;
    }

    /// <summary>Имя из «site.one», «https://site.one/path», «site.one:443». null — не имя (одна метка, маска, недопустимые символы).</summary>
    public static string? CleanHost(string token)
    {
        var h = token.Trim();
        var scheme = h.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) h = h[(scheme + 3)..];
        var cut = h.IndexOfAny(['/', '?', ':']);
        if (cut >= 0) h = h[..cut];
        h = HostsRules.Normalize(h);
        if (h.Length is 0 or > 253 || !h.Contains('.') || h.StartsWith('.') || h.Contains("..") || h.EndsWith(".local", StringComparison.Ordinal)) return null;
        if (h.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))) return null;
        return h;
    }
}

/// <summary>Источник адресов: системный резолвер, DNS-сервер по UDP или DoH.</summary>
public interface IDnsSource
{
    string Name { get; }
    /// <summary>Пустой массив — имени нет или сервер не ответил (тогда <paramref name="error"/> не пуст через исключение).</summary>
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct = default);
}

/// <summary>DNS-сообщение в двоичном виде (RFC 1035) — общее для UDP и DoH (RFC 8484).</summary>
public static class DnsWire
{
    public const ushort TypeA = 1, TypeAaaa = 28;

    public static byte[] Query(string host, ushort type, ushort id)
    {
        var buf = new List<byte>(32 + host.Length);
        buf.AddRange([(byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0]);   // RD=1, один вопрос
        foreach (var label in host.TrimEnd('.').Split('.'))
        {
            var b = Encoding.ASCII.GetBytes(label);
            if (b.Length is 0 or > 63) throw new ArgumentException(L.T("core.hosts.dns.bad_name", new { host }));
            buf.Add((byte)b.Length);
            buf.AddRange(b);
        }
        buf.AddRange([0, (byte)(type >> 8), (byte)type, 0, 1]);
        return buf.ToArray();
    }

    /// <summary>Адреса A/AAAA из ответа. id = null — не сверять (DoH шлёт id 0). Ошибка сервера (RCODE ≠ 0 и ≠ NXDOMAIN) — исключение.</summary>
    public static IPAddress[] Parse(ReadOnlySpan<byte> msg, ushort? id = null)
    {
        if (msg.Length < 12) throw new InvalidDataException(L.T("core.hosts.dns.short"));
        if (id is { } want && BinaryPrimitives.ReadUInt16BigEndian(msg) != want) throw new InvalidDataException(L.T("core.hosts.dns.foreign"));
        if ((msg[2] & 0x80) == 0) throw new InvalidDataException(L.T("core.hosts.dns.not_reply"));
        var rcode = msg[3] & 0x0F;
        if (rcode == 3) return [];
        if (rcode != 0) throw new InvalidDataException($"DNS RCODE {rcode}");
        int qd = BinaryPrimitives.ReadUInt16BigEndian(msg[4..]), an = BinaryPrimitives.ReadUInt16BigEndian(msg[6..]);
        int pos = 12;
        for (int i = 0; i < qd; i++) pos = SkipName(msg, pos) + 4;
        var result = new List<IPAddress>();
        for (int i = 0; i < an; i++)
        {
            pos = SkipName(msg, pos);
            if (pos + 10 > msg.Length) throw new InvalidDataException(L.T("core.hosts.dns.truncated"));
            var type = BinaryPrimitives.ReadUInt16BigEndian(msg[pos..]);
            var len = BinaryPrimitives.ReadUInt16BigEndian(msg[(pos + 8)..]);
            pos += 10;
            if (pos + len > msg.Length) throw new InvalidDataException(L.T("core.hosts.dns.truncated"));
            if (type == TypeA && len == 4 || type == TypeAaaa && len == 16) result.Add(new IPAddress(msg.Slice(pos, len)));
            pos += len;
        }
        return result.ToArray();
    }

    static int SkipName(ReadOnlySpan<byte> msg, int pos)
    {
        while (true)
        {
            if (pos >= msg.Length) throw new InvalidDataException(L.T("core.hosts.dns.truncated_name"));
            var len = msg[pos];
            if ((len & 0xC0) == 0xC0) return pos + 2;   // ссылка на имя выше: имя кончается здесь
            if (len == 0) return pos + 1;
            pos += 1 + len;
        }
    }
}

/// <summary>Обычный DNS по UDP 53. Провайдер может подменять и эти ответы — живая проверка адресов это отсеет.</summary>
public sealed class UdpDnsSource(string name, IPAddress server) : IDnsSource
{
    public string Name { get; } = name;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(2);

    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct = default)
    {
        var a = Ask(host, DnsWire.TypeA, ct);
        var aaaa = Ask(host, DnsWire.TypeAaaa, ct);
        return [.. await a, .. await aaaa];
    }

    async Task<IPAddress[]> Ask(string host, ushort type, CancellationToken ct)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var id = (ushort)Random.Shared.Next(1, 65536);
            using var udp = new UdpClient(server.AddressFamily);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);
            try
            {
                await udp.SendAsync(DnsWire.Query(host, type, id), new IPEndPoint(server, 53), cts.Token);
                while (true)
                {
                    var r = await udp.ReceiveAsync(cts.Token);
                    if (r.Buffer.Length >= 2 && BinaryPrimitives.ReadUInt16BigEndian(r.Buffer) == id) return DnsWire.Parse(r.Buffer, id);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { last = new TimeoutException(L.T("core.hosts.finder.silent")); }
            catch (SocketException e) { last = e; }
        }
        throw last!;
    }
}

/// <summary>DNS-over-HTTPS (RFC 8484, POST application/dns-message). Провайдер его не подменяет, но может блокировать сам сервер.</summary>
public sealed class DohDnsSource : IDnsSource, IDisposable
{
    static readonly MediaTypeHeaderValue Mime = new("application/dns-message");
    readonly HttpClient _http;
    readonly string _url;

    public string Name { get; }

    /// <param name="connectTo">Адрес сервера: имя из URL тогда не разрешается через систему (провайдер может подменить и его), SNI остаётся по имени.</param>
    public DohDnsSource(string name, string url, IPAddress? connectTo = null)
    {
        Name = name;
        _url = url;
        var handler = new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(4) };
        if (connectTo is not null)
            handler.ConnectCallback = async (ctx, token) =>
            {
                var s = new Socket(connectTo.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try { await s.ConnectAsync(new IPEndPoint(connectTo, ctx.DnsEndPoint.Port), token); return new NetworkStream(s, ownsSocket: true); }
                catch { s.Dispose(); throw; }
            };
        // Quad9 и Яндекс отвечают только по HTTP/2 (на HTTP/1.1 — 505): версия задаётся в запросе, Default* на SendAsync не действует.
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
    }

    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct = default)
    {
        var a = Ask(host, DnsWire.TypeA, ct);
        var aaaa = Ask(host, DnsWire.TypeAaaa, ct);
        return [.. await a, .. await aaaa];
    }

    async Task<IPAddress[]> Ask(string host, ushort type, CancellationToken ct)
    {
        using var body = new ByteArrayContent(DnsWire.Query(host, type, 0));
        body.Headers.ContentType = Mime;
        using var req = new HttpRequestMessage(HttpMethod.Post, _url)
        {
            Content = body, Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-message"));
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return DnsWire.Parse(await resp.Content.ReadAsByteArrayAsync(ct));
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Резолвер Windows: учитывает hosts и DNS, настроенный в системе, — то, что увидит браузер сейчас.</summary>
public sealed class SystemDnsSource : IDnsSource
{
    public string Name => SystemName;
    /// <summary>Имя системного источника: и признак (по нему сравнивают), и подпись — на языке окна даёт <see cref="DnsSources.Display"/>.</summary>
    public const string SystemName = "Система";
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct = default) => Dns.GetHostAddressesAsync(host, ct);
}

public static class DnsSources
{
    /// <summary>
    /// Сторонние DNS, отвечающие адресами своих прокси для сайтов, закрытых для России: трафик идёт через их сервер (сертификат настоящий).
    /// Не проверены по vetting.md — только с флагом --extra, в отчёте с пометкой.
    /// </summary>
    public static readonly IReadOnlyList<(string Name, string Url)> Extra =
    [
        ("Comss DoH", "https://dns.comss.one/dns-query"),
        ("Xbox-DNS DoH", "https://xbox-dns.ru/dns-query"),
    ];

    /// <summary>
    /// Набор по умолчанию — те же варианты, что на экране DNS (<see cref="DnsPresets.All"/>): сначала система (если сайт и так открывается,
    /// строка не нужна), затем каждый вариант по UDP и по DoH. DoH подключается к адресу варианта, а не по имени из URL.
    /// </summary>
    public static List<IDnsSource> Default(bool extra = false)
    {
        var list = new List<IDnsSource> { new SystemDnsSource() };
        var presets = DnsPresets.All.Where(p => p.Ips.Count > 0).ToList();
        foreach (var p in presets) list.Add(new UdpDnsSource($"{p.Title} UDP", IPAddress.Parse(p.Ips[0])));
        foreach (var p in presets.Where(p => p.Doh is not null))
            list.Add(new DohDnsSource($"{p.Title} DoH", p.Doh!, IPAddress.Parse(p.Ips[0])));
        if (extra)
            foreach (var (name, url) in Extra) list.Add(new DohDnsSource(name, url));
        return list;
    }

    /// <summary>Подпись источника на языке окна: системный — по ключу, остальные (Google UDP, Cloudflare DoH…) — как есть.</summary>
    public static string Display(string source) => source == SystemDnsSource.SystemName ? L.T("core.hosts.source_system") : source;

    /// <summary>Пометка для отчёта: российский DNS с реестром блокировок или сторонний непроверенный.</summary>
    public static string? Note(string source) =>
        Extra.Any(e => e.Name == source) ? L.T("core.hosts.source_extra")
        : DnsPresets.All.FirstOrDefault(p => p.Note is not null && source.StartsWith(p.Title + " ", StringComparison.Ordinal))?.Note;

    /// <summary>Свой список: строка «адрес [имя]» — UDP, «https://… [имя]» — DoH, «system» — системный. # — комментарий.</summary>
    public static List<IDnsSource> Parse(string text)
    {
        var list = new List<IDnsSource>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0) continue;
            var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            var name = parts.Length > 1 ? parts[1].Trim() : parts[0];
            if (parts[0].Equals("system", StringComparison.OrdinalIgnoreCase)) list.Add(new SystemDnsSource());
            else if (parts[0].StartsWith("https://", StringComparison.OrdinalIgnoreCase)) list.Add(new DohDnsSource(name, parts[0]));
            else if (IPAddress.TryParse(parts[0], out var ip)) list.Add(new UdpDnsSource(name, ip));
            else throw new FormatException(L.T("core.hosts.dns_bad_line", new { line }));
        }
        return list;
    }
}

/// <summary>Один адрес домена: откуда взят и чем кончилась проверка.</summary>
public sealed record FoundAddress(IPAddress Ip, EndpointCheck Check, IReadOnlyList<string> Sources)
{
    public bool Ok => Check.Ok;
}

/// <summary>Что дал один DNS по одному домену.</summary>
public sealed record SourceAnswer(string Source, IReadOnlyList<IPAddress> Ips, string? Error)
{
    public bool Answered => Error is null;
}

public sealed class DomainResult(string host)
{
    public string Host { get; } = host;
    public List<SourceAnswer> Answers { get; } = [];
    public List<FoundAddress> Addresses { get; } = [];
    /// <summary>Лучший рабочий адрес (быстрейший). null — ни один не открылся.</summary>
    public FoundAddress? Best => Addresses.Where(a => a.Ok).MinBy(a => a.Check.Seconds);
    /// <summary>Источник открывает сайт: хоть один его адрес прошёл проверку (браузер переберёт адреса из ответа).</summary>
    public bool WorksVia(string source) =>
        Answers.FirstOrDefault(a => a.Source == source) is { Answered: true } ans &&
        Addresses.Any(x => x.Ok && ans.Ips.Contains(x.Ip));
}

/// <summary>Сколько доменов открывает один DNS (режим «все DNS»).</summary>
public sealed record SourceCoverage(string Source, int Works, int Answered, double AvgSeconds);

public sealed record HostsFindReport(
    IReadOnlyList<DomainResult> Domains, IReadOnlyList<SourceCoverage> Coverage, bool AllMode, DateTime At)
{
    /// <summary>Домены, которые и так открываются через системный DNS: строка hosts им не нужна.</summary>
    public IEnumerable<DomainResult> WorkAsIs => Domains.Where(d => d.WorksVia("Система"));
    public IEnumerable<DomainResult> Unreachable => Domains.Where(d => d.Best is null);

    /// <summary>
    /// DNS, который открывает больше всего доменов (не считая системного). Ничья — меньше средняя задержка.
    /// null — ни один сторонний DNS не помог.
    /// </summary>
    public SourceCoverage? BestDns => Coverage.Where(c => c.Source != "Система" && c.Works > 0)
        .OrderByDescending(c => c.Works).ThenBy(c => c.AvgSeconds).FirstOrDefault();

    /// <summary>
    /// Строки hosts: домены, которым нужна помощь. <paramref name="withDns"/> — после смены DNS на него: строки только для того, что он не открывает.
    /// </summary>
    public IReadOnlyList<(DomainResult Domain, FoundAddress Address)> HostsLines(string? withDns = null) =>
        Domains.Where(d => !d.WorksVia("Система") && (withDns is null || !d.WorksVia(withDns)) && d.Best is not null)
            .Select(d => (d, d.Best!)).ToList();
}

public sealed record HostsFindProgress(string Message);

/// <summary>
/// Подбор: режим «до первого» — DNS по очереди, каждый спрашивается только о доменах, для которых ещё нет рабочего адреса;
/// режим «все DNS» — каждый DNS о каждом домене, затем покрытие по DNS и строки hosts для остатка.
/// </summary>
public sealed class HostsFinder(IReadOnlyList<IDnsSource> sources, IEndpointProbe probe, int parallel = 16)
{
    /// <summary>Только IPv4. По умолчанию — если у машины нет глобального IPv6, иначе каждый IPv6-адрес ждал бы тайм-аут.</summary>
    public bool IPv4Only { get; init; } = !HasGlobalIPv6();

    public static bool HasGlobalIPv6() =>
        System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Any(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6 && HostsRules.IsPublicAddress(a.Address));

    readonly SemaphoreSlim _gate = new(Math.Max(1, parallel));

    async Task<T> Gated<T>(Func<Task<T>> f, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await f(); }
        finally { _gate.Release(); }
    }

    public async Task<HostsFindReport> RunAsync(IReadOnlyList<string> hosts, bool all, IProgress<HostsFindProgress>? progress = null, CancellationToken ct = default)
    {
        var results = hosts.Select(h => new DomainResult(h)).ToList();
        // Проверка (домен, адрес) делается один раз, сколько бы DNS ни вернули этот адрес.
        var checks = new Dictionary<(string, IPAddress), Task<EndpointCheck>>();

        foreach (var src in sources)
        {
            var todo = all ? results : results.Where(r => r.Best is null).ToList();
            if (todo.Count == 0) break;
            var sw = Stopwatch.StartNew();
            await Task.WhenAll(todo.Select(r => AskAsync(src, r, checks, ct)));
            var found = todo.Count(r => r.WorksVia(src.Name));
            var errors = todo.Select(r => r.Answers.Last(a => a.Source == src.Name).Error).OfType<string>().ToList();
            progress?.Report(new(L.T("core.hosts.finder.progress", new
            {
                name = $"{DnsSources.Display(src.Name),-16}", found, total = todo.Count,
                errors = errors.Count > 0 ? L.T("core.hosts.finder.errors", new { n = errors.Count, kind = errors.GroupBy(e => e).MaxBy(g => g.Count())!.Key }) : "",
                sec = sw.Elapsed.TotalSeconds.ToString("F1"),
            })));
        }

        var coverage = sources.Select(s =>
        {
            var answered = results.Where(r => r.Answers.Any(a => a.Source == s.Name && a.Answered)).ToList();
            var works = results.Where(r => r.WorksVia(s.Name)).ToList();
            var avg = works.Count == 0 ? 0 : works.Average(r =>
                r.Addresses.Where(x => x.Ok && x.Sources.Contains(s.Name)).Min(x => x.Check.Seconds));
            return new SourceCoverage(s.Name, works.Count, answered.Count, avg);
        }).ToList();
        return new HostsFindReport(results, coverage, all, DateTime.Now);
    }

    async Task AskAsync(IDnsSource src, DomainResult r, Dictionary<(string, IPAddress), Task<EndpointCheck>> checks, CancellationToken ct)
    {
        IPAddress[] ips;
        try { ips = await Gated(() => src.ResolveAsync(r.Host, ct), ct); }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            lock (r) r.Answers.Add(new SourceAnswer(src.Name, [], Short(e)));
            return;
        }
        // Заглушки (0.0.0.0, 127.*, частные сети) — не адрес сайта: так отвечают блокирующие DNS и сам hosts.
        var usable = ips.Distinct().Where(HostsRules.IsPublicAddress)
            .Where(ip => !IPv4Only || ip.AddressFamily == AddressFamily.InterNetwork).ToList();
        lock (r) r.Answers.Add(new SourceAnswer(src.Name, usable, null));

        var jobs = new List<(IPAddress Ip, Task<EndpointCheck> Check)>();
        lock (checks)
            foreach (var ip in usable)
            {
                if (!checks.TryGetValue((r.Host, ip), out var t))
                    checks[(r.Host, ip)] = t = Gated(() => probe.CheckAsync(r.Host, ip, ct), ct);
                jobs.Add((ip, t));
            }
        foreach (var (ip, t) in jobs)
        {
            var check = await t;
            lock (r)
            {
                var i = r.Addresses.FindIndex(a => a.Ip.Equals(ip));
                if (i < 0) r.Addresses.Add(new FoundAddress(ip, check, [src.Name]));
                else if (!r.Addresses[i].Sources.Contains(src.Name)) r.Addresses[i] = r.Addresses[i] with { Sources = [.. r.Addresses[i].Sources, src.Name] };
            }
        }
    }

    static string Short(Exception e) => e switch
    {
        TimeoutException or TaskCanceledException => L.T("core.hosts.finder.silent"),
        HttpRequestException h => h.StatusCode is { } c ? $"HTTP {(int)c}" : L.T("core.hosts.finder.no_connection"),
        SocketException s => s.SocketErrorCode == SocketError.HostNotFound ? L.T("core.hosts.finder.no_name") : s.SocketErrorCode.ToString(),
        _ => e.Message,
    };
}

/// <summary>Текст отчёта и готовый блок hosts — общий для консоли и файла.</summary>
public static class HostsFindText
{
    public static string Report(HostsFindReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(L.T("core.hosts.rep.title", new { at = r.At.ToString("yyyy-MM-dd HH:mm"), mode = L.T(r.AllMode ? "core.hosts.rep.mode_all" : "core.hosts.rep.mode_first"), n = r.Domains.Count }));
        sb.AppendLine(L.T("core.hosts.rep.depends"));
        sb.AppendLine();

        foreach (var d in r.Domains)
        {
            string state = d.WorksVia("Система") ? L.T("core.hosts.rep.opens_anyway")
                : d.Best is { } b ? L.T("core.hosts.rep.best_addr", new { ip = b.Ip, sources = string.Join(", ", b.Sources.Select(DnsSources.Display)), sec = b.Check.Seconds.ToString("F2") })
                : d.Answers.All(a => a.Ips.Count == 0) ? L.T("core.hosts.rep.no_name")
                : L.T("core.hosts.rep.none_opened");
            sb.AppendLine($"#  {d.Host,-40} {state}");
        }

        if (r.AllMode)
        {
            sb.AppendLine();
            sb.AppendLine(L.T("core.hosts.rep.coverage"));
            foreach (var c in r.Coverage.OrderByDescending(c => c.Works).ThenBy(c => c.AvgSeconds))
                sb.AppendLine($"#  {DnsSources.Display(c.Source),-16} {c.Works,3}/{r.Domains.Count}" + (c.Works > 0 ? L.T("core.hosts.rep.coverage_avg", new { sec = c.AvgSeconds.ToString("F2") }) : "") +
                    (c.Answered < r.Domains.Count ? L.T("core.hosts.rep.coverage_answered", new { n = c.Answered }) : "") +
                    (DnsSources.Note(c.Source) is { } note ? $"  — {note}" : ""));
            var sys = r.Coverage.FirstOrDefault(c => c.Source == "Система")?.Works ?? 0;
            if (r.BestDns is { } best && best.Works > sys)
                sb.AppendLine(L.T("core.hosts.rep.best_dns", new { source = DnsSources.Display(best.Source), works = best.Works, sys }));
            else
                sb.AppendLine(L.T("core.hosts.rep.no_better"));
        }

        var withDns = r.AllMode && r.BestDns is { } bd && bd.Works > (r.Coverage.FirstOrDefault(c => c.Source == "Система")?.Works ?? 0) ? bd.Source : null;
        var lines = r.HostsLines(withDns);
        sb.AppendLine();
        sb.AppendLine(lines.Count == 0 ? L.T("core.hosts.rep.no_lines") : L.T("core.hosts.rep.lines", new { n = lines.Count }));
        foreach (var (d, a) in lines)
            sb.AppendLine(L.T("core.hosts.rep.line_note", new { ip = a.Ip, host = d.Host, sources = string.Join(", ", a.Sources.Select(DnsSources.Display)), sec = a.Check.Seconds.ToString("F2") }));
        if (lines.Count > 0)
            sb.AppendLine(L.T("core.hosts.rep.recheck"));
        return sb.ToString();
    }
}
