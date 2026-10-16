using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DpiShell.Core.Helper;

public sealed class HelperClientOptions
{
    public string PipeName { get; init; } = HelperProtocol.PipeName;
    /// <summary>Сколько ждать канал и приветствие помощника.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>
    /// Канал должен принадлежать SYSTEM или Администраторам — иначе это подмена (обычный пользователь занял имя раньше службы).
    /// Тесты выключают: их сервер работает от текущего пользователя.
    /// </summary>
    public bool RequireTrustedServer { get; init; } = true;
}

/// <summary>
/// Клиент помощника для окна. Один экземпляр — одно подключение; запросы можно слать параллельно.
/// Помощника нет или он молчит — <see cref="HelperUnavailableException"/>; помощник ответил ошибкой — <see cref="HelperException"/>.
/// Отмена через CancellationToken шлёт помощнику cancel; подбор после отмены возвращает частичный отчёт (Cancelled = true),
/// остальные команды — <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class HelperClient : IAsyncDisposable, IDisposable
{
    readonly NamedPipeClientStream _pipe;
    readonly LineReader _reader;
    readonly SemaphoreSlim _write = new(1, 1);
    readonly ConcurrentDictionary<long, Pending> _pending = new();
    readonly CancellationTokenSource _closed = new();
    long _nextId;
    Task? _readLoop;

    /// <summary>Версия программы помощника (из hello): окно новее — пора обновить помощника.</summary>
    public string HelperVersion { get; private set; } = "";
    public int Protocol { get; private set; }
    public bool IsConnected => !_closed.IsCancellationRequested && _pipe.IsConnected;
    /// <summary>Связь оборвалась (помощник остановлен, перезапущен) — окно переподключается или показывает «не отвечает».</summary>
    public event EventHandler? Disconnected;

    HelperClient(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _reader = new LineReader(pipe);
    }

    public static async Task<HelperClient> ConnectAsync(HelperClientOptions? options = null, CancellationToken ct = default)
    {
        var o = options ?? new HelperClientOptions();
        // Identification: помощник узнаёт, кто подключился, но не может действовать от имени пользователя.
        var pipe = new NamedPipeClientStream(".", o.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            try { await pipe.ConnectAsync(o.ConnectTimeout, ct); }
            catch (TimeoutException e) { throw new HelperUnavailableException(WhyUnavailable(o.PipeName), e); }
            catch (UnauthorizedAccessException) { throw new HelperException(HelperErrors.Denied, HelperErrors.Text(HelperErrors.Denied)); }
            catch (IOException e) { throw new HelperUnavailableException(HelperUnavailableReason.NotResponding, e); }

            if (o.RequireTrustedServer && !IsTrustedOwner(pipe))
                throw new HelperException(HelperErrors.Denied, "Канал помощника создан не системой — подключение отменено.");

            var client = new HelperClient(pipe);
            await client.HandshakeAsync(o.ConnectTimeout, ct);
            client._readLoop = Task.Run(client.ReadLoopAsync);
            return client;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    /// <summary>Владелец канала — SYSTEM или Администраторы.</summary>
    static bool IsTrustedOwner(NamedPipeClientStream pipe)
    {
        try
        {
            var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            return owner is not null && (owner.IsWellKnown(WellKnownSidType.LocalSystemSid) || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));
        }
        catch (Exception e) when (e is UnauthorizedAccessException or InvalidOperationException or IOException) { return false; }
    }

    /// <summary>Канала нет: служба не установлена или не запущена; канал есть — помощник занят или завис.</summary>
    static HelperUnavailableReason WhyUnavailable(string pipeName)
    {
        bool pipeExists;
        try { pipeExists = Directory.EnumerateFiles(@"\\.\pipe\").Any(p => string.Equals(Path.GetFileName(p), pipeName, StringComparison.OrdinalIgnoreCase)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { pipeExists = false; }
        if (pipeExists) return HelperUnavailableReason.NotResponding;
        if (pipeName == HelperProtocol.PipeName)
        {
            try { if (ServiceManager.Query(HelperProtocol.ServiceName) == SvcState.NotInstalled) return HelperUnavailableReason.NotInstalled; }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }
        return HelperUnavailableReason.NotRunning;
    }

    async Task HandshakeAsync(TimeSpan timeout, CancellationToken ct)
    {
        await WriteLineAsync(new JsonObject { ["hello"] = new JsonObject { ["protocol"] = HelperProtocol.Version } }.ToJsonString(), ct);
        string? line;
        using (var t = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            t.CancelAfter(timeout);
            try { line = await _reader.ReadLineAsync(t.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new HelperUnavailableException(HelperUnavailableReason.NotResponding); }
            catch (IOException e) { throw new HelperUnavailableException(HelperUnavailableReason.NotResponding, e); }
        }
        if (line is null) throw new HelperUnavailableException(HelperUnavailableReason.NotResponding);
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (root.TryGetProperty("hello", out var hello))
        {
            Protocol = hello.TryGetProperty("protocol", out var p) && p.TryGetInt32(out var pv) ? pv : 0;
            HelperVersion = hello.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
            return;
        }
        throw ToException(root);
    }

    /// <summary>
    /// Вызвать команду и дождаться финального ответа. <paramref name="progress"/> получает события хода (объект progress).
    /// Результат — поле result (для команд без результата — JsonValueKind.Null).
    /// </summary>
    public async Task<JsonElement> CallAsync(string cmd, object? args = null, IProgress<JsonElement>? progress = null, CancellationToken ct = default)
    {
        if (!IsConnected) throw new HelperUnavailableException(HelperUnavailableReason.Disconnected);
        long id = Interlocked.Increment(ref _nextId);
        var pending = new Pending(progress);
        _pending[id] = pending;
        var request = new JsonObject { ["id"] = id, ["cmd"] = cmd };
        if (args is not null) request["args"] = args as JsonNode ?? JsonSerializer.SerializeToNode(args, args.GetType(), HelperProtocol.Json);

        try
        {
            await WriteLineAsync(request.ToJsonString(HelperProtocol.Json), CancellationToken.None);
            bool cancelSent = false;
            using var reg = ct.Register(() =>
            {
                cancelSent = true;
                _ = SendCancelAsync(id);
            });
            var reply = await pending.Done.Task;
            if (reply.GetProperty("ok").GetBoolean())
                return reply.TryGetProperty("result", out var r) ? r : default;
            var ex = ToException(reply);
            if (ex is HelperException { Code: HelperErrors.Cancelled } && (cancelSent || ct.IsCancellationRequested))
                throw new OperationCanceledException(ex.Message, ex, ct);
            throw ex;
        }
        finally { _pending.TryRemove(id, out _); }
    }

    public async Task<T> CallAsync<T>(string cmd, object? args = null, IProgress<JsonElement>? progress = null, CancellationToken ct = default)
    {
        var r = await CallAsync(cmd, args, progress, ct);
        return r.Deserialize<T>(HelperProtocol.Json)!;
    }

    async Task SendCancelAsync(long target)
    {
        long id = Interlocked.Increment(ref _nextId);
        try { await WriteLineAsync(new JsonObject { ["id"] = id, ["cmd"] = "cancel", ["target"] = target }.ToJsonString(), CancellationToken.None); }
        catch (Exception e) when (e is IOException or ObjectDisposedException or HelperUnavailableException) { }
    }

    async Task WriteLineAsync(string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        await _write.WaitAsync(ct);
        try
        {
            await _pipe.WriteAsync(bytes, ct);
            await _pipe.FlushAsync(ct);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            OnClosed();
            throw new HelperUnavailableException(HelperUnavailableReason.Disconnected, e);
        }
        finally { _write.Release(); }
    }

    async Task ReadLoopAsync()
    {
        try
        {
            while (!_closed.IsCancellationRequested)
            {
                var line = await _reader.ReadLineAsync(_closed.Token);
                if (line is null) break;
                JsonElement msg;
                try { using var doc = JsonDocument.Parse(line); msg = doc.RootElement.Clone(); }
                catch (JsonException) { continue; }
                if (!msg.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var id)) continue;
                if (!_pending.TryGetValue(id, out var p)) continue;
                if (msg.TryGetProperty("progress", out var prog))
                {
                    try { p.Progress?.Report(prog); } catch { }
                }
                else if (msg.TryGetProperty("ok", out _)) p.Done.TrySetResult(msg);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or InvalidDataException) { }
        OnClosed();
    }

    void OnClosed()
    {
        if (_closed.IsCancellationRequested) return;
        try { _closed.Cancel(); } catch (ObjectDisposedException) { return; }
        foreach (var p in _pending.Values)
            p.Done.TrySetException(new HelperUnavailableException(HelperUnavailableReason.Disconnected));
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    static Exception ToException(JsonElement reply)
    {
        if (reply.TryGetProperty("error", out var err))
        {
            var code = err.TryGetProperty("code", out var c) ? c.GetString() ?? HelperErrors.Failed : HelperErrors.Failed;
            var message = err.TryGetProperty("message", out var m) ? m.GetString() ?? HelperErrors.Text(code) : HelperErrors.Text(code);
            JsonElement? details = err.TryGetProperty("details", out var d) ? d.Clone() : null;
            return new HelperException(code, message, details);
        }
        return new HelperException(HelperErrors.Failed, HelperErrors.Text(HelperErrors.Failed));
    }

    public void Dispose()
    {
        OnClosed();
        _pipe.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        if (_readLoop is not null) { try { await _readLoop; } catch { } }
    }

    sealed class Pending(IProgress<JsonElement>? progress)
    {
        public IProgress<JsonElement>? Progress { get; } = progress;
        public TaskCompletionSource<JsonElement> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // ---------- типизированные команды для окна ----------

    static IProgress<JsonElement>? Map<T>(IProgress<T>? p) =>
        p is null ? null : new Relay(e => { if (e.Deserialize<T>(HelperProtocol.Json) is { } v) p.Report(v); });

    static IProgress<JsonElement>? MapLog(IProgress<string>? p) =>
        p is null ? null : new Relay(e => { if (e.TryGetProperty("message", out var m) && m.GetString() is { } s) p.Report(s); });

    sealed class Relay(Action<JsonElement> a) : IProgress<JsonElement> { public void Report(JsonElement v) => a(v); }

    public Task<HelperStatus> StatusAsync(CancellationToken ct = default) => CallAsync<HelperStatus>("status", null, null, ct);
    public Task EnableAsync(string strategyId, CancellationToken ct = default) => CallAsync("enable", new { strategy = strategyId }, null, ct);
    public Task DisableAsync(CancellationToken ct = default) => CallAsync("disable", null, null, ct);
    public Task SetRunModeAsync(bool service, CancellationToken ct = default) => CallAsync("set_run_mode", new { service }, null, ct);

    /// <summary>Подбор. Отмена — частичный отчёт с Cancelled = true (как у Shell.SelectAsync).</summary>
    public Task<SelectionReport> SelectAsync(bool quick, IReadOnlyList<string>? groups = null, IProgress<SelectorProgress>? progress = null, CancellationToken ct = default) =>
        CallAsync<SelectionReport>("select", new { quick, groups }, Map(progress), ct);

    public Task<SearchReport> SearchAsync(string? baseId = null, IReadOnlyList<string>? groups = null, IProgress<SelectorProgress>? progress = null, CancellationToken ct = default) =>
        CallAsync<SearchReport>("search", new { @base = baseId, groups }, Map(progress), ct);

    /// <summary>Off или List; «Все адреса» — <see cref="StartAllAddressesAsync"/>.</summary>
    public Task SetIpsetModeAsync(IpsetMode mode, CancellationToken ct = default) =>
        mode == IpsetMode.Any ? StartAllAddressesAsync(ct) : CallAsync("ipset_mode", new { mode = mode == IpsetMode.List ? "list" : "off" }, null, ct);

    public Task StartAllAddressesAsync(CancellationToken ct = default) => CallAsync("all_addresses_start", null, null, ct);

    public async Task<bool> StopAllAddressesAsync(CancellationToken ct = default) =>
        (await CallAsync("all_addresses_stop", null, null, ct)).GetProperty("stopped").GetBoolean();

    /// <summary>Окно передаёт только Off и TcpUdp (как и раньше).</summary>
    public Task<GameModeChange> SetGameModeAsync(GameMode mode, IProgress<string>? log = null, CancellationToken ct = default) =>
        CallAsync<GameModeChange>("game_mode", new { mode = mode == GameMode.Off ? "off" : "all" }, MapLog(log), ct);

    public Task<IpsetUpdateSummary> UpdateIpsetFromManifestAsync(bool confirmed = false, IProgress<string>? log = null, CancellationToken ct = default) =>
        CallAsync<IpsetUpdateSummary>("ipset_update", new { from = "manifest", confirmed }, MapLog(log), ct);

    /// <summary>Содержимое выбранного пользователем файла (окно читает его само); помощник прогоняет валидатор.</summary>
    public Task<IpsetUpdateSummary> UpdateIpsetFromContentAsync(string content, bool confirmed = false, CancellationToken ct = default) =>
        CallAsync<IpsetUpdateSummary>("ipset_update", new { from = "content", content, confirmed }, null, ct);

    public Task<IpsetVerdictSummary> WriteUserIpsetAsync(IReadOnlyList<string> lines, bool exclude = false, bool confirmed = false, CancellationToken ct = default) =>
        CallAsync<IpsetVerdictSummary>("user_ipset_set", new { lines, exclude, confirmed }, null, ct);

    public Task<UserTargetsResult> SetUserTargetsAsync(IReadOnlyList<string> lines, CancellationToken ct = default) =>
        CallAsync<UserTargetsResult>("user_targets_set", new { lines }, null, ct);

    public Task<HostsCheckReport> HostsCheckAsync(IProgress<HostsCheckProgress>? progress = null, CancellationToken ct = default) =>
        CallAsync<HostsCheckReport>("hosts_check", null, Map(progress), ct);

    public Task<HostsPreview> HostsPreviewAsync(CancellationToken ct = default) => CallAsync<HostsPreview>("hosts_preview", null, null, ct);
    public Task<HostsWriteResult> HostsApplyAsync(CancellationToken ct = default) => CallAsync<HostsWriteResult>("hosts_apply", null, null, ct);
    public Task<HostsWriteResult> HostsRevertAsync(CancellationToken ct = default) => CallAsync<HostsWriteResult>("hosts_revert", null, null, ct);
    public Task<HostsRecheckResult> HostsRecheckAsync(CancellationToken ct = default) => CallAsync<HostsRecheckResult>("hosts_recheck", null, null, ct);
    public Task<StopForeignResult> StopForeignAsync(CancellationToken ct = default) => CallAsync<StopForeignResult>("stop_foreign", null, null, ct);
    public Task CoreRepairAsync(IProgress<string>? log = null, CancellationToken ct = default) => CallAsync("core_repair", null, MapLog(log), ct);
    public Task RemoveAllAsync(CancellationToken ct = default) => CallAsync("remove_all", null, null, ct);

    public async Task<int> ClearHistoryAsync(CancellationToken ct = default) =>
        (await CallAsync("clear_history", null, null, ct)).GetProperty("removed").GetInt32();

    public Task<ResultsSnapshot> ReadResultsAsync(CancellationToken ct = default) => CallAsync<ResultsSnapshot>("read_results", null, null, ct);
    public Task<HelperDetails> DetailsAsync(CancellationToken ct = default) => CallAsync<HelperDetails>("details", null, null, ct);
    public Task ChooseAsync(string strategyId, CancellationToken ct = default) => CallAsync("choose", new { strategy = strategyId }, null, ct);
}
