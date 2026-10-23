using DpiShell.Core.Localization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace DpiShell.Core;

/// <summary>Закреплённая версия ядра: откуда качать и какие хеши обязаны совпасть (data/core.json).</summary>
/// <param name="MirrorUrls">Запасные адреса того же архива: пробуются, только если основной недоступен или отдал не тот файл. Подменить ядро через них нельзя — хеш тот же.</param>
public sealed record CoreManifest(string Version, string ZipUrl, string ZipSha256, string ZipDir, Dictionary<string, string> Files, List<string>? MirrorUrls = null)
{
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static CoreManifest Load(string path) =>
        JsonSerializer.Deserialize<CoreManifest>(File.ReadAllText(path), Json)
        ?? throw new InvalidDataException(L.T("core.core.manifest_empty", new { path }));
}

public enum CoreState { Ok, Missing, Modified }

public sealed record CoreCheck(CoreState State, IReadOnlyList<string> BadFiles, IReadOnlyList<string> ExtraFiles);

/// <summary>Ставит winws + WinDivert из релиза bol-van строго по хешам манифеста.</summary>
public sealed class CoreInstaller(AppPaths paths, CoreManifest manifest)
{
    public CoreManifest Manifest => manifest;

    /// <summary>Файл записан (архив скачан, файл ядра распакован) — в тестах здесь «антивирус» удаляет или закрывает его.</summary>
    public Action<string>? Written { get; set; }

    public static string Sha256(string file)
    {
        using var s = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }

    public CoreCheck Check()
    {
        if (!Directory.Exists(paths.Core))
            return new(CoreState.Missing, manifest.Files.Keys.ToList(), []);
        var bad = new List<string>();
        bool anyPresent = false;
        foreach (var (name, hash) in manifest.Files)
        {
            var f = Path.Combine(paths.Core, name);
            if (!File.Exists(f)) { bad.Add(name); continue; }
            anyPresent = true;
            if (!string.Equals(Sha256(f), hash, StringComparison.OrdinalIgnoreCase)) bad.Add(name);
        }
        // Лишняя cygwin1.dll или чужой exe рядом с winws ломают его или подменяют поведение.
        var extra = Directory.GetFiles(paths.Core).Select(Path.GetFileName)
            .Where(n => n is not null && !manifest.Files.ContainsKey(n)).Select(n => n!).ToList();
        if (bad.Count == 0) return new(CoreState.Ok, bad, extra);
        return new(anyPresent ? CoreState.Modified : CoreState.Missing, bad, extra);
    }

    /// <summary>Скачать zip, сверить хеш zip и каждого файла, разложить атомарно. Любое несовпадение — исключение, старое ядро не трогается.</summary>
    public async Task InstallAsync(HttpClient http, IProgress<string>? log = null, CancellationToken ct = default)
    {
        paths.EnsureDirectories();
        var tmp = Path.Combine(paths.Root, "core.download");
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        Directory.CreateDirectory(tmp);
        try
        {
            var zip = Path.Combine(tmp, "core.zip");
            Exception? failure = null;
            foreach (var url in new[] { manifest.ZipUrl }.Concat(manifest.MirrorUrls ?? []))
            {
                log?.Report(url == manifest.ZipUrl ? L.T("core.core.downloading", new { version = manifest.Version }) : L.T("core.core.fallback", new { url }));
                try { await DownloadWithRetriesAsync(http, url, zip, ct); }
                catch (HttpRequestException e) { failure = e; continue; }
                // Скачанный архив пропал или закрыт до сверки — антивирус (дополнение 2): не качать заново по кругу, сказать прямо.
                string zipHash;
                try { Written?.Invoke(zip); zipHash = Sha256(zip); }
                catch (Exception e) when (AvBlock.IsVanished(e)) { throw new AvBlockedException(L.T("core.av.what.zip"), zip, e); }
                if (string.Equals(zipHash, manifest.ZipSha256, StringComparison.OrdinalIgnoreCase)) { failure = null; break; }
                File.Delete(zip);
                failure = new SecurityException(L.T("core.core.zip_hash", new { url, expected = manifest.ZipSha256, actual = zipHash }));
            }
            if (failure is not null) throw failure;
            InstallVerifiedZip(zip, tmp, log);
        }
        finally { DeleteQuietly(tmp); }
    }

    /// <summary>Временную папку держит антивирус — не прятать за этим настоящую причину неудачи.</summary>
    static void DeleteQuietly(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Предел офлайн-архива ядра (окно передаёт его помощнику частями): релиз bol-van — около 8 МБ.</summary>
    public const long MaxOfflineZipBytes = 64L * 1024 * 1024;

    /// <summary>Куда помощник собирает присланный окном архив (закрытая папка программы; окно сюда не пишет).</summary>
    public string OfflineDir => Path.Combine(paths.Root, "core.offline");
    public string OfflineZip => Path.Combine(OfflineDir, "core.zip");

    /// <summary>
    /// Часть офлайн-архива от окна: <paramref name="first"/> — начать заново. Возвращает размер собранного. Больше
    /// <see cref="MaxOfflineZipBytes"/> — отказ и файл стирается. Это только байты в закрытой папке: ставится лишь то, что сойдётся с core.json.
    /// </summary>
    public long AppendOffline(ReadOnlySpan<byte> chunk, bool first)
    {
        paths.EnsureDirectories();
        if (first && Directory.Exists(OfflineDir)) Directory.Delete(OfflineDir, true);
        Directory.CreateDirectory(OfflineDir);
        if (!first && !File.Exists(OfflineZip)) throw new InvalidOperationException(L.T("core.core.offline_missing"));
        using (var fs = new FileStream(OfflineZip, first ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.None))
        {
            if (fs.Length + chunk.Length > MaxOfflineZipBytes)
            {
                fs.Dispose();
                Directory.Delete(OfflineDir, true);
                throw new InvalidOperationException(L.T("core.core.offline_too_big", new { mb = MaxOfflineZipBytes / 1024 / 1024 }));
            }
            fs.Write(chunk);
            return fs.Length;
        }
    }

    /// <summary>
    /// Поставить ядро из присланного архива (<see cref="AppendOffline"/>): тот же sha256 архива из манифеста и те же проверки каждого файла,
    /// что у скачанного. Архив стирается в любом случае.
    /// </summary>
    public void InstallOffline(IProgress<string>? log = null)
    {
        try
        {
            if (!File.Exists(OfflineZip)) throw new InvalidOperationException(L.T("core.core.offline_missing"));
            string hash;
            try { hash = Sha256(OfflineZip); }
            catch (Exception e) when (AvBlock.IsVanished(e)) { throw new AvBlockedException(L.T("core.av.what.zip"), OfflineZip, e); }
            if (!string.Equals(hash, manifest.ZipSha256, StringComparison.OrdinalIgnoreCase))
                throw new SecurityException(L.T("core.core.offline_hash", new { version = manifest.Version }));
            log?.Report(L.T("core.core.offline_ok", new { version = manifest.Version }));
            InstallVerifiedZip(OfflineZip, OfflineDir, log);
        }
        finally { DeleteQuietly(OfflineDir); }
    }

    /// <summary>Архив уже сверен с манифестом: извлечь только перечисленные файлы во временную папку, сверить каждый и заменить ядро атомарно.</summary>
    void InstallVerifiedZip(string zip, string tmp, IProgress<string>? log)
    {
        {
            var staged = Path.Combine(tmp, "core");
            if (Directory.Exists(staged)) Directory.Delete(staged, true);
            Directory.CreateDirectory(staged);
            ZipArchive archive;
            try { archive = ZipFile.OpenRead(zip); }
            catch (Exception e) when (AvBlock.IsVanished(e)) { throw new AvBlockedException(L.T("core.av.what.zip"), zip, e); }
            using (archive)
            {
                foreach (var (name, hash) in manifest.Files)
                {
                    var entry = archive.GetEntry(manifest.ZipDir + name)
                        ?? throw new SecurityException(L.T("core.core.entry_missing", new { file = manifest.ZipDir + name }));
                    var target = Path.Combine(staged, name);
                    string h;
                    // Распакованный файл удалили или закрыли сразу (обычно WinDivert64.sys или winws.exe) — антивирус, а не битый архив.
                    try { entry.ExtractToFile(target); Written?.Invoke(target); h = Sha256(target); }
                    catch (Exception e) when (AvBlock.IsVanished(e)) { throw new AvBlockedException(L.T("core.av.what.file", new { name }), target, e); }
                    if (!string.Equals(h, hash, StringComparison.OrdinalIgnoreCase))
                        throw new SecurityException(L.T("core.core.file_hash", new { name }));
                }
            }

            var backup = paths.Core + ".old";
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
            if (Directory.Exists(paths.Core)) Directory.Move(paths.Core, backup);
            try { Directory.Move(staged, paths.Core); }
            catch { if (Directory.Exists(backup)) Directory.Move(backup, paths.Core); throw; }
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
            // Файлы на месте и сходятся — иначе их тронули сразу после установки (антивирус), а не «установлено».
            if (Check() is { State: not CoreState.Ok, BadFiles: [var first, ..] })
                throw new AvBlockedException(L.T("core.av.what.file", new { name = first }), Path.Combine(paths.Core, first));
            log?.Report(L.T("core.core.installed", new { version = manifest.Version }));
        }
    }

    // github.com и его файловый хостинг сами бывают под DPI: обрывы TLS лечатся повтором.
    internal static async Task DownloadWithRetriesAsync(HttpClient http, string url, string file, CancellationToken ct)
    {
        Exception? last = null;
        for (int i = 0; i < 6; i++)
        {
            try
            {
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(file);
                await resp.Content.CopyToAsync(fs, ct);
                return;
            }
            // Файл закрыл антивирус (225/226) — повтор скачал бы его снова и снова: сразу понятная причина.
            catch (Exception e) when (AvBlock.IsVirusError(e) || e is UnauthorizedAccessException)
            {
                throw new AvBlockedException(L.T("core.av.what.zip"), file, e);
            }
            // 4xx (файла нет) повтором не лечится — сразу к запасному адресу.
            catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested
                                      && !(e is HttpRequestException { StatusCode: { } sc } && (int)sc is >= 400 and < 500))
            {
                last = e;
                await Task.Delay(1500 * (i + 1), ct);
            }
        }
        throw new HttpRequestException(L.T("core.core.download_failed", new { url, error = last?.Message }), last);
    }
}

public sealed class SecurityException(string message) : Exception(message);
