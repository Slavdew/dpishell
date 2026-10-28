using DpiShell.Core.Localization;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace DpiShell.Core.Helper;

public sealed class HelperServerOptions
{
    /// <summary>Имя канала без <c>\\.\pipe\</c>. Тесты берут своё, чтобы не встретиться с установленным помощником.</summary>
    public string PipeName { get; init; } = HelperProtocol.PipeName;
    /// <summary>Пользователь, установивший помощника: ему — чтение и запись канала (не создание экземпляров).</summary>
    public SecurityIdentifier? AllowedUser { get; init; }
    /// <summary>Администраторы (повышенный токен) допускаются. Тест «чужой SID» выключает, если запущен из-под администратора.</summary>
    public bool AllowAdministrators { get; init; } = true;
    /// <summary>
    /// Учётная запись самого сервера получает полный доступ: без FILE_CREATE_PIPE_INSTANCE нельзя открыть второй экземпляр канала.
    /// В службе это SYSTEM (и так в списке). Тест DACL выключает, чтобы проверить отказ «чужому».
    /// </summary>
    public bool GrantSelf { get; init; } = true;
    public int MaxClients { get; init; } = 4;
    /// <summary>За сколько клиент обязан прислать hello после подключения.</summary>
    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Сколько ждать завершения отменённых операций отключившегося клиента перед остановкой winws.</summary>
    public TimeSpan CancelGrace { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>
    /// Проверка образа клиента для действий на весь компьютер (API 1.14, <see cref="HelperCommands.IsMachine"/>): путь exe клиента → та же ли это
    /// сборка, что помощник (<see cref="HelperClientImage.Check"/>). null — не проверять (тесты, где клиент — testhost). SYSTEM и повышенный
    /// администратор не проверяются: им эти действия доступны и без помощника.
    /// </summary>
    public Func<string?, ClientBuild>? CheckClient { get; init; }
    public Action<string>? Log { get; init; }
}

/// <summary>
/// Сервер канала помощника (helper-protocol §3).
/// <para>
/// Доступ — два слоя. DACL канала: SYSTEM и Администраторы — полный, разрешённый пользователь — чтение/запись, сеть — запрет;
/// создавать свои экземпляры канала (и перехватывать подключения) обычный пользователь не может, а первый экземпляр
/// создаётся с FILE_FLAG_FIRST_PIPE_INSTANCE — занять имя заранее не выйдет. Второй слой — проверка клиента по его токену
/// через олицетворение (<see cref="NamedPipeServerStream.RunAsClient"/>): это ровно та учётная запись, которой ядро
/// разрешило открыть канал, без гонки с повторным использованием PID (как у GetNamedPipeClientProcessId → OpenProcess)
/// и без открытия чужих процессов. Клиент подключается с уровнем Identification — помощник может узнать, кто это,
/// но не действовать от его имени.
/// </para>
/// <para>
/// Третий слой — только для действий на весь компьютер (hosts, DNS, прокси машины; API 1.14): образ процесса клиента
/// (<see cref="HelperServerOptions.CheckClient"/>) должен быть той же сборкой DpiShell, что помощник. Это отсекает чужую программу,
/// пишущую в канал сама, но не процесс того же пользователя, внедрившийся в настоящий DpiShell.exe.
/// </para>
/// </summary>
public sealed class HelperServer : IAsyncDisposable
{
    readonly IHelperBackend _backend;
    readonly HelperServerOptions _o;
    readonly CancellationTokenSource _stop = new();
    readonly SemaphoreSlim _exclusive = new(1, 1);
    readonly ConcurrentDictionary<Connection, byte> _connections = new();
    NamedPipeServerStream? _first;
    Task? _accept;
    int _clients;
    volatile string? _current;

    public HelperServer(IHelperBackend backend, HelperServerOptions? options = null)
    {
        _backend = backend;
        _o = options ?? new HelperServerOptions();
    }

    /// <summary>Сколько проверенных клиентов подключено.</summary>
    public int ClientCount => Volatile.Read(ref _clients);

    /// <summary>
    /// Открыть канал и начать приём. Имя уже занято (другой помощник или чужая подмена) — исключение сразу,
    /// а не тихая работа на втором экземпляре.
    /// </summary>
    public void Start()
    {
        if (_accept is not null) throw new InvalidOperationException("Сервер уже запущен.");
        _first = CreateInstance(first: true);
        _accept = Task.Run(AcceptLoopAsync);
    }

    public async Task StopAsync()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();
        _first?.Dispose();
        foreach (var c in _connections.Keys) c.Close();
        if (_accept is not null) { try { await _accept; } catch { } }
        // Помощник останавливается — свой winws режима без службы не оставляем (его убил бы и объект задания).
        try { _backend.OnClientsGone(); } catch (Exception e) { Log("остановка процесса: " + e.Message); }
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    /// <summary>DACL канала. Чистая функция — проверяется тестом.</summary>
    public static PipeSecurity BuildSecurity(HelperServerOptions o)
    {
        var ps = new PipeSecurity();
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        if (o.AllowAdministrators)
            ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        if (o.AllowedUser is { } user)
            ps.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        if (o.GrantSelf)
        {
            using var me = WindowsIdentity.GetCurrent();
            if (me.User is { } self) ps.AddAccessRule(new PipeAccessRule(self, PipeAccessRights.FullControl, AccessControlType.Allow));
        }
        return ps;
    }

    /// <summary>Кого пускаем (второй слой после DACL): SYSTEM, разрешённый пользователь, повышенный администратор, сам сервер.</summary>
    public static bool IsAuthorized(SecurityIdentifier user, bool elevatedAdmin, HelperServerOptions o, SecurityIdentifier? self = null)
    {
        if (user.IsWellKnown(WellKnownSidType.LocalSystemSid)) return true;
        if (o.AllowedUser is { } allowed && user == allowed) return true;
        if (o.AllowAdministrators && elevatedAdmin) return true;
        return o.GrantSelf && self is not null && user == self;
    }

    NamedPipeServerStream CreateInstance(bool first) =>
        NamedPipeServerStreamAcl.Create(_o.PipeName, PipeDirection.InOut, _o.MaxClients, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None), 0, 0, BuildSecurity(_o));

    async Task AcceptLoopAsync()
    {
        var listening = _first;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                listening ??= CreateInstance(first: false);
                await listening.WaitForConnectionAsync(_stop.Token);
                var pipe = listening;
                listening = null;
                _ = Task.Run(() => HandleClientAsync(pipe));
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (IOException e)
            {
                // Все экземпляры заняты или клиент отвалился до приёма — пробуем снова.
                listening?.Dispose();
                listening = null;
                Log("канал: " + e.Message);
                try { await Task.Delay(200, _stop.Token); } catch (OperationCanceledException) { break; }
            }
        }
        listening?.Dispose();
    }

    async Task HandleClientAsync(NamedPipeServerStream pipe)
    {
        var conn = new Connection(pipe, _stop.Token, Log);
        _connections[conn] = 0;
        bool counted = false;
        try
        {
            var reader = new LineReader(pipe);
            // Клиент первым шлёт hello: олицетворение возможно только после чтения из канала.
            string? hello;
            using (var t = CancellationTokenSource.CreateLinkedTokenSource(conn.Closing))
            {
                t.CancelAfter(_o.HelloTimeout);
                hello = await reader.ReadLineAsync(t.Token);
            }
            if (hello is null || !IsHello(hello)) { Log("клиент не прислал hello — отключён"); return; }

            var (who, ok, elevated) = Identify(pipe);
            if (!ok)
            {
                Log($"отказ клиенту {who}");
                await conn.SendDirectAsync(HelperProtocol.Error(0, HelperErrors.Denied, HelperErrors.Text(HelperErrors.Denied)));
                return;
            }
            conn.ClientSid = who;   // при успехе Identify возвращает SID токена клиента
            conn.Build = (elevated && _o.AllowAdministrators) || _o.CheckClient is not { } check ? ClientBuild.Same : CheckBuild(check, pipe);
            Interlocked.Increment(ref _clients);
            counted = true;
            conn.StartWriter();
            conn.Send(HelperProtocol.Hello(machine: conn.Build == ClientBuild.Same));

            while (!conn.Closing.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(conn.Closing);
                if (line is null) break;
                HelperCall call;
                try { call = HelperCommands.Parse(line, _backend); }
                catch (HelperRequestException e)
                {
                    conn.Send(HelperProtocol.Error(e.Id, HelperErrors.Invalid, e.Message));
                    continue;
                }
                if (call.Target is { } target)
                {
                    conn.Send(HelperProtocol.Ok(call.Id, new JsonObject { ["cancelled"] = conn.Cancel(target) }));
                    continue;
                }
                if (!conn.TryRegister(call.Id, out var cts))
                {
                    conn.Send(HelperProtocol.Error(call.Id, HelperErrors.Invalid, "Запрос с таким id ещё выполняется."));
                    continue;
                }
                call = call with { ClientSid = conn.ClientSid };
                conn.Track(Task.Run(() => ExecuteAsync(call, conn, cts)));
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (InvalidDataException e) { Log(e.Message); }
        finally
        {
            conn.CancelAll();
            await conn.WaitRunningAsync(_o.CancelGrace);
            conn.Close();
            _connections.TryRemove(conn, out _);
            if (counted && Interlocked.Decrement(ref _clients) == 0 && !_stop.IsCancellationRequested)
                await ClientsGoneAsync();
        }
    }

    /// <summary>Последнее окно отключилось: в режиме без службы winws гасится (через backend, под тем же замком, что и команды).</summary>
    async Task ClientsGoneAsync()
    {
        await _exclusive.WaitAsync();
        try { if (ClientCount == 0) _backend.OnClientsGone(); }
        catch (Exception e) { Log("остановка процесса: " + e.Message); }
        finally { _exclusive.Release(); }
    }

    static bool IsHello(string line)
    {
        try { return JsonNode.Parse(line) is JsonObject o && o["hello"] is JsonObject; }
        catch (Exception e) when (e is System.Text.Json.JsonException or ArgumentException or InvalidOperationException) { return false; }
    }

    /// <summary>Сборка клиента по образу его процесса; сбой проверки — «другая» (действия на весь компьютер не пройдут, остальное работает).</summary>
    ClientBuild CheckBuild(Func<string?, ClientBuild> check, NamedPipeServerStream pipe)
    {
        string? path = null;
        try
        {
            path = HelperClientImage.ClientPath(pipe);
            var b = check(path);
            if (b != ClientBuild.Same) Log($"клиент {path ?? "(образ не прочитан)"}: {b} — действия на весь компьютер не принимаются");
            return b;
        }
        catch (Exception e)
        {
            Log($"проверка клиента {path}: {e.Message}");
            return ClientBuild.Other;
        }
    }

    (string Who, bool Ok, bool Elevated) Identify(NamedPipeServerStream pipe)
    {
        SecurityIdentifier? sid = null;
        bool admin = false;
        try
        {
            pipe.RunAsClient(() =>
            {
                using var id = WindowsIdentity.GetCurrent(TokenAccessLevels.Query | TokenAccessLevels.Duplicate);
                // ImpersonationLevel None — это токен процесса, а не клиента: олицетворение не состоялось, не пускаем.
                if (id.IsAnonymous || id.ImpersonationLevel == TokenImpersonationLevel.None) return;
                sid = id.User;
                admin = new WindowsPrincipal(id).IsInRole(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            return ("(не удалось определить: " + e.Message + ")", false, false);
        }
        if (sid is null) return ("(аноним)", false, false);
        using var me = WindowsIdentity.GetCurrent();
        return (sid.Value, IsAuthorized(sid, admin, _o, me.User), admin || sid.IsWellKnown(WellKnownSidType.LocalSystemSid));
    }

    async Task ExecuteAsync(HelperCall call, Connection conn, CancellationTokenSource cts)
    {
        // Язык ответа — на запрос, а не на помощника: окон может быть несколько, и у каждого свой. Scope живёт в этом запросе и его продолжениях.
        using var lang = L.Scope(call.Lang);
        bool held = false;
        try
        {
            // Действия на весь компьютер — только от окна той же сборки (API 1.14); остальные команды этого клиента работают как прежде.
            if (conn.Build != ClientBuild.Same && HelperCommands.IsMachine(call))
            {
                Log($"{call.Cmd}: отказ — клиент {conn.Build}");
                conn.Send(HelperProtocol.Error(call.Id, HelperErrors.Denied, HelperClientImage.Refusal(conn.Build)));
                return;
            }
            if (call.Spec.Exclusive)
            {
                if (!_exclusive.Wait(0))
                {
                    var now = _current;
                    conn.Send(HelperProtocol.Error(call.Id, HelperErrors.Busy,
                        HelperErrors.Text(HelperErrors.Busy) + (now is null ? "" : L.T("core.helper.busy_now", new { cmd = now }))));
                    return;
                }
                held = true;
                _current = call.Cmd;
            }
            var result = await DispatchAsync(call, p => conn.Send(HelperProtocol.Progress(call.Id, p)), cts.Token);
            conn.Send(HelperProtocol.Ok(call.Id, result));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            conn.Send(HelperProtocol.Error(call.Id, HelperErrors.Cancelled, HelperErrors.Text(HelperErrors.Cancelled)));
        }
        catch (ConflictException e)
        {
            conn.Send(HelperProtocol.Error(call.Id, HelperErrors.Conflict, e.Message, e.Conflicts));
        }
        catch (Exception e)
        {
            Log($"{call.Cmd}: {e}");
            conn.Send(HelperProtocol.Error(call.Id, HelperErrors.Failed, string.IsNullOrWhiteSpace(e.Message) ? HelperErrors.Text(HelperErrors.Failed) : e.Message));
        }
        finally
        {
            if (held) { _current = null; _exclusive.Release(); }
            conn.Unregister(call.Id);
            cts.Dispose();
        }
    }

    /// <summary>Команда → метод backend. Синхронные операции Shell (sc, ожидания) уходят в пул потоков.</summary>
    async Task<object?> DispatchAsync(HelperCall c, Action<object> progress, CancellationToken ct)
    {
        var b = _backend;
        var log = new SyncProgress<string>(m => progress(new { message = m }));
        switch (c.Cmd)
        {
            // Версия протокола и список команд — из белого списка этого помощника, а не от бэкенда (helper-protocol §6).
            case "status": return (await Task.Run(b.Status, ct)) with { Api = HelperProtocol.Api, Commands = HelperCommands.Names };
            case "enable": await Task.Run(() => b.Enable(c.Str("strategy")!), ct); return null;
            case "disable": await Task.Run(b.Disable, ct); return null;
            case "set_run_mode": await Task.Run(() => b.SetRunMode(c.Bool("service")), ct); return null;
            case "select":
                b.SetCheckDns(c.Str("dns") ?? CheckDns.Secure); b.SetSelectOwnOnly(c.Bool("ownOnly"));
                return await Task.Run(() => b.SelectAsync(c.Bool("quick", true), c.List("groups"), new SyncProgress<SelectorProgress>(p => progress(p)), ct), ct);
            case "strategy_add": return await Task.Run(() => b.AddStrategy(c.Str("title")!, c.Map("roles")!, c.Str("source")!), ct);
            case "strategy_delete": await Task.Run(() => b.DeleteStrategy(c.Str("strategy")!), ct); return null;
            case "strategy_check":
                b.SetCheckDns(c.Str("dns") ?? CheckDns.Secure); b.SetSelectOwnOnly(c.Bool("ownOnly"));
                return await Task.Run(() => b.CheckStrategyAsync(c.Str("strategy")!, c.List("groups"), new SyncProgress<SelectorProgress>(p => progress(p)), ct), ct);
            case "search":
                b.SetCheckDns(c.Str("dns") ?? CheckDns.Secure); b.SetSelectOwnOnly(c.Bool("ownOnly"));
                return await Task.Run(() => b.SearchAsync(c.Str("base"), c.List("groups"), new SyncProgress<SelectorProgress>(p => progress(p)), ct), ct);
            case "ipset_mode":
                await Task.Run(() => b.SetIpsetMode(c.Str("mode") == "list" ? IpsetMode.List : IpsetMode.Off), ct); return null;
            case "all_addresses_start": await Task.Run(b.StartAllAddresses, ct); return null;
            case "all_addresses_stop": return new { stopped = await Task.Run(b.StopAllAddresses, ct) };
            case "game_mode":
                return await Task.Run(() => b.SetGameModeAsync(c.Str("mode") == "all" ? GameMode.TcpUdp : GameMode.Off, log, ct), ct);
            case "ipset_update":
                return c.Str("from") == "manifest"
                    ? await Task.Run(() => b.UpdateIpsetFromManifestAsync(c.Bool("confirmed"), log, ct), ct)
                    : await Task.Run(() => b.UpdateIpsetFromContent(c.Str("content")!, c.Bool("confirmed")), ct);
            case "user_ipset_set":
                return await Task.Run(() => b.WriteUserIpset(c.List("lines")!, c.Bool("exclude"), c.Bool("confirmed")), ct);
            case "user_targets_set": return await Task.Run(() => b.SetUserTargets(c.List("lines")!), ct);
            case "user_groups_set": return await Task.Run(() => b.SetUserGroups(c.Groups("groups")!), ct);
            case "user_exclude_set": return await Task.Run(() => b.SetUserExclude(c.List("lines")!), ct);
            case "settings_export_read": return await Task.Run(b.SettingsExportRead, ct);
            case "settings_import": return await Task.Run(() => b.SettingsImportAsync(c.Str("plan")!, ct), ct);
            case "hosts_check":
                return await Task.Run(() => b.HostsCheckAsync(new SyncProgress<HostsCheckProgress>(p => progress(p)), ct), ct);
            case "hosts_preview": return await Task.Run(b.HostsPreview, ct);
            case "hosts_apply": return await Task.Run(b.HostsApply, ct);
            case "hosts_revert": return await Task.Run(b.HostsRevert, ct);
            case "hosts_recheck": return await Task.Run(() => b.HostsRecheckAsync(ct), ct);
            case "hosts_try":
                return await Task.Run(() => b.HostsTryAsync(Entries(c, "entries"), new SyncProgress<HostsCheckProgress>(p => progress(p)), ct), ct);
            case "hosts_audit":
                return await Task.Run(() => b.HostsAuditAsync(new SyncProgress<HostsCheckProgress>(p => progress(p)), ct), ct);
            case "hosts_user_set": return await Task.Run(() => b.HostsUserSetAsync(Entries(c, "entries"), ct), ct);
            case "hosts_comment": return await Task.Run(() => b.HostsComment(c.List("ids")!), ct);
            case "hosts_uncomment": return await Task.Run(b.HostsUncomment, ct);
            case "hosts_restore": return await Task.Run(b.HostsRestore, ct);
            case "stop_foreign": return await Task.Run(b.StopForeign, ct);
            case "core_repair": await Task.Run(() => b.CoreRepairAsync(log, ct), ct); return null;
            case "core_offline_part": return new { size = await Task.Run(() => b.CoreOfflinePart(c.Bytes("data")!, c.Bool("first")), ct) };
            case "core_offline_install": await Task.Run(() => b.CoreOfflineInstallAsync(log, ct), ct); return null;
            case "remove_all": await Task.Run(b.RemoveAll, ct); return null;
            case "clear_history": return new { removed = await Task.Run(b.ClearHistory, ct) };
            case "read_results": return await Task.Run(b.ReadResults, ct);
            case "details": return await Task.Run(b.Details, ct);
            case "log_tail": return new { lines = await Task.Run(b.LogTail, ct) };
            case "choose": await Task.Run(() => b.Choose(c.Str("strategy")!), ct); return null;
            case "tcp_timestamps_on": return new { enabled = await Task.Run(b.TcpTimestampsOn, ct) };
            case "data_update": return await Task.Run(() => b.UpdateDataAsync(c.Bool("confirmed"), log, ct), ct);
            case "dns_status": return await Task.Run(b.DnsStatus, ct);
            case "dns_set":
                return await Task.Run(() => b.DnsSetAsync(c.Str("preset")!, c.List("ips"), c.Str("doh"), c.Bool("fallback"), ct), ct);
            case "dns_restore": return await Task.Run(() => b.DnsRestoreAsync(ct), ct);
            case "rec_start":
                return await Task.Run(() => b.RecStart(c.Str("mode") == "process" ? RecordingMode.Process : RecordingMode.NewProcesses, c.Int("pid"), c.Bool("driver", true)), ct);
            case "rec_status": return await Task.Run(b.RecStatus, ct);
            case "rec_stop": return await Task.Run(b.RecStop, ct);
            case "app_profiles": return await Task.Run(b.AppProfilesInfo, ct);
            case "app_profile_set":
                // Ни одного поля 1.22 — запрос старого окна: сигнатуру существующего профиля не стирать (signature = null).
                return await Task.Run(() => b.AppProfileSet(c.Str("id"), c.Str("title")!, c.Str("tcp"), c.Str("udp"), c.List("addresses") ?? [], c.Int("variant"),
                    !ProfileFields.Any(c.Args.ContainsKey) ? null
                    : new ProfileSignature(c.Int("tcpLen"), c.Str("tcpHex") ?? "", c.Int("udpLen"), c.Str("udpHex") ?? "", c.Str("tcpDesync"), c.Str("udpDesync"), c.Str("tcpFilter") ?? "", c.Str("udpFilter") ?? "")), ct);
            case "changes_list": return await Task.Run(b.ChangesList, ct);
            case "changes_undo_all": return await Task.Run(() => b.ChangesUndoAllAsync(ct), ct);
            case "changes_undo": return await Task.Run(() => b.ChangesUndoAsync(c.Str("id")!, ct), ct);
            case "changes_undo_group": return await Task.Run(() => b.ChangesUndoGroupAsync(c.Str("id")!, ct), ct);
            case "proxy_status": return await Task.Run(() => b.ProxyStatus(c.ClientSid), ct);
            case "proxy_user_set":
                return await Task.Run(() => b.ProxyUserSet(c.ClientSid, c.Bool("enabled"), c.Str("server"), c.Str("bypass"), c.Str("pac")), ct);
            case "proxy_machine_set":
                return await Task.Run(() => b.ProxyMachineSet(c.Bool("enabled"), c.Str("server"), c.Str("bypass"), c.Str("pac"), c.Bool("winhttp")), ct);
            case "proxy_machine_restore": return await Task.Run(b.ProxyMachineRestore, ct);
            case "app_profile_remove": return new { removed = await Task.Run(() => b.AppProfileRemove(c.Str("id")!), ct) };
            case "app_profile_options":
                return await Task.Run(() => b.AppProfileOptions(c.Str("id")!, c.Args.ContainsKey("enabled") ? c.Bool("enabled") : null, c.Str("strategy")), ct);
            case "app_profiles_separate": return await Task.Run(() => b.AppProfilesSeparate(c.Bool("on")), ct);
            case "advanced_get": return await Task.Run(b.AdvancedGet, ct);
            case "advanced_set": return await Task.Run(() => b.AdvancedSet(c.Bool("enabled"), c.Str("others")!, c.Rows("rows")!), ct);
            case "joint_get": return await Task.Run(b.JointGet, ct);
            case "joint_set": return await Task.Run(() => b.JointSet(c.Bool("active"), c.List("owned") ?? []), ct);
            case "av_exclusion_status": return await Task.Run(b.AvExclusionStatus, ct);
            case "time_sync": return await Task.Run(b.TimeSync, ct);
            case "av_exclusion_add": return await Task.Run(b.AvExclusionAdd, ct);
            case "av_exclusion_remove": return await Task.Run(b.AvExclusionRemove, ct);
        }
        // Белый список и диспетчер должны совпадать — тест проверяет, что сюда не попасть.
        throw new HelperRequestException(c.Id, $"Команда «{c.Cmd}» не поддерживается.");
    }

    /// <summary>Поля <c>app_profile_set</c> с API 1.22 (сигнатура, свой фильтр, свои параметры).</summary>
    static readonly string[] ProfileFields = ["tcpLen", "tcpHex", "udpLen", "udpHex", "tcpDesync", "udpDesync", "tcpFilter", "udpFilter"];

    /// <summary>Пары «адрес имя», уже приведённые проверкой запроса к каноническому виду.</summary>
    static IReadOnlyList<HostsEntry> Entries(HelperCall c, string name) =>
        [.. (c.List(name) ?? []).Select(s => HostsPaste.TryEntry(s, false, out var e) ? e : throw new HelperRequestException(c.Id, $"{c.Cmd}: {name}: не пара «адрес имя»."))];

    void Log(string message) => _o.Log?.Invoke(message);

    /// <summary>IProgress без пересылки в пул потоков: ход уходит в канал в том порядке, в каком пришёл, и до финального ответа.</summary>
    sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    /// <summary>Одно подключение: очередь записи (ход и ответы из разных потоков — по одному), выполняемые запросы.</summary>
    sealed class Connection(NamedPipeServerStream pipe, CancellationToken serverStop, Action<string> log)
    {
        readonly CancellationTokenSource _closing = CancellationTokenSource.CreateLinkedTokenSource(serverStop);
        readonly Channel<string> _out = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        readonly ConcurrentDictionary<long, CancellationTokenSource> _pending = new();
        readonly ConcurrentBag<Task> _running = [];

        public CancellationToken Closing => _closing.Token;

        /// <summary>SID пользователя клиента (по токену канала).</summary>
        public string? ClientSid { get; set; }

        /// <summary>Сборка клиента по образу процесса (API 1.14): не <see cref="ClientBuild.Same"/> — действия на весь компьютер не принимаются.</summary>
        public ClientBuild Build { get; set; } = ClientBuild.Same;

        public void StartWriter() => _ = Task.Run(WriterAsync);

        async Task WriterAsync()
        {
            try
            {
                await foreach (var line in _out.Reader.ReadAllAsync(Closing))
                {
                    var bytes = Encoding.UTF8.GetBytes(line);
                    await pipe.WriteAsync(bytes, Closing);
                    await pipe.FlushAsync(Closing);
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
            {
                if (e is IOException) log("запись в канал: " + e.Message);
                Close();
            }
        }

        public void Send(string line) => _out.Writer.TryWrite(line);

        /// <summary>Запись до запуска очереди (отказ в доступе) — сразу и с ожиданием.</summary>
        public async Task SendDirectAsync(string line)
        {
            try
            {
                await pipe.WriteAsync(Encoding.UTF8.GetBytes(line), Closing);
                await pipe.FlushAsync(Closing);
                // Дать клиенту дочитать отказ, но не ждать вечно клиента, который не читает.
                await Task.WhenAny(Task.Run(pipe.WaitForPipeDrain), Task.Delay(2000));
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException) { }
        }

        public bool TryRegister(long id, out CancellationTokenSource cts)
        {
            cts = CancellationTokenSource.CreateLinkedTokenSource(Closing);
            if (_pending.TryAdd(id, cts)) return true;
            cts.Dispose();
            return false;
        }

        public void Unregister(long id) => _pending.TryRemove(id, out _);

        public bool Cancel(long id)
        {
            if (!_pending.TryGetValue(id, out var cts)) return false;
            try { cts.Cancel(); return true; } catch (ObjectDisposedException) { return false; }
        }

        public void CancelAll()
        {
            foreach (var id in _pending.Keys) Cancel(id);
        }

        public void Track(Task t) => _running.Add(t);

        public async Task WaitRunningAsync(TimeSpan grace)
        {
            var all = Task.WhenAll(_running);
            await Task.WhenAny(all, Task.Delay(grace));
        }

        public void Close()
        {
            try { _closing.Cancel(); } catch (ObjectDisposedException) { }
            _out.Writer.TryComplete();
            try { pipe.Dispose(); } catch { }
        }
    }
}
