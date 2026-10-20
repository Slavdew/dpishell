using System.Windows;
using System.Windows.Controls;
using DpiShell.App;
using DpiShell.Core;

namespace DpiShell.Tests;

/// <summary>
/// Каркас окна: меню слева (значки, сворачивание, точка у «Информации», низ), вкладка «Сеть» (блоки DNS → «Прокси» → «Починить адреса»),
/// единый отступ раскрывающихся блоков, «Показывать роутер» (docs/localization.md, замечания владельца 2026-10-20, п. 10–12).
/// </summary>
public sealed partial class WindowSmokeTests
{
    [Fact]
    public void Shell_Menu_Collapses_Remembers_AndShowsUpdateDot()
    {
        var snapDir = Environment.GetEnvironmentVariable("DPISHELL_SNAPSHOTS");
        using var tmp = new TempDir();
        var errors = WithBindingErrors(() =>
        {
            foreach (var theme in new[] { ThemeManager.Light, ThemeManager.Dark })
            {
                ThemeManager.Apply(theme);
                var shell = new Shell(new AppPaths(tmp.Sub("root-" + theme), Repo.Data));
                var settings = TestWindow.Settings(shell);
                settings.UpdatesApp = "99.0";
                var vm = TestWindow.Model(shell, settings: settings);
                var w = Open(vm);
                SetProp(vm, "IsLoading", false);
                Pump();
                var tabs = Find<TabControl>(w)!;
                var items = tabs.Items.Cast<TabItem>().ToList();
                if (items.Count != 9) throw new Exception($"Пунктов меню {items.Count}, ожидалось 9");
                if (items.Any(t => Nav.GetIcon(t) is null)) throw new Exception("У пункта меню нет значка");
                if (!items.All(t => t.IsVisible)) throw new Exception("Не все пункты меню видны");
                if (vm.NavCollapsed) throw new Exception("По умолчанию меню должно быть раскрыто");

                // «Настройки» и «Информация» — у нижнего края, остальные — сверху.
                var ys = items.Select(t => t.TranslatePoint(new Point(0, 0), tabs).Y).ToList();
                if (!ys.SequenceEqual(ys.Order())) throw new Exception("Пункты меню не по порядку");
                var settingsItem = items[Pages.Settings];
                var bottom = settingsItem.TranslatePoint(new Point(0, 0), tabs).Y;
                var prev = items[Pages.Diag].TranslatePoint(new Point(0, 0), tabs).Y;
                if (bottom - prev < 80) throw new Exception($"«Настройки» не прижаты к низу: {prev} → {bottom}");

                // Точка «есть обновление» — на значке «Информации».
                if (!Nav.GetBadge(items[Pages.About])) throw new Exception("Нет отметки об обновлении у «Информации»");
                if (Nav.GetBadge(items[Pages.Home])) throw new Exception("Отметка об обновлении не у того пункта");
                if (snapDir is not null) { Directory.CreateDirectory(snapDir); Snap(w, Path.Combine(snapDir, $"{theme}-shell-menu.png")); }

                // Сворачивание: только значки, подсказка с названием; состояние — в window.json.
                vm.ToggleNavCmd.Execute(null);
                Pump();
                if (!Nav.GetCollapsed(items[0])) throw new Exception("Пункты не получили свёрнутое состояние");
                var narrow = tabs.Items.Cast<TabItem>().Max(t => t.ActualWidth);
                if (narrow > 60) throw new Exception($"Свёрнутое меню шире значков: {narrow}");
                if (!settings.NavCollapsed || !File.ReadAllText(settings.File).Contains("\"nav_collapsed\": true")) throw new Exception("Свёрнутость меню не записана в window.json");
                if (snapDir is not null) Snap(w, Path.Combine(snapDir, $"{theme}-shell-menu-collapsed.png"));
                Close(w);

                var vm2 = TestWindow.Model(shell, settings: WindowSettings.Load(settings.File));
                if (!vm2.NavCollapsed) throw new Exception("Свёрнутость меню не запомнилась");
                var w2 = Open(vm2);
                Pump();
                if (!Nav.GetCollapsed(Find<TabControl>(w2)!.Items.Cast<TabItem>().First())) throw new Exception("Окно открылось с раскрытым меню");
                Close(w2);
            }
        });
        Assert.Empty(errors);
    }

    static void Top(TabControl tabs) { foreach (var sv in FindAll<ScrollViewer>(tabs, x => x.IsVisible)) sv.ScrollToHome(); Pump(); }

    [Fact]
    public void Net_Tab_HasThreeCollapsedBlocks_LinksOpenTheRightOne_AndSpacingIsUniform()
    {
        var snapDir = Environment.GetEnvironmentVariable("DPISHELL_SNAPSHOTS");
        using var tmp = new TempDir();
        var errors = WithBindingErrors(() =>
        {
            foreach (var theme in new[] { ThemeManager.Light, ThemeManager.Dark })
            {
                ThemeManager.Apply(theme);
                var vm = TestWindow.Model(new Shell(new AppPaths(tmp.Sub("root-" + theme), Repo.Data)));
                vm.Proxy.Show(ProxyOverview.Read(vm.Proxy.Registry, vm.Proxy.WinHttp));
                var w = Open(vm);
                SetProp(vm, "IsLoading", true);
                vm.Page = Pages.Net;
                Pump();
                SetProp(vm, "IsLoading", false);
                Pump();
                var tabs = Find<TabControl>(w)!;
                var blocks = new[] { "NetDnsBlock", "NetProxyBlock", "NetHostsBlock" }.Select(n => (Expander)w.FindName(n)).ToList();
                if (blocks.Any(b => b.IsExpanded)) throw new Exception("Блоки «Сети» должны быть свёрнуты по умолчанию");
                var ys = blocks.Select(b => b.TranslatePoint(new Point(0, 0), tabs).Y).ToList();
                if (!ys.SequenceEqual(ys.Order())) throw new Exception("Порядок блоков: DNS, Прокси, Починить адреса");

                // Строка состояния в заголовке свёрнутого блока — живая.
                var hostsStatus = Find<TextBlock>(blocks[2], t => t.Text.StartsWith("hosts:"))?.Text;
                if (hostsStatus != "hosts: строк DpiShell нет") throw new Exception($"Строка состояния «Починить адреса»: «{hostsStatus}»");
                if (Find<TextBlock>(blocks[1], t => t.Text.StartsWith("выключен"))?.Text is not { } px) throw new Exception("Нет строки состояния «Прокси»");
                if (px.Contains("Мои прокси")) throw new Exception("Пустой список прокси не должен попадать в строку: " + px);

                // Живая строка DNS: данные из адаптеров, подмена — из проверки.
                vm.ShowPcDns(new DnsStatusInfo("google", true, false, true, true,
                    [new DnsAdapterInfo("Ethernet", "ethernet", false, ["8.8.8.8", "8.8.4.4"], true)], []));
                Pump();
                if (Find<TextBlock>(blocks[0], t => t.Text.StartsWith("Google, защищённый · Ethernet")) is null) throw new Exception("Нет строки состояния DNS: Google, защищённый · Ethernet");

                // «Раскрыть все» и обратно.
                var all = Find<Button>(tabs, b => b.Content as string == "Раскрыть все" && b.IsVisible) ?? throw new Exception("Нет «Раскрыть все»");
                all.Command.Execute(null);
                Pump();
                if (blocks.Any(b => !b.IsExpanded)) throw new Exception("«Раскрыть все» раскрыла не всё");
                if (Find<Button>(tabs, b => b.Content as string == "Свернуть все" && b.IsVisible) is null) throw new Exception("После раскрытия нет «Свернуть все»");
                vm.NetToggleAllCmd.Execute(null);
                Pump();
                if (blocks.Any(b => b.IsExpanded)) throw new Exception("«Свернуть все» свернула не всё");

                // Ссылки старых вкладок ведут на «Сеть» с нужным блоком.
                vm.GoProxyCmd.Execute(null);
                Pump();
                if (vm.Page != Pages.Net || !blocks[1].IsExpanded || blocks[0].IsExpanded || blocks[2].IsExpanded) throw new Exception("GoProxyCmd: не тот блок");
                vm.Page = Pages.Home;
                vm.ChooseDnsCmd.Execute(null);
                Pump();
                if (vm.Page != Pages.Net || !blocks[0].IsExpanded) throw new Exception("ChooseDnsCmd: DNS не раскрыт");

                // Единый отступ: между соседними раскрывающимися блоками — один и тот же зазор из общего стиля Expander.
                vm.NetToggleAllCmd.Execute(null);
                Pump();
                foreach (var e in FindAll<Expander>(tabs, x => x.IsVisible))
                    if (e.Margin != new Thickness(0, 6, 0, 0))
                        throw new Exception($"У раскрывающегося блока свой отступ: {e.Margin} ({e.Header})");
                if (snapDir is not null)
                {
                    Directory.CreateDirectory(snapDir);
                    vm.DnsBlockOpen = vm.ProxyBlockOpen = vm.HostsBlockOpen = false;
                    Pump();
                    Top(tabs); Snap(w, Path.Combine(snapDir, $"{theme}-net-collapsed.png"));
                    vm.DnsBlockOpen = true;
                    Pump();
                    Top(tabs); Snap(w, Path.Combine(snapDir, $"{theme}-net-dns.png"));
                    vm.DnsBlockOpen = false; vm.ProxyBlockOpen = true;
                    Pump();
                    Top(tabs); Snap(w, Path.Combine(snapDir, $"{theme}-net-proxy.png"));
                    vm.ProxyBlockOpen = false; vm.HostsBlockOpen = true;
                    Pump();
                    Top(tabs); Snap(w, Path.Combine(snapDir, $"{theme}-net-hosts.png"));
                }
                Close(w);
            }
        });
        Assert.Empty(errors);
    }

    /// <summary>
    /// «Показывать роутер» снята: все вкладки выглядят так же, как при ненайденном роутере (кроме самого пункта «Роутер» в меню — его нет),
    /// и обращений к роутеру нет вовсе.
    /// </summary>
    [Fact]
    public void ShowRouter_Off_LooksLikeRouterNotFound_AndNeverTouchesTheRouter()
    {
        using var tmp = new TempDir();
        var errors = WithBindingErrors(() =>
        {
            ThemeManager.Apply(ThemeManager.Light);
            using var fake = new FakeRouter { WarningsJson = "[]" }.As020();

            Dictionary<int, string> Snapshot(bool showRouter, string root)
            {
                var shell = new Shell(new AppPaths(tmp.Sub(root), Repo.Data));
                var settings = TestWindow.Settings(shell);
                settings.ShowRouter = showRouter;
                var vm = TestWindow.Model(shell, settings: settings);
                vm.Router.AddressAuto = false;
                vm.Router.CustomAddress = showRouter ? "127.0.0.1:1" : fake.Address;   // «не найден» — на адресе, где никого нет
                // Роутер ищется до открытия окна: его события трогают окно, а здесь вызов идёт с другого потока.
                Task.Run(async () => { await vm.Router.RefreshAsync(); await vm.Router.ConnectAsync(null); await vm.Router.RefreshRouterPageAsync(); }).GetAwaiter().GetResult();
                var w = Open(vm);
                SetProp(vm, "IsLoading", false);
                Pump();
                if (vm.Router.Found || vm.Router.Connected) throw new Exception("Роутер найден при " + (showRouter ? "ненайденном" : "снятой галочке"));
                var tabs = Find<TabControl>(w)!;
                var texts = new Dictionary<int, string>();
                foreach (var page in new[] { Pages.Home, Pages.Select, Pages.Sites, Pages.Apps, Pages.Net, Pages.Diag, Pages.Settings, Pages.About })
                {
                    SetProp(vm, "IsLoading", true);
                    vm.GoTo(page);
                    Pump();
                    SetProp(vm, "IsLoading", false);
                    Pump();
                    var content = ((TabItem)tabs.Items[page]).Content as DependencyObject;
                    var seen = new List<string>();
                    foreach (var t in FindAll<TextBlock>(content!, x => x.IsVisible && x.Text.Length > 0)) seen.Add("T:" + t.Text);
                    foreach (var b in FindAll<Button>(content!, x => x.IsVisible && x.Content is string)) seen.Add("B:" + b.Content);
                    foreach (var c in FindAll<CheckBox>(content!, x => x.IsVisible && x.Content is string)) seen.Add("C:" + c.Content);
                    texts[page] = string.Join("\n", seen);
                }
                if (showRouter ? !((TabItem)tabs.Items[Pages.Router]).IsVisible : ((TabItem)tabs.Items[Pages.Router]).IsVisible)
                    throw new Exception("Пункт «Роутер»: " + (showRouter ? "его нет при включённой галочке" : "виден при снятой галочке"));
                Close(w);
                return texts;
            }

            var off = Snapshot(false, "off");
            lock (fake.Calls) { if (fake.Calls.Count != 0) throw new Exception($"Роутеру ушло {fake.Calls.Count} запросов при снятой галочке"); }
            var notFound = Snapshot(true, "nf");
            foreach (var page in off.Keys)
                if (off[page] != notFound[page])
                {
                    var a = off[page].Split('\n'); var b = notFound[page].Split('\n');
                    throw new Exception($"Вкладка {page}: при снятой галочке отличается от «роутер не найден»: «{string.Join("|", a.Except(b).Concat(b.Except(a)))}»");
                }
        });
        Assert.Empty(errors);
    }
}
