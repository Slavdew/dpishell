using System.Security.Principal;

namespace DpiShell.Core;

/// <summary>Точка входа для окна и консоли: всё, что делает пользователь, — здесь.</summary>
public sealed class Shell
{
    public AppPaths Paths { get; }
    public DataStore Data { get; }
    public CoreInstaller Installer { get; private set; }
    public ServiceManager Service { get; }
    public AppState State { get; private set; }
    public StrategyCatalog Catalog { get; private set; }
    public TargetSet DefaultTargets { get; private set; }
    /// <summary>«Починить адреса» — системный hosts (architecture.md «Починить адреса»); в тестах — копия во временной папке.</summary>
    public HostsManager Hosts { get => _hosts ??= new HostsManager(Paths); set => _hosts = value; }
    HostsManager? _hosts;
    /// <summary>IP-списки (architecture.md «IP-списки и игровой режим»): общий из data.json, свои подсети, валидатор.</summary>
    public IpLists IpLists => _ipLists ??= new IpLists(Paths);
    IpLists? _ipLists;

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
        if (IsAdmin()) Paths.Harden();   // свои адреса и ядро — только системе и администраторам
        var check = Installer.Check();
        if (check.State == CoreState.Missing)
        {
            using var http = CreateDownloadClient();
            await Installer.InstallAsync(http, log, ct);
            check = Installer.Check();
        }
        if (check.State == CoreState.Ok && IsAdmin())
        {
            // Программу закрыли во время «Все адреса» — срок вышел, вернуть; служба после обновления программы — на новые аргументы.
            try { if (!RevertExpiredAllAddresses()) RefreshServiceIfOutdated(); }
            catch (Exception e) when (e is InvalidOperationException or SecurityException or IOException) { log?.Report(e.Message); }
        }
        return check;
    }

    public async Task RepairCoreAsync(IProgress<string>? log = null, CancellationToken ct = default)
    {
        using var http = CreateDownloadClient();
        await Installer.InstallAsync(http, log, ct);
    }

    /// <summary>Источники обновлений данных — только из поставки (architecture.md «Обновления данных»).</summary>
    public UpdateSources UpdateSources => UpdateSources.Load(Path.Combine(Paths.Package, UpdateSources.FileName));

    /// <summary>Версия данных в работе (обновлённых или поставки).</summary>
    public string? DataVersion => Paths.SeedManifest?.Version;

    /// <summary>
    /// Поставить обновление данных по data.json с источников из списка (architecture.md «Обновления данных»). Новое ядро в манифесте —
    /// ставится по нему же до замены данных; затем каталог, цели и списки перечитываются, свои файлы не трогаются.
    /// </summary>
    public async Task<DataUpdateResult> UpdateDataAsync(bool confirmed, Func<CoreManifest, Task>? installCore, IProgress<string>? log = null,
        CancellationToken ct = default, HttpClient? http = null)
    {
        var own = http is null;
        http ??= new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) }) { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            var updater = new DataUpdater(Paths, new DataUpdateClient(http, UpdateSources)) { InstallCore = installCore };
            var r = await updater.InstallAsync(confirmed, log, ct);
            if (r.Applied) ReloadData();
            return r;
        }
        finally { if (own) http.Dispose(); }
    }

    /// <summary>Перечитать данные (после обновления): каталог, цели, манифест ядра; стандартные списки — в lists\ и fake\.</summary>
    public void ReloadData()
    {
        Paths.ResetSeed();
        Installer = new CoreInstaller(Paths, CoreManifest.Load(Path.Combine(Paths.Seed, "core.json")));
        Catalog = StrategyCatalog.Load(Path.Combine(Paths.Seed, "strategies.json"));
        DefaultTargets = TargetSet.Load(Path.Combine(Paths.Seed, "targets.json"));
        Data.Seed();
    }

    /// <summary>Поставить ядро по другому манифесту (новое из обновления данных) — тем же установщиком, по хешам.</summary>
    public async Task InstallCoreAsync(CoreManifest manifest, IProgress<string>? log = null, CancellationToken ct = default)
    {
        using var http = CreateDownloadClient();
        await new CoreInstaller(Paths, manifest).InstallAsync(http, log, ct);
    }

    public IReadOnlyList<TargetGroup> SelectedGroups(IEnumerable<string>? ids = null)
    {
        var want = (ids ?? State.Groups).ToHashSet();
        var groups = DefaultTargets.Groups.Where(g => want.Contains(g.Id)).ToList();
        if (want.Contains("user") && State.UserTargets.Count > 0) groups.Add(TargetSet.UserGroup(State.UserTargets));
        return groups;
    }

    /// <summary>
    /// Для подбора и поиска: выбранные группы + «Обычные сайты». Контрольную группу не выбирают — это страховка «не сломать»,
    /// она идёт в каждый подбор (без этого подбор её не проверял вовсе).
    /// </summary>
    public static IReadOnlyList<TargetGroup> WithControl(IReadOnlyList<TargetGroup> groups, TargetSet all) =>
        [.. groups.Where(g => !g.Control), .. all.Groups.Where(g => g.Control)];

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

    /// <summary>
    /// Остановить чужие обходы (Flowseal и т. п.), чтобы не мешали: процессы завершаются, службы останавливаются,
    /// но не удаляются — чужие настройки не трогаем. Возвращает, что остановили, и службы, которые вернутся после перезагрузки.
    /// </summary>
    public (IReadOnlyList<string> Stopped, IReadOnlyList<string> ComeBackOnReboot) StopForeign()
    {
        var stopped = new List<string>(); var comeBack = new List<string>();
        var found = ZapretDetector.Find(Paths, AllStrategies).Where(z => z.Owner != ZapretOwner.DpiShell).ToList();
        foreach (var z in found.Where(z => z.Kind == "service"))
        {
            ServiceManager.Sc("stop", z.Name);
            ServiceManager.WaitFor(z.Name, SvcState.Stopped, TimeSpan.FromSeconds(10));
            stopped.Add(z.OwnerTitle + $" (служба {z.Name})");
            comeBack.Add(z.Name);
        }
        foreach (var z in found.Where(z => z.Kind == "process"))
        {
            if (!int.TryParse(z.Name.Replace("PID ", ""), out var pid)) continue;
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(pid);
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
                stopped.Add(z.OwnerTitle);
            }
            catch (ArgumentException) { }            // уже завершился
            catch (InvalidOperationException) { }
        }
        return (stopped, comeBack);
    }

    // ---------- режимы работы: служба Windows или процесс программы ----------

    WinwsProcess? _proc;

    public bool ServiceMode => State.RunMode != "process";

    /// <summary>Обход сейчас работает — в своём режиме (служба запущена или наш процесс winws жив).</summary>
    public bool IsActive => ServiceMode ? Service.State == SvcState.Running : _proc is { Alive: true };

    /// <summary>Временные метки TCP нужны стратегиям с fooling=ts; выключены — включаем и запоминаем, что это сделали мы.</summary>
    public void EnsureTcpTimestamps()
    {
        if (TcpTimestamps.IsEnabled() != false) return;
        if (TcpTimestamps.Set(true))
        {
            State.TimestampsEnabledByUs = true;
            State.Save(Paths);
        }
    }

    /// <summary>«Удалить полностью»: вернуть временные метки, если включали мы.</summary>
    public void RestoreTcpTimestamps()
    {
        if (!State.TimestampsEnabledByUs) return;
        if (TcpTimestamps.Set(false))
        {
            State.TimestampsEnabledByUs = false;
            State.Save(Paths);
        }
    }

    public void Enable(string strategyId)
    {
        EnsureCoreIntact();
        ThrowIfConflicts();
        EnsureTcpTimestamps();
        var s = FindStrategy(strategyId) ?? throw new ArgumentException($"Нет стратегии {strategyId}");
        // Список «По списку» удалён/изменён антивирусом — вернуть из поставки, иначе winws не стартует без файла.
        if (CurrentOptions.Ipset == IpsetMode.List && !IpLists.HasList) IpLists.SeedFromPackage();
        var args = WinwsArgs.Build(s, Paths, CurrentOptions);
        if (ServiceMode)
        {
            StopProcess();
            Service.Apply(args);
        }
        else
        {
            // Без службы: служба (если стояла) не должна стартовать с Windows и держать WinDivert.
            if (Service.State != SvcState.NotInstalled) Service.Disable();
            StartProcess(args);
        }
        State.StrategyId = s.Id;
        State.Enabled = true;
        State.Save(Paths);
    }

    public void Disable()
    {
        if (ServiceMode) Service.Disable(); else StopProcess();
        State.Enabled = false;
        State.Save(Paths);
    }

    /// <summary>Переключить режим. Обход был включён — продолжает работать в новом режиме на той же стратегии.</summary>
    public void SetRunMode(bool service)
    {
        var mode = service ? "service" : "process";
        if (State.RunMode == mode) return;
        bool wasOn = State.Enabled && IsActive && State.StrategyId is not null;
        if (ServiceMode) Service.Disable(); else StopProcess();
        State.RunMode = mode;
        State.Save(Paths);
        if (wasOn) Enable(State.StrategyId!);
    }

    /// <summary>Режим без службы: при старте программы вернуть обход, если он был включён.</summary>
    public void ResumeIfNeeded()
    {
        if (!ServiceMode && State.Enabled && State.StrategyId is { } id && !IsActive) Enable(id);
    }

    void StartProcess(IReadOnlyList<string> args)
    {
        StopProcess();
        var p = WinwsProcess.Start(Paths, args);
        // winws с ошибкой в параметрах или без доступа к WinDivert завершается сразу.
        Thread.Sleep(1000);
        if (!p.Alive)
        {
            var output = p.Output;
            p.Dispose();
            throw new InvalidOperationException("winws не запустился: " + (output.Length > 300 ? output[^300..] : output).Trim());
        }
        _proc = p;
    }

    /// <summary>
    /// Помощник: окно отключилось от канала (или помощник останавливается) — winws режима «без службы» гасится.
    /// state.json не меняется: при следующем подключении окно вернёт обход (как <see cref="ResumeIfNeeded"/>).
    /// </summary>
    public void StopOwnProcess() => StopProcess();

    void StopProcess()
    {
        _proc?.Dispose();
        _proc = null;
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
                StrategyStats.Order(AllStrategies, StrategyStats.LoadDetailed(Paths)), WithControl(groups ?? SelectedGroups(), DefaultTargets), quick, progress, ct);
            ResultStore.Save(Paths, report);
            return report;
        });

    /// <summary>Глубокий поиск (architecture.md «Глубокий поиск») от базовой стратегии; найденное, что прошло полную проверку, сохраняется как свои стратегии.</summary>
    public Task<SearchReport> SearchAsync(string? baseId = null, IReadOnlyList<TargetGroup>? groups = null,
        IProgress<SelectorProgress>? progress = null, CancellationToken ct = default) =>
        WithServicePausedAsync(async () =>
        {
            var baseStrategy = FindStrategy(baseId ?? State.StrategyId ?? "alt9") ?? Catalog.Strategies[0];
            using var doh = new DohResolver();
            var report = await new StrategySearch(Paths, () => NewProbe(doh)).RunAsync(baseStrategy, WithControl(groups ?? SelectedGroups(), DefaultTargets), progress, ct);
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
        EnsureTcpTimestamps();   // иначе подбор отбросит стратегии с fooling=ts как нерабочие

        bool serviceWasRunning = Service.State == SvcState.Running;
        bool processWasRunning = _proc is { Alive: true };
        if (serviceWasRunning) Service.Stop();
        if (processWasRunning) StopProcess();
        try { return await work(); }
        finally
        {
            if (State.Enabled && State.StrategyId is { } id)
            {
                if (serviceWasRunning) ServiceManager.Sc("start", ServiceManager.Name);
                if (processWasRunning && FindStrategy(id) is { } s)
                    try { StartProcess(WinwsArgs.Build(s, Paths, CurrentOptions)); } catch (InvalidOperationException) { }
            }
        }
    }

    /// <summary>«Проверить сейчас»: работает ли то, что включено.</summary>
    public async Task<IReadOnlyList<TargetVerdict>> CheckNowAsync(CancellationToken ct = default)
    {
        return await CheckGroupsAsync(SelectedGroups(), ct);
    }

    /// <summary>
    /// Проверка целей по DoH и, где DNS системы даёт другой адрес, — ещё и по пути браузера (<see cref="BrowserCheck"/>).
    /// Для «Проверить сейчас» и проверки после «Включить»; подбор оценивается без этого.
    /// </summary>
    public async Task<IReadOnlyList<TargetVerdict>> CheckGroupsAsync(IReadOnlyList<TargetGroup> groups, CancellationToken ct = default)
    {
        using var doh = new DohResolver();
        var verdicts = await new Selector(Paths, NewProbe(doh)).CheckGroupsAsync(groups, ct);
        return await BrowserCheck.AddAsync(verdicts, SystemResolver.Instance, doh,
            (t, r, token) => new HttpProbe { Resolver = r }.CheckAsync(t, HttpProbe.DefaultTls, token), BrowserCheck.SystemHosts(), ct);
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

    // ---------- IP-списки и игровой режим (architecture.md «IP-списки и игровой режим») ----------

    /// <summary>С чем собирается командная строка сейчас. «Все адреса» с истёкшим сроком уже не действует.</summary>
    public BuildOptions CurrentOptions => BuildOptions.From(State, DateTime.UtcNow);

    /// <summary>Состояние для экранов «IP-списки» и «Игровой режим».</summary>
    public NetFeatures Features
    {
        get
        {
            var o = CurrentOptions;
            var game = BuildOptions.ParseGame(State.GameMode);
            return new(o.Ipset, o.Ipset == IpsetMode.Any ? State.IpsetAnyUntil : null,
                o.Ipset == IpsetMode.Any ? BuildOptions.ParseIpset(State.IpsetModeBeforeAny) : o.Ipset,
                game, o.GameTcp || o.GameUdp, game != GameMode.Off && o.Ipset == IpsetMode.Any, IpLists.Meta);
        }
    }

    /// <summary>
    /// «Выкл» или «По списку». «Все адреса» — через <see cref="StartAllAddresses"/>. «Выкл» выключает и игровой режим:
    /// без списка ему нечего ловить. Обход включён — перезапускается на той же стратегии.
    /// </summary>
    public void SetIpsetMode(IpsetMode mode)
    {
        if (mode == IpsetMode.Any) { StartAllAddresses(); return; }
        if (mode == IpsetMode.List && !IpLists.HasList && !IpLists.SeedFromPackage())
            throw new InvalidOperationException("IP-список не загружен — обновите его.");
        if (CurrentOptions.Ipset == mode && State.IpsetMode != "any") return;
        State.IpsetMode = BuildOptions.Format(mode);
        State.IpsetAnyUntil = null;
        if (mode == IpsetMode.Off) State.GameMode = BuildOptions.Format(GameMode.Off);
        State.Save(Paths);
        ReapplyIfActive();
    }

    /// <summary>«Все адреса — на 10 минут»: потом сам возврат к прежнему режиму. Повторный вызов продлевает срок.</summary>
    public void StartAllAddresses()
    {
        var current = CurrentOptions.Ipset;
        if (current != IpsetMode.Any) State.IpsetModeBeforeAny = BuildOptions.Format(current);
        State.IpsetMode = BuildOptions.Format(IpsetMode.Any);
        State.IpsetAnyUntil = DateTime.UtcNow + IpLists.AnyDuration;
        State.Save(Paths);
        ReapplyIfActive();
    }

    /// <summary>Вернуть прежний режим сейчас (и при выходе из программы, если пользователь согласился). False — «Все адреса» не включены.</summary>
    public bool StopAllAddresses()
    {
        if (State.IpsetMode != BuildOptions.Format(IpsetMode.Any)) return false;
        var back = BuildOptions.ParseIpset(State.IpsetModeBeforeAny);
        State.IpsetMode = BuildOptions.Format(back == IpsetMode.Any ? IpsetMode.Off : back);
        State.IpsetAnyUntil = null;
        State.Save(Paths);
        ReapplyIfActive();   // служба запущена с пустым списком — без перезапуска она так и ловила бы всё
        return true;
    }

    /// <summary>Для таймера окна (раз в 10–30 с) и старта программы: срок «Все адреса» вышел — вернуть. True — вернули (показать уведомление).</summary>
    public bool RevertExpiredAllAddresses() =>
        State.IpsetMode == BuildOptions.Format(IpsetMode.Any) && (State.IpsetAnyUntil is not { } until || until <= DateTime.UtcNow)
        && StopAllAddresses();

    /// <summary>
    /// Игровой режим. Окно передаёт только Off/TcpUdp. При включении без загруженного списка IP-список включается сам
    /// (если файла нет — ставится из поставки или скачивается по манифесту); при «Все адреса» — запрещено.
    /// </summary>
    public async Task<GameModeChange> SetGameModeAsync(GameMode mode, IProgress<string>? log = null, CancellationToken ct = default)
    {
        if (BuildOptions.ParseGame(State.GameMode) == mode) return new(false, null);
        string? note = null;
        if (mode != GameMode.Off)
        {
            var ipset = CurrentOptions.Ipset;
            if (ipset == IpsetMode.Any)
                throw new InvalidOperationException("Игровой режим недоступен, пока включены «Все адреса».");
            if (ipset == IpsetMode.Off || !IpLists.HasList)
            {
                if (!IpLists.HasList && !IpLists.SeedFromPackage())
                {
                    using var http = CreateDownloadClient();
                    var r = await IpLists.UpdateFromManifestAsync(http, null, false, log, ct);
                    if (!r.Applied) throw new InvalidOperationException(r.Message);
                }
                if (ipset == IpsetMode.Off)
                {
                    State.IpsetMode = BuildOptions.Format(IpsetMode.List);
                    State.IpsetAnyUntil = null;
                    note = "Включён IP-список: игровой режим работает по нему.";
                }
            }
        }
        State.GameMode = BuildOptions.Format(mode);
        State.Save(Paths);
        ReapplyIfActive();
        return new(note is not null, note);
    }

    /// <summary>Обновить общий IP-список из файла. Перезапуск не нужен: winws перечитывает список сам.</summary>
    public IpsetUpdateResult UpdateIpsetFromFile(string file, bool confirmed = false) => IpLists.UpdateFromFile(file, confirmed);

    /// <summary>Обновить общий IP-список по манифесту (sha256 из data.json поставки).</summary>
    public async Task<IpsetUpdateResult> UpdateIpsetFromManifestAsync(bool confirmed = false, IProgress<string>? log = null, CancellationToken ct = default)
    {
        using var http = CreateDownloadClient();
        return await IpLists.UpdateFromManifestAsync(http, null, confirmed, log, ct);
    }

    /// <summary>Свои подсети / исключения (одна на строку). Ошибочные строки — в Problems; широкие — только с confirmed.</summary>
    public IpsetVerdict WriteUserIpset(IEnumerable<string> lines, bool exclude = false, bool confirmed = false) =>
        IpLists.WriteUser(lines, exclude, confirmed);

    /// <summary>Изменились режимы — работающий обход перезапускается на той же стратегии (как Enable).</summary>
    void ReapplyIfActive()
    {
        // Сначала state: выключенный обход не трогаем и не опрашиваем службу.
        if (!State.Enabled || State.StrategyId is not { } id) return;
        if (IsActive) Enable(id);
    }

    /// <summary>Служба работает со старой командной строкой (обновилась программа или режимы) — переставить. True — переставили.</summary>
    public bool RefreshServiceIfOutdated()
    {
        if (!ServiceMode || !State.Enabled || State.StrategyId is not { } id || FindStrategy(id) is not { } s) return false;
        if (Service.State != SvcState.Running) return false;
        var (exe, args) = ZapretDetector.SplitCommandLine(ServiceManager.QueryBinPath(ServiceManager.Name));
        if (exe is null || !exe.Equals(Paths.Winws, StringComparison.OrdinalIgnoreCase)) return false;
        if (args.SequenceEqual(WinwsArgs.Build(s, Paths, CurrentOptions))) return false;
        Enable(id);
        return true;
    }

    public void SaveState() => State.Save(Paths);
}

/// <param name="Ipset">Действующий режим IP-списка.</param>
/// <param name="AnyUntilUtc">Когда «Все адреса» вернутся сами (только при Ipset = Any).</param>
/// <param name="ReturnTo">Куда вернутся «Все адреса» (иначе = Ipset).</param>
/// <param name="Game">Выбранный игровой режим.</param>
/// <param name="GameActive">Игровые профили реально в командной строке.</param>
/// <param name="GameBlocked">Игровой режим выбран, но не действует из-за «Все адреса» (переключатель серый).</param>
/// <param name="List">Установленный общий список: подсети, доля IPv4, дата; null — не установлен.</param>
public sealed record NetFeatures(IpsetMode Ipset, DateTime? AnyUntilUtc, IpsetMode ReturnTo, GameMode Game, bool GameActive, bool GameBlocked, IpsetMeta? List);

/// <param name="IpsetSwitched">IP-список включён автоматически — показать <paramref name="Note"/> одной строкой.</param>
public sealed record GameModeChange(bool IpsetSwitched, string? Note);

/// <summary>Работают другие программы обхода (Flowseal, GoodbyeDPI…): подбор с ними бессмысленен.</summary>
public sealed class ConflictException(IReadOnlyList<Conflict> conflicts)
    : InvalidOperationException("Мешают другие программы обхода: " + string.Join("; ", conflicts.Select(c => $"{c.Name} ({c.Detail})")))
{
    public IReadOnlyList<Conflict> Conflicts { get; } = conflicts;
}

/// <summary>Служба DpiShell по её настройкам в Windows. OurCore = false — служба с нашим именем запускает чужой файл.</summary>
public sealed record ServiceInfo(SvcState State, string? ExePath, Strategy? Strategy, bool OurCore);
