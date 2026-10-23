using DpiShell.Core.Localization;
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
    public HostsManager Hosts
    {
        get => _hosts ??= new HostsManager(Paths) { Recorder = Journal };
        set { _hosts = value; value.Recorder = Journal; }
    }
    HostsManager? _hosts;
    /// <summary>IP-списки (architecture.md «IP-списки и игровой режим»): общий из data.json, свои подсети, валидатор.</summary>
    public IpLists IpLists => _ipLists ??= new IpLists(Paths);
    IpLists? _ipLists;

    /// <summary>Журнал изменений системы (<c>changes.jsonl</c>, откат всего): пишет помощник, запись — до действия.</summary>
    public ChangeJournal Journal { get; }
    /// <summary>Временные метки TCP в системе; в тестах — подмена.</summary>
    public ITcpTimestampsSystem Tcp { get; set; } = new NetshTcpTimestamps();

    public Shell(AppPaths? paths = null)
    {
        Paths = paths ?? new AppPaths();
        Journal = new ChangeJournal(Paths.ChangesFile);
        Data = new DataStore(Paths);
        Installer = new CoreInstaller(Paths, CoreManifest.Load(Path.Combine(Paths.Seed, "core.json")));
        Service = new ServiceManager(Paths) { Recorder = Journal, BeforeStart = EnsureCoreIntact };
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
        if (IsAdmin())   // свои сайты и ядро — только системе и администраторам
            foreach (var r in Paths.Harden()) log?.Report(L.T("core.shell.unsafe_removed", new { item = r }));
        var check = Installer.Check();
        if (check.State == CoreState.Missing)
        {
            // Помощник только что поставлен: ядро качается по команде окна, после вопроса об исключении Защитника (CoreWait).
            if (CoreWait.Remaining(Paths, UtcNow()) is { } left)
                log?.Report(L.T("core.shell.core_wait", new { minutes = (int)Math.Ceiling(left.TotalMinutes) }));
            else
            {
                CoreWait.Clear(Paths);
                using var http = DownloadClient();
                await InstallRecordedAsync(http, log, ct);
                check = Installer.Check();
            }
        }
        // Служба стартует с Windows раньше помощника и без его проверки: ядро не сходится с манифестом — остановить.
        if (IsAdmin()) StopServiceIfCoreBroken(log);
        if (check.State == CoreState.Ok && IsAdmin())
        {
            // Программу закрыли во время «Все адреса» — срок вышел, вернуть; служба после обновления программы — на новые аргументы.
            try { if (!RevertExpiredAllAddresses() && !ExpireJoint()) RefreshServiceIfOutdated(); }
            catch (Exception e) when (e is InvalidOperationException or SecurityException or IOException) { log?.Report(e.Message); }
        }
        return check;
    }

    /// <summary>Скачать ядро по команде окна («Восстановить», первый запуск): метка ожидания окна больше не нужна.</summary>
    public async Task RepairCoreAsync(IProgress<string>? log = null, CancellationToken ct = default)
    {
        CoreWait.Clear(Paths);
        using var http = DownloadClient();
        await InstallRecordedAsync(http, log, ct);
    }

    /// <summary>
    /// Служба ждёт окна (<see cref="CoreWait"/>): по истечении — скачать ядро самой, если окно так и не скачало. Метку сняло окно — ничего не делать.
    /// </summary>
    public async Task InstallAfterWaitAsync(IProgress<string>? log = null, CancellationToken ct = default)
    {
        while (CoreWait.Exists(Paths))
        {
            if (CoreWait.Remaining(Paths, UtcNow()) is { } left)
            {
                // Ждать частями: метку могло снять окно.
                await Delay(left < TimeSpan.FromMinutes(1) ? left : TimeSpan.FromMinutes(1), ct);
                continue;
            }
            CoreWait.Clear(Paths);
            if (Installer.Check().State != CoreState.Missing) return;
            log?.Report(L.T("core.shell.core_wait_over"));
            using var http = DownloadClient();
            await InstallRecordedAsync(http, log, ct);
            return;
        }
    }

    /// <summary>Поставить ядро из архива, который окно передало частями (<see cref="CoreInstaller.AppendOffline"/>): те же хеши, тот же журнал.</summary>
    public Task InstallOfflineAsync(IProgress<string>? log = null, CancellationToken ct = default)
    {
        CoreWait.Clear(Paths);
        return RecordedAsync(() => Task.Run(() => Installer.InstallOffline(log), ct), ct);
    }

    /// <summary>Скачивание ядра для служб и тестов: в тестах — подставной сервер с архивом.</summary>
    public Func<HttpClient> DownloadClient { get; set; } = CreateDownloadClient;
    /// <summary>Часы и ожидание для <see cref="CoreWait"/>; в тестах — подмена.</summary>
    public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    /// <summary>Одна установка ядра за раз: служба по истечении ожидания и окно могут начать одновременно (общая папка core.download).</summary>
    readonly SemaphoreSlim _coreLock = new(1, 1);

    /// <summary>Установка ядра. Ядра ещё не было — в журнал «было: нет ядра» до скачивания; замена уже стоящего (восстановление) отката не требует.</summary>
    public Task InstallRecordedAsync(HttpClient http, IProgress<string>? log, CancellationToken ct) =>
        RecordedAsync(() => Installer.InstallAsync(http, log, ct), ct);

    async Task RecordedAsync(Func<Task> install, CancellationToken ct)
    {
        await _coreLock.WaitAsync(ct);
        try
        {
            string rec = "";
            if (Installer.Check().State == CoreState.Missing && Journal.Active(ChangeKinds.Core).Count == 0)
                rec = Journal.Begin(ChangeKinds.Core, L.T("core.shell.journal_core", new { version = Installer.Manifest.Version }),
                    new System.Text.Json.Nodes.JsonObject { ["installed"] = false }, new System.Text.Json.Nodes.JsonObject { ["version"] = Installer.Manifest.Version });
            try { await install(); }
            catch { Journal.Abort(rec); throw; }
            Journal.Done(rec);
        }
        finally { _coreLock.Release(); }
    }

    /// <summary>Источники обновлений данных — только из поставки (architecture.md «Обновления данных»).</summary>
    public UpdateSources UpdateSources => UpdateSources.Load(Path.Combine(Paths.Package, UpdateSources.FileName));

    /// <summary>Версия данных в работе (обновлённых или поставки).</summary>
    public string? DataVersion => Paths.SeedManifest?.Version;

    /// <summary>Проверочный ключ подписи манифеста данных (в тестах подменяется тестовым).</summary>
    public byte[] DataSigningKey { get; set; } = ReleaseSignature.ReleaseKey;

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
            var updater = new DataUpdater(Paths, new DataUpdateClient(http, UpdateSources) { SigningKey = DataSigningKey }) { InstallCore = installCore };
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

    /// <summary>Что не так с ядром для запуска winws (хеши winws.exe, WinDivert и остальных файлов по манифесту, чужие dll/exe рядом); всё в порядке — null.</summary>
    public string? CoreProblem()
    {
        var c = Installer.Check();
        if (c.State != CoreState.Ok)
            return c.State == CoreState.Missing
                ? L.T("core.shell.core_missing")
                : L.T("core.shell.core_changed", new { files = string.Join(", ", c.BadFiles) });
        // Чужая dll или exe рядом с winws может быть подхвачена при запуске.
        var foreign = c.ExtraFiles.Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToList();
        return foreign.Count > 0 ? L.T("core.shell.core_foreign", new { files = string.Join(", ", foreign) }) : null;
    }

    /// <summary>Перед каждым запуском winws (процесс или служба): ядро должно сходиться с манифестом, иначе — отказ.</summary>
    void EnsureCoreIntact()
    {
        if (CoreProblem() is { } problem) throw new SecurityException(problem);
    }

    /// <summary>
    /// Служба с нашим winws работает, а ядро не сходится с манифестом (служба стартует с Windows сама, без проверки) —
    /// остановить её и сообщить. True — остановили.
    /// </summary>
    public bool StopServiceIfCoreBroken(IProgress<string>? log = null)
    {
        if (CoreProblem() is not { } problem) return false;
        if (Service.State is not (SvcState.Running or SvcState.StartPending)) return false;
        var (exe, _) = ZapretDetector.SplitCommandLine(ServiceManager.QueryBinPath(ServiceManager.Name));
        if (exe is null || !exe.Equals(Paths.Winws, StringComparison.OrdinalIgnoreCase)) return false;
        Service.Stop();
        log?.Report(L.T("core.shell.service_stopped_core", new { problem }));
        return true;
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
        var snapshotAt = DateTime.Now;   // процесс из снимка запущен не позже этого момента
        var found = ZapretDetector.Find(Paths, AllStrategies).Where(z => z.Owner != ZapretOwner.DpiShell).ToList();
        foreach (var z in found.Where(z => z.Kind == "service"))
        {
            ServiceManager.Sc("stop", z.Name);
            ServiceManager.WaitFor(z.Name, SvcState.Stopped, TimeSpan.FromSeconds(10));
            stopped.Add(L.T("core.shell.stopped_service", new { owner = z.OwnerTitle, name = z.Name }));
            comeBack.Add(z.Name);
        }
        foreach (var z in found.Where(z => z.Kind == "process"))
        {
            if (!int.TryParse(z.Name.Replace("PID ", ""), out var pid)) continue;
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(pid);
                // Между снимком и Kill номер мог достаться другому процессу (в т. ч. чужого пользователя) — тогда не трогаем.
                if (!IsSameProcess(p, z.ExePath, snapshotAt)) continue;
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
                stopped.Add(z.OwnerTitle);
            }
            catch (ArgumentException) { }            // уже завершился
            catch (InvalidOperationException) { }
        }
        return (stopped, comeBack);
    }

    /// <summary>
    /// Тот ли это процесс, что был в снимке: запущен не позже снимка, то же имя и (если система его отдаёт) тот же путь образа.
    /// Не удалось узнать время старта — нет.
    /// </summary>
    public static bool IsSameProcess(System.Diagnostics.Process p, string? exe, DateTime snapshotAt)
    {
        try
        {
            if (p.HasExited || p.StartTime > snapshotAt) return false;
            var name = exe is null ? null : Path.GetFileNameWithoutExtension(exe);
            if (name is not null ? !p.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase)
                                 : p.ProcessName is not ("winws" or "goodbyedpi")) return false;
            if (exe is null) return true;
            string? image;
            try { image = p.MainModule?.FileName; }
            catch (System.ComponentModel.Win32Exception) { image = null; }   // защищённый процесс: хватит имени и времени
            return image is null || image.Equals(exe, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return false; }
    }

    // ---------- режимы работы: служба Windows или процесс программы ----------

    WinwsProcess? _proc;

    public bool ServiceMode => State.RunMode != "process";

    /// <summary>Обход сейчас работает — в своём режиме (служба запущена или наш процесс winws жив).</summary>
    public bool IsActive => ServiceMode ? Service.State == SvcState.Running : _proc is { Alive: true };

    /// <summary>Временные метки TCP нужны стратегиям с fooling=ts; выключены — включаем и запоминаем, что это сделали мы.</summary>
    public void EnsureTcpTimestamps()
    {
        if (Tcp.IsEnabled() != false) return;
        // Были выключены — запись ДО netsh: прежнее «выключены», поставим «включены».
        var rec = Journal.Begin(ChangeKinds.TcpTimestamps, L.T("core.shell.journal_timestamps"),
            new System.Text.Json.Nodes.JsonObject { ["enabled"] = false }, new System.Text.Json.Nodes.JsonObject { ["enabled"] = true });
        if (Tcp.Set(true))
        {
            Journal.Done(rec);
            State.TimestampsEnabledByUs = true;
            State.Save(Paths);
        }
        else Journal.Abort(rec);
    }

    /// <summary>«Удалить полностью»: вернуть временные метки, если включали мы.</summary>
    public void RestoreTcpTimestamps()
    {
        if (!State.TimestampsEnabledByUs) return;
        if (Tcp.Set(false))
        {
            State.TimestampsEnabledByUs = false;
            State.Save(Paths);
            Journal.Close(ChangeKinds.TcpTimestamps, L.T("core.shell.closed_timestamps"));
        }
    }

    public void Enable(string strategyId)
    {
        EnsureCoreIntact();
        ThrowIfConflicts();
        EnsureTcpTimestamps();
        var s = FindStrategy(strategyId) ?? throw new ArgumentException(L.T("core.shell.no_strategy", new { id = strategyId }));
        // Список «По списку» удалён/изменён антивирусом — вернуть из поставки, иначе winws не стартует без файла.
        if (CurrentOptions.Ipset == IpsetMode.List && !IpLists.HasList) IpLists.SeedFromPackage();
        SyncAppProfileFiles();   // файлы адресов профилей программ должны быть на диске до запуска winws
        var options = CurrentOptions;
        // Продвинутый режим без единой строки со стратегией и «остальные напрямую»: winws нечего обрабатывать (и без профилей программ).
        if (options.Advanced is { Others: AdvancedSites.OthersDirect } adv && !adv.Applied.Any() && options.Profiles is null)
            throw new InvalidOperationException(AdvancedSites.NothingToDo);
        if (JointNothingToDo(options)) throw new InvalidOperationException(JointTexts.NothingToDo);
        var args = WinwsArgs.Build(s, Paths, options);
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
            throw new InvalidOperationException(L.T("core.shell.winws_failed", new { output = (output.Length > 300 ? output[^300..] : output).Trim() }));
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
    public async Task<SelectionReport> SelectAsync(bool quick, IReadOnlyList<TargetGroup>? groups = null,
        IProgress<SelectorProgress>? progress = null, CancellationToken ct = default)
    {
        bool resolved = false;
        var result = await WithServicePausedAsync(async () =>
        {
            var doh = NewCheckResolver();
            using var _ = doh as IDisposable;
            // Строки «auto» продвинутого режима — свои группы целей (по адресу первого сайта): вне оценки стратегии, по ним выбирается лучшая для строки.
            var withRows = (groups ?? SelectedGroups()).Concat(AdvancedSites.Groups(State.Advanced)).ToList();
            var report = await new Selector(Paths, NewProbe(doh)).RunAsync(
                StrategyStats.Order(AllStrategies, StrategyStats.LoadDetailed(Paths)), WithControl(withRows, DefaultTargets), quick, progress, ct, mix: true);
            report = report with { AddressSource = AddressSourceOf(doh) };
            // Смешанная, которая оказалась лучше одиночных, — в файл своих стратегий (окно применяет её по id, как любую лучшую).
            foreach (var m in report.Scores.Where(x => x.Strategy.Source == MixBuilder.Source && x.Error is null))
                UserStrategies.AddMix(Paths, m.Strategy, State.StrategyId);
            ResultStore.Save(Paths, report);
            if (!report.Cancelled && State.Advanced.Enabled)
            {
                var adv = State.Advanced.Clone();
                if (AdvancedSites.Resolve(adv, report)) { State.Advanced = adv; State.Save(Paths); resolved = true; }
            }
            return report;
        });
        // Служба вернулась со старой командной строкой — переставить на новые итоги строк.
        if (resolved)
            try { RefreshServiceIfOutdated(); }
            catch (Exception e) when (e is InvalidOperationException or SecurityException or IOException) { }
        return result;
    }

    /// <summary>
    /// Проверка сайтов для одной стратегии (своей, вставленной): полный прогон из одного кандидата, итог сохраняется как у подбора —
    /// статистика и подпись в списке. Служба на время проверки останавливается.
    /// </summary>
    public Task<SelectionReport> CheckOneAsync(string strategyId, IReadOnlyList<TargetGroup>? groups = null,
        IProgress<SelectorProgress>? progress = null, CancellationToken ct = default) =>
        WithServicePausedAsync(async () =>
        {
            var strategy = FindStrategy(strategyId) ?? throw new InvalidOperationException(L.T("core.shell.no_such_strategy"));
            var doh = NewCheckResolver();
            using var _ = doh as IDisposable;
            var report = await new Selector(Paths, NewProbe(doh)).RunAsync([strategy], WithControl(groups ?? SelectedGroups(), DefaultTargets), quick: false, progress, ct);
            report = report with { AddressSource = AddressSourceOf(doh) };
            ResultStore.Save(Paths, report);
            return report;
        });

    /// <summary>Глубокий поиск (architecture.md «Глубокий поиск») от базовой стратегии; найденное, что прошло полную проверку, сохраняется как свои стратегии.</summary>
    public Task<SearchReport> SearchAsync(string? baseId = null, IReadOnlyList<TargetGroup>? groups = null,
        IProgress<SelectorProgress>? progress = null, CancellationToken ct = default) =>
        WithServicePausedAsync(async () =>
        {
            var baseStrategy = FindStrategy(baseId ?? State.StrategyId ?? "alt9") ?? Catalog.Strategies[0];
            var doh = NewCheckResolver();
            using var _ = doh as IDisposable;
            var report = await new StrategySearch(Paths, () => NewProbe(doh)).RunAsync(baseStrategy, WithControl(groups ?? SelectedGroups(), DefaultTargets), progress, ct);
            if (report.Final is { } f0) report = report with { Final = f0 with { AddressSource = AddressSourceOf(doh) } };
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
                // За время подбора ядро могли подменить: не сходится с манифестом — служба не стартует (окно увидит ядро в status).
                if (serviceWasRunning && CoreProblem() is null) ServiceManager.Sc("start", ServiceManager.Name);
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
        var doh = NewCheckResolver();
        using var _ = doh as IDisposable;
        var verdicts = await new Selector(Paths, NewProbe(doh)).CheckGroupsAsync(groups, ct);
        // «Как у системы»: проверка и так шла по пути браузера — сравнивать нечего, DoH не трогаем.
        var result = doh is DohResolver d
            ? await BrowserCheck.AddAsync(verdicts, SystemResolver.Instance, d,
                (t, r, token) => new HttpProbe { Resolver = r }.CheckAsync(t, HttpProbe.DefaultTls, token), BrowserCheck.SystemHosts(), ct)
            : verdicts;
        LastAddressSource = AddressSourceOf(doh);
        return result;
    }

    /// <summary>
    /// Откуда адреса для проверок (<see cref="CheckDns"/>): <see cref="CheckDns.Secure"/> (по умолчанию) или <see cref="CheckDns.System"/>.
    /// Окно ставит из своих настроек, помощник — из аргумента команды подбора.
    /// </summary>
    public string CheckDnsMode { get; set; } = CheckDns.Secure;

    /// <summary>Откуда взяла адреса последняя проверка «Проверить сейчас» (<see cref="IResolverSource.AddressSource"/>); null — проверок ещё не было.</summary>
    public string? LastAddressSource { get; private set; }

    /// <summary>Резолвер проверок по <see cref="CheckDnsMode"/>. Защищённый DNS не отвечает — проверка сама берёт адрес у системы (<see cref="HttpProbe"/>), пометка — в <see cref="AddressSourceOf"/>.</summary>
    IAddressResolver NewCheckResolver() => CheckDns.IsSystem(CheckDnsMode) ? SystemResolver.Instance : new DohResolver();

    static string? AddressSourceOf(IAddressResolver r) => (r as IResolverSource)?.AddressSource;

    /// <summary>Проверка резолвит цели через DoH (или как у системы — по настройке): результат зависит от DPI, а не от DNS системы. Кэш DoH живёт один прогон.</summary>
    public static HttpProbe NewProbe(IAddressResolver resolver) => new() { Resolver = resolver };

    /// <summary>Подменяет ли DNS системы адреса проверяемых сайтов (для предупреждения и анализа DNS).</summary>
    public async Task<IReadOnlyList<DnsComparison>> CheckDnsAsync(CancellationToken ct = default)
    {
        using var doh = new DohResolver();
        var hosts = SelectedGroups().SelectMany(g => g.Targets).Select(t => new Uri(t.Url).Host).Distinct().ToList();
        return await Task.WhenAll(hosts.Select(h => DnsCheck.CompareAsync(h, doh, ct)));
    }

    // ---------- IP-списки и игровой режим (architecture.md «IP-списки и игровой режим») ----------

    /// <summary>С чем собирается командная строка сейчас. «Все адреса» с истёкшим сроком уже не действует.</summary>
    public BuildOptions CurrentOptions => BuildOptions.From(State, DateTime.UtcNow, FindStrategy, State.StrategyId);

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
            throw new InvalidOperationException(L.T("core.shell.iplist_missing"));
        if (CurrentOptions.Ipset == mode && State.IpsetMode != "any") return;
        State.IpsetMode = BuildOptions.Format(mode);
        State.IpsetAnyUntil = null;
        State.IpsetByGame = false;   // режим списка выбран рукой — игровой режим его больше не вернёт
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
                throw new InvalidOperationException(L.T("core.shell.game_needs_lists"));
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
                    State.IpsetByGame = true;
                    note = L.T("core.shell.game_uses_list");
                }
            }
        }
        else if (State.IpsetByGame)
        {
            // Включить → выключить = как было: список, который включил сам игровой режим, выключается.
            if (State.IpsetMode == BuildOptions.Format(IpsetMode.List))
            {
                State.IpsetMode = BuildOptions.Format(IpsetMode.Off);
                note = L.T("core.shell.game_list_off");
            }
            State.IpsetByGame = false;
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

    /// <summary>Свои сайты: файл доменов, список целей, группа «Свои» включается. Негодные строки отбрасываются и возвращаются.</summary>
    public (IReadOnlyList<string> Saved, IReadOnlyList<string> Rejected) SetUserTargets(IEnumerable<string> lines)
    {
        Paths.EnsureDirectories();
        var trimmed = lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var rejected = Data.WriteUserDomains(trimmed);
        var good = trimmed.Where(l => DataStore.NormalizeDomain(l) is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        State.UserTargets = good;
        if (good.Count > 0 && !State.Groups.Contains("user")) State.Groups.Add("user");
        SaveState();
        return (good, rejected);
    }

    /// <summary>
    /// Профили программ целиком (загрузка настроек и её откат): профили из <paramref name="add"/> добавляются как есть (id и дата сохраняются),
    /// из <paramref name="removeIds"/> убираются; файлы адресов и работающая оптимизация приводятся в порядок. Журнал не трогается — его ведёт вызывающий.
    /// </summary>
    public void ChangeAppProfiles(IEnumerable<string> removeIds, IEnumerable<AppProfile> add)
    {
        var ids = removeIds.ToHashSet();
        State.AppProfiles.RemoveAll(p => ids.Contains(p.Id));
        foreach (var p in add)
        {
            if (AppProfiles.Problem(p) is { } problem) throw new InvalidOperationException(problem);
            if (State.AppProfiles.Count >= AppProfiles.MaxProfiles) throw new InvalidOperationException(L.T("core.shell.profiles_max", new { max = AppProfiles.MaxProfiles }));
            State.AppProfiles.RemoveAll(x => x.Id == p.Id);
            State.AppProfiles.Add(p);
        }
        State.Save(Paths);
        SyncAppProfileFiles();
        ReapplyIfActive();
    }

    /// <summary>
    /// «Открывать без оптимизации» (API 1.10): список целиком. Домены — в user-exclude.txt (его читают общие роли и IP-списки) и в состояние (сборка добавляет их
    /// как <c>--hostlist-exclude-domains</c> во все роли каталога, в том числе google и discord_media). Работающий обход перезапускается; не поднялся — список возвращается.
    /// </summary>
    public (IReadOnlyList<string> Saved, IReadOnlyList<string> Rejected) SetUserExclude(IEnumerable<string> lines)
    {
        Paths.EnsureDirectories();
        var trimmed = lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var good = trimmed.Select(DataStore.NormalizeDomain).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Take(AdvancedSites.MaxExclude).ToList();
        var before = State.UserExclude.ToList();
        Data.WriteUserDomains(good, DataStore.UserExclude);
        IReadOnlyList<string> rejected = [.. trimmed.Where(l => DataStore.NormalizeDomain(l) is null)];
        if (before.SequenceEqual(good, StringComparer.OrdinalIgnoreCase)) return (good, rejected);
        State.UserExclude = good;
        State.Save(Paths);
        try { ReapplyIfActive(); }
        catch (Exception e) when (e is InvalidOperationException or SecurityException or IOException or ConflictException)
        {
            State.UserExclude = before;
            State.Save(Paths);
            Data.WriteUserDomains(before, DataStore.UserExclude);
            try { ReapplyIfActive(); } catch (Exception again) when (again is InvalidOperationException or SecurityException or IOException) { }
            throw;
        }
        return (good, rejected);
    }

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

    // ---------- профили программ (ТЗ 13, docs/dev/architecture.md «Подключения программ») ----------

    /// <summary>Варианты параметров профиля: как у текущей стратегии, потом у остальных.</summary>
    public IReadOnlyList<ProfileVariant> ProfileVariants() => AppProfiles.Variants(AllStrategies, State.StrategyId);

    /// <summary>
    /// Создать профиль (id пуст) или заменить (id есть). Порты и адреса приводятся к каноническому виду, параметры берутся из варианта —
    /// из числа готовых, окно своих параметров не передаёт. Работающая оптимизация перезапускается на той же стратегии.
    /// </summary>
    public AppProfile SetAppProfile(string? id, string title, string? tcpPorts, string? udpPorts, IReadOnlyList<string> addresses, int variant)
    {
        var variants = ProfileVariants();
        if (variant < 0 || variant >= variants.Count) throw new InvalidOperationException(L.T("core.shell.no_variant"));
        var v = variants[variant];
        string tcp = tcpPorts is null or "" ? "" : AppProfiles.NormalizePorts(tcpPorts) ?? throw new InvalidOperationException(L.T("core.profile.bad_tcp"));
        string udp = udpPorts is null or "" ? "" : AppProfiles.NormalizePorts(udpPorts) ?? throw new InvalidOperationException(L.T("core.profile.bad_udp"));
        var verdict = IpsetValidator.Validate(string.Join('\n', addresses), IpsetKind.User);
        var existing = id is null ? null : State.AppProfiles.FirstOrDefault(p => p.Id == id);
        if (id is not null && existing is null) throw new InvalidOperationException(L.T("core.shell.no_profile"));
        var profile = new AppProfile
        {
            Id = id ?? AppProfiles.NewId(), Title = title.Trim(), TcpPorts = tcp, UdpPorts = udp, Addresses = [.. verdict.Entries],
            TcpDesync = tcp.Length > 0 ? v.Tcp : "", UdpDesync = udp.Length > 0 ? v.Udp : "", Variant = v.Index,
            CreatedAt = existing?.CreatedAt ?? DateTime.Now,
        };
        if (AppProfiles.Problem(profile) is { } problem) throw new InvalidOperationException(problem);
        if (existing is null && State.AppProfiles.Count >= AppProfiles.MaxProfiles)
            throw new InvalidOperationException(L.T("core.shell.too_many_profiles", new { max = AppProfiles.MaxProfiles }));
        // Журнал отката: новый профиль — запись «было: нет профиля» до сохранения; правка своего же профиля обновляет «что поставили».
        var snapshot = AppProfileUndoer.Snapshot(profile);
        if (existing is null)
        {
            var rec = Journal.Begin(ChangeKinds.AppProfile, L.T("core.shell.journal_profile", new { title = profile.Title, ports = (profile.TcpPorts + " " + profile.UdpPorts).Trim() }),
                new System.Text.Json.Nodes.JsonObject { ["exists"] = false }, snapshot);
            State.AppProfiles.Add(profile);
            State.Save(Paths);
            Journal.Done(rec);
        }
        else
        {
            State.AppProfiles[State.AppProfiles.IndexOf(existing)] = profile;
            State.Save(Paths);
            foreach (var en in Journal.Active(ChangeKinds.AppProfile).Where(x => x.After?["id"]?.GetValue<string>() == profile.Id))
                Journal.Update(en.Id, snapshot);
        }
        SyncAppProfileFiles();
        ReapplyIfActive();
        return profile;
    }

    /// <summary>«Вернуть как было»: профиль и его файл удаляются, оптимизация перезапускается без него. False — такого профиля нет.</summary>
    public bool RemoveAppProfile(string id)
    {
        var p = State.AppProfiles.FirstOrDefault(x => x.Id == id);
        if (p is null) return false;
        State.AppProfiles.Remove(p);
        State.Save(Paths);
        SyncAppProfileFiles();
        Journal.Close(ChangeKinds.AppProfile, L.T("core.shell.closed_profile"), en => en.After?["id"]?.GetValue<string>() == id);
        ReapplyIfActive();
        return true;
    }

    /// <summary>«Удалить полностью»: все профили и файлы.</summary>
    public void ClearAppProfiles()
    {
        State.AppProfiles.Clear();
        State.Save(Paths);
        SyncAppProfileFiles();
        Journal.Close(ChangeKinds.AppProfile, L.T("core.shell.closed_profiles"));
    }

    /// <summary>Файлы адресов профилей — ровно по state.json: недостающие пишутся, лишние (app-*.txt) удаляются.</summary>
    public void SyncAppProfileFiles()
    {
        Directory.CreateDirectory(Paths.Lists);
        var want = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in State.AppProfiles.Where(p => AppProfiles.Problem(p) is null))
        {
            var name = AppProfiles.FileName(p.Id);
            want.Add(name);
            var path = Path.Combine(Paths.Lists, name);
            var text = AppProfiles.FileText(p);
            if (!File.Exists(path) || File.ReadAllText(path) != text) File.WriteAllText(path, text);
        }
        foreach (var f in Directory.GetFiles(Paths.Lists, "app-*.txt"))
            if (!want.Contains(Path.GetFileName(f))) { try { File.Delete(f); } catch (IOException) { } }
    }

    // ---------- продвинутый режим «сайты → стратегия» (docs/research/mixed-strategies.md §6) ----------

    /// <summary>Текущие настройки продвинутого режима (копия): строки хранятся и при выключенном режиме.</summary>
    public AdvancedConfig GetAdvanced() => State.Advanced.Clone();

    /// <summary>
    /// Что показать в окне: настройки и чем идёт каждая строка (у «auto» — итог подбора, до него — текущая стратегия; у «direct» — ничем).
    /// </summary>
    public AdvancedInfo AdvancedInfo()
    {
        var a = State.Advanced;
        return new(a.Enabled, a.Others, [.. a.Rows.Select(r => new AdvancedRowInfo(r.Domains, r.Strategy,
            r.Strategy == AdvancedSites.Direct ? null : r.Strategy == AdvancedSites.Auto ? r.Resolved ?? State.StrategyId : r.Strategy))]);
    }

    /// <summary>
    /// Сохранить продвинутый режим. Сайты приводятся к нормальному виду (строгий шаблон), стратегия — «auto», «direct» или из каталога и найденных;
    /// итог подбора у неизменных строк «auto» сохраняется. Работающая оптимизация перезапускается; не запустилась — всё возвращается как было.
    /// Запись в журнал отката — ДО сохранения.
    /// </summary>
    public AdvancedConfig SetAdvanced(bool enabled, string others, IReadOnlyList<AdvancedRowInput> rows)
    {
        if (!AdvancedSites.IsOthers(others)) throw new InvalidOperationException(L.T("core.adv.others"));
        if (rows.Count > AdvancedSites.MaxRows) throw new InvalidOperationException(L.T("core.adv.too_many_rows", new { max = AdvancedSites.MaxRows }));
        var next = new AdvancedConfig { Enabled = enabled, Others = others };
        for (int i = 0; i < rows.Count; i++)
        {
            var (domains, problem) = AdvancedSites.Domains(rows[i].Domains, i);
            if (problem is not null) throw new InvalidOperationException(problem);
            var strategy = rows[i].Strategy;
            if (!AdvancedSites.IsStrategyChoice(strategy) || strategy is not (AdvancedSites.Auto or AdvancedSites.Direct) && FindStrategy(strategy) is null)
                throw new InvalidOperationException(L.T("core.adv.row_no_strategy", new { n = i + 1 }));
            var old = strategy != AdvancedSites.Auto ? null
                : State.Advanced.Rows.FirstOrDefault(r => r.Strategy == AdvancedSites.Auto && r.Domains.SequenceEqual(domains!));
            next.Rows.Add(new() { Domains = domains!, Strategy = strategy, Resolved = old?.Resolved });
        }
        if (AdvancedSites.IsEmpty(next) && State.AppProfiles.Count == 0) throw new InvalidOperationException(AdvancedSites.NothingToDo);
        ApplyAdvanced(next);
        return next;
    }

    void ApplyAdvanced(AdvancedConfig next)
    {
        var before = State.Advanced.Clone();
        if (next.SameChoice(before)) return;
        // Журнал: первое изменение — «было …, стало …»; следующие правят «стало»; вернули как было вначале — запись закрывается.
        string rec = "";
        var open = Journal.Active(ChangeKinds.Advanced).FirstOrDefault();
        bool closed = false;
        if (open is null)
            rec = Journal.Begin(ChangeKinds.Advanced, AdvancedTitle(next), before.ToJson(), next.ToJson());
        else if (next.SameChoice(AdvancedConfig.FromJson(open.Before)))
            closed = true;
        else
            Journal.Update(open.Id, next.ToJson());
        State.Advanced = next;
        State.Save(Paths);
        try { ReapplyIfActive(); }
        catch (Exception e) when (e is InvalidOperationException or SecurityException or IOException or ConflictException)
        {
            // winws с новыми строками не поднялся — настройки возвращаются, оптимизация запускается на прежних.
            State.Advanced = before;
            State.Save(Paths);
            Journal.Abort(rec);
            try { ReapplyIfActive(); } catch (Exception again) when (again is InvalidOperationException or SecurityException or IOException) { }
            throw;
        }
        Journal.Done(rec);
        if (closed) Journal.Close(ChangeKinds.Advanced, L.T("core.shell.adv_restored"));
    }

    static string AdvancedTitle(AdvancedConfig c) =>
        L.T("core.shell.adv_state", new { state = L.T(c.Enabled ? "core.shell.adv_on" : "core.shell.adv_off", new { n = c.Rows.Count }) });

    /// <summary>Откат записи журнала: вернуть прежние настройки (итоги подбора совпадающих строк сохраняются). Журнал не трогается — его ведёт движок отката.</summary>
    public void RestoreAdvanced(AdvancedConfig was)
    {
        var next = was.Clone();
        foreach (var r in next.Rows)
            r.Resolved = State.Advanced.Rows.FirstOrDefault(x => x.Strategy == AdvancedSites.Auto && r.Strategy == AdvancedSites.Auto && x.Domains.SequenceEqual(r.Domains))?.Resolved;
        State.Advanced = next;
        State.Save(Paths);
        ReapplyIfActive();
    }

    /// <summary>
    /// Откат загрузки настроек, которая правила уже открытую запись продвинутого режима: вернуть прежнее через тот же путь, что кнопки окна, —
    /// открытая запись обновит «что поставили» или закроется, если вернулись к её началу. Открытой записи нет — просто вернуть.
    /// </summary>
    public void RevertAdvancedTo(AdvancedConfig was)
    {
        if (Journal.Active(ChangeKinds.Advanced).Count == 0) { RestoreAdvanced(was); return; }
        var next = was.Clone();
        foreach (var r in next.Rows)
            r.Resolved = State.Advanced.Rows.FirstOrDefault(x => x.Strategy == AdvancedSites.Auto && r.Strategy == AdvancedSites.Auto && x.Domains.SequenceEqual(r.Domains))?.Resolved;
        ApplyAdvanced(next);
    }

    /// <summary>«Удалить полностью»: строки и режим сбрасываются.</summary>
    public void ClearAdvanced()
    {
        State.Advanced = new();
        State.Save(Paths);
        Journal.Close(ChangeKinds.Advanced, L.T("core.shell.closed_adv"));
    }

    // ---------- совместная работа с роутером (docs/research/mixed-strategies.md §7) ----------

    /// <summary>Когда у ПК нечего вести при связке: нет своих сайтов, нет строк со своей стратегией и профилей программ (после отдачи роутеру его адресов).</summary>
    public bool JointNothingToDo(BuildOptions options)
    {
        if (options.Joint is not { } j) return false;
        bool OwnedByRouter(string d) => JointRules.OwnedBy(d, j.Owned);
        return Data.ReadUserDomains().All(OwnedByRouter)
            && !(options.Advanced?.Applied.Any(r => r.Domains.Any(d => !OwnedByRouter(d))) ?? false)
            && options.Profiles is null;
    }

    /// <summary>
    /// Что этот ПК готов взять на себя при совместной работе (ответ <c>joint_get</c>): свои сайты, сайты строк продвинутого режима
    /// (в том числе «напрямую»: роутеру тоже не стоит их трогать), имена приманок и действует ли связка. Номер ПК создаётся при первом обращении.
    /// </summary>
    public JointInfo JointInfo()
    {
        if (State.Joint.PcId is null) { State.Joint.PcId = Guid.NewGuid().ToString("D"); State.Save(Paths); }
        var rows = State.Advanced.Enabled ? State.Advanced.Rows.SelectMany(r => r.Domains) : [];
        // Приманки — по параметрам тех ролей, что остаются на ПК при связке: общая и QUIC у стратегии ПК, у стратегий строк и у профилей программ.
        var desyncs = new List<(string Role, string Desync)>();
        void Roles(Strategy? s)
        {
            if (s is null) return;
            foreach (var role in new[] { "general", "quic" })
                if (s.Roles.GetValueOrDefault(role) is { } d) desyncs.Add((role, d));
        }
        var current = State.StrategyId is { } id ? FindStrategy(id) : null;
        Roles(current);
        if (AdvancedSites.Plan(State.Advanced, FindStrategy, State.StrategyId) is { } plan)
            foreach (var row in plan.Applied) Roles(row.Strategy);
        foreach (var p in State.AppProfiles)
        {
            if (p.TcpDesync.Length > 0) desyncs.Add(("game_tcp", p.TcpDesync));
            if (p.UdpDesync.Length > 0) desyncs.Add(("game_udp", p.UdpDesync));
        }
        var decoys = JointDecoys.Of(desyncs, ReadFake, () => JointDecoys.SupplyNames(Paths.Fake));
        var j = State.Joint;
        var own = Data.ReadUserDomains();
        var work = own.Concat(State.Advanced.Enabled ? State.Advanced.Rows.Where(r => r.Strategy != AdvancedSites.Direct).SelectMany(r => r.Domains) : []).Distinct().ToList();
        return new(j.PcId!, CurrentOptions.Joint is not null, [.. j.Owned], own, [.. rows], decoys.Names, decoys.Reliable, decoys.Notes,
            State.StrategyId, State.Enabled, work, State.AppProfiles.Count);
    }

    byte[]? ReadFake(string name)
    {
        if (name.Contains('/') || name.Contains('\\') || name.Contains("..")) return null;
        try { var f = Path.Combine(Paths.Fake, name); return File.Exists(f) ? File.ReadAllBytes(f) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Включить или выключить связку и продлить её (<see cref="JointRules.Lease"/>). <paramref name="owned"/> — сайты ПК, которые ведёт роутер.
    /// Командная строка меняется только если изменились режим или список — тогда работающая оптимизация перезапускается; не запустилась — всё возвращается.
    /// Запись в журнал отката — ДО сохранения.
    /// </summary>
    public JointConfig SetJoint(bool active, IReadOnlyList<string> owned)
    {
        var clean = JointRules.Clean(owned);
        if (clean.Count != owned.Count && owned.Any(o => !AdvancedSites.IsDomain(o))) throw new InvalidOperationException(L.T("core.shell.site_form"));
        if (clean.Count > JointRules.MaxDomains) throw new InvalidOperationException(L.T("core.shell.too_many_sites", new { max = JointRules.MaxDomains }));
        var before = State.Joint.Clone();
        var next = new JointConfig { Active = active, Owned = active ? clean : [], Until = active ? DateTime.UtcNow + JointRules.Lease : null, PcId = before.PcId };
        if (!active && !before.Active) { return before; }

        // Журнал: включение — «было выключено»; выключение самой функцией закрывает запись; смена списка при включённой связке правит «стало».
        string rec = "";
        var open = Journal.Active(ChangeKinds.Joint).FirstOrDefault();
        if (active && open is null && !before.Active) rec = Journal.Begin(ChangeKinds.Joint, JointTexts.JournalTitle, before.ToJson(), next.ToJson());
        else if (active && open is not null && !next.SameBuild(before)) Journal.Update(open.Id, next.ToJson());

        State.Joint = next;
        State.Save(Paths);
        if (!next.SameBuild(before))
        {
            try { ReapplyIfActive(); }
            catch (Exception e) when (e is InvalidOperationException or SecurityException or IOException or ConflictException)
            {
                // winws с новой настройкой не поднялся — связка возвращается, оптимизация запускается на прежней.
                State.Joint = before;
                State.Save(Paths);
                Journal.Abort(rec);
                try { ReapplyIfActive(); } catch (Exception again) when (again is InvalidOperationException or SecurityException or IOException) { }
                throw;
            }
        }
        Journal.Done(rec);
        if (!active) Journal.Close(ChangeKinds.Joint, L.T("core.shell.closed_joint"));
        return next;
    }

    /// <summary>Срок связки вышел (окно не продлило): ПК возвращается к полной настройке. True — вернули.</summary>
    public bool ExpireJoint()
    {
        if (State.Joint is not { Active: true, Until: { } until } || until > DateTime.UtcNow) return false;
        SetJoint(false, []);
        return true;
    }

    /// <summary>Откат записи журнала: связка выключается, ПК работает по полной настройке. Журнал не трогается — его ведёт движок отката.</summary>
    public void RestoreJointOff()
    {
        if (!State.Joint.Active) return;
        State.Joint = new JointConfig { PcId = State.Joint.PcId };
        State.Save(Paths);
        ReapplyIfActive();
    }

    /// <summary>«Удалить полностью»: связка снимается (номер ПК остаётся).</summary>
    public void ClearJoint()
    {
        State.Joint = new JointConfig { PcId = State.Joint.PcId };
        State.Save(Paths);
        Journal.Close(ChangeKinds.Joint, L.T("core.shell.closed_adv"));
    }

    public void SaveState() => State.Save(Paths);
}

/// <summary>Строка из окна: сайты и стратегия («auto», «direct» или id).</summary>
public sealed record AdvancedRowInput(IReadOnlyList<string> Domains, string Strategy);

/// <summary>Чем идёт строка: <paramref name="Resolved"/> — у «auto» итог подбора (до подбора — текущая стратегия), у «direct» — null.</summary>
public sealed record AdvancedRowInfo(IReadOnlyList<string> Domains, string Strategy, string? Resolved);

/// <summary>Ответ advanced_get / advanced_set: настройки продвинутого режима на ПК.</summary>
public sealed record AdvancedInfo(bool Enabled, string Others, IReadOnlyList<AdvancedRowInfo> Rows);

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
    : InvalidOperationException(L.T("core.shell.conflict", new { list = string.Join("; ", conflicts.Select(c => $"{c.Name} ({c.Detail})")) }))
{
    public IReadOnlyList<Conflict> Conflicts { get; } = conflicts;
}

/// <summary>Служба DpiShell по её настройкам в Windows. OurCore = false — служба с нашим именем запускает чужой файл.</summary>
public sealed record ServiceInfo(SvcState State, string? ExePath, Strategy? Strategy, bool OurCore);
