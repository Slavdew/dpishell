using System.Text.Json.Serialization;

namespace DpiShell.Core.Router;

// Форматы — docs/dev/router-api.md (API 1). Имена полей — snake_case, как в договоре.

public sealed record RouterPublicStatus(
    string? Product, ApiVersion? Api, string? Version, string? Engine, string? Core,
    bool Enabled, string? Strategy, int? CatalogVersion, DateTime? CheckedAt, bool? LastCheckOk,
    // С 0.2.0: минимальная версия окна ПК, которую понимает этот роутер.
    string? MinApp = null);

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

/// <remarks>
/// Version — ядро, закреплённое в пакете. InstalledVersion (роутер 0.2.0-r8; старые не отдают) — установленное в /opt/zapret:
/// null — не установлено или роутер поля не знает (тогда смотрят Installed).
/// </remarks>
public sealed record RouterCore(string? Version, bool Installed, bool HashOk, string? InstalledVersion = null)
{
    /// <summary>Версия ядра на роутере для окна: установленная, а если роутер её не отдаёт — закреплённая; null — ядра нет.</summary>
    public string? ShownVersion => !Installed ? null : InstalledVersion is { Length: > 0 } v ? v : Version is { Length: > 0 } p ? p : "";
}

/// <summary>
/// status.packages (роутер 0.2.0-r8; старые не отдают): установленные версии пакетов DpiShell, null — пакета нет.
/// Страница LuCI — отдельный пакет luci-app-dpishell и версия у неё своя.
/// </summary>
public sealed record RouterPackages(string? Dpishell = null, [property: System.Text.Json.Serialization.JsonPropertyName("luci-app-dpishell")] string? LuciAppDpishell = null);
public sealed record RouterGroupCheck(string Id, bool Ok);
public sealed record RouterLastCheck(DateTime? At, string? Source, bool Ok, List<RouterGroupCheck>? Groups);
/// <summary>Таймер проверки; hours ∈ 0.5, 1, 3, 6, 12, 24 (редакция §9).</summary>
/// <remarks>watchdog — сторож включён (аварийный выключатель, §5.1); null — роутер до сторожа.</remarks>
public sealed record RouterSchedule(bool Enabled, double Hours, bool AutoSelect = false, bool? Watchdog = null);

/// <summary>
/// Включение на пробу ждёт confirm (§5.1 pending_confirm): left_s — сколько осталось на момент ответа (часы роутера и ПК расходятся,
/// поэтому отсчёт — от left_s, не от until); revert_to — что вернёт роутер сам: off или id стратегии.
/// </summary>
public sealed record RouterPendingConfirm(DateTime? Until, int LeftS, string? RevertTo)
{
    public bool RevertsToOff => RevertTo is null or "off";
}

/// <summary>Цель, которая не открылась у сторожа: адрес и что сломалось.</summary>
public sealed record RouterOffTarget(string Url, string? Detail = null);

/// <summary>Последнее выключение без человека (§5.1 last_off): by watchdog | button | revert; null при включённом обходе.</summary>
public sealed record RouterLastOff(string By, DateTime? At, string? Text, List<RouterOffTarget>? Targets = null);

/// <summary>Жив ли сторож (§5.1 watchdog): последняя проверка, её итог и через сколько минут следующая.</summary>
/// <remarks>Select (роутер 0.2.0-r8) — подбор после сторожа: null — не было с последнего действия человека (или старый роутер).</remarks>
public sealed record RouterWatchdog(bool Enabled, DateTime? CheckedAt, bool? Ok, int? IntervalMin = null, RouterWatchdogSelect? Select = null);

/// <summary>Подбор после сторожа (status.watchdog.select): at — когда, ok — включил ли стратегию (пока идёт — false), best — какую (null при неудаче).</summary>
public sealed record RouterWatchdogSelect(DateTime? At, bool Ok, string? Best);
/// <summary>Фоновые задания роутера; dns — любое DNS-задание (§9, 2026-10-17), dns_analyze оставлен с тем же значением.</summary>
public sealed record RouterJobs(bool Select, bool Check, bool CoreInstall, bool DnsAnalyze = false, bool SiteCheck = false, bool Dns = false, bool Update = false);

/// <summary>
/// Последнее задание обновления (status.update.job): action update | update_check, from feed | local; at — когда закончилось.
/// step — шаг идущего обновления (download | install | restart | done), log — хвост журнала (массив строк или одна строка);
/// оба необязательные: старый роутер их не отдаёт.
/// </summary>
public sealed record RouterUpdateJob(string? Action, string? From, bool Running, bool? Ok, string? Message, DateTime? At = null,
    string? Step = null, System.Text.Json.JsonElement? Log = null)
{
    /// <summary>Хвост журнала строками; не отдан или непонятного вида — пусто.</summary>
    public IReadOnlyList<string> LogLines => Log is not { } l ? []
        : l.ValueKind == System.Text.Json.JsonValueKind.Array
            ? l.EnumerateArray().Where(x => x.ValueKind == System.Text.Json.JsonValueKind.String).Select(x => x.GetString()!).ToList()
        : l.ValueKind == System.Text.Json.JsonValueKind.String ? l.GetString()!.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        : [];
}
/// <summary>
/// status.update (ядро договора §5.4): available — в витрине версия новее установленной, notes — что нового (до 15 строк),
/// job — последнее задание обновления (null — не было).
/// </summary>
public sealed record RouterUpdate(bool Available, string? Version, List<string>? Notes, DateTime? CheckedAt, RouterUpdateJob? Job = null);

/// <summary>Откуда обновлять: feed — витрина; local — /tmp/dpishell-update (кладёт окно по SSH, <see cref="RouterSsh.UploadUpdateAsync"/>).</summary>
public enum RouterUpdateSource { Feed, Local }
public sealed record RouterWarning(string Code, string? Text);

public sealed record RouterStatus(
    ApiVersion Api, string? Version, string? Engine, RouterCore? Core, bool Enabled, string? Strategy, List<string>? Backups,
    RouterLastCheck? LastCheck, int FailStreak, RouterSchedule? Schedule, RouterJobs? Jobs, List<RouterWarning>? Warnings,
    // mode: "all" — «Все сайты, кроме исключений» (по умолчанию на роутере), "lists" — «По спискам».
    // needs_tcp_timestamps: стратегия роутера работает только у устройств с метками TCP — ПК включает их сам (§6).
    string? Mode = null, bool NeedsTcpTimestamps = false,
    // С 0.2.0: методы, которые роутер реально обрабатывает (null — старый роутер, считаем «все из API 1.0»), и min_app.
    List<string>? Methods = null, string? MinApp = null,
    // С 0.2.0 (ядро договора): есть ли обновление и последнее задание обновления.
    RouterUpdate? Update = null,
    // Аварийный выключатель (§5.1): включение ждёт confirm, последнее выключение без человека, состояние сторожа. Старый роутер — null.
    RouterPendingConfirm? PendingConfirm = null, RouterLastOff? LastOff = null, RouterWatchdog? Watchdog = null,
    // С 0.2.0-r8: версии установленных пакетов (dpishell, luci-app-dpishell). Старый роутер — null.
    RouterPackages? Packages = null,
    // С 0.2.0-r9: продвинутый режим (§5.1 advanced_get): включён ли и сколько строк. Старый роутер — null.
    RouterAdvancedSummary? Advanced = null);

/// <summary>status.advanced (роутер 0.2.0-r9): включён ли продвинутый режим и сколько в нём строк.</summary>
public sealed record RouterAdvancedSummary(bool Enabled, int Rows);

/// <summary>
/// Строка продвинутого режима на роутере (§5.1 advanced_get/advanced_set): сайты и стратегия — <c>auto</c>, <c>direct</c> или id из <c>strategies</c>.
/// Resolved приходит только в ответе: чем строка идёт сейчас (у <c>auto</c> — итог подбора, до подбора — текущая стратегия; у <c>direct</c> — null).
/// </summary>
public sealed record RouterAdvancedRow(List<string> Domains, string Strategy, string? Resolved = null);

/// <summary>advanced_get: <c>others</c> — остальные адреса: <c>direct</c> | <c>lists</c> | <c>all</c>.</summary>
public sealed record RouterAdvanced(bool Enabled, string Others, List<RouterAdvancedRow> Rows);

/// <summary>
/// source: catalog | user; у user id «u-…» — присланные с ПК (strategies_user_set), «r-…» — найденные глубоким поиском на роутере (§9).
/// Runs/Passed — история: окно её не показывает (о сегодняшней сети не говорит). Last — последняя проверка, Roles — параметры по ролям
/// (необязательные поля, предложение окна; старый роутер — null: тогда в окне ни того, ни другого).
/// </summary>
public sealed record RouterStrategyItem(string Id, string Title, string Source, int Runs, int Passed,
    RouterStrategyLast? Last = null, Dictionary<string, string>? Roles = null)
{
    public bool FoundOnRouter => Source == "user" && Id.StartsWith("r-", StringComparison.Ordinal);
    public bool FromPc => Source == "user" && Id.StartsWith("u-", StringComparison.Ordinal);
}
public sealed record RouterStrategies(int CatalogVersion, List<RouterStrategyItem> Items);

/// <summary>Последняя проверка стратегии на роутере: когда и открыла ли всё; failed — названия групп, что не открылись.</summary>
public sealed record RouterStrategyLast(DateTime? At, bool Ok, List<string>? Failed = null);

/// <summary>
/// Строка хода подбора; title — название из каталога или описание кандидата глубокого поиска (редакция §9, 2026-10-17).
/// Кандидат глубокого поиска — candidate: true, ok из total — открылось обязательных целей его роли (§9, 2026-10-17);
/// в strategies его нет — применить нельзя, найденные в финале приходят в strategies с id «r-…».
/// </summary>
/// <remarks>verdicts — необязательно (предложение к договору, формат обсуждается): результаты по адресам для «Подробнее»; без него — только доли.</remarks>
public sealed record RouterSelectRow(string Id, double GroupShare, double TargetShare, double AvgS, string? Error, string? Title = null,
    bool Candidate = false, int? Ok = null, int? Total = null, List<RouterUrlVerdict>? Verdicts = null,
    Dictionary<string, string>? Roles = null, int? GroupsOk = null, int? GroupsTotal = null);

/// <summary>Итог по одному адресу в строке подбора роутера: группа целей, адрес, открылся ли, подробности (код, байты), время.</summary>
public sealed record RouterUrlVerdict(string Group, string Url, bool Ok, string? Detail = null, double? Seconds = null);
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

/// <param name="Id">Вариант из dns_presets с одним сервером (§9, 2026-10-17).</param>
public sealed record RouterDnsServer(string Name, bool DohOk, int? Ms, string? Id = null);
/// <summary>Последнее DNS-задание (dns_enable/dns_restore/dns_analyze/dns_set — фоновые).</summary>
public sealed record RouterDnsJob(string? Action, bool Running, bool? Ok, string? Message, DateTime? At = null);
/// <summary>DNS для всей сети: mode provider|doh|plain; fallback_active — оба DoH пропали, временно DNS провайдера.</summary>
/// <param name="Preset">Включённый вариант (dns_presets); Custom — последний свой (null — не задавался).</param>
public sealed record RouterDnsStatus(string? Mode, bool Spoofed, DateTime? CheckedAt, List<RouterDnsServer>? Servers, bool FallbackActive, RouterDnsJob? Job,
    string? Preset = null, RouterDnsCustom? Custom = null);

/// <summary>Сервер DoH варианта: шаблон и адреса для его имени.</summary>
public sealed record RouterDnsResolver(string Doh, List<string>? Bootstrap);
/// <summary>Вариант DNS для всей сети (§5.1 dns_presets): provider, default, google, cloudflare, quad9, yandex.</summary>
public sealed record RouterDnsPreset(string Id, string Title, string? Note, List<RouterDnsResolver>? Resolvers, List<string>? Plain);
/// <summary>Свой вариант (dns_set custom): title до 40 символов; doh https://…, bootstrap/plain — до 4 адресов; нужен doh или plain.</summary>
public sealed record RouterDnsCustom(string Title, string? Doh = null, List<string>? Bootstrap = null, List<string>? Plain = null);
public sealed record RouterDnsPresets(List<RouterDnsPreset> Presets, string? Current, RouterDnsCustom? Custom);

/// <summary>kind: service | package (§9: zapret, luci-app-zapret, zapret-manager-beam, zapret2 — conflict_stop их не останавливает, только удаление).</summary>
public sealed record RouterConflict(string Kind, string Name, bool Running);
/// <summary>Ответ flow_offload_fix: flowoffload — кому передана разгрузка (software | hardware).</summary>
public sealed record RouterFlowOffloadFix(bool Changed, string? Flowoffload);
public sealed record RouterDns(bool Spoofed, DateTime? CheckedAt);
/// <summary>flow_offload — разгрузка в межсетевом экране (мешает обходу); flowoffload — её ведёт zapret после flow_offload_fix: none | software | hardware (router-api §5.1).</summary>
public sealed record RouterDiag(List<RouterConflict>? Conflicts, bool FlowOffload, bool Ipv6Wan, RouterDns? Dns, long OverlayFreeKb, string? Flowoffload = null);

public sealed record RouterReport(string Text);
public sealed record RouterStarted(bool Started);

/// <summary>Стратегия для `strategies_user_set`: только id, название и роли — пути и командную строку роутер не принимает.</summary>
/// <param name="Parts">Только у смешанной (договор §5.1, роутер с catalog_version 3): { youtube | discord | user → id стратегии }; роутер хранит как прислали.</param>
public sealed record RouterUserStrategy(string Id, string Title, Dictionary<string, string> Roles, Dictionary<string, string>? Parts = null);

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
