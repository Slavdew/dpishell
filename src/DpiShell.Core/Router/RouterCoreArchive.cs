using System.Security.Cryptography;
using System.Text.Json;

namespace DpiShell.Core.Router;

/// <summary>Ядро для роутера — router/dpishell/files/usr/share/dpishell/core.json (встроен в сборку): тот же zip, что у Windows-версии.</summary>
public sealed record RouterCoreManifest(string Version, string ZipUrl, string ZipSha256, string ZipDir,
    Dictionary<string, string> Nfqws, List<string>? MirrorUrls = null, List<string>? Extract = null)
{
    public static RouterCoreManifest Parse(string json) =>
        JsonSerializer.Deserialize<RouterCoreManifest>(json, RouterScriptJson.Json) ?? throw new InvalidDataException("Пустой core.json роутера");

    /// <summary>Манифест, с которым собрано приложение (тот же файл, что уходит в пакет роутера).</summary>
    public static RouterCoreManifest Embedded()
    {
        using var s = typeof(RouterCoreManifest).Assembly.GetManifestResourceStream("DpiShell.Router.core.json")
                      ?? throw new InvalidOperationException("В сборке нет core.json роутера");
        using var r = new StreamReader(s);
        return Parse(r.ReadToEnd());
    }

    /// <summary>Имя файла архива («zapret-v72.13.zip»).</summary>
    public string ZipName => ZipUrl[(ZipUrl.LastIndexOf('/') + 1)..];
}

/// <summary>
/// Архив ядра для роутера готовит окно: скачивает само (у роутера через туннель 8 МБ с GitHub не успевают), сверяет sha256 по core.json
/// и держит в кэше; на роутер кладёт <see cref="RouterSsh.UploadCoreAsync"/>.
/// </summary>
public sealed class RouterCoreArchive(RouterCoreManifest manifest, string cacheDir)
{
    public RouterCoreManifest Manifest => manifest;
    public string CachePath => Path.Combine(cacheDir, manifest.ZipName);

    public bool IsValid(string path)
    {
        if (!File.Exists(path)) return false;
        using var s = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(s)).Equals(manifest.ZipSha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Путь к проверенному архиву: кэш → готовые копии (localCandidates, напр. уже скачанный для ПК тот же zip) → загрузка: основной адрес, затем зеркала.
    /// Принимается только файл с хешем из манифеста; иначе — исключение, кэш не трогается.
    /// </summary>
    public async Task<string> EnsureAsync(HttpClient http, IEnumerable<string>? localCandidates = null, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (IsValid(CachePath)) return CachePath;
        Directory.CreateDirectory(cacheDir);
        var tmp = CachePath + ".part";
        foreach (var c in localCandidates ?? [])
        {
            if (!IsValid(c)) continue;
            File.Copy(c, tmp, true);
            File.Move(tmp, CachePath, true);
            return CachePath;
        }
        var errors = new List<string>();
        foreach (var url in new[] { manifest.ZipUrl }.Concat(manifest.MirrorUrls ?? []))
        {
            progress?.Report($"Загрузка ядра: {url}");
            try { await CoreInstaller.DownloadWithRetriesAsync(http, url, tmp, ct); }
            catch (HttpRequestException e) { errors.Add(e.Message); continue; }
            if (IsValid(tmp)) { File.Move(tmp, CachePath, true); return CachePath; }
            File.Delete(tmp);
            errors.Add($"{url}: хеш не совпал");
        }
        throw new HttpRequestException($"Не удалось скачать ядро для роутера ({manifest.ZipName}): {string.Join("; ", errors)}");
    }
}
