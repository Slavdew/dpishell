using System.Windows;
using System.Windows.Controls;
using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.Tests;

/// <summary>
/// UX-аудит 8, §3 №7–13 (этап Д): настройки не появляются и не исчезают от косвенно связанных переключателей —
/// остаются на месте, а когда менять нельзя, серые с короткой причиной рядом.
/// </summary>
public sealed partial class WindowSmokeTests
{
    static T Named<T>(FrameworkElement w, string name) where T : class => w.FindName(name) as T ?? throw new Exception("Нет элемента " + name);

    /// <summary>
    /// №7, 8, 12: роутер ни разу не находился — переключателей «ПК / роутер» нет (выбирать не из чего), карточка «Совместная работа» на месте, серая с причиной.
    /// Роутер подключён — «На роутере» доступен на «Сайтах» и «Подборе», совместную работу можно включить. Роутер пропал — переключатели на месте,
    /// «На роутере» серый, под ним «Роутер не подключён. Найти →», выбор «На роутере» не стёрт; карточка совместной работы на месте, серая с причиной.
    /// </summary>
    [Fact]
    public void RouterSwitches_And_JointCard_StayInPlace_WhenRouterGone()
    {
        using var tmp = new TempDir();
        var errors = WithBindingErrors(() =>
        {
            ThemeManager.Apply(ThemeManager.Light);

            // Роутера не было ни разу.
            {
                var shell = new Shell(new AppPaths(tmp.Sub("never"), Repo.Data));
                var f = new HelperFixture();
                var link = Task.Run(f.LinkAsync).GetAwaiter().GetResult();
                var vm = TestWindow.Model(shell, link);
                var w = Open(vm);
                SetProp(vm, "IsLoading", false);
                foreach (var (page, name) in new[] { (Pages.Sites, "SitesTargetSwitch"), (Pages.Select, "SelectWhereSwitch") })
                {
                    vm.GoTo(page);
                    Pump();
                    if (Named<FrameworkElement>(w, name).IsVisible) throw new Exception(name + " виден, хотя роутер ни разу не находился");
                }
                vm.GoTo(Pages.Router);
                Pump();
                CheckJointOff(w, vm);
                Close(w);
                Stop(vm, f);
            }

            // Роутер был, потом пропал.
            {
                using var fake = new FakeRouter { WarningsJson = "[]" }.As020();
                fake.UpdateFieldOn = false;
                var shell = new Shell(new AppPaths(tmp.Sub("seen"), Repo.Data));
                var f = new HelperFixture();
                var link = Task.Run(f.LinkAsync).GetAwaiter().GetResult();
                var vm = TestWindow.Model(shell, link);
                vm.Router.AddressAuto = false;
                vm.Router.CustomAddress = fake.Address;
                vm.Router.PcTimestamps = () => true;
                vm.Router.OwnMac = _ => "aa:bb:cc:dd:ee:01";
                Task.Run(() => vm.Router.ConnectAsync("secret")).GetAwaiter().GetResult();
                if (!vm.Router.Connected) throw new Exception("Подставной роутер не подключился");
                if (!vm.Settings.RouterSeen) throw new Exception("Найденный роутер не запомнен");
                var w = Open(vm);
                w.Height = 900;
                SetProp(vm, "IsLoading", false);

                // Подключён: «На роутере» доступен, причины нет.
                vm.GoTo(Pages.Sites);
                Pump();
                CheckSwitch(w, "SitesTargetSwitch", "SitesTargetRouter", "SitesTargetOff", routerOn: true);
                Named<RadioButton>(w, "SitesTargetRouter").IsChecked = true;
                Pump();
                if (!vm.Router.TargetsOnRouter) throw new Exception("«На роутере» на «Сайтах» не выбрался");
                vm.GoTo(Pages.Select);
                Pump();
                CheckSwitch(w, "SelectWhereSwitch", "SelectWhereRouter", "SelectWhereOff", routerOn: true);
                if (!vm.Router.SelectOnRouter) throw new Exception("После входа подбор не на роутере по умолчанию");
                vm.GoTo(Pages.Router);
                Pump();
                if (!Named<FrameworkElement>(w, "JointCard").IsVisible) throw new Exception("Нет карточки совместной работы при подключённом роутере");
                if (Named<FrameworkElement>(w, "JointOffReasonText").IsVisible) throw new Exception("Причина «серого» переключателя при подключённом роутере");

                // Роутер пропал.
                fake.Remove();
                for (var until = DateTime.UtcNow.AddSeconds(60); vm.Router.Found && DateTime.UtcNow < until;)
                {
                    RefreshRouter(vm.Router);
                    Pump();
                }
                if (vm.Router.Found || vm.Router.Connected) throw new Exception("Роутер не признан пропавшим");
                Pump();
                CheckJointOff(w, vm);
                vm.GoTo(Pages.Sites);
                Pump();
                CheckSwitch(w, "SitesTargetSwitch", "SitesTargetRouter", "SitesTargetOff", routerOn: false);
                if (!vm.Router.TargetsOnRouter) throw new Exception("Выбор «На роутере» стёрся, когда роутер пропал");
                vm.GoTo(Pages.Select);
                Pump();
                CheckSwitch(w, "SelectWhereSwitch", "SelectWhereRouter", "SelectWhereOff", routerOn: false);
                if (!vm.Router.SelectOnRouter) throw new Exception("Выбор «Где подбирать: на роутере» стёрся, когда роутер пропал");

                // «Найти →» ведёт на вкладку «Роутер».
                var tabs = Find<TabControl>(w)!;
                var find = Find<Button>(tabs, b => b.IsVisible && b.Content as string == L.T("router.target.find")) ?? throw new Exception("Нет «Найти →»");
                find.Command.Execute(null);
                Pump();
                if (vm.Page != Pages.Router) throw new Exception("«Найти →» не открыл вкладку «Роутер»");
                Close(w);
                Stop(vm, f);
            }
        });
        Assert.True(errors.Count == 0, "Ошибки привязок:\n" + string.Join("\n", errors));
    }

    static void CheckSwitch(MainWindow w, string panel, string routerRadio, string offText, bool routerOn)
    {
        if (!Named<FrameworkElement>(w, panel).IsVisible) throw new Exception(panel + " не виден");
        var radio = Named<RadioButton>(w, routerRadio);
        if (!radio.IsVisible) throw new Exception(routerRadio + " не виден");
        if (radio.IsEnabled != routerOn) throw new Exception($"{routerRadio}: доступен = {radio.IsEnabled}, ждали {routerOn}");
        if (!routerOn && radio.IsChecked == true) throw new Exception(routerRadio + " отмечен без роутера");
        var off = Named<TextBlock>(w, offText);
        if (off.IsVisible == routerOn) throw new Exception($"{offText}: виден = {off.IsVisible}, ждали {!routerOn}");
        if (!routerOn && off.Text != L.T("router.target.off")) throw new Exception("Причина не та: " + off.Text);
    }

    static void CheckJointOff(MainWindow w, MainViewModel vm)
    {
        if (!Named<FrameworkElement>(w, "JointCard").IsVisible) throw new Exception("Карточка совместной работы исчезла без роутера");
        var tabs = Find<TabControl>(w)!;
        var sw = Find<CheckBox>(tabs, c => c.IsVisible && System.Windows.Automation.AutomationProperties.GetName(c) == L.T("router.joint.title"))
                 ?? throw new Exception("Нет переключателя совместной работы");
        if (sw.IsEnabled) throw new Exception("Переключатель совместной работы доступен без роутера");
        var reason = Named<TextBlock>(w, "JointOffReasonText");
        if (!reason.IsVisible || reason.Text != L.T("router.joint.needs_router")) throw new Exception("Нет причины у серого переключателя: " + reason.Text);
    }

    /// <summary>
    /// №9, 10: «Отладка подключений (для опытных)» — свёрнутый раздел внизу «Игр и программ», виден всегда, раскрывается без галки в «Настройках»;
    /// «Другие параметры» у профиля — всегда. В «Настройках» галки «Показывать инструменты» нет, «Выгрузить шаблон перевода» — рядом с языками.
    /// №13: «Проверить адреса» в hosts на месте и при выключенном — серая с «Сначала включите.»; включили — доступна, причины нет.
    /// </summary>
    [Fact]
    public void DebugSection_LangTemplate_HostsCheck_StayInPlace()
    {
        using var tmp = new TempDir();
        var errors = WithBindingErrors(() =>
        {
            ThemeManager.Apply(ThemeManager.Light);
            var shell = new Shell(new AppPaths(tmp.Sub("root"), Repo.Data));
            shell.Data.Seed();
            var f = new HelperFixture();
            f.Backend.AppProfileSet(null, "Моя игра", "443", "27015", ["203.0.113.50"], 0);
            var link = Task.Run(f.LinkAsync).GetAwaiter().GetResult();
            var vm = TestWindow.Model(shell, link);
            var w = Open(vm);
            w.Height = 900;
            SetProp(vm, "IsLoading", false);
            vm.ApplyHelperData();
            var tabs = Find<TabControl>(w)!;
            Button? Btn(string content) => Find<Button>(tabs, b => b.IsVisible && b.Content as string == content);

            // №9: раздел на месте и свёрнут; «Отладка подключений →» раскрывает его; «Другие параметры» у профиля без всяких галок.
            vm.GoTo(Pages.Apps);
            PumpUntil(() => vm.Apps.Profiles.Count == 1, "профиль от помощника");
            Pump();
            var debug = Named<Expander>(w, "DebugSection");
            if (!debug.IsVisible || debug.IsExpanded) throw new Exception("«Отладка подключений» не видна или раскрыта по умолчанию");
            if (!VisibleText(tabs, t => t.Text == L.T("apps.debug.section"))) throw new Exception("Нет заголовка «Отладка подключений (для опытных)»");
            if (Btn(L.T("apps.debug.record")) is not null) throw new Exception("Содержимое свёрнутого раздела видно");
            if (Btn(L.T("apps.profile.other_variant")) is null) throw new Exception("Нет «Другие параметры» у профиля");
            (Btn(L.T("apps.profiles.record")) ?? throw new Exception("Нет «Записать игру…»")).Command.Execute(null);   // ссылка — в карточке записи
            Pump();
            (Btn(L.T("apps.more")) ?? throw new Exception("Нет «Отладка подключений →»")).Command.Execute(null);
            Pump();
            if (!debug.IsExpanded || Btn(L.T("apps.debug.record")) is null) throw new Exception("«Отладка подключений →» не раскрыла раздел");

            // №10: галки «для опытных» нет, шаблон перевода — у языков.
            vm.GoTo(Pages.Settings);
            Pump();
            if (Find<CheckBox>(tabs, c => c.IsVisible && c.Content as string == "Показывать инструменты") is not null) throw new Exception("В «Настройках» осталась галка «Показывать инструменты»");
            if (!Named<FrameworkElement>(w, "LangTemplateRow").IsVisible || Btn(L.T("settings.lang.template")) is null) throw new Exception("Нет «Выгрузить шаблон перевода»");

            // №13: «Проверить адреса» видна при обоих состояниях переключателя.
            vm.Hosts.ListOpen = true;
            vm.GoTo(NetPages.Hosts);
            Pump();
            var check = Named<Button>(w, "HostsCheckButton");
            var off = Named<TextBlock>(w, "HostsCheckOff");
            if (vm.Hosts.Enabled) throw new Exception("hosts включён по умолчанию");
            if (!check.IsVisible || check.IsEnabled) throw new Exception("«Проверить адреса» при выключенном: видна = " + check.IsVisible + ", доступна = " + check.IsEnabled);
            if (!off.IsVisible || off.Text != L.T("hosts.check_needs_on")) throw new Exception("Нет причины «Сначала включите.»");
            vm.Hosts.Enabled = true;
            Pump();
            if (!check.IsVisible || !check.IsEnabled) throw new Exception("«Проверить адреса» при включённом: видна = " + check.IsVisible + ", доступна = " + check.IsEnabled);
            if (off.IsVisible) throw new Exception("Причина видна при включённом");
            Close(w);
            Stop(vm, f);
        });
        Assert.True(errors.Count == 0, "Ошибки привязок:\n" + string.Join("\n", errors));
    }
}
