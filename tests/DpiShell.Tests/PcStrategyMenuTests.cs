using System.Reflection;
using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.Tests;

/// <summary>
/// UX-аудит 8, этап Г: меню «Стратегия ▾» ПК на главной (выбор — тем же путём, что прежний список) и последний отчёт подбора,
/// который хранится на диске до нового подбора («Итоги подбора»).
/// </summary>
public sealed class PcStrategyMenuTests : IDisposable
{
    const BindingFlags Priv = BindingFlags.Instance | BindingFlags.NonPublic;
    readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    Shell NewShell(string name = "root") => new(new AppPaths(_tmp.Sub(name), Repo.Data));

    static MainViewModel Model(Shell shell, HelperLink? link = null)
    {
        var vm = TestWindow.Model(shell, link);
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsLoading))!.SetValue(vm, false);
        vm.ToastTime = TimeSpan.FromMinutes(5);
        vm.Confirm = _ => true;
        return vm;
    }

    static async Task WaitFor(Func<bool> cond)
    {
        for (int i = 0; i < 3000 && !cond(); i++) await Task.Delay(10);
        Assert.True(cond(), "не дождались");
    }

    static MenuEntry Entry(MainViewModel vm, string id) =>
        vm.PcStrategyMenu().Single(m => !m.IsLine && m.Header.TrimStart('✓', ' ') == vm.StrategyChoices.Single(c => c.Id == id).Title);

    [Fact]
    public async Task Menu_Pick_GoesThroughChosenStrategy_ConfirmForUnchecked_CurrentMarked()
    {
        await using var f = new HelperFixture();
        // Оптимизация выключена: выбор запоминается у помощника («choose»), без перезапуска.
        f.Backend.StatusValue = FakeHelperBackend.DefaultStatus with
        {
            Enabled = false, Active = false, Service = SvcState.Stopped,
            Strategies = [new StrategyInfo("alt9", "Alt 9", "catalog"), new StrategyInfo("alt10", "Alt 10", "catalog")],
        };
        var shell = NewShell();
        var vm = Model(shell, await f.LinkAsync());
        await vm.ReloadChoicesAsync();

        // Готовая — «choose» у помощника, как выбор в прежнем списке.
        var ready = vm.StrategyChoices.First(c => c.Id != vm.CurrentStrategyId && !c.Personal).Id;
        Entry(vm, ready).Run!();
        await WaitFor(() => f.Backend.Calls.Contains("choose " + ready));
        await WaitFor(() => vm.CurrentStrategyId == ready);
        Assert.StartsWith("✓ ", Entry(vm, ready).Header);
        Assert.Single(vm.PcStrategyMenu(), m => m.Header.StartsWith("✓ "));

        // Своя непроверенная — сначала вопрос; отказ не выбирает (тот же ConfirmUnchecked, что у списка).
        vm.ReadText = () => StrategyExchange.Encode("Чужая", shell.Catalog.Strategies[0].Roles);
        await vm.PasteStrategyAsync();
        var id = vm.PendingCheckId!;
        string? asked = null;
        vm.Confirm = q => { asked = q; return false; };
        Entry(vm, id).Run!();
        Assert.Contains("не проверена", asked);
        Assert.DoesNotContain("choose " + id, f.Backend.Calls);
        vm.Confirm = _ => true;
        Entry(vm, id).Run!();
        await WaitFor(() => f.Backend.Calls.Contains("choose " + id));
    }

    [Fact]
    public void Menu_Tail_CopyParams_Report_AllStrategies()
    {
        var shell = NewShell();
        var vm = Model(shell);
        vm.ReloadChoices();
        var menu = vm.PcStrategyMenu();
        int line = menu.ToList().FindIndex(m => m.IsLine);
        Assert.Equal(vm.StrategyChoices.Count, line);
        // Параметры — в подсказке пункта, справа — итог прогона.
        var first = menu[0];
        Assert.Equal(vm.StrategyChoices[0].Stats, first.Gesture);
        Assert.Contains("Параметры по ролям:", first.Tip);
        // Подбора не было — «Итоги подбора…» серая с причиной, «Все стратегии…» ведёт на «Подбор».
        var report = menu.Single(m => m.Header == L.T("home.menu.report"));
        Assert.False(report.Enabled);
        Assert.Equal(L.T("home.report.none"), report.Tip);
        menu.Single(m => m.Header == L.T("home.menu.all")).Run!();
        Assert.Equal(Pages.Select, vm.Page);
    }

    [Fact]
    public void Menu_WhileSelecting_StrategiesGrey_TipSaysWhy()
    {
        var vm = Model(NewShell());
        vm.ReloadChoices();
        Assert.Equal(L.T("home.strategy.pc_tip"), vm.PcStrategyTip);
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.Selecting))!.SetValue(vm, true);
        Assert.All(vm.PcStrategyMenu().TakeWhile(m => !m.IsLine), m => Assert.False(m.Enabled));
        Assert.Equal(L.T("home.choose_blocked.select"), vm.PcStrategyTip);
    }

    static SelectionReport Report(Shell shell, out string bestId)
    {
        var yt = new TargetGroup("youtube", "YouTube", [new("https://www.youtube.com/", 20480)]);
        var dc = new TargetGroup("discord", "Discord", [new("https://discord.com/", 20480)]);
        TargetGroup[] groups = [yt, dc];
        var s = shell.Catalog.Strategies;
        StrategyScore Score(Strategy st, bool discord) =>
            Selector.Score(st, groups, [new TargetVerdict("youtube", yt.Targets[0], true, 0.3, []), new TargetVerdict("discord", dc.Targets[0], discord, 0.4, [])]);
        bestId = s[1].Id;
        return new SelectionReport([], groups, [Score(s[0], false), Score(s[1], true), Score(s[2], false)], false);
    }

    static void Show(MainViewModel vm, SelectionReport r) =>
        typeof(MainViewModel).GetMethod("ShowReport", Priv)!.Invoke(vm, [r]);

    [Fact]
    public void LastReport_SavedOnSelection_RestoredAfterRestart_WithoutAutoApply()
    {
        var shell = NewShell();
        var vm = Model(shell);
        Assert.False(vm.HasLastReport);
        Assert.False(vm.ShowLastReportCmd.CanExecute(null));
        Assert.Equal(L.T("home.report.none"), vm.LastReportTip);

        Show(vm, Report(shell, out var bestId));
        Assert.True(File.Exists(vm.LastSelectFile));
        Assert.True(vm.HasLastReport);
        Assert.StartsWith("Последний подбор: ", vm.LastReportTip);
        var saved = MainViewModel.ReadLastReport(vm.LastSelectFile)!.Value;
        Assert.Equal(3, saved.Report.Scores.Count);
        Assert.Equal(bestId, saved.Report.Best!.Strategy.Id);

        // «Перезапуск»: новое окно над тем же window.json — таблица и итог на месте, автоприменения нет.
        var again = Model(shell);
        Assert.Equal(3, again.Scores.Count);
        Assert.True(again.HasLastReport);
        Assert.Equal(bestId, again.SelectedScore?.Id);
        Assert.True(again.Scores.Single(r => r.Id == bestId).IsBest);
        Assert.StartsWith("Последний подбор: ", again.ResultSummary);
        Assert.Contains(saved.Report.Best!.Strategy.Title, again.ResultSummary);
        Assert.True(again.ResultCompact);
        Assert.Null(typeof(MainViewModel).GetField("_autoApplyId", Priv)!.GetValue(again));

        // «Итоги подбора»: «Подбор» с раскрытой карточкой итога.
        again.ShowLastReportCmd.Execute(null);
        Assert.Equal(Pages.Select, again.Page);
        Assert.True(again.ResultFull);
    }

    [Fact]
    public void LastReport_NewSelectionStarts_ForgottenOnDisk()
    {
        var shell = NewShell();
        var vm = Model(shell);
        Show(vm, Report(shell, out _));
        Assert.True(File.Exists(vm.LastSelectFile));
        typeof(MainViewModel).GetMethod("ResetResults", Priv)!.Invoke(vm, []);
        Assert.False(File.Exists(vm.LastSelectFile));
        Assert.False(vm.HasLastReport);
        Assert.Empty(Model(shell).Scores);
    }

    [Theory]
    [InlineData("other_version")]
    [InlineData("broken")]
    [InlineData("no_report")]
    [InlineData("empty_scores")]
    public void LastReport_OldOrBroken_SilentlyNotShown(string kind)
    {
        var shell = NewShell();
        var vm = Model(shell);
        Show(vm, Report(shell, out _));
        var file = vm.LastSelectFile;
        var text = File.ReadAllText(file);
        File.WriteAllText(file, kind switch
        {
            "other_version" => text.Replace("\"version\": 1", "\"version\": 99").Replace("\"version\":1", "\"version\":99"),
            "broken" => text[..(text.Length / 2)],
            "no_report" => "{\"version\":1,\"at\":\"2026-10-27T14:05:00\"}",
            _ => "{\"version\":1,\"at\":\"2026-10-27T14:05:00\",\"report\":{\"baseline\":[],\"testedGroups\":[],\"scores\":[],\"cancelled\":false}}",
        });
        if (kind == "other_version") Assert.Contains("99", File.ReadAllText(file));

        Assert.Null(MainViewModel.ReadLastReport(file));
        var again = Model(shell);
        Assert.Empty(again.Scores);
        Assert.False(again.HasLastReport);
        Assert.Equal("", again.ResultSummary);
    }

    [Fact]
    public void ScoreMenu_Found_SendAndDelete_Catalog_OnlyParams()
    {
        var shell = NewShell();
        var vm = Model(shell);
        vm.ReloadChoices();
        var catalog = shell.Catalog.Strategies[0];
        var row = new ScoreRow(new StrategyScore(catalog, [], 0, 0), vm.Names);
        vm.Scores.Add(row);
        vm.SelectedScore = row;
        var menu = vm.ScoreMenu();
        Assert.Contains(menu, m => m.Header == L.T("home.params.copy"));
        Assert.DoesNotContain(menu, m => m.Header == L.T("home.delete"));

        var found = new ScoreRow(new StrategyScore(new Strategy("found-x", "Найденная", "search", new() { ["general"] = "--dpi-desync=fake" }), [], 0, 0), vm.Names);
        vm.Scores.Add(found);
        vm.SelectedScore = found;
        menu = vm.ScoreMenu();
        Assert.Contains(menu, m => m.Header == L.T("home.delete") && m.Enabled);
        // Роутер не подключён — «Отправить на роутер» не предлагается.
        Assert.DoesNotContain(menu, m => m.Header == L.T("home.send_to_router"));
    }
}
