using System.Diagnostics;
using System.Net;
using System.Reflection;
using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.Tests;

/// <summary>
/// Аудит уведомлений (C:\Dev\dpishell-private\audit-notifications.md): сценарий из аудита — нет ложного показа, есть «Скрыть», карточка уходит сама.
/// Сеть, реестр и окна не настоящие: подмены, окно не открывается.
/// </summary>
public class NotificationAuditTests
{
    static readonly BindingFlags NonPublic = BindingFlags.Instance | BindingFlags.NonPublic;

    sealed class Ident : INetworkIdentity
    {
        public string? Id { get; set; }
        public string? CurrentId() => Id;
    }

    static MainViewModel Window(TempDir t, HelperLink link, Func<NetVerdict>? verdict = null)
    {
        var vm = TestWindow.Model(new Shell(new AppPaths(t.Sub("root"), Repo.Data)), link);
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsLoading))!.SetValue(vm, false);
        vm.GuidedEnabled = true;
        vm.AskHelper = _ => true;
        var v = verdict ?? (() => new NetVerdict(NetCause.Ok));
        vm.NetDiagnose = _ => Task.FromResult(v());
        vm.SystemFindings = () => [];
        vm.LinkOpener = _ => { };
        return vm;
    }

    static Func<IReadOnlyList<TargetGroup>, Task<IReadOnlyList<TargetVerdict>>> Probe(Func<bool> open) => groups =>
        Task.FromResult<IReadOnlyList<TargetVerdict>>(groups.SelectMany(g => g.Targets.Select(x => new TargetVerdict(g.Id, x, open(), 0.3, []))).ToList());

    static void Pick(MainViewModel vm) { foreach (var g in vm.Groups) g.IsChecked = g.Id is "youtube" or "discord"; }

    static HelperFixture Running(string strategy = "alt9")
    {
        var f = new HelperFixture();
        f.Backend.TrackEnabled = true;
        f.Backend.StatusValue = FakeHelperBackend.DefaultStatus with { Enabled = true, Active = true, StrategyId = strategy };
        return f;
    }

    // ---------- №1: «Почему не получилось» — «Скрыть» и уходит сама ----------

    [Fact]
    public async Task Guided_card_can_be_hidden()
    {
        using var t = new TempDir();
        await using var f = new HelperFixture();
        var vm = Window(t, await f.LinkAsync(), () => new NetVerdict(NetCause.NoInternet));
        await vm.StartSelectAsync();
        Assert.True(vm.ShowGuided);
        vm.HideGuidedCmd.Execute(null);
        Assert.False(vm.ShowGuided);
        Assert.Null(vm.Guided);
    }

    [Fact]
    public async Task Hidden_network_changed_card_is_not_returned_by_the_next_network_change()
    {
        using var t = new TempDir();
        await using var f = Running();
        var vm = Window(t, await f.LinkAsync());
        var ident = new Ident { Id = "home" };
        vm.NetworkIdentity = ident;
        bool open = true;
        vm.ProbeGroups = Probe(() => open);
        await vm.RefreshStatusAsync();
        Pick(vm);
        await vm.NetworkChangedAsync();
        ident.Id = "cafe"; open = false;
        await vm.NetworkChangedAsync();
        Assert.Equal(GuidedKind.NetChanged, vm.Guided!.Kind);

        vm.HideGuidedCmd.Execute(null);
        ident.Id = "hotel";
        await vm.NetworkChangedAsync();   // сайты по-прежнему не открываются — но человек уже сказал «Скрыть»
        Assert.Null(vm.Guided);
    }

    [Fact]
    public async Task No_internet_card_goes_away_when_the_internet_is_back()
    {
        using var t = new TempDir();
        await using var f = new HelperFixture();
        var net = new NetVerdict(NetCause.NoInternet);
        var vm = Window(t, await f.LinkAsync(), () => net);
        await vm.StartSelectAsync();
        Assert.Equal(GuidedKind.NoInternet, vm.Guided!.Kind);
        await vm.RecheckGuidedAsync();
        Assert.NotNull(vm.Guided);   // интернета всё ещё нет — карточка остаётся

        net = new NetVerdict(NetCause.Ok);
        await vm.RecheckGuidedAsync();
        Assert.Null(vm.Guided);
    }

    [Fact]
    public async Task Finding_card_goes_away_when_the_finding_turns_green_and_clock_card_when_the_clock_is_fixed()
    {
        using var t = new TempDir();
        await using var f = new HelperFixture();
        f.Backend.EnableThrows = new InvalidOperationException("сбой");
        f.Backend.StatusValue = FakeHelperBackend.DefaultStatus with { Enabled = false, Active = false };
        var vm = Window(t, await f.LinkAsync());
        bool bad = true;
        vm.SystemFindings = () => [SystemFinding.Of(!bad, () => ("Adguard", "мешает")) with { Key = "adguard" }];
        await vm.RefreshStatusAsync();
        await vm.EnableAsync();
        Assert.Equal(GuidedKind.Finding, vm.Guided!.Kind);
        await vm.RecheckGuidedAsync();
        Assert.NotNull(vm.Guided);
        bad = false;
        await vm.RecheckGuidedAsync();
        Assert.Null(vm.Guided);
    }

    [Fact]
    public async Task Rights_card_waits_for_the_person_and_is_not_rechecked_away()
    {
        using var t = new TempDir();
        var vm = Window(t, new HelperLink { Connector = _ => throw new HelperUnavailableException(HelperUnavailableReason.NotInstalled), Setup = new NoSetup() });
        await vm.EnableAsync();
        Assert.Equal(GuidedKind.Uac, vm.Guided!.Kind);
        await vm.RecheckGuidedAsync();
        Assert.Equal(GuidedKind.Uac, vm.Guided!.Kind);
    }

    // ---------- №8: находка — только относящаяся к сорвавшемуся действию ----------

    [Fact]
    public async Task Failed_enable_does_not_blame_the_system_proxy_or_the_window_folder()
    {
        using var t = new TempDir();
        await using var f = new HelperFixture();
        f.Backend.EnableThrows = new InvalidOperationException("странный сбой");
        f.Backend.StatusValue = FakeHelperBackend.DefaultStatus with { Enabled = false, Active = false };
        var vm = Window(t, await f.LinkAsync());
        vm.SystemFindings = () =>
        [
            SystemChecks.ProxyFinding((true, "proxy.example:8080")),
            SystemFinding.Of(false, () => ("Папка программы", "кириллица")) with { Key = "folder" },
        ];
        await vm.RefreshStatusAsync();
        await vm.EnableAsync();
        Assert.Equal(GuidedKind.Unknown, vm.Guided!.Kind);
        Assert.DoesNotContain("proxy.example", vm.GuidedText);
    }

    [Fact]
    public void Findings_know_which_ones_are_context_and_unknown_service_state_is_not_a_failure()
    {
        Assert.True(SystemChecks.ProxyFinding((true, "x:1")).IsContext);
        Assert.False((SystemFinding.Of(false, () => ("Adguard", "x")) with { Key = "adguard" }).IsContext);
        Assert.False((SystemFinding.Of(false, () => ("BFE", "x")) with { Key = "bfe" }).IsContext);
    }

    // ---------- №9: вход в сеть и прокси ----------

    sealed class Net(Dictionary<string, NetReply?> map) : INetFetcher
    {
        public Task<NetReply?> GetAsync(string url, CancellationToken ct) => Task.FromResult(map.TryGetValue(url, out var r) ? r : null);
    }

    [Fact]
    public async Task Provider_stub_on_one_probe_address_is_not_a_captive_portal()
    {
        var net = new Net(new()
        {
            [NetDiagnosis.PortalProbeUrl] = new(302, "http://stub.provider.example/", null, null),
            [NetDiagnosis.PortalProbe2Url] = new(204, null, null, null),
            ["https://ya.ru/"] = new(200, null, "x", null),
        });
        Assert.Equal(NetCause.Ok, (await NetDiagnosis.RunAsync(["https://ya.ru/"], net)).Cause);
    }

    [Fact]
    public async Task Real_captive_portal_is_still_found_when_both_probe_addresses_are_replaced()
    {
        var net = new Net(new()
        {
            [NetDiagnosis.PortalProbeUrl] = new(302, "http://login.cafe.example/auth", null, null),
            [NetDiagnosis.PortalProbe2Url] = new(302, "http://login.cafe.example/auth", null, null),
        });
        Assert.Equal(NetCause.CaptivePortal, (await NetDiagnosis.RunAsync(["https://ya.ru/"], net)).Cause);
    }

    // ---------- №10: смена сети — ждать, пока сеть установится ----------

    [Fact]
    public async Task Network_change_waits_for_the_network_to_settle_and_shows_nothing_when_it_comes_back()
    {
        using var t = new TempDir();
        await using var f = Running();
        var vm = Window(t, await f.LinkAsync());
        var ident = new Ident { Id = "home" };
        vm.NetworkIdentity = ident;
        var clock = Stopwatch.StartNew();
        // Сеть поднимается не сразу: первые 150 мс сайты не открываются.
        vm.ProbeGroups = Probe(() => clock.ElapsedMilliseconds > 150);
        await vm.RefreshStatusAsync();
        Pick(vm);
        await vm.NetworkChangedAsync();
        vm.NetQuiet = TimeSpan.FromMilliseconds(300);
        vm.NetworkEvent();
        ident.Id = "cafe";
        clock.Restart();
        await vm.NetworkChangedAsync();
        Assert.True(clock.ElapsedMilliseconds >= 250, "проверка началась, не дождавшись окна «сеть нестабильна»");
        Assert.Null(vm.Guided);
    }

    [Fact]
    public async Task Network_change_without_internet_is_not_offered_as_a_new_strategy()
    {
        using var t = new TempDir();
        await using var f = Running();
        var vm = Window(t, await f.LinkAsync(), () => new NetVerdict(NetCause.NoInternet));
        var ident = new Ident { Id = "home" };
        vm.NetworkIdentity = ident;
        vm.ProbeGroups = Probe(() => false);
        await vm.RefreshStatusAsync();
        Pick(vm);
        await vm.NetworkChangedAsync();
        ident.Id = "cafe";
        await vm.NetworkChangedAsync();
        Assert.NotEqual(GuidedKind.NetChanged, vm.Guided?.Kind);   // это не «подобрать для этой сети»: интернета нет
    }

    // ---------- №3: итог проверки устаревает ----------

    [Fact]
    public async Task Old_check_result_does_not_paint_the_headline_red_and_says_so()
    {
        using var t = new TempDir();
        await using var f = Running();
        var vm = Window(t, await f.LinkAsync());
        vm.ProbeGroups = Probe(() => false);
        await vm.RefreshStatusAsync();
        Pick(vm);
        await vm.CheckNowAsync();
        Assert.Equal(L.T("home.status.partial"), vm.StatusTitle);

        vm.CheckOldAfter = TimeSpan.FromMilliseconds(1);
        await Task.Delay(30);
        await vm.RefreshStatusAsync();
        Assert.Equal(L.T("home.status.running"), vm.StatusTitle);
        Assert.Contains("давно", vm.TilesMeta);
    }

    [Fact]
    public async Task Check_result_is_dropped_when_the_network_changes()
    {
        using var t = new TempDir();
        await using var f = Running();
        var vm = Window(t, await f.LinkAsync());
        vm.ProbeGroups = Probe(() => false);
        await vm.RefreshStatusAsync();
        Pick(vm);
        await vm.CheckNowAsync();
        Assert.NotEmpty(vm.CheckGroups);
        vm.NetworkEvent();
        Assert.Empty(vm.CheckGroups);
        Assert.Equal(L.T("home.status.running"), vm.StatusTitle);
    }

    [Fact]
    public async Task Failed_check_inside_the_unsettled_window_is_not_shown_as_closed_sites()
    {
        using var t = new TempDir();
        await using var f = Running();
        var vm = Window(t, await f.LinkAsync(), () => new NetVerdict(NetCause.NoInternet));
        vm.ProbeGroups = Probe(() => false);
        await vm.RefreshStatusAsync();
        Pick(vm);
        vm.NetQuiet = TimeSpan.FromSeconds(30);
        vm.NetworkEvent();
        await vm.CheckNowAsync();
        Assert.Empty(vm.CheckGroups);
        Assert.Null(vm.Guided);
        Assert.Equal(L.T("home.status.running"), vm.StatusTitle);
    }

    // ---------- №4: «только через Windows» — сведение, не «не откроется» ----------

    static Func<IReadOnlyList<TargetGroup>, Task<IReadOnlyList<TargetVerdict>>> BrowserProbe(BrowserPath path) => groups =>
        Task.FromResult<IReadOnlyList<TargetVerdict>>(groups.SelectMany(g => g.Targets.Select(x =>
            new TargetVerdict(g.Id, x, true, 0.3, [], g.Id == "youtube" ? path : null))).ToList());

    [Fact]
    public async Task Windows_only_reason_is_an_info_tile_and_does_not_touch_the_headline()
    {
        using var t = new TempDir();
        await using var f = Running();
        var vm = Window(t, await f.LinkAsync());
        vm.ProbeGroups = BrowserProbe(new BrowserPath([], [IPAddress.Parse("1.2.3.4")], false, null, []));
        await vm.RefreshStatusAsync();
        Pick(vm);
        await vm.CheckNowAsync();
        Assert.Equal(L.T("home.status.running"), vm.StatusTitle);
        var tile = Assert.Single(vm.Tiles, x => x.State != Health.Ok);
        Assert.Equal(Health.None, tile.State);
        Assert.Equal(L.T("home.tile.windows_only"), tile.Status);
        Assert.DoesNotContain(vm.PcItems, i => i.State is Health.Warn or Health.Error && i.Label == L.T("web.diag.dns_label"));
        Assert.DoesNotContain("не откроется", tile.Status);
    }

    [Fact]
    public async Task Hosts_reason_still_warns_and_colors_the_headline()
    {
        using var t = new TempDir();
        await using var f = Running();
        var vm = Window(t, await f.LinkAsync());
        vm.ProbeGroups = BrowserProbe(new BrowserPath([IPAddress.Parse("9.9.9.9")], [IPAddress.Parse("1.2.3.4")], false, "9.9.9.9", []));
        await vm.RefreshStatusAsync();
        Pick(vm);
        await vm.CheckNowAsync();
        Assert.Equal(L.T("home.status.partial"), vm.StatusTitle);
        Assert.Contains(vm.Tiles, x => x.State == Health.Warn);
    }

    // ---------- №5: DNS «подменяет» ----------

    static async Task<MainViewModel> DnsWindow(TempDir t, Func<string, DnsComparison> compare, Func<Func<string, string?>>? hosts = null, TimeSpan? recheck = null)
    {
        var vm = TestWindow.Model(new Shell(new AppPaths(t.Sub("root"), Repo.Data)));
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsLoading))!.SetValue(vm, false);
        vm.DnsCompare = hosts => Task.FromResult(hosts.Select(compare).ToArray());
        vm.HostsLookup = hosts ?? (() => _ => null);
        vm.DnsRecheckDelay = recheck ?? TimeSpan.Zero;
        await vm.CheckDnsAsync();
        return vm;
    }

    static DnsComparison Cmp(string host, string[] system) =>
        new(host, [.. system.Select(IPAddress.Parse)], [IPAddress.Parse("142.251.152.4")], null);

    [Fact]
    public async Task Silent_system_dns_is_not_a_spoof()
    {
        using var t = new TempDir();
        var vm = await DnsWindow(t, h => Cmp(h, []));
        Assert.False(vm.DnsSpoofed);
    }

    [Fact]
    public async Task Stub_address_that_goes_away_on_the_recheck_is_not_a_spoof()
    {
        using var t = new TempDir();
        var seen = new HashSet<string>();
        var vm = await DnsWindow(t, h => Cmp(h, seen.Add(h) ? ["10.1.1.1"] : ["142.251.152.4"]), recheck: TimeSpan.FromMilliseconds(1));
        // Первый проход — заглушка у каждого хоста, повторный — настоящий адрес: подмена не подтвердилась.
        Assert.False(vm.DnsSpoofed);
    }

    [Fact]
    public async Task Stub_address_that_stays_is_a_spoof_unless_it_comes_from_the_hosts_file()
    {
        using var t = new TempDir();
        var spoof = await DnsWindow(t, h => Cmp(h, ["10.1.1.1"]), recheck: TimeSpan.FromMilliseconds(1));
        Assert.True(spoof.DnsSpoofed);

        using var t2 = new TempDir();
        var blocker = await DnsWindow(t2, h => Cmp(h, ["0.0.0.0"]), () => _ => "0.0.0.0");
        Assert.False(blocker.DnsSpoofed);   // заглушка из hosts (блокировщик рекламы) — не провайдер
    }

    // ---------- №7: VPN ----------

    static void SetVpn(MainViewModel vm, string names) => typeof(MainViewModel).GetProperty("VpnNames", NonPublic)!.SetValue(vm, names);

    [Fact]
    public void Vpn_notice_is_information_and_the_dismissal_survives_a_restart()
    {
        using var t = new TempDir();
        var shell = new Shell(new AppPaths(t.Sub("root"), Repo.Data));
        var settings = WindowSettings.Load(t.Sub("w.json"));
        var vm = TestWindow.Model(shell, settings: settings);
        SetVpn(vm, "Proton VPN");
        Assert.True(vm.HasVpn);
        var item = vm.PcItems.Single(i => i.Label == L.T("home.pc.vpn.label"));
        Assert.Equal(Health.None, item.State);   // не «!» на значке ПК
        vm.DismissVpnCmd.Execute(null);
        Assert.False(vm.HasVpn);

        var vm2 = TestWindow.Model(shell, settings: WindowSettings.Load(settings.File));
        SetVpn(vm2, "Proton VPN");
        Assert.False(vm2.HasVpn);   // после перезапуска не возвращается
        SetVpn(vm2, "Proton VPN, Outline");
        Assert.True(vm2.HasVpn);    // другой набор адаптеров — снова
    }

    // ---------- №12: списки сайтов ----------

    [Fact]
    public void Lists_update_is_information_with_a_persistent_hide()
    {
        using var t = new TempDir();
        var shell = new Shell(new AppPaths(t.Sub("root"), Repo.Data));
        var settings = WindowSettings.Load(t.Sub("w.json"));
        settings.UpdatesData = "2099-01-01";
        var vm = TestWindow.Model(shell, settings: settings);
        var item = vm.PcItems.Single(i => i.Label == L.T("home.pc.lists.label"));
        Assert.Equal(Health.None, item.State);
        Assert.True(item.HasHide);
        item.HideCommand!.Execute(item.HideKey);
        Assert.DoesNotContain(vm.PcItems, i => i.Label == L.T("home.pc.lists.label"));

        var vm2 = TestWindow.Model(shell, settings: WindowSettings.Load(settings.File));
        Assert.DoesNotContain(vm2.PcItems, i => i.Label == L.T("home.pc.lists.label"));
        // Вышла ещё более новая версия списков — снова.
        vm2.Settings.UpdatesData = "2099-02-02";
        vm2.Updates.Refresh();
        typeof(MainViewModel).GetMethod("RaiseIssues", NonPublic)!.Invoke(vm2, []);
        Assert.Contains(vm2.PcItems, i => i.Label == L.T("home.pc.lists.label"));
    }

    // ---------- №2: карточка «Первый запуск» ----------

    [Fact]
    public async Task First_run_card_can_be_hidden_and_stays_hidden_after_a_restart()
    {
        using var t = new TempDir();
        var shell = new Shell(new AppPaths(t.Sub("root"), Repo.Data));
        var settings = WindowSettings.Load(t.Sub("w.json"));
        var vm = TestWindow.Model(shell, settings: settings);
        vm.Defender = FakeDefender.Active();
        await AvExclusionFlowTests.Probe(vm);
        Assert.True(vm.ShowAvCard);
        Assert.True(vm.AvCanHide);
        vm.AvHideCmd.Execute(null);
        Assert.False(vm.ShowAvCard);

        var vm2 = TestWindow.Model(shell, settings: WindowSettings.Load(settings.File));
        vm2.Defender = FakeDefender.Active();
        await AvExclusionFlowTests.Probe(vm2);
        Assert.False(vm2.ShowAvCard);
    }

    // ---------- №11: роутер ----------

    [Fact]
    public void Hosts_warnings_do_not_include_the_read_only_mark()
    {
        using var t = new TempDir();
        var shell = new Shell(new AppPaths(t.Sub("root"), Repo.Data));
        var vm = TestWindow.Model(shell);
        var status = new HostsStatus("hosts", new HostsBlockInfo(false, false, null, []), [], [], null, BlockLost: false, ReadOnly: true, []);
        typeof(HostsViewModel).GetField("_status", NonPublic)!.SetValue(vm.Hosts, status);
        Assert.DoesNotContain(vm.Hosts.Warnings, w => w.Contains("только чтение"));
    }
    [Fact]
    public void Router_recommendation_can_be_hidden_and_the_choice_is_saved()
    {
        using var t = new TempDir();
        var shell = new Shell(new AppPaths(t.Sub("root"), Repo.Data));
        var settings = WindowSettings.Load(t.Sub("w.json"));
        var router = new RouterViewModel(shell, settings);
        int changed = 0;
        router.Changed += () => changed++;
        router.HideNoticeCmd.Execute("r_watchdog_off");
        router.HideNoticeCmd.Execute("r_update:1.2.3");
        Assert.Equal(2, changed);
        var saved = WindowSettings.Load(settings.File);
        Assert.True(saved.IsHidden("r_watchdog_off"));
        Assert.True(saved.IsHidden("r_update:1.2.3"));
        Assert.False(saved.IsHidden("r_update:1.2.4"));   // новая версия роутера — снова
    }

    [Fact]
    public void Foreign_hosts_notice_has_a_hide_for_this_set_of_hosts()
    {
        using var t = new TempDir();
        var shell = new Shell(new AppPaths(t.Sub("root"), Repo.Data));
        var settings = WindowSettings.Load(t.Sub("w.json"));
        var vm = TestWindow.Model(shell, settings: settings);
        vm.Hosts.Enabled = true;
        var status = new HostsStatus("hosts", new HostsBlockInfo(false, false, null, []), [new HostsConflict(3, "github.com", "1.2.3.4", "1.2.3.4 github.com")], [], null,
            BlockLost: false, ReadOnly: false, []);
        typeof(HostsViewModel).GetField("_status", NonPublic)!.SetValue(vm.Hosts, status);
        typeof(MainViewModel).GetMethod("RaiseIssues", NonPublic)!.Invoke(vm, []);
        var item = vm.PcItems.Single(i => i.Label == L.T("home.pc.foreign.label") && i.State == Health.Warn);
        Assert.True(item.HasHide);
        item.HideCommand!.Execute(item.HideKey);
        Assert.DoesNotContain(vm.PcItems, i => i.Label == L.T("home.pc.foreign.label") && i.State == Health.Warn);
        Assert.True(WindowSettings.Load(settings.File).IsHidden(item.HideKey!));
    }
}
