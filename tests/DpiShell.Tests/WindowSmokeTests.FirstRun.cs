using System.Windows;
using System.Windows.Controls;
using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.Tests;

public sealed partial class WindowSmokeTests
{
    /// <summary>
    /// Главная первого запуска (UX-аудит 7, п. 1–3, 5) в обеих темах: на карточке одна главная кнопка, «Не помогло? → Подобрать» всегда, «Подобрать на роутере» — только с роутером,
    /// «Сайт не открывается?» — пока оптимизация работает; после «Включить» в строке «Открывается 2 из 2 · было 1 из 2»; панель «адрес → Открыть без оптимизации»
    /// кладёт сайт в простой список исключений ПК (помощник), ответ — строкой под ссылками; сайт вне списков — «Добавить в список оптимизации».
    /// Снимки: &lt;тема&gt;-home-working-was, -home-sitecheck-not-in-list, -home-site-fix.
    /// </summary>
    [Fact]
    public void Home_FirstRun_SiteFix_AndBefore_Binds_AndSnap()
    {
        var snapDir = Environment.GetEnvironmentVariable("DPISHELL_SNAPSHOTS");
        using var tmp = new TempDir();
        var errors = WithBindingErrors(() =>
        {
            foreach (var theme in new[] { ThemeManager.Light, ThemeManager.Dark })
            {
                ThemeManager.Apply(theme);
                var shell = new Shell(new AppPaths(tmp.Sub("root-" + theme), Repo.Data));
                var f = new HelperFixture();
                f.Backend.TrackEnabled = true;
                f.Backend.StatusValue = f.Backend.StatusValue with { Enabled = false, Active = false, StrategyId = "alt9", Service = SvcState.Stopped };
                var link = Task.Run(f.LinkAsync).GetAwaiter().GetResult();
                var vm = TestWindow.Model(shell, link);
                bool on = false;
                vm.ProbeGroups = groups => Task.FromResult<IReadOnlyList<TargetVerdict>>(groups.SelectMany(g => g.Targets.Select(t =>
                    new TargetVerdict(g.Id, t, g.Id == "youtube" || on, 0.3, []))).ToList());
                var w = Open(vm);
                w.Height = 700;
                SetProp(vm, "IsLoading", false);
                foreach (var g in vm.Groups) g.IsChecked = g.Id is "youtube" or "discord";
                vm.GoTo(Pages.Home);
                WaitUi(vm.RefreshStatusAsync());
                WaitUi(vm.CheckNowAsync());
                Pump();
                var tabs = Find<TabControl>(w)!;
                bool Visible(Func<TextBlock, bool> m) => VisibleText(tabs, m);
                Button? Link(string content) => Find<Button>(tabs, b => b.IsVisible && b.Content as string == content);

                // Выключено: «Открывается 1 из 2», главная кнопка одна, ссылка про сломавшийся сайт спрятана (чинить пока нечего).
                if (!Visible(t => t.Text.StartsWith("Открывается 1 из 2 · в "))) throw new Exception("До включения нет итога проверки");
                if (Link("Подобрать") is null) throw new Exception("Нет ссылки «Подобрать»");
                if (Link("Сайт не открывается?") is not null) throw new Exception("«Сайт не открывается?» видна, пока оптимизация выключена");
                if (Link(L.T("home.find_on_router")) is not null) throw new Exception("«Подобрать на роутере» видна без роутера");
                int primary = FindAll<Button>(tabs, b => b.IsVisible && b.Style == (Style)w.FindResource("Primary")).Count();
                if (primary != 1) throw new Exception($"На Главной главных (синих) кнопок {primary}, ждали 1");

                // «Подобрать» ведёт на «Подбор».
                Link("Подобрать")!.Command.Execute(null);
                Pump();
                if (vm.Page != Pages.Select) throw new Exception("«Подобрать» не открыла «Подбор»");
                vm.GoTo(Pages.Home);

                // Включили: проверка сама, строка «было 1 из 2».
                on = true;
                WaitUi(vm.EnableAsync());
                Pump();
                if (!Visible(t => t.Text.StartsWith("Открывается 2 из 2 · в ") && t.Text.EndsWith(" · было 1 из 2"))) throw new Exception("После включения нет «Открывается 2 из 2 · в … · было 1 из 2»");
                if (Link("Не помогло? → Подобрать") is null) throw new Exception("Включено, а ссылки «Не помогло? → Подобрать» нет");
                if (snapDir is not null) { Directory.CreateDirectory(snapDir); Snap(w, Path.Combine(snapDir, $"{theme}-home-working-was.png")); }

                // «Сайт не открывается?» → «Проверить сайт: с оптимизацией и без неё» → итог строкой → «Добавить в исключения» (п. 15, решение владельца).
                if (Link("Сайт не открывается?") is not { } fix) throw new Exception("Нет «Сайт не открывается?» при работающей оптимизации");
                string aria = L.T("home.direct.site.aria");
                if (Find<TextBox>(tabs, t => t.IsVisible && System.Windows.Automation.AutomationProperties.GetName(t) == aria) is not null) throw new Exception("Блок «Проверить сайт» раскрыт сам");
                fix.Command.Execute(null);
                Pump();
                var box = Find<TextBox>(tabs, t => t.IsVisible && System.Windows.Automation.AutomationProperties.GetName(t) == aria) ?? throw new Exception("Блок «Проверить сайт» не раскрылся");
                if (!Visible(t => t.Text == L.T("home.sitecheck.title")) || Link(L.T("home.sitecheck.button")) is null) throw new Exception("Нет подписи или кнопки «Проверить»");
                if (Link("Открыть без оптимизации") is not null) throw new Exception("Осталась кнопка «Открыть без оптимизации»");
                box.Text = "https://Bank.Example/login";
                Pump();
                on = false;   // с оптимизацией сайт не открывается
                // «Только из списков», сайта в них нет — «не в списке оптимизации» и «Добавить в список оптимизации», исключений не предлагают.
                WaitUi(vm.SiteCheckAsync());
                Pump();
                if (!Visible(t => t.Text == L.T("home.sitecheck.not_in_list", new { site = "bank.example" }))) throw new Exception("Нет итога «не в списке оптимизации»");
                if (Find<Button>(tabs, b => b.IsVisible && b.Content as string == L.T("home.sitecheck.add_list")) is null) throw new Exception("Нет «Добавить в список оптимизации»");
                if (Find<Button>(tabs, b => b.IsVisible && b.Content as string == L.T("home.sitecheck.add")) is not null) throw new Exception("«Добавить в исключения» при сайте вне списков");
                if (snapDir is not null) Snap(w, Path.Combine(snapDir, $"{theme}-home-sitecheck-not-in-list.png"));
                // «Для всех сайтов» — сайт под оптимизацией: дальше как раньше, «Добавить в исключения».
                f.Backend.AdvancedValue = new(true, AdvancedSites.OthersAll, []);
                WaitUi(vm.SiteCheckAsync());
                Pump();
                if (!Visible(t => t.Text == L.T("home.sitecheck.fails_with"))) throw new Exception("Нет итога «С оптимизацией не открывается»");
                if (Find<Button>(tabs, b => b.IsVisible && b.Content as string == L.T("home.sitecheck.add")) is null) throw new Exception("Нет «Добавить в исключения»");
                if (snapDir is not null) Snap(w, Path.Combine(snapDir, $"{theme}-home-sitecheck.png"));
                on = true;    // без оптимизации — открывается
                WaitUi(vm.AddCheckedToExcludeAsync());
                Pump();
                if (!f.Backend.Calls.Contains("user_exclude_set bank.example")) throw new Exception("Сайт не ушёл в список исключений ПК: " + string.Join(" | ", f.Backend.Calls));
                if (!Visible(t => t.Text == L.T("home.direct.done_pc", new { site = "bank.example" }))) throw new Exception("Нет ответа под ссылками");
                foreach (var t in FindAll<TextBlock>(tabs, t => t.IsVisible && t.Text.Contains("bank.example")))
                    if (t.Text.Contains("обход", StringComparison.OrdinalIgnoreCase)) throw new Exception("В ответе запретное слово: " + t.Text);
                foreach (var t in FindAll<TextBox>(tabs, t => t.IsVisible && t.IsReadOnly && t.Text.Contains("bank.example")))
                    if (t.Text.Contains("обход", StringComparison.OrdinalIgnoreCase)) throw new Exception("В ответе запретное слово: " + t.Text);
                if (snapDir is not null) Snap(w, Path.Combine(snapDir, $"{theme}-home-site-fix.png"));

                // Тот же сайт — в списке на «Сайтах».
                vm.GoTo(Pages.Sites);
                Pump();
                var list = Find<TextBox>(Find<TabControl>(w)!, t => t.IsVisible && System.Windows.Automation.AutomationProperties.GetName(t) == "Исключения") ?? throw new Exception("На «Сайтах» нет списка «Открывать без оптимизации»");
                if (list.Text.Trim() != "bank.example") throw new Exception("В списке исключений не тот сайт: " + list.Text);
                if (snapDir is not null) Snap(w, Path.Combine(snapDir, $"{theme}-sites-exclude.png"));
                Close(w);
                Stop(vm, f);
            }
        });
        Assert.True(errors.Count == 0, "Ошибки привязок:\n" + string.Join("\n", errors));
    }
}
