using System.IO;
using System.Text.RegularExpressions;
using DpiShell.App;
using DpiShell.Core;
using static DpiShell.Tests.WindowSmokeTests;

namespace DpiShell.Tests;

/// <summary>
/// Возраст итога проверки роутера: порог «старше часа» один в окне и на странице роутера, «N мин/ч назад» или число и месяц — по языку.
/// Язык переключается — поэтому в коллекции lang.
/// </summary>
[Collection(LangCollection.Name)]
public sealed class RouterStaleCheckTests : IDisposable
{
    public void Dispose() => L.ResetForTests();

    static readonly DateTime At = new(2026, 10, 23, 12, 28, 0, DateTimeKind.Utc);

    [Fact]
    public void Threshold_same_in_window_and_router_page()
    {
        var api = Path.Combine(Directory.GetParent(Repo.Data)!.FullName, "router", "luci-app-dpishell", "htdocs", "luci-static", "resources", "dpishell", "api.js");
        var m = Regex.Match(File.ReadAllText(api), @"CHECK_STALE_S:\s*(\d+)");
        Assert.True(m.Success, "В api.js нет CHECK_STALE_S");
        Assert.Equal(RouterTexts.CheckStaleAfter.TotalSeconds, double.Parse(m.Groups[1].Value));
    }

    [Fact]
    public void Stale_after_one_hour_only()
    {
        Assert.False(RouterTexts.IsStale(At, At.AddMinutes(59)));
        Assert.False(RouterTexts.IsStale(At, At.AddHours(1)));
        Assert.True(RouterTexts.IsStale(At, At.AddHours(1).AddSeconds(1)));
        Assert.False(RouterTexts.IsStale(null, At.AddDays(3)));                                       // времени нет — не узнать, как раньше
        Assert.False(RouterTexts.IsStale(At, At.AddHours(-3)));                                       // часы ПК отстают — не «давно»
        Assert.True(RouterTexts.IsStale(DateTime.SpecifyKind(At, DateTimeKind.Unspecified), At.AddHours(2)));   // без зоны — UTC, как Time()
    }

    /// <summary>Старый итог на английском: вкладка «Роутер» и панель «Роутер: …» без кириллицы, возраст — «checked 5 h ago — not everything opened».</summary>
    [Fact]
    public void Stale_check_window_in_en()
    {
        using var tmp = new TempDir();
        var errors = WithBindingErrors(() =>
        {
            ThemeManager.Apply(ThemeManager.Light);
            using var fake = new FakeRouter
            {
                WarningsJson = """[ { "code": "strategy_failing", "text": "Стратегия не проходит проверку — нужен подбор", "key": "web.warn.strategy_failing", "params": { "strategy": "alt10" } } ]""",
                LastCheckOk = false, LastCheckSource = "manual", ScheduleEnabled = true,
            }.As020();
            var shell = new Shell(new AppPaths(tmp.Sub("root"), Repo.Data));
            var vm = TestWindow.Model(shell);
            vm.Router.AddressAuto = false;
            vm.Router.CustomAddress = fake.Address;
            vm.Router.PcTimestamps = () => true;
            vm.Router.UtcNow = () => fake.CheckedAt.AddHours(5).AddMinutes(10);
            Task.Run(() => vm.Router.ConnectAsync("secret")).GetAwaiter().GetResult();
            var w = Open(vm);
            SetProp(vm, "IsLoading", false);
            L.Set(L.En);
            vm.GoTo(Pages.Router);
            Pump();
            Assert.Contains("checked 5 h ago — not everything opened", vm.Router.HomeDetail);
            CyrillicScan.CheckIfEn(w, "router-stale-en");
            vm.GoTo(Pages.Home);
            vm.Panel = HomePanel.Router;
            Pump();
            PumpUntil(() => !vm.Router.Busy, "diag роутера для панели не дочитался");
            Pump();
            Assert.Contains(vm.RouterItems, i => i.Value == "checked 5 h ago — not everything opened" && i.Action == "Check now");
            CyrillicScan.CheckIfEn(w, "home-router-stale-en");
            Close(w);
        });
        Assert.True(errors.Count == 0, "Ошибки привязок:\n" + string.Join("\n", errors));
    }

    [Fact]
    public void Ago_texts_in_ru_and_en()
    {
        L.Set(L.Ru);
        Assert.Equal("12 мин назад", RouterTexts.Ago(At, At.AddMinutes(12)));
        Assert.Equal("1 мин назад", RouterTexts.Ago(At, At.AddSeconds(5)));
        Assert.Equal("5 ч назад", RouterTexts.Ago(At, At.AddHours(5).AddMinutes(42)));
        Assert.Equal(L.DayMonth(At.ToLocalTime()), RouterTexts.Ago(At, At.AddDays(2)));
        Assert.Equal("проверено 5 ч назад — открывалось не всё", L.T("router.home.checked_bad_ago", new { ago = RouterTexts.Ago(At, At.AddHours(5)) }));
        L.Set(L.En);
        Assert.Equal("12 min ago", RouterTexts.Ago(At, At.AddMinutes(12)));
        Assert.Equal("5 h ago", RouterTexts.Ago(At, At.AddHours(5)));
        Assert.Equal("checked 5 h ago — not everything opened", L.T("router.home.checked_bad_ago", new { ago = RouterTexts.Ago(At, At.AddHours(5)) }));
        Assert.Equal("checked " + L.DayMonth(At.ToLocalTime()), L.T("router.home.checked_ago", new { ago = RouterTexts.Ago(At, At.AddDays(2)) }));
    }
}
