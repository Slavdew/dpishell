using System.Windows;
using System.Windows.Controls;
using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.Tests;

public sealed partial class WindowSmokeTests
{
    /// <summary>
    /// Карточка «Почему не получилось» на Главной в обеих темах: нет интернета, вход в сеть, сбитое время, отказ в правах, находка «Диагностики»,
    /// неизвестная причина (отчёт и обращение). Каждая — через настоящий путь действия («Подобрать»/«Включить»), сеть и находки подставные.
    /// Снимки: &lt;тема&gt;-home-guided-nointernet, -portal, -clock, -uac, -finding, -unknown.
    /// </summary>
    [Fact]
    public void Home_Guided_Card_AllCauses_Binds_AndSnap()
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
                f.Backend.StatusValue = f.Backend.StatusValue with { Enabled = false, Active = false, Service = SvcState.Stopped };
                var link = Task.Run(f.LinkAsync).GetAwaiter().GetResult();
                var vm = TestWindow.Model(shell, link);
                var verdict = new NetVerdict(NetCause.Ok);
                var opened = new List<string>();
                vm.GuidedEnabled = true;
                vm.AskHelper = _ => true;
                vm.NetDiagnose = _ => Task.FromResult(verdict);
                vm.SystemFindings = () => [];
                vm.LinkOpener = opened.Add;
                var w = Open(vm);
                w.Height = 760;
                SetProp(vm, "IsLoading", false);
                vm.GoTo(Pages.Home);
                WaitUi(vm.RefreshStatusAsync());
                Pump();
                var tabs = Find<TabControl>(w)!;
                Button? Btn(string content) => Find<Button>(tabs, b => b.IsVisible && b.Content as string == content);
                bool Visible(string text) => VisibleText(tabs, t => t.Text == text);
                string Shot(string name) => Path.Combine(snapDir!, $"{theme}-home-guided-{name}.png");
                void Card(string name, string text, params string[] buttons)
                {
                    Pump();
                    if (!Visible(L.T("home.guided.title"))) throw new Exception($"{name}: нет заголовка карточки");
                    if (!Visible(text)) throw new Exception($"{name}: нет текста «{text}»");
                    foreach (var b in buttons) if (Btn(b) is null) throw new Exception($"{name}: нет кнопки «{b}»");
                    int primary = FindAll<Button>(tabs, b => b.IsVisible && b.Style == (Style)w.FindResource("Primary")).Count();
                    if (primary != 1) throw new Exception($"{name}: на Главной главных кнопок {primary}, ждали 1");
                    if (snapDir is not null) { Directory.CreateDirectory(snapDir); Snap(w, Shot(name)); }
                }

                // 1) Нет интернета: подбор не начат, кнопка «Повторить».
                verdict = new NetVerdict(NetCause.NoInternet);
                WaitUi(vm.StartSelectAsync());
                Card("nointernet", "Нет доступа в интернет (или сеть требует входа).", "Повторить");
                if (f.Backend.Calls.Any(c => c.StartsWith("select"))) throw new Exception("Подбор начался без интернета");

                // 2) Вход в сеть: «Открыть страницу входа».
                verdict = new NetVerdict(NetCause.CaptivePortal, PortalUrl: "http://login.example/auth");
                WaitUi(vm.StartSelectAsync());
                Card("portal", L.T("home.guided.portal"), "Открыть страницу входа");
                WaitUi(vm.GuidedActionAsync());
                if (opened.Count != 1) throw new Exception("Страница входа не открыта");

                // 3) Сбилось время.
                verdict = new NetVerdict(NetCause.ClockSkew, TimeSpan.FromMinutes(3 * 60 + 12));
                WaitUi(vm.StartSelectAsync());
                Card("clock", "Время на компьютере отличается на 3 ч 12 мин — проверки сайтов не проходят.", "Синхронизировать время");

                // 4) Находка «Диагностики»: системный прокси — одна кнопка к нужному месту.
                verdict = new NetVerdict(NetCause.Ok);
                f.Backend.EnableThrows = new InvalidOperationException("сбой запуска");
                vm.SystemFindings = () => [SystemChecks.ProxyFinding((true, "proxy.local:8080"))];
                WaitUi(vm.EnableAsync());
                if (!vm.GuidedText.Contains("proxy.local:8080")) throw new Exception("В находке нет адреса прокси: " + vm.GuidedText);
                Card("finding", vm.GuidedText, "Настроить прокси");

                // 5) Причина неизвестна: «Сохранить отчёт» и «Написать о проблеме», кнопки исправления нет.
                vm.SystemFindings = () => [];
                WaitUi(vm.EnableAsync());
                if (!vm.GuidedText.StartsWith("Причина не определена.") || !vm.GuidedText.Contains("сбой запуска")) throw new Exception("Текст неизвестной причины: " + vm.GuidedText);
                Card("unknown", vm.GuidedText, "Сохранить отчёт", "Написать о проблеме");
                if (Btn("Повторить") is not null || Btn("Настроить прокси") is not null) throw new Exception("При неизвестной причине есть кнопка исправления");

                Close(w);
                Stop(vm, f);

                // 6) Отказ в правах: окно без помощника, «Повторить».
                var vm2 = TestWindow.Model(shell);
                vm2.GuidedEnabled = true;
                vm2.AskHelper = _ => true;
                vm2.NetDiagnose = _ => Task.FromResult(new NetVerdict(NetCause.Ok));
                w = Open(vm2);
                w.Height = 760;
                SetProp(vm2, "IsLoading", false);
                vm2.GoTo(Pages.Home);
                tabs = Find<TabControl>(w)!;
                WaitUi(vm2.EnableAsync());
                vm2.GoTo(Pages.Home);
                Card("uac", "Без помощника включить нельзя: он работает от имени системы, окно — без прав.", "Повторить");
                Close(w);
            }
        });
        Assert.True(errors.Count == 0, "Ошибки привязок:\n" + string.Join("\n", errors));
    }
}
