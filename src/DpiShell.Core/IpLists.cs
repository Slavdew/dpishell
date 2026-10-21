using Loc = DpiShell.Core.Localization.L;
using System.Text.Json;

namespace DpiShell.Core;

/// <summary>Файл данных в манифесте: хеш обязателен; для IP-списка — ещё адрес источника и счётчики (architecture.md «IP-списки и игровой режим»).</summary>
public sealed record DataFile(string Sha256, string? Url = null, int? Lines = null, double? Ipv4Share = null, DateTime? Updated = null, string? Source = null);

/// <summary>
/// Манифест данных (data/data.json): версия и sha256 каждого файла — по образцу core.json. Ключ — путь от data/ через «/».
/// Версия — дата выпуска данных «ГГГГ-ММ-ДД» (вторая за день — «ГГГГ-ММ-ДД.2»).
/// </summary>
/// <param name="AppVersion">Последняя версия программы — окно покажет ссылку на релиз, если у него старее.</param>
/// <param name="MinAppVersion">С какой версии программы эти данные можно ставить (новый формат, новые параметры winws).</param>
public sealed record DataManifest(string Version, Dictionary<string, DataFile> Files, string? Source = null,
    string? AppVersion = null, string? MinAppVersion = null)
{
    public const string FileName = "data.json";
    public const string IpsetKey = "lists/" + IpLists.AllFile;
    public const int MaxFiles = 200;

    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static DataManifest Load(string path) =>
        JsonSerializer.Deserialize<DataManifest>(File.ReadAllText(path), Json)
        ?? throw new InvalidDataException(Loc.T("core.data.manifest_empty_path", new { path }));

    public static DataManifest Parse(byte[] utf8) =>
        JsonSerializer.Deserialize<DataManifest>(utf8, Json) ?? throw new InvalidDataException(Loc.T("core.data.manifest_empty"));

    /// <summary>Что не так с манифестом для установки обновления (пусто — годен): версия, ключи, хеши, адреса.</summary>
    public IReadOnlyList<string> UpdateProblems()
    {
        var p = new List<string>();
        if (ParseVersion(Version) is null) p.Add(Loc.T("core.data.v.bad_version"));
        if (Files is null || Files.Count == 0) { p.Add(Loc.T("core.data.v.no_files")); return p; }
        if (Files.Count > MaxFiles) p.Add(Loc.T("core.data.v.too_many"));
        foreach (var (key, f) in Files)
        {
            if (!DataKeys.IsAllowed(key)) p.Add(Loc.T("core.data.v.bad_file", new { name = key.Length > 40 ? key[..40] : key }));
            else if (f?.Sha256 is not { Length: 64 } h || !h.All(Uri.IsHexDigit)) p.Add(Loc.T("core.data.v.no_sha", new { key }));
            else if (f.Url is not null && !UpdateSources.IsSafeHttps(f.Url)) p.Add(Loc.T("core.data.v.not_https", new { key }));
        }
        foreach (var v in new[] { AppVersion, MinAppVersion })
            if (v is not null && !System.Version.TryParse(v, out _)) p.Add(Loc.T("core.data.v.bad_app_version"));
        return p;
    }

    /// <summary>Сравнить версии данных; нечитаемая — старше любой читаемой.</summary>
    public static int CompareVersions(string? a, string? b)
    {
        var x = ParseVersion(a);
        var y = ParseVersion(b);
        if (x is null || y is null) return (x is null ? 0 : 1) - (y is null ? 0 : 1);
        int c = x.Value.Date.CompareTo(y.Value.Date);
        return c != 0 ? c : x.Value.N.CompareTo(y.Value.N);
    }

    static (DateOnly Date, int N)? ParseVersion(string? v)
    {
        if (v is null) return null;
        var parts = v.Split('.');
        if (parts.Length > 2 || !DateOnly.TryParseExact(parts[0], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var d)) return null;
        int n = 1;
        if (parts.Length == 2 && (!int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out n) || n is < 1 or > 999)) return null;
        return (d, n);
    }

    /// <summary>Файлы из манифеста, которых нет в <paramref name="dataDir"/> или у которых другой хеш.</summary>
    public IReadOnlyList<string> Mismatches(string dataDir) =>
        Files.Where(f =>
        {
            var p = Path.Combine(dataDir, f.Key.Replace('/', Path.DirectorySeparatorChar));
            return !File.Exists(p) || !string.Equals(Sha256(p), f.Value.Sha256, StringComparison.OrdinalIgnoreCase);
        }).Select(f => f.Key).ToList();

    /// <summary>
    /// Хеш файла данных. Текст (.txt, .json, .sha256) — по содержимому с переводами строк LF: git с text=auto может выдать CRLF,
    /// а смысл файла от этого не меняется. Двоичные (.bin) — побайтно.
    /// </summary>
    public static string Sha256(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".txt" or ".json" or ".sha256")) return CoreInstaller.Sha256(path);
        var b = File.ReadAllBytes(path);
        var lf = new List<byte>(b.Length);
        for (int i = 0; i < b.Length; i++)
            if (!(b[i] == '\r' && i + 1 < b.Length && b[i + 1] == '\n')) lf.Add(b[i]);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(lf.ToArray()));
    }
}

/// <summary>Режим IP-списка: «Выкл» (заглушка), «По списку», «Все адреса» (временно).</summary>
public enum IpsetMode { Off, List, Any }

/// <summary>Игровой режим. В окне — только Off/TcpUdp (решение 1), Tcp/Udp — для отладочной консоли.</summary>
public enum GameMode { Off, TcpUdp, Tcp, Udp }

/// <summary>Что установлено как общий IP-список (ipset-all.meta.json).</summary>
public sealed record IpsetMeta(string Sha256, int Lines, int Ipv4, int Ipv6, double Ipv4Share, string? Version, DateTime? Updated, DateTime InstalledAt, string Source);

/// <param name="Applied">Новый список установлен. False — остался прежний (см. <see cref="Message"/>).</param>
/// <param name="Verdict">Итог валидатора; null — до проверки не дошло (не скачалось, хеш не совпал).</param>
public sealed record IpsetUpdateResult(bool Applied, IpsetVerdict? Verdict, string Message)
{
    /// <summary>Список проверку прошёл, но сильно отличается от прошлого: повторить с confirmed = true после вопроса пользователю.</summary>
    public bool NeedsConfirm => !Applied && Verdict is { NeedsConfirm: true };
}

/// <summary>
/// IP-списки в %ProgramData%\DpiShell\lists (architecture.md «IP-списки и игровой режим»). Режимы не трогают файлы — WinwsArgs.Build подставляет нужный:
/// ipset-all.txt + user-ipset.txt («По списку»), ipset-none.txt (заглушка, «Выкл»), ipset-any.txt (пустой, «Все адреса»).
/// Любое обновление общего списка проходит IpsetValidator; отказ — старый список остаётся.
/// </summary>
public sealed class IpLists(AppPaths paths)
{
    public const string AllFile = "ipset-all.txt";
    public const string PrevFile = "ipset-all.prev.txt";
    public const string MetaFile = "ipset-all.meta.json";
    public const string NoneFile = "ipset-none.txt";
    public const string AnyFile = "ipset-any.txt";
    public const string ExcludeFile = "ipset-exclude.txt";
    public const string UserFile = "user-ipset.txt";
    public const string UserExcludeFile = "user-ipset-exclude.txt";

    /// <summary>Тестовая сеть (RFC 5737): include-лист с ней ничего не ловит — так «выключает» профили и Flowseal.</summary>
    public const string Placeholder = "203.0.113.113/32";

    /// <summary>«Все адреса» — только на время (решение 3).</summary>
    public static readonly TimeSpan AnyDuration = TimeSpan.FromMinutes(10);

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    string L(string f) => Path.Combine(paths.Lists, f);

    /// <summary>Манифест данных из поставки (data\data.json рядом с exe); null — его нет.</summary>
    public DataManifest? PackageManifest
    {
        get
        {
            var f = Path.Combine(paths.Seed, DataManifest.FileName);
            return File.Exists(f) ? DataManifest.Load(f) : null;
        }
    }

    /// <summary>Служебные файлы режимов и пустые свои списки. Свои не перезаписываются.</summary>
    public void EnsureFiles()
    {
        paths.EnsureDirectories();
        WriteIfDifferent(L(NoneFile), Placeholder + "\n");
        WriteIfDifferent(L(AnyFile), "");   // пустой include-лист = ограничения нет (nfq/ipset.c, «old behavior compat»)
        foreach (var f in new[] { UserFile, UserExcludeFile })
            if (!File.Exists(L(f))) File.WriteAllText(L(f), Placeholder + "\n");
    }

    static void WriteIfDifferent(string file, string text)
    {
        if (!File.Exists(file) || File.ReadAllText(file) != text) File.WriteAllText(file, text);
    }

    public IpsetMeta? Meta
    {
        get
        {
            try { return File.Exists(L(MetaFile)) ? JsonSerializer.Deserialize<IpsetMeta>(File.ReadAllText(L(MetaFile)), Json) : null; }
            catch (JsonException) { return null; }
        }
    }

    /// <summary>Общий список установлен и цел (хеш совпадает с meta).</summary>
    public bool HasList => Meta is { } m && File.Exists(L(AllFile))
                           && string.Equals(CoreInstaller.Sha256(L(AllFile)), m.Sha256, StringComparison.OrdinalIgnoreCase);

    /// <summary>Подсети файла (без комментариев и заглушки).</summary>
    public IReadOnlyList<string> Read(string file) =>
        File.Exists(L(file))
            ? File.ReadAllLines(L(file)).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#') && l != Placeholder).ToList()
            : [];

    /// <summary>
    /// Поставить список из поставки, если своего нет, он повреждён (антивирус) или в поставке новее.
    /// Скачанный более свежий список не трогается. Возвращает true, если файл заменён.
    /// </summary>
    public bool SeedFromPackage()
    {
        var pkg = Path.Combine(paths.Seed, "lists", AllFile);
        if (!File.Exists(pkg)) return false;
        var entry = PackageManifest?.Files.GetValueOrDefault(DataManifest.IpsetKey);
        // Файл поставки не совпал с её же манифестом — не ставим ничего, остаётся то, что было.
        if (entry is not null && !string.Equals(DataManifest.Sha256(pkg), entry.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
        if (HasList && (entry?.Updated is not { } packaged || Meta!.Updated >= packaged)) return false;
        return Install(pkg, IpsetValidator.ValidateFile(pkg, IpsetKind.Main), "package", PackageManifest?.Version, entry?.Updated) is { Applied: true };
    }

    /// <summary>Обновить общий список из файла (ручная загрузка). Отказ валидатора или неподтверждённое сильное изменение — остаётся прежний.</summary>
    public IpsetUpdateResult UpdateFromFile(string file, bool confirmed = false, string source = "file", string? version = null, DateTime? updated = null)
    {
        if (!File.Exists(file)) return new(false, null, Loc.T("core.ip.file_missing", new { file }));
        var previous = HasList ? Read(AllFile).ToHashSet() : null;
        var v = IpsetValidator.ValidateFile(file, IpsetKind.Main, previous);
        if (!v.Accepted) return new(false, v, Loc.T("core.ip.rejected", new { errors = string.Join(" ", v.Errors) }));
        if (v.NeedsConfirm && !confirmed) return new(false, v, string.Join(" ", v.Confirm));
        return Install(file, v, source, version, updated ?? File.GetLastWriteTimeUtc(file));
    }

    /// <summary>Скачать список по манифесту (по умолчанию — из поставки): только HTTPS, во временный файл, сверка sha256, затем валидатор.</summary>
    public async Task<IpsetUpdateResult> UpdateFromManifestAsync(HttpClient http, DataManifest? manifest = null, bool confirmed = false,
        IProgress<string>? log = null, CancellationToken ct = default)
    {
        manifest ??= PackageManifest;
        var entry = manifest?.Files.GetValueOrDefault(DataManifest.IpsetKey);
        if (entry?.Url is not { } url) return new(false, null, Loc.T("core.ip.no_url"));
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
            return new(false, null, Loc.T("core.ip.https_only"));
        paths.EnsureDirectories();
        var tmp = Path.Combine(paths.Root, "ipset.download.txt");
        try
        {
            log?.Report(Loc.T("core.ip.downloading"));
            try { await CoreInstaller.DownloadWithRetriesAsync(http, url, tmp, ct); }
            catch (HttpRequestException e) { return new(false, null, Loc.T("core.ip.download_failed", new { error = e.Message })); }
            var hash = DataManifest.Sha256(tmp);
            if (!string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                return new(false, null, Loc.T("core.ip.hash_mismatch"));
            var r = UpdateFromFile(tmp, confirmed, "download", manifest!.Version, entry.Updated);
            log?.Report(r.Message);
            return r;
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    IpsetUpdateResult Install(string from, IpsetVerdict v, string source, string? version, DateTime? updated)
    {
        if (!v.Accepted) return new(false, v, Loc.T("core.ip.rejected", new { errors = string.Join(" ", v.Errors) }));
        paths.EnsureDirectories();
        // В рабочий файл — только то, что прошло проверку, в каноническом виде. winws перечитает его сам.
        var tmp = L(AllFile + ".tmp");
        File.WriteAllText(tmp, string.Join("\n", v.Entries) + "\n");
        if (HasList) File.Copy(L(AllFile), L(PrevFile), overwrite: true);
        File.Move(tmp, L(AllFile), overwrite: true);
        var meta = new IpsetMeta(CoreInstaller.Sha256(L(AllFile)), v.Entries.Count, v.Ipv4, v.Ipv6, v.Ipv4Share, version, updated, DateTime.UtcNow, source);
        File.WriteAllText(L(MetaFile + ".tmp"), JsonSerializer.Serialize(meta, Json));
        File.Move(L(MetaFile + ".tmp"), L(MetaFile), overwrite: true);
        var fixedNote = v.Fixed > 0 ? Loc.T("core.ip.fixed", new { n = v.Fixed }) : "";
        var dropNote = v.Dropped > 0 ? Loc.T("core.ip.dropped", new { n = v.Dropped }) : "";
        return new(true, v, Loc.T("core.ip.updated", new { n = v.Entries.Count, fixes = fixedNote, drops = dropNote }));
    }

    /// <summary>Записать свои подсети (или исключения). Ошибочные строки отбрасываются и возвращаются в Problems; широкие — только с подтверждением.</summary>
    public IpsetVerdict WriteUser(IEnumerable<string> lines, bool exclude = false, bool confirmed = false)
    {
        var v = IpsetValidator.Validate(string.Join("\n", lines), IpsetKind.User);
        if (!v.Accepted || v.NeedsConfirm && !confirmed) return v;
        paths.EnsureDirectories();
        File.WriteAllText(L(exclude ? UserExcludeFile : UserFile), string.Join("\n", v.Entries.Count > 0 ? v.Entries : [Placeholder]) + "\n");
        return v;
    }
}
