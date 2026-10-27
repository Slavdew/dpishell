using System.Windows;
using System.Windows.Controls;
using DpiShell.App;
using DpiShell.Core;

namespace DpiShell.Tests;

public sealed partial class WindowSmokeTests
{
    /// <summary>
    /// UX-аудит 8, этап Г, в окне: стратегия ПК — кнопка «▾» (как у роутера), пункт меню меняет стратегию на кнопке;
    /// «Итоги подбора» на главной видна и без отчёта — серая, с подсказкой почему; после подбора — активна и ведёт на «Подбор» с раскрытым итогом.
    /// Меню строится тем же кодом, что по щелчку (<see cref="MainWindow.BuildMenu"/>), но не открывается: всплывающих окон под тестами нет.
    /// </summary>
    [Fact]
    public void Home_PcStrategyMenu_And_ReportButton()
    {
        using var tmp = new TempDir();
        var errors = WithBindingErrors(() =>
        {
            var shell = new Shell(new AppPaths(tmp.Sub("root"), Repo.Data));
            var vm = TestWindow.Model(shell);
            vm.Confirm = _ => true;
            var w = Open(vm);
            SetProp(vm, "IsLoading", false);
            vm.ReloadChoices();
            vm.GoTo(Pages.Home);
            Pump();
            var tabs = Find<TabControl>(w)!;

            var report = Find<Button>(tabs, b => b.Content as string == L.T("home.report")) ?? throw new Exception("Нет «Итоги подбора» на главной");
            if (!report.IsVisible) throw new Exception("«Итоги подбора» без отчёта скрыта, а должна быть серой");
            if (report.IsEnabled) throw new Exception("«Итоги подбора» активна без отчёта");
            if (report.ToolTip as string != L.T("home.report.none") || !ToolTipService.GetShowOnDisabled(report)) throw new Exception("У серой «Итогов подбора» нет подсказки почему");

            // Стратегия ПК: кнопка «▾»; пункт меню (щелчок) — стратегия на кнопке сменилась.
            var pick = Find<Button>(tabs, b => b.Name == "PcStrategyButton" && b.IsVisible) ?? throw new Exception("Нет кнопки «Стратегия ▾» у ПК");
            if (!pick.IsEnabled || pick.ToolTip as string != L.T("home.strategy.pc_tip")) throw new Exception("Кнопка стратегии ПК серая или без подсказки");
            var target = vm.StrategyChoices.First(c => c.Id != vm.CurrentStrategyId);
            var menu = MainWindow.BuildMenu(pick, vm.PcStrategyMenu());
            var item = menu.Items.OfType<MenuItem>().First(i => (i.Header as string)?.Trim() == target.Title);
            if (item.InputGestureText != target.Stats) throw new Exception("Справа у пункта не итог прогона: " + item.InputGestureText);
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Pump();
            if (vm.CurrentStrategyId != target.Id) throw new Exception("Пункт меню не сменил стратегию ПК");
            if (Find<TextBlock>(pick, t => t.Text == target.Title) is null) throw new Exception("На кнопке стратегии ПК прежнее название");
            if (!menu.Items.OfType<Separator>().Any()) throw new Exception("В меню нет черты перед «Итоги подбора…»");
            var menuReport = menu.Items.OfType<MenuItem>().First(i => i.Header as string == L.T("home.menu.report"));
            if (menuReport.IsEnabled) throw new Exception("«Итоги подбора…» в меню активна без отчёта");

            // Идёт подбор — кнопка серая, в подсказке почему.
            SetProp(vm, "Selecting", true);
            Pump();
            if (pick.IsEnabled || pick.ToolTip as string != L.T("home.choose_blocked.select")) throw new Exception("Во время подбора кнопка стратегии ПК не серая или без причины");
            SetProp(vm, "Selecting", false);

            // Подбор прошёл — кнопка активна, ведёт на «Подбор» с раскрытой карточкой итога.
            var yt = new TargetGroup("youtube", "YouTube", [new("https://www.youtube.com/", 20480)]);
            var score = Selector.Score(shell.Catalog.Strategies[1], [yt], [new TargetVerdict("youtube", yt.Targets[0], true, 0.3, [])]);
            typeof(MainViewModel).GetMethod("ShowReport", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(vm, [new SelectionReport([], [yt], [score], false)]);
            Pump();
            if (!report.IsEnabled || !(report.ToolTip as string ?? "").StartsWith("Последний подбор: ")) throw new Exception("После подбора «Итоги подбора» серая или без времени");
            report.Command.Execute(null);
            Pump();
            if (vm.Page != Pages.Select || !vm.ResultFull) throw new Exception("«Итоги подбора» не открыла «Подбор» с итогом");
            if (Find<Button>(Find<TabControl>(w)!, b => b.Name == "ScoreMenuButton" && b.IsVisible) is null) throw new Exception("Нет «⋯» у выбранной строки таблицы");
            Close(w);
        });
        Assert.True(errors.Count == 0, "Ошибки привязок:\n" + string.Join("\n", errors));
    }
}
