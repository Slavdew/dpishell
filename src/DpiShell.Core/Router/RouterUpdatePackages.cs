using System.Security.Cryptography;
using System.Text.Json;

namespace DpiShell.Core.Router;

/// <summary>Подписанный release.json выпуска для роутера (router-api §5.4): версия, тег, пакетный менеджер, что нового, файлы с sha256.</summary>
public sealed record RouterReleaseInfo(int Schema, string Version, string Tag, string? Api, string? MinApp, string? Pkg, List<string>? Changes, List<AppReleaseAsset> Assets)
{
    public const int MaxBytes = 64 * 1024;

    public static RouterReleaseInfo Parse(byte[] bytes)
    {
        RouterReleaseInfo r;
        try { r = JsonSerializer.Deserialize<RouterReleaseInfo>(bytes, RouterScriptJson.Json) ?? throw new InvalidDataException("пустой release.json"); }
        catch (JsonException e) { throw new InvalidDataException("release.json роутера не читается: " + e.Message); }
        if (r.Schema != 1) throw new InvalidDataException($"неизвестная схема release.json ({r.Schema})");
        if (string.IsNullOrEmpty(r.Version) || r.Tag != RouterUpdatePackages.TagPrefix + r.Version) throw new InvalidDataException($"тег {r.Tag} не совпадает с версией {r.Version}");
        foreach (var a in r.Assets ?? [])
            if (a.Name is null || a.Sha256 is not { Length: 64 } h || !h.All(Uri.IsHexDigit)) throw new InvalidDataException("файл выпуска с негодным именем или хешем");
        return r with { Assets = r.Assets ?? [] };
    }
}

/// <summary>
/// Пакеты обновления для роутера через этот ПК (когда у роутера нет связи с витриной): release.json выпуска с подписью ed25519
/// (<see cref="ReleaseSignature"/>), затем только наши пакеты из него — по sha256. Дальше их кладёт <see cref="RouterSsh.UploadUpdateAsync"/>,
/// и роутер ставит <c>update_install { from: "local" }</c>.
/// </summary>
public static class RouterUpdatePackages
{
    public const string TagPrefix = "router-v";
    public const string DownloadBase = "https://github.com/Slavdew/dpishell/releases/download/";

    /// <summary>«0.2.1-r1» → «0.2.1» (номер сборки в тег не входит).</summary>
    public static string BaseVersion(string version) => version.Split('-')[0].Trim();

    /// <summary>Адрес release.json: из data/router-releases.json, если выпуск там есть, иначе — по тегу.</summary>
    public static string ReleaseJsonUrl(RouterReleases? releases, string version)
    {
        var v = BaseVersion(version);
        return releases?.Releases.FirstOrDefault(r => r.Version == v)?.ReleaseJson ?? $"{DownloadBase}{TagPrefix}{v}/release.json";
    }

    /// <summary>
    /// Скачать и проверить пакеты выпуска <paramref name="version"/> в <paramref name="dir"/>. Подпись не та, версия не та, хеш не сошёлся —
    /// <see cref="SecurityException"/>; файлы с ошибкой удаляются.
    /// </summary>
    public static async Task<IReadOnlyList<RouterUpdateFile>> DownloadAsync(HttpClient http, RouterReleases? releases, string version, string dir,
        byte[]? publicKey = null, CancellationToken ct = default)
    {
        var url = ReleaseJsonUrl(releases, version);
        var json = await GetBytesAsync(http, url, RouterReleaseInfo.MaxBytes, ct);
        var sig = System.Text.Encoding.ASCII.GetString(await GetBytesAsync(http, url + ReleaseSignature.Extension, ReleaseSignature.MaxSignatureFileBytes, ct));
        if (!ReleaseSignature.Verify(json, sig, publicKey))
            throw new SecurityException("Подпись выпуска для роутера не сошлась — пакеты не загружаю");
        RouterReleaseInfo info;
        try { info = RouterReleaseInfo.Parse(json); }
        catch (InvalidDataException e) { throw new SecurityException("Описание выпуска для роутера не по формату: " + e.Message); }
        if (info.Version != BaseVersion(version)) throw new SecurityException($"Выпуск {info.Version} вместо {BaseVersion(version)} — пакеты не загружаю");

        var ext = "." + (info.Pkg ?? "apk");
        var assets = info.Assets.Where(a => RouterSsh.UpdatePackageName().IsMatch(a.Name) && a.Name.EndsWith(ext, StringComparison.Ordinal)).ToList();
        if (assets.Count == 0) throw new SecurityException($"В выпуске {info.Version} нет пакетов DpiShell");

        Directory.CreateDirectory(dir);
        var baseUrl = url[..(url.LastIndexOf('/') + 1)];
        var files = new List<RouterUpdateFile>();
        foreach (var a in assets)
        {
            var path = Path.Combine(dir, a.Name);
            var bytes = await GetBytesAsync(http, baseUrl + a.Name, 64 * 1024 * 1024, ct);
            if (!Convert.ToHexStringLower(SHA256.HashData(bytes)).Equals(a.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new SecurityException($"Пакет {a.Name} не совпал по sha256 — не загружаю");
            await File.WriteAllBytesAsync(path, bytes, ct);
            files.Add(new(a.Name, path, a.Sha256.ToLowerInvariant()));
        }
        return files;
    }

    static async Task<byte[]> GetBytesAsync(HttpClient http, string url, int max, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"{url}: HTTP {(int)resp.StatusCode}");
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = await s.ReadAsync(buf, ct)) > 0)
        {
            if (ms.Length + n > max) throw new HttpRequestException($"{url}: файл больше ожидаемого");
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }
}
