using DpiShell.Core.Localization;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DpiShell.Core;

// Обновление самой программы (architecture.md «Обновление программы»): лента windows-vX.Y.Z, release.json + release.json.sig
// (ed25519, ReleaseSignature), portable-архив по sha256 из подписанного release.json, распаковка рядом —
// %LocalAppData%\DpiShell\versions\X.Y.Z\ (права администратора не нужны), перезапуск на новую версию, откат на прежнюю.

/// <summary>Файл выпуска: имя и sha256.</summary>
public sealed record AppReleaseAsset(string Name, string Sha256);

/// <summary>
/// Описание выпуска программы для Windows (<c>release.json</c> релиза <c>windows-vX.Y.Z</c>):
/// <c>{ schema: 1, product, version, tag, min_os, changes: [..], assets: [{ name, sha256 }] }</c>. Собирает CI
/// (<c>packaging/release-json.py</c>), подписывает ключом выпусков. Принимается только с верной подписью.
/// </summary>
/// <param name="MinOs">Минимальная Windows (<c>10.0.17763</c>); null — без ограничения.</param>
/// <param name="Changes">Что нового — словами пользователя (packaging/release-notes.json).</param>
public sealed partial record AppRelease(int Schema, string Version, string Tag, string? MinOs, IReadOnlyList<string> Changes, IReadOnlyList<AppReleaseAsset> Assets)
{
    public const string FileName = "release.json";
    public const string SignatureName = FileName + ReleaseSignature.Extension;
    public const string TagPrefix = "windows-v";
    public const int MaxBytes = 64 * 1024;

    /// <summary>Версия этой программы «X.Y.Z» (из Directory.Build.props — единый источник, CI сверяет с тегом).</summary>
    public static string CurrentVersion
    {
        get
        {
            var v = typeof(AppRelease).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
        }
    }

    /// <summary>Имя portable-архива выпуска (как пишет packaging/make-portable.ps1).</summary>
    public static string PortableName(string version) => $"DpiShell-{version}-portable.zip";

    /// <summary>Архив для этого компьютера (win-x64 portable); null — в выпуске его нет.</summary>
    public AppReleaseAsset? Portable => Assets.FirstOrDefault(a => a.Name == PortableName(Version));

    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    sealed record FileForm(
        int Schema, string? Product, string? Version, string? Tag, string? MinOs,
        List<string>? Changes, List<AssetForm>? Assets);
    sealed record AssetForm(string? Name, string? Sha256);

    /// <summary>Разобрать и проверить поля. Не то — <see cref="InvalidDataException"/>.</summary>
    public static AppRelease Parse(byte[] bytes)
    {
        FileForm f;
        try { f = JsonSerializer.Deserialize<FileForm>(bytes, Json) ?? throw new InvalidDataException("пустой release.json"); }
        catch (JsonException e) { throw new InvalidDataException("release.json не читается: " + e.Message); }
        if (f.Schema != 1) throw new InvalidDataException($"неизвестная схема release.json ({f.Schema})");
        if (f.Product is not null && f.Product != "dpishell-windows") throw new InvalidDataException("release.json — не программы для Windows");
        if (f.Version is not { } v || !SemVerRx().IsMatch(v)) throw new InvalidDataException("версия выпуска — не X.Y.Z");
        if (f.Tag != TagPrefix + v) throw new InvalidDataException($"тег {f.Tag} не совпадает с версией {v}");
        if (f.MinOs is { } os && !System.Version.TryParse(os, out _)) throw new InvalidDataException("min_os — не версия Windows");
        var changes = (f.Changes ?? []).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Length > 300 ? c[..300] : c).Take(30).ToList();
        var assets = new List<AppReleaseAsset>();
        foreach (var a in f.Assets ?? [])
        {
            if (a.Name is not { } n || !AssetNameRx().IsMatch(n) || a.Sha256 is not { } h || !Sha256Rx().IsMatch(h))
                throw new InvalidDataException("файл выпуска с негодным именем или хешем");
            assets.Add(new(n, h.ToLowerInvariant()));
        }
        return new(1, v, f.Tag, f.MinOs, changes, assets);
    }

    /// <summary>a &lt; b для «X.Y.Z» (и «X.Y.Z.W» — четвёртая часть отбрасывается). Не версия — false.</summary>
    public static bool IsOlder(string a, string b) => Parse3(a) is { } x && Parse3(b) is { } y && x < y;

    static System.Version? Parse3(string s) =>
        System.Version.TryParse(s, out var v) ? new System.Version(v.Major, v.Minor, Math.Max(v.Build, 0)) : null;

    [GeneratedRegex(@"^\d{1,4}\.\d{1,4}\.\d{1,6}$")]
    internal static partial Regex SemVerRx();
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-]{0,99}$")]
    private static partial Regex AssetNameRx();
    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Rx();
}

/// <summary>release.json с источника вместе с подписью (она уже проверена).</summary>
public sealed record FetchedRelease(AppRelease Release, string Source, byte[] Bytes, byte[] Signature);

/// <summary>Итог проверки выпуска.</summary>
/// <param name="Found">Описание выпуска с верной подписью; null — ни один источник не дал годного (причины — в Errors).</param>
/// <param name="Newer">Выпуск новее этой программы.</param>
/// <param name="OsTooOld">Выпуск новее, но требует более новую Windows (<c>min_os</c>) — не предлагать.</param>
/// <param name="SignatureFailed">Хотя бы у одного источника подпись описания выпуска не сошлась (признак для окна, не текст ошибки).</param>
public sealed record AppUpdateCheck(FetchedRelease? Found, bool Newer, bool OsTooOld, string Current, IReadOnlyList<string> Errors, bool SignatureFailed = false)
{
    /// <summary>Предложить обновление: есть новее, Windows подходит и архив для неё в выпуске есть.</summary>
    public bool Available => Newer && !OsTooOld && Found?.Release.Portable is not null;
    public string? Version => Found?.Release.Version;
    public IReadOnlyList<string> Changes => Found?.Release.Changes ?? [];
}

/// <summary>
/// Скачивание выпусков: источники по очереди (5 с на ответ сервера), release.json принимается только с верной подписью
/// release.json.sig — подписи нет или она неверна, источник отброшен (переходного периода, как у данных, нет:
/// это исполняемый файл). Архив — только с sha256 из подписанного release.json.
/// </summary>
public sealed class AppUpdateClient(HttpClient http, IReadOnlyList<string> sources)
{
    public TimeSpan PerSourceTimeout { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Сколько ждать тело архива (~70 МБ) после ответа сервера.</summary>
    public TimeSpan BodyTimeout { get; init; } = TimeSpan.FromMinutes(15);
    public byte[] SigningKey { get; init; } = ReleaseSignature.ReleaseKey;
    /// <summary>Версия Windows этого компьютера — для min_os; в тестах — подмена.</summary>
    public Version OsVersion { get; init; } = Environment.OSVersion.Version;

    public const long MaxArchiveBytes = 400L * 1024 * 1024;

    public IReadOnlyList<string> Sources => sources;

    /// <summary>Есть ли выпуск новее <paramref name="current"/>: первый источник с подписанным годным release.json.</summary>
    public async Task<AppUpdateCheck> CheckAsync(string current, CancellationToken ct = default)
    {
        var errors = new List<string>();
        bool badSignature = false;
        if (sources.Count == 0) return new(null, false, false, current, [L.T("core.upd.no_sources")]);
        foreach (var src in sources)
        {
            byte[] bytes, sig;
            try
            {
                bytes = await GetBytesAsync(src + AppRelease.FileName, AppRelease.MaxBytes, ct);
                sig = await GetBytesAsync(src + AppRelease.SignatureName, ReleaseSignature.MaxSignatureFileBytes, ct);
            }
            catch (Exception e) when (IsNetError(e) && !ct.IsCancellationRequested)
            {
                errors.Add($"{Host(src)}: {Reason(e)}");
                continue;
            }
            if (!ReleaseSignature.Verify(bytes, sig, SigningKey))
            {
                errors.Add(L.T("core.upd.bad_release_sig", new { host = Host(src) }));
                badSignature = true;
                continue;
            }
            AppRelease r;
            try { r = AppRelease.Parse(bytes); }
            catch (InvalidDataException e)
            {
                errors.Add($"{Host(src)}: {e.Message}");
                continue;
            }
            bool newer = AppRelease.IsOlder(current, r.Version);
            bool osOld = r.MinOs is { } min && Version.TryParse(min, out var mv) && OsVersion < mv;
            return new(new(r, src, bytes, sig), newer, newer && osOld, current, errors, badSignature);
        }
        return new(null, false, false, current, errors, badSignature);
    }

    /// <summary>
    /// Скачать файл выпуска в <paramref name="target"/> и сверить sha256: сначала с источника описания, затем с остальных.
    /// Не совпал — файл удаляется, пробуется следующий; ни один — <see cref="SecurityException"/> со всеми причинами.
    /// </summary>
    public async Task DownloadAsync(FetchedRelease release, AppReleaseAsset asset, string target, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        List<string> order = [release.Source, .. sources.Where(s => s != release.Source)];
        var errors = new List<string>();
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        foreach (var src in order)
        {
            string hash;
            try { hash = await GetFileAsync(src + asset.Name, target, progress, ct); }
            catch (Exception e) when (IsNetError(e) && !ct.IsCancellationRequested)
            {
                TryDelete(target);
                errors.Add($"{Host(src)}: {Reason(e)}");
                continue;
            }
            if (hash == asset.Sha256) return;
            TryDelete(target);
            errors.Add(L.T("core.upd.bad_archive_hash", new { host = Host(src) }));
        }
        throw new SecurityException(L.T("core.upd.archive_missing", new { name = asset.Name, errors = string.Join("; ", errors) }));
    }

    async Task<HttpResponseMessage> HeadAsync(string url, CancellationToken ct)
    {
        using var head = CancellationTokenSource.CreateLinkedTokenSource(ct);
        head.CancelAfter(PerSourceTimeout);
        var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, head.Token);
        try { resp.EnsureSuccessStatusCode(); }
        catch { resp.Dispose(); throw; }
        return resp;
    }

    async Task<byte[]> GetBytesAsync(string url, int max, CancellationToken ct)
    {
        using var resp = await HeadAsync(url, ct);
        if (resp.Content.Headers.ContentLength > max) throw new InvalidDataException("файл слишком большой");
        using var body = CancellationTokenSource.CreateLinkedTokenSource(ct);
        body.CancelAfter(PerSourceTimeout);
        await using var s = await resp.Content.ReadAsStreamAsync(body.Token);
        using var ms = new MemoryStream();
        var buf = new byte[16384];
        int n;
        while ((n = await s.ReadAsync(buf, body.Token)) > 0)
        {
            if (ms.Length + n > max) throw new InvalidDataException("файл слишком большой");
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }

    /// <summary>Тело — сразу в файл, хеш считается по ходу (архив в память не берём). Возвращает sha256.</summary>
    async Task<string> GetFileAsync(string url, string target, IProgress<long>? progress, CancellationToken ct)
    {
        using var resp = await HeadAsync(url, ct);
        if (resp.Content.Headers.ContentLength > MaxArchiveBytes) throw new InvalidDataException("архив слишком большой");
        using var body = CancellationTokenSource.CreateLinkedTokenSource(ct);
        body.CancelAfter(BodyTimeout);
        await using var s = await resp.Content.ReadAsStreamAsync(body.Token);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var f = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buf = new byte[81920];
            long total = 0;
            int n;
            while ((n = await s.ReadAsync(buf, body.Token)) > 0)
            {
                total += n;
                if (total > MaxArchiveBytes) throw new InvalidDataException("архив слишком большой");
                sha.AppendData(buf, 0, n);
                await f.WriteAsync(buf.AsMemory(0, n), body.Token);
                progress?.Report(total);
            }
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    static bool IsNetError(Exception e) => e is HttpRequestException or IOException or TaskCanceledException or InvalidDataException;

    static void TryDelete(string f)
    {
        try { if (File.Exists(f)) File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;

    static string Reason(Exception e) => e switch
    {
        TaskCanceledException => L.T("core.upd.reason.timeout"),
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } => L.T("core.upd.reason.not_found"),
        HttpRequestException { StatusCode: { } sc } => L.T("core.upd.reason.status", new { code = (int)sc }),
        HttpRequestException => L.T("core.upd.reason.offline"),
        InvalidDataException d => d.Message,
        _ => e.Message,
    };
}

/// <summary>Какая версия запущена из versions\ и на какую можно откатиться.</summary>
/// <param name="Current">Версия, на которую переключились последней.</param>
/// <param name="Previous">Прежняя версия (её не удаляет очистка) — откат.</param>
/// <param name="PreviousExe">Где лежит прежняя: папка versions\ или исходная portable-папка пользователя.</param>
/// <param name="Pending">Версия, на которую только что переключились и которая ещё не подтвердила, что запустилась (окно подтверждает после старта).</param>
/// <param name="PendingStarts">Сколько раз эта версия уже стартовала без подтверждения: второй старт — признак, что она не запускается.</param>
/// <param name="Failed">Версия, которая не запустилась и с которой вернулись автоматически (окно скажет об этом один раз).</param>
public sealed record AppVersionsState(string? Current = null, string? Previous = null, string? PreviousExe = null,
    string? Pending = null, int PendingStarts = 0, string? Failed = null);

/// <summary>
/// Версии программы рядом с пользователем: <c>%LocalAppData%\DpiShell\versions\X.Y.Z\</c> (portable, права не нужны).
/// Хранятся текущая и одна прежняя (откат); остальные удаляет <see cref="Prune"/>. Состояние — <c>versions\state.json</c>.
/// </summary>
public sealed class AppVersionStore(string? root = null)
{
    public const string ExeName = "DpiShell.exe";
    /// <summary>Папка внутри portable-архива (packaging/make-portable.ps1).</summary>
    public const string ArchiveFolder = "DpiShell/";
    /// <summary>Предел распакованного размера (защита от «zip-бомбы»; сам архив уже сверен по sha256).</summary>
    public const long MaxUnpackedBytes = 1024L * 1024 * 1024;

    public string Root { get; } = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DpiShell", "versions");
    string StateFile => Path.Combine(Root, "state.json");

    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };

    public string PathFor(string version) =>
        AppRelease.SemVerRx().IsMatch(version) ? Path.Combine(Root, version) : throw new ArgumentException("версия — не X.Y.Z", nameof(version));

    public string ExeFor(string version) => Path.Combine(PathFor(version), ExeName);

    /// <summary>Версия распакована целиком (последний шаг распаковки — переименование готовой папки).</summary>
    public bool IsInstalled(string version) => File.Exists(ExeFor(version));

    public AppVersionsState State
    {
        get
        {
            try { return File.Exists(StateFile) ? JsonSerializer.Deserialize<AppVersionsState>(File.ReadAllText(StateFile), Json) ?? new() : new(); }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { return new(); }
        }
    }

    /// <summary>Записать состояние целиком (откат неудавшегося переключения, тесты).</summary>
    public void Save(AppVersionsState s)
    {
        Directory.CreateDirectory(Root);
        var tmp = StateFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, Json));
        File.Move(tmp, StateFile, overwrite: true);
    }

    /// <summary>
    /// Распаковать проверенный архив в <c>versions\X.Y.Z\</c>: сначала в <c>X.Y.Z.new</c>, только записи под <c>DpiShell/</c>,
    /// без выхода за папку; нет DpiShell.exe — отказ. Готовая папка появляется одним переименованием.
    /// </summary>
    public string Install(string zipPath, string version)
    {
        var target = PathFor(version);
        if (IsInstalled(version)) return target;
        var staged = target + ".new";
        if (Directory.Exists(staged)) Directory.Delete(staged, true);
        Directory.CreateDirectory(staged);
        var stagedFull = Path.GetFullPath(staged) + Path.DirectorySeparatorChar;
        try
        {
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                long total = 0;
                foreach (var e in zip.Entries)
                {
                    var name = e.FullName.Replace('\\', '/');
                    if (!name.StartsWith(ArchiveFolder, StringComparison.Ordinal))
                        throw new InvalidDataException($"в архиве лишняя запись {e.FullName}");
                    var rel = name[ArchiveFolder.Length..];
                    if (rel.Length == 0) continue;
                    var dest = Path.GetFullPath(Path.Combine(staged, rel.Replace('/', Path.DirectorySeparatorChar)));
                    if (!dest.StartsWith(stagedFull, StringComparison.OrdinalIgnoreCase) || rel.Split('/').Contains(".."))
                        throw new InvalidDataException($"запись архива выходит за папку: {e.FullName}");
                    if (name.EndsWith('/')) { Directory.CreateDirectory(dest); continue; }
                    total += e.Length;
                    if (total > MaxUnpackedBytes) throw new InvalidDataException("архив распаковывается в слишком большой объём");
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    e.ExtractToFile(dest, overwrite: false);
                }
            }
            if (!File.Exists(Path.Combine(staged, ExeName))) throw new InvalidDataException($"в архиве нет {ExeName}");
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(staged, target);
            return target;
        }
        finally
        {
            try { if (Directory.Exists(staged)) Directory.Delete(staged, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Запомнить переключение: прежняя версия (и где её exe) — для отката, новая — текущая.</summary>
    /// <param name="pending">Новая версия ещё не проверена запуском: если она не стартует, следующий старт вернёт прежнюю.</param>
    public void RecordSwitch(string fromVersion, string fromExe, string toVersion, bool pending = false) =>
        Save(new(toVersion, fromVersion, fromExe, pending ? toVersion : null));

    /// <summary>
    /// Удалить версии, кроме текущей, прежней и <paramref name="running"/> (запущенную удалить нельзя — exe занят).
    /// Занятые и недоступные папки пропускаются молча — удалятся в следующий раз.
    /// </summary>
    public IReadOnlyList<string> Prune(string? running = null)
    {
        var st = State;
        var keep = new HashSet<string?>([st.Current, st.Previous, running], StringComparer.OrdinalIgnoreCase);
        var removed = new List<string>();
        if (!Directory.Exists(Root)) return removed;
        foreach (var d in Directory.EnumerateDirectories(Root))
        {
            var name = Path.GetFileName(d);
            if (keep.Contains(name)) continue;
            if (!AppRelease.SemVerRx().IsMatch(name) && !name.EndsWith(".new", StringComparison.Ordinal)) continue;
            try { Directory.Delete(d, true); removed.Add(name); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        foreach (var f in Directory.EnumerateFiles(Root, "*.zip.part"))
            try { File.Delete(f); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return removed;
    }
}

/// <summary>Что запустить, чтобы перейти на другую версию: exe и аргументы (без оболочки). Окно запускает и закрывается.</summary>
public sealed record RestartPlan(string Exe, string Version, IReadOnlyList<string> Args)
{
    public ProcessStartInfo ToStartInfo()
    {
        var psi = new ProcessStartInfo(Exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(Exe)! };
        foreach (var a in Args) psi.ArgumentList.Add(a);
        return psi;
    }
}

/// <summary>Итог подготовки обновления: версия <paramref name="Version"/> распакована и проверена, или почему нет (Message).</summary>
/// <param name="Details">Технические подробности для «Подробнее» (какой источник, что не совпало); у успеха — null.</param>
public sealed record AppUpdatePrepared(bool Ok, string Message, string? Version = null, string? Details = null);

/// <summary>
/// Обновление программы целиком — для окна: <see cref="CheckAsync"/> (раз в день и по кнопке) → <see cref="PrepareAsync"/>
/// (скачать, сверить, распаковать) → <see cref="RestartInto"/> + <see cref="RestartPlan.ToStartInfo"/> и выход окна; новая копия первым делом ждёт
/// выхода старой (<see cref="WaitForPrevious"/>). Откат — <see cref="Rollback"/>.
/// </summary>
public sealed class AppUpdater(AppUpdateClient client, AppVersionStore store)
{
    /// <summary>Аргумент новой копии: номер процесса старой — дождаться её выхода (одна копия окна).</summary>
    public const string WaitPidArg = "--wait-pid";
    /// <summary>Аргумент новой копии: с какой версии перешли (окно покажет «Обновлено до X», после отката — «Возвращена X»).</summary>
    public const string UpdatedFromArg = "--updated-from";

    public string CurrentVersion { get; init; } = AppRelease.CurrentVersion;
    /// <summary>Exe, который работает сейчас (для отката на него).</summary>
    public string CurrentExe { get; init; } = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, AppVersionStore.ExeName);
    public int CurrentPid { get; init; } = Environment.ProcessId;

    public AppVersionStore Store => store;

    public Task<AppUpdateCheck> CheckAsync(CancellationToken ct = default) => client.CheckAsync(CurrentVersion, ct);

    /// <summary>Сколько свободного места нужно на диске с папкой версий: архив + распаковка (с запасом).</summary>
    public long RequiredFreeBytes { get; init; } = 600L * 1024 * 1024;
    /// <summary>Свободно байт на диске папки; null — не узнать (тогда не проверяем). В тестах — подмена.</summary>
    public Func<string, long?> FreeSpace { get; init; } = DefaultFreeSpace;

    static long? DefaultFreeSpace(string dir)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(dir));
            return root is null ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { return null; }
    }

    static long Mb(long bytes) => (bytes + 1024 * 1024 - 1) / (1024 * 1024);

    /// <summary>
    /// Скачать архив выпуска, сверить sha256, распаковать в versions\X.Y.Z. Уже распакован — без скачивания.
    /// Работающая программа и её папка не меняются ни при каком исходе: неудача оставляет самое большее пустую папку versions.
    /// </summary>
    public async Task<AppUpdatePrepared> PrepareAsync(AppUpdateCheck check, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        if (check.Found is not { } found || !check.Available || found.Release.Portable is not { } asset)
            return new(false, L.T(check.OsTooOld ? "core.upd.os_too_old" : "core.upd.none_for_pc"));
        var version = found.Release.Version;
        try
        {
            if (!store.IsInstalled(version))
            {
                if (FreeSpace(store.Root) is { } free && free < RequiredFreeBytes)
                    return new(false, NoSpace(free));
                var part = Path.Combine(store.Root, asset.Name + ".part");
                try
                {
                    await client.DownloadAsync(found, asset, part, progress, ct);
                    store.Install(part, version);
                }
                finally { try { if (File.Exists(part)) File.Delete(part); } catch (IOException) { } }
            }
            return new(true, L.T("core.upd.ready", new { version }), version);
        }
        catch (SecurityException e)
        {
            return new(false, L.T("core.upd.hash_mismatch"), Details: e.Message);
        }
        catch (InvalidDataException e)
        {
            return new(false, L.T("core.upd.bad_archive"), Details: e.Message);
        }
        catch (HttpRequestException e)
        {
            return new(false, L.T("core.upd.offline"), Details: e.Message);
        }
        catch (IOException e) when (IsDiskFull(e))
        {
            return new(false, NoSpace(FreeSpace(store.Root) ?? 0), Details: e.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new(false, L.T("core.upd.write_failed", new { path = store.Root }), Details: e.Message);
        }
    }

    string NoSpace(long free) =>
        L.T("core.upd.no_space", new { disk = Path.GetPathRoot(Path.GetFullPath(store.Root)), need = Mb(RequiredFreeBytes), free = Mb(free) });

    // ERROR_DISK_FULL (112) и ERROR_HANDLE_DISK_FULL (39) — младшие 16 бит HResult.
    public static bool IsDiskFull(IOException e) => (e.HResult & 0xFFFF) is 112 or 39;

    /// <summary>
    /// Команда перезапуска на распакованную версию; текущая запоминается как прежняя (откат). Окно запускает
    /// <see cref="RestartPlan.ToStartInfo"/> и закрывается.
    /// </summary>
    public RestartPlan RestartInto(string version, bool record = true)
    {
        if (!store.IsInstalled(version)) throw new InvalidOperationException($"версия {version} не распакована");
        if (record) store.RecordSwitch(CurrentVersion, CurrentExe, version, pending: true);
        return new(store.ExeFor(version), version, [WaitPidArg, CurrentPid.ToString(System.Globalization.CultureInfo.InvariantCulture), UpdatedFromArg, CurrentVersion]);
    }

    /// <summary>Есть куда откатываться: прежняя версия записана, это не та, что работает, и её exe на месте.</summary>
    public bool CanRollback => store.State is { Previous: { } p, PreviousExe: { } e } && p != CurrentVersion && File.Exists(e);

    /// <summary>Прежняя версия для подписи кнопки отката; null — откатываться не на что.</summary>
    public string? PreviousVersion => CanRollback ? store.State.Previous : null;

    /// <summary>Откат на прежнюю версию; null — откатываться не на что (её exe нет).</summary>
    public RestartPlan? Rollback()
    {
        var st = store.State;
        if (st.Previous is not { } prev || st.PreviousExe is not { } exe || !File.Exists(exe)) return null;
        // После отката прежняя — та, с которой уходим: можно вернуться обратно.
        store.RecordSwitch(CurrentVersion, CurrentExe, prev);
        return new(exe, prev, [WaitPidArg, CurrentPid.ToString(System.Globalization.CultureInfo.InvariantCulture), UpdatedFromArg, CurrentVersion]);
    }

    /// <summary>
    /// Первым делом при запуске переключённой версии (после захвата «одной копии»). Новая версия, которая уже стартовала и не
    /// подтвердила запуск (<see cref="ConfirmStarted"/>), на втором старте возвращает прежнюю: результат — команда запуска прежней
    /// (окно её запускает и выходит), версия-неудачница запоминается для сообщения (<see cref="TakeFailedNotice"/>). Нет — null.
    /// </summary>
    public RestartPlan? CheckStartup()
    {
        var st = store.State;
        if (st.Pending != CurrentVersion) return null;
        if (st.PendingStarts < 1)
        {
            store.Save(st with { PendingStarts = 1 });
            return null;
        }
        if (st.Previous is { } prev && st.PreviousExe is { } exe && prev != CurrentVersion && File.Exists(exe))
        {
            // Прежняя становится текущей; вернуться на неудачную кнопкой не предлагаем.
            store.Save(new(prev, null, null, Failed: CurrentVersion));
            return new(exe, prev, [WaitPidArg, CurrentPid.ToString(System.Globalization.CultureInfo.InvariantCulture), UpdatedFromArg, CurrentVersion]);
        }
        store.Save(st with { Pending = null, PendingStarts = 0 });   // откатываться некуда — не зацикливаться
        return null;
    }

    /// <summary>Окно показалось и работает: версия подтверждена, откат при следующем старте не нужен.</summary>
    public void ConfirmStarted()
    {
        var st = store.State;
        if (st.Pending == CurrentVersion) store.Save(st with { Pending = null, PendingStarts = 0 });
    }

    /// <summary>Версия, которая не запустилась и была заменена прежней, — один раз; null — такого не было.</summary>
    public string? TakeFailedNotice()
    {
        var st = store.State;
        if (st.Failed is not { } f) return null;
        store.Save(st with { Failed = null });
        return f;
    }

    /// <summary>
    /// Для запуска новой копии: есть <c>--wait-pid N</c> — подождать выхода процесса N (до <paramref name="timeout"/>),
    /// иначе проверка «одна копия окна» увидит старую. Возвращает версию из <c>--updated-from</c> (null — обычный запуск).
    /// </summary>
    public static string? WaitForPrevious(IReadOnlyList<string> args, TimeSpan timeout)
    {
        string? from = null;
        for (int i = 0; i + 1 < args.Count; i++)
        {
            if (args[i] == UpdatedFromArg && AppRelease.SemVerRx().IsMatch(args[i + 1])) from = args[i + 1];
            if (args[i] == WaitPidArg && int.TryParse(args[i + 1], out var pid) && pid != Environment.ProcessId)
            {
                try { using var p = Process.GetProcessById(pid); p.WaitForExit(timeout); }
                catch (ArgumentException) { }          // уже вышел
                catch (InvalidOperationException) { }
            }
        }
        return from;
    }
}

/// <summary>Настройка автопроверки обновлений программы и когда проверяли (окно хранит у пользователя).</summary>
/// <param name="AutoCheck">Проверять раз в день (по умолчанию — да).</param>
/// <param name="SkippedVersion">«Пропустить эту версию» — не напоминать о ней.</param>
public sealed record AppUpdateSettings(bool AutoCheck = true, DateTimeOffset? LastCheck = null, string? LatestVersion = null, string? SkippedVersion = null)
{
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    /// <summary>Пора проверять: автопроверка включена и с прошлой прошли сутки (часы ушли назад — тоже пора).</summary>
    public bool IsDue(DateTimeOffset now) => AutoCheck && (LastCheck is not { } last || now - last >= Interval || last > now);

    /// <summary>Запомнить проверку (версию — только если источник ответил).</summary>
    public AppUpdateSettings Checked(DateTimeOffset now, string? latest) => this with { LastCheck = now, LatestVersion = latest ?? LatestVersion };

    /// <summary>Напомнить о версии: новее текущей и не пропущена.</summary>
    public bool ShouldOffer(string version, string current) => AppRelease.IsOlder(current, version) && version != SkippedVersion;
}

/// <summary>Хранилище настройки автопроверки (окно подключит своё или файловое).</summary>
public interface IAppUpdateSettingsStore
{
    AppUpdateSettings Load();
    void Save(AppUpdateSettings settings);
}

/// <summary>Файл <c>%LocalAppData%\DpiShell\app-update.json</c> — общий для всех версий (переживает обновление и откат).</summary>
public sealed class AppUpdateSettingsFile(string? path = null) : IAppUpdateSettingsStore
{
    public string Path { get; } = path ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DpiShell", "app-update.json");

    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };

    public AppUpdateSettings Load()
    {
        try { return File.Exists(Path) ? JsonSerializer.Deserialize<AppUpdateSettings>(File.ReadAllText(Path), Json) ?? new() : new(); }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { return new(); }
    }

    public void Save(AppUpdateSettings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
        File.Move(tmp, Path, overwrite: true);
    }
}
