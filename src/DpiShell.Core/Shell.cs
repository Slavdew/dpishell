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

    public void Enable(string strategyId)
    {
        EnsureCoreIntact();
        var s = Catalog.Find(strategyId) ?? throw new ArgumentException($"Нет стратегии {strategyId}");
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

    /// <summary>Подбор. Служба на время подбора останавливается и потом возвращается в прежнее состояние.</summary>
    public async Task<SelectionReport> SelectAsync(bool quick, IReadOnlyList<TargetGroup>? groups = null,
        IProgress<SelectorProgress>? progress = null, CancellationToken ct = default)
    {
        EnsureCoreIntact();
        var foreign = Conflicts.Find(Paths);
        if (foreign.Count > 0)
            throw new InvalidOperationException("Мешают другие программы обхода: " + string.Join("; ", foreign.Select(c => $"{c.Name} ({c.Detail})")));

        bool wasRunning = Service.State == SvcState.Running;
        if (wasRunning) Service.Stop();
        try
        {
            var selector = new Selector(Paths, new HttpProbe());
            var report = await selector.RunAsync(Catalog.Strategies, groups ?? SelectedGroups(), quick, progress, ct);
            ResultStore.Save(Paths, report);
            return report;
        }
        finally
        {
            if (wasRunning && State.Enabled && State.StrategyId is not null)
                ServiceManager.Sc("start", ServiceManager.Name);
        }
    }

    /// <summary>«Проверить сейчас»: работает ли то, что включено.</summary>
    public async Task<IReadOnlyList<TargetVerdict>> CheckNowAsync(CancellationToken ct = default) =>
        await new Selector(Paths, new HttpProbe()).CheckGroupsAsync(SelectedGroups(), ct);

    public void SaveState() => State.Save(Paths);
}
