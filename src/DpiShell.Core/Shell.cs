using System.Security.Principal;

namespace DpiShell.Core;

/// <summary>Точка входа для окна и консоли: всё, что делает пользователь, — здесь.</summary>
public sealed class Shell
{
    public AppPaths Paths { get; }
    public DataStore Data { get; }
    public CoreInstaller Installer { get; }
    public ServiceManager Service { get; }
    public AppState State { get; private set; }
    public StrategyCatalog Catalog { get; }
    public TargetSet DefaultTargets { get; }

    public Shell(AppPaths? paths = null)
    {
        Paths = paths ?? new AppPaths();
        Data = new DataStore(Paths);
        Installer = new CoreInstaller(Paths, CoreManifest.Load(Path.Combine(Paths.Seed, "core.json")));
        Service = new ServiceManager(Paths);
        Catalog = StrategyCatalog.Load(Path.Combine(Paths.Seed, "strategies.json"));
        DefaultTargets = TargetSet.Load(Path.Combine(Paths.Seed, "targets.json"));
        State = AppState.Load(Paths);
    }

    public static bool IsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static HttpClient CreateDownloadClient() =>
        new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>Разложить данные и при необходимости поставить ядро. Возвращает итог проверки ядра.</summary>
    public async Task<CoreCheck> PrepareAsync(IProgress<string>? log = null, CancellationToken ct = default)
    {
        Data.Seed();
        var check = Installer.Check();
        if (check.State == CoreState.Missing)
        {
            using var http = CreateDownloadClient();
            await Installer.InstallAsync(http, log, ct);
            check = Installer.Check();
        }
        return check;
    }

    public async Task RepairCoreAsync(IProgress<string>? log = null, CancellationToken ct = default)
    {
        using var http = CreateDownloadClient();
        await Installer.InstallAsync(http, log, ct);
    }

    public IReadOnlyList<TargetGroup> SelectedGroups(IEnumerable<string>? ids = null)
    {
        var want = (ids ?? State.Groups).ToHashSet();
        var groups = DefaultTargets.Groups.Where(g => want.Contains(g.Id)).ToList();
        if (want.Contains("user") && State.UserTargets.Count > 0) groups.Add(TargetSet.UserGroup(State.UserTargets));
        return groups;
    }

    void EnsureCoreIntact()
    {
        var c = Installer.Check();
        if (c.State != CoreState.Ok)
            throw new SecurityException(c.State == CoreState.Missing
                ? "Файлы ядра не найдены. Возможно, их удалил антивирус."
                : $"Файлы ядра изменены: {string.Join(", ", c.BadFiles)}. Возможно, их тронул антивирус — восстановите ядро.");
        // Чужая dll или exe рядом с winws может быть подхвачена при запуске.
        var foreign = c.ExtraFiles.Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToList();
        if (foreign.Count > 0)
            throw new SecurityException($"В папке ядра посторонние файлы: {string.Join(", ", foreign)}. Восстановите ядро.");
    }

    void ThrowIfConflicts()
    {
        var foreign = Conflicts.Find(Paths, AllStrategies);
        if (foreign.Count > 0) throw new ConflictException(foreign);
    }

    /// <summary>
    /// Что на самом деле стоит в службе Windows (её настройки, а не наша память): наше ли ядро и какая стратегия.
    /// Если стратегия в службе не та, что в state.json (state потерян, программу перенесли), — верим службе.
    /// </summary>
    public ServiceInfo InspectService()
    {
        var state = Service.State;
        if (state == SvcState.NotInstalled) return new(state, null, null, false);
        var bin = ServiceManager.QueryBinPath(ServiceManager.Name);
        var (exe, args) = ZapretDetector.SplitCommandLine(bin);
        var strategy = ZapretDetector.Match(args, AllStrategies);
        bool ours = exe is not null && exe.Equals(Paths.Winws, StringComparison.OrdinalIgnoreCase);
        if (ours && strategy is not null && State.StrategyId != strategy.Id)
        {
            State.StrategyId = strategy.Id;
            State.Enabled = state == SvcState.Running;
            State.Save(Paths);
        }
        return new(state, exe, strategy, ours);
    }

    public void Enable(string strategyId)
    {
        EnsureCoreIntact();
        ThrowIfConflicts();
        var s = FindStrategy(strategyId) ?? throw new ArgumentException($"Нет стратегии {strategyId}");
        Service.Apply(WinwsArgs.Build(s, Paths));
        State.StrategyId = s.Id;
        State.Enabled = true;
        State.Save(Paths);
    }

    public void Disable()
    {
        Service.Disable();
        State.Enabled = false;
        State.Save(Paths);
    }

    /// <summary>Готовые стратегии + найденные глубоким поиском (user-strategies.json, та же проверка).</summary>
    public IReadOnlyList<Strategy> AllStrategies => [.. Catalog.Strategies, .. UserStrategies.Load(Paths)];

    public Strategy? FindStrategy(string id) => AllStrategies.FirstOrDefault(s => s.Id == id);

    /// <summary>Подбор. Служба на время подбора останавливается и потом возвращается в прежнее состояние.</summary>
    public Task<SelectionReport> SelectAsync(bool quick, IReadOnlyList<TargetGroup>? groups = null,
        IProgress<SelectorProgress>? progress = null, CancellationToken ct = default) =>
        WithServicePausedAsync(async () =>
        {
            using var doh = new DohResolver();
            var report = await new Selector(Paths, NewProbe(doh)).RunAsync(
                StrategyStats.Order(AllStrategies, StrategyStats.Load(Paths)), groups ?? SelectedGroups(), quick, progress, ct);
            ResultStore.Save(Paths, report);
            return report;
        });

    /// <summary>Глубокий поиск (ТЗ §4.7) от базовой стратегии; найденное, что прошло полную проверку, сохраняется как свои стратегии.</summary>
    public Task<SearchReport> SearchAsync(string? baseId = null, IReadOnlyList<TargetGroup>? groups = null,
        IProgress<SelectorProgress>? progress = null, CancellationToken ct = default) =>
        WithServicePausedAsync(async () =>
        {
            var baseStrategy = FindStrategy(baseId ?? State.StrategyId ?? "alt9") ?? Catalog.Strategies[0];
            using var doh = new DohResolver();
            var report = await new StrategySearch(Paths, () => NewProbe(doh)).RunAsync(baseStrategy, groups ?? SelectedGroups(), progress, ct);
            if (report.Final is { } f)
            {
                ResultStore.Save(Paths, f);
                var good = f.Scores.Where(x => x.Strategy.Source == "search" && x.Error is null && x.GroupsPassed > 0).Select(x => x.Strategy).ToList();
                if (good.Count > 0) UserStrategies.Add(Paths, good);
            }
            return report;
        });

    async Task<T> WithServicePausedAsync<T>(Func<Task<T>> work)
    {
        EnsureCoreIntact();
        ThrowIfConflicts();

        bool wasRunning = Service.State == SvcState.Running;
        if (wasRunning) Service.Stop();
        try { return await work(); }
        finally
        {
            if (wasRunning && State.Enabled && State.StrategyId is not null)
                ServiceManager.Sc("start", ServiceManager.Name);
        }
    }

    /// <summary>«Проверить сейчас»: работает ли то, что включено.</summary>
    public async Task<IReadOnlyList<TargetVerdict>> CheckNowAsync(CancellationToken ct = default)
    {
        using var doh = new DohResolver();
        return await new Selector(Paths, NewProbe(doh)).CheckGroupsAsync(SelectedGroups(), ct);
    }

    /// <summary>Проверка резолвит цели через DoH: результат зависит от DPI, а не от DNS системы. Кэш DoH живёт один прогон.</summary>
    public static HttpProbe NewProbe(DohResolver doh) => new() { Resolver = doh };

    /// <summary>Подменяет ли DNS системы адреса проверяемых сайтов (для предупреждения и анализа DNS).</summary>
    public async Task<IReadOnlyList<DnsComparison>> CheckDnsAsync(CancellationToken ct = default)
    {
        using var doh = new DohResolver();
        var hosts = SelectedGroups().SelectMany(g => g.Targets).Select(t => new Uri(t.Url).Host).Distinct().ToList();
        return await Task.WhenAll(hosts.Select(h => DnsCheck.CompareAsync(h, doh, ct)));
    }

    public void SaveState() => State.Save(Paths);
}

/// <summary>Работают другие программы обхода (Flowseal, GoodbyeDPI…): подбор с ними бессмысленен.</summary>
public sealed class ConflictException(IReadOnlyList<Conflict> conflicts)
    : InvalidOperationException("Мешают другие программы обхода: " + string.Join("; ", conflicts.Select(c => $"{c.Name} ({c.Detail})")))
{
    public IReadOnlyList<Conflict> Conflicts { get; } = conflicts;
}

/// <summary>Служба DpiShell по её настройкам в Windows. OurCore = false — служба с нашим именем запускает чужой файл.</summary>
public sealed record ServiceInfo(SvcState State, string? ExePath, Strategy? Strategy, bool OurCore);
