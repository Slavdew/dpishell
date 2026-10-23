using System.Security.Authentication;
using System.Windows.Controls;
using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.Tests;

public sealed partial class WindowSmokeTests
{
    /// <summary>
    /// Группы «Своих сайтов»: вкладка «Сайты» (шапки групп, свёрнутая группа, «Новая группа») и итог подбора (строка на группу, выборка, закрытый по адресу).
    /// Снимки при DPISHELL_SNAPSHOTS: site-groups-sites.png, site-groups-select.png.
    /// </summary>
    [Fact]
    public async Task SiteGroups_SitesPage_And_SelectSummary_Bind_And_Snap()
    {
        var snapDir = Environment.GetEnvironmentVariable("DPISHELL_SNAPSHOTS");
        if (snapDir is not null) Directory.CreateDirectory(snapDir);
        using var tmp = new TempDir();
        await using var f = new HelperFixture();

        // Отчёт подбора: Telegram — 9 из 120 проверены, ни один не открылся, ещё один закрыт по адресу; «Прочее» — 7 из 8.
        static Target T(string host, string? group) => new($"https://{host}/", Group: group);
        static ProbeResult Pr(Target t, ProbeOutcome o, bool noTcp = false) => new(t, SslProtocols.Tls13, o, 0.2, 0, "", NoTcp: noTcp);
        static TargetVerdict V(Target t, bool ok, bool noTcp = false) =>
            new("user", t, ok, ok ? 0.3 : 0, [Pr(t, ok ? ProbeOutcome.Ok : noTcp ? ProbeOutcome.Timeout : ProbeOutcome.Reset, noTcp)]);
        var telegram = Enumerable.Range(1, 9).Select(i => T($"tg{i}.example", "Telegram")).ToList();
        var dead = T("dead.example", "Telegram");
        var other = Enumerable.Range(1, 8).Select(i => T($"site{i}.example", null)).ToList();
        var verdicts = telegram.Select(t => V(t, false)).Concat(other.Select((t, i) => V(t, i != 3))).ToList();
        var tested = new TargetGroup("user", "Свои сайты", [.. telegram, dead, .. other], Totals: new() { ["Telegram"] = 120, [""] = 8 });
        var score = new StrategyScore(new Strategy("alt9", "general (ALT9)", "flowseal", new Dictionary<string, string> { ["general"] = "--dpi-desync=fake" }), verdicts, 0, 1)
        { Blocked = [V(dead, false, noTcp: true)] };
        var report = new SelectionReport([V(dead, false, noTcp: true)], [tested], [score], false) { AddressBlocked = [dead.Url] };

        var errors = WithBindingErrors(() =>
        {
            ThemeManager.Apply(ThemeManager.Light);
            var shell = new Shell(new AppPaths(tmp.Sub("root"), Repo.Data));
            var link = Task.Run(f.LinkAsync).GetAwaiter().GetResult();
            var vm = TestWindow.Model(shell, link);
            var w = Open(vm);
            SetProp(vm, "IsLoading", false);
            vm.Confirm = _ => true;
            WaitUi(vm.RefreshStatusAsync());

            // Сайты: две группы, одна свёрнута.
            vm.GoTo(Pages.Sites);
            vm.TargetsText = string.Join(Environment.NewLine, other.Select(t => new Uri(t.Url).Host));
            var tg = vm.NewGroup("Telegram");
            vm.MoveSites(vm.SiteGroupRows[^1], [new Uri(other[0].Url).Host, new Uri(other[1].Url).Host], tg);
            var sp = vm.NewGroup("Spotify");
            sp.Text = "spotify.com\r\nscdn.co";
            sp.Expanded = false;
            Pump();
            var tabs = Find<TabControl>(w)!;
            var boxes = FindAll<TextBox>(tabs, b => b.IsVisible && b.Tag as string == "SitesBox").ToList();
            Assert.Equal(2, boxes.Count);   // Telegram и «Прочее»; Spotify свёрнута
            Assert.NotNull(Find<Button>(tabs, b => b.IsVisible && b.Content as string == L.T("sites.group.add")));
            Assert.NotNull(Find<Button>(tabs, b => b.IsVisible && b.Content as string == L.T("sites.group.delete")));
            if (snapDir is not null) Snap(w, Path.Combine(snapDir, "site-groups-sites.png"));

            // Подбор: строка на группу под итогом.
            vm.GoTo(Pages.Select);
            typeof(MainViewModel).GetMethod("ShowReport", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(vm, [report]);
            Pump();
            Assert.Equal(["Telegram ✗ 0/9 — проверено 10 из 120; заблокировано по адресу: 1 — оптимизация не поможет", "Прочее ✓ 7/8 — не открыто: site4.example"], vm.ResultGroupLines);
            Assert.True(vm.HasResultGroupLines);
            // В строке таблицы закрытый по адресу не назван провалом стратегии.
            var row = vm.Scores.Single();
            Assert.Contains(row.Details, r => r.Note == L.T("select.check.note.addr_blocked") && !r.Alert);
            if (snapDir is not null) Snap(w, Path.Combine(snapDir, "site-groups-select.png"));
        });
        Assert.Empty(errors);
    }
}
