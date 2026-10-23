using System.Windows.Controls;
using DpiShell.App;
using DpiShell.Core;

namespace DpiShell.Tests;

public sealed partial class WindowSmokeTests
{
    /// <summary>
    /// «Сайты» → «На роутере», «Сайт не открывается», режим «Только из списков»: сайта нет в списках и он не открывается — итог прямо
    /// («нет в списке оптимизации»), «Добавить в список оптимизации» (не исключения) и «Проверить снова»; после добавления — user-general
    /// на роутере, проверка заново, итог «с оптимизацией открывается». Сеть — FakeRouter.
    /// </summary>
    [Fact]
    public void RouterSites_SiteCheck_NotInList_AddToList_AndCheckAgain()
    {
        using var tmp = new TempDir();
        var errors = WithBindingErrors(() =>
        {
            ThemeManager.Apply(ThemeManager.Light);
            using var fake = new FakeRouter { Mode = "lists", SiteWithBypassOk = false, SiteWithoutBypassOk = true };
            var shell = new Shell(new AppPaths(tmp.Sub("site-router"), Repo.Data));
            var vm = TestWindow.Model(shell);
            var w = Open(vm);
            SetProp(vm, "IsLoading", false);
            var router = vm.Router;
            router.AddressAuto = false;
            router.CustomAddress = fake.Address;
            router.PollInterval = TimeSpan.FromMilliseconds(10);
            router.Gateway = () => "192.168.1.1";
            router.PcTimestamps = () => true;
            WaitUi(router.ConnectAsync("secret"));
            if (!router.Connected) throw new Exception("Роутер не принял вход");
            router.TargetsOnRouter = true;
            vm.GoTo(Pages.Sites);
            Pump();
            router.SiteUrl = "bank.example";
            WaitUi(router.SiteCheckAsync());
            Pump();
            var tabs = Find<TabControl>(w)!;
            bool Shown(Func<string, bool> m) => Find<TextBlock>(tabs, t => t.IsVisible && m(t.Text)) is not null || Find<TextBox>(tabs, t => t.IsVisible && m(t.Text)) is not null;
            Button? Btn(string text) => Find<Button>(tabs, b => b.IsVisible && b.Content as string == text);

            if (!Shown(t => t == "Сайт не открывается: bank.example нет в списке оптимизации")) throw new Exception("Нет итога «нет в списке оптимизации»: " + router.SiteResult);
            if (!Shown(t => t == L.T("web.site.not_in_list_hint"))) throw new Exception("Нет пояснения про «Только из списков»");
            if (Btn(L.T("home.sitecheck.add_list")) is not { IsEnabled: true } add) throw new Exception("Нет доступной «Добавить в список оптимизации»");
            if (Btn(L.T("sites.check.add_exclude")) is not null) throw new Exception("«Добавить в исключения» при сайте вне списков");
            if (Btn(L.T("home.sitecheck.again")) is null) throw new Exception("Нет «Проверить снова» у итога");

            fake.SiteWithBypassOk = true;   // в списке — открывается
            WaitUi(router.AddSiteToListAsync());
            Pump();
            if (!fake.Lists.TryGetValue("user-general", out var general) || !general.Contains("bank.example")) throw new Exception("Сайт не ушёл в user-general роутера");
            if (!Shown(t => t == L.T("router.site.opens"))) throw new Exception("После добавления нет итога «С оптимизацией сайт открывается»: " + router.SiteResult);
            if (!Shown(t => t.StartsWith(L.T("web.site.listed", new { host = "bank.example" })))) throw new Exception("Нет строки «в списке оптимизации роутера»");
            if (Btn(L.T("home.sitecheck.add_list")) is not null) throw new Exception("«Добавить в список оптимизации» осталась после добавления");

            // «Проверить снова» — тот же сайт ещё раз.
            fake.SiteWithBypassOk = false;
            fake.SiteWithoutBypassOk = false;
            var again = Btn(L.T("home.sitecheck.again")) ?? throw new Exception("Нет «Проверить снова» после добавления");
            again.Command.Execute(null);
            for (int i = 0; i < 500 && router.SiteResult != L.T("router.site.closed"); i++) { Pump(); Thread.Sleep(10); }
            if (!Shown(t => t == L.T("router.site.closed"))) throw new Exception("«Проверить снова» не проверила заново: " + router.SiteResult);
            Close(w);
        });
        Assert.True(errors.Count == 0, "Ошибки привязок:\n" + string.Join("\n", errors));
    }
}
