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
                var zipHash = Sha256(zip);
                if (string.Equals(zipHash, manifest.ZipSha256, StringComparison.OrdinalIgnoreCase)) { failure = null; break; }
                File.Delete(zip);
                failure = new SecurityException(L.T("core.core.zip_hash", new { url, expected = manifest.ZipSha256, actual = zipHash }));
            }
            if (failure is not null) throw failure;

            var staged = Path.Combine(tmp, "core");
            Directory.CreateDirectory(staged);
            using (var archive = ZipFile.OpenRead(zip))
            {
                foreach (var (name, hash) in manifest.Files)
                {
                    var entry = archive.GetEntry(manifest.ZipDir + name)
                        ?? throw new SecurityException(L.T("core.core.entry_missing", new { file = manifest.ZipDir + name }));
                    var target = Path.Combine(staged, name);
                    entry.ExtractToFile(target);
                    var h = Sha256(target);
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
            log?.Report(L.T("core.core.installed", new { version = manifest.Version }));
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
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
