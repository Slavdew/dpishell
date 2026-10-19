using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.Tests;

/// <summary>
/// Сервер канала помощника в процессе теста (своё имя канала, фейковый backend): команды и ответы, ход и отмена,
/// одна операция за раз, отказы, доступ по DACL, отключение окна (helper-protocol §5).
/// </summary>
public class HelperChannelTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Status_enable_and_typed_results_roundtrip()
    {
        await using var f = new HelperFixture();
        await using var c = await f.ConnectAsync();
        Assert.Equal(HelperProtocol.Version, c.Protocol);
        Assert.Equal(HelperProtocol.AppVersion, c.HelperVersion);

        var st = await c.StatusAsync();
        Assert.Equal("alt9", st.StrategyId);
        Assert.Equal(SvcState.Running, st.Service);
        Assert.Equal(CoreState.Ok, st.Core);

        await c.EnableAsync("alt9");
        await c.SetRunModeAsync(false);
        Assert.True(await c.StopAllAddressesAsync());
        Assert.Equal(3, await c.ClearHistoryAsync());
        var r = await c.ReadResultsAsync();
        Assert.Equal(2, r.Stats["alt9"].Runs);
        Assert.Contains("enable alt9", f.Backend.Calls);
        Assert.Contains("set_run_mode False", f.Backend.Calls);
    }

    /// <summary>Версия протокола и список команд (helper-protocol §6): в hello и status, список — белый список помощника.</summary>
    [Fact]
    public async Task Status_carries_api_and_commands_from_whitelist()
    {
        await using var f = new HelperFixture();
        await using var c = await f.ConnectAsync();
        Assert.Equal(HelperProtocol.Api, c.HelperApi);
        var st = await c.StatusAsync();
        Assert.Equal(HelperProtocol.Api, st.Api);
        Assert.Equal(HelperCommands.Specs.Keys.Order(StringComparer.Ordinal), st.Commands);
        Assert.Equal(HelperCompatLevel.Ok, c.Compat.Level);
        Assert.Null(c.Compat.Message);
    }

    [Fact]
    public async Task Every_whitelisted_command_reaches_backend()
    {
        await using var f = new HelperFixture();
        await using var c = await f.ConnectAsync();
        f.Backend.Release.SetResult();   // долгие не висят

        var args = new Dictionary<string, object?>
        {
            ["enable"] = new { strategy = "found-1" },
            ["choose"] = new { strategy = "alt9" },
            ["set_run_mode"] = new { service = true },
            ["ipset_mode"] = new { mode = "list" },
            ["game_mode"] = new { mode = "all" },
            ["ipset_update"] = new { from = "manifest" },
            ["user_ipset_set"] = new { lines = new[] { "10.0.0.0/8" } },
            ["user_targets_set"] = new { lines = new[] { "example.com" } },
            ["dns_set"] = new { preset = "google" },
            ["strategy_add"] = new { title = "Моя", roles = new { general = "--dpi-desync=fake --dpi-desync-repeats=6" }, source = "user" },
            ["strategy_delete"] = new { strategy = "found-1" },
            ["strategy_check"] = new { strategy = "found-1", groups = new[] { "youtube" } },
            ["hosts_try"] = new { entries = new[] { "0.0.0.0 ads.example.com" } },
            ["hosts_user_set"] = new { entries = new[] { "140.82.121.3 github.com" } },
            ["hosts_comment"] = new { ids = new[] { "4:0123456789ab" } },
        };
        foreach (var cmd in HelperCommands.Specs.Keys.Where(k => k != "cancel"))
            await c.CallAsync(cmd, args.GetValueOrDefault(cmd));
        Assert.Equal(HelperCommands.Specs.Count - 1, f.Backend.Calls.Count);
    }

    [Fact]
    public async Task Select_reports_progress_then_result()
    {
        await using var f = new HelperFixture();
        await using var c = await f.ConnectAsync();
        var seen = new List<string>();
        var progress = new SyncProgress<SelectorProgress>(p => { lock (seen) seen.Add(p.Message); });

        var task = c.SelectAsync(quick: true, ["youtube"], progress);
        await f.Backend.LongStarted.Task.WaitAsync(Wait);
        f.Backend.Release.SetResult();
        var report = await task.WaitAsync(Wait);

        Assert.False(report.Cancelled);
        Assert.Equal("alt9", report.Best?.Strategy.Id);
        Assert.Equal(System.Security.Authentication.SslProtocols.Tls13, report.Scores[0].Verdicts[0].Probes[0].Tls);
        lock (seen) Assert.Equal(["Проверка без оптимизации…", "Стратегия Alt 9…"], seen);
        Assert.Contains("select True youtube", f.Backend.Calls);
    }

    [Fact]
    public async Task Cancel_returns_partial_select_report()
    {
        await using var f = new HelperFixture();
        await using var c = await f.ConnectAsync();
        using var cts = new CancellationTokenSource();

        var task = c.SelectAsync(quick: false, null, null, cts.Token);
        await f.Backend.LongStarted.Task.WaitAsync(Wait);
        cts.Cancel();
        var report = await task.WaitAsync(Wait);

        Assert.True(report.Cancelled);
        Assert.True(await f.Backend.LongCancelled.Task.WaitAsync(Wait));
    }

    [Fact]
    public async Task Cancel_of_other_long_command_throws_OperationCanceled()
    {
        await using var f = new HelperFixture();
        await using var c = await f.ConnectAsync();
        using var cts = new CancellationTokenSource();
        var logs = new List<string>();

        var task = c.CoreRepairAsync(new SyncProgress<string>(m => { lock (logs) logs.Add(m); }), cts.Token);
        await f.Backend.LongStarted.Task.WaitAsync(Wait);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(Wait));
        lock (logs) Assert.Equal(["Скачиваю ядро…"], logs);
    }

    [Fact]
    public async Task Second_operation_while_select_runs_is_busy_but_status_works()
    {
        await using var f = new HelperFixture();
        await using var c = await f.ConnectAsync();
        await using var other = await f.ConnectAsync();   // второе окно (или администратор) — тот же замок

        var select = c.SelectAsync(quick: true);
        await f.Backend.LongStarted.Task.WaitAsync(Wait);

        var e = await Assert.ThrowsAsync<HelperException>(() => c.EnableAsync("alt9"));
        Assert.Equal(HelperErrors.Busy, e.Code);
        Assert.Contains("select", e.Message);
        var e2 = await Assert.ThrowsAsync<HelperException>(() => other.SearchAsync());
        Assert.Equal(HelperErrors.Busy, e2.Code);

        Assert.Equal("alt9", (await other.StatusAsync()).StrategyId);   // чтение не ждёт
        Assert.DoesNotContain(f.Backend.Calls, x => x.StartsWith("enable") || x.StartsWith("search"));

        f.Backend.Release.SetResult();
        await select.WaitAsync(Wait);
        await c.EnableAsync("alt9");   // замок освобождён
    }

    [Fact]
    public async Task Invalid_requests_never_reach_backend()
    {
        await using var f = new HelperFixture();
        await using var c = await f.ConnectAsync();

        async Task<HelperException> Bad(string cmd, object? args) =>
            await Assert.ThrowsAsync<HelperException>(() => c.CallAsync(cmd, args));

        Assert.Equal(HelperErrors.Invalid, (await Bad("format_disk", null)).Code);
        Assert.Equal(HelperErrors.Invalid, (await Bad("enable", new { strategy = "alt9", extra = 1 })).Code);
        Assert.Equal(HelperErrors.Invalid, (await Bad("enable", new { strategy = @"C:\Windows\System32\cmd.exe" })).Code);
        Assert.Equal(HelperErrors.Invalid, (await Bad("enable", new { strategy = "not-in-catalog" })).Code);
        Assert.Equal(HelperErrors.Invalid, (await Bad("select", new { groups = new[] { @"..\..\secret" } })).Code);
        Assert.Equal(HelperErrors.Invalid, (await Bad("user_targets_set", new { lines = new[] { "\"C:\\x.exe\" --run" } })).Code);
        Assert.Equal(HelperErrors.Invalid, (await Bad("ipset_update", new { from = "file", path = @"C:\list.txt" })).Code);
        Assert.Empty(f.Backend.Calls);

        // Лишнее поле верхнего уровня и мусор — обычный клиент такого не пошлёт, шлём руками.
        using var raw = await f.RawAsync();
        var r1 = JsonDocument.Parse(await raw.AskAsync("""{"id":5,"cmd":"status","exec":"calc.exe"}""")).RootElement;
        Assert.Equal(5, r1.GetProperty("id").GetInt64());
        Assert.False(r1.GetProperty("ok").GetBoolean());
        Assert.Equal("invalid", r1.GetProperty("error").GetProperty("code").GetString());
        var r2 = JsonDocument.Parse(await raw.AskAsync("garbage")).RootElement;
        Assert.Equal("invalid", r2.GetProperty("error").GetProperty("code").GetString());
        // После отказа канал работает дальше.
        var r3 = JsonDocument.Parse(await raw.AskAsync("""{"id":6,"cmd":"status"}""")).RootElement;
        Assert.True(r3.GetProperty("ok").GetBoolean());
        Assert.Equal(["status"], f.Backend.Calls);
    }

    [Fact]
    public async Task Conflict_and_failure_are_mapped()
    {
        await using var f = new HelperFixture();
        await using var c = await f.ConnectAsync();

        f.Backend.EnableThrows = new ConflictException([new Conflict("service", "zapret от Flowseal (служба zapret)", "Запустите service.bat")]);
        var e = await Assert.ThrowsAsync<HelperException>(() => c.EnableAsync("alt9"));
        Assert.Equal(HelperErrors.Conflict, e.Code);
        Assert.Contains("Flowseal", e.Message);
        var list = e.Details!.Value.Deserialize<List<Conflict>>(HelperProtocol.Json)!;
        Assert.Equal("service", list[0].Kind);

        f.Backend.EnableThrows = new InvalidOperationException("winws не запустился: нет WinDivert");
        e = await Assert.ThrowsAsync<HelperException>(() => c.EnableAsync("alt9"));
        Assert.Equal(HelperErrors.Failed, e.Code);
        Assert.Equal("winws не запустился: нет WinDivert", e.Message);
    }

    [Fact]
    public async Task Client_disconnect_stops_process_through_backend()
    {
        await using var f = new HelperFixture();
        var c = await f.ConnectAsync();
        await c.StatusAsync();
        Assert.Equal(1, f.Server.ClientCount);
        Assert.False(f.Backend.ClientsGone.Task.IsCompleted);

        await c.DisposeAsync();
        await f.Backend.ClientsGone.Task.WaitAsync(Wait);
        Assert.Equal(0, f.Server.ClientCount);
    }

    [Fact]
    public async Task Disconnect_during_select_cancels_it_then_stops_process()
    {
        await using var f = new HelperFixture();
        var c = await f.ConnectAsync();
        _ = c.SelectAsync(quick: true).ContinueWith(t => _ = t.Exception);
        await f.Backend.LongStarted.Task.WaitAsync(Wait);

        await c.DisposeAsync();
        Assert.True(await f.Backend.LongCancelled.Task.WaitAsync(Wait));
        await f.Backend.ClientsGone.Task.WaitAsync(Wait);
        // Остановка процесса — после завершения отменённого подбора (он мог вернуть winws).
        var calls = f.Backend.Calls.ToList();
        Assert.True(calls.IndexOf("clients_gone") > calls.FindIndex(x => x.StartsWith("select")));
    }

    [Fact]
    public async Task Process_kept_while_another_client_connected()
    {
        await using var f = new HelperFixture();
        var a = await f.ConnectAsync();
        await using var b = await f.ConnectAsync();
        await a.StatusAsync();
        await b.StatusAsync();
        await a.DisposeAsync();
        await Task.Delay(300);
        Assert.False(f.Backend.ClientsGone.Task.IsCompleted);
        Assert.Equal(1, f.Server.ClientCount);
    }

    [Fact]
    public async Task Pipe_dacl_rejects_user_not_in_list()
    {
        // Разрешён только «чужой» SID: ни текущий пользователь, ни администраторы, ни сам сервер в DACL не попадают.
        var foreign = new SecurityIdentifier("S-1-5-21-1111111111-2222222222-3333333333-1001");
        await using var f = new HelperFixture(o => new HelperServerOptions
        {
            PipeName = o.PipeName, AllowedUser = foreign, AllowAdministrators = false, GrantSelf = false, Log = o.Log,
        });

        var e = await Assert.ThrowsAsync<HelperException>(() => f.ConnectAsync());
        Assert.Equal(HelperErrors.Denied, e.Code);
        // И напрямую, мимо нашего клиента: ядро не даёт открыть канал.
        using var p = new NamedPipeClientStream(".", f.PipeName, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Identification);
        Assert.Throws<UnauthorizedAccessException>(() => p.Connect(2000));
        Assert.Equal(0, f.Server.ClientCount);
        Assert.Empty(f.Backend.Calls);
    }

    [Fact]
    public void Dacl_gives_user_no_pipe_creation_and_denies_network()
    {
        using var me = WindowsIdentity.GetCurrent();
        var user = new SecurityIdentifier("S-1-5-21-1111111111-2222222222-3333333333-1001");
        var sec = HelperServer.BuildSecurity(new HelperServerOptions { AllowedUser = user, GrantSelf = false });
        var rules = sec.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();

        var u = Assert.Single(rules, r => (SecurityIdentifier)r.IdentityReference == user);
        Assert.Equal(AccessControlType.Allow, u.AccessControlType);
        Assert.Equal(0, (int)(u.PipeAccessRights & PipeAccessRights.CreateNewInstance));   // не перехватит подключения
        Assert.Equal(0, (int)(u.PipeAccessRights & (PipeAccessRights.ChangePermissions | PipeAccessRights.TakeOwnership)));
        Assert.Contains(rules, r => ((SecurityIdentifier)r.IdentityReference).IsWellKnown(WellKnownSidType.NetworkSid) && r.AccessControlType == AccessControlType.Deny);
        Assert.Contains(rules, r => ((SecurityIdentifier)r.IdentityReference).IsWellKnown(WellKnownSidType.LocalSystemSid));
        Assert.Contains(rules, r => ((SecurityIdentifier)r.IdentityReference).IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));
        Assert.DoesNotContain(rules, r => (SecurityIdentifier)r.IdentityReference == me.User);
    }

    [Fact]
    public void Authorization_check_after_dacl()
    {
        var allowed = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var other = new SecurityIdentifier("S-1-5-21-1-2-3-1002");
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var o = new HelperServerOptions { AllowedUser = allowed, GrantSelf = false };
        Assert.True(HelperServer.IsAuthorized(allowed, false, o));
        Assert.True(HelperServer.IsAuthorized(system, false, o));
        Assert.True(HelperServer.IsAuthorized(other, elevatedAdmin: true, o));
        Assert.False(HelperServer.IsAuthorized(other, false, o));
        Assert.False(HelperServer.IsAuthorized(other, true, new HelperServerOptions { AllowedUser = allowed, AllowAdministrators = false, GrantSelf = false }));
        Assert.False(HelperServer.IsAuthorized(other, false, new HelperServerOptions { AllowedUser = null, GrantSelf = false }));
    }

    [Fact]
    public async Task Pipe_name_cannot_be_taken_twice()
    {
        // Первый экземпляр — FILE_FLAG_FIRST_PIPE_INSTANCE: занять имя раньше службы или открыть второй сервер нельзя.
        await using var f = new HelperFixture();
        var second = new HelperServer(new FakeHelperBackend(), new HelperServerOptions { PipeName = f.PipeName });
        Assert.ThrowsAny<Exception>(second.Start);
    }

    [Fact]
    public async Task Client_rejects_pipe_not_owned_by_system()
    {
        if (Shell.IsAdmin()) return;   // от администратора владелец канала — Администраторы, подмену так не изобразить
        await using var f = new HelperFixture();
        var e = await Assert.ThrowsAsync<HelperException>(() =>
            HelperClient.ConnectAsync(new HelperClientOptions { PipeName = f.PipeName, RequireTrustedServer = true }));
        Assert.Equal(HelperErrors.Denied, e.Code);
    }

    [Fact]
    public async Task Missing_helper_is_unavailable()
    {
        var e = await Assert.ThrowsAsync<HelperUnavailableException>(() =>
            HelperClient.ConnectAsync(new HelperClientOptions { PipeName = "DpiShell.Helper.Test.none." + Guid.NewGuid().ToString("N"), ConnectTimeout = TimeSpan.FromMilliseconds(300) }));
        Assert.Equal(HelperUnavailableReason.NotRunning, e.Reason);
        Assert.Matches("[а-яА-Я]", e.Message);
    }

    [Fact]
    public async Task Client_without_hello_is_dropped()
    {
        await using var f = new HelperFixture(o => new HelperServerOptions
        {
            PipeName = o.PipeName, AllowedUser = o.AllowedUser, HelloTimeout = TimeSpan.FromMilliseconds(300), Log = o.Log,
        });
        using var p = new NamedPipeClientStream(".", f.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await p.ConnectAsync(5000);
        var buf = new byte[16];
        using var cts = new CancellationTokenSource(Wait);
        Assert.Equal(0, await p.ReadAsync(buf, cts.Token));   // сервер закрыл канал, ничего не сказав
        Assert.Equal(0, f.Server.ClientCount);
    }

    [Fact]
    public async Task Server_stop_disconnects_client_and_stops_process()
    {
        var f = new HelperFixture();
        var c = await f.ConnectAsync();
        var gone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        c.Disconnected += (_, _) => gone.TrySetResult();
        await f.Server.StopAsync();
        await gone.Task.WaitAsync(Wait);
        await Assert.ThrowsAsync<HelperUnavailableException>(() => c.StatusAsync());
        Assert.Contains("clients_gone", f.Backend.Calls);
        await c.DisposeAsync();
    }

    sealed class SyncProgress<T>(Action<T> a) : IProgress<T> { public void Report(T v) => a(v); }
}
