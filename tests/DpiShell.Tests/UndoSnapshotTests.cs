using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Export;
using DpiShell.Core.Helper;

namespace DpiShell.Tests;

/// <summary>
/// Правило владельца об откате (аудит, волна 2): каждая функция, меняющая машину, — включить → откатить → состояние совпадает со снимком,
/// снятым до неё. Машина — подменная: hosts во временной папке, DNS адаптеров, прокси (реестр и WinHTTP), служба и метки TCP — в памяти;
/// настоящее ничего не трогается. «Отменить всё», отмена загрузки настроек и удаление данных программы — без остатков своего и без удаления чужого.
/// </summary>
public sealed class UndoSnapshotTests : IDisposable
{
    readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    const string Live = "github.com@140.82.112.3";
    const string Live2 = "example.org@93.184.215.14";
    /// <summary>Чужие строки hosts: их не трогает ни одна функция и ни один откат.</summary>
    const string ForeignHosts = "127.0.0.1 localhost\r\n10.0.0.5 nas.home\r\n0.0.0.0 ads.example.net\r\n";

    sealed class Probe(params string[] ok) : IEndpointProbe
    {
        public Task<EndpointCheck> CheckAsync(string host, IPAddress ip, CancellationToken ct = default) =>
            Task.FromResult(ok.Contains($"{host}@{ip}") ? new EndpointCheck(EndpointOutcome.Ok, 0.1, "HTTP 200") : new EndpointCheck(EndpointOutcome.Timeout, 5, "нет ответа"));
    }

    sealed class FixedDns(string ip) : IHostsDns
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct = default) => Task.FromResult(new[] { IPAddress.Parse(ip) });
    }

    sealed class NoRdap : IRdapClient
    {
        public Task<RdapOwner?> LookupAsync(IPAddress ip, CancellationToken ct = default) => Task.FromResult<RdapOwner?>(null);
    }

    sealed class Tcp : ITcpTimestampsSystem
    {
        public bool Enabled;
        public bool? IsEnabled() => Enabled;
        public bool Set(bool e) { Enabled = e; return true; }
    }

    sealed class Ops : IServiceOps
    {
        public SvcState State { get; set; } = SvcState.NotInstalled;
        public string? ImagePath { get; set; }
        public void Remove() { State = SvcState.NotInstalled; ImagePath = null; }
    }

    /// <summary>Подменный ПК: настоящий помощник (<see cref="ShellHelperBackend"/>) над временной папкой и машиной в памяти.</summary>
    sealed record Pc(Shell Shell, ShellHelperBackend B, string HostsFile, FakeDnsSystem Dns, MemoryProxyRegistry Proxy, MemoryWinHttpProxy WinHttp, Ops Service, Tcp Tcp);

    Pc NewPc(string name)
    {
        var shell = new Shell(Make.Paths(_t, name));
        shell.Data.Seed();
        var hosts = _t.Sub(name + "-hosts");
        File.WriteAllText(hosts, ForeignHosts);
        shell.Hosts = new HostsManager(shell.Paths, hosts, new Probe(Live, Live2), new FixedDns("140.82.112.3"), new NoRdap(), () => []);
        var tcp = new Tcp();
        shell.Tcp = tcp;
        var dns = new FakeDnsSystem();
        // Чужой DNS на Wi-Fi до DpiShell — откат должен вернуть именно его.
        dns.Dns[FakeDnsSystem.Wifi.Id] = new AdapterDns(false, ["192.168.0.1"], true, [], new Dictionary<string, DohSetting>());
        var proxy = new MemoryProxyRegistry();
        var winHttp = new MemoryWinHttpProxy();
        var ops = new Ops();
        var b = new ShellHelperBackend(shell)
        {
            ProxyRegistry = proxy, WinHttp = winHttp, ServiceOps = ops, DnsSystem = dns,
            InspectService = () => new ServiceInfo(SvcState.NotInstalled, null, null, false), IsActive = () => false, FindForeign = () => [],
            RecorderSources = _ => [new FakeFlowSource()], Processes = new FakeProcesses(),
        };
        return new(shell, b, hosts, dns, proxy, winHttp, ops, tcp);
    }

    /// <summary>
    /// Снимок «машины» и данных программы: hosts (даты пометок — без дня), DNS каждого адаптера и зарегистрированные DoH, прокси (реестр, WinHTTP),
    /// служба, метки TCP, свои данные (сайты, исключения, подсети, стратегии, продвинутый режим, профили, режимы списков) и файлы «вернуть как было».
    /// </summary>
    static string Snap(Pc pc)
    {
        var s = pc.Shell;
        var o = new JsonObject
        {
            ["hosts"] = Regex.Replace(File.ReadAllText(pc.HostsFile), @"\d{4}-\d{2}-\d{2}", "D"),
            ["dns"] = new JsonArray([.. pc.Dns.List.Select(a => (JsonNode)JsonSerializer.Serialize(pc.Dns.Read(a)))]),
            ["doh"] = string.Join(",", pc.Dns.Registered.Keys.Order()),
            ["proxy"] = string.Join(";", pc.Proxy.Values.Select(kv => $"{kv.Key}={(kv.Value is byte[] bytes ? Convert.ToHexString(bytes) : kv.Value)}").Order(StringComparer.Ordinal)),
            ["winhttp"] = JsonSerializer.Serialize(pc.WinHttp.Value),
            ["service"] = $"{pc.Service.State} {pc.Service.ImagePath}",
            ["tcp"] = pc.Tcp.Enabled,
            ["sites"] = string.Join(",", s.State.UserTargets),
            ["exclude"] = string.Join(",", s.State.UserExclude),
            ["subnets"] = string.Join(",", s.IpLists.Read(IpLists.UserFile)),
            ["subnetsExclude"] = string.Join(",", s.IpLists.Read(IpLists.UserExcludeFile)),
            ["strategies"] = string.Join(",", s.AllStrategies.Select(x => x.Id).Order(StringComparer.Ordinal)),
            ["advanced"] = s.State.Advanced.ToJson().ToJsonString(),
            ["apps"] = string.Join(";", s.State.AppProfiles.Select(p => $"{p.Id}|{p.Title}|{p.TcpPorts}|{p.UdpPorts}|{string.Join(",", p.Addresses)}")),
            ["modes"] = $"{s.State.IpsetMode} {s.State.GameMode} {s.State.IpsetByGame} enabled={s.State.Enabled} ts={s.State.TimestampsEnabledByUs} fix={s.State.HostsFix}",
            ["dnsBackup"] = File.Exists(s.Paths.DnsBackup),
            ["proxyBackup"] = File.Exists(s.Paths.ProxyBackup),
        };
        return o.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    static async Task UndoOnly(Pc pc, string kind)
    {
        var e = Assert.Single(pc.Shell.Journal.Active(), x => x.Kind == kind);
        var r = await pc.B.ChangesUndoAsync(e.Id, CancellationToken.None);
        Assert.All(r.Items, i => Assert.Equal(UndoStatus.Restored, i.Status));
    }

    // ---------- каждая функция отдельно ----------

    [Fact]
    public async Task Hosts_own_lines_on_then_undo_gives_the_snapshot_back()
    {
        var pc = NewPc("pc");
        var before = Snap(pc);
        var r = await pc.B.HostsUserSetAsync([new("github.com", "140.82.112.3")], CancellationToken.None);
        Assert.True(r.Changed);
        Assert.NotEqual(before, Snap(pc));
        await UndoOnly(pc, ChangeKinds.HostsUser);
        Assert.Equal(before, Snap(pc));
        Assert.Equal(ForeignHosts, File.ReadAllText(pc.HostsFile));   // чужие строки — байт в байт
    }

    [Fact]
    public async Task Hosts_commented_line_on_then_undo_gives_the_snapshot_back()
    {
        var pc = NewPc("pc");
        var before = Snap(pc);
        var c = pc.Shell.Hosts.Comment([HostsText.LineId(3, "0.0.0.0 ads.example.net")]);
        Assert.True(c.Changed);
        Assert.NotEqual(before, Snap(pc));
        await UndoOnly(pc, ChangeKinds.HostsComment);
        Assert.Equal(before, Snap(pc));
    }

    [Fact]
    public async Task Dns_on_then_undo_gives_the_snapshot_back_without_leftover_backup()
    {
        var pc = NewPc("pc");
        var before = Snap(pc);
        await pc.B.DnsSetAsync("cloudflare", null, null, false, CancellationToken.None);
        Assert.NotEqual(before, Snap(pc));
        await UndoOnly(pc, ChangeKinds.Dns);
        Assert.Equal(before, Snap(pc));
    }

    [Fact]
    public async Task Machine_proxy_on_then_undo_gives_the_snapshot_back()
    {
        var pc = NewPc("pc");
        pc.Proxy.Values[(ProxyKey.MachineSettings, "", "ProxyEnable")] = 0;   // чужое значение до DpiShell
        var before = Snap(pc);
        pc.B.ProxyMachineSet(true, "127.0.0.1:7897", "<local>", null, winhttp: true);
        Assert.NotEqual(before, Snap(pc));
        await UndoOnly(pc, ChangeKinds.ProxyMachine);
        Assert.Equal(before, Snap(pc));
    }

    [Fact]
    public async Task Service_and_tcp_timestamps_on_then_undo_give_the_snapshot_back()
    {
        var pc = NewPc("pc");
        var before = Snap(pc);
        // Служба: установку делает sc — в тесте её роль играет подменная служба; запись журнала — та же, что пишет ServiceManager.
        pc.Shell.Journal.Begin(ChangeKinds.Service, "Установлена служба", new JsonObject { ["exists"] = false }, new JsonObject { ["exe"] = pc.Shell.Paths.Winws });
        pc.Service.State = SvcState.Running; pc.Service.ImagePath = $"\"{pc.Shell.Paths.Winws}\" --wf-tcp=443";
        pc.Shell.State.Enabled = true;
        pc.Shell.EnsureTcpTimestamps();
        Assert.True(pc.Tcp.Enabled);
        await UndoOnly(pc, ChangeKinds.TcpTimestamps);
        await UndoOnly(pc, ChangeKinds.Service);
        Assert.Equal(before, Snap(pc));
    }

    [Fact]
    public async Task Game_mode_on_then_off_gives_the_snapshot_back()
    {
        var pc = NewPc("pc");
        var before = Snap(pc);
        var on = await pc.Shell.SetGameModeAsync(GameMode.TcpUdp);
        Assert.True(on.IpsetSwitched);
        Assert.NotEqual(before, Snap(pc));
        await pc.Shell.SetGameModeAsync(GameMode.Off);
        Assert.Equal(before, Snap(pc));
    }

    // ---------- загрузка настроек: отменяется отдельно, вместе со своими пунктами ----------

    async Task<ParsedPackage> FriendPackage()
    {
        var a = NewPc("friend");
        a.B.SetUserTargets(["site-one.example", "site-two.example"]);
        a.B.SetUserExclude(["bank.example"]);
        a.B.WriteUserIpset(["198.51.100.7/32"], false, true);
        a.B.WriteUserIpset(["192.0.2.0/24"], true, true);
        var own = a.B.AddStrategy("Моя первая", new Dictionary<string, string> { ["general"] = "--dpi-desync=multisplit --dpi-desync-split-pos=1" }, "user");
        a.B.AdvancedSet(true, "lists", [new(["game.example"], own.Id), new(["x.example"], "auto")]);
        a.B.AppProfileSet(null, "Игра", "443", "27015-27030", ["203.0.113.0/24"], 0);
        await a.B.HostsUserSetAsync([new("github.com", "140.82.112.3")], CancellationToken.None);
        await a.B.DnsSetAsync("google", null, null, false, CancellationToken.None);
        var folder = _t.Sub("pkg-" + Guid.NewGuid().ToString("N")[..6]);
        SettingsExport.Write(folder, a.B.SettingsExportRead(), SettingsSections.All);
        var pkg = SettingsImport.Read(folder);
        Assert.Empty(pkg.Problems);
        return pkg;
    }

    /// <summary>ПК, где уже есть свои данные и открытые записи журнала: свои строки hosts, продвинутый режим, метки TCP.</summary>
    async Task<Pc> UsedPc()
    {
        var b = NewPc("pc");
        b.B.SetUserTargets(["mine.example"]);
        await b.B.HostsUserSetAsync([new("example.org", "93.184.215.14")], CancellationToken.None);
        b.B.AdvancedSet(true, "lists", [new(["pre.example"], "direct")]);
        b.Shell.EnsureTcpTimestamps();
        return b;
    }

    [Fact]
    public async Task Undo_import_returns_exactly_the_snapshot_and_leaves_earlier_changes_alone()
    {
        var pkg = await FriendPackage();
        var b = await UsedPc();
        var earlier = b.Shell.Journal.Active().Select(e => e.Id).ToHashSet();
        var before = Snap(b);

        var plan = SettingsImport.Plan(pkg, b.B.SettingsExportRead(), SettingsSections.All, ImportMode.Replace);
        var res = await b.B.SettingsImportAsync(plan.ToRequest(), CancellationToken.None);
        Assert.True(res.AllOk, string.Join("; ", res.Sections.Where(s => !s.Ok).Select(s => s.Id + ": " + s.Message)));
        Assert.NotEqual(before, Snap(b));

        // Пункты, сделанные внутри загрузки, помечены её номером; прежние записи — нет.
        var list = b.B.ChangesList().Items;
        var import = Assert.Single(list, i => i.Kind == ChangeKinds.SettingsImport);
        Assert.Equal(res.JournalId, import.Id);
        var members = list.Where(i => i.Group == import.Id).Select(i => i.Kind).Order().ToList();
        Assert.Equal([ChangeKinds.AppProfile, ChangeKinds.Dns], members);
        Assert.All(list.Where(i => earlier.Contains(i.Id)), i => Assert.Null(i.Group));
        // Отмена по пунктам: загрузка — целиком, её пункты — с ней.
        var policy = ChangeUndoPolicy.Decide(list);
        Assert.Equal(SingleUndo.Group, policy[import.Id]);
        Assert.All(list.Where(i => i.Group == import.Id), i => Assert.Equal(SingleUndo.InGroup, policy[i.Id]));

        var report = await b.B.ChangesUndoGroupAsync(import.Id, CancellationToken.None);
        Assert.All(report.Items, i => Assert.Equal(UndoStatus.Restored, i.Status));
        Assert.Equal(import.Id, report.Items[^1].Id);   // сама загрузка — последней
        Assert.Equal(before, Snap(b));
        // Прежние записи (свои строки hosts, продвинутый режим, метки TCP) остались и по-прежнему откатываются.
        Assert.Equal(earlier, b.Shell.Journal.Active().Select(e => e.Id).ToHashSet());
        Assert.True(b.Tcp.Enabled);
    }

    [Fact]
    public async Task Undo_all_after_an_import_gives_the_clean_machine_back()
    {
        var pkg = await FriendPackage();
        var b = NewPc("pc");
        var clean = Snap(b);
        b.B.SetUserTargets([]);   // данные программы — не машина; «до DpiShell» здесь пусто
        await b.B.HostsUserSetAsync([new("example.org", "93.184.215.14")], CancellationToken.None);
        b.B.AdvancedSet(true, "lists", [new(["pre.example"], "direct")]);
        b.Shell.EnsureTcpTimestamps();
        await b.B.DnsSetAsync("quad9", null, null, false, CancellationToken.None);
        b.B.ProxyMachineSet(true, "127.0.0.1:7897", null, null, winhttp: true);
        b.Shell.Hosts.Comment([HostsText.LineId(3, "0.0.0.0 ads.example.net")]);
        var plan = SettingsImport.Plan(pkg, b.B.SettingsExportRead(), SettingsSections.All, ImportMode.Add);
        Assert.True((await b.B.SettingsImportAsync(plan.ToRequest(), CancellationToken.None)).AllOk);

        var report = await b.B.ChangesUndoAllAsync(CancellationToken.None);
        Assert.DoesNotContain(report.Items, i => i.Status is UndoStatus.Failed or UndoStatus.ChangedByOthers);
        Assert.Empty(b.Shell.Journal.Active());
        // Машина — как до DpiShell; свои сайты загрузки откачены к пустому списку (как было).
        Assert.Equal(clean, Snap(b));
        Assert.Equal(ForeignHosts, File.ReadAllText(b.HostsFile));
    }

    [Fact]
    public async Task Changes_made_by_others_after_the_import_are_not_overwritten_by_undo_import()
    {
        var pkg = await FriendPackage();
        var b = await UsedPc();
        var plan = SettingsImport.Plan(pkg, b.B.SettingsExportRead(), [SettingsSections.Hosts, SettingsSections.Advanced, SettingsSections.Strategies], ImportMode.Replace);
        var res = await b.B.SettingsImportAsync(plan.ToRequest(), CancellationToken.None);
        Assert.True(res.AllOk, string.Join("; ", res.Sections.Select(s => s.Message)));
        // Человек сам поправил продвинутый режим после загрузки.
        b.B.AdvancedSet(true, "all", [new(["later.example"], "direct")]);
        var report = await b.B.ChangesUndoGroupAsync(res.JournalId!, CancellationToken.None);
        var item = Assert.Single(report.Items);
        Assert.Equal(UndoStatus.Restored, item.Status);   // hosts и стратегии вернулись
        Assert.Contains(SettingsSections.Title(SettingsSections.Advanced), item.Note);   // а продвинутый режим — нет, и это сказано
        Assert.Equal(["later.example"], b.Shell.State.Advanced.Rows.Single().Domains);
        Assert.Contains("93.184.215.14 example.org", b.B.SettingsExportRead().Hosts);
    }

    // ---------- журнал: группа видна только в своём потоке выполнения ----------

    [Fact]
    public async Task Group_tags_only_entries_of_its_own_async_flow()
    {
        using var t = new TempDir();
        var j = new ChangeJournal(Path.Combine(t.Path, "changes.jsonl"));
        var head = j.Begin(ChangeKinds.SettingsImport, "загрузка", null, null);
        var gate = new TaskCompletionSource();
        string other = "";
        var parallel = Task.Run(async () => { await gate.Task; other = j.Begin(ChangeKinds.Dns, "чужое действие", null, null); });
        async Task Import()
        {
            j.BeginGroup(head);
            await Task.Yield();
            j.Begin(ChangeKinds.Dns, "DNS загрузки", null, null);
            gate.SetResult();
            await parallel;
            j.EndGroup();
        }
        await Import();
        var after = j.Begin(ChangeKinds.Dns, "после", null, null);
        var e = j.Entries().ToDictionary(x => x.Title);
        Assert.Equal(head, e["DNS загрузки"].Group);
        Assert.Null(e["чужое действие"].Group);
        Assert.Null(e["после"].Group);
        Assert.Null(e["загрузка"].Group);
        Assert.NotEmpty(other + after);
    }

    [Fact]
    public void Policy_allows_single_undo_only_where_it_is_safe()
    {
        var at = new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
        ChangeInfo I(string id, string kind, int min, string? group = null, bool undoable = true) => new(id, at.AddMinutes(min), kind, id, undoable, group);
        var p = ChangeUndoPolicy.Decide([
            I("dns-old", ChangeKinds.Dns, 1), I("dns-new", ChangeKinds.Dns, 5),
            I("core", ChangeKinds.Core, 2), I("imp", ChangeKinds.SettingsImport, 3),
            I("imp-dns", ChangeKinds.Dns, 4, "imp"), I("app1", ChangeKinds.AppProfile, 1), I("app2", ChangeKinds.AppProfile, 2),
            I("x", "future_kind", 6, undoable: false), I("orphan", ChangeKinds.HostsUser, 7, "gone"),
        ]);
        Assert.Equal(SingleUndo.NewerFirst, p["dns-old"]);
        Assert.Equal(SingleUndo.Yes, p["dns-new"]);
        Assert.Equal(SingleUndo.OnlyAll, p["core"]);
        Assert.Equal(SingleUndo.Group, p["imp"]);
        Assert.Equal(SingleUndo.InGroup, p["imp-dns"]);
        Assert.Equal(SingleUndo.Yes, p["app1"]);   // у каждого профиля свой предмет
        Assert.Equal(SingleUndo.Yes, p["app2"]);
        Assert.Equal(SingleUndo.Unknown, p["x"]);
        Assert.Equal(SingleUndo.Yes, p["orphan"]);   // загрузки уже нет в списке — пункт сам по себе
    }

    // ---------- удаление данных программы: только своё ----------

    static void Junction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "/c", "mklink", "/J", link, target }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0, "junction не создалась: " + p.StandardError.ReadToEnd());
    }

    [Fact]
    public void Purge_removes_the_program_folder_but_not_what_links_point_to_or_neighbours()
    {
        var paths = Make.Paths(_t, "ProgramData-DpiShell");
        paths.EnsureDirectories();
        File.WriteAllText(paths.StateFile, "{}");
        File.WriteAllText(Path.Combine(paths.Logs, "helper.log"), "журнал");
        File.SetAttributes(Path.Combine(paths.Logs, "helper.log"), FileAttributes.ReadOnly);
        var outside = _t.Sub("someone-else");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "precious.txt"), "чужое");
        Junction(Path.Combine(paths.Results, "link"), outside);
        var neighbour = paths.Root + ".untrusted-20261022-120000";
        Directory.CreateDirectory(neighbour);
        File.WriteAllText(Path.Combine(neighbour, "x.txt"), "отложено");

        paths.Purge();
        Assert.False(Directory.Exists(paths.Root));
        Assert.Equal("чужое", File.ReadAllText(Path.Combine(outside, "precious.txt")));
        Assert.True(File.Exists(Path.Combine(neighbour, "x.txt")));
        paths.Purge();   // повтор безопасен

        // Корень сам оказался ссылкой — удаляется ссылка, а не то, на что она указывает.
        var linked = Make.Paths(_t, "linked-root");
        Junction(linked.Root, outside);
        linked.Purge();
        Assert.False(Directory.Exists(linked.Root));
        Assert.True(File.Exists(Path.Combine(outside, "precious.txt")));
    }

    // ---------- окно: отмена по пунктам и «Отменить загрузку» ----------

    static ChangeInfo Info(string id, string kind, int minute, string title, string? group = null) =>
        new(id, new DateTime(2026, 10, 22, 12, minute, 0, DateTimeKind.Utc), kind, title, true, group);

    Shell NewShell() => new(Make.Paths(_t, "w-" + Guid.NewGuid().ToString("N")[..6]));

    [Fact]
    public async Task Card_offers_undo_per_item_only_where_safe_and_undo_import_takes_its_items()
    {
        await using var f = new HelperFixture();
        f.Backend.ChangesValue.Add(Info("aaaaaaaaaaa1", ChangeKinds.Core, 1, "Установлено ядро"));
        f.Backend.ChangesValue.Add(Info("aaaaaaaaaaa2", ChangeKinds.Dns, 2, "DNS: Google"));
        f.Backend.ChangesValue.Add(Info("aaaaaaaaaaa3", ChangeKinds.SettingsImport, 3, "Загружены настройки из файлов: свои сайты, DNS"));
        f.Backend.ChangesValue.Add(Info("aaaaaaaaaaa4", ChangeKinds.Dns, 4, "DNS: Cloudflare", group: "aaaaaaaaaaa3"));
        var vm = TestWindow.Model(NewShell(), await f.LinkAsync());
        var asked = new List<string>();
        vm.Changes.Confirm = s => { asked.Add(s); return true; };
        await vm.Changes.RefreshAsync();
        var rows = vm.Changes.Items.ToDictionary(r => r.Id);

        Assert.Equal(L.T("settings.changes.row.only_all"), rows["aaaaaaaaaaa1"].Note);
        Assert.False(rows["aaaaaaaaaaa1"].HasAction);
        Assert.Equal(L.T("settings.changes.row.newer_first"), rows["aaaaaaaaaaa2"].Note);
        Assert.Equal(L.T("settings.changes.row.undo_import"), rows["aaaaaaaaaaa3"].Action);
        Assert.Equal(L.T("settings.changes.row.in_import"), rows["aaaaaaaaaaa4"].Note);
        Assert.All(vm.Changes.Items, r => Assert.True(r.HasAction ^ r.HasNote));

        Assert.True(await vm.Changes.UndoOneAsync(rows["aaaaaaaaaaa3"]));
        Assert.Contains("changes_undo_group aaaaaaaaaaa3", f.Backend.Calls);
        Assert.DoesNotContain("changes_undo_all", f.Backend.Calls);
        Assert.Contains(rows["aaaaaaaaaaa3"].Title, asked.Single());
        // Загрузка и её DNS ушли, остальное — на месте; отчёт — по двум пунктам.
        Assert.Equal(["aaaaaaaaaaa2", "aaaaaaaaaaa1"], vm.Changes.Items.Select(r => r.Id));
        Assert.Equal(2, vm.Changes.Report.Count);
        // Теперь поздний DNS ушёл — ранний можно отменить отдельно.
        var dns = vm.Changes.Items.Single(r => r.Id == "aaaaaaaaaaa2");
        Assert.Equal(L.T("settings.changes.row.undo"), dns.Action);
        Assert.True(await vm.Changes.UndoOneAsync(dns));
        Assert.Contains("changes_undo aaaaaaaaaaa2", f.Backend.Calls);
    }

    [Fact]
    public async Task With_an_older_helper_undo_import_is_hidden_and_says_why()
    {
        await using var f = new HelperFixture();
        f.Backend.ChangesValue.Add(Info("bbbbbbbbbbb1", ChangeKinds.SettingsImport, 3, "Загружены настройки из файлов: свои сайты"));
        f.Backend.ChangesValue.Add(Info("bbbbbbbbbbb2", ChangeKinds.Dns, 4, "DNS: Google"));
        var link = await f.LinkAsync();
        // Помощник 1.14: команды changes_undo_group он не знает (список команд — из status).
        var old = HelperCompat.Evaluate("1.14", [.. HelperCommands.Names.Where(n => n != "changes_undo_group")], "0.10.0", windowVersion: "0.10.0");
        typeof(HelperClient).GetProperty(nameof(HelperClient.Compat))!.SetValue(link.Reader, old);
        var vm = TestWindow.Model(NewShell(), link);
        await vm.Changes.RefreshAsync();
        var import = vm.Changes.Items.Single(r => r.Kind == ChangeKinds.SettingsImport);
        Assert.False(import.HasAction);
        Assert.Equal(L.T("settings.changes.row.import_old_helper"), import.Note);
        Assert.False(vm.Changes.CanUndoImport);
        Assert.False(await vm.Changes.UndoImportAsync("bbbbbbbbbbb1"));
        Assert.DoesNotContain(f.Backend.Calls, c => c.StartsWith("changes_undo_group"));
        // Остальное по одному — старой командой changes_undo (1.6).
        Assert.Equal(L.T("settings.changes.row.undo"), vm.Changes.Items.Single(r => r.Kind == ChangeKinds.Dns).Action);
    }

    [Fact]
    public async Task Transfer_window_undoes_only_the_import()
    {
        await using var f = new HelperFixture();
        f.Backend.ChangesValue.Add(Info("ccccccccccc1", ChangeKinds.Dns, 1, "DNS: Google"));
        f.Backend.ChangesValue.Add(Info("ccccccccccc2", ChangeKinds.SettingsImport, 2, "Загружены настройки из файлов: свои сайты"));
        var vm = TestWindow.Model(NewShell(), await f.LinkAsync());
        vm.Changes.Confirm = _ => true;
        var t = vm.CreateTransfer(import: true);
        Assert.True(t.CanUndoImport());
        Assert.True(await t.UndoImport("ccccccccccc2"));
        Assert.Contains("changes_undo_group ccccccccccc2", f.Backend.Calls);
        Assert.Equal(["ccccccccccc1"], f.Backend.ChangesValue.Select(c => c.Id));
    }

    // ---------- окно: «Удалить DpiShell с этого ПК…» ----------

    static Task<HelperLink> Link(HelperFixture f, IHelperSetup setup) => f.LinkAsync(setup);

    [Fact]
    public async Task Uninstall_from_pc_goes_step_by_step_and_lists_the_folders_left()
    {
        await using var f = new HelperFixture();
        f.Backend.ChangesValue.Add(Info("ddddddddddd1", ChangeKinds.Dns, 1, "DNS: Google"));
        var setup = new OkSetup();
        var vm = TestWindow.Model(NewShell(), await Link(f, setup));
        vm.ProgramFolder = () => @"D:\Apps\DpiShell";
        vm.WindowFolder = () => @"C:\Users\user\AppData\Local\DpiShell";
        var asked = new List<string>();
        vm.Confirm = s => { asked.Add(s); return true; };

        Assert.True(await vm.UninstallPcAsync());
        Assert.Equal(L.T("settings.uninstall.confirm", new { data = vm.Shell.Paths.Root }), asked[0]);
        Assert.Single(asked);   // один вопрос на всё
        // По порядку: отменить всё → служба → помощник с данными.
        var calls = f.Backend.Calls.ToList();
        var order = new[] { "changes_undo_all", "remove_all" }.Select(c => calls.IndexOf(c)).ToList();
        Assert.True(order[0] >= 0 && order[1] > order[0], string.Join(", ", f.Backend.Calls));
        Assert.Equal(["--uninstall-helper " + HelperLink.PurgeArg], setup.Calls);
        Assert.Equal(["ok", "ok", "ok"], vm.UninstallSteps.Select(s => s.Mark));
        Assert.Contains(@"D:\Apps\DpiShell", vm.UninstallLeftText);
        Assert.Contains(@"AppData\Local\DpiShell", vm.UninstallLeftText);
        Assert.Equal(L.T("settings.uninstall.done"), vm.Message);
        Assert.Equal(HelperState.NotInstalled, vm.Helper.State);
    }

    [Fact]
    public async Task Uninstall_stops_at_the_failed_step()
    {
        await using var f = new HelperFixture();
        var setup = new OkSetup { Answer = SetupResult.Declined };
        var vm = TestWindow.Model(NewShell(), await Link(f, setup));
        vm.Confirm = _ => true;
        Assert.False(await vm.UninstallPcAsync());
        Assert.Equal(["skip", "ok", "fail"], vm.UninstallSteps.Select(s => s.Mark));
        Assert.False(vm.HasUninstallLeft);
        Assert.Equal(L.T("settings.uninstall.stopped"), vm.Message);

        // «Нет» на вопрос — ничего не делается.
        await using var g = new HelperFixture();
        var vm2 = TestWindow.Model(NewShell(), await Link(g, new OkSetup()));
        vm2.Confirm = _ => false;
        Assert.False(await vm2.UninstallPcAsync());
        Assert.Empty(vm2.UninstallSteps);
        Assert.DoesNotContain("remove_all", g.Backend.Calls);
    }

    // ---------- ошибки: «Подробнее» и «Скопировать» ----------

    [Fact]
    public async Task Error_toast_has_details_with_action_time_versions_and_hides_private_data()
    {
        var vm = TestWindow.Model(NewShell());
        string? copied = null;
        vm.CopyText = s => copied = s;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        await vm.Changes.Guard("Отмена изменений…", () => throw new HelperException(HelperErrors.Conflict,
            $"не удалось: 192.168.1.20 занят, путь {profile}\\x.txt"));
        Assert.True(vm.ToastError);
        Assert.NotNull(vm.LastError);
        Assert.False(vm.ErrorDetailsOpen);
        vm.ErrorDetailsCmd.Execute(null);
        Assert.True(vm.ErrorDetailsOpen);
        var text = vm.ErrorDetailsText;
        Assert.Contains(L.T("shell.error.action", new { v = "Отмена изменений…" }), text);
        Assert.Contains("HelperException [conflict]", text);
        Assert.Contains(L.T("diag.report.v.app", new { v = vm.AppVersionText }), text);
        Assert.Contains(L.T("diag.report.v.helper", new { v = "" }).Trim(), text);
        Assert.Contains(L.T("shell.error.router", new { v = L.T("shell.error.none") }), text);
        Assert.Matches(@"\d{4}-\d\d-\d\d \d\d:\d\d:\d\d", text);
        Assert.DoesNotContain("192.168.1.20", text);
        Assert.DoesNotContain(profile, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(L.T("diag.report.hidden"), text);

        vm.CopyErrorCmd.Execute(null);
        Assert.Equal(text, copied);
        Assert.Equal(L.T("shell.error.copied"), vm.Message);
        vm.CloseToastCmd.Execute(null);
        Assert.False(vm.ErrorDetailsOpen);
    }
}
