using System.Net;
using System.Text.RegularExpressions;
using DpiShell.Core.Localization;

namespace DpiShell.Core.Export;

/// <summary>
/// Единая проверка содержимого пакета настроек: ею проходит и то, что прочитано из чужих файлов, и то, что окно прислало помощнику
/// (помощник проверяет ещё раз и окну не доверяет). Каждая запись проверяется теми же правилами, что и ввод руками: домены — как у
/// «Своих сайтов» (<see cref="AdvancedSites.Domain"/>), параметры стратегий — белым списком winws (<see cref="StrategyExchange.CheckRoles"/>),
/// подсети — <see cref="IpsetValidator"/>, профили — <see cref="AppProfiles.Problem"/>, hosts — только публичный адрес и имя сайта.
/// Негодная запись отбрасывается с причиной; ничего не «чинится» молча.
/// </summary>
public static partial class SettingsClean
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.\-]{0,63}\z")]
    private static partial Regex NameRx();

    /// <summary>
    /// Имя файла раздела (strategies/&lt;имя&gt;.txt, apps/&lt;имя&gt;.json) и ссылка на него: латиница, цифры, «_», «-», «.», до 64 знаков,
    /// без «..», без точки в конце. Пути, обратные и прямые слеши, диски и абсолютные пути — отказ.
    /// </summary>
    public static bool IsSafeName(string? name) =>
        name is not null && NameRx().IsMatch(name) && !name.Contains("..") && !name.EndsWith('.');

    static readonly Dictionary<char, string> Translit = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "e", ['ж'] = "zh", ['з'] = "z", ['и'] = "i", ['й'] = "y",
        ['к'] = "k", ['л'] = "l", ['м'] = "m", ['н'] = "n", ['о'] = "o", ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u", ['ф'] = "f",
        ['х'] = "h", ['ц'] = "c", ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "sch", ['ъ'] = "", ['ы'] = "y", ['ь'] = "", ['э'] = "e", ['ю'] = "yu", ['я'] = "ya",
    };

    /// <summary>Имя для файла из названия: строчная латиница и цифры через «-» (русские буквы — транслитом); пусто — запасное имя.</summary>
    public static string Slug(string? title, string fallback)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in (title ?? "").ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(c);
            else if (Translit.TryGetValue(c, out var t)) sb.Append(t);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
            if (sb.Length >= 40) break;
        }
        var s = sb.ToString().Trim('-');
        if (s.Length == 0) s = fallback;
        return IsSafeName(s) ? s : fallback;
    }

    /// <summary>Свободное имя: <paramref name="slug"/>, потом slug-2, slug-3… (без учёта регистра — файловая система Windows).</summary>
    public static string Unique(string slug, ISet<string> used)
    {
        var name = slug;
        for (int n = 2; !used.Add(name); n++) name = $"{slug}-{n}";
        return name;
    }

    public static string Cut(string? s) => s is null ? "" : s.Length > 40 ? s[..40] + "…" : s;

    static string Ln(int line, string reason) => line > 0 ? L.T("export.line.prefix", new { line }) + reason : reason;

    static SectionProblem Prob(string section, string? file, int line, string reason) => new(section, file, Ln(line, reason));

    static SectionProblem Limit(string section, string? file, int max) => new(section, file, L.T("export.item.limit", new { max }));

    // ---------- списки доменов ----------

    public static List<string> Domains(IReadOnlyList<(int Line, string Text)> raw, int max, string section, string? file, List<SectionProblem> problems)
    {
        var res = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (line, text) in raw)
        {
            var d = AdvancedSites.Domain(text);
            if (d is null) { problems.Add(Prob(section, file, line, L.T("export.line.bad_domain", new { text = Cut(text) }))); continue; }
            if (!seen.Add(d)) continue;
            if (res.Count >= max) { problems.Add(Limit(section, file, max)); break; }
            res.Add(d);
        }
        return res;
    }

    static List<string> Domains(List<string> raw, int max, string section, string? file, List<SectionProblem> problems) =>
        Domains([.. raw.Select((t, i) => (0, t))], max, section, file, problems);

    // ---------- подсети ----------

    /// <summary>
    /// Свои подсети и исключения: канонический вид, без служебных сетей и слишком широких (шире /16 для IPv4, /32 для IPv6 — чужой файл не должен
    /// перехватывать полсети). Номера строк в причинах — по тексту <paramref name="text"/>.
    /// </summary>
    public static List<string> Subnets(string text, int max, string section, string? file, List<SectionProblem> problems)
    {
        var v = IpsetValidator.Validate(text, IpsetKind.User);
        foreach (var p in v.Problems) problems.Add(new(section, file, p));
        if (v.Dropped > v.Problems.Count) problems.Add(new(section, file, L.T("export.subnet.more", new { count = v.Dropped - v.Problems.Count })));
        var res = new List<string>();
        foreach (var e in v.Entries)
        {
            if (e == IpLists.Placeholder) continue;
            if (!Cidr.TryParse(e, false, out var c, out _, out _)) continue;
            if (c.Len < (c.V6 ? 32 : 16)) { problems.Add(new(section, file, L.T("export.subnet.wide", new { text = Cut(e) }))); continue; }
            if (res.Count >= max) { problems.Add(Limit(section, file, max)); break; }
            res.Add(e);
        }
        return res;
    }

    // ---------- hosts ----------

    /// <summary>
    /// Строка hosts из чужого файла: ровно «адрес имя», адрес — публичный (петля 127.*, 0.0.0.0, частные и служебные сети — отказ, значит подмены
    /// системных и локальных имён не проходят), имя — настоящее имя сайта. Каждая новая строка при записи ещё проверяется живым подключением
    /// (сертификат должен быть выдан этому сайту).
    /// </summary>
    public static bool TryHostsLine(string text, out string canonical, out string? reason)
    {
        canonical = ""; reason = null;
        var parts = text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) { reason = L.T("export.hosts.two_words", new { text = Cut(text) }); return false; }
        if (!IPAddress.TryParse(parts[0], out _) || parts[0].Contains('%')) { reason = L.T("export.hosts.bad_ip", new { text = Cut(parts[0]) }); return false; }
        if (!HostsRules.TryParsePublic(parts[0], out var ip)) { reason = L.T("export.hosts.not_public", new { text = Cut(text) }); return false; }
        if (!HostsPaste.TryName(parts[1], out var host) || !AdvancedSites.IsDomain(host) || host != HostsRules.Normalize(parts[1]))
        { reason = L.T("export.hosts.bad_name", new { text = Cut(parts[1]) }); return false; }
        canonical = new HostsEntry(host, ip.ToString()).ToString();
        return true;
    }

    public static List<string> Hosts(IReadOnlyList<(int Line, string Text)> raw, int max, string section, string? file, List<SectionProblem> problems)
    {
        var res = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (line, text) in raw)
        {
            if (!TryHostsLine(text, out var c, out var why)) { problems.Add(Prob(section, file, line, why!)); continue; }
            if (!seen.Add(c)) continue;
            if (res.Count >= max) { problems.Add(Limit(section, file, max)); break; }
            res.Add(c);
        }
        return res;
    }

    // ---------- стратегии ----------

    public static List<ImportStrategy> Strategies(List<ImportStrategy> raw, SettingsState? env, List<SectionProblem> problems)
    {
        var res = new List<ImportStrategy>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Func<string, bool>? fake = env is null ? null : n => env.FakeFiles.Contains(n, StringComparer.OrdinalIgnoreCase);
        foreach (var s in raw)
        {
            var file = SettingsSections.StrategiesDir + "/" + Cut(s.Name);
            if (!IsSafeName(s.Name)) { problems.Add(new(SettingsSections.Strategies, file, L.T("export.name.bad", new { name = Cut(s.Name) }))); continue; }
            if (!names.Add(s.Name)) { problems.Add(new(SettingsSections.Strategies, file, L.T("export.name.dup", new { name = s.Name }))); continue; }
            if (StrategyExchange.CheckRoles(s.Roles, fake) is { } why)
            { problems.Add(new(SettingsSections.Strategies, file, L.T("export.strategy.bad", new { reason = why }))); continue; }
            if (res.Count >= SettingsSections.MaxStrategies) { problems.Add(Limit(SettingsSections.Strategies, file, SettingsSections.MaxStrategies)); break; }
            res.Add(new() { Name = s.Name, Title = StrategyExchange.CleanTitle(s.Title), Roles = new Dictionary<string, string>(s.Roles) });
        }
        return res;
    }

    // ---------- свои настройки для сайтов ----------

    public static ImportAdvanced? Advanced(ImportAdvanced raw, SettingsState? env, List<SectionProblem> problems)
    {
        const string sec = SettingsSections.Advanced, file = SettingsSections.AdvancedFile;
        if (!AdvancedSites.IsOthers(raw.Others)) { problems.Add(new(sec, file, L.T("export.advanced.bad_others"))); return null; }
        var res = new ImportAdvanced { Enabled = raw.Enabled, Others = raw.Others };
        var catalog = env?.Strategies.Where(s => !SettingsState.IsExportable(s)).Select(s => s.Id).ToHashSet();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < raw.Rows.Count; i++)
        {
            var r = raw.Rows[i];
            var (domains, problem) = AdvancedSites.Domains(r.Domains, i);
            if (domains is null) { problems.Add(new(sec, file, problem!)); continue; }
            string? strategy = null, fileRef = null;
            if (r.Strategy is not null && r.StrategyFile is not null) { problems.Add(new(sec, file, L.T("export.advanced.both", new { row = i + 1 }))); continue; }
            if (r.StrategyFile is not null)
            {
                if (!IsSafeName(r.StrategyFile)) { problems.Add(new(sec, file, L.T("export.advanced.bad_ref", new { row = i + 1, name = Cut(r.StrategyFile) }))); continue; }
                fileRef = r.StrategyFile;
            }
            else if (r.Strategy is AdvancedSites.Auto or AdvancedSites.Direct) strategy = r.Strategy;
            else if (r.Strategy is not null && AdvancedSites.IsStrategyChoice(r.Strategy) && (catalog is null || catalog.Contains(r.Strategy))) strategy = r.Strategy;
            else { problems.Add(new(sec, file, L.T("export.advanced.bad_strategy", new { row = i + 1, name = Cut(r.Strategy) }))); continue; }
            if (res.Rows.Count >= AdvancedSites.MaxRows) { problems.Add(Limit(sec, file, AdvancedSites.MaxRows)); break; }
            var row = new ImportAdvancedRow { Domains = domains, Strategy = strategy, StrategyFile = fileRef };
            if (seen.Add(RowKey(row))) res.Rows.Add(row);
        }
        return res;
    }

    /// <summary>Что считать одной и той же строкой: сайты и стратегия.</summary>
    public static string RowKey(ImportAdvancedRow r) => string.Join(',', r.Domains) + "|" + (r.Strategy ?? "file:" + r.StrategyFile);

    // ---------- профили игр и программ ----------

    /// <summary>Вариант параметров этого ПК, подходящий профилю: сравниваются сами параметры (id стратегий у людей разные). null — подходящего нет.</summary>
    public static int? FindVariant(SettingsState env, string tcpPorts, string udpPorts, string tcpDesync, string udpDesync)
    {
        foreach (var v in env.Variants)
            if ((tcpPorts.Length == 0 || v.Tcp == tcpDesync) && (udpPorts.Length == 0 || v.Udp == udpDesync)) return v.Index;
        return null;
    }

    public static List<ImportApp> Apps(List<ImportApp> raw, SettingsState? env, List<SectionProblem> problems)
    {
        var res = new List<ImportApp>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in raw)
        {
            var file = SettingsSections.AppsDir + "/" + Cut(a.Name);
            if (!IsSafeName(a.Name)) { problems.Add(new(SettingsSections.Apps, file, L.T("export.name.bad", new { name = Cut(a.Name) }))); continue; }
            if (!names.Add(a.Name)) { problems.Add(new(SettingsSections.Apps, file, L.T("export.name.dup", new { name = a.Name }))); continue; }
            var list = new List<SectionProblem>();
            var addresses = Subnets(string.Join('\n', a.Addresses), AppProfiles.MaxAddresses, SettingsSections.Apps, file, list);
            var p = new AppProfile
            {
                Id = "p00000000", Title = a.Title.Trim(), TcpPorts = a.Tcp, UdpPorts = a.Udp, Addresses = addresses,
                TcpDesync = a.Tcp.Length > 0 ? a.TcpDesync : "", UdpDesync = a.Udp.Length > 0 ? a.UdpDesync : "",
            };
            // Адреса, не прошедшие проверку, — причина в журнале раздела; но профиль с урезанным списком не берём: игра без части серверов работает хуже, чем без профиля.
            if (list.Count > 0) { problems.AddRange(list); continue; }
            if (AppProfiles.Problem(p) is { } why) { problems.Add(new(SettingsSections.Apps, file, why)); continue; }
            if (env is not null && FindVariant(env, p.TcpPorts, p.UdpPorts, p.TcpDesync, p.UdpDesync) is null)
            { problems.Add(new(SettingsSections.Apps, file, L.T("export.apps.no_variant", new { title = p.Title }))); continue; }
            if (res.Count >= SettingsSections.MaxApps) { problems.Add(Limit(SettingsSections.Apps, file, SettingsSections.MaxApps)); break; }
            res.Add(new() { Name = a.Name, Title = p.Title, Tcp = p.TcpPorts, Udp = p.UdpPorts, Addresses = p.Addresses, TcpDesync = p.TcpDesync, UdpDesync = p.UdpDesync });
        }
        return res;
    }

    // ---------- DNS ----------

    public static DnsChoice? Dns(DnsChoice raw, List<SectionProblem> problems)
    {
        const string sec = SettingsSections.Dns, file = SettingsSections.DnsFile;
        if (raw.Choice == DnsPresets.Custom)
        {
            if (raw.Ips is not { Count: > 0 and <= DnsValues.MaxServers }) { problems.Add(new(sec, file, L.T("export.dns.bad_ips", new { max = DnsValues.MaxServers }))); return null; }
            var ips = new List<string>();
            foreach (var ip in raw.Ips)
            {
                var n = DnsValues.NormalizeIp(ip);
                if (n is null) { problems.Add(new(sec, file, L.T("export.dns.bad_ip", new { text = Cut(ip) }))); return null; }
                if (!ips.Contains(n)) ips.Add(n);
            }
            if (raw.Doh is not null && !DnsValues.IsDohTemplate(raw.Doh)) { problems.Add(new(sec, file, L.T("export.dns.bad_doh"))); return null; }
            return new() { Choice = DnsPresets.Custom, Ips = ips, Doh = raw.Doh };
        }
        if (DnsPresets.Find(raw.Choice) is null) { problems.Add(new(sec, file, L.T("export.dns.unknown", new { name = Cut(raw.Choice) }))); return null; }
        if (raw.Ips is not null || raw.Doh is not null) { problems.Add(new(sec, file, L.T("export.dns.extra"))); return null; }
        return new() { Choice = raw.Choice };
    }

    // ---------- пакет целиком ----------

    /// <summary>
    /// Проверить содержимое пакета. <paramref name="env"/> — сведения о ПК (фейки, варианты, известные стратегии); null — только то, что видно из самих данных.
    /// Всё негодное отбрасывается, причины — в <paramref name="problems"/>.
    /// </summary>
    public static ImportData Clean(ImportData d, SettingsState? env, List<SectionProblem> problems) => new()
    {
        Sites = d.Sites is null ? null : Domains(d.Sites, SettingsSections.MaxSites, SettingsSections.Sites, SettingsSections.SitesFile, problems),
        Exclude = d.Exclude is null ? null : Domains(d.Exclude, SettingsSections.MaxExclude, SettingsSections.Exclude, SettingsSections.ExcludeFile, problems),
        Subnets = d.Subnets is null ? null : Subnets(string.Join('\n', d.Subnets), SettingsSections.MaxSubnets, SettingsSections.Subnets, SettingsSections.SubnetsFile, problems),
        SubnetsExclude = d.SubnetsExclude is null ? null : Subnets(string.Join('\n', d.SubnetsExclude), SettingsSections.MaxSubnets, SettingsSections.Subnets, SettingsSections.SubnetsExcludeFile, problems),
        Strategies = d.Strategies is null ? null : Strategies(d.Strategies, env, problems),
        Advanced = d.Advanced is null ? null : Advanced(d.Advanced, env, problems),
        Apps = d.Apps is null ? null : Apps(d.Apps, env, problems),
        Hosts = d.Hosts is null ? null : Hosts([.. d.Hosts.Select(h => (0, h))], SettingsSections.MaxHosts, SettingsSections.Hosts, SettingsSections.HostsFile, problems),
        Dns = d.Dns is null ? null : Dns(d.Dns, problems),
    };
}
