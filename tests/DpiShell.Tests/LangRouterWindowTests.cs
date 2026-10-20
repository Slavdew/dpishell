using System.Windows;
using System.Windows.Controls;
using DpiShell.App;
using DpiShell.Core;
using static DpiShell.Tests.WindowSmokeTests;

namespace DpiShell.Tests;

/// <summary>
/// Роутер на двух языках: вкладка «Роутер» (пустая и с подключённым роутером), строка на главной, диалоги входа, установки, удаления
/// и «ключ роутера изменился». Привязки без ошибок, строки языка стоят в окне, английский не обрезан (снимки при DPISHELL_SNAPSHOTS:
/// router-&lt;язык&gt;-&lt;вид&gt;.png).
/// </summary>
[Collection(LangCollection.Name)]
public sealed class LangRouterWindowTests : IDisposable
{
    public void Dispose() => L.ResetForTests();

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public void Router_pages_and_dialogs_follow_language(string lang)
    {
        using var tmp = new TempDir();
        using var fake = new FakeRouter
        {
            SelectSteps = 0,
            ConflictsJson = """[ { "kind": "service", "name": "zapret2", "running": true }, { "kind": "package", "name": "zapret-manager-beam", "running": true } ]""",
            SelectRowsJson = """
                [ { "id": "alt", "group_share": 1.0, "target_share": 0.88, "avg_s": 0.74, "error": null },
                  { "id": "alt9", "group_share": 1.0, "target_share": 0.80, "avg_s": 0.61, "error": null },
                  { "id": "d-17", "title": "fake + multisplit, ts — YouTube", "group_share": 0.5, "target_share": 0.62, "avg_s": 0.83, "error": null, "candidate": true, "ok": 3, "total": 4 },
                  { "id": "alt2", "group_share": 0.0, "target_share": 0.0, "avg_s": 0, "error": "ломает обычные сайты: vk.com" } ]
                """,
        };
        var snapDir = Environment.GetEnvironmentVariable("DPISHELL_SNAPSHOTS");
        void Shot(FrameworkElement w, string name)
        {
            Pump();
            CyrillicScan.CheckIfEn(w, "router-" + name);
            if (snapDir is null) return;
            Directory.CreateDirectory(snapDir);
            Save(w is Window win ? (FrameworkElement)System.Windows.Media.VisualTreeHelper.GetChild(win, 0) : w, Path.Combine(snapDir, $"router-{lang}-{name}.png"));
        }
        Window Show(Window d)
        {
            d.WindowStartupLocation = WindowStartupLocation.Manual;
            d.Left = d.Top = -20000;
            d.ShowActivated = false;
            d.Show();
            Pump();
            return d;
        }
        var errors = WithBindingErrors(() =>
        {
            ThemeManager.Apply(ThemeManager.Light);
            L.Set(lang);

            // Без роутера: пустое состояние вкладки и карточки нет.
            {
                var shell = new Shell(new AppPaths(tmp.Sub("empty"), Repo.Data));
                var vm = TestWindow.Model(shell);
                vm.Router.AddressAuto = false;
                vm.Router.CustomAddress = "127.0.0.1:1";
                var w = Open(vm);
                SetProp(vm, "IsLoading", false);
                vm.Page = Pages.Router;
                Pump();
                var tabs = Find<TabControl>(w)!;
                if (Find<Button>(tabs, b => b.Content as string == L.T("router.empty.fit") && b.IsVisible) is null) throw new Exception("Нет «Проверить, подходит ли мой роутер»");
                if (Find<Button>(tabs, b => b.Content as string == L.T("router.empty.guide") && b.IsVisible) is null) throw new Exception("Нет ссылки «Как поставить»");
                if (Find<TextBlock>(tabs, t => t.Text == vm.Router.EmptyTitle && t.IsVisible) is null) throw new Exception("Нет заголовка пустого состояния");
                Shot(w, "tab-empty");
                Close(w);
            }

            // С роутером: главная, вкладка, диалоги.
            {
                var shell = new Shell(new AppPaths(tmp.Sub("connected"), Repo.Data));
                var settings = TestWindow.Settings(shell);
                var vm = TestWindow.Model(shell, null, settings);
                var router = vm.Router;
                router.AddressAuto = false;
                router.CustomAddress = fake.Address;
                router.PollInterval = TimeSpan.FromMilliseconds(10);
                router.Gateway = () => null;
                router.PcTimestamps = () => false;
                router.EnablePcTimestamps = _ => Task.FromResult(false);
                router.Confirm = _ => true;
                Task.Run(async () =>
                {
                    if (!await router.ConnectAsync("secret")) throw new Exception("Не подключились: " + router.ConnectionText);
                    await router.StartSelectAsync(deep: false);
                    await router.RefreshDiagAsync();
                    await router.RefreshRouterPageAsync();
                }).GetAwaiter().GetResult();
                var w = Open(vm);
                SetProp(vm, "IsLoading", false);
                foreach (var (page, name) in new[] { (Pages.Home, "home"), (Pages.Router, "tab") })
                {
                    vm.Page = page;
                    Pump();
                    var tabs = Find<TabControl>(w)!;
                    if (page == Pages.Home && Find<TextBlock>(tabs, t => t.Text == L.T("router.home.on") && t.IsVisible) is null) throw new Exception("На главной нет строки роутера");
                    if (page == Pages.Router)
                    {
                        if (Find<TextBlock>(tabs, t => t.Text == L.T("router.diag.title") && t.IsVisible) is null) throw new Exception("Нет «Роутер: что мешает»");
                        if (Find<TextBlock>(tabs, t => t.Text == L.T("router.schedule.title") && t.IsVisible) is null) throw new Exception("Нет расписания");
                        if (Find<TextBlock>(tabs, t => t.Text == L.T("router.dns.title") && t.IsVisible) is null) throw new Exception("Нет DNS для всей сети");
                    }
                    Shot(w, name);
                }
                vm.Page = Pages.Select;
                Pump();
                Shot(w, "select");
                Close(w);

                var login = Show(RouterLoginDialog.Create(router, null));
                Shot(login, "login");
                login.Close();

                var i = router.NewInstall();
                var d = Show(RouterInstallDialog.Create(i, null));
                if (Find<Button>(d, b => b.Content as string == L.T("router.install.check_router") && b.IsVisible) is null) throw new Exception("Нет «Проверить роутер»");
                Shot(d, "install");
                d.Close();

                var u = router.NewUninstall();
                var ud = Show(RouterUninstallDialog.Create(u, null));
                Shot(ud, "uninstall");
                ud.Close();

                var (hk, _) = HostKeyDialog.Create(RouterViewModel.HostKeyText, null);
                Show(hk);
                Shot(hk, "hostkey");
                hk.Close();
            }
        });
        Assert.Empty(errors);
    }
}
