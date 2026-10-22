using DpiShell.Core.Localization;
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
        try { r = JsonSerializer.Deserialize<RouterReleaseInfo>(bytes, RouterScriptJson.Json) ?? throw new InvalidDataException(L.T("core.app.rel.empty")); }
        catch (JsonException e) { throw new InvalidDataException(L.T("core.rt.upd.rel_unreadable", new { error = e.Message })); }
        if (r.Schema != 1) throw new InvalidDataException(L.T("core.app.rel.schema", new { schema = r.Schema }));
        if (string.IsNullOrEmpty(r.Version) || r.Tag != RouterUpdatePackages.TagPrefix + r.Version) throw new InvalidDataException(L.T("core.app.rel.tag", new { tag = r.Tag, version = r.Version }));
        foreach (var a in r.Assets ?? [])
            if (a.Name is null || a.Sha256 is not { Length: 64 } h || !h.All(Uri.IsHexDigit)) throw new InvalidDataException(L.T("core.app.rel.bad_asset"));
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

    public const string ReleaseJsonName = "release.json";

    /// <summary>release.json и release.json.sig, скачанные <see cref="DownloadAsync"/> в <paramref name="dir"/>; null — их там нет.</summary>
    public static RouterReleaseFiles? ReleaseFilesIn(string dir)
    {
        var json = Path.Combine(dir, ReleaseJsonName);
        var sig = json + ReleaseSignature.Extension;
        return File.Exists(json) && File.Exists(sig) ? new(json, sig) : null;
    }

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
            throw new SecurityException(L.T("core.rt.upd.bad_signature"));
        RouterReleaseInfo info;
        try { info = RouterReleaseInfo.Parse(json); }
        catch (InvalidDataException e) { throw new SecurityException(L.T("core.rt.upd.bad_format", new { error = e.Message })); }
        if (info.Version != BaseVersion(version)) throw new SecurityException(L.T("core.rt.upd.wrong_release", new { got = info.Version, want = BaseVersion(version) }));

        var ext = "." + (info.Pkg ?? "apk");
        var assets = info.Assets.Where(a => RouterSsh.UpdatePackageName().IsMatch(a.Name) && a.Name.EndsWith(ext, StringComparison.Ordinal)).ToList();
        if (assets.Count == 0) throw new SecurityException(L.T("core.rt.upd.no_packages", new { version = info.Version }));

        Directory.CreateDirectory(dir);
        // Проверенные release.json и подпись — рядом с пакетами: роутер с local_signed проверяет их сам (ReleaseFilesIn).
        await File.WriteAllBytesAsync(Path.Combine(dir, ReleaseJsonName), json, ct);
        await File.WriteAllBytesAsync(Path.Combine(dir, ReleaseJsonName + ReleaseSignature.Extension), System.Text.Encoding.ASCII.GetBytes(sig), ct);
        var baseUrl = url[..(url.LastIndexOf('/') + 1)];
        var files = new List<RouterUpdateFile>();
        foreach (var a in assets)
        {
            var path = Path.Combine(dir, a.Name);
            var bytes = await GetBytesAsync(http, baseUrl + a.Name, 64 * 1024 * 1024, ct);
            if (!Convert.ToHexStringLower(SHA256.HashData(bytes)).Equals(a.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new SecurityException(L.T("core.rt.upd.pkg_hash", new { name = a.Name }));
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
            if (ms.Length + n > max) throw new HttpRequestException(L.T("core.rt.upd.too_big", new { url }));
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }
}
