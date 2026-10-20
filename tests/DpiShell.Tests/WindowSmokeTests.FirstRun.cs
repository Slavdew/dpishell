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
    /// кладёт сайт в простой список исключений ПК (помощник), ответ — строкой под ссылками. Снимки: &lt;тема&gt;-home-working-was, -home-site-fix.
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
                vm.Page = Pages.Home;
                WaitUi(vm.RefreshStatusAsync());
                WaitUi(vm.CheckNowAsync());
                Pump();
                var tabs = Find<TabControl>(w)!;
                bool Visible(Func<TextBlock, bool> m) => VisibleText(tabs, m);
                Button? Link(string content) => Find<Button>(tabs, b => b.IsVisible && b.Content as string == content);

                // Выключено: «Открывается 1 из 2», главная кнопка одна, ссылка про сломавшийся сайт спрятана (чинить пока нечего).
                if (!Visible(t => t.Text == "Открывается 1 из 2")) throw new Exception("До включения нет итога проверки");
                if (Link("Не помогло? → Подобрать") is null) throw new Exception("Нет ссылки «Не помогло? → Подобрать»");
                if (Link("Сайт не открывается?") is not null) throw new Exception("«Сайт не открывается?» видна, пока оптимизация выключена");
                if (Link(L.T("home.find_on_router")) is not null) throw new Exception("«Подобрать на роутере» видна без роутера");
                int primary = FindAll<Button>(tabs, b => b.IsVisible && b.Style == (Style)w.FindResource("Primary")).Count();
                if (primary != 1) throw new Exception($"На Главной главных (синих) кнопок {primary}, ждали 1");

                // «Не помогло? → Подобрать» ведёт на «Подбор».
                Link("Не помогло? → Подобрать")!.Command.Execute(null);
                Pump();
                if (vm.Page != Pages.Select) throw new Exception("«Не помогло? → Подобрать» не открыла «Подбор»");
                vm.Page = Pages.Home;

                // Включили: проверка сама, строка «было 1 из 2».
                on = true;
                WaitUi(vm.EnableAsync());
                Pump();
                if (!Visible(t => t.Text == "Открывается 2 из 2")) throw new Exception("После включения нет «Открывается 2 из 2»");
                if (!Visible(t => t.Text.StartsWith("в ") && t.Text.EndsWith(" · было 1 из 2"))) throw new Exception("Рядом с итогом нет «было 1 из 2»");
                if (snapDir is not null) { Directory.CreateDirectory(snapDir); Snap(w, Path.Combine(snapDir, $"{theme}-home-working-was.png")); }

                // «Сайт не открывается?» → адрес → «Открыть без оптимизации».
                if (Link("Сайт не открывается?") is not { } fix) throw new Exception("Нет «Сайт не открывается?» при работающей оптимизации");
                if (Find<TextBox>(tabs, t => t.IsVisible && System.Windows.Automation.AutomationProperties.GetName(t) == "Адрес сайта без оптимизации") is not null) throw new Exception("Панель адреса раскрыта сама");
                fix.Command.Execute(null);
                Pump();
                var box = Find<TextBox>(tabs, t => t.IsVisible && System.Windows.Automation.AutomationProperties.GetName(t) == "Адрес сайта без оптимизации") ?? throw new Exception("Панель адреса не раскрылась");
                if (Link("Открыть без оптимизации") is null) throw new Exception("Нет кнопки «Открыть без оптимизации»");
                box.Text = "https://Bank.Example/login";
                Pump();
                WaitUi(vm.OpenDirectAsync());
                Pump();
                if (!f.Backend.Calls.Contains("user_exclude_set bank.example")) throw new Exception("Сайт не ушёл в список исключений ПК: " + string.Join(" | ", f.Backend.Calls));
                if (!Visible(t => t.Text == L.T("home.direct.done_pc", new { site = "bank.example" }))) throw new Exception("Нет ответа под ссылками");
                foreach (var t in FindAll<TextBlock>(tabs, t => t.IsVisible && t.Text.Contains("bank.example")))
                    if (t.Text.Contains("обход", StringComparison.OrdinalIgnoreCase)) throw new Exception("В ответе запретное слово: " + t.Text);
                foreach (var t in FindAll<TextBox>(tabs, t => t.IsVisible && t.IsReadOnly && t.Text.Contains("bank.example")))
                    if (t.Text.Contains("обход", StringComparison.OrdinalIgnoreCase)) throw new Exception("В ответе запретное слово: " + t.Text);
                if (snapDir is not null) Snap(w, Path.Combine(snapDir, $"{theme}-home-site-fix.png"));

                // Тот же сайт — в списке на «Сайтах».
                vm.Page = Pages.Sites;
                Pump();
                var list = Find<TextBox>(Find<TabControl>(w)!, t => t.IsVisible && System.Windows.Automation.AutomationProperties.GetName(t) == "Открывать без оптимизации") ?? throw new Exception("На «Сайтах» нет списка «Открывать без оптимизации»");
                if (list.Text.Trim() != "bank.example") throw new Exception("В списке исключений не тот сайт: " + list.Text);
                if (snapDir is not null) Snap(w, Path.Combine(snapDir, $"{theme}-sites-exclude.png"));
                Close(w);
                Stop(vm, f);
            }
        });
        Assert.True(errors.Count == 0, "Ошибки привязок:\n" + string.Join("\n", errors));
    }
}
