namespace DpiShell.Core.Router;

/// <summary>Итог установки: решение предпроверки и (если до неё дошло) итог install.sh.</summary>
public sealed record RouterInstallOutcome(RouterVerdict Verdict, RouterInstallResult? Install)
{
    public bool Ok => Install?.Ok == true;
}

/// <summary>Итог удаления по SSH и проверка «что осталось» (preflight --after-uninstall).</summary>
public sealed record RouterUninstallOutcome(RouterUninstallResult Result, RouterAfterUninstall? Left);

/// <summary>
/// Установка и удаление DpiShell из окна по SSH целиком (architecture.md «Установка, обновление, удаление»):
/// подключение → предпроверка → решение → (туннель через ПК, если роутер без сети) → ядро по SSH → install.sh.
/// </summary>
public sealed class RouterInstallFlow(ISshConnector connector, RouterReleases releases, RouterCoreArchive core, HttpClient http)
{
    /// <summary>Готовые копии архива ядра на ПК (напр. уже скачанный для Windows тот же zip) — проверяются по хешу.</summary>
    public IReadOnlyList<string> LocalCoreCandidates { get; init; } = [];

    /// <summary>Только предпроверка и решение — для «Проверить роутер» до установки (ничего не меняет, кроме индексов пакетов).</summary>
    public async Task<RouterVerdict> CheckAsync(RouterSshTarget target, Func<string> password, CancellationToken ct = default)
    {
        await using var ssh = await connector.ConnectAsync(target, password, ct);
        var r = new RouterSsh(ssh);
        return RouterSuitability.Evaluate(await r.PreflightAsync(updateIndexes: true, ct: ct), releases, core.Manifest.Nfqws.Keys.ToArray());
    }

    public async Task<RouterInstallOutcome> InstallAsync(RouterSshTarget target, Func<string> password, IProgress<string>? progress = null, CancellationToken ct = default,
        IProgress<RouterInstallStepCode>? steps = null)
    {
        progress?.Report("Подключение к роутеру"); steps?.Report(RouterInstallStepCode.Prepare);
        await using var ssh = await connector.ConnectAsync(target, password, ct);
        var r = new RouterSsh(ssh);

        progress?.Report("Проверка роутера"); steps?.Report(RouterInstallStepCode.Prepare);
        var arches = core.Manifest.Nfqws.Keys.ToArray();
        var verdict = RouterSuitability.Evaluate(await r.PreflightAsync(updateIndexes: true, ct: ct), releases, arches);
        if (!verdict.CanInstall) return new(verdict, null);

        RouterTunnel? tunnel = null;
        try
        {
            if (verdict.NeedsTunnel)
            {
                progress?.Report("Интернет роутеру — через этот компьютер"); steps?.Report(RouterInstallStepCode.Prepare);
                tunnel = await RouterTunnel.OpenAsync(ssh, await r.FeedHostsAsync(ct), m => progress?.Report(m), ct);
                if (verdict.KmodPending)
                {
                    // Без сети индекс фида был пуст — модуль ядра проверяем ещё раз, уже через туннель.
                    verdict = RouterSuitability.Evaluate(await r.PreflightAsync(true, tunnel.RemotePort, ct), releases, arches);
                    if (!verdict.CanInstall || verdict.KmodPending) return new(verdict with { CanInstall = false }, null);
                }
            }

            progress?.Report("Ядро zapret: загрузка на роутер"); steps?.Report(RouterInstallStepCode.Prepare);
            var zip = await core.EnsureAsync(http, LocalCoreCandidates, progress, ct);
            await using (var s = File.OpenRead(zip)) await r.UploadCoreAsync(s, core.Manifest.ZipSha256, ct);

            var result = await r.InstallAsync(tunnel?.RemotePort, progress, ct, steps);
            return new(verdict, result);
        }
        finally
        {
            if (tunnel is not null) await tunnel.DisposeAsync();
        }
    }

    /// <summary>Удаление без API (пакет не отвечает) — uninstall.sh по SSH, затем «что осталось».</summary>
    public async Task<RouterUninstallOutcome> UninstallAsync(RouterSshTarget target, Func<string> password, bool keepSettings, CancellationToken ct = default)
    {
        await using var ssh = await connector.ConnectAsync(target, password, ct);
        var r = new RouterSsh(ssh);
        var res = await r.UninstallAsync(keepSettings, ct);
        return new(res, await r.AfterUninstallAsync(ct));
    }
}
