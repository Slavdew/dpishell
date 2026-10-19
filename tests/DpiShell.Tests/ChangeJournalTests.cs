using System.Net;
using System.Text.Json.Nodes;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.Tests;

/// <summary>«Откат всего»: журнал изменений, обработчики отката по видам изменений на фейках, отчёт, команды помощника.</summary>
public sealed class ChangeJournalTests
{
    // ---------- журнал ----------

    static ChangeJournal Journal(TempDir t, Func<DateTime>? now = null) =>
        new(Path.Combine(t.Sub("j"), "changes.jsonl")) { Now = now ?? (() => new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc)) };

    [Fact]
    public void Begin_is_on_disk_before_the_action_runs()
    {
        using var t = new TempDir();
        var j = Journal(t);
        var id = j.Begin("dns", "DNS", new JsonObject { ["a"] = 1 }, new JsonObject { ["a"] = 2 });
        // Действие ещё не началось, подтверждения нет — а запись уже в файле и читается заново другим экземпляром.
        var e = Assert.Single(new ChangeJournal(j.File).Entries());
        Assert.Equal(id, e.Id);
        Assert.False(e.Completed);
        Assert.Equal(ChangeState.Active, e.State);
        Assert.Equal(1, e.Before!["a"]!.GetValue<int>());
        j.Done(id, new JsonObject { ["a"] = 3 });
        e = Assert.Single(j.Entries());
        Assert.True(e.Completed);
        Assert.Equal(3, e.After!["a"]!.GetValue<int>());
    }

    [Fact]
    public void Truncated_last_line_is_skipped_and_next_append_does_not_glue_to_it()
    {
        using var t = new TempDir();
        var j = Journal(t);
        var a = j.Begin("service", "A", null, null);
        // Питание пропало посреди записи: хвост без перевода строки.
        File.AppendAllText(j.File, "{\"op\":\"do\",\"id\":\"deadbeef0000\",\"at\":\"2026-10-19T12:00:00.0000000Z\",\"kind\":\"dns\",\"ti");
        Assert.Equal([a], j.Entries().Select(e => e.Id));
        var b = j.Begin("dns", "B", null, null);
        Assert.Equal([a, b], j.Entries().Select(e => e.Id));   // новая запись не склеилась с обрывком
        Assert.Equal("B", j.Entries()[1].Title);
    }

    [Fact]
    public void Garbage_lines_and_unknown_ids_are_ignored()
    {
        using var t = new TempDir();
        var j = Journal(t);
        Directory.CreateDirectory(Path.GetDirectoryName(j.File)!);
        File.WriteAllText(j.File, "не json\n{\"op\":\"done\",\"id\":\"nope\"}\n[1,2]\n\n");
        Assert.Empty(j.Entries());
        var id = j.Begin("tcp_timestamps", "T", null, null);
        j.Abort(id);
        Assert.Equal(ChangeState.Aborted, j.Entries().Single().State);
        Assert.Empty(j.Active());
    }

    [Fact]
    public void Close_and_update_apply_to_active_entries_of_the_kind()
    {
        using var t = new TempDir();
        var j = Journal(t);
        var a = j.Begin("dns", "A", null, null);
        var b = j.Begin("service", "B", null, null);
        j.Update(a, new JsonObject { ["x"] = 1 });
        j.Close("dns", "сами вернули");
        var all = j.Entries();
        Assert.Equal(ChangeState.Closed, all.Single(e => e.Id == a).State);
        Assert.Equal(ChangeState.Active, all.Single(e => e.Id == b).State);
        Assert.Equal([b], j.Active().Select(e => e.Id));
    }

    // ---------- движок: порядок, отчёт, чужая правка ----------

    sealed class Recording(string kind, Func<ChangeEntry, UndoOutcome> f, List<string> order) : IChangeUndoer
    {
        public IReadOnlyCollection<string> Kinds { get; } = [kind];
        public Task<UndoOutcome> UndoAsync(ChangeEntry e, CancellationToken ct) { order.Add(e.Title); return Task.FromResult(f(e)); }
    }

    [Fact]
    public async Task UndoAll_goes_in_reverse_order_and_reports_each_item()
    {
        using var t = new TempDir();
        var j = Journal(t);
        var order = new List<string>();
        var engine = new ChangeUndoEngine(j);
        engine.Register(new Recording("ok", _ => UndoOutcome.Restored("вернул"), order));
        engine.Register(new Recording("same", _ => UndoOutcome.Already(), order));
        engine.Register(new Recording("foreign", _ => UndoOutcome.Changed("менял кто-то"), order));
        engine.Register(new Recording("bad", _ => UndoOutcome.Fail("занято"), order));
        j.Begin("ok", "первое", null, null);
        j.Begin("same", "второе", null, null);
        j.Begin("foreign", "третье", null, null);
        j.Begin("bad", "четвёртое", null, null);
        j.Begin("mystery", "пятое", null, null);   // вид, которого эта версия не знает

        var r = await engine.UndoAllAsync();
        Assert.Equal(["четвёртое", "третье", "второе", "первое"], order);   // пятое — без обработчика
        Assert.Equal([UndoStatus.Failed, UndoStatus.Failed, UndoStatus.ChangedByOthers, UndoStatus.AlreadyBack, UndoStatus.Restored], r.Items.Select(i => i.Status));
        Assert.False(r.AllDone);
        // Откачено, уже было и «изменено кем-то» закрываются; «не смог» остаётся — можно повторить.
        Assert.Equal(["пятое", "четвёртое"], j.Active().Select(e => e.Title).Order());
        Assert.Equal("skipped", j.Entries().Single(e => e.Title == "третье").Result);
        Assert.Equal(2, engine.List().Items.Count);
        Assert.False(engine.List().Items.Single(i => i.Title == "пятое").Undoable);
    }

    [Fact]
    public async Task UndoAll_survives_a_throwing_undoer()
    {
        using var t = new TempDir();
        var j = Journal(t);
        var engine = new ChangeUndoEngine(j);
        engine.Register(new Recording("boom", _ => throw new IOException("диск занят"), []));
        j.Begin("boom", "х", null, null);
        var r = await engine.UndoAllAsync();
        Assert.Equal(UndoStatus.Failed, r.Items.Single().Status);
        Assert.Contains("диск занят", r.Items.Single().Note);
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.UndoAsync("000000000000"));
    }

    // ---------- служба ----------

    sealed class FakeService(SvcState state, string? image) : IServiceOps
    {
        public SvcState State { get; set; } = state;
        public string? ImagePath { get; set; } = image;
        public int Removed { get; private set; }
        public void Remove() { Removed++; State = SvcState.NotInstalled; ImagePath = null; }
    }

    static ChangeEntry ServiceEntry(string exe) =>
        new("a", DateTime.UtcNow, ChangeKinds.Service, "служба", new JsonObject { ["exists"] = false }, new JsonObject { ["exe"] = exe }, ChangeState.Active, true);

    [Fact]
    public async Task Service_removed_only_when_it_runs_our_winws()
    {
        const string ours = @"C:\ProgramData\DpiShell\core\winws.exe";
        var mine = new FakeService(SvcState.Running, $"\"{ours}\" --wf-tcp=443");
        Assert.Equal(UndoStatus.Restored, (await new ServiceUndoer(mine).UndoAsync(ServiceEntry(ours), default)).Status);
        Assert.Equal(1, mine.Removed);

        var other = new FakeService(SvcState.Running, "\"C:\\Tools\\other.exe\" --x");   // под нашим именем теперь чужая программа
        Assert.Equal(UndoStatus.ChangedByOthers, (await new ServiceUndoer(other).UndoAsync(ServiceEntry(ours), default)).Status);
        Assert.Equal(0, other.Removed);

        var none = new FakeService(SvcState.NotInstalled, null);
        Assert.Equal(UndoStatus.AlreadyBack, (await new ServiceUndoer(none).UndoAsync(ServiceEntry(ours), default)).Status);
    }

    // ---------- временные метки TCP ----------

    sealed class FakeTcp(bool? enabled, bool setOk = true) : ITcpTimestampsSystem
    {
        public bool? Enabled { get; set; } = enabled;
        public Action<bool>? OnSet { get; set; }
        public bool? IsEnabled() => Enabled;
        public bool Set(bool e) { OnSet?.Invoke(e); if (!setOk) return false; Enabled = e; return true; }
    }

    [Fact]
    public async Task Shell_writes_tcp_entry_before_netsh_and_undo_turns_it_off()
    {
        using var t = new TempDir();
        var shell = new Shell(new AppPaths(t.Sub("root"), Repo.Data));
        var tcp = new FakeTcp(false);
        shell.Tcp = tcp;
        int seen = -1;
        tcp.OnSet = _ => seen = shell.Journal.Active(ChangeKinds.TcpTimestamps).Count;   // в момент netsh запись уже на диске
        shell.EnsureTcpTimestamps();
        Assert.Equal(1, seen);
        Assert.True(shell.State.TimestampsEnabledByUs);
        var e = shell.Journal.Active().Single();
        Assert.True(e.Completed);

        bool restored = false;
        var u = new TcpTimestampsUndoer(tcp, () => restored = true);
        Assert.Equal(UndoStatus.Restored, (await u.UndoAsync(e, default)).Status);
        Assert.False(tcp.Enabled);
        Assert.True(restored);
        // Второй раз — уже выключены (или их выключил сам человек): ничего не делаем.
        Assert.Equal(UndoStatus.AlreadyBack, (await u.UndoAsync(e, default)).Status);
        Assert.Equal(UndoStatus.Failed, (await new TcpTimestampsUndoer(new FakeTcp(null)).UndoAsync(e, default)).Status);
        Assert.Equal(UndoStatus.Failed, (await new TcpTimestampsUndoer(new FakeTcp(true, setOk: false)).UndoAsync(e, default)).Status);
    }

    [Fact]
    public void Tcp_already_enabled_by_someone_else_is_not_recorded()
    {
        using var t = new TempDir();
        var shell = new Shell(new AppPaths(t.Sub("root"), Repo.Data)) { Tcp = new FakeTcp(true) };
        shell.EnsureTcpTimestamps();
        Assert.Empty(shell.Journal.Entries());
        Assert.False(shell.State.TimestampsEnabledByUs);
    }

    [Fact]
    public void Tcp_failed_netsh_aborts_the_entry()
    {
        using var t = new TempDir();
        var shell = new Shell(new AppPaths(t.Sub("root"), Repo.Data)) { Tcp = new FakeTcp(false, setOk: false) };
        shell.EnsureTcpTimestamps();
        Assert.Equal(ChangeState.Aborted, shell.Journal.Entries().Single().State);
        Assert.Empty(shell.Journal.Active());
    }

    // ---------- DNS ----------

    static (DnsManager M, FakeDnsSystem Sys, ChangeJournal J) Dns(TempDir t)
    {
        var sys = new FakeDnsSystem();
        var j = Journal(t);
        return (new DnsManager(sys, Path.Combine(t.Sub("dns"), "dns-backup.json")) { Recorder = j, VerifyDelay = TimeSpan.Zero }, sys, j);
    }

    [Fact]
    public async Task Dns_set_is_journaled_and_undo_restores_only_untouched_adapters()
    {
        using var t = new TempDir();
        var (m, sys, j) = Dns(t);
        sys.Dns[FakeDnsSystem.Ethernet.Id] = new AdapterDns(false, ["192.168.1.1"], true, [], new Dictionary<string, DohSetting>());   // был статический DNS
        var r = await m.SetAsync("google", null, null, false, default);
        Assert.True(r.Ok);
        var e = j.Active().Single();
        Assert.Equal(ChangeKinds.Dns, e.Kind);
        Assert.True(e.Completed);
        Assert.Contains("Google", e.Title);

        // После нас человек сам сменил DNS Wi-Fi — его не трогаем; Ethernet стоит как мы поставили — вернуть.
        sys.Dns[FakeDnsSystem.Wifi.Id] = new AdapterDns(false, ["9.9.9.10"], true, [], new Dictionary<string, DohSetting>());
        var o = await new DnsUndoer(sys).UndoAsync(e, default);
        Assert.Equal(UndoStatus.Restored, o.Status);
        Assert.Contains("Wi-Fi", o.Note);
        Assert.Equal(["192.168.1.1"], sys.Read(FakeDnsSystem.Ethernet).Ipv4);
        Assert.Equal(["9.9.9.10"], sys.Read(FakeDnsSystem.Wifi).Ipv4);   // чужая правка осталась
    }

    [Fact]
    public async Task Dns_changed_by_user_after_us_is_skipped_and_untouched_is_already_back()
    {
        using var t = new TempDir();
        var (m, sys, j) = Dns(t);
        await m.SetAsync("cloudflare", null, null, false, default);
        var e = j.Active().Single();
        foreach (var a in new[] { FakeDnsSystem.Ethernet, FakeDnsSystem.Wifi })
            sys.Dns[a.Id] = new AdapterDns(false, ["77.88.8.8"], true, [], new Dictionary<string, DohSetting>());
        var before = sys.Dns.ToDictionary(kv => kv.Key, kv => kv.Value);
        var o = await new DnsUndoer(sys).UndoAsync(e, default);
        Assert.Equal(UndoStatus.ChangedByOthers, o.Status);
        Assert.All(before, kv => Assert.Same(kv.Value, sys.Dns[kv.Key]));   // ничего не записано

        // Человек вернул «автоматически» сам — «уже как раньше».
        foreach (var a in new[] { FakeDnsSystem.Ethernet, FakeDnsSystem.Wifi }) sys.Dns[a.Id] = AdapterDns.Automatic;
        Assert.Equal(UndoStatus.AlreadyBack, (await new DnsUndoer(sys).UndoAsync(e, default)).Status);
    }

    [Fact]
    public async Task Dns_verify_failure_aborts_the_entry_and_restore_closes_it()
    {
        using var t = new TempDir();
        var (m, sys, j) = Dns(t);
        sys.Resolves = () => false;
        var bad = await m.SetAsync("google", null, null, false, default);
        Assert.False(bad.Ok);   // DnsManager сам вернул прежнее
        Assert.Empty(j.Active());
        Assert.Equal(ChangeState.Aborted, j.Entries().Single().State);

        sys.Resolves = () => true;
        await m.SetAsync("quad9", null, null, false, default);
        Assert.Single(j.Active());
        await m.RestoreAsync(default);   // у функции есть свой «Вернуть как было» — журнал его вызывает, а не дублирует
        Assert.Empty(j.Active());
        Assert.Equal(ChangeState.Closed, j.Entries().Last().State);
    }

    [Fact]
    public async Task Dns_set_to_what_is_already_there_writes_nothing()
    {
        using var t = new TempDir();
        var (m, _, j) = Dns(t);
        await m.SetAsync("auto", null, null, false, default);   // адаптеры и так «как у провайдера»
        Assert.Empty(j.Entries());
    }

    // ---------- hosts ----------

    sealed class OkProbe : IEndpointProbe
    {
        public Task<EndpointCheck> CheckAsync(string host, IPAddress ip, CancellationToken ct = default) =>
            Task.FromResult(new EndpointCheck(EndpointOutcome.Ok, 0.1, "HTTP 200"));
    }

    [Fact]
    public async Task Hosts_user_lines_are_journaled_and_undo_removes_only_ours()
    {
        using var t = new TempDir();
        var hostsFile = Path.Combine(Directory.CreateDirectory(t.Sub("etc")).FullName, "hosts");
        File.WriteAllText(hostsFile, "127.0.0.1 localhost\r\n10.0.0.5 nas.home\r\n");
        var j = Journal(t);
        var m = new HostsManager(new AppPaths(t.Sub("root"), Repo.Data), hostsFile, new OkProbe()) { Recorder = j };

        var r = await m.SetUserAsync([new HostsEntry("github.com", "140.82.121.3")]);
        Assert.True(r.Changed);
        var e = j.Active().Single();
        Assert.Equal(ChangeKinds.HostsUser, e.Kind);
        Assert.Contains("140.82.121.3 github.com", File.ReadAllText(hostsFile));

        // Человек дописал свою строку уже после нас.
        File.AppendAllText(hostsFile, "10.0.0.6 printer.home\r\n");
        var o = await new HostsUndoer(() => m).UndoAsync(e, default);
        Assert.Equal(UndoStatus.Restored, o.Status);
        var left = File.ReadAllText(hostsFile);
        Assert.DoesNotContain("github.com", left);
        Assert.Contains("10.0.0.5 nas.home", left);
        Assert.Contains("10.0.0.6 printer.home", left);   // чужие правки, в том числе поздние, на месте
        Assert.Equal(UndoStatus.AlreadyBack, (await new HostsUndoer(() => m).UndoAsync(e, default)).Status);
    }

    [Fact]
    public void Hosts_restore_all_closes_every_hosts_entry()
    {
        using var t = new TempDir();
        var hostsFile = Path.Combine(Directory.CreateDirectory(t.Sub("etc")).FullName, "hosts");
        File.WriteAllText(hostsFile, "127.0.0.1 localhost\r\n1.2.3.4 old.example.com\r\n");
        var j = Journal(t);
        var m = new HostsManager(new AppPaths(t.Sub("root"), Repo.Data), hostsFile, new OkProbe()) { Recorder = j };
        j.Begin(ChangeKinds.HostsBlock, "блок", null, null);
        j.Begin(ChangeKinds.HostsComment, "комментарии", null, null);
        j.Begin(ChangeKinds.Dns, "dns", null, null);
        m.RestoreAll();
        Assert.Equal([ChangeKinds.Dns], j.Active().Select(e => e.Kind));
    }

    [Fact]
    public async Task Hosts_comment_undo_returns_the_line_byte_for_byte()
    {
        using var t = new TempDir();
        var hostsFile = Path.Combine(Directory.CreateDirectory(t.Sub("etc")).FullName, "hosts");
        const string original = "0.0.0.0 web.telegram.org";
        File.WriteAllText(hostsFile, "127.0.0.1 localhost\r\n" + original + "\r\n");
        var j = Journal(t);
        var m = new HostsManager(new AppPaths(t.Sub("root"), Repo.Data), hostsFile, new OkProbe()) { Recorder = j };
        var id = HostsText.LineId(2, original);
        var c = m.Comment([id]);
        Assert.True(c.Changed);
        var e = j.Active().Single();
        Assert.Equal(ChangeKinds.HostsComment, e.Kind);
        Assert.Equal(UndoStatus.Restored, (await new HostsUndoer(() => m).UndoAsync(e, default)).Status);
        Assert.Equal("127.0.0.1 localhost\r\n" + original + "\r\n", File.ReadAllText(hostsFile));
    }

    // ---------- ядро ----------

    [Fact]
    public async Task Core_files_removed_when_intact_kept_when_changed()
    {
        using var t = new TempDir();
        var paths = new AppPaths(t.Sub("root"), Repo.Data);
        Directory.CreateDirectory(paths.Core);
        var exe = Path.Combine(paths.Core, "winws.exe");
        File.WriteAllText(exe, "winws");
        var manifest = new CoreManifest("1", "u", "z", "d/", new() { ["winws.exe"] = CoreInstaller.Sha256(exe) });
        var undoer = new CoreUndoer(() => new CoreInstaller(paths, manifest), paths);
        var e = new ChangeEntry("a", DateTime.UtcNow, ChangeKinds.Core, "ядро", null, null, ChangeState.Active, true);

        File.WriteAllText(exe, "подменено антивирусом или человеком");
        Assert.Equal(UndoStatus.ChangedByOthers, (await undoer.UndoAsync(e, default)).Status);
        Assert.True(File.Exists(exe));

        File.WriteAllText(exe, "winws");
        Assert.Equal(UndoStatus.Restored, (await undoer.UndoAsync(e, default)).Status);
        Assert.False(Directory.Exists(paths.Core));
        Assert.Equal(UndoStatus.AlreadyBack, (await undoer.UndoAsync(e, default)).Status);
    }

    // ---------- профили программ (подключение «Игры и программы») ----------

    [Fact]
    public async Task App_profile_is_journaled_edits_update_it_and_user_changes_block_undo()
    {
        using var t = new TempDir();
        var shell = new Shell(new AppPaths(t.Sub("root"), Repo.Data));
        var p = shell.SetAppProfile(null, "Игра", "443", null, ["203.0.113.50"], 0);
        var e = shell.Journal.Active().Single();
        Assert.Equal(ChangeKinds.AppProfile, e.Kind);
        var undoer = new AppProfileUndoer(id => shell.State.AppProfiles.FirstOrDefault(x => x.Id == id), shell.RemoveAppProfile);

        // Правка профиля через программу — та же запись, «что поставили» обновляется.
        shell.SetAppProfile(p.Id, "Игра", "443,27015", null, ["203.0.113.50"], 0);
        Assert.Single(shell.Journal.Active());
        e = shell.Journal.Active().Single();
        Assert.Equal("443,27015", e.After!["tcp"]!.GetValue<string>());

        // Файл state.json поправили руками (порт) — профиль уже не тот, что записан.
        shell.State.AppProfiles[0].TcpPorts = "80";
        Assert.Equal(UndoStatus.ChangedByOthers, (await undoer.UndoAsync(e, default)).Status);
        shell.State.AppProfiles[0].TcpPorts = "443,27015";
        Assert.Equal(UndoStatus.Restored, (await undoer.UndoAsync(e, default)).Status);
        Assert.Empty(shell.State.AppProfiles);
        Assert.Equal(UndoStatus.AlreadyBack, (await undoer.UndoAsync(e, default)).Status);
    }

    [Fact]
    public void App_profile_removed_by_user_closes_its_entry()
    {
        using var t = new TempDir();
        var shell = new Shell(new AppPaths(t.Sub("root"), Repo.Data));
        var p = shell.SetAppProfile(null, "Игра", "443", null, ["203.0.113.50"], 0);
        shell.SetAppProfile(null, "Другая", "80", null, ["203.0.113.51"], 0);
        Assert.Equal(2, shell.Journal.Active().Count);
        shell.RemoveAppProfile(p.Id);
        Assert.Equal(["Другая"], shell.Journal.Active().Select(e => e.After!["title"]!.GetValue<string>()));
    }

    // ---------- помощник целиком: каналом ----------

    sealed class Ops : IServiceOps
    {
        public SvcState State { get; set; } = SvcState.NotInstalled;
        public string? ImagePath { get; set; }
        public void Remove() { State = SvcState.NotInstalled; ImagePath = null; }
    }

    [Fact]
    public async Task Helper_lists_and_undoes_through_the_channel_and_syncs_state()
    {
        using var t = new TempDir();
        var shell = new Shell(new AppPaths(t.Sub("root"), Repo.Data));
        var tcp = new FakeTcp(false);
        shell.Tcp = tcp;
        var ops = new Ops();
        var dns = new FakeDnsSystem();
        var backend = new ShellHelperBackend(shell)
        {
            InspectService = () => new ServiceInfo(SvcState.NotInstalled, null, null, false), IsActive = () => false, FindForeign = () => [],
            DnsSystem = dns, ServiceOps = ops,
        };
        // Что программа успела сделать.
        shell.EnsureTcpTimestamps();
        await backend.DnsSetAsync("google", null, null, false, default);
        ops.State = SvcState.Running; ops.ImagePath = $"\"{shell.Paths.Winws}\" --x";
        shell.Journal.Begin(ChangeKinds.Service, "Установлена служба", new JsonObject { ["exists"] = false }, new JsonObject { ["exe"] = shell.Paths.Winws });
        shell.State.Enabled = true;

        await using var f = new HelperFixture(backend: backend);
        await using var c = await f.ConnectAsync();
        var list = await c.ChangesListAsync();
        Assert.Equal(3, list.Items.Count);
        Assert.All(list.Items, i => Assert.True(i.Undoable));
        Assert.DoesNotContain(list.Items, i => i.Title.Contains("обход", StringComparison.OrdinalIgnoreCase));

        var report = await c.ChangesUndoAllAsync();
        // Обратный порядок записи: служба, DNS, метки.
        Assert.Equal(3, report.Items.Count);
        Assert.Equal(["Установлена служба"], report.Items.Take(1).Select(i => i.Title));
        Assert.All(report.Items, i => Assert.Equal(UndoStatus.Restored, i.Status));
        Assert.Equal(SvcState.NotInstalled, ops.State);
        Assert.Equal(false, tcp.Enabled);
        Assert.Equal(AdapterDns.Automatic.Ipv4Auto, dns.Read(FakeDnsSystem.Ethernet).Ipv4Auto);
        Assert.False(shell.State.Enabled);
        Assert.False(shell.State.TimestampsEnabledByUs);
        Assert.False(File.Exists(shell.Paths.DnsBackup));   // возвращать больше нечего
        Assert.Empty((await c.ChangesListAsync()).Items);
        Assert.Empty((await c.ChangesUndoAllAsync()).Items);

        await Assert.ThrowsAsync<HelperException>(() => c.ChangesUndoAsync("0123456789ab"));
    }

    [Fact]
    public async Task Helper_undo_one_by_id_and_bad_id_rejected_by_whitelist()
    {
        using var t = new TempDir();
        var shell = new Shell(new AppPaths(t.Sub("root"), Repo.Data)) { Tcp = new FakeTcp(false) };
        var backend = new ShellHelperBackend(shell) { DnsSystem = new FakeDnsSystem(), ServiceOps = new Ops() };
        shell.EnsureTcpTimestamps();
        await using var f = new HelperFixture(backend: backend);
        await using var c = await f.ConnectAsync();
        var id = (await c.ChangesListAsync()).Items.Single().Id;
        var r = await c.ChangesUndoAsync(id);
        Assert.Equal(UndoStatus.Restored, r.Items.Single().Status);
        using var raw = await f.RawAsync();
        Assert.Contains("invalid", await raw.AskAsync("""{"id":9,"cmd":"changes_undo","args":{"id":"../../etc/hosts"}}"""));
        Assert.Contains("invalid", await raw.AskAsync("""{"id":10,"cmd":"changes_undo_all","args":{"force":true}}"""));
    }
}
