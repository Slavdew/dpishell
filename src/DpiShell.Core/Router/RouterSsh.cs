using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DpiShell.Core.Router;

/// <summary>
/// Сценарии по SSH (router-api.md §5.5): предпроверка, установка, удаление без API, загрузка ядра и пакетов обновления.
/// Сценарий уходит через stdin (<c>sh -s -- …</c>) и на роутере не остаётся; тексты — ресурсы сборки из router/ (те же файлы).
/// </summary>
public sealed partial class RouterSsh(ISshSession ssh)
{
    public const string PreflightScript = "dpishell-preflight.sh";
    public const string InstallScript = "dpishell-install.sh";
    public const string UninstallScript = "uninstall.sh";

    public const string CoreDir = "/tmp/dpishell/core";
    public const string CoreZip = "/tmp/dpishell/core/zapret.zip";
    public const string UpdateDir = "/tmp/dpishell-update";
    public const string UninstallResultPath = "/tmp/dpishell-uninstall.json";

    public ISshSession Session => ssh;

    /// <summary>Текст сценария из ресурсов (LF — sh на роутере CR не переносит).</summary>
    public static string Script(string name)
    {
        using var s = typeof(RouterSsh).Assembly.GetManifestResourceStream("DpiShell.Router." + name)
                      ?? throw new InvalidOperationException($"В сборке нет сценария {name}");
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd().Replace("\r\n", "\n");
    }

    /// <summary>Переменные прокси для команды: через туннель к прокси ПК (ssh -R) ходят apk и curl.</summary>
    public static string ProxyEnv(int? proxyPort) =>
        proxyPort is { } p ? $"https_proxy=http://127.0.0.1:{p} http_proxy=http://127.0.0.1:{p} " : "";

    static MemoryStream ScriptStream(string name) => new(Encoding.UTF8.GetBytes(Script(name)));

    async Task<SshRunResult> RunScriptAsync(string name, string args, int? proxyPort, Action<string>? onLine, CancellationToken ct)
    {
        using var stdin = ScriptStream(name);
        var cmd = ProxyEnv(proxyPort) + "sh -s" + (args.Length > 0 ? " -- " + args : "");
        return await ssh.RunAsync(cmd, stdin, onLine, ct);
    }

    // ---------- предпроверка ----------

    /// <param name="updateIndexes">--update: сначала обновить индексы пакетов (через туннель — с proxyPort).</param>
    public async Task<RouterPreflight> PreflightAsync(bool updateIndexes = false, int? proxyPort = null, CancellationToken ct = default)
    {
        var r = await RunScriptAsync(PreflightScript, updateIndexes ? "--update" : "", proxyPort, null, ct);
        return RouterScriptJson.ParsePreflight(r.Stdout);
    }

    public async Task<RouterAfterUninstall> AfterUninstallAsync(CancellationToken ct = default)
    {
        var r = await RunScriptAsync(PreflightScript, "--after-uninstall", null, null, ct);
        return RouterScriptJson.ParseAfterUninstall(r.Stdout);
    }

    // ---------- установка ----------

    /// <summary>
    /// dpishell-install.sh: строки «# …» — в progress, последняя строка — итог. Архив ядра — заранее <see cref="UploadCoreAsync"/>:
    /// через туннель 8 МБ с GitHub не успевают.
    /// </summary>
    public async Task<RouterInstallResult> InstallAsync(int? proxyPort = null, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var r = await RunScriptAsync(InstallScript, "", proxyPort, line =>
        {
            if (RouterScriptJson.ProgressLine(line) is { } p) progress?.Report(p);
        }, ct);
        return RouterScriptJson.ParseInstall(r.Stdout);
    }

    // ---------- удаление без API ----------

    /// <summary>uninstall.sh --keep | --full через stdin (работает и без пакета); итог — /tmp/dpishell-uninstall.json.</summary>
    public async Task<RouterUninstallResult> UninstallAsync(bool keepSettings, CancellationToken ct = default)
    {
        var run = await RunScriptAsync(UninstallScript, keepSettings ? "--keep" : "--full", null, null, ct);
        return await ReadUninstallResultAsync(ct)
               ?? throw new RouterSshException(RouterSshFailure.Failed, "Удаление не оставило итога" + (run.Stderr.Length > 0 ? ": " + run.Stderr.Trim() : ""));
    }

    /// <summary>Ход/итог удаления (и после <c>uninstall</c> через API — объект dpishell к концу исчезает). null — файла нет.</summary>
    public async Task<RouterUninstallResult?> ReadUninstallResultAsync(CancellationToken ct = default)
    {
        var r = await ssh.RunAsync($"cat {UninstallResultPath} 2>/dev/null", null, null, ct);
        if (RouterScriptJson.LastJsonLine(r.Stdout) is null) return null;
        return RouterScriptJson.Parse<RouterUninstallResult>(r.Stdout, "Удаление");
    }

    // ---------- фиды роутера (для белого списка прокси) ----------

    const string FeedFiles = "/etc/apk/repositories.d/*.list /etc/apk/repositories /etc/opkg/distfeeds.conf /etc/opkg/customfeeds.conf";

    /// <summary>Имена серверов из distfeeds/customfeeds роутера — туда прокси ПК пустит apk.</summary>
    public async Task<IReadOnlyList<string>> FeedHostsAsync(CancellationToken ct = default)
    {
        var r = await ssh.RunAsync($"cat {FeedFiles} 2>/dev/null", null, null, ct);
        return ParseFeedHosts(r.Stdout);
    }

    /// <summary>Строки «https://host/…» (apk) и «src/gz имя https://host/…» (opkg); комментарии пропускаются.</summary>
    public static IReadOnlyList<string> ParseFeedHosts(string text)
    {
        var hosts = new List<string>();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            foreach (Match m in FeedUrl().Matches(line))
            {
                var h = m.Groups[1].Value.ToLowerInvariant();
                if (!hosts.Contains(h)) hosts.Add(h);
            }
        }
        return hosts;
    }

    [GeneratedRegex(@"https?://([A-Za-z0-9.-]+)(?::\d+)?(?:/|\s|$)")]
    private static partial Regex FeedUrl();

    // ---------- загрузка файлов ----------

    /// <summary>sha256 файла на роутере (sha256sum); null — файла нет.</summary>
    public async Task<string?> RemoteSha256Async(string path, CancellationToken ct = default)
    {
        var r = await ssh.RunAsync($"sha256sum {Sh.Quote(path)} 2>/dev/null", null, null, ct);
        var h = r.Stdout.Trim();
        return h.Length >= 64 && h[..64].All(Uri.IsHexDigit) ? h[..64].ToLowerInvariant() : null;
    }

    async Task RunOkAsync(string command, string what, CancellationToken ct)
    {
        var r = await ssh.RunAsync(command, null, null, ct);
        if (r.ExitCode != 0) throw new RouterSshException(RouterSshFailure.Failed, $"{what}: {r.Stderr.Trim()}");
    }

    /// <summary>Положить файл и сверить sha256 на самом роутере; не совпал — файл удаляется, исключение.</summary>
    public async Task UploadVerifiedAsync(Stream content, string remotePath, string sha256, CancellationToken ct = default)
    {
        await ssh.UploadAsync(content, remotePath, ct);
        var got = await RemoteSha256Async(remotePath, ct);
        if (!string.Equals(got, sha256, StringComparison.OrdinalIgnoreCase))
        {
            await ssh.RunAsync($"rm -f {Sh.Quote(remotePath)}", null, null, ct);
            throw new RouterSshException(RouterSshFailure.Failed, $"Файл {Path.GetFileName(remotePath)} на роутере не совпал по sha256 — загрузка отменена");
        }
    }

    /// <summary>
    /// Архив ядра zapret (уже проверенный по core.json — <see cref="RouterCoreArchive"/>) → /tmp/dpishell/core/zapret.zip, папка 700.
    /// core_install роутера возьмёт его без сети. Класть до установки.
    /// </summary>
    public async Task UploadCoreAsync(Stream zip, string sha256, CancellationToken ct = default)
    {
        await RunOkAsync($"mkdir -p {CoreDir} && chmod 700 /tmp/dpishell {CoreDir}", "Не удалось создать папку ядра на роутере", ct);
        if (await RemoteSha256Async(CoreZip, ct) is { } have && have.Equals(sha256, StringComparison.OrdinalIgnoreCase)) return;
        await UploadVerifiedAsync(zip, CoreZip, sha256, ct);
    }

    /// <summary>Имена пакетов, которые роутер примет из папки обновления (как local_files в update.uc).</summary>
    [GeneratedRegex(@"^(dpishell|luci-app-dpishell)-[0-9][A-Za-z0-9._~+-]*\.(apk|ipk)$")]
    public static partial Regex UpdatePackageName();

    /// <summary>
    /// Пакеты для <c>update_install { from: "local" }</c>: папка /tmp/dpishell-update очищается, файлы проверяются по sha256 здесь,
    /// кладутся по SSH и сверяются ещё раз на роутере; SHA256SUMS пишется последним.
    /// </summary>
    public async Task UploadUpdateAsync(IReadOnlyList<RouterUpdateFile> files, CancellationToken ct = default)
    {
        if (files.Count == 0) throw new ArgumentException("Нет пакетов обновления");
        foreach (var f in files)
        {
            if (!UpdatePackageName().IsMatch(f.Name)) throw new ArgumentException($"Не пакет DpiShell: {f.Name}");
            if (Sha256File(f.LocalPath) is var h && !h.Equals(f.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new SecurityException($"Пакет {f.Name} не совпал по sha256 — не загружаю");
        }
        await RunOkAsync($"rm -rf {UpdateDir} && mkdir -p {UpdateDir} && chmod 700 {UpdateDir}", "Не удалось подготовить папку обновления на роутере", ct);
        var sums = new StringBuilder();
        foreach (var f in files)
        {
            await using var s = File.OpenRead(f.LocalPath);
            await UploadVerifiedAsync(s, $"{UpdateDir}/{f.Name}", f.Sha256, ct);
            sums.Append(f.Sha256.ToLowerInvariant()).Append("  ").Append(f.Name).Append('\n');
        }
        using var sumsStream = new MemoryStream(Encoding.UTF8.GetBytes(sums.ToString()));
        await ssh.UploadAsync(sumsStream, $"{UpdateDir}/SHA256SUMS", ct);
    }

    static string Sha256File(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }
}

/// <summary>Пакет обновления на ПК: имя как в релизе, путь, sha256 из release.json.</summary>
public sealed record RouterUpdateFile(string Name, string LocalPath, string Sha256);
