using System.Windows;
using System.Windows.Controls;
using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Helper;
using static DpiShell.Tests.WindowSmokeTests;

namespace DpiShell.Tests;

/// <summary>
/// «Кириллица в английском окне» с заполненными данными: меню стратегий на главной с историей проверок (автоимена найденных, смешанная,
/// «ломает обычные сайты», адреса с русскими причинами от помощника), мешающие программы от помощника по-русски, DNS ПК. Окно
/// открывается сразу на английском и, отдельно, на русском с переключением на лету. Снимки: populated-home-*.png (DPISHELL_SNAPSHOTS).
/// </summary>
[Collection(LangCollection.Name)]
public sealed class LangPopulatedTests : IDisposable
{
    public void Dispose() => L.ResetForTests();

    static readonly Dictionary<string, string> Roles = new() { ["general"] = "--dpi-desync=fake,multisplit --dpi-desync-fooling=md5sig --dpi-desync-repeats=6", ["google"] = "--dpi-desync=multidisorder --dpi-desync-split-pos=midsld" };

    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    public void Strategy_list_with_history_and_foreign_programs(string lang)
    {
        using var tmp = new TempDir();
        var snapDir = Environment.GetEnvironmentVariable("DPISHELL_SNAPSHOTS");
        var errors = WithBindingErrors(() =>
        {
            ThemeManager.Apply(ThemeManager.Light);
            L.Set(lang);
            var shell = new Shell(new AppPaths(tmp.Sub("root"), Repo.Data));
            var f = new HelperFixture();
            f.Backend.TrackEnabled = true;
            var auto = new Strategy("found-261018-0733-1", "Новая стратегия 1 (поиск 18.10 07:33)", "search", Roles);
            var found = new Strategy("found-261016-2213-2", "Найденная 2 (261016-2213)", "search", new Dictionary<string, string>());
            var mix = new Strategy("mix-1", "Смешанная · YouTube — general (ALT) · свои сайты и Discord — general (ALT2)", "mix", Roles);
            var target = new StrategyStats.TargetStat("youtube", "https://www.youtube.com/", true, true, false, 0.4, [new StrategyStats.ProbeStat("Tls13", "Ok", "301 → www.youtube.com/ → 200, 512 КБ", 0.4)]);
            var slow = new StrategyStats.TargetStat("discord", "https://discord.com/", false, true, false, 5, [new StrategyStats.ProbeStat("Tls13", "Timeout", "таймаут", 5), new StrategyStats.ProbeStat("Tls12", "Truncated", "200, получено 16384 из 500000", 1)]);
            var own = new StrategyStats.TargetStat("user", "свой адрес №1", false, true, false, 1, [new StrategyStats.ProbeStat("Tls13", "ForeignRedirect", "302: редирект на warning.rt.ru", 0.3)]);
            var control = new StrategyStats.TargetStat("control", "https://example.com/", false, true, true, 1, [new StrategyStats.ProbeStat("Tls13", "Reset", "", 1)]);
            f.Backend.DetailsValue = f.Backend.DetailsValue with { Found = [auto, found, mix] };
            f.Backend.StatusValue = f.Backend.StatusValue with
            {
                Strategies = [.. f.Backend.StatusValue.Strategies, new("found-261018-0733-1", auto.Title, "search"), new("found-261016-2213-2", found.Title, "search"), new("mix-1", mix.Title, "mix")],
                Foreign = [new Conflict("service", "zapret от Flowseal (служба zapret), стратегия general (ALT11)", "Запустите service.bat из папки Flowseal от администратора → «Remove Services».")],
            };
            f.Backend.ResultsValue = new(new Dictionary<string, StrategyStats.Stat>
            {
                ["alt9"] = new(0.5, 3, 1, Newest: new StrategyStats.NewestRun("2026-10-18_16-39-00.json", 1, 3, true), Last: new StrategyStats.LastRun("2026-10-18_16-39-00.json", [target, slow, own, control])),
                [auto.Id] = new(1, 1, 1, Newest: new StrategyStats.NewestRun("2026-10-18_07-33-00.json", 2, 2, false)),
                [mix.Id] = new(1, 1, 1, Newest: new StrategyStats.NewestRun("2026-10-19_12-00-00.json", 2, 2, false)),
            }, []);
            var link = Task.Run(f.LinkAsync).GetAwaiter().GetResult();
            var vm = TestWindow.Model(shell, link);
            vm.ProbeGroups = groups => Task.FromResult<IReadOnlyList<TargetVerdict>>(groups.SelectMany(g => g.Targets.Select(t => new TargetVerdict(g.Id, t, true, 0.3, []))).ToList());
            var w = Open(vm);
            w.Height = 1100;
            SetProp(vm, "IsLoading", false);
            vm.ReloadChoices();
            WaitUi(vm.RefreshStatusAsync());
            vm.GoTo(Pages.Home);
            Pump();
            if (snapDir is not null) { Directory.CreateDirectory(snapDir); Snap(w, Path.Combine(snapDir, $"populated-home-{lang}.png")); }
            CyrillicScan.CheckIfEn(w, "populated-home");

            // Плашки главной (VPN, подмена DNS) и DNS ПК: «Сейчас: Google, защищённый · Ethernet», «VPN … не меняются».
            typeof(MainViewModel).GetProperty("VpnNames", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(vm, "WireGuard Tunnel");
            SetProp(vm, "DnsSpoofed", true);
            vm.ShowPcDns(new DnsStatusInfo("google", true, false, true, true,
                [new DnsAdapterInfo("Ethernet", "ethernet", false, ["8.8.8.8", "8.8.4.4"], true)], ["Radmin VPN"]));
            Pump();
            CyrillicScan.CheckIfEn(w, "populated-home-banners");
            vm.GoTo(NetPages.Dns);
            Pump();
            foreach (var e in FindAll<Expander>(w, x => x.IsVisible)) e.IsExpanded = true;
            vm.DnsDetailsOpen = true;
            Pump();
            if (snapDir is not null) Snap(w, Path.Combine(snapDir, $"populated-dns-{lang}.png"));
            CyrillicScan.CheckIfEn(w, "populated-dns");
            Close(w);
            Task.Run(async () => { await vm.Helper.DisposeAsync(); await f.DisposeAsync(); }).GetAwaiter().GetResult();
        });
        Assert.Empty(errors);
    }
}
