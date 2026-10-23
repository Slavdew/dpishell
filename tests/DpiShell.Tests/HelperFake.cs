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

    /// <summary>enable/disable меняют то, что отвечает status (оптимизация на ПК включена/выключена); по умолчанию — нет.</summary>
    public bool TrackEnabled { get; set; }

    public void Enable(string strategyId)
    {
        Call("enable " + strategyId);
        if (EnableThrows is { } e) throw e;
        if (TrackEnabled) StatusValue = StatusValue with { Enabled = true, Active = true, StrategyId = strategyId, Service = SvcState.Running };
    }

    public void Disable()
    {
        Call("disable");
        if (TrackEnabled) StatusValue = StatusValue with { Enabled = false, Active = false, Service = SvcState.Stopped };
    }
    public void SetRunMode(bool service) => Call("set_run_mode " + service);
    /// <summary>Откуда подбор берёт адреса (API 1.12): последнее присланное окном значение.</summary>
    public string CheckDnsMode { get; private set; } = DpiShell.Core.CheckDns.Secure;
    public void SetCheckDns(string mode) => CheckDnsMode = mode;

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
        progress.Report(new(0, 2, "Проверка без оптимизации…"));
        bool cancelled = await HangAsync(() => progress.Report(new(1, 2, "Стратегия Alt 9…")), ct);
        // Как Selector: отмена — частичный отчёт, а не исключение.
        return ReportOverride is { } o && !cancelled ? o : Report(cancelled);
    }

    /// <summary>Что вернёт подбор вместо стандартного отчёта (тесты итога).</summary>
    public SelectionReport? ReportOverride { get; set; }

    public async Task<SearchReport> SearchAsync(string? baseId, IReadOnlyList<string>? groups, IProgress<SelectorProgress> progress, CancellationToken ct)
    {
        Call($"search {baseId}");
        await HangAsync(() => progress.Report(new(0, 0, "google: 1/2")), ct);
        return new(new Dictionary<string, IReadOnlyList<CandidateResult>>(), [], Report(false), null);
    }

    public Strategy AddStrategy(string title, IReadOnlyDictionary<string, string> roles, string source)
    {
        Call($"strategy_add {source} {title} {string.Join(";", roles.Select(r => r.Key + "=" + r.Value))}");
        ((HashSet<string>)StrategyIds).Add("own-1");
        var s = new Strategy("own-1", title, source, new Dictionary<string, string>(roles));
        DetailsValue = DetailsValue with { Found = [.. DetailsValue.Found, s] };
        StatusValue = StatusValue with { Strategies = [.. StatusValue.Strategies, new StrategyInfo(s.Id, s.Title, source)] };
        return s;
    }

    public void DeleteStrategy(string strategyId)
    {
        Call("strategy_delete " + strategyId);
        DetailsValue = DetailsValue with { Found = [.. DetailsValue.Found.Where(x => x.Id != strategyId)] };
        StatusValue = StatusValue with { Strategies = [.. StatusValue.Strategies.Where(x => x.Id != strategyId)] };
    }

    public Task<SelectionReport> CheckStrategyAsync(string strategyId, IReadOnlyList<string>? groups, IProgress<SelectorProgress> progress, CancellationToken ct)
    {
        Call($"strategy_check {strategyId} {string.Join(",", groups ?? [])}");
        progress.Report(new(0, 1, "Проверка без оптимизации…"));
        var r = Report(false);
        return Task.FromResult(r with { Scores = [.. r.Scores.Select(s => s with { Strategy = s.Strategy with { Id = strategyId, Title = "Проверяемая" } })] });
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
        StatusValue = StatusValue with { UserTargets = [.. lines] };   // как настоящий помощник: status отдаёт сохранённые
        return new(lines, []);
    }

    public UserTargetsResult SetUserExclude(IReadOnlyList<string> lines)
    {
        Call("user_exclude_set " + string.Join(",", lines));
        StatusValue = StatusValue with { UserExclude = [.. lines] };
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

    public Task<HostsTryReport> HostsTryAsync(IReadOnlyList<HostsEntry> entries, IProgress<HostsCheckProgress> progress, CancellationToken ct)
    {
        Call("hosts_try " + string.Join(",", entries));
        progress.Report(new(entries.Count, entries.Count));
        return Task.FromResult(new HostsTryReport([], 0, false, 0, DateTime.Now));
    }

    public Task<HostsTryReport> HostsAuditAsync(IProgress<HostsCheckProgress> progress, CancellationToken ct)
    {
        Call("hosts_audit");
        return Task.FromResult(new HostsTryReport([], 0, false, 0, DateTime.Now));
    }

    public Task<HostsUserResult> HostsUserSetAsync(IReadOnlyList<HostsEntry> entries, CancellationToken ct)
    {
        Call("hosts_user_set " + string.Join(",", entries));
        return Task.FromResult(new HostsUserResult(false, [], [], [], null));
    }

    public HostsCommentResult HostsComment(IReadOnlyList<string> ids) { Call("hosts_comment " + string.Join(",", ids)); return new(false, [], [], null); }
    public HostsCommentResult HostsUncomment() { Call("hosts_uncomment"); return new(false, [], [], null); }
    public HostsRestoreResult HostsRestore() { Call("hosts_restore"); return new(false, 0, 0, 0, null); }

    /// <summary>Что отвечает stop_foreign.</summary>
    public StopForeignResult StopForeignValue { get; set; } = new(["zapret от Flowseal"], []);
    public StopForeignResult StopForeign() { Call("stop_foreign"); return StopForeignValue; }

    public async Task CoreRepairAsync(IProgress<string> log, CancellationToken ct)
    {
        Call("core_repair");
        await HangAsync(() => log.Report("Скачиваю ядро…"), ct);
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>Что отвечает data_update.</summary>
    public DataUpdateResult DataUpdateValue { get; set; } = new(true, false, false, "Данные обновлены до 2026-11-01.", "2026-11-01");

    public Task<DataUpdateResult> UpdateDataAsync(bool confirmed, IProgress<string> log, CancellationToken ct)
    {
        Call("data_update " + confirmed);
        log.Report("Скачиваю lists/general.txt…");
        return Task.FromResult(DataUpdateValue);
    }

    /// <summary>Что отвечает changes_list; changes_undo_all отвечает отчётом по этим записям.</summary>
    public List<ChangeInfo> ChangesValue { get; } = [];
    public UndoStatus UndoAnswer { get; set; } = UndoStatus.Restored;

    public ChangesList ChangesList() { Call("changes_list"); return new([.. ChangesValue]); }

    public Task<UndoReport> ChangesUndoAllAsync(CancellationToken ct)
    {
        Call("changes_undo_all");
        var items = ChangesValue.AsEnumerable().Reverse().Select(c => new UndoItem(c.Id, c.Title, c.At, UndoAnswer, UndoAnswer == UndoStatus.Failed ? "занято" : "")).ToList();
        if (UndoAnswer != UndoStatus.Failed) ChangesValue.Clear();
        return Task.FromResult(new UndoReport(items));
    }

    public Task<UndoReport> ChangesUndoAsync(string id, CancellationToken ct)
    {
        Call("changes_undo " + id);
        var c = ChangesValue.FirstOrDefault(x => x.Id == id);
        if (c is null) return Task.FromResult(new UndoReport([]));
        ChangesValue.Remove(c);
        return Task.FromResult(new UndoReport([new UndoItem(c.Id, c.Title, c.At, UndoAnswer, "")]));
    }

    public Task<UndoReport> ChangesUndoGroupAsync(string id, CancellationToken ct)
    {
        Call("changes_undo_group " + id);
        var group = ChangesValue.Where(x => x.Id == id || x.Group == id).Reverse().ToList();
        foreach (var c in group) ChangesValue.Remove(c);
        return Task.FromResult(new UndoReport([.. group.Select(c => new UndoItem(c.Id, c.Title, c.At, UndoAnswer, ""))]));
    }

    public void RemoveAll() => Call("remove_all");

    /// <summary>Защитник помощника (подмена): по умолчанию выключен — карточка исключения на Главной не видна.</summary>
    public FakeDefender Defender { get; set; } = FakeDefender.Off();
    /// <summary>Журнал помощника для исключения (в памяти): «наше» исключение и его снятие.</summary>
    public MemoryRecorder AvJournal { get; } = new();
    public const string AvRoot = @"C:\ProgramData\DpiShell";
    AvExclusions Av => new(Defender, AvRoot, AvJournal);

    public AvExclusionStatus AvExclusionStatus() { Call("av_exclusion_status"); return Av.Status(); }
    public AvExclusionResult AvExclusionAdd() { Call("av_exclusion_add"); return Av.Add(); }
    public AvExclusionResult AvExclusionRemove()
    {
        Call("av_exclusion_remove");
        if (AvJournal.Active(ChangeKinds.AvExclusion).Count == 0) return new(false, AvOutcomes.NotOurs, "not ours", Av.Status());
        Defender.RemovePath(AvRoot);
        AvJournal.Close(ChangeKinds.AvExclusion, "removed");
        return new(true, AvOutcomes.Removed, "removed", Av.Status());
    }
    public int ClearHistory() { Call("clear_history"); return 3; }

    /// <summary>Что отвечает read_results; по умолчанию — одна стратегия с общей статистикой.</summary>
    public ResultsSnapshot? ResultsValue { get; set; }

    /// <summary>Что отвечает log_tail.</summary>
    public IReadOnlyList<string> LogTailValue { get; set; } = [];
    public IReadOnlyList<string> LogTail() { Call("log_tail"); return LogTailValue; }

    public ResultsSnapshot ReadResults()
    {
        Call("read_results");
        return ResultsValue ?? new(new Dictionary<string, StrategyStats.Stat> { ["alt9"] = new(0.9, 2, 1) }, []);
    }

    public void OnClientsGone()
    {
        Call("clients_gone");
        ClientsGone.TrySetResult();
    }

    // ---------- подключения программ ----------

    /// <summary>Что отдаст rec_stop. По умолчанию — игра с UDP-сервером, браузер и служба Windows (шум).</summary>
    public RecordingResult RecValue { get; set; } = SampleRecording();
    public bool RecActive { get; private set; }
    public IReadOnlyList<ProfileVariant> VariantsValue { get; set; } =
    [
        new(0, "как у «Alt 9» (сейчас выбрана)", "--dpi-desync=fake --dpi-desync-any-protocol=1 --dpi-desync-cutoff=n3", WinwsArgs.GameUdpDefault),
        new(1, "как у «Alt 10»", "--dpi-desync=multisplit --dpi-desync-any-protocol=1 --dpi-desync-cutoff=n3", WinwsArgs.GameUdpDefault),
        new(2, "как у «Alt 11»", "--dpi-desync=syndata --dpi-desync-any-protocol=1 --dpi-desync-cutoff=n4", WinwsArgs.GameUdpDefault),
    ];
    public List<AppProfileInfo> ProfilesValue { get; } = [];

    public static RecordingResult SampleRecording() => new("new", false, 42, true, false, false, [],
    [
        new("udp", "203.0.113.50", 27015, 4100, "game.exe", true, 30, 5.2, ""),
        new("udp", "203.0.113.51", 27016, 4100, "game.exe", true, 12, 5.4, ""),
        new("tcp", "198.51.100.7", 443, 4100, "game.exe", true, 4, 3.1, "works"),
        new("tcp", "198.51.100.9", 443, 4200, "chrome.exe", true, 9, 8.0, "works"),
        new("udp", "192.168.1.1", 53, 4100, "game.exe", true, 3, 1.0, ""),
        new("tcp", "198.51.100.200", 443, 700, "svchost.exe", false, 5, 0.5, "works"),
    ]);

    public RecordingStatus RecStart(RecordingMode mode, int pid, bool useDriver)
    {
        Call($"rec_start {mode} {pid} {useDriver}");
        if (RecActive) throw new InvalidOperationException("Запись уже идёт.");
        RecActive = true;
        return new(true, true, 0, 0, 0, useDriver, []);
    }

    public RecordingStatus RecStatus()
    {
        Call("rec_status");
        return new(RecActive, RecActive, 3, RecValue.Flows.Count, 2, true, []);
    }

    public RecordingResult RecStop()
    {
        Call("rec_stop");
        if (!RecActive) throw new InvalidOperationException("Запись не идёт.");
        RecActive = false;
        return RecValue;
    }

    public AppProfilesInfo AppProfilesInfo()
    {
        Call("app_profiles");
        return new([.. ProfilesValue], VariantsValue, new(RecActive, RecActive, 0, 0, 0, true, []));
    }

    public AppProfileInfo AppProfileSet(string? id, string title, string? tcpPorts, string? udpPorts, IReadOnlyList<string> addresses, int variant)
    {
        Call($"app_profile_set {id} {title} {tcpPorts} {udpPorts} {addresses.Count} {variant}");
        var info = new AppProfileInfo(id ?? "p0000000" + (ProfilesValue.Count + 1), title, tcpPorts ?? "", udpPorts ?? "", addresses, variant,
            VariantsValue[variant].Title, DateTime.Now);
        ProfilesValue.RemoveAll(p => p.Id == info.Id);
        ProfilesValue.Add(info);
        return info;
    }

    public bool AppProfileRemove(string id)
    {
        Call("app_profile_remove " + id);
        return ProfilesValue.RemoveAll(p => p.Id == id) > 0;
    }

    /// <summary>Что отвечает advanced_get; advanced_set меняет и пишет сюда принятое.</summary>
    public AdvancedInfo AdvancedValue { get; set; } = new(false, AdvancedSites.OthersLists, []);
    /// <summary>Последние строки advanced_set как пришли в бэкенд (после проверки запроса).</summary>
    public IReadOnlyList<AdvancedRowInput> LastAdvancedRows { get; private set; } = [];
    /// <summary>advanced_set отвечает отказом с этим текстом (как Shell при ошибке).</summary>
    public string? AdvancedFails { get; set; }

    public AdvancedInfo AdvancedGet()
    {
        Call("advanced_get");
        return AdvancedValue;
    }

    public AdvancedInfo AdvancedSet(bool enabled, string others, IReadOnlyList<AdvancedRowInput> rows)
    {
        Call($"advanced_set {enabled} {others} {rows.Count}");
        if (AdvancedFails is { } f) throw new InvalidOperationException(f);
        LastAdvancedRows = rows;
        AdvancedValue = new(enabled, others, [.. rows.Select(r => new AdvancedRowInfo(r.Domains, r.Strategy,
            r.Strategy == AdvancedSites.Direct ? null : r.Strategy == AdvancedSites.Auto ? StatusValue.StrategyId : r.Strategy))]);
        return AdvancedValue;
    }

    /// <summary>Что отвечает joint_get; joint_set меняет Active и Owned.</summary>
    public JointInfo JointValue { get; set; } = new("00000000-0000-4000-8000-0000000000a1", false, [], ["mysite.example", "youtube.com"], [],
        ["www.google.com", "4pda.to", "max.ru"], true, [], "alt9", true, ["mysite.example", "youtube.com"], 0);
    /// <summary>joint_set отвечает отказом с этим текстом (как Shell при ошибке).</summary>
    public string? JointFails { get; set; }
    /// <summary>joint_set отвечает «занято» (идёт подбор).</summary>
    public bool JointBusy { get; set; }

    public JointInfo JointGet()
    {
        Call("joint_get");
        return JointValue;
    }

    public JointInfo JointSet(bool active, IReadOnlyList<string> owned)
    {
        Call($"joint_set {active} {string.Join(",", owned)}");
        if (JointBusy) throw new HelperException(HelperErrors.Busy, "Идёт подбор");
        if (JointFails is { } f) throw new InvalidOperationException(f);
        JointValue = JointValue with { Active = active, Owned = active ? owned : [] };
        return JointValue;
    }

    /// <summary>Что отвечает dns_status (dns_set/dns_restore меняют Current).</summary>
    public DnsStatusInfo DnsValue { get; set; } = new("auto", false, false, true, false,
        [new DnsAdapterInfo("Ethernet", "ethernet", true, [], false)], ["Radmin VPN"]);
    /// <summary>dns_set отвечает «не заработало».</summary>
    public bool DnsSetFails { get; set; }

    public DnsStatusInfo DnsStatus() { Call("dns_status"); return DnsValue; }

    public Task<DnsSetResult> DnsSetAsync(string preset, IReadOnlyList<string>? ips, string? doh, bool fallback, CancellationToken ct)
    {
        Call($"dns_set {preset} {string.Join(",", ips ?? [])} {doh} {fallback}");
        if (DnsSetFails) return Task.FromResult(new DnsSetResult(false, "Защищённый DNS Quad9 не ответил — прежние настройки возвращены. Попробуйте другой вариант.", DnsValue));
        DnsValue = DnsValue with { Current = preset, Encrypted = preset != "auto", CanRestore = true };
        return Task.FromResult(new DnsSetResult(true, "Google, защищённый — Ethernet.", DnsValue));
    }

    // ---------- системный прокси: состояние в памяти ----------
    public ProxySettings ProxyUserValue { get; set; } = ProxySettings.Off;
    public ProxySettings ProxyMachineValue { get; set; } = ProxySettings.Off;
    public ProxyBackup? ProxyPreviousValue { get; set; }
    /// <summary>SID клиента, пришедший с последней командой «для пользователя».</summary>
    public string? LastClientSid { get; private set; }

    ProxyStatusInfo ProxyInfo() => new(new ProxyOverview(ProxyUserValue, false, ProxyMachineValue, false, ProxyMachineValue.IsOn ? 0 : null, false, WinHttpInfo.Direct), ProxyPreviousValue);

    public ProxyStatusInfo ProxyStatus(string? clientSid) { Call("proxy_status"); LastClientSid = clientSid; return ProxyInfo(); }

    public ProxyChangeResult ProxyUserSet(string? clientSid, bool enabled, string? server, string? bypass, string? pac)
    {
        Call($"proxy_user_set {enabled} {server} {bypass} {pac}");
        LastClientSid = clientSid;
        ProxyUserValue = enabled ? ProxyValues.Build(server, bypass, pac) : ProxyUserValue.Cleared();
        return new(true, enabled ? "Системный прокси включён для пользователя." : "Системный прокси выключен для пользователя.", ProxyInfo());
    }

    public ProxyChangeResult ProxyMachineSet(bool enabled, string? server, string? bypass, string? pac, bool winhttp)
    {
        Call($"proxy_machine_set {enabled} {server} {bypass} {pac} {winhttp}");
        if (!enabled && ProxyMachineValue.IsOn) ProxyPreviousValue = new(ProxyMachineValue, null);
        ProxyMachineValue = enabled ? ProxyValues.Build(server, bypass, pac) : ProxyMachineValue.Cleared();
        return new(true, enabled ? "Системный прокси включён для всей машины." : "Системный прокси выключен для всей машины.", ProxyInfo());
    }

    /// <summary>Что отвечает settings_export_read.</summary>
    public DpiShell.Core.Export.SettingsState SettingsStateValue { get; set; } = new();

    public DpiShell.Core.Export.SettingsState SettingsExportRead()
    {
        Call("settings_export_read");
        return SettingsStateValue;
    }

    /// <summary>Номер записи журнала, который отвечает settings_import (null — загрузка ничего не записала).</summary>
    public string? ImportJournalId { get; set; }

    public Task<DpiShell.Core.Export.ImportResult> SettingsImportAsync(string request, CancellationToken ct)
    {
        Call("settings_import " + request.Length);
        return Task.FromResult(new DpiShell.Core.Export.ImportResult([], ImportJournalId));
    }

    public ProxyChangeResult ProxyMachineRestore()
    {
        Call("proxy_machine_restore");
        if (ProxyPreviousValue is null) return new(false, "Прежнего прокси машины нет.", ProxyInfo());
        ProxyMachineValue = ProxyPreviousValue.Machine;
        ProxyPreviousValue = null;
        return new(true, "Системный прокси включён для всей машины.", ProxyInfo());
    }

    public Task<DnsSetResult> DnsRestoreAsync(CancellationToken ct)
    {
        Call("dns_restore");
        DnsValue = DnsValue with { Current = "auto", Encrypted = false, CanRestore = false };
        return Task.FromResult(new DnsSetResult(true, "DNS — как было: Ethernet.", DnsValue));
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
    public async Task<HelperLink> LinkAsync() => await LinkAsync(new NoSetup());

    /// <summary>То же со своей подменой установки/удаления (например, <see cref="OkSetup"/>).</summary>
    public async Task<HelperLink> LinkAsync(IHelperSetup setup)
    {
        var link = new HelperLink { Connector = ct => HelperClient.ConnectAsync(ClientOptions, ct), Setup = setup, WindowVersion = "0.0" };
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
/// <summary>Установка/удаление «прошли» (без UAC): аргументы запоминаются, ответ — <see cref="Answer"/>.</summary>
public sealed class OkSetup : IHelperSetup
{
    public List<string> Calls { get; } = [];
    public SetupResult Answer { get; set; } = SetupResult.Done;
    public Task<SetupOutcome> RunElevatedAsync(string arguments) { Calls.Add(arguments); return Task.FromResult(new SetupOutcome(Answer)); }
}

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
    public static ShellHelperBackend Over(Shell shell, FakeFlowSource? flows = null, IProcessCatalog? processes = null,
        IProxyRegistry? proxy = null, IWinHttpProxy? winHttp = null) => new(shell)
    {
        // Системный прокси: реестр и WinHTTP — в памяти, настоящие настройки машины тесты не трогают.
        ProxyRegistry = proxy ?? new MemoryProxyRegistry(),
        WinHttp = winHttp ?? new MemoryWinHttpProxy(),
        InspectService = () => new ServiceInfo(SvcState.NotInstalled, null, null, false),
        IsActive = () => false,
        FindForeign = () => [],
        DnsSystem = new FakeDnsSystem(),
        // Запись подключений: настоящий драйвер и список процессов машины в тестах не трогаем.
        RecorderSources = _ => [flows ?? new FakeFlowSource()],
        Processes = processes ?? new FakeProcesses(),
        // Защитник: настоящие исключения машины не трогаем (выключен — карточка исключения не видна).
        Defender = FakeDefender.Off(),
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

    /// <summary>Защитник окна — подмена (по умолчанию выключен: карточка исключения не видна); настоящий под тестами запрещён (<see cref="WmiDefender.Forbidden"/>).</summary>
    public static MainViewModel Model(Shell shell, HelperLink? link = null, WindowSettings? settings = null) =>
        new(shell, link ?? NoHelper(), settings ?? Settings(shell)) { Defender = FakeDefender.Off() };
}
