using System.Windows;
using System.Windows.Controls;
using DpiShell.App;
using DpiShell.Core;

namespace DpiShell.Tests;

public sealed partial class WindowSmokeTests
{
    /// <summary>
    /// Вкладка «Прокси» в обеих темах: карточки, список со своими прокси, три пути применения, «Вернуть прежний» при запомненном прежнем,
    /// результат проверки; пустой список — подсказка формата (не запись); у находки «Системный прокси» на «Диагностике» ссылка «Настроить →».
    /// Реестр, WinHTTP и настройки — в памяти и во временной папке (настоящие не трогаются). Снимки — DPISHELL_SNAPSHOTS.
    /// </summary>
    [Fact]
    public void Proxy_Page_Renders_InBothThemes_AndDiagLinkLeadsThere()
    {
        var snapDir = Environment.GetEnvironmentVariable("DPISHELL_SNAPSHOTS");
        using var tmp = new TempDir();
        var errors = WithBindingErrors(() =>
        {
            foreach (var theme in new[] { ThemeManager.Light, ThemeManager.Dark })
            {
                ThemeManager.Apply(theme);
                var shell = new Shell(new AppPaths(tmp.Sub("root-" + theme), Repo.Data));
                var vm = TestWindow.Model(shell);
                vm.Proxy.Show(ProxyOverview.Read(vm.Proxy.Registry, vm.Proxy.WinHttp));
                var w = Open(vm);
                SetProp(vm, "IsLoading", true);   // как в остальных снимках: страница выбирается под заставкой, потом заставка снимается
                vm.Page = Pages.Proxy;
                Pump();
                SetProp(vm, "IsLoading", false);
                Pump();
                var tabs = Find<TabControl>(w)!;
                bool Visible(string text) => Find<TextBlock>(tabs, t => t.Text == text && t.IsVisible) is not null;

                // Пустой список: подсказка формата, а не запись; «Вернуть прежний» нет.
                if (!Visible("Сейчас в Windows") || !Visible("Мои прокси")) throw new Exception("Нет карточек «Сейчас в Windows» / «Мои прокси»");
                if (Find<TextBlock>(tabs, t => t.Text.Contains("Clash Verge — 127.0.0.1:7897") && t.IsVisible) is null) throw new Exception("Нет примера формата в пустом списке");
                if (vm.Proxy.Items.Count != 0) throw new Exception("Пример формата попал в список");
                if (Find<Button>(tabs, b => b.Content as string == "Вернуть прежний" && b.IsVisible) is not null) throw new Exception("«Вернуть прежний» без прежнего");
                if (Find<RadioButton>(tabs, r => r.Content as string == "Для всей машины (права администратора)" && r.IsVisible) is null) throw new Exception("Нет пути «Для всей машины»");
                if (Find<InfoButton>(tabs, b => b.Topic == InfoTexts.Proxy && b.IsVisible) is null) throw new Exception("Нет «(i)» у вкладки «Прокси»");
                if (snapDir is not null) { Directory.CreateDirectory(snapDir); Snap(w, Path.Combine(snapDir, $"{theme}-proxy-empty.png")); }
                Close(w);

                // С данными.
                var vm2 = TestWindow.Model(new Shell(new AppPaths(tmp.Sub("root2-" + theme), Repo.Data)));
                FillProxy(vm2);
                vm2.Proxy.Target = ProxyTarget.Machine;
                vm2.Findings.Add(new(false, "Системный прокси", SystemChecks.ProxyFinding((true, "127.0.0.1:7897")).Text, SystemFinding.GoProxy));
                var w2 = Open(vm2);
                SetProp(vm2, "IsLoading", true);
                vm2.Page = Pages.Proxy;
                Pump();
                SetProp(vm2, "IsLoading", false);
                Pump();
                var t2 = Find<TabControl>(w2)!;
                if (Find<TextBlock>(t2, t => t.Text.StartsWith("127.0.0.1:7897") && t.IsVisible) is null) throw new Exception("Не показан действующий прокси пользователя");
                if (Find<ListBox>(t2, l => l.Items.Count == 2 && l.IsVisible) is null) throw new Exception("Список своих прокси не показан");
                if (Find<Button>(t2, b => b.Content as string == "Вернуть прежний" && b.IsVisible) is not null)
                    throw new Exception("Для машины прежнего нет — кнопки быть не должно");
                if (Find<CheckBox>(t2, c => (c.Content as string)?.StartsWith("Ещё и для служб Windows") == true && c.IsVisible) is null) throw new Exception("Для машины нет галочки WinHTTP");
                vm2.Proxy.Target = ProxyTarget.User;
                Pump();
                if (Find<Button>(t2, b => b.Content as string == "Вернуть прежний" && b.IsVisible && b.IsEnabled) is null) throw new Exception("Для пользователя прежний запомнен — нужна доступная «Вернуть прежний»");
                if (Find<CheckBox>(t2, c => (c.Content as string)?.StartsWith("Ещё и для служб Windows") == true && c.IsVisible) is not null) throw new Exception("Галочка WinHTTP — только для машины");
                if (Find<TextBox>(t2, t => t.IsVisible && t.Text.Contains("работает (HTTP)")) is null) throw new Exception("Нет итога проверки");
                if (snapDir is not null)
                {
                    Snap(w2, Path.Combine(snapDir, $"{theme}-proxy-filled.png"));
                    // Нижние карточки («Куда применить», подсказки) — прокруткой вниз.
                    if (Find<ScrollViewer>(t2, s => s.ScrollableHeight > 0 && s.IsVisible) is { } sv)
                    {
                        sv.ScrollToEnd();
                        Pump();
                        Snap(w2, Path.Combine(snapDir, $"{theme}-proxy-bottom.png"));
                        sv.ScrollToTop();
                        Pump();
                    }
                }

                // «Диагностика» → «Настроить →» ведёт на вкладку «Прокси».
                SetProp(vm2, "IsLoading", true);   // под заставкой смена вкладки не запускает проверку служб (sc.exe)
                vm2.Page = Pages.Diag;
                Pump();
                SetProp(vm2, "IsLoading", false);
                Pump();
                var link = Find<Button>(Find<TabControl>(w2)!, b => b.Content as string == "Открыть Прокси →" && b.IsVisible)
                           ?? throw new Exception("У находки «Системный прокси» нет ссылки «Открыть Прокси →»");
                link.Command.Execute(null);
                Pump();
                if (vm2.Page != Pages.Proxy) throw new Exception("«Открыть Прокси →» не привела на вкладку «Прокси»");
                Close(w2);
            }
        });
        Assert.True(errors.Count == 0, "Ошибки привязок:\n" + string.Join("\n", errors));
    }
}
