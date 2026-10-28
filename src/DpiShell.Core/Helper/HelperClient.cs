using DpiShell.Core.Localization;
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
    /// <summary>Язык окна, на котором помощник отвечает (API 1.13): берётся на каждый запрос, чтобы смена языка действовала сразу. null — как выбран в окне.</summary>
    public Func<string?>? Language { get; init; }
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
    /// <summary>Версия протокола помощника «мажор.минор» (из hello; помощник до 1.1 её не шлёт — «1.0»).</summary>
    public string HelperApi { get; private set; } = HelperCompat.LegacyApi;
    /// <summary>
    /// Принимает ли помощник от этого окна действия на весь компьютер (hosts, DNS, прокси машины; API 1.14, поле hello <c>machine</c>).
    /// false — окно не той же сборки, что помощник; null — помощник до 1.14, проверки нет.
    /// </summary>
    public bool? MachineAllowed { get; private set; }
    /// <summary>Язык ответов помощника на каждый запрос (API 1.13); null — язык окна.</summary>
    public Func<string?>? Language { get; init; }
    /// <summary>
    /// Совместимость с помощником: после подключения — по hello, после <see cref="StatusAsync"/> — ещё и по списку команд.
    /// Мажор другой — <see cref="CallAsync"/> отказывает сам (код <c>incompatible</c>), кроме status и cancel.
    /// </summary>
    public HelperCompat Compat { get; private set; } = HelperCompat.Evaluate(null, null, null);
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
                throw new HelperException(HelperErrors.Denied, L.T("core.helper.not_by_system"));

            var client = new HelperClient(pipe) { Language = o.Language };
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
            HelperApi = hello.TryGetProperty("api", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString()! : HelperCompat.LegacyApi;
            MachineAllowed = hello.TryGetProperty("machine", out var mc) && mc.ValueKind is JsonValueKind.True or JsonValueKind.False ? mc.GetBoolean() : null;
            Compat = HelperCompat.Evaluate(HelperApi, null, HelperVersion);
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
        if (!Compat.Allows(cmd)) throw new HelperException(HelperErrors.Incompatible, Compat.Message ?? HelperErrors.Text(HelperErrors.Incompatible));
        long id = Interlocked.Increment(ref _nextId);
        var pending = new Pending(progress) { Cmd = cmd };
        _pending[id] = pending;
        var request = new JsonObject { ["id"] = id, ["cmd"] = cmd };
        // Язык ответа — только помощнику 1.13+: прежний отвергает неизвестные поля запроса и отвечает по-русски.
        if (HelperCompat.AtLeast(HelperApi, HelperProtocol.LangApi) && (Language?.Invoke() ?? L.Current) is { Length: > 0 } lang) request["lang"] = lang;
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
                msg = Localized(msg, p.Cmd);
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
            var message = L.FromRu(err.TryGetProperty("message", out var m) ? m.GetString() ?? HelperErrors.Text(code) : HelperErrors.Text(code));
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

    /// <summary>
    /// Помощник отвечает по-русски (языка окна он не знает): известные фразы ядра в ответах, ходе и ошибках показываются на языке окна
    /// (<see cref="L.FromRu"/>). Состояние и результаты проверок (status, details, read_results) не трогаются: это данные, их тексты окно переводит само.
    /// </summary>
    static JsonElement Localized(JsonElement msg, string? cmd)
    {
        if (L.Current == L.Ru || cmd is "status" or "details" or "read_results") return msg;
        var node = JsonNode.Parse(msg.GetRawText());
        if (!LocalizeNode(node)) return msg;
        using var doc = JsonDocument.Parse(node!.ToJsonString(HelperProtocol.Json));
        return doc.RootElement.Clone();
    }

    static bool LocalizeNode(JsonNode? node)
    {
        bool changed = false;
        switch (node)
        {
            case JsonObject o:
                foreach (var (k, v) in o.ToList())
                {
                    if (Replace(v) is { } s) { o[k] = s; changed = true; }
                    else if (LocalizeNode(v)) changed = true;
                }
                break;
            case JsonArray a:
                for (int i = 0; i < a.Count; i++)
                {
                    if (Replace(a[i]) is { } s) { a[i] = s; changed = true; }
                    else if (LocalizeNode(a[i])) changed = true;
                }
                break;
        }
        return changed;

        static string? Replace(JsonNode? v)
        {
            if (v is not JsonValue jv || !jv.TryGetValue<string>(out var text) || text.Length is 0 or > 600 || !HasCyrillic(text)) return null;
            var t = L.FromRu(text);
            return t == text ? null : t;
        }
    }

    static bool HasCyrillic(string s) { foreach (var c in s) if (c is >= 'А' and <= 'я' or 'Ё' or 'ё') return true; return false; }

    sealed class Pending(IProgress<JsonElement>? progress)
    {
        public string? Cmd { get; init; }
        public IProgress<JsonElement>? Progress { get; } = progress;
        public TaskCompletionSource<JsonElement> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // ---------- типизированные команды для окна ----------

    static IProgress<JsonElement>? Map<T>(IProgress<T>? p) =>
        p is null ? null : new Relay(e => { if (e.Deserialize<T>(HelperProtocol.Json) is { } v) p.Report(v); });

    static IProgress<JsonElement>? MapLog(IProgress<string>? p) =>
        p is null ? null : new Relay(e => { if (e.TryGetProperty("message", out var m) && m.GetString() is { } s) p.Report(s); });

    sealed class Relay(Action<JsonElement> a) : IProgress<JsonElement> { public void Report(JsonElement v) => a(v); }

    public async Task<HelperStatus> StatusAsync(CancellationToken ct = default)
    {
        var s = await CallAsync<HelperStatus>("status", null, null, ct);
        Compat = HelperCompat.Evaluate(s.Api ?? HelperApi, s.Commands, s.HelperVersion);
        return s;
    }
    public Task EnableAsync(string strategyId, CancellationToken ct = default) => CallAsync("enable", new { strategy = strategyId }, null, ct);
    public Task DisableAsync(CancellationToken ct = default) => CallAsync("disable", null, null, ct);
    public Task SetRunModeAsync(bool service, CancellationToken ct = default) => CallAsync("set_run_mode", new { service }, null, ct);

    /// <summary>Подбор. Отмена — частичный отчёт с Cancelled = true (как у Shell.SelectAsync).</summary>
    /// <param name="ownOnly">Только свои сайты рядом с роутером (API 1.21; старому помощнику не слать — он отвергает лишние поля).</param>
    public Task<SelectionReport> SelectAsync(bool quick, IReadOnlyList<string>? groups = null, IProgress<SelectorProgress>? progress = null, CancellationToken ct = default, string? dns = null, bool ownOnly = false) =>
        CallAsync<SelectionReport>("select", new { quick, groups, dns, ownOnly = OwnOnly(ownOnly) }, Map(progress), ct);

    /// <summary>Поле <c>ownOnly</c> уходит, только когда оно true: иначе запрос тот же, что у окна до 1.21.</summary>
    static bool? OwnOnly(bool on) => on ? true : null;

    /// <summary>Сохранить свою стратегию у помощника (он проверит роли ещё раз и назначит id). source: user — своя, pasted — вставленная.</summary>
    public Task<Strategy> StrategyAddAsync(string title, IReadOnlyDictionary<string, string> roles, string source, CancellationToken ct = default) =>
        CallAsync<Strategy>("strategy_add", new { title, roles, source }, null, ct);

    public Task StrategyDeleteAsync(string strategyId, CancellationToken ct = default) =>
        CallAsync("strategy_delete", new { strategy = strategyId }, null, ct);

    /// <summary>Проверка сайтов для одной стратегии; итог — отчёт подбора из одной строки.</summary>
    public Task<SelectionReport> StrategyCheckAsync(string strategyId, IReadOnlyList<string>? groups = null, IProgress<SelectorProgress>? progress = null, CancellationToken ct = default, string? dns = null, bool ownOnly = false) =>
        CallAsync<SelectionReport>("strategy_check", new { strategy = strategyId, groups, dns, ownOnly = OwnOnly(ownOnly) }, Map(progress), ct);

    public Task<SearchReport> SearchAsync(string? baseId = null, IReadOnlyList<string>? groups = null, IProgress<SelectorProgress>? progress = null, CancellationToken ct = default, string? dns = null, bool ownOnly = false) =>
        CallAsync<SearchReport>("search", new { @base = baseId, groups, dns, ownOnly = OwnOnly(ownOnly) }, Map(progress), ct);

    /// <summary>Off или List; «Все адреса» — <see cref="StartAllAddressesAsync"/>.</summary>
    public Task SetIpsetModeAsync(IpsetMode mode, CancellationToken ct = default) =>
        mode == IpsetMode.Any ? StartAllAddressesAsync(ct) : CallAsync("ipset_mode", new { mode = mode == IpsetMode.List ? "list" : "off" }, null, ct);

    public Task StartAllAddressesAsync(CancellationToken ct = default) => CallAsync("all_addresses_start", null, null, ct);

    public async Task<bool> StopAllAddressesAsync(CancellationToken ct = default) =>
        (await CallAsync("all_addresses_stop", null, null, ct)).GetProperty("stopped").GetBoolean();

    /// <summary>«Общий игровой режим»: Off или TcpUdp.</summary>
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

    /// <summary>Сайты, которые на этом ПК открываются без оптимизации (API 1.10): весь список целиком.</summary>
    /// <summary>Свои сайты по группам (API 1.18); помощник старее — команды нет в <c>commands</c>, окно сохраняет плоским списком.</summary>
    public Task<UserGroupsResult> SetUserGroupsAsync(IReadOnlyList<SiteGroup> groups, CancellationToken ct = default) =>
        CallAsync<UserGroupsResult>("user_groups_set", new { groups }, null, ct);

    public Task<UserTargetsResult> SetUserExcludeAsync(IReadOnlyList<string> lines, CancellationToken ct = default) =>
        CallAsync<UserTargetsResult>("user_exclude_set", new { lines }, null, ct);

    public Task<HostsCheckReport> HostsCheckAsync(IProgress<HostsCheckProgress>? progress = null, CancellationToken ct = default) =>
        CallAsync<HostsCheckReport>("hosts_check", null, Map(progress), ct);

    public Task<HostsPreview> HostsPreviewAsync(CancellationToken ct = default) => CallAsync<HostsPreview>("hosts_preview", null, null, ct);
    public Task<HostsWriteResult> HostsApplyAsync(CancellationToken ct = default) => CallAsync<HostsWriteResult>("hosts_apply", null, null, ct);
    public Task<HostsWriteResult> HostsRevertAsync(CancellationToken ct = default) => CallAsync<HostsWriteResult>("hosts_revert", null, null, ct);
    public Task<HostsRecheckResult> HostsRecheckAsync(CancellationToken ct = default) => CallAsync<HostsRecheckResult>("hosts_recheck", null, null, ct);

    // ТЗ 12 (API 1.4): примерка строк, аудит чужих строк, свои строки, комментирование.
    public Task<HostsTryReport> HostsTryAsync(IReadOnlyList<HostsEntry> entries, IProgress<HostsCheckProgress>? progress = null, CancellationToken ct = default) =>
        CallAsync<HostsTryReport>("hosts_try", new { entries = entries.Select(e => $"{e.Ip} {e.Host}").ToList() }, Map(progress), ct);
    public Task<HostsTryReport> HostsAuditAsync(IProgress<HostsCheckProgress>? progress = null, CancellationToken ct = default) =>
        CallAsync<HostsTryReport>("hosts_audit", null, Map(progress), ct);
    /// <summary>Поставить ровно эти свои строки (пустой список — убрать все свои).</summary>
    public Task<HostsUserResult> HostsUserSetAsync(IReadOnlyList<HostsEntry> entries, CancellationToken ct = default) =>
        CallAsync<HostsUserResult>("hosts_user_set", new { entries = entries.Select(e => $"{e.Ip} {e.Host}").ToList() }, null, ct);
    public Task<HostsCommentResult> HostsCommentAsync(IReadOnlyList<string> ids, CancellationToken ct = default) =>
        CallAsync<HostsCommentResult>("hosts_comment", new { ids }, null, ct);
    public Task<HostsCommentResult> HostsUncommentAsync(CancellationToken ct = default) => CallAsync<HostsCommentResult>("hosts_uncomment", null, null, ct);
    public Task<HostsRestoreResult> HostsRestoreAsync(CancellationToken ct = default) => CallAsync<HostsRestoreResult>("hosts_restore", null, null, ct);
    public Task<StopForeignResult> StopForeignAsync(CancellationToken ct = default) => CallAsync<StopForeignResult>("stop_foreign", null, null, ct);
    public Task CoreRepairAsync(IProgress<string>? log = null, CancellationToken ct = default) => CallAsync("core_repair", null, MapLog(log), ct);

    /// <summary>
    /// «Указать архив ядра…» (API 1.19): байты файла — частями по <see cref="HelperCommands.MaxOfflineChunkBytes"/>, путь помощнику не передаётся.
    /// Помощник ставит ядро, только если sha256 архива совпал с core.json.
    /// </summary>
    public async Task CoreOfflineAsync(Stream zip, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var buf = new byte[HelperCommands.MaxOfflineChunkBytes];
        bool first = true;
        int n;
        while ((n = await zip.ReadAtLeastAsync(buf, buf.Length, throwOnEndOfStream: false, ct)) > 0)
        {
            await CallAsync("core_offline_part", new { data = Convert.ToBase64String(buf, 0, n), first }, null, ct);
            first = false;
        }
        if (first) throw new InvalidDataException(L.T("core.core.offline_missing"));
        await CallAsync("core_offline_install", null, MapLog(log), ct);
    }

    /// <summary>Обновить данные по манифесту: что и откуда качать, помощник решает сам (окно не передаёт ни адресов, ни путей).</summary>
    public Task<DataUpdateResult> UpdateDataAsync(bool confirmed = false, IProgress<string>? log = null, CancellationToken ct = default) =>
        CallAsync<DataUpdateResult>("data_update", new { confirmed }, MapLog(log), ct);
    public Task RemoveAllAsync(CancellationToken ct = default) => CallAsync("remove_all", null, null, ct);

    public async Task<int> ClearHistoryAsync(CancellationToken ct = default) =>
        (await CallAsync("clear_history", null, null, ct)).GetProperty("removed").GetInt32();

    public Task<ResultsSnapshot> ReadResultsAsync(CancellationToken ct = default) => CallAsync<ResultsSnapshot>("read_results", null, null, ct);
    public Task<HelperDetails> DetailsAsync(CancellationToken ct = default) => CallAsync<HelperDetails>("details", null, null, ct);
    /// <summary>Последние строки журнала помощника (API 1.13).</summary>
    public async Task<IReadOnlyList<string>> LogTailAsync(CancellationToken ct = default) =>
        (await CallAsync("log_tail", null, null, ct)).GetProperty("lines").Deserialize<List<string>>() ?? [];
    public Task ChooseAsync(string strategyId, CancellationToken ct = default) => CallAsync("choose", new { strategy = strategyId }, null, ct);
    /// <summary>Включить метки TCP (нужны стратегии роутера); true — включены.</summary>
    public async Task<bool> TcpTimestampsOnAsync(CancellationToken ct = default) =>
        (await CallAsync("tcp_timestamps_on", null, null, ct)).GetProperty("enabled").GetBoolean();

    public Task<DnsStatusInfo> DnsStatusAsync(CancellationToken ct = default) => CallAsync<DnsStatusInfo>("dns_status", null, null, ct);

    /// <summary>Вариант DNS этого компьютера; ips и doh — только для своего (preset = custom).</summary>
    public Task<DnsSetResult> DnsSetAsync(string preset, IReadOnlyList<string>? ips = null, string? doh = null, bool fallback = false, CancellationToken ct = default) =>
        CallAsync<DnsSetResult>("dns_set", new { preset, ips, doh, fallback }, null, ct);

    public Task<DnsSetResult> DnsRestoreAsync(CancellationToken ct = default) => CallAsync<DnsSetResult>("dns_restore", null, null, ct);

    // Откат всего (API 1.6): журнал изменений помощника.
    public Task<ChangesList> ChangesListAsync(CancellationToken ct = default) => CallAsync<ChangesList>("changes_list", null, null, ct);
    public Task<UndoReport> ChangesUndoAllAsync(CancellationToken ct = default) => CallAsync<UndoReport>("changes_undo_all", null, null, ct);
    public Task<UndoReport> ChangesUndoAsync(string id, CancellationToken ct = default) => CallAsync<UndoReport>("changes_undo", new { id }, null, ct);
    /// <summary>Загрузка настроек вместе с её пунктами (API 1.15; старый помощник команды не знает — кнопка скрыта).</summary>
    public Task<UndoReport> ChangesUndoGroupAsync(string id, CancellationToken ct = default) => CallAsync<UndoReport>("changes_undo_group", new { id }, null, ct);

    // Системный прокси (API 1.7). Значения проходят белый список помощника; пути реестра окно не передаёт.
    public Task<ProxyStatusInfo> ProxyStatusAsync(CancellationToken ct = default) => CallAsync<ProxyStatusInfo>("proxy_status", null, null, ct);

    /// <summary>Для пользователя окна с правами администратора. enabled = false — «Очистить» (значения не передаются).</summary>
    public Task<ProxyChangeResult> ProxyUserSetAsync(bool enabled, string? server = null, string? bypass = null, string? pac = null, CancellationToken ct = default) =>
        CallAsync<ProxyChangeResult>("proxy_user_set", new { enabled, server, bypass, pac }, null, ct);   // пустые поля в запрос не попадают

    /// <summary>Для всей машины; winhttp — ещё и прокси для служб. enabled = false — «Очистить».</summary>
    public Task<ProxyChangeResult> ProxyMachineSetAsync(bool enabled, string? server = null, string? bypass = null, string? pac = null, bool winhttp = false, CancellationToken ct = default) =>
        CallAsync<ProxyChangeResult>("proxy_machine_set", new { enabled, server, bypass, pac, winhttp }, null, ct);

    public Task<ProxyChangeResult> ProxyMachineRestoreAsync(CancellationToken ct = default) => CallAsync<ProxyChangeResult>("proxy_machine_restore", null, null, ct);

    // ---------- подключения программ (ТЗ 13, протокол 1.4) ----------

    /// <summary>Начать запись: процессы, запущенные после этого (pid не нужен), или выбранный процесс с дочерними. driver = false — без драйвера, только TCP.</summary>
    public Task<RecordingStatus> RecStartAsync(RecordingMode mode, int? pid = null, bool driver = true, CancellationToken ct = default) =>
        CallAsync<RecordingStatus>("rec_start", new { mode = mode == RecordingMode.Process ? "process" : "new", pid, driver }, null, ct);

    public Task<RecordingStatus> RecStatusAsync(CancellationToken ct = default) => CallAsync<RecordingStatus>("rec_status", null, null, ct);

    /// <summary>«Готово»: запись останавливается, помощник отдаёт записанное и забывает его.</summary>
    public Task<RecordingResult> RecStopAsync(CancellationToken ct = default) => CallAsync<RecordingResult>("rec_stop", null, null, ct);

    public Task<AppProfilesInfo> AppProfilesAsync(CancellationToken ct = default) => CallAsync<AppProfilesInfo>("app_profiles", null, null, ct);

    /// <summary>Создать профиль (id = null) или заменить. Параметры оптимизации — номер варианта из <see cref="AppProfilesInfo.Variants"/>.</summary>
    public Task<AppProfileInfo> AppProfileSetAsync(string? id, string title, string? tcp, string? udp, IReadOnlyList<string> addresses, int variant, ProfileSignature? signature = null, CancellationToken ct = default)
    {
        signature ??= ProfileSignature.None;
        // Сигнатура и свои параметры — с API 1.22; старый помощник лишних полей не принимает, молча потерять сигнатуру нельзя.
        if (!signature.IsEmpty && !HelperCompat.AtLeast(HelperApi, HelperProtocol.ProfilesApi))
            throw new HelperException("outdated", L.T("core.helper.needs_update"));
        static string? S(string v) => v.Length > 0 ? v : null;
        static int? N(int v) => v > 0 ? v : null;
        return CallAsync<AppProfileInfo>("app_profile_set", new
        {
            id, title, tcp = string.IsNullOrEmpty(tcp) ? null : tcp, udp = string.IsNullOrEmpty(udp) ? null : udp, addresses, variant,
            tcpLen = N(signature.TcpLength), tcpHex = S(signature.TcpHex), udpLen = N(signature.UdpLength), udpHex = S(signature.UdpHex),
            tcpDesync = signature.TcpDesync, udpDesync = signature.UdpDesync, tcpFilter = S(signature.TcpFilter), udpFilter = S(signature.UdpFilter),
        }, null, ct);
    }

    public async Task<bool> AppProfileRemoveAsync(string id, CancellationToken ct = default) =>
        (await CallAsync("app_profile_remove", new { id }, null, ct)).GetProperty("removed").GetBoolean();

    /// <summary>API 1.22: включить/выключить профиль и (или) выбрать стратегию («default», «own» или id). Старый помощник профили по отдельности не включает.</summary>
    public Task<AppProfileInfo> AppProfileOptionsAsync(string id, bool? enabled = null, string? strategy = null, CancellationToken ct = default)
    {
        if (!HelperCompat.AtLeast(HelperApi, HelperProtocol.ProfilesApi)) throw new HelperException("outdated", L.T("core.helper.needs_update"));
        return CallAsync<AppProfileInfo>("app_profile_options", new { id, enabled, strategy }, null, ct);
    }

    /// <summary>API 1.22: «Разные стратегии» у профилей.</summary>
    public Task<AppProfilesInfo> AppProfilesSeparateAsync(bool on, CancellationToken ct = default)
    {
        if (!HelperCompat.AtLeast(HelperApi, HelperProtocol.ProfilesApi)) throw new HelperException("outdated", L.T("core.helper.needs_update"));
        return CallAsync<AppProfilesInfo>("app_profiles_separate", new { on }, null, ct);
    }

    /// <summary>Продвинутый режим «сайты → стратегия» (API 1.8): настройки и чем идёт каждая строка.</summary>
    public Task<AdvancedInfo> AdvancedGetAsync(CancellationToken ct = default) => CallAsync<AdvancedInfo>("advanced_get", null, null, ct);

    /// <summary>Сохранить режим, остальные адреса и строки. Помощник проверяет сайты и стратегии и перезапускает оптимизацию; ошибка — исключение с причиной.</summary>
    /// <summary>Совместная работа с роутером (API 1.9): что этот ПК готов взять на себя (свои сайты, строки, имена приманок) и действует ли связка.</summary>
    public Task<JointInfo> JointGetAsync(CancellationToken ct = default) => CallAsync<JointInfo>("joint_get", null, null, ct);

    /// <summary>Включить или выключить связку и продлить её; <paramref name="owned"/> — сайты ПК, которые ведёт роутер. Оптимизация перезапускается, если изменилась командная строка.</summary>
    public Task<JointInfo> JointSetAsync(bool active, IReadOnlyList<string>? owned = null, CancellationToken ct = default)
    {
        IReadOnlyList<string> list = active ? owned ?? [] : [];
        return CallAsync<JointInfo>("joint_set", new { active, owned = list }, null, ct);
    }

    public Task<AdvancedInfo> AdvancedSetAsync(bool enabled, string others, IReadOnlyList<AdvancedRowInput> rows, CancellationToken ct = default) =>
        CallAsync<AdvancedInfo>("advanced_set", new { enabled, others, rows = rows.Select(r => new { domains = r.Domains, strategy = r.Strategy }) }, null, ct);

    // ---------- выгрузка и загрузка своих настроек (API 1.11) ----------

    /// <summary>Свои данные этого ПК из закрытой папки помощника (для <see cref="Export.SettingsExport"/>). Только чтение.</summary>
    /// <summary>Исключение Защитника для папки ядра (API 1.16): аргументов нет — путь помощник берёт свой.</summary>
    public Task<AvExclusionStatus> AvExclusionStatusAsync(CancellationToken ct = default) => CallAsync<AvExclusionStatus>("av_exclusion_status", null, null, ct);
    public Task<AvExclusionResult> AvExclusionAddAsync(CancellationToken ct = default) => CallAsync<AvExclusionResult>("av_exclusion_add", null, null, ct);
    /// <summary>Синхронизировать часы (API 1.20); аргументов нет.</summary>
    public Task<TimeSyncResult> TimeSyncAsync(CancellationToken ct = default) => CallAsync<TimeSyncResult>("time_sync", null, null, ct);
    public Task<AvExclusionResult> AvExclusionRemoveAsync(CancellationToken ct = default) => CallAsync<AvExclusionResult>("av_exclusion_remove", null, null, ct);

    public Task<Export.SettingsState> SettingsExportReadAsync(CancellationToken ct = default) =>
        CallAsync<Export.SettingsState>("settings_export_read", null, null, ct);

    /// <summary>Загрузить проверенный план. Помощник проверяет его заново, пишет в журнал изменений и возвращает итог по разделам.</summary>
    public Task<Export.ImportResult> SettingsImportAsync(Export.ImportPlan plan, IProgress<JsonElement>? progress = null, CancellationToken ct = default) =>
        CallAsync<Export.ImportResult>("settings_import", new { plan = plan.ToRequest(HelperCompat.AtLeast(HelperApi, HelperProtocol.GroupsApi)) }, progress, ct);
}
