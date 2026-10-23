using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.Tests;

/// <summary>Карточка «Почему не получилось» (docs/dev/architecture.md, «Общие ошибки»): разбор сети, времени, прав, смены сети, Telegram.</summary>
public class GuidedErrorsTests
{
    // ---------- ядро ----------

    sealed class Net(Dictionary<string, NetReply?> map) : INetFetcher
    {
        public List<string> Asked { get; } = [];
        public Task<NetReply?> GetAsync(string url, CancellationToken ct)
        {
            Asked.Add(url);
            return Task.FromResult(map.TryGetValue(url, out var r) ? r : null);
        }
    }

    static readonly string[] Control = ["https://vk.com/", "https://ya.ru/"];
    static readonly NetReply PortalOk = new(200, null, "Microsoft Connect Test", null);

    [Fact]
    public async Task Internet_is_fine_when_a_control_site_opens()
    {
        var net = new Net(new() { [NetDiagnosis.PortalProbeUrl] = PortalOk, ["https://ya.ru/"] = new(200, null, "x", null) });
        Assert.Equal(NetCause.Ok, (await NetDiagnosis.RunAsync(Control, net)).Cause);
    }

    [Fact]
    public async Task Nothing_opens_means_no_internet()
    {
        var v = await NetDiagnosis.RunAsync(Control, new Net([]));
        Assert.Equal(NetCause.NoInternet, v.Cause);
    }

    [Theory]
    [InlineData(302, "http://login.cafe.example/auth", "http://login.cafe.example/auth")]
    [InlineData(302, null, NetDiagnosis.PortalProbeUrl)]
    public async Task Redirect_instead_of_the_probe_page_is_a_portal(int status, string? location, string url)
    {
        var net = new Net(new() { [NetDiagnosis.PortalProbeUrl] = new(status, location, null, null) });
        var v = await NetDiagnosis.RunAsync(Control, net);
        Assert.Equal(NetCause.CaptivePortal, v.Cause);
        Assert.Equal(url, v.PortalUrl);
        Assert.DoesNotContain("https://ya.ru/", net.Asked);   // за входом в сеть обычные сайты не спрашиваем
    }

    [Fact]
    public async Task Foreign_page_body_is_a_portal()
    {
        var net = new Net(new() { [NetDiagnosis.PortalProbeUrl] = new(200, null, "<html>Войдите</html>", null) });
        Assert.Equal(NetCause.CaptivePortal, (await NetDiagnosis.RunAsync(Control, net)).Cause);
    }

    [Theory]
    [InlineData(3 * 60 + 12, true)]
    [InlineData(-300, true)]
    [InlineData(4, false)]
    public async Task Clock_off_by_more_than_five_minutes_is_reported_only_when_sites_fail(int minutes, bool skew)
    {
        var server = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        var net = new Net(new() { [NetDiagnosis.PortalProbeUrl] = new(404, null, null, server) });
        var v = await NetDiagnosis.RunAsync(Control, net, () => server.AddMinutes(minutes));
        Assert.Equal(skew ? NetCause.ClockSkew : NetCause.NoInternet, v.Cause);
        if (skew) Assert.Equal(TimeSpan.FromMinutes(minutes), v.Skew);
    }

    [Fact]
    public async Task Clock_is_not_reported_when_sites_open()
    {
        var server = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        var net = new Net(new() { [NetDiagnosis.PortalProbeUrl] = PortalOk with { Date = server }, ["https://vk.com/"] = new(200, null, "x", server) });
        Assert.Equal(NetCause.Ok, (await NetDiagnosis.RunAsync(Control, net, () => server.AddHours(5))).Cause);
    }

    [Theory]
    [InlineData(@"C:\Users\a\AppData\Local\Temp\Temp1_DpiShell-0.12.zip\DpiShell.exe", true)]
    [InlineData(@"c:\users\a\appdata\local\temp\temp2_x.ZIP\DpiShell.exe", true)]
    [InlineData(@"C:\Programs\DpiShell\DpiShell.exe", false)]
    [InlineData(@"C:\Temp\zip\DpiShell.exe", false)]
    public void Archive_path_is_recognised(string path, bool archive) => Assert.Equal(archive, ArchiveRun.LooksLikeArchive(path));

    [Fact]
    public void Missing_data_folder_is_detected()
    {
        using var t = new TempDir();
        Assert.True(ArchiveRun.DataMissing(t.Path));
        Directory.CreateDirectory(Path.Combine(t.Path, "data"));
        Assert.False(ArchiveRun.DataMissing(t.Path));
    }

    [Fact]
    public void Network_strategies_persist_and_keep_no_addresses()
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Path, "w", "networks.json");
        var a = new NetworkStrategies(file);
        a.Remember("0123456789abcdef", "alt9");
        a.Remember("fedcba9876543210", "alt10");
        var b = new NetworkStrategies(file);
        Assert.Equal("alt9", b.StrategyFor("0123456789abcdef"));
        Assert.Null(b.StrategyFor("unknown"));
        var text = File.ReadAllText(file);
        Assert.DoesNotMatch(@"\d+\.\d+\.\d+\.\d+", text);
        for (int i = 0; i < NetworkStrategies.MaxEntries + 5; i++) b.Remember("n" + i, "alt9");
        Assert.Equal(NetworkStrategies.MaxEntries, b.Count);
    }

    [Fact]
    public void Network_id_is_a_short_hash_of_gateway_and_mac()
    {
        var id = WindowsNetworkIdentity.Hash("192.168.1.1", "AABBCCDDEEFF", "Wireless80211");
        Assert.Equal(16, id.Length);
        Assert.DoesNotContain("192", id);
        Assert.NotEqual(id, WindowsNetworkIdentity.Hash("192.168.1.1", "AABBCCDDEE00", "Wireless80211"));
    }

    [Theory]
    [InlineData(false, false, false, TelegramState.Unknown)]
    [InlineData(false, false, true, TelegramState.Blocked)]
    [InlineData(true, false, true, TelegramState.Open)]
    [InlineData(false, true, true, TelegramState.Open)]
    public async Task Telegram_is_blocked_only_when_ordinary_sites_connect(bool ip, bool web, bool control, TelegramState expected)
    {
        var tcp = new TelegramCheck.Tcp((host, port, _) => Task.FromResult(
            host == "149.154.167.51" ? ip : host == "web.telegram.org" ? web : control));
        Assert.Equal(expected, await TelegramCheck.RunAsync(tcp));
    }

    [Fact]
    public async Task Helper_time_sync_goes_through_the_protocol()
    {
        await using var f = new HelperFixture();
        await using var c = await f.ConnectAsync();
        Assert.True(c.Compat.Supports("time_sync"));
        Assert.True((await c.TimeSyncAsync()).Ok);
        f.Backend.TimeSyncValue = new(false, "служба времени остановлена");
        var r = await c.TimeSyncAsync();
        Assert.False(r.Ok);
        Assert.Equal(2, f.Backend.Calls.Count(x => x == "time_sync"));
    }

    [Fact]
    public void Real_helper_backend_runs_w32tm_resync_through_a_replaceable_system()
    {
        using var t = new TempDir();
        var shell = new Shell(new AppPaths(t.Sub("root"), Repo.Data));
        var ok = PcHelper.Over(shell);
        var sys = new FakeTime(0);
        var b = new ShellHelperBackend(shell) { Time = sys };
        Assert.True(b.TimeSync().Ok);
        sys.Code = 1; sys.Output = "Ошибка: служба не запущена.\nвторая строка";
        var r = b.TimeSync();
        Assert.False(r.Ok);
        Assert.Equal("Ошибка: служба не запущена.", r.Message);
        Assert.NotNull(ok);
    }

    sealed class FakeTime(int code) : ITimeSystem
    {
        public int Code { get; set; } = code;
        public string Output { get; set; } = "";
        public (int, string) Resync() => (Code, Output);
    }

    // ---------- окно ----------

    static MainViewModel Window(TempDir t, HelperLink link, Func<NetVerdict>? verdict = null)
    {
        var vm = TestWindow.Model(new Shell(new AppPaths(t.Sub("root"), Repo.Data)), link);
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsLoading))!.SetValue(vm, false);
        vm.GuidedEnabled = true;
        vm.AskHelper = _ => true;
        var v = verdict ?? (() => new NetVerdict(NetCause.Ok));
        vm.NetDiagnose = _ => Task.FromResult(v());
        vm.SystemFindings = () => [];
        vm.LinkOpener = Opened.Add;
        Opened.Clear();
        return vm;
    }

    static readonly List<string> Opened = [];

    [Fact]
    public async Task Declined_rights_show_a_card_with_retry_instead_of_an_error()
    {
        using var t = new TempDir();
        var setup = new NoSetup();
        var vm = Window(t, new HelperLink { Connector = _ => throw new HelperUnavailableException(HelperUnavailableReason.NotInstalled), Setup = setup });
        await vm.EnableAsync();
        Assert.Equal(GuidedKind.Uac, vm.Guided!.Kind);
        Assert.Equal(L.T("home.guided.uac"), vm.GuidedText);
        Assert.Equal(L.T("home.guided.retry"), vm.GuidedActionText);
        Assert.True(vm.GuidedHasAction);
        Assert.DoesNotContain(L.T("shell.helper.declined"), vm.Message);
        int before = setup.Calls.Count;
        await vm.GuidedActionAsync();   // «Повторить» повторяет то действие, на котором отказали
        Assert.Equal(before + 1, setup.Calls.Count);
        Assert.Equal(GuidedKind.Uac, vm.Guided!.Kind);
    }

    [Fact]
    public async Task Declined_rights_without_enable_or_select_keep_the_plain_error()
    {
        using var t = new TempDir();
        var vm = Window(t, TestWindow.NoHelper());
        Assert.Null(await vm.NeedHelperAsync());
        Assert.Null(vm.Guided);
        Assert.Equal(L.T("shell.helper.declined"), vm.Message);
    }

    [Fact]
    public async Task No_internet_stops_the_selection_before_the_helper()
    {
        using var t = new TempDir();
        await using var f = new HelperFixture();
        var vm = Window(t, await f.LinkAsync(), () => new NetVerdict(NetCause.NoInternet));
        await vm.StartSelectAsync();
        Assert.Equal(GuidedKind.NoInternet, vm.Guided!.Kind);
        Assert.Equal("Нет доступа в интернет (или сеть требует входа).", vm.GuidedText);
        Assert.DoesNotContain(f.Backend.Calls, c => c.StartsWith("select"));
        // Интернет появился — «Повторить» запускает подбор.
        vm.NetDiagnose = _ => Task.FromResult(new NetVerdict(NetCause.Ok));
        f.Backend.Release.SetResult();
        await vm.GuidedActionAsync();
        Assert.Contains(f.Backend.Calls, c => c.StartsWith("select"));
        Assert.Null(vm.Guided);
    }

    [Fact]
    public async Task Portal_card_opens_the_login_page()
    {
        using var t = new TempDir();
        await using var f = new HelperFixture();
        var vm = Window(t, await f.LinkAsync(), () => new NetVerdict(NetCause.CaptivePortal, PortalUrl: "http://login.cafe.example/auth"));
        await vm.StartSelectAsync();
        Assert.Equal(GuidedKind.Portal, vm.Guided!.Kind);
        Assert.Equal(L.T("home.guided.portal_open"), vm.GuidedActionText);
        await vm.GuidedActionAsync();
        Assert.Equal(["http://login.cafe.example/auth"], Opened);
        Assert.DoesNotContain(f.Backend.Calls, c => c.StartsWith("select"));
    }

    [Fact]
    public async Task Clock_card_says_how_much_and_syncs_through_the_helper()
    {
        using var t = new TempDir();
        await using var f = new HelperFixture();
        var vm = Window(t, await f.LinkAsync(), () => new NetVerdict(NetCause.ClockSkew, TimeSpan.FromMinutes(3 * 60 + 12)));
        await vm.StartSelectAsync();
        Assert.Equal("Время на компьютере отличается на 3 ч 12 мин — проверки сайтов не проходят.", vm.GuidedText);
        Assert.Equal("Синхронизировать время", vm.GuidedActionText);
        await vm.GuidedActionAsync();
        Assert.Contains("time_sync", f.Backend.Calls);
        Assert.Null(vm.Guided);
        Assert.Empty(Opened);

        // Синхронизация не вышла — открываются «Параметры → Время».
        vm.NetDiagnose = _ => Task.FromResult(new NetVerdict(NetCause.ClockSkew, TimeSpan.FromDays(2) + TimeSpan.FromHours(1)));
        await vm.StartSelectAsync();
        Assert.Contains("2 дн 1 ч", vm.GuidedText);
        f.Backend.TimeSyncValue = new(false, "нет службы");
        await vm.GuidedActionAsync();
        Assert.Equal(["ms-settings:dateandtime"], Opened);
        Assert.NotNull(vm.Guided);
    }

    [Fact]
    public async Task Enable_failure_with_a_known_finding_points_to_it_with_one_button()
    {
        using var t = new TempDir();
        await using var f = new HelperFixture();
        f.Backend.EnableThrows = new InvalidOperationException("сбой");
        f.Backend.StatusValue = FakeHelperBackend.DefaultStatus with { Enabled = false, Active = false };
        var vm = Window(t, await f.LinkAsync());
        vm.SystemFindings = () => [SystemFinding.Of(true, () => ("hosts", "в порядке")), SystemChecks.ProxyFinding((true, "proxy.example:8080"))];
        await vm.RefreshStatusAsync();
        await vm.EnableAsync();
        Assert.Equal(GuidedKind.Finding, vm.Guided!.Kind);
        Assert.Contains("proxy.example", vm.GuidedText);
        Assert.Equal(L.T("home.guided.open_proxy"), vm.GuidedActionText);
        Assert.False(vm.GuidedIsUnknown);
    }

    [Fact]
    public async Task Enable_failure_without_a_known_cause_offers_report_and_issue()
    {
        using var t = new TempDir();
        await using var f = new HelperFixture();
        f.Backend.EnableThrows = new InvalidOperationException("странный сбой");
        f.Backend.StatusValue = FakeHelperBackend.DefaultStatus with { Enabled = false, Active = false };
        var vm = Window(t, await f.LinkAsync());
        await vm.RefreshStatusAsync();
        await vm.EnableAsync();
        Assert.Equal(GuidedKind.Unknown, vm.Guided!.Kind);
        Assert.True(vm.GuidedIsUnknown);
        Assert.False(vm.GuidedHasAction);
        Assert.Contains("странный сбой", vm.GuidedText);
        var url = vm.IssueUrl();
        Assert.StartsWith(MainViewModel.RepoUrl + "/issues/new?title=", url);
        var body = Uri.UnescapeDataString(url[(url.IndexOf("&body=", StringComparison.Ordinal) + 6)..]);
        Assert.Contains("странный сбой", body);
        Assert.Contains(L.T("diag.report.v.app", new { v = vm.AppVersionText }), body);
        vm.ReportIssueCmd.Execute(null);
        Assert.Single(Opened, o => o.StartsWith(MainViewModel.RepoUrl));
    }

    [Fact]
    public async Task Network_cause_wins_over_findings()
    {
        using var t = new TempDir();
        await using var f = new HelperFixture();
        f.Backend.EnableThrows = new InvalidOperationException("сбой");
        f.Backend.StatusValue = FakeHelperBackend.DefaultStatus with { Enabled = false, Active = false };
        var vm = Window(t, await f.LinkAsync(), () => new NetVerdict(NetCause.NoInternet));
        vm.SystemFindings = () => [SystemChecks.ProxyFinding((true, "proxy.example:8080"))];
        await vm.RefreshStatusAsync();
        await vm.EnableAsync();
        Assert.Equal(GuidedKind.NoInternet, vm.Guided!.Kind);
    }

    [Fact]
    public async Task Diagnosis_is_off_by_default_so_other_tests_never_touch_the_network()
    {
        using var t = new TempDir();
        await using var f = new HelperFixture();
        f.Backend.EnableThrows = new InvalidOperationException("сбой");
        f.Backend.StatusValue = FakeHelperBackend.DefaultStatus with { Enabled = false, Active = false };
        var vm = TestWindow.Model(new Shell(new AppPaths(t.Sub("root"), Repo.Data)), await f.LinkAsync());
        await vm.RefreshStatusAsync();
        await vm.EnableAsync();
        Assert.Null(vm.Guided);
    }

    // ---------- сеть сменилась ----------

    sealed class Ident : INetworkIdentity
    {
        public string? Id { get; set; }
        public string? CurrentId() => Id;
    }

    static Func<IReadOnlyList<TargetGroup>, Task<IReadOnlyList<TargetVerdict>>> Probe(Func<bool> open) => groups =>
        Task.FromResult<IReadOnlyList<TargetVerdict>>(groups.SelectMany(g => g.Targets.Select(x => new TargetVerdict(g.Id, x, open(), 0.3, []))).ToList());

    [Fact]
    public async Task New_network_with_closed_sites_offers_selection_and_known_network_restores_its_strategy()
    {
        using var t = new TempDir();
        await using var f = new HelperFixture();
        f.Backend.TrackEnabled = true;
        f.Backend.StatusValue = FakeHelperBackend.DefaultStatus with { Enabled = true, Active = true, StrategyId = "alt9" };
        var vm = Window(t, await f.LinkAsync());
        var ident = new Ident { Id = "home" };
        vm.NetworkIdentity = ident;
        bool open = true;
        vm.ProbeGroups = Probe(() => open);
        await vm.RefreshStatusAsync();
        foreach (var g in vm.Groups) g.IsChecked = g.Id is "youtube" or "discord";
        await vm.NetworkChangedAsync();   // старт: сеть запомнена, ничего не предлагается
        Assert.Null(vm.Guided);

        // Сайты открылись на стратегии — запоминается за этой сетью.
        await vm.CheckNowAsync();
        Assert.Equal("alt9", vm.Networks.StrategyFor("home"));

        // Другая сеть, сайты не открываются — предложение подобрать.
        ident.Id = "cafe"; open = false;
        await vm.NetworkChangedAsync();
        Assert.Equal(GuidedKind.NetChanged, vm.Guided!.Kind);
        Assert.Equal("Сеть сменилась, сайты не открываются. Подобрать для этой сети?", vm.GuidedText);
        Assert.Equal(L.T("home.guided.select"), vm.GuidedActionText);
        Assert.Null(vm.Networks.StrategyFor("cafe"));

        // Вернулись домой — ставится запомненная стратегия.
        vm.Networks.Remember("home", "alt10");
        ident.Id = "home";
        f.Backend.StatusValue = f.Backend.StatusValue with { StrategyId = "alt9" };
        await vm.NetworkChangedAsync();
        Assert.Contains("enable alt10", f.Backend.Calls);
        Assert.Null(vm.Guided);
    }

    [Fact]
    public async Task Network_change_does_nothing_when_optimization_is_off_or_the_same_network()
    {
        using var t = new TempDir();
        await using var f = new HelperFixture();
        f.Backend.StatusValue = FakeHelperBackend.DefaultStatus with { Enabled = false, Active = false };
        var vm = Window(t, await f.LinkAsync());
        var ident = new Ident { Id = "a" };
        vm.NetworkIdentity = ident;
        vm.ProbeGroups = Probe(() => false);
        await vm.RefreshStatusAsync();
        await vm.NetworkChangedAsync();
        await vm.NetworkChangedAsync();   // та же сеть
        ident.Id = "b";
        await vm.NetworkChangedAsync();   // оптимизация выключена — предлагать нечего
        Assert.Null(vm.Guided);
        Assert.DoesNotContain(f.Backend.Calls, c => c.StartsWith("enable"));
    }

    // ---------- Telegram в «Диагностике» ----------

    [Theory]
    [InlineData(TelegramState.Blocked, false, "адреса заблокированы целиком")]
    [InlineData(TelegramState.Open, true, "соединение с серверами есть")]
    [InlineData(TelegramState.Unknown, true, "не проверено")]
    public async Task Diagnostics_has_a_telegram_row_that_does_not_touch_selection(TelegramState state, bool ok, string text)
    {
        using var t = new TempDir();
        await using var f = new HelperFixture();
        var vm = Window(t, await f.LinkAsync());
        vm.TelegramTcp = (host, _, _) => Task.FromResult(state == TelegramState.Open ? host == "web.telegram.org" : state == TelegramState.Blocked && host is "ya.ru" or "vk.com");
        await vm.RefreshDiagAsync();
        var row = Assert.Single(vm.Findings, r => r.Title == "Telegram");
        Assert.Equal(ok, row.Ok);
        Assert.Contains(text, row.Text);
        if (!ok) Assert.Contains("MTProto или SOCKS5", row.Text);
    }
}
