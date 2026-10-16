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
    Task<HostsCheckReport> HostsCheckAsync(IProgress<HostsCheckProgress> progress, CancellationToken ct);
    /// <summary>Предпросмотр по последней проверке (что будет записано); проверки не было — предпросмотр снятия блока.</summary>
    HostsPreview HostsPreview();
    /// <summary>Записать только то, что последняя проверка признала нужным. Окно не передаёт адреса — непроверенное не запишется.</summary>
    HostsWriteResult HostsApply();
    HostsWriteResult HostsRevert();
    Task<HostsRecheckResult> HostsRecheckAsync(CancellationToken ct);
    StopForeignResult StopForeign();
    Task CoreRepairAsync(IProgress<string> log, CancellationToken ct);
    void RemoveAll();
    int ClearHistory();
    ResultsSnapshot ReadResults();
    /// <summary>Сведения из закрытой папки для окна без прав: hosts, свои подсети, ядро, найденные стратегии.</summary>
    HelperDetails Details();
    /// <summary>Выбрать стратегию без включения обхода (обход включён — окно зовёт enable).</summary>
    void Choose(string strategyId);
    /// <summary>Включить временные метки TCP (их требует стратегия роутера, 07-router-api §6). true — включены.</summary>
    bool TcpTimestampsOn();
    /// <summary>Все окна отключились от канала: в режиме без службы winws гасится.</summary>
    void OnClientsGone();
}

/// <summary>Ответ на status: всё, что окну без прав нужно для экранов.</summary>
public sealed record HelperStatus(
    bool Enabled, bool Active, string RunMode, string? StrategyId,
    SvcState Service, bool ServiceOurCore, string? ServiceStrategy,
    CoreState Core, NetFeatures Features,
    IReadOnlyList<StrategyInfo> Strategies, IReadOnlyList<GroupInfo> Groups,
    IReadOnlyList<string> SelectedGroups, IReadOnlyList<string> UserTargets, IReadOnlyList<string> Fallbacks,
    bool TimestampsEnabledByUs, bool HostsFix, IReadOnlyList<Conflict> Foreign,
    string HelperVersion);

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
/// <param name="Results">Последние результаты подборов (свои адреса в них уже скрыты — ResultStore).</param>
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
public sealed class ShellHelperBackend(Shell shell) : IHelperBackend
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
            [.. shell.DefaultTargets.Groups.Select(g => new GroupInfo(g.Id, g.Title, g.Targets.Count)), new GroupInfo("user", "Свои адреса", st.UserTargets.Count)],
            st.Groups, st.UserTargets, st.Fallbacks,
            st.TimestampsEnabledByUs, st.HostsFix, foreign,
            HelperProtocol.AppVersion);
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
        return TcpTimestamps.IsEnabled() == true;
    }
    public void SetRunMode(bool service) => shell.SetRunMode(service);

    public void Choose(string strategyId)
    {
        shell.State.StrategyId = strategyId;
        WatchdogPolicy.Applied(shell.State, strategyId);
        shell.SaveState();
    }

    public async Task<SelectionReport> SelectAsync(bool quick, IReadOnlyList<string>? groups, IProgress<SelectorProgress> progress, CancellationToken ct)
    {
        RememberGroups(groups);
        var r = await shell.SelectAsync(quick, groups is null ? null : shell.SelectedGroups(groups), progress, ct);
        RememberFallbacks(r);
        return r;
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

    /// <summary>Как «Сохранить» своих адресов в окне: файл доменов, список целей, группа «Свои» включается.</summary>
    public UserTargetsResult SetUserTargets(IReadOnlyList<string> lines)
    {
        shell.Paths.EnsureDirectories();
        var trimmed = lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var rejected = shell.Data.WriteUserDomains(trimmed);
        var good = trimmed.Where(l => DataStore.NormalizeDomain(l) is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        shell.State.UserTargets = good;
        if (good.Count > 0 && !shell.State.Groups.Contains("user")) shell.State.Groups.Add("user");
        shell.SaveState();
        return new(good, rejected);
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
        lock (_hostsLock) r = _lastHostsCheck ?? throw new InvalidOperationException("Сначала проверьте адреса.");
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
        shell.State.Enabled = false;
        shell.SaveState();
    }

    public int ClearHistory() => ResultStore.ClearHistory(shell.Paths);

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
    }
}
