using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Authentication;
using System.Security.Principal;
using System.Text;
using DpiShell.Core;
using DpiShell.App;
using DpiShell.Core.Helper;

namespace DpiShell.Tests;

/// <summary>Помощник без winws и sc: записывает вызовы, подбор ждёт отмены или сигнала.</summary>
public sealed class FakeHelperBackend : IHelperBackend
{
    public ConcurrentQueue<string> Calls { get; } = new();
    public IReadOnlyCollection<string> StrategyIds { get; } = new HashSet<string> { "alt9", "alt10", "found-1" };
    public IReadOnlyCollection<string> GroupIds { get; } = new HashSet<string> { "youtube", "discord", "user" };

    /// <summary>Подбор/поиск/восстановление ядра висят, пока не отменят или не откроют этот сигнал.</summary>
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>Долгая операция началась (первое событие хода отправлено).</summary>
    public TaskCompletionSource LongStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> LongCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ClientsGone { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Exception? EnableThrows { get; set; }

    void Call(string s) => Calls.Enqueue(s);

    public static Strategy Alt9 => new("alt9", "Alt 9", "catalog", new() { ["general"] = "--dpi-desync=fake" });

    public static SelectionReport Report(bool cancelled)
    {
        var target = new Target("https://example.com/", 0, true);
        var verdict = new TargetVerdict("youtube", target, true, 0.4,
            [new ProbeResult(target, SslProtocols.Tls13, ProbeOutcome.Ok, 0.4, 1234, "ok")]);
        return new([verdict], [new TargetGroup("youtube", "YouTube", [target])],
            [new StrategyScore(Alt9, [verdict], 1, 1)], cancelled);
    }

    public static HelperStatus DefaultStatus => new(true, true, "service", "alt9", SvcState.Running, true, "alt9", CoreState.Ok,
        new NetFeatures(IpsetMode.Off, null, IpsetMode.Off, GameMode.Off, false, false, null),
        [new StrategyInfo("alt9", "Alt 9", "catalog")], [new GroupInfo("youtube", "YouTube", 2)],
        ["youtube"], [], [], false, false, [], "test");

    /// <summary>Что отвечает status (тесты окна подставляют своё: режимы, hosts).</summary>
    public HelperStatus StatusValue { get; set; } = DefaultStatus;

    /// <summary>Что отвечает details.</summary>
    public HelperDetails DetailsValue { get; set; } = new(null, null, [], [], new CoreCheck(CoreState.Ok, [], []), "test", []);

    public HelperStatus Status()
    {
        Call("status");
        return StatusValue;
    }

    public HelperDetails Details()
    {
        Call("details");
        return DetailsValue;
    }

    public void Choose(string strategyId) => Call("choose " + strategyId);

    public bool TcpTimestampsOn() { Call("tcp_timestamps_on"); return true; }

    public void Enable(string strategyId)
    {
        Call("enable " + strategyId);
        if (EnableThrows is { } e) throw e;
    }

    public void Disable() => Call("disable");
    public void SetRunMode(bool service) => Call("set_run_mode " + service);

    async Task<bool> HangAsync(Action report, CancellationToken ct)
    {
        report();
        LongStarted.TrySetResult();
        try { await Release.Task.WaitAsync(ct); return false; }
        catch (OperationCanceledException) { LongCancelled.TrySetResult(true); return true; }
    }

    public async Task<SelectionReport> SelectAsync(bool quick, IReadOnlyList<string>? groups, IProgress<SelectorProgress> progress, CancellationToken ct)
    {
        Call($"select {quick} {string.Join(",", groups ?? [])}");
        progress.Report(new(0, 2, "Проверка без обхода…"));
        bool cancelled = await HangAsync(() => progress.Report(new(1, 2, "Стратегия Alt 9…")), ct);
        // Как Selector: отмена — частичный отчёт, а не исключение.
        return Report(cancelled);
    }

    public async Task<SearchReport> SearchAsync(string? baseId, IReadOnlyList<string>? groups, IProgress<SelectorProgress> progress, CancellationToken ct)
    {
        Call($"search {baseId}");
        await HangAsync(() => progress.Report(new(0, 0, "google: 1/2")), ct);
        return new(new Dictionary<string, IReadOnlyList<CandidateResult>>(), [], Report(false), null);
    }

    public void SetIpsetMode(IpsetMode mode) => Call("ipset_mode " + mode);
    public void StartAllAddresses() => Call("all_addresses_start");
    public bool StopAllAddresses() { Call("all_addresses_stop"); return true; }

    public Task<GameModeChange> SetGameModeAsync(GameMode mode, IProgress<string> log, CancellationToken ct)
    {
        Call("game_mode " + mode);
        log.Report("Скачиваю IP-список…");
        return Task.FromResult(new GameModeChange(true, "Включён IP-список"));
    }

    public Task<IpsetUpdateSummary> UpdateIpsetFromManifestAsync(bool confirmed, IProgress<string> log, CancellationToken ct)
    {
        Call("ipset_update manifest " + confirmed);
        return Task.FromResult(new IpsetUpdateSummary(true, false, "ok", null));
    }

    public IpsetUpdateSummary UpdateIpsetFromContent(string content, bool confirmed)
    {
        Call($"ipset_update content {content.Length} {confirmed}");
        return new IpsetUpdateSummary(false, true, "подтвердите", null);
    }

    public IpsetVerdictSummary WriteUserIpset(IReadOnlyList<string> lines, bool exclude, bool confirmed)
    {
        Call($"user_ipset_set {lines.Count} {exclude} {confirmed}");
        return new IpsetVerdictSummary(true, false, lines.Count, 0, 0, 0, [], [], [], lines.Count, 0, 1, lines.Count, 0);
    }

    public UserTargetsResult SetUserTargets(IReadOnlyList<string> lines)
    {
        Call("user_targets_set " + string.Join(",", lines));
        return new(lines, []);
    }

    public Task<HostsCheckReport> HostsCheckAsync(IProgress<HostsCheckProgress> progress, CancellationToken ct)
    {
        Call("hosts_check");
        progress.Report(new(1, 1));
        return Task.FromResult(new HostsCheckReport([], DateTime.Now));
    }

    public HostsPreview HostsPreview()
    {
        Call("hosts_preview");
        return new(false, [], [], [], [], new HostsBlockInfo(false, false, null, []));
    }

    public HostsWriteResult HostsApply() { Call("hosts_apply"); return new(false, [], [], null); }
    public HostsWriteResult HostsRevert() { Call("hosts_revert"); return new(false, [], [], null); }

    public Task<HostsRecheckResult> HostsRecheckAsync(CancellationToken ct)
    {
        Call("hosts_recheck");
        return Task.FromResult(new HostsRecheckResult([], [], new HostsWriteResult(false, [], [], null), false));
    }

    public StopForeignResult StopForeign() { Call("stop_foreign"); return new(["zapret от Flowseal"], []); }

    public async Task CoreRepairAsync(IProgress<string> log, CancellationToken ct)
    {
        Call("core_repair");
        await HangAsync(() => log.Report("Скачиваю ядро…"), ct);
        ct.ThrowIfCancellationRequested();
    }

    public void RemoveAll() => Call("remove_all");
    public int ClearHistory() { Call("clear_history"); return 3; }

    public ResultsSnapshot ReadResults()
    {
        Call("read_results");
        return new(new Dictionary<string, StrategyStats.Stat> { ["alt9"] = new(0.9, 2, 1) }, []);
    }

    public void OnClientsGone()
    {
        Call("clients_gone");
        ClientsGone.TrySetResult();
    }
}

/// <summary>Сервер на своём имени канала + фейк. Разрешённый пользователь — текущий.</summary>
public sealed class HelperFixture : IAsyncDisposable
{
    public FakeHelperBackend Backend { get; } = new();
    public string PipeName { get; } = "DpiShell.Helper.Test." + Guid.NewGuid().ToString("N")[..12];
    public HelperServer Server { get; }
    public ConcurrentQueue<string> Log { get; } = new();

    /// <param name="backend">Свой помощник (например, настоящий <see cref="ShellHelperBackend"/> над временной папкой); null — фейк.</param>
    public HelperFixture(Func<HelperServerOptions, HelperServerOptions>? tune = null, IHelperBackend? backend = null)
    {
        using var me = WindowsIdentity.GetCurrent();
        var o = new HelperServerOptions { PipeName = PipeName, AllowedUser = me.User, Log = Log.Enqueue };
        Server = new HelperServer(backend ?? Backend, tune?.Invoke(o) ?? o);
        Server.Start();
    }

    public HelperClientOptions ClientOptions => new() { PipeName = PipeName, RequireTrustedServer = false, ConnectTimeout = TimeSpan.FromSeconds(5) };

    public Task<HelperClient> ConnectAsync() => HelperClient.ConnectAsync(ClientOptions);

    /// <summary>Связь окна с этим помощником (без UAC: установка — подмена), уже подключённая и с данными.</summary>
    public async Task<HelperLink> LinkAsync()
    {
        var link = new HelperLink { Connector = ct => HelperClient.ConnectAsync(ClientOptions, ct), Setup = new NoSetup(), WindowVersion = "0.0" };
        if (!await link.ConnectAsync()) throw new InvalidOperationException("Не подключились к помощнику фикстуры: " + link.Problem);
        await link.FetchAsync(full: true);
        return link;
    }

    /// <summary>Сырой клиент: hello, затем произвольные строки — для проверки того, что обычный клиент не отправит.</summary>
    public async Task<RawPipe> RawAsync()
    {
        var p = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await p.ConnectAsync(5000);
        var raw = new RawPipe(p);
        await raw.SendAsync("{\"hello\":{\"protocol\":1}}");
        var hello = await raw.ReadAsync();
        Assert.Contains("\"hello\"", hello);
        return raw;
    }

    public async ValueTask DisposeAsync() => await Server.StopAsync();
}

public sealed class RawPipe(NamedPipeClientStream pipe) : IDisposable
{
    readonly LineReader _r = new(pipe);

    public async Task SendAsync(string line)
    {
        await pipe.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
        await pipe.FlushAsync();
    }

    public async Task<string> ReadAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await _r.ReadLineAsync(cts.Token) ?? throw new IOException("канал закрыт");
    }

    public async Task<string> AskAsync(string line)
    {
        await SendAsync(line);
        return await ReadAsync();
    }

    public void Dispose() => pipe.Dispose();
}

/// <summary>Установка помощника в тестах: прав не просим, «пользователь отказал».</summary>
public sealed class NoSetup : IHelperSetup
{
    public List<string> Calls { get; } = [];
    public Task<SetupOutcome> RunElevatedAsync(string arguments)
    {
        Calls.Add(arguments);
        return Task.FromResult(new SetupOutcome(SetupResult.Declined));
    }
}

/// <summary>Настоящий помощник над временной папкой — для тестов окна: службы машины не трогает (sc, WMI — подмены).</summary>
public static class PcHelper
{
    public static ShellHelperBackend Over(Shell shell) => new(shell)
    {
        InspectService = () => new ServiceInfo(SvcState.NotInstalled, null, null, false),
        IsActive = () => false,
        FindForeign = () => [],
    };
}

/// <summary>Окно в тестах: связь с помощником и настройки окна во временной папке.</summary>
public static class TestWindow
{
    /// <summary>Помощник не установлен: режим просмотра, установка — «отказ» без UAC.</summary>
    public static HelperLink NoHelper() => new()
    {
        Connector = _ => throw new HelperUnavailableException(HelperUnavailableReason.NotInstalled),
        Setup = new NoSetup(),
    };

    public static WindowSettings Settings(Shell shell) =>
        WindowSettings.Load(Path.Combine(shell.Paths.Root, "window", "window.json"));

    public static MainViewModel Model(Shell shell, HelperLink? link = null, WindowSettings? settings = null) =>
        new(shell, link ?? NoHelper(), settings ?? Settings(shell));
}
