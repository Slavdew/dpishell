using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace DpiShell.Core;

/// <summary>Закреплённая версия ядра: откуда качать и какие хеши обязаны совпасть (data/core.json).</summary>
public sealed record CoreManifest(string Version, string ZipUrl, string ZipSha256, string ZipDir, Dictionary<string, string> Files)
{
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static CoreManifest Load(string path) =>
        JsonSerializer.Deserialize<CoreManifest>(File.ReadAllText(path), Json)
        ?? throw new InvalidDataException($"Пустой манифест ядра: {path}");
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
            log?.Report($"Скачиваю ядро {manifest.Version}…");
            await DownloadWithRetriesAsync(http, manifest.ZipUrl, zip, ct);

            var zipHash = Sha256(zip);
            if (!string.Equals(zipHash, manifest.ZipSha256, StringComparison.OrdinalIgnoreCase))
                throw new SecurityException($"Хеш архива ядра не совпал: ожидался {manifest.ZipSha256}, получен {zipHash}. Файл удалён.");

            var staged = Path.Combine(tmp, "core");
            Directory.CreateDirectory(staged);
            using (var archive = ZipFile.OpenRead(zip))
            {
                foreach (var (name, hash) in manifest.Files)
                {
                    var entry = archive.GetEntry(manifest.ZipDir + name)
                        ?? throw new SecurityException($"В архиве нет {manifest.ZipDir}{name}");
                    var target = Path.Combine(staged, name);
                    entry.ExtractToFile(target);
                    var h = Sha256(target);
                    if (!string.Equals(h, hash, StringComparison.OrdinalIgnoreCase))
                        throw new SecurityException($"Хеш {name} не совпал с манифестом.");
                }
            }

            var backup = paths.Core + ".old";
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
            if (Directory.Exists(paths.Core)) Directory.Move(paths.Core, backup);
            try { Directory.Move(staged, paths.Core); }
            catch { if (Directory.Exists(backup)) Directory.Move(backup, paths.Core); throw; }
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
            log?.Report($"Ядро {manifest.Version} установлено и проверено.");
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        }
    }

    // github.com и его файловый хостинг сами бывают под DPI: обрывы TLS лечатся повтором.
    static async Task DownloadWithRetriesAsync(HttpClient http, string url, string file, CancellationToken ct)
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
            catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                last = e;
                await Task.Delay(1500 * (i + 1), ct);
            }
        }
        throw new HttpRequestException($"Не удалось скачать {url}: {last?.Message}", last);
    }
}

public sealed class SecurityException(string message) : Exception(message);
