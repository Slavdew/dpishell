using System.Text;
using System.Text.Json;
using DpiShell.Core.Localization;

namespace DpiShell.Core.Export;

/// <summary>Раздел пакета в окне загрузки: сколько записей, нужна ли осторожность, что внутри; пропущен — с причиной.</summary>
public sealed record PackageSection(string Id, string Title, int Count, bool Risky, string? SkippedReason, IReadOnlyList<string> Items)
{
    public bool Usable => SkippedReason is null;
}

/// <summary>
/// Разобранный пакет. Битый раздел пропущен с причиной (<see cref="Sections"/>, <see cref="Problems"/>), остальные прочитаны.
/// <see cref="Fatal"/> — пакет не читается целиком (нет папки, нет или не тот manifest.json, формат другой версии).
/// </summary>
public sealed class ParsedPackage
{
    public string Folder { get; init; } = "";
    public SettingsManifest? Manifest { get; set; }
    public string? Fatal { get; set; }
    public ImportData Data { get; } = new();
    public List<PackageSection> Sections { get; } = [];
    public List<SectionProblem> Problems { get; } = [];

    public bool Ok => Fatal is null;
    public IEnumerable<SectionProblem> ProblemsOf(string section) => Problems.Where(p => p.Section == section);
}

/// <summary>Чтение пакета настроек из папки: размеры и число строк ограничены до разбора, каждый файл проверяется отдельно.</summary>
public static class SettingsImport
{
    internal static readonly JsonSerializerOptions ManifestJson = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, MaxDepth = 8 };

    internal static readonly JsonSerializerOptions StrictFileJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        MaxDepth = 8,
    };

    /// <summary>
    /// Прочитать папку. Ничего не применяет и на диск не пишет. Файлы чужих людей: неизвестные имена игнорируются, ссылки (reparse point) и слишком большие файлы
    /// отклоняются, текст — строго UTF-8 без нулевых байтов, JSON — строго по схеме (лишние и повторные поля — ошибка раздела).
    /// </summary>
    public static ParsedPackage Read(string folder)
    {
        var pkg = new ParsedPackage { Folder = folder };
        if (!Directory.Exists(folder)) { pkg.Fatal = L.T("export.pkg.no_folder"); return pkg; }
        var manifestText = ReadText(Path.Combine(folder, SettingsSections.ManifestFile), SettingsSections.ManifestFile, SettingsSections.MaxManifestBytes, 400, out var mwhy);
        if (manifestText is null) { pkg.Fatal = mwhy is null ? L.T("export.pkg.no_manifest") : L.T("export.pkg.bad_manifest", new { reason = mwhy }); return pkg; }
        SettingsManifest? m;
        try { m = JsonSerializer.Deserialize<SettingsManifest>(manifestText, ManifestJson); }
        catch (JsonException) { m = null; }
        if (m is null) { pkg.Fatal = L.T("export.pkg.bad_manifest", new { reason = L.T("export.file.bad_json") }); return pkg; }
        if (m.Format > SettingsSections.FormatVersion) { pkg.Fatal = L.T("export.pkg.format_newer", new { format = m.Format }); return pkg; }
        if (m.Format < SettingsSections.FormatVersion) { pkg.Fatal = L.T("export.pkg.format_bad"); return pkg; }
        m.App = SettingsExport.CleanLine(m.App, 40) ?? "";
        m.AppVersion = SettingsExport.CleanLine(m.AppVersion, 40) ?? "";
        m.Created = SettingsExport.CleanLine(m.Created, 40) ?? "";
        m.Provider = SettingsExport.CleanLine(m.Provider, SettingsSections.MaxProvider);
        m.Note = SettingsExport.CleanLine(m.Note, SettingsSections.MaxNote);
        m.Sections = [.. (m.Sections ?? []).Where(SettingsSections.IsKnown).Distinct()];
        pkg.Manifest = m;

        foreach (var id in SettingsSections.All.Where(m.Sections.Contains))
        {
            var problems = new List<SectionProblem>();
            string? skipped = null;
            try { skipped = ReadSection(folder, id, pkg.Data, problems); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { skipped = L.T("export.file.unreadable", new { reason = e.Message }); }
            pkg.Problems.AddRange(problems);
            if (skipped is null && pkg.Data.Count(id) == 0)
                skipped = problems.FirstOrDefault()?.Reason ?? L.T("export.section.empty");
            if (skipped is not null) Drop(pkg.Data, id);
            pkg.Sections.Add(new(id, SettingsSections.Title(id), skipped is null ? pkg.Data.Count(id) : 0, SettingsSections.IsRisky(id, pkg.Data.Dns),
                skipped, skipped is null ? SettingsExport.Describe(pkg.Data, id) : []));
        }
        return pkg;
    }

    /// <summary>Что будет при загрузке выбранных разделов (<see cref="SettingsPlanner.Plan"/>).</summary>
    public static ImportPlan Plan(ParsedPackage package, SettingsState current, IEnumerable<string> sections, ImportMode mode) =>
        SettingsPlanner.Plan(package, current, sections, mode);

    /// <summary>Записать план (в помощнике — после повторной проверки; окно отправляет его командой settings_import).</summary>
    public static Task<ImportResult> ApplyAsync(ImportPlan plan, ISettingsTarget target, CancellationToken ct = default) =>
        SettingsApplier.ApplyAsync(plan, target, ct);

    static void Drop(ImportData d, string id)
    {
        switch (id)
        {
            case SettingsSections.Sites: d.Sites = null; break;
            case SettingsSections.Exclude: d.Exclude = null; break;
            case SettingsSections.Subnets: d.Subnets = null; d.SubnetsExclude = null; break;
            case SettingsSections.Strategies: d.Strategies = null; break;
            case SettingsSections.Advanced: d.Advanced = null; break;
            case SettingsSections.Apps: d.Apps = null; break;
            case SettingsSections.Hosts: d.Hosts = null; break;
            case SettingsSections.Dns: d.Dns = null; break;
        }
    }

    /// <summary>Прочитать раздел в <paramref name="d"/>. Возврат — причина, по которой раздел пропущен целиком (null — прочитан, возможно с замечаниями).</summary>
    static string? ReadSection(string folder, string id, ImportData d, List<SectionProblem> problems)
    {
        switch (id)
        {
            case SettingsSections.Sites:
            {
                var lines = ReadLinesFile(folder, SettingsSections.SitesFile, SettingsSections.SitesBytes, SettingsSections.MaxSites, out var why);
                if (lines is null) return why;
                d.Sites = SettingsClean.Domains(lines, SettingsSections.MaxSites, id, SettingsSections.SitesFile, problems);
                return null;
            }
            case SettingsSections.Exclude:
            {
                var lines = ReadLinesFile(folder, SettingsSections.ExcludeFile, SettingsSections.ExcludeBytes, SettingsSections.MaxExclude, out var why);
                if (lines is null) return why;
                d.Exclude = SettingsClean.Domains(lines, SettingsSections.MaxExclude, id, SettingsSections.ExcludeFile, problems);
                return null;
            }
            case SettingsSections.Hosts:
            {
                var lines = ReadLinesFile(folder, SettingsSections.HostsFile, SettingsSections.HostsBytes, SettingsSections.MaxHosts, out var why, hosts: true);
                if (lines is null) return why;
                d.Hosts = SettingsClean.Hosts(lines, SettingsSections.MaxHosts, id, SettingsSections.HostsFile, problems);
                return null;
            }
            case SettingsSections.Subnets:
            {
                bool any = false;
                string? firstWhy = null;
                foreach (var (file, exclude) in new[] { (SettingsSections.SubnetsFile, false), (SettingsSections.SubnetsExcludeFile, true) })
                {
                    if (!File.Exists(Path.Combine(folder, file))) continue;
                    var text = ReadText(Path.Combine(folder, file), file, SettingsSections.SubnetsBytes, SettingsSections.MaxSubnets + SettingsSections.CommentLines, out var why);
                    if (text is null) { firstWhy ??= why; problems.Add(new(id, file, why!)); continue; }
                    any = true;
                    var list = SettingsClean.Subnets(text, SettingsSections.MaxSubnets, id, file, problems);
                    if (exclude) d.SubnetsExclude = list; else d.Subnets = list;
                }
                return any ? null : firstWhy ?? L.T("export.file.missing", new { file = SettingsSections.SubnetsFile });
            }
            case SettingsSections.Strategies: return ReadStrategies(folder, d, problems);
            case SettingsSections.Apps: return ReadApps(folder, d, problems);
            case SettingsSections.Advanced: return ReadAdvanced(folder, d, problems);
            case SettingsSections.Dns: return ReadDns(folder, d, problems);
        }
        return L.T("export.section.unknown");
    }

    static string? ReadStrategies(string folder, ImportData d, List<SectionProblem> problems)
    {
        const string id = SettingsSections.Strategies;
        var dir = Path.Combine(folder, id);
        if (!Directory.Exists(dir)) return L.T("export.file.missing", new { file = id + "/" });
        var raw = new List<ImportStrategy>();
        var files = ListFiles(dir, id, ".txt", 2 * SettingsSections.MaxStrategies, problems);
        foreach (var f in files)
        {
            var name = Path.GetFileNameWithoutExtension(f);
            var rel = id + "/" + Path.GetFileName(f);
            var text = ReadText(f, rel, SettingsSections.StrategyBytes, 20, out var why);
            if (text is null) { problems.Add(new(id, rel, why!)); continue; }
            var lines = Lines(text).Select(l => l.Text).ToList();
            if (lines.Count != 1) { problems.Add(new(id, rel, L.T("export.strategy.one_line"))); continue; }
            try
            {
                // Только строка DpiShell («Скопировать параметры»); запуск из .bat сюда не годится — формат файла один.
                var parsed = StrategyExchange.Decode(lines[0]);
                raw.Add(new() { Name = name, Title = parsed.Title, Roles = new Dictionary<string, string>(parsed.Roles) });
            }
            catch (StrategyFormatException e) { problems.Add(new(id, rel, L.T("export.strategy.bad", new { reason = e.Message }))); }
        }
        d.Strategies = SettingsClean.Strategies(raw, null, problems);
        return null;
    }

    static string? ReadApps(string folder, ImportData d, List<SectionProblem> problems)
    {
        const string id = SettingsSections.Apps;
        var dir = Path.Combine(folder, id);
        if (!Directory.Exists(dir)) return L.T("export.file.missing", new { file = id + "/" });
        var raw = new List<ImportApp>();
        foreach (var f in ListFiles(dir, id, ".json", 2 * SettingsSections.MaxApps, problems))
        {
            var rel = id + "/" + Path.GetFileName(f);
            var text = ReadText(f, rel, SettingsSections.JsonBytes, 2000, out var why);
            if (text is null) { problems.Add(new(id, rel, why!)); continue; }
            AppFile? a;
            try { a = JsonSerializer.Deserialize<AppFile>(text, StrictFileJson); }
            catch (JsonException) { a = null; }
            if (a?.Title is null || a.Addresses is null) { problems.Add(new(id, rel, L.T("export.file.bad_json"))); continue; }
            raw.Add(new()
            {
                Name = Path.GetFileNameWithoutExtension(f), Title = a.Title, Tcp = a.Tcp ?? "", Udp = a.Udp ?? "", Addresses = a.Addresses,
                TcpDesync = a.TcpDesync ?? "", UdpDesync = a.UdpDesync ?? "",
            });
        }
        d.Apps = SettingsClean.Apps(raw, null, problems);
        return null;
    }

    static string? ReadAdvanced(string folder, ImportData d, List<SectionProblem> problems)
    {
        const string id = SettingsSections.Advanced, file = SettingsSections.AdvancedFile;
        var text = ReadText(Path.Combine(folder, file), file, SettingsSections.JsonBytes, 5000, out var why);
        if (text is null) return why ?? L.T("export.file.missing", new { file });
        AdvancedFile? a;
        try { a = JsonSerializer.Deserialize<AdvancedFile>(text, StrictFileJson); }
        catch (JsonException) { a = null; }
        if (a?.Rows is null) return L.T("export.file.bad_json");
        var raw = new ImportAdvanced
        {
            Enabled = a.Enabled, Others = a.Others ?? AdvancedSites.OthersLists,
            Rows = [.. a.Rows.Select(r => new ImportAdvancedRow { Domains = r.Domains ?? [], Strategy = r.Strategy, StrategyFile = r.StrategyFile })],
        };
        if (raw.Rows.Count > AdvancedSites.MaxRows * 2) return L.T("export.file.too_many_lines", new { file, max = AdvancedSites.MaxRows });
        d.Advanced = SettingsClean.Advanced(raw, null, problems);
        return d.Advanced is null ? problems.LastOrDefault(p => p.Section == id)?.Reason ?? L.T("export.file.bad_json") : null;
    }

    static string? ReadDns(string folder, ImportData d, List<SectionProblem> problems)
    {
        const string file = SettingsSections.DnsFile;
        var text = ReadText(Path.Combine(folder, file), file, 4096, 100, out var why);
        if (text is null) return why ?? L.T("export.file.missing", new { file });
        DnsFile? f;
        try { f = JsonSerializer.Deserialize<DnsFile>(text, StrictFileJson); }
        catch (JsonException) { f = null; }
        if (f?.Choice is null) return L.T("export.file.bad_json");
        d.Dns = SettingsClean.Dns(new() { Choice = f.Choice, Ips = f.Ips, Doh = f.Doh }, problems);
        return d.Dns is null ? problems.LastOrDefault(p => p.Section == SettingsSections.Dns)?.Reason : null;
    }

    /// <summary>Файлы папки раздела: только обычные файлы с нужным расширением и безопасным именем, не больше <paramref name="max"/>; остальное — замечания.</summary>
    static List<string> ListFiles(string dir, string section, string ext, int max, List<SectionProblem> problems)
    {
        var res = new List<string>();
        foreach (var entry in Directory.GetFileSystemEntries(dir).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(entry);
            var rel = section + "/" + name;
            if (Directory.Exists(entry)) { problems.Add(new(section, rel, L.T("export.name.folder"))); continue; }
            if (!name.EndsWith(ext, StringComparison.Ordinal)) { problems.Add(new(section, rel, L.T("export.name.ext", new { ext }))); continue; }
            if (!SettingsClean.IsSafeName(Path.GetFileNameWithoutExtension(name))) { problems.Add(new(section, rel, L.T("export.name.bad", new { name = SettingsClean.Cut(name) }))); continue; }
            if (res.Count >= max) { problems.Add(new(section, rel, L.T("export.pkg.too_many_files", new { max }))); break; }
            res.Add(entry);
        }
        return res;
    }

    // ---------- чтение файлов с пределами ----------

    /// <summary>Файл целиком как текст или null с причиной. Нет файла — null без причины.</summary>
    static string? ReadText(string path, string display, int maxBytes, int maxLines, out string? why)
    {
        why = null;
        var info = new FileInfo(path);
        if (!info.Exists) return null;
        if ((info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) { why = L.T("export.file.link", new { file = display }); return null; }
        if (info.Length > maxBytes) { why = L.T("export.file.too_big", new { file = display, max = maxBytes / 1024 }); return null; }
        byte[] bytes;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            // Размер мог вырасти между проверкой и чтением — читаем не больше предела.
            bytes = new byte[Math.Min(fs.Length, maxBytes + 1L)];
            int read = 0, n;
            while (read < bytes.Length && (n = fs.Read(bytes, read, bytes.Length - read)) > 0) read += n;
            if (read > maxBytes) { why = L.T("export.file.too_big", new { file = display, max = maxBytes / 1024 }); return null; }
            if (read < bytes.Length) Array.Resize(ref bytes, read);
        }
        string text;
        try { text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch (DecoderFallbackException) { why = L.T("export.file.not_text", new { file = display }); return null; }
        if (text.Contains('\0')) { why = L.T("export.file.not_text", new { file = display }); return null; }
        text = text.TrimStart((char)0xFEFF);
        int lines = 1;
        foreach (var c in text) if (c == '\n' && ++lines > maxLines) { why = L.T("export.file.too_many_lines", new { file = display, max = maxLines }); return null; }
        return text;
    }

    /// <summary>Строки без пустых и комментариев, с номерами строк файла (нумерация с единицы). Для hosts отсекается и хвостовой комментарий.</summary>
    static List<(int Line, string Text)> Lines(string text, bool hosts = false)
    {
        var res = new List<(int, string)>();
        int n = 0;
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            n++;
            var l = raw.Trim();
            if (hosts) { var h = l.IndexOf('#'); if (h >= 0) l = l[..h].Trim(); }
            if (l.Length == 0 || l.StartsWith('#')) continue;
            res.Add((n, l));
        }
        return res;
    }

    static List<(int Line, string Text)>? ReadLinesFile(string folder, string file, int maxBytes, int maxItems, out string? why, bool hosts = false)
    {
        var path = Path.Combine(folder, file);
        if (!File.Exists(path)) { why = L.T("export.file.missing", new { file }); return null; }
        var text = ReadText(path, file, maxBytes, maxItems + SettingsSections.CommentLines, out why);
        return text is null ? null : Lines(text, hosts);
    }
}
