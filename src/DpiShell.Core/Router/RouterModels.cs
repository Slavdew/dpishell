using System.Text.Json.Serialization;

namespace DpiShell.Core.Router;

// Форматы — docs/07-router-api.md (API 1). Имена полей — snake_case, как в договоре.

public sealed record RouterPublicStatus(
    string? Product, int? Api, string? Version, string? Engine, string? Core,
    bool Enabled, string? Strategy, int? CatalogVersion, DateTime? CheckedAt, bool? LastCheckOk);

public enum RouterFound
{
    /// <summary>Нет ответа, 404, не JSON или чужой product — «На роутере DpiShell не найден».</summary>
    NotFound,
    /// <summary>Всё совместимо.</summary>
    Ok,
    /// <summary>api роутера новее приложения — «Обновите приложение», только чтение статуса.</summary>
    AppTooOld,
    /// <summary>api роутера старше (или не указан) — «Обновите пакет на роутере».</summary>
    RouterTooOld,
}

public sealed record RouterDetection(string Address, RouterFound Found, RouterPublicStatus? Status)
{
    public bool CanControl => Found == RouterFound.Ok;
}

public sealed record RouterCore(string? Version, bool Installed, bool HashOk);
public sealed record RouterGroupCheck(string Id, bool Ok);
public sealed record RouterLastCheck(DateTime? At, string? Source, bool Ok, List<RouterGroupCheck>? Groups);
/// <summary>Таймер проверки; hours ∈ 0.5, 1, 3, 6, 12, 24 (редакция §9).</summary>
public sealed record RouterSchedule(bool Enabled, double Hours, bool AutoSelect = false);
/// <summary>Фоновые задания роутера; dns — любое DNS-задание (§9, 2026-10-17), dns_analyze оставлен с тем же значением.</summary>
public sealed record RouterJobs(bool Select, bool Check, bool CoreInstall, bool DnsAnalyze = false, bool SiteCheck = false, bool Dns = false);
public sealed record RouterWarning(string Code, string? Text);

public sealed record RouterStatus(
    int Api, string? Version, string? Engine, RouterCore? Core, bool Enabled, string? Strategy, List<string>? Backups,
    RouterLastCheck? LastCheck, int FailStreak, RouterSchedule? Schedule, RouterJobs? Jobs, List<RouterWarning>? Warnings,
    // mode: "all" — «Все сайты, кроме исключений» (по умолчанию на роутере), "lists" — «По спискам».
    // needs_tcp_timestamps: стратегия роутера работает только у устройств с метками TCP — ПК включает их сам (§6).
    string? Mode = null, bool NeedsTcpTimestamps = false);

public sealed record RouterStrategyItem(string Id, string Title, string Source, int Runs, int Passed);
public sealed record RouterStrategies(int CatalogVersion, List<RouterStrategyItem> Items);

/// <summary>Строка хода подбора; title — название из каталога или описание кандидата глубокого поиска (редакция §9, 2026-10-17).</summary>
public sealed record RouterSelectRow(string Id, double GroupShare, double TargetShare, double AvgS, string? Error, string? Title = null);
/// <summary>
/// Ход подбора (§5.1, редакция §9 от 2026-10-16): по завершении роутер сам включает лучшую — `applied`, `backups`;
/// остановленный — `cancelled`. `started_by`: page | app | auto. `eta_s` — оценка оставшегося, null — неизвестна.
/// </summary>
public sealed record RouterSelectProgress(
    bool Running, int Index, int Total, string? Message, List<RouterSelectRow>? Rows,
    DateTime? FinishedAt, string? Best,
    string? StartedBy = null, string? Depth = null, int? EtaS = null,
    bool Cancelled = false, bool Applied = false, List<string>? Backups = null);

public sealed record RouterTarget(string Url, bool Important);
public sealed record RouterTargets(List<RouterTarget> Items);
/// <summary>editable: false у стандартного списка исключений `exclude` (только чтение).</summary>
public sealed record RouterLines(List<string> Lines, bool Editable = true);

public sealed record RouterSiteCheckSide(bool Ok, string? Detail);
/// <summary>«Сайт не открывается с обходом» (§5.1): suggest_exclude — открывается только без обхода, предложить в исключения.</summary>
public sealed record RouterSiteCheckResult(bool Running, string? Url, RouterSiteCheckSide? WithBypass, RouterSiteCheckSide? WithoutBypass, bool SuggestExclude);

public sealed record RouterDnsServer(string Name, bool DohOk, int? Ms);
/// <summary>Последнее DNS-задание (dns_enable/dns_restore/dns_analyze — фоновые).</summary>
public sealed record RouterDnsJob(string? Action, bool Running, bool? Ok, string? Message, DateTime? At = null);
/// <summary>DNS для всей сети: mode provider|doh; fallback_active — оба DoH пропали, временно DNS провайдера.</summary>
public sealed record RouterDnsStatus(string? Mode, bool Spoofed, DateTime? CheckedAt, List<RouterDnsServer>? Servers, bool FallbackActive, RouterDnsJob? Job);

public sealed record RouterConflict(string Kind, string Name, bool Running);
public sealed record RouterDns(bool Spoofed, DateTime? CheckedAt);
public sealed record RouterDiag(List<RouterConflict>? Conflicts, bool FlowOffload, bool Ipv6Wan, RouterDns? Dns, long OverlayFreeKb);

public sealed record RouterReport(string Text);
public sealed record RouterStarted(bool Started);

/// <summary>Стратегия для `strategies_user_set`: только id, название и роли — пути и командную строку роутер не принимает.</summary>
public sealed record RouterUserStrategy(string Id, string Title, Dictionary<string, string> Roles);

/// <summary>Ошибка уровня DpiShell (код 0 от ubus, но в ответе `error`).</summary>
public sealed record RouterAppError(string Code, string? Text, string? Field);

public enum RouterFailure
{
    /// <summary>Роутер не ответил (сеть, таймаут HTTP).</summary>
    Unreachable,
    /// <summary>Неверный пароль (login → 6) или доступ не вернулся после повторного входа.</summary>
    AccessDenied,
    /// <summary>ubus 2 — неверные аргументы.</summary>
    InvalidArgs,
    /// <summary>ubus 4 — метода/объекта нет: пакет не установлен или старый.</summary>
    NotSupported,
    /// <summary>ubus 5/7 — ответа нет или таймаут на роутере.</summary>
    NoResponse,
    /// <summary>Ошибка DpiShell на роутере (busy, conflict, invalid…) — см. <see cref="RouterException.AppError"/>.</summary>
    App,
    /// <summary>Ответ не по договору.</summary>
    Protocol,
}

public sealed class RouterException(RouterFailure failure, string message, RouterAppError? appError = null, int? ubusCode = null)
    : Exception(message)
{
    public RouterFailure Failure { get; } = failure;
    public RouterAppError? AppError { get; } = appError;
    public int? UbusCode { get; } = ubusCode;
}
