using DpiShell.Core.Localization;
using System.Text.Json.Nodes;

namespace DpiShell.Core.Helper;

/// <summary>
/// Что умеет помощник — ровно то, что разрешает белый список. Сервер канала зовёт только этот интерфейс:
/// в службе — <see cref="ShellHelperBackend"/> над <see cref="Shell"/>, в тестах — фейк без winws и sc.
/// Аргументы уже проверены <see cref="HelperCommands"/>.
/// </summary>
public interface IHelperBackend : IHelperCatalog
{
    HelperStatus Status();
    void Enable(string strategyId);
    void Disable();
    void SetRunMode(bool service);
    /// <summary>Откуда подбор и проверки берут адреса сайтов (API 1.12, <see cref="CheckDns"/>): ставится перед командой подбора, если окно её прислало.</summary>
    void SetCheckDns(string mode);
    Task<SelectionReport> SelectAsync(bool quick, IReadOnlyList<string>? groups, IProgress<SelectorProgress> progress, CancellationToken ct);
    Task<SearchReport> SearchAsync(string? baseId, IReadOnlyList<string>? groups, IProgress<SelectorProgress> progress, CancellationToken ct);
    void SetIpsetMode(IpsetMode mode);
    void StartAllAddresses();
    bool StopAllAddresses();
    Task<GameModeChange> SetGameModeAsync(GameMode mode, IProgress<string> log, CancellationToken ct);
    Task<IpsetUpdateSummary> UpdateIpsetFromManifestAsync(bool confirmed, IProgress<string> log, CancellationToken ct);
    IpsetUpdateSummary UpdateIpsetFromContent(string content, bool confirmed);
    IpsetVerdictSummary WriteUserIpset(IReadOnlyList<string> lines, bool exclude, bool confirmed);
    UserTargetsResult SetUserTargets(IReadOnlyList<string> lines);
    /// <summary>Сайты, которые на этом ПК открываются без оптимизации (API 1.10): список целиком, домены проходят тот же разбор, что «Свои сайты».</summary>
    UserTargetsResult SetUserExclude(IReadOnlyList<string> lines);
    Task<HostsCheckReport> HostsCheckAsync(IProgress<HostsCheckProgress> progress, CancellationToken ct);
    /// <summary>Предпросмотр по последней проверке (что будет записано); проверки не было — предпросмотр снятия блока.</summary>
    HostsPreview HostsPreview();
    /// <summary>Записать только то, что последняя проверка признала нужным. Окно не передаёт адреса — непроверенное не запишется.</summary>
    HostsWriteResult HostsApply();
    HostsWriteResult HostsRevert();
    Task<HostsRecheckResult> HostsRecheckAsync(CancellationToken ct);
    /// <summary>Примерить строки «адрес имя» (вердикты); hosts не меняется.</summary>
    Task<HostsTryReport> HostsTryAsync(IReadOnlyList<HostsEntry> entries, IProgress<HostsCheckProgress> progress, CancellationToken ct);
    /// <summary>Проверить чужие строки hosts (вне блоков DpiShell); запоминает мешающие — только их можно закомментировать.</summary>
    Task<HostsTryReport> HostsAuditAsync(IProgress<HostsCheckProgress> progress, CancellationToken ct);
    /// <summary>Поставить ровно эти свои строки пользователя; новые заново проверяются живым подключением.</summary>
    Task<HostsUserResult> HostsUserSetAsync(IReadOnlyList<HostsEntry> entries, CancellationToken ct);
    /// <summary>Закомментировать выбранные мешающие строки из последнего аудита.</summary>
    HostsCommentResult HostsComment(IReadOnlyList<string> ids);
    /// <summary>Вернуть закомментированные нами строки.</summary>
    HostsCommentResult HostsUncomment();
    /// <summary>«Вернуть как было»: блоки DpiShell и свои строки долой, закомментированные — обратно.</summary>
    HostsRestoreResult HostsRestore();
    StopForeignResult StopForeign();
    Task CoreRepairAsync(IProgress<string> log, CancellationToken ct);
    void RemoveAll();
    int ClearHistory();
    ResultsSnapshot ReadResults();
    /// <summary>Сведения из закрытой папки для окна без прав: hosts, свои подсети, ядро, найденные стратегии.</summary>
    HelperDetails Details();
    /// <summary>Последние строки журнала помощника (<see cref="HelperLog.TailLines"/>), старые вперёд; журнала нет — пусто.</summary>
    IReadOnlyList<string> LogTail();
    /// <summary>Выбрать стратегию без включения обхода (обход включён — окно зовёт enable).</summary>
    void Choose(string strategyId);
    /// <summary>Включить временные метки TCP (их требует стратегия роутера, router-api §6). true — включены.</summary>
    bool TcpTimestampsOn();
    /// <summary>Обновление данных по data.json с источников помощника (architecture.md «Обновления данных»).</summary>
    Task<DataUpdateResult> UpdateDataAsync(bool confirmed, IProgress<string> log, CancellationToken ct);
    /// <summary>DNS этого компьютера: вариант на физических адаптерах, защищён ли, есть ли что вернуть (architecture.md «DNS этого компьютера»).</summary>
    DnsStatusInfo DnsStatus();
    /// <summary>Включить вариант DNS; ips/doh — только для preset = custom (уже проверены).</summary>
    Task<DnsSetResult> DnsSetAsync(string preset, IReadOnlyList<string>? ips, string? doh, bool fallback, CancellationToken ct);
    /// <summary>«Вернуть как было» — прежние настройки DNS адаптеров.</summary>
    Task<DnsSetResult> DnsRestoreAsync(CancellationToken ct);
    /// <summary>Сохранить свою (source = user) или вставленную (pasted) стратегию под новым id. Роли проверяются заново, файлы fake — по папке программы.</summary>
    Strategy AddStrategy(string title, IReadOnlyDictionary<string, string> roles, string source);
    /// <summary>Удалить свою/вставленную/найденную стратегию; выбранную сейчас — нельзя.</summary>
    void DeleteStrategy(string strategyId);
    /// <summary>Проверка сайтов для одной стратегии (прогон подбора из одного кандидата).</summary>
    Task<SelectionReport> CheckStrategyAsync(string strategyId, IReadOnlyList<string>? groups, IProgress<SelectorProgress> progress, CancellationToken ct);
    /// <summary>Все окна отключились от канала: в режиме без службы winws гасится; запись подключений останавливается.</summary>
    void OnClientsGone();

    // ---------- подключения программ (ТЗ 13) ----------
    /// <summary>Начать запись подключений. pid — только для режима Process. useDriver = false: без драйвера перехвата (только TCP).</summary>
    RecordingStatus RecStart(RecordingMode mode, int pid, bool useDriver);
    RecordingStatus RecStatus();
    /// <summary>Остановить и отдать записанное; записи не было — исключение.</summary>
    RecordingResult RecStop();
    AppProfilesInfo AppProfilesInfo();
    /// <summary>Создать (id = null) или заменить профиль; параметры берутся из варианта готовых стратегий.</summary>
    AppProfileInfo AppProfileSet(string? id, string title, string? tcpPorts, string? udpPorts, IReadOnlyList<string> addresses, int variant);
    bool AppProfileRemove(string id);

    // ---------- продвинутый режим «сайты → стратегия» (API 1.8) ----------
    /// <summary>Настройки продвинутого режима и чем идёт каждая строка. Только чтение.</summary>
    AdvancedInfo AdvancedGet();
    /// <summary>Сохранить: режим, остальные адреса и строки (сайты и стратегия — «auto», «direct» или id; других полей нет). Оптимизация перезапускается на новых строках.</summary>
    AdvancedInfo AdvancedSet(bool enabled, string others, IReadOnlyList<AdvancedRowInput> rows);

    // ---------- совместная работа с роутером (API 1.9) ----------
    /// <summary>Что этот ПК готов взять на себя при совместной работе (свои сайты, строки, имена приманок) и действует ли связка. Только чтение.</summary>
    JointInfo JointGet();
    /// <summary>Включить/выключить связку и продлить её (срок <see cref="JointRules.Lease"/>); owned — сайты ПК, которые ведёт роутер. Оптимизация перезапускается, если изменилась командная строка.</summary>
    JointInfo JointSet(bool active, IReadOnlyList<string> owned);

    // ---------- откат всего (журнал изменений, API 1.6) ----------
    /// <summary>Что сделала программа в системе и можно откатить: из журнала, понятными словами.</summary>
    ChangesList ChangesList();
    /// <summary>Откатить всё по журналу в обратном порядке. Значение возвращается, только если сейчас стоит то, что ставили мы.</summary>
    Task<UndoReport> ChangesUndoAllAsync(CancellationToken ct);
    /// <summary>Откатить одну запись журнала.</summary>
    Task<UndoReport> ChangesUndoAsync(string id, CancellationToken ct);
    /// <summary>Откатить действие вместе с записями, сделанными внутри него (загрузка настроек, API 1.15).</summary>
    Task<UndoReport> ChangesUndoGroupAsync(string id, CancellationToken ct);

    // ---------- системный прокси (API 1.7) ----------
    /// <summary>Что стоит сейчас (ветка пользователя канала, машина, политика, WinHTTP) и есть ли что вернуть. Только чтение.</summary>
    ProxyStatusInfo ProxyStatus(string? clientSid);
    /// <summary>Для пользователя канала (HKU\SID клиента). enabled = false — «Очистить»: прокси и PAC выключаются, прежнее запоминает окно.</summary>
    ProxyChangeResult ProxyUserSet(string? clientSid, bool enabled, string? server, string? bypass, string? pac);
    /// <summary>Для всей машины: HKLM + политика «одни настройки на всех»; winhttp — ещё и прокси для служб.</summary>
    ProxyChangeResult ProxyMachineSet(bool enabled, string? server, string? bypass, string? pac, bool winhttp);
    /// <summary>«Вернуть прежний» для машины — из proxy-backup.json помощника.</summary>
    ProxyChangeResult ProxyMachineRestore();

    // ---------- выгрузка и загрузка своих настроек (API 1.11) ----------
    /// <summary>Свои данные ПК и сведения, нужные для проверки чужого файла. Только чтение; окно без прав читает их только так.</summary>
    Export.SettingsState SettingsExportRead();
    /// <summary>Применить план загрузки (JSON <see cref="Export.ImportRequest"/>). Помощник проверяет его сам, сверяет со своим состоянием и пишет в журнал изменений; подделка — исключение.</summary>
    Task<Export.ImportResult> SettingsImportAsync(string request, CancellationToken ct);
}

/// <summary>Ответ proxy_status. Previous — что вернёт «Вернуть прежний» для машины (null — нечего).</summary>
public sealed record ProxyStatusInfo(ProxyOverview Now, ProxyBackup? Previous);

public sealed record ProxyChangeResult(bool Ok, string Message, ProxyStatusInfo Status);

/// <summary>Ответ на status: всё, что окну без прав нужно для экранов.</summary>
public sealed record HelperStatus(
    bool Enabled, bool Active, string RunMode, string? StrategyId,
    SvcState Service, bool ServiceOurCore, string? ServiceStrategy,
    CoreState Core, NetFeatures Features,
    IReadOnlyList<StrategyInfo> Strategies, IReadOnlyList<GroupInfo> Groups,
    IReadOnlyList<string> SelectedGroups, IReadOnlyList<string> UserTargets, IReadOnlyList<string> Fallbacks,
    bool TimestampsEnabledByUs, bool HostsFix, IReadOnlyList<Conflict> Foreign,
    string HelperVersion, string? DataVersion = null,
    string? Api = null, IReadOnlyList<string>? Commands = null, IReadOnlyList<string>? UserExclude = null)
{
    // Api — версия протокола «мажор.минор», Commands — белый список этого помощника (helper-protocol §6). Заполняет сервер;
    // помощник до 1.1 их не присылает (null = «1.0», список неизвестен).
}

/// <summary>Профиль программы для окна. Addresses — как записаны в профиле (до 500).</summary>
public sealed record AppProfileInfo(string Id, string Title, string TcpPorts, string UdpPorts, IReadOnlyList<string> Addresses, int Variant, string VariantTitle, DateTime CreatedAt);

/// <summary>Ответ app_profiles: профили, варианты параметров (как у текущей стратегии первым) и запись, если она идёт.</summary>
public sealed record AppProfilesInfo(IReadOnlyList<AppProfileInfo> Profiles, IReadOnlyList<ProfileVariant> Variants, RecordingStatus Recording);

public sealed record StrategyInfo(string Id, string Title, string Source);
public sealed record GroupInfo(string Id, string Title, int Targets);
public sealed record HostsCheckProgress(int Done, int Total);

/// <summary>Итог проверки IP-списка без самих подсетей (их до 100 тыс. — окну не нужны).</summary>
public sealed record IpsetVerdictSummary(
    bool Accepted, bool NeedsConfirm, int Entries, int Fixed, int Dropped, int Duplicates,
    IReadOnlyList<string> Problems, IReadOnlyList<string> Errors, IReadOnlyList<string> Confirm,
    int Ipv4, int Ipv6, double Ipv4Share, int Added, int Removed)
{
    public static IpsetVerdictSummary From(IpsetVerdict v) => new(v.Accepted, v.NeedsConfirm, v.Entries.Count, v.Fixed, v.Dropped,
        v.Duplicates, v.Problems.Take(50).ToList(), v.Errors, v.Confirm, v.Ipv4, v.Ipv6, v.Ipv4Share, v.Added, v.Removed);
}

public sealed record IpsetUpdateSummary(bool Applied, bool NeedsConfirm, string Message, IpsetVerdictSummary? Verdict)
{
    public static IpsetUpdateSummary From(IpsetUpdateResult r) =>
        new(r.Applied, r.NeedsConfirm, r.Message, r.Verdict is { } v ? IpsetVerdictSummary.From(v) : null);
}

/// <param name="Saved">Принятые адреса (как в окне: распознанные, без повторов).</param>
/// <param name="Rejected">Не распознаны и убраны.</param>
public sealed record UserTargetsResult(IReadOnlyList<string> Saved, IReadOnlyList<string> Rejected);

public sealed record StopForeignResult(IReadOnlyList<string> Stopped, IReadOnlyList<string> ComeBackOnReboot);

/// <param name="Stats">Статистика стратегий по результатам подборов.</param>
/// <param name="Results">Последние результаты подборов (свои сайты в них уже скрыты — ResultStore).</param>
public sealed record ResultsSnapshot(IReadOnlyDictionary<string, StrategyStats.Stat> Stats, IReadOnlyList<ResultFile> Results);

public sealed record ResultFile(string Name, JsonNode? Content);

/// <summary>Ответ на details: то, что окно раньше читало из %ProgramData%\DpiShell само.</summary>
/// <param name="Hosts">Состояние «Починить адреса»; null — hosts не прочитать.</param>
/// <param name="HostsCheckedAt">Когда последний раз перепроверяли записи hosts (локальное время).</param>
/// <param name="UserIpset">Свои подсети.</param>
/// <param name="UserIpsetExclude">Исключения.</param>
/// <param name="Core">Проверка файлов ядра (хеши).</param>
/// <param name="CoreVersion">Версия ядра из поставки.</param>
/// <param name="Found">Стратегии, найденные глубоким поиском (целиком — окно отправляет их на роутер).</param>
public sealed record HelperDetails(
    HostsStatus? Hosts, DateTime? HostsCheckedAt,
    IReadOnlyList<string> UserIpset, IReadOnlyList<string> UserIpsetExclude,
    CoreCheck Core, string CoreVersion, IReadOnlyList<Strategy> Found);

/// <summary>Помощник над настоящим <see cref="Shell"/> (служба, SYSTEM).</summary>
public sealed partial class ShellHelperBackend(Shell shell) : IHelperBackend
{
    /// <summary>Сколько последних результатов отдаёт read_results.</summary>
    public const int MaxResults = 30;

    readonly object _hostsLock = new();
    HostsCheckReport? _lastHostsCheck;

    public IReadOnlyCollection<string> StrategyIds => shell.AllStrategies.Select(s => s.Id).ToHashSet();
    public IReadOnlyCollection<string> GroupIds => [.. shell.DefaultTargets.Groups.Select(g => g.Id), "user"];

    /// <summary>Служба обхода (sc query); в тестах окна — подмена, чтобы не трогать службы машины.</summary>
    public Func<ServiceInfo> InspectService { get; init; } = shell.InspectService;
    /// <summary>Обход работает (в режиме службы — тоже sc).</summary>
    public Func<bool> IsActive { get; init; } = () => shell.IsActive;
    /// <summary>Чужие обходы (процессы через WMI, службы).</summary>
    public Func<IReadOnlyList<Conflict>> FindForeign { get; init; } = () => Conflicts.Find(shell.Paths, shell.AllStrategies);

    public HelperStatus Status()
    {
        var info = InspectService();
        var st = shell.State;
        IReadOnlyList<Conflict> foreign;
        try { foreign = FindForeign(); }
        catch (Exception e) when (e is InvalidOperationException or System.Management.ManagementException or UnauthorizedAccessException) { foreign = []; }
        return new HelperStatus(
            st.Enabled, IsActive(), st.RunMode, st.StrategyId,
            info.State, info.OurCore, info.Strategy?.Id,
            shell.Installer.Check().State, shell.Features,
            shell.AllStrategies.Select(s => new StrategyInfo(s.Id, s.Title, s.Source)).ToList(),
            [.. shell.DefaultTargets.Groups.Select(g => new GroupInfo(g.Id, g.Title, g.Targets.Count)), new GroupInfo("user", L.T("select.group.user"), st.UserTargets.Count)],
            st.Groups, st.UserTargets, st.Fallbacks,
            st.TimestampsEnabledByUs, st.HostsFix, foreign,
            HelperProtocol.AppVersion, shell.DataVersion, UserExclude: st.UserExclude);
    }

    /// <summary>Клиент HTTP для обновления данных; в тестах — подмена без сети.</summary>
    public Func<HttpClient>? UpdateHttp { get; init; }

    /// <summary>
    /// Обновить данные. Новое ядро в манифесте — как «Восстановить ядро»: работающий обход останавливается, ядро ставится
    /// по хешам нового core.json, обход возвращается на той же стратегии (аргументы — уже по новому каталогу).
    /// </summary>
    public async Task<DataUpdateResult> UpdateDataAsync(bool confirmed, IProgress<string> log, CancellationToken ct)
    {
        bool stopped = false;
        var http = UpdateHttp?.Invoke();
        try
        {
            var r = await shell.UpdateDataAsync(confirmed, async m =>
            {
                if (shell.State.Enabled && shell.IsActive)
                {
                    stopped = true;
                    if (shell.ServiceMode) shell.Service.Stop(); else shell.StopOwnProcess();
                }
                await shell.InstallCoreAsync(m, log, ct);
            }, log, ct, http);
            try
            {
                // Ядро меняли — обход вернуть; иначе служба на старых аргументах (каталог поменялся) — перезапустить на новых.
                if (stopped && shell.State.StrategyId is { } id) shell.Enable(id);
                else if (r.Applied) shell.RefreshServiceIfOutdated();
            }
            catch (Exception e) when (e is InvalidOperationException or SecurityException or IOException) { log.Report(e.Message); }
            return r;
        }
        finally { http?.Dispose(); }
    }

    /// <summary>Включить; выбранная руками — первая в запасных (раньше это делало окно).</summary>
    public void Enable(string strategyId)
    {
        shell.Enable(strategyId);
        WatchdogPolicy.Applied(shell.State, strategyId);
        shell.SaveState();
    }

    public void Disable() => shell.Disable();

    public bool TcpTimestampsOn()
    {
        shell.EnsureTcpTimestamps();
        return shell.Tcp.IsEnabled() == true;
    }
    public void SetRunMode(bool service) => shell.SetRunMode(service);

    public void Choose(string strategyId)
    {
        shell.State.StrategyId = strategyId;
        WatchdogPolicy.Applied(shell.State, strategyId);
        shell.SaveState();
    }

    public void SetCheckDns(string mode) => shell.CheckDnsMode = CheckDns.Normalize(mode);

    public async Task<SelectionReport> SelectAsync(bool quick, IReadOnlyList<string>? groups, IProgress<SelectorProgress> progress, CancellationToken ct)
    {
        RememberGroups(groups);
        var r = await shell.SelectAsync(quick, groups is null ? null : shell.SelectedGroups(groups), progress, ct);
        RememberFallbacks(r);
        return r;
    }

    public Strategy AddStrategy(string title, IReadOnlyDictionary<string, string> roles, string source) =>
        UserStrategies.AddPersonal(shell.Paths, title, roles, source);

    public void DeleteStrategy(string strategyId)
    {
        if (shell.Catalog.Find(strategyId) is not null) throw new InvalidOperationException(L.T("core.helper.builtin_strategy"));
        if (shell.State.StrategyId == strategyId) throw new InvalidOperationException(L.T("core.helper.strategy_selected"));
        if (!UserStrategies.Remove(shell.Paths, strategyId)) throw new InvalidOperationException(L.T("core.helper.no_such_strategy"));
        if (shell.State.Fallbacks.Remove(strategyId)) shell.SaveState();
    }

    public async Task<SelectionReport> CheckStrategyAsync(string strategyId, IReadOnlyList<string>? groups, IProgress<SelectorProgress> progress, CancellationToken ct)
    {
        RememberGroups(groups);
        return await shell.CheckOneAsync(strategyId, groups is null ? null : shell.SelectedGroups(groups), progress, ct);
    }

    public async Task<SearchReport> SearchAsync(string? baseId, IReadOnlyList<string>? groups, IProgress<SelectorProgress> progress, CancellationToken ct)
    {
        RememberGroups(groups);
        var r = await shell.SearchAsync(baseId, groups is null ? null : shell.SelectedGroups(groups), progress, ct);
        if (r.Final is { } f) RememberFallbacks(f);
        return r;
    }

    /// <summary>Рабочие по рейтингу — запасные для автопереключения (раньше — окно, теперь state.json только у помощника).</summary>
    void RememberFallbacks(SelectionReport r)
    {
        WatchdogPolicy.Remember(shell.State, r);
        shell.SaveState();
    }

    public HelperDetails Details()
    {
        HostsStatus? hosts;
        try { hosts = shell.Hosts.Status(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { hosts = null; }
        IReadOnlyList<string> Read(string file)
        {
            try { return shell.IpLists.Read(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
        }
        return new HelperDetails(hosts, shell.State.HostsCheckedAt, Read(IpLists.UserFile), Read(IpLists.UserExcludeFile),
            shell.Installer.Check(), shell.Installer.Manifest.Version, UserStrategies.Load(shell.Paths));
    }

    /// <summary>Выбор групп в окне — обходная настройка, живёт у помощника (как раньше в state.json).</summary>
    void RememberGroups(IReadOnlyList<string>? groups)
    {
        if (groups is null || groups.SequenceEqual(shell.State.Groups)) return;
        shell.State.Groups = [.. groups];
        shell.SaveState();
    }

    public void SetIpsetMode(IpsetMode mode) => shell.SetIpsetMode(mode);
    public void StartAllAddresses() => shell.StartAllAddresses();
    public bool StopAllAddresses() => shell.StopAllAddresses();
    public Task<GameModeChange> SetGameModeAsync(GameMode mode, IProgress<string> log, CancellationToken ct) => shell.SetGameModeAsync(mode, log, ct);

    public async Task<IpsetUpdateSummary> UpdateIpsetFromManifestAsync(bool confirmed, IProgress<string> log, CancellationToken ct) =>
        IpsetUpdateSummary.From(await shell.UpdateIpsetFromManifestAsync(confirmed, log, ct));

    /// <summary>Окно прислало содержимое файла: пишем во временный файл в закрытой папке и прогоняем тот же валидатор.</summary>
    public IpsetUpdateSummary UpdateIpsetFromContent(string content, bool confirmed)
    {
        shell.Paths.EnsureDirectories();
        var tmp = Path.Combine(shell.Paths.Root, "ipset.upload.txt");
        try
        {
            File.WriteAllText(tmp, content);
            return IpsetUpdateSummary.From(shell.IpLists.UpdateFromFile(tmp, confirmed, "file", null, DateTime.UtcNow));
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    public IpsetVerdictSummary WriteUserIpset(IReadOnlyList<string> lines, bool exclude, bool confirmed) =>
        IpsetVerdictSummary.From(shell.WriteUserIpset(lines, exclude, confirmed));

    /// <summary>Как «Сохранить» своих сайтов в окне: файл доменов, список целей, группа «Свои» включается.</summary>
    public UserTargetsResult SetUserTargets(IReadOnlyList<string> lines)
    {
        var (saved, rejected) = shell.SetUserTargets(lines);
        return new(saved, rejected);
    }

    /// <summary>
    /// «Открывать без оптимизации» (API 1.10): <see cref="Shell.SetUserExclude"/> — список в user-exclude.txt и в состояние, работающая оптимизация перезапускается.
    /// Только домены: остальное отбрасывается и возвращается в Rejected.
    /// </summary>
    public UserTargetsResult SetUserExclude(IReadOnlyList<string> lines)
    {
        var (saved, rejected) = shell.SetUserExclude(lines);
        return new(saved, rejected);
    }

    public async Task<HostsCheckReport> HostsCheckAsync(IProgress<HostsCheckProgress> progress, CancellationToken ct)
    {
        var report = await shell.Hosts.CheckAsync(new Progress<(int Done, int Total)>(p => progress.Report(new(p.Done, p.Total))), ct);
        lock (_hostsLock) _lastHostsCheck = report;
        return report;
    }

    public HostsPreview HostsPreview()
    {
        HostsCheckReport? r;
        lock (_hostsLock) r = _lastHostsCheck;
        return shell.Hosts.Preview(r?.ToInstall ?? []);
    }

    public HostsWriteResult HostsApply()
    {
        HostsCheckReport r;
        lock (_hostsLock) r = _lastHostsCheck ?? throw new InvalidOperationException(L.T("core.helper.check_addresses_first"));
        var w = shell.Hosts.Apply(r);
        shell.State.HostsFix = r.ToInstall.Count > 0;
        shell.State.HostsCheckedAt = DateTime.Now;
        shell.SaveState();
        return w;
    }

    public HostsWriteResult HostsRevert()
    {
        var w = shell.Hosts.Revert();
        shell.State.HostsFix = false;
        shell.SaveState();
        return w;
    }

    public async Task<HostsRecheckResult> HostsRecheckAsync(CancellationToken ct)
    {
        var r = await shell.Hosts.RecheckAsync(ct);
        shell.State.HostsCheckedAt = DateTime.Now;
        shell.SaveState();
        return r;
    }

    HostsTryReport? _lastAudit;

    public async Task<HostsTryReport> HostsTryAsync(IReadOnlyList<HostsEntry> entries, IProgress<HostsCheckProgress> progress, CancellationToken ct) =>
        await shell.Hosts.TryAsync(entries, new Progress<(int Done, int Total)>(p => progress.Report(new(p.Done, p.Total))), ct);

    public async Task<HostsTryReport> HostsAuditAsync(IProgress<HostsCheckProgress> progress, CancellationToken ct)
    {
        var report = await shell.Hosts.AuditAsync(new Progress<(int Done, int Total)>(p => progress.Report(new(p.Done, p.Total))), ct);
        lock (_hostsLock) _lastAudit = report;
        return report;
    }

    public async Task<HostsUserResult> HostsUserSetAsync(IReadOnlyList<HostsEntry> entries, CancellationToken ct)
    {
        var r = await shell.Hosts.SetUserAsync(entries, ct);
        shell.State.HostsCheckedAt ??= DateTime.Now;   // отсчёт суток для перепроверки своих строк
        shell.SaveState();
        return r;
    }

    public HostsCommentResult HostsComment(IReadOnlyList<string> ids)
    {
        HostsTryReport a;
        lock (_hostsLock) a = _lastAudit ?? throw new InvalidOperationException(L.T("core.helper.check_hosts_first"));
        return shell.Hosts.Comment(ids, a.ProblemIds.ToHashSet());
    }

    public HostsCommentResult HostsUncomment() => shell.Hosts.Uncomment();

    public HostsRestoreResult HostsRestore()
    {
        var r = shell.Hosts.RestoreAll();
        shell.State.HostsFix = false;
        shell.SaveState();
        return r;
    }

    public StopForeignResult StopForeign()
    {
        var (stopped, back) = shell.StopForeign();
        return new(stopped, back);
    }

    public Task CoreRepairAsync(IProgress<string> log, CancellationToken ct)
    {
        // Ядро меняется — работающий winws держит файлы; после восстановления обход возвращается.
        bool wasOn = shell.State.Enabled && shell.IsActive;
        return Run();
        async Task Run()
        {
            if (wasOn) { if (shell.ServiceMode) shell.Service.Stop(); else shell.StopOwnProcess(); }
            await shell.RepairCoreAsync(log, ct);
            if (wasOn && shell.State.StrategyId is { } id) shell.Enable(id);
        }
    }

    /// <summary>«Удалить полностью»: служба обхода и WinDivert, наш winws, временные метки TCP — если включали мы.</summary>
    public void RemoveAll()
    {
        shell.StopOwnProcess();
        shell.Service.Remove();
        shell.RestoreTcpTimestamps();
        DropRecording();
        shell.ClearAppProfiles();
        shell.ClearAdvanced();
        lock (_jointLock) { _jointTimer?.Dispose(); _jointTimer = null; }
        shell.ClearJoint();
        shell.State.Enabled = false;
        shell.SaveState();
    }

    public int ClearHistory() => ResultStore.ClearHistory(shell.Paths);

    public IReadOnlyList<string> LogTail() => HelperLog.Tail(shell.Paths, HelperLog.TailLines);

    public ResultsSnapshot ReadResults()
    {
        var files = Directory.Exists(shell.Paths.Results)
            ? new DirectoryInfo(shell.Paths.Results).GetFiles("*.json").OrderByDescending(f => f.Name).Take(MaxResults).ToList()
            : [];
        var results = new List<ResultFile>();
        foreach (var f in files)
        {
            try { results.Add(new(f.Name, JsonNode.Parse(File.ReadAllText(f.FullName)))); }
            catch (Exception e) when (e is System.Text.Json.JsonException or IOException) { }
        }
        return new(StrategyStats.LoadDetailed(shell.Paths), results);
    }

    public void OnClientsGone()
    {
        if (!shell.ServiceMode) shell.StopOwnProcess();
        DropRecording();   // окна нет — записывать некому
    }

    // ---------- запись подключений и профили программ ----------

    readonly object _recLock = new();
    ConnectionRecorder? _rec;
    Timer? _recTimer;

    /// <summary>Источники записи; в тестах — подмена. По умолчанию: таблица TCP и (если можно) драйвер WinDivert, слой FLOW, только чтение.</summary>
    public Func<bool, IReadOnlyList<IFlowSource>>? RecorderSources { get; init; }
    /// <summary>Список процессов для записи; в тестах — подмена.</summary>
    public IProcessCatalog Processes { get; init; } = new ToolhelpProcessCatalog();

    IReadOnlyList<IFlowSource> Sources(bool useDriver)
    {
        if (RecorderSources is { } custom) return custom(useDriver);
        var list = new List<IFlowSource> { new TcpTableSource() };
        if (useDriver) list.Add(new WinDivertFlowSource(Path.Combine(shell.Paths.Core, "WinDivert.dll")));
        return list;
    }

    public RecordingStatus RecStart(RecordingMode mode, int pid, bool useDriver)
    {
        lock (_recLock)
        {
            if (_rec is not null) throw new InvalidOperationException(L.T("core.helper.rec_running"));
            if (mode == RecordingMode.Process && !Processes.Snapshot().Any(p => p.Pid == pid))
                throw new InvalidOperationException(L.T("core.helper.no_process"));
            // Драйвер перехвата — только при целых файлах ядра (WinDivert.dll берётся по полному пути из папки ядра).
            if (useDriver && RecorderSources is null && shell.Installer.Check().State != CoreState.Ok) useDriver = false;
            var rec = new ConnectionRecorder(mode, pid, Sources(useDriver), Processes);
            rec.Start();
            _rec = rec;
            _recTimer = new Timer(_ => rec.Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            return rec.Status();
        }
    }

    public RecordingStatus RecStatus()
    {
        lock (_recLock)
        {
            _rec?.Tick();
            return _rec?.Status() ?? new(false, false, 0, 0, 0, false, []);
        }
    }

    public RecordingResult RecStop()
    {
        lock (_recLock)
        {
            var rec = _rec ?? throw new InvalidOperationException(L.T("core.helper.rec_idle"));
            rec.Stop();
            var r = rec.Result();
            DropRecording();
            return r;
        }
    }

    void DropRecording()
    {
        lock (_recLock)
        {
            _recTimer?.Dispose(); _recTimer = null;
            _rec?.Dispose(); _rec = null;
        }
    }

    static AppProfileInfo Info(AppProfile p, IReadOnlyList<ProfileVariant> variants) =>
        new(p.Id, p.Title, p.TcpPorts, p.UdpPorts, p.Addresses, p.Variant,
            variants.FirstOrDefault(v => v.Index == p.Variant)?.Title ?? L.T("core.helper.variant_n", new { n = p.Variant + 1 }), p.CreatedAt);

    public AppProfilesInfo AppProfilesInfo()
    {
        var variants = shell.ProfileVariants();
        return new([.. shell.State.AppProfiles.Select(p => Info(p, variants))], variants, RecStatus());
    }

    public AppProfileInfo AppProfileSet(string? id, string title, string? tcpPorts, string? udpPorts, IReadOnlyList<string> addresses, int variant) =>
        Info(shell.SetAppProfile(id, title, tcpPorts, udpPorts, addresses, variant), shell.ProfileVariants());

    public bool AppProfileRemove(string id) => shell.RemoveAppProfile(id);

    public AdvancedInfo AdvancedGet() => shell.AdvancedInfo();

    public AdvancedInfo AdvancedSet(bool enabled, string others, IReadOnlyList<AdvancedRowInput> rows)
    {
        shell.SetAdvanced(enabled, others, rows);
        return shell.AdvancedInfo();
    }

    // ---------- совместная работа с роутером ----------

    readonly object _jointLock = new();
    Timer? _jointTimer;

    public JointInfo JointGet() => shell.JointInfo();

    public JointInfo JointSet(bool active, IReadOnlyList<string> owned)
    {
        shell.SetJoint(active, owned);
        lock (_jointLock)
        {
            _jointTimer?.Dispose();
            _jointTimer = null;
            // Окно не продлило связку (закрыто, ноутбук ушёл из дома) — ПК сам возвращается к полной настройке.
            if (active) _jointTimer = new Timer(_ => ExpireJoint(), null, JointRules.Lease + TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
        }
        return shell.JointInfo();
    }

    void ExpireJoint()
    {
        try { shell.ExpireJoint(); }
        catch (Exception e) when (e is InvalidOperationException or System.Security.SecurityException or IOException) { }
    }

    /// <summary>Смена DNS в системе; в тестах — подмена в памяти (реестр и сеть машины не трогаются).</summary>
    public IDnsSystem DnsSystem { get; init; } = new WindowsDnsSystem();
    DnsManager? _dns;
    DnsManager Dns => _dns ??= new DnsManager(DnsSystem, shell.Paths.DnsBackup) { Recorder = shell.Journal };

    // ---------- откат всего ----------

    /// <summary>Служба оптимизации для отката; в тестах — подмена, чтобы не трогать службы машины.</summary>
    public IServiceOps ServiceOps { get; init; } = shell.Service;

    ChangeUndoEngine? _changes;

    /// <summary>
    /// Журнал и откат. Точка расширения: новый вид изменения пишется в <c>shell.Journal</c> (<see cref="IChangeRecorder.Begin"/> до действия),
    /// а его обработчик добавляется сюда: <c>backend.Changes.Register(new MyUndoer(...))</c>.
    /// </summary>
    public ChangeUndoEngine Changes
    {
        get
        {
            if (_changes is not null) return _changes;
            var e = new ChangeUndoEngine(shell.Journal);
            e.Register(new ServiceUndoer(ServiceOps));
            e.Register(new TcpTimestampsUndoer(shell.Tcp));
            e.Register(new DnsUndoer(DnsSystem));
            e.Register(new ProxyUndoer(ProxyRegistry, WinHttp));
            e.Register(new HostsUndoer(() => shell.Hosts));
            e.Register(new CoreUndoer(() => shell.Installer, shell.Paths));
            e.Register(new AppProfileUndoer(id => shell.State.AppProfiles.FirstOrDefault(p => p.Id == id), shell.RemoveAppProfile));
            e.Register(new AdvancedUndoer(() => shell.State.Advanced, shell.RestoreAdvanced));
            e.Register(new JointUndoer(() => shell.State.Joint, shell.RestoreJointOff));
            e.Register(new Export.SettingsImportUndoer(shell));
            return _changes = e;
        }
    }

    public ChangesList ChangesList() => Changes.List();

    public async Task<UndoReport> ChangesUndoAllAsync(CancellationToken ct)
    {
        // Свой winws режима «без службы» — не запись журнала, но откат всего его гасит.
        shell.StopOwnProcess();
        var r = await Changes.UndoAllAsync(ct);
        AfterUndo(r);
        return r;
    }

    public async Task<UndoReport> ChangesUndoAsync(string id, CancellationToken ct)
    {
        var r = await Changes.UndoAsync(id, ct);
        AfterUndo(r);
        return r;
    }

    public async Task<UndoReport> ChangesUndoGroupAsync(string id, CancellationToken ct)
    {
        var r = await Changes.UndoGroupAsync(id, ct);
        AfterUndo(r);
        return r;
    }

    /// <summary>Привести память программы к тому, что откатили: служба убрана — оптимизация выключена, hosts возвращён — «Починить адреса» выключено и т. д.</summary>
    void AfterUndo(UndoReport r)
    {
        bool done(string kind) => r.Items.Any(i => i.Status is UndoStatus.Restored or UndoStatus.AlreadyBack && Kind(i) == kind);
        if (done(ChangeKinds.Service)) shell.State.Enabled = false;
        if (done(ChangeKinds.TcpTimestamps)) shell.State.TimestampsEnabledByUs = false;
        if (done(ChangeKinds.HostsBlock) || done(ChangeKinds.HostsUser)) shell.State.HostsFix = false;
        shell.SaveState();
        // Прокси машины откатили — «Вернуть прежний» после этого теряет смысл.
        if (done(ChangeKinds.ProxyMachine)) new ProxyBackupStore(shell.Paths.ProxyBackup).Forget();
        // DNS: все записи откачены чисто — сохранённые прежние настройки больше не нужны («Вернуть как было» не должно предлагаться зря).
        var dnsItems = r.Items.Where(i => Kind(i) == ChangeKinds.Dns).ToList();
        if (dnsItems.Count > 0 && dnsItems.All(i => i.Status is UndoStatus.Restored or UndoStatus.AlreadyBack) && shell.Journal.Active(ChangeKinds.Dns).Count == 0)
            Dns.ForgetBackup();
    }

    string Kind(UndoItem i) => shell.Journal.Entries().FirstOrDefault(e => e.Id == i.Id)?.Kind ?? "";

    // ---------- системный прокси ----------

    /// <summary>Реестр и WinHTTP; в тестах — подмена в памяти (настоящие настройки машины не трогаются).</summary>
    public IProxyRegistry ProxyRegistry { get; init; } = new WindowsProxyRegistry();
    public IWinHttpProxy WinHttp { get; init; } = new WindowsWinHttpProxy();

    ProxyManager ProxyMgr() => new(ProxyRegistry, WinHttp) { Recorder = shell.Journal, Backup = new ProxyBackupStore(shell.Paths.ProxyBackup) };

    public ProxyStatusInfo ProxyStatus(string? clientSid)
    {
        var sid = ProxySid.IsUserSid(clientSid) && ProxyRegistry.UserHiveLoaded(clientSid!) ? clientSid : null;
        return new(ProxyMgr().Overview(sid), new ProxyBackupStore(shell.Paths.ProxyBackup).Load());
    }

    ProxyChangeResult ProxyResult(ProxyOutcome o, string? sid) => new(o.Ok, o.Message, ProxyStatus(sid));

    public ProxyChangeResult ProxyUserSet(string? clientSid, bool enabled, string? server, string? bypass, string? pac)
    {
        if (!ProxySid.IsUserSid(clientSid))
            return new(false, L.T("core.helper.proxy_no_user"), ProxyStatus(null));
        shell.Paths.EnsureDirectories();
        var mgr = ProxyMgr();
        var cur = ProxyRegistryIo.Read(ProxyRegistry, ProxyKey.UserSettings, clientSid);
        var next = enabled ? ProxyValues.Build(server, bypass, pac) : cur.Cleared();
        return ProxyResult(mgr.ApplyUser(clientSid, next, ChangeKinds.ProxyUserAdmin), clientSid);
    }

    public ProxyChangeResult ProxyMachineSet(bool enabled, string? server, string? bypass, string? pac, bool winhttp)
    {
        shell.Paths.EnsureDirectories();
        var mgr = ProxyMgr();
        var cur = ProxyRegistryIo.Read(ProxyRegistry, ProxyKey.MachineSettings);
        var next = enabled ? ProxyValues.Build(server, bypass, pac) : cur.Cleared();
        return ProxyResult(mgr.ApplyMachine(next, winhttp ? WinHttpInfo.From(next) : null), null);
    }

    public ProxyChangeResult ProxyMachineRestore()
    {
        shell.Paths.EnsureDirectories();
        return ProxyResult(ProxyMgr().RestoreMachine(), null);
    }

    public DnsStatusInfo DnsStatus() => Dns.Status();

    public Task<DnsSetResult> DnsSetAsync(string preset, IReadOnlyList<string>? ips, string? doh, bool fallback, CancellationToken ct)
    {
        shell.Paths.EnsureDirectories();
        return Dns.SetAsync(preset, ips, doh, fallback, ct);
    }

    public Task<DnsSetResult> DnsRestoreAsync(CancellationToken ct) => Dns.RestoreAsync(ct);
}
