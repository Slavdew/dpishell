using System.Text.Json;
using System.Text.RegularExpressions;

namespace DpiShell.Core;

/// <summary>
/// Откуда качать обновления данных (data/update-sources.json): адреса папки data/ нашего репозитория по очереди —
/// основной, затем зеркала. Добавить зеркало — одна строка в файле, код не меняется. Файл едет с программой
/// и обновлением данных не меняется: зеркало не может само себя назначить или добавить другое (architecture.md «Обновления данных»).
/// </summary>
/// <param name="Sources">Базовые адреса (https, с «/» в конце): манифест — <c>{адрес}data.json</c>, файлы — <c>{адрес}{ключ}</c>.</param>
/// <param name="ReleasePage">Страница релизов — ссылка «Скачать новую версию» (приложение само себя не обновляет).</param>
public sealed record UpdateSources(IReadOnlyList<string> Sources, string? ReleasePage = null)
{
    public const string FileName = "update-sources.json";

    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, ReadCommentHandling = JsonCommentHandling.Skip };

    sealed record FileForm(List<string>? Sources, string? ReleasePage);

    /// <summary>Прочитать; негодные адреса (не https, с запросом, логином) отбрасываются. Файла нет — источников нет.</summary>
    public static UpdateSources Load(string path)
    {
        if (!File.Exists(path)) return new([]);
        var f = JsonSerializer.Deserialize<FileForm>(File.ReadAllText(path), Json);
        return From(f?.Sources ?? [], f?.ReleasePage);
    }

    public static UpdateSources From(IEnumerable<string> sources, string? releasePage = null)
    {
        var list = new List<string>();
        foreach (var s in sources)
            if (Normalize(s) is { } n && !list.Contains(n)) list.Add(n);
        return new(list, IsSafeHttps(releasePage) ? releasePage : null);
    }

    static string? Normalize(string? s)
    {
        if (!IsSafeHttps(s)) return null;
        return s!.EndsWith('/') ? s : s + "/";
    }

    /// <summary>Только https, без логина в адресе, запроса и якоря.</summary>
    public static bool IsSafeHttps(string? s) =>
        Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.UserInfo.Length == 0
        && u.Query.Length == 0 && u.Fragment.Length == 0 && !string.IsNullOrEmpty(u.Host);
}

/// <summary>Манифест, полученный с одного из источников.</summary>
/// <param name="Bytes">Как пришёл — кладётся в обновлённую папку данных без пересборки.</param>
public sealed record FetchedManifest(DataManifest Manifest, string Source, byte[] Bytes);

/// <summary>Итог проверки: манифест (null — ни один источник не дал годного) и что не так с остальными.</summary>
public sealed record ManifestFetch(FetchedManifest? Result, IReadOnlyList<string> Errors);

/// <summary>
/// Скачивание по списку источников: по очереди, на каждый — 5 с до ответа сервера (заблокированный адрес
/// не держит проверку). Манифест доверенный, только если пришёл с адреса из списка; файл — только если его sha256
/// совпал с манифестом, иначе он отбрасывается и берётся следующий источник.
/// </summary>
public sealed class DataUpdateClient(HttpClient http, UpdateSources sources)
{
    /// <summary>Сколько ждать ответа сервера на один источник.</summary>
    public TimeSpan PerSourceTimeout { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Сколько ждать тело файла после ответа (IP-список ~0,6 МБ).</summary>
    public TimeSpan BodyTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public const int MaxManifestBytes = 256 * 1024;
    public const int MaxFileBytes = 8 * 1024 * 1024;

    public UpdateSources Sources => sources;

    /// <summary>Манифест с первого источника, который ответил и отдал годный манифест.</summary>
    public async Task<ManifestFetch> FetchManifestAsync(IProgress<string>? log = null, CancellationToken ct = default)
    {
        var errors = new List<string>();
        if (sources.Sources.Count == 0) return new(null, ["Не заданы адреса обновлений (update-sources.json)."]);
        foreach (var src in sources.Sources)
        {
            log?.Report("Проверяю обновления: " + Host(src));
            byte[] bytes;
            try { bytes = await GetAsync(src + DataManifest.FileName, MaxManifestBytes, ct); }
            catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or InvalidDataException && !ct.IsCancellationRequested)
            {
                errors.Add($"{Host(src)}: {Reason(e)}");
                continue;
            }
            DataManifest m;
            try { m = DataManifest.Parse(bytes); }
            catch (Exception e) when (e is JsonException or InvalidDataException)
            {
                errors.Add($"{Host(src)}: манифест не читается");
                continue;
            }
            if (m.UpdateProblems() is { Count: > 0 } p)
            {
                errors.Add($"{Host(src)}: манифест не прошёл проверку ({p[0]})");
                continue;
            }
            return new(new(m, src, bytes), errors);
        }
        return new(null, errors);
    }

    /// <summary>
    /// Файл <paramref name="key"/> с хешем из манифеста: источники по очереди, начиная с <paramref name="first"/>.
    /// Хеш не совпал — файл удаляется, пробуется следующий. Ни один не подошёл — исключение со всеми причинами.
    /// </summary>
    public async Task DownloadVerifiedAsync(string key, string sha256, string target, string? first = null, CancellationToken ct = default)
    {
        var order = first is not null && sources.Sources.Contains(first)
            ? [first, .. sources.Sources.Where(s => s != first)]
            : sources.Sources.ToList();
        var errors = new List<string>();
        foreach (var src in order)
        {
            byte[] bytes;
            try { bytes = await GetAsync(src + key, MaxFileBytes, ct); }
            catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or InvalidDataException && !ct.IsCancellationRequested)
            {
                errors.Add($"{Host(src)}: {Reason(e)}");
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllBytesAsync(target, bytes, ct);
            if (string.Equals(DataManifest.Sha256(target), sha256, StringComparison.OrdinalIgnoreCase)) return;
            File.Delete(target);
            errors.Add($"{Host(src)}: sha256 не совпал с манифестом — файл отброшен");
        }
        throw new SecurityException($"Файл {key} не получен: " + string.Join("; ", errors));
    }

    async Task<byte[]> GetAsync(string url, int max, CancellationToken ct)
    {
        using var head = CancellationTokenSource.CreateLinkedTokenSource(ct);
        head.CancelAfter(PerSourceTimeout);
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, head.Token);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength > max) throw new InvalidDataException("файл слишком большой");
        using var body = CancellationTokenSource.CreateLinkedTokenSource(ct);
        body.CancelAfter(BodyTimeout);
        await using var s = await resp.Content.ReadAsStreamAsync(body.Token);
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = await s.ReadAsync(buf, body.Token)) > 0)
        {
            if (ms.Length + n > max) throw new InvalidDataException("файл слишком большой");
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }

    static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;

    static string Reason(Exception e) => e switch
    {
        TaskCanceledException => "не ответил за 5 с",
        HttpRequestException { StatusCode: { } sc } => $"ответ {(int)sc}",
        HttpRequestException => "нет связи",
        InvalidDataException d => d.Message,
        _ => e.Message,
    };
}

/// <summary>Что показать в окне после проверки.</summary>
/// <param name="DataVersion">Версия данных у источника; null — не проверили.</param>
/// <param name="DataNewer">Есть данные новее установленных и программа их понимает.</param>
/// <param name="NeedsNewApp">Данные новее, но требуют более новую программу.</param>
/// <param name="AppNewer">Вышла новая версия программы (её ставят руками — ссылка на релиз).</param>
public sealed record DataUpdateCheck(string? DataVersion, bool DataNewer, bool NeedsNewApp, string? AppVersion, bool AppNewer)
{
    public static DataUpdateCheck Compare(DataManifest remote, string? currentData, string app)
    {
        bool newer = currentData is null || DataManifest.CompareVersions(remote.Version, currentData) > 0;
        bool tooOld = remote.MinAppVersion is { } min && IsOlder(app, min);
        bool appNewer = remote.AppVersion is { } latest && IsOlder(app, latest);
        return new(remote.Version, newer && !tooOld, newer && tooOld, remote.AppVersion, appNewer);
    }

    public static bool IsOlder(string a, string b) =>
        Version.TryParse(a, out var x) && Version.TryParse(b, out var y) && x < y;
}

/// <summary>Итог установки обновления данных (ответ помощника на data_update).</summary>
/// <param name="Applied">Новые данные установлены.</param>
/// <param name="UpToDate">Установлено то же или новее — делать нечего.</param>
/// <param name="NeedsConfirm">IP-список сильно изменился: повторить с confirmed после вопроса пользователю.</param>
/// <param name="CoreUpdated">Вместе с данными обновилось ядро (core.json в манифесте).</param>
public sealed record DataUpdateResult(bool Applied, bool UpToDate, bool NeedsConfirm, string Message, string? Version = null, bool CoreUpdated = false, string? Source = null);

/// <summary>
/// Установка обновления данных (делает помощник): манифест — с источников из списка, каждый файл — по sha256 манифеста,
/// всё складывается рядом (data.new) из текущих данных и скачанного, проверяется целиком (каталог, цели, ядро,
/// hosts-кандидаты, валидатор IP-списка) и только потом заменяет %ProgramData%\DpiShell\data. Ошибка на любом шаге —
/// остаются прежние данные. Пользовательские файлы (user-*.txt в lists\) в данных не лежат и в манифест попасть не могут.
/// </summary>
public sealed class DataUpdater(AppPaths paths, DataUpdateClient client)
{
    /// <summary>Откуда может прийти ядро, поменявшееся с обновлением данных.</summary>
    public const string OfficialCoreReleases = "https://github.com/bol-van/zapret/releases/download/";

    /// <summary>Поставить ядро по новому core.json (служба останавливается/возвращается снаружи). Null — ядро не трогать.</summary>
    public Func<CoreManifest, Task>? InstallCore { get; init; }
    /// <summary>Версия программы — для min_app_version.</summary>
    public string AppVersion { get; init; } = Helper.HelperProtocol.AppVersion;

    public async Task<DataUpdateResult> InstallAsync(bool confirmed, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var fetch = await client.FetchManifestAsync(log, ct);
        if (fetch.Result is not { } got)
            return new(false, false, false, "Источники обновлений не ответили: " + string.Join("; ", fetch.Errors));
        var m = got.Manifest;
        var current = paths.SeedManifest;
        if (current is not null && DataManifest.CompareVersions(m.Version, current.Version) <= 0)
            return new(false, true, false, $"Данные уже последней версии ({current.Version}).", current.Version, false, got.Source);
        if (m.MinAppVersion is { } min && DataUpdateCheck.IsOlder(AppVersion, min))
            return new(false, false, false, $"Данные {m.Version} требуют новую версию программы ({min}) — скачайте её со страницы релизов.");

        var staged = paths.Updates + ".new";
        try
        {
            if (Directory.Exists(staged)) Directory.Delete(staged, true);
            // Основа — текущие данные целиком: файлы, которых нет в манифесте, остаются как были.
            CopyTree(paths.Seed, staged);
            int downloaded = 0;
            foreach (var (key, f) in m.Files)
            {
                var target = Path.Combine(staged, key.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(target) && string.Equals(DataManifest.Sha256(target), f.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
                log?.Report($"Скачиваю {key}…");
                if (File.Exists(target)) File.Delete(target);
                await client.DownloadVerifiedAsync(key, f.Sha256, target, got.Source, ct);
                downloaded++;
            }
            await File.WriteAllBytesAsync(Path.Combine(staged, DataManifest.FileName), got.Bytes, ct);
            // Список источников — только из поставки (его не меняет и не может поменять зеркало).
            var srcFile = Path.Combine(paths.Package, UpdateSources.FileName);
            if (File.Exists(srcFile)) File.Copy(srcFile, Path.Combine(staged, UpdateSources.FileName), overwrite: true);

            if (Validate(staged, m, confirmed) is { } problem) return problem;

            bool coreChanged = CoreChanged(staged);
            if (coreChanged && InstallCore is { } install)
            {
                log?.Report("Обновляю ядро…");
                await install(CoreManifest.Load(Path.Combine(staged, "core.json")));
            }

            Swap(staged);
            paths.ResetSeed();
            log?.Report($"Данные обновлены до {m.Version}.");
            var note = downloaded == 0 ? "" : $" Скачано файлов: {downloaded}.";
            return new(true, false, false, $"Данные обновлены до {m.Version}.{note}" + (coreChanged ? " Ядро обновлено." : ""), m.Version, coreChanged, got.Source);
        }
        catch (Exception e) when (e is SecurityException or IOException or UnauthorizedAccessException or InvalidDataException or JsonException or HttpRequestException)
        {
            return new(false, false, false, "Обновление не установлено, остались прежние данные: " + e.Message);
        }
        finally
        {
            try { if (Directory.Exists(staged)) Directory.Delete(staged, true); } catch (IOException) { }
        }
    }

    /// <summary>Проверить собранное целиком тем же кодом, что читает данные при работе. Null — всё в порядке.</summary>
    DataUpdateResult? Validate(string staged, DataManifest m, bool confirmed)
    {
        if (m.Mismatches(staged) is { Count: > 0 } bad)
            throw new SecurityException("не совпали хеши: " + string.Join(", ", bad));
        try
        {
            StrategyCatalog.Load(Path.Combine(staged, "strategies.json"));
            TargetSet.Load(Path.Combine(staged, "targets.json"));
            CoreManifest.Load(Path.Combine(staged, "core.json"));
            HostsCandidates.LoadSeed(new AppPaths(paths.Root, staged));
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or IOException or ArgumentException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidDataException("новые данные не прошли проверку: " + e.Message);
        }
        var packageCore = Path.Combine(paths.Package, "core.json");
        if (CoreChanged(staged) && !(File.Exists(packageCore) && DataManifest.Sha256(packageCore) == DataManifest.Sha256(Path.Combine(staged, "core.json"))))
        {
            // Манифест без подписи: новое ядро — только официальный релиз bol-van и без запасных адресов.
            // Подменивший наш репозиторий может выбрать лишь настоящий релиз zapret, но не свой архив.
            var core = CoreManifest.Load(Path.Combine(staged, "core.json"));
            if (!core.ZipUrl.StartsWith(OfficialCoreReleases, StringComparison.Ordinal) || !UpdateSources.IsSafeHttps(core.ZipUrl) || core.ZipUrl.Contains("..")
                || core.MirrorUrls is { Count: > 0 })
                throw new SecurityException("новое ядро — только из релизов bol-van/zapret, без запасных адресов");
        }
        var ipset = Path.Combine(staged, "lists", IpLists.AllFile);
        if (File.Exists(ipset))
        {
            var lists = new IpLists(paths);
            var previous = lists.HasList ? lists.Read(IpLists.AllFile).ToHashSet() : null;
            var v = IpsetValidator.ValidateFile(ipset, IpsetKind.Main, previous);
            if (!v.Accepted) throw new InvalidDataException("IP-список не принят: " + string.Join(" ", v.Errors));
            if (v.NeedsConfirm && !confirmed)
                return new(false, false, true, string.Join(" ", v.Confirm), m.Version);
        }
        return null;
    }

    bool CoreChanged(string staged)
    {
        var now = Path.Combine(paths.Seed, "core.json");
        var next = Path.Combine(staged, "core.json");
        return File.Exists(next) && (!File.Exists(now) || DataManifest.Sha256(now) != DataManifest.Sha256(next));
    }

    void Swap(string staged)
    {
        var target = paths.Updates;
        var old = target + ".old";
        if (Directory.Exists(old)) Directory.Delete(old, true);
        if (Directory.Exists(target)) Directory.Move(target, old);
        try { Directory.Move(staged, target); }
        catch { if (Directory.Exists(old)) Directory.Move(old, target); throw; }
        if (Directory.Exists(old)) Directory.Delete(old, true);
    }

    static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(from, f);
            var dest = Path.Combine(to, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest, overwrite: true);
        }
    }
}

public static partial class DataKeys
{
    /// <summary>
    /// Ключ файла в манифесте: «имя» в корне data\ или «lists|fake|hosts|licenses/имя». Без «..», подпапок глубже,
    /// пробелов и слешей другого вида; data.json, update-sources.json и пользовательские user-* — никогда.
    /// </summary>
    public static bool IsAllowed(string key)
    {
        if (!KeyRx().IsMatch(key) || key.Contains("..")) return false;
        var name = key[(key.LastIndexOf('/') + 1)..];
        if (name.StartsWith("user-", StringComparison.OrdinalIgnoreCase)) return false;
        return !string.Equals(key, DataManifest.FileName, StringComparison.OrdinalIgnoreCase)
               && !string.Equals(key, UpdateSources.FileName, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"^(?:(?:lists|fake|hosts|licenses)/)?[A-Za-z0-9][A-Za-z0-9._\-]{0,79}$")]
    private static partial Regex KeyRx();
}
