using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DpiShell.Core.Localization;

namespace DpiShell.Core.Export;

// Файловые DTO: snake_case, как в docs/settings-export.md.
sealed class AdvancedFileRow
{
    public List<string>? Domains { get; set; }
    public string? Strategy { get; set; }
    public string? StrategyFile { get; set; }
}

sealed class AdvancedFile
{
    public bool Enabled { get; set; }
    public string? Others { get; set; }
    public List<AdvancedFileRow>? Rows { get; set; }
}

sealed class AppFile
{
    public string? Title { get; set; }
    public string? Tcp { get; set; }
    public string? Udp { get; set; }
    public List<string>? Addresses { get; set; }
    public string? TcpDesync { get; set; }
    public string? UdpDesync { get; set; }
}

sealed class ModeFile
{
    public string? Mode { get; set; }
}

sealed class DnsFile
{
    public string? Choice { get; set; }
    public List<string>? Ips { get; set; }
    public string? Doh { get; set; }
    /// <summary>Адреса для поиска сервера DoH (пишет роутер); на ПК проверяются и не используются, в выгрузку ПК не попадают.</summary>
    public List<string>? Bootstrap { get; set; }
}

/// <summary>Результат выгрузки: куда записано, какие разделы и что пришлось пропустить.</summary>
public sealed record ExportResult(string Folder, IReadOnlyList<string> Sections, IReadOnlyList<SectionProblem> Skipped);

/// <summary>
/// Выгрузка своих настроек папкой текстовых файлов (docs/settings-export.md). Данные приходят как <see cref="SettingsState"/> — их читает помощник
/// (папка данных закрыта для окна). Окно сначала показывает <see cref="Preview"/> (галочки и «Свои сайты: 12»), потом зовёт <see cref="Write"/>.
/// </summary>
public static class SettingsExport
{
    internal static readonly JsonSerializerOptions FileJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        MaxDepth = 8,
    };

    /// <summary>Разделы с числом записей и самими записями (чтобы показать, что уйдёт: свои сайты бывают личными). Пустые разделы тоже в списке — без галочки.</summary>
    public static IReadOnlyList<SectionPreview> Preview(SettingsState s)
    {
        var data = Gather(s, out _);
        return [.. SettingsSections.All.Select(id => new SectionPreview(id, SettingsSections.Title(id), data.Count(id),
            SettingsSections.IsRisky(id, s.Dns), Describe(data, id)))];
    }

    /// <summary>
    /// Записать выбранные разделы в папку: она новая или уже пустая; папка с чужими файлами не трогается. Прошлая выгрузка DpiShell в папке
    /// заменяется целиком. Пустые разделы не пишутся (пустой файл при загрузке «заменить» стёр бы чужие настройки).
    /// </summary>
    public static ExportResult Write(string folder, SettingsState state, IEnumerable<string> sections, ExportMeta? meta = null)
    {
        var chosen = sections.Where(SettingsSections.IsKnown).Distinct().ToList();
        var data = Gather(state, out var names);
        var skipped = new List<SectionProblem>();
        var written = chosen.Where(id => data.Count(id) > 0).ToList();
        if (Directory.Exists(folder))
        {
            var known = KnownEntries();
            var foreign = Directory.GetFileSystemEntries(folder).Select(Path.GetFileName).Where(n => !known.Contains(n!)).ToList();
            if (foreign.Count > 0) throw new IOException(L.T("export.err.folder_not_empty", new { folder }));
            foreach (var e in Directory.GetFileSystemEntries(folder))
            {
                if (Directory.Exists(e)) Directory.Delete(e, true); else File.Delete(e);
            }
        }
        Directory.CreateDirectory(folder);

        foreach (var id in written)
        {
            switch (id)
            {
                case SettingsSections.Sites: WriteSites(folder, data); break;
                case SettingsSections.Exclude: WriteLines(folder, SettingsSections.ExcludeFile, "sites opened without optimization, one domain per line", data.Exclude!); break;
                case SettingsSections.Subnets:
                    if (data.Subnets is { Count: > 0 }) WriteLines(folder, SettingsSections.SubnetsFile, "own IP subnets (CIDR), one per line", data.Subnets);
                    if (data.SubnetsExclude is { Count: > 0 }) WriteLines(folder, SettingsSections.SubnetsExcludeFile, "own excluded IP subnets (CIDR), one per line", data.SubnetsExclude);
                    break;
                case SettingsSections.Hosts: WriteLines(folder, SettingsSections.HostsFile, "own hosts lines: <public IP> <site name>", data.Hosts!); break;
                case SettingsSections.Strategies:
                {
                    var dir = Path.Combine(folder, SettingsSections.StrategiesDir);
                    Directory.CreateDirectory(dir);
                    foreach (var s in data.Strategies!)
                    {
                        try { WriteText(Path.Combine(dir, s.Name + ".txt"), StrategyText(s)); }
                        catch (StrategyFormatException e) { skipped.Add(new(id, s.Name, e.Message)); }
                    }
                    break;
                }
                case SettingsSections.Apps:
                {
                    var dir = Path.Combine(folder, SettingsSections.AppsDir);
                    Directory.CreateDirectory(dir);
                    foreach (var a in data.Apps!)
                        WriteText(Path.Combine(dir, a.Name + ".json"), JsonSerializer.Serialize(new AppFile
                        {
                            Title = a.Title, Tcp = a.Tcp.Length > 0 ? a.Tcp : null, Udp = a.Udp.Length > 0 ? a.Udp : null, Addresses = a.Addresses,
                            TcpDesync = a.TcpDesync.Length > 0 ? a.TcpDesync : null, UdpDesync = a.UdpDesync.Length > 0 ? a.UdpDesync : null,
                        }, FileJson) + "\n");
                    break;
                }
                case SettingsSections.Advanced:
                {
                    var a = data.Advanced!;
                    WriteText(Path.Combine(folder, SettingsSections.AdvancedFile), JsonSerializer.Serialize(new AdvancedFile
                    {
                        Enabled = a.Enabled, Others = a.Others,
                        Rows = [.. a.Rows.Select(r => new AdvancedFileRow { Domains = r.Domains, Strategy = r.Strategy, StrategyFile = r.StrategyFile })],
                    }, FileJson) + "\n");
                    break;
                }
                case SettingsSections.Dns:
                    WriteText(Path.Combine(folder, SettingsSections.DnsFile), JsonSerializer.Serialize(
                        new DnsFile { Choice = data.Dns!.Choice, Ips = data.Dns.Ips, Doh = data.Dns.Doh }, FileJson) + "\n");
                    break;
            }
        }
        // Раздел «стратегии», у которого не записалась ни одна, в манифесте не значится.
        if (written.Contains(SettingsSections.Strategies) && !Directory.EnumerateFiles(Path.Combine(folder, SettingsSections.StrategiesDir)).Any())
        {
            Directory.Delete(Path.Combine(folder, SettingsSections.StrategiesDir));
            written.Remove(SettingsSections.Strategies);
        }
        var manifest = new SettingsManifest
        {
            Format = SettingsSections.FormatVersion, App = "DpiShell", AppVersion = meta?.AppVersion ?? Helper.HelperProtocol.AppVersion,
            Created = (meta?.Created ?? DateTime.UtcNow).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
            Sections = [.. SettingsSections.All.Where(written.Contains)],
            Provider = CleanLine(meta?.Provider, SettingsSections.MaxProvider), Note = CleanLine(meta?.Note, SettingsSections.MaxNote),
        };
        WriteText(Path.Combine(folder, SettingsSections.ManifestFile), JsonSerializer.Serialize(manifest, FileJson) + "\n");
        return new(folder, manifest.Sections, skipped);
    }

    /// <summary>Имя папки пакета (и папки внутри архива): dpishell-settings-ГГГГ-ММ-ДД.</summary>
    public static string PackageName(DateTime? created = null) => "dpishell-settings-" + (created ?? DateTime.Now).ToString("yyyy-MM-dd");

    /// <summary>
    /// То же, что <see cref="Write"/>, но одним архивом .zip: внутри папка <see cref="PackageName"/> (так делает и роутер). Пишется во временную папку,
    /// архив появляется целиком или не появляется; существующий файл заменяется (подтверждение — дело окна).
    /// </summary>
    public static ExportResult WriteZip(string zipPath, SettingsState state, IEnumerable<string> sections, ExportMeta? meta = null)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "dpishell-export-" + Guid.NewGuid().ToString("N"));
        var part = zipPath + ".part";
        try
        {
            var folder = Path.Combine(tmp, PackageName(meta?.Created?.ToLocalTime()));
            var r = Write(folder, state, sections, meta);
            if (File.Exists(part)) File.Delete(part);
            System.IO.Compression.ZipFile.CreateFromDirectory(folder, part, System.IO.Compression.CompressionLevel.Optimal, includeBaseDirectory: true);
            File.Move(part, zipPath, overwrite: true);
            return new(zipPath, r.Sections, r.Skipped);
        }
        finally
        {
            try { if (File.Exists(part)) File.Delete(part); } catch (IOException) { }
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch (IOException) { }
        }
    }

    static HashSet<string> KnownEntries() =>
    [
        SettingsSections.ManifestFile, SettingsSections.SitesFile, SettingsSections.ExcludeFile, SettingsSections.AdvancedFile,
        SettingsSections.SubnetsFile, SettingsSections.SubnetsExcludeFile, SettingsSections.HostsFile, SettingsSections.DnsFile,
        SettingsSections.StrategiesDir, SettingsSections.AppsDir,
    ];

    /// <summary>Одна строка без управляющих знаков (в том числе меняющих направление текста), не длиннее предела; пусто — null.</summary>
    public static string? CleanLine(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var sb = new StringBuilder();
        foreach (var r in s.EnumerateRunes())
        {
            var cat = Rune.GetUnicodeCategory(r);
            if (cat is System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator) sb.Append(' ');
            else if (cat is System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.OtherNotAssigned or System.Globalization.UnicodeCategory.PrivateUse) continue;
            else sb.Append(r.ToString());
        }
        var t = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        if (t.Length > max) t = t[..max].TrimEnd();
        return t.Length == 0 ? null : t;
    }

    /// <summary>
    /// sites.txt: домен в строке. Группы — комментарии «# group: Имя» перед сайтами группы (сайты без группы идут первыми, без метки): остальные читатели
    /// (роутер, прежние версии) пропускают комментарии и видят обычный список. Формат пакета не меняется.
    /// </summary>
    static void WriteSites(string folder, ImportData data)
    {
        var sites = data.Sites!;
        var groups = data.SiteGroups;
        if (groups is not { Count: > 0 }) { WriteLines(folder, SettingsSections.SitesFile, "own sites, one domain per line", sites); return; }
        var lines = new List<string>(sites.Where(s => !groups.ContainsKey(s)));
        foreach (var name in sites.Where(groups.ContainsKey).Select(s => groups[s]).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            lines.Add("# group: " + SiteGroups.CleanName(name));
            lines.AddRange(sites.Where(s => groups.TryGetValue(s, out var g) && string.Equals(g, name, StringComparison.OrdinalIgnoreCase)));
        }
        WriteLines(folder, SettingsSections.SitesFile, "own sites, one domain per line; \"# group: Name\" starts a group", lines);
    }

    static void WriteLines(string folder, string file, string what, IEnumerable<string> lines) =>
        WriteText(Path.Combine(folder, file), "# DpiShell settings: " + what + "\n" + string.Concat(lines.Select(l => l + "\n")));

    /// <summary>UTF-8 без BOM, переводы строк LF — один и тот же файл на Windows и на роутере.</summary>
    static void WriteText(string path, string text) => File.WriteAllText(path, text.Replace("\r\n", "\n"), new UTF8Encoding(false));

    static string StrategyText(ImportStrategy s) =>
        "# DpiShell strategy: " + CleanLine(s.Title, StrategyExchange.MaxTitle) + "\n"
        + "# The line below is the same text as \"Copy parameters\" in DpiShell.\n"
        + StrategyExchange.Encode(s.Title, s.Roles) + "\n";

    /// <summary>
    /// Состояние → то, что войдёт в пакет: тот же вид, что и после чтения (<see cref="ImportData"/>), поэтому «выгрузить → загрузить» даёт те же данные.
    /// <paramref name="names"/> — имена файлов стратегий по id (на них ссылаются строки «своих настроек для сайтов»).
    /// </summary>
    static ImportData Gather(SettingsState s, out Dictionary<string, string> names)
    {
        var d = new ImportData();
        d.Sites = [.. s.Sites];
        d.SiteGroups = s.SiteGroups is { Count: > 0 } ? new(s.SiteGroups) : null;
        d.Exclude = [.. s.Exclude];
        d.Subnets = [.. s.Subnets];
        d.SubnetsExclude = [.. s.SubnetsExclude];
        d.Hosts = [.. s.Hosts];
        names = [];
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        d.Strategies = [];
        foreach (var st in s.Exportable.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            var name = SettingsClean.Unique(SettingsClean.Slug(st.Title, "strategy"), used);
            names[st.Id] = name;
            d.Strategies.Add(new() { Name = name, Title = st.Title, Roles = new Dictionary<string, string>(st.Roles) });
        }
        var rows = new List<ImportAdvancedRow>();
        foreach (var r in s.Advanced.Rows)
        {
            if (r.Strategy is AdvancedSites.Auto or AdvancedSites.Direct) rows.Add(new() { Domains = [.. r.Domains], Strategy = r.Strategy });
            else if (names.TryGetValue(r.Strategy, out var n)) rows.Add(new() { Domains = [.. r.Domains], StrategyFile = n });
            else if (s.Strategies.Any(x => x.Id == r.Strategy && !SettingsState.IsExportable(x))) rows.Add(new() { Domains = [.. r.Domains], Strategy = r.Strategy });
            // Стратегия, которой нет ни в каталоге, ни среди своих (удалена), — строка бессмысленна для другого человека.
        }
        d.Advanced = rows.Count > 0 ? new() { Enabled = s.Advanced.Enabled, Others = s.Advanced.Others, Rows = rows } : null;
        var appNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        d.Apps = [.. s.Apps.Select(a => new ImportApp
        {
            Name = SettingsClean.Unique(SettingsClean.Slug(a.Title, "app"), appNames), Title = a.Title, Tcp = a.TcpPorts, Udp = a.UdpPorts,
            Addresses = [.. a.Addresses], TcpDesync = a.TcpDesync, UdpDesync = a.UdpDesync,
        })];
        d.Dns = s.Dns is { Choice: not (DnsPresets.Auto or "other" or "") } c ? new DnsChoice { Choice = c.Choice, Ips = c.Ips, Doh = c.Doh } : null;
        if (d.Strategies.Count == 0) d.Strategies = null;
        if (d.Apps.Count == 0) d.Apps = null;
        if (d.Sites.Count == 0) d.Sites = null;
        if (d.Exclude.Count == 0) d.Exclude = null;
        if (d.Subnets.Count == 0) d.Subnets = null;
        if (d.SubnetsExclude.Count == 0) d.SubnetsExclude = null;
        if (d.Hosts.Count == 0) d.Hosts = null;
        return d;
    }

    /// <summary>Записи раздела для показа человеку: что именно выгрузится или загрузится.</summary>
    public static IReadOnlyList<string> Describe(ImportData d, string section) => section switch
    {
        SettingsSections.Sites => d.Sites ?? [],
        SettingsSections.Exclude => d.Exclude ?? [],
        SettingsSections.Subnets => [.. (d.Subnets ?? []), .. (d.SubnetsExclude ?? []).Select(x => L.T("export.item.excluded", new { text = x }))],
        SettingsSections.Hosts => d.Hosts ?? [],
        SettingsSections.Strategies => [.. (d.Strategies ?? []).Select(x => x.Title)],
        SettingsSections.Apps => [.. (d.Apps ?? []).Select(x => x.Title)],
        SettingsSections.Advanced => [.. (d.Advanced?.Rows ?? []).Select(DescribeRow)],
        SettingsSections.Dns => d.Dns is null ? [] : [d.Dns.Describe()],
        _ => [],
    };

    static string DescribeRow(ImportAdvancedRow r)
    {
        var sites = string.Join(", ", r.Domains.Take(3)) + (r.Domains.Count > 3 ? "…" : "");
        return $"{sites} → {r.Strategy ?? r.StrategyFile}";
    }
}
