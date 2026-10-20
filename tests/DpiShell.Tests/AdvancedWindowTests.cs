using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Helper;
using DpiShell.Core.Router;

namespace DpiShell.Tests;

/// <summary>Продвинутый режим в окне: карточка на «Сайтах» для ПК (помощник, протокол 1.8) и для роутера (advanced_get/advanced_set, confirm_s).</summary>
public sealed class AdvancedWindowTests : IDisposable
{
    readonly TempDir _tmp = new();
    readonly List<string> _said = [];
    public void Dispose() => _tmp.Dispose();

    static AdvancedRowVm AddRow(AdvancedViewModel a, string domains, string strategy)
    {
        a.AddRowCmd.Execute(null);
        var r = a.Rows[^1];
        r.DomainsText = domains;
        r.Strategy = strategy;
        return r;
    }

    // ---------- ПК ----------

    async Task<(MainViewModel Vm, HelperFixture F)> PcAsync(bool running = false, Func<HelperStatus, HelperStatus>? tune = null)
    {
        var f = new HelperFixture();
        f.Backend.StatusValue = f.Backend.StatusValue with { Enabled = running, Active = running, StrategyId = "alt9", Service = running ? SvcState.Running : SvcState.Stopped };
        if (tune is not null) f.Backend.StatusValue = tune(f.Backend.StatusValue);
        var vm = TestWindow.Model(new Shell(new AppPaths(_tmp.Sub("root"), Repo.Data)), await f.LinkAsync());
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsLoading))!.SetValue(vm, false);
        vm.Confirm = _ => true;
        await vm.RefreshStatusAsync();
        await vm.AdvancedPc.RefreshAsync(force: true);
        return (vm, f);
    }

    [Fact]
    public async Task Pc_DefaultsOff_ToggleAppliesImmediately_RowsAreNormalizedAndSent()
    {
        var (vm, f) = await PcAsync();
        await using var _ = f;
        var a = vm.AdvancedPc;
        Assert.False(a.Enabled);
        Assert.False(a.ShowEditor);
        Assert.Equal(L.T("sites.adv.off"), a.ToggleText);
        Assert.Equal(L.T("sites.adv.summary.off"), a.Summary);

        a.Enabled = true;
        await WaitAsync(() => !a.Busy && a.Enabled && f.Backend.Calls.Any(c => c.StartsWith("advanced_set")));
        Assert.True(a.ShowEditor);
        Assert.Contains("advanced_set True lists 0", f.Backend.Calls);

        AddRow(a, "HTTPS://Example.com/x, example.org\nexample.com", "auto");
        AddRow(a, "bank.example", "direct");
        AddRow(a, "named.example", "alt9");
        AddRow(a, "   ", "auto");   // пустая строка не отправляется
        Assert.True(a.Dirty);
        await a.ApplyAsync();

        Assert.False(a.Dirty);
        Assert.Equal(["example.com", "example.org"], f.Backend.LastAdvancedRows[0].Domains);
        Assert.Equal(["auto", "direct", "alt9"], f.Backend.LastAdvancedRows.Select(r => r.Strategy));
        Assert.Equal(3, f.Backend.LastAdvancedRows.Count);
        Assert.Contains("3", a.Summary);
        Assert.Equal(L.T("sites.adv.on"), a.ToggleText);
        Assert.Equal(L.T("sites.adv.summary.lists", new { n = 3 }), a.Summary);
        Assert.Equal(3, a.Rows.Count);
        Assert.StartsWith("Авто — сейчас", a.Rows[0].Hint);
        Assert.Contains("не трогает", a.Rows[1].Hint);
    }

    [Fact]
    public async Task Pc_InvalidRow_NotSent_ToggleStaysOff_ErrorIsShown()
    {
        var (vm, f) = await PcAsync();
        await using var _ = f;
        var a = vm.AdvancedPc;
        AddRow(a, "good.example\nbad domain.example", "auto");
        a.Enabled = true;
        await WaitAsync(() => a.HasMessage && !a.Busy);

        Assert.False(a.Enabled);
        Assert.True(a.MessageIsError);
        Assert.Equal(L.T("sites.adv.err.row_not_domain", new { row = 1 }), a.Message);
        Assert.DoesNotContain(f.Backend.Calls, c => c.StartsWith("advanced_set"));
    }

    [Fact]
    public async Task Pc_HelperRefuses_ToggleReturns_MessageFromHelper()
    {
        var (vm, f) = await PcAsync();
        await using var _ = f;
        f.Backend.AdvancedFails = "Строка 1: такой стратегии нет.";
        var a = vm.AdvancedPc;
        a.Enabled = true;
        await WaitAsync(() => a.MessageIsError && !a.Busy);   // «Включаю…» — не итог: ждём отказ
        Assert.False(a.Enabled);
        Assert.Contains("такой стратегии нет", a.Message);
    }

    [Fact]
    public async Task Pc_Off_KeepsRowsStored_AndShowsThem()
    {
        var (vm, f) = await PcAsync();
        await using var _ = f;
        f.Backend.AdvancedValue = new(false, "direct", [new(["a.example"], "auto", null), new(["b.example"], "direct", null)]);
        await vm.AdvancedPc.RefreshAsync(force: true);
        var a = vm.AdvancedPc;
        Assert.False(a.Enabled);
        Assert.True(a.ShowStored);
        Assert.Contains("2", a.StoredText);
        Assert.True(a.OthersDirect);
        Assert.Equal(2, a.Rows.Count);
    }

    [Fact]
    public async Task Pc_All_RefusedWhileOptimizationIsOff_AndOnlyAfterControlCheck()
    {
        var (vm, f) = await PcAsync();
        await using var _ = f;
        var a = vm.AdvancedPc;
        a.Enabled = true;
        await WaitAsync(() => !a.Busy && a.Enabled);
        f.Backend.Calls.Clear();
        a.OthersAll = true;
        await a.ApplyAsync();
        Assert.Equal(L.T("sites.adv.err.all_needs_running"), a.Message);
        Assert.DoesNotContain(f.Backend.Calls, c => c.StartsWith("advanced_set"));
        await f.DisposeAsync();

        // Оптимизация работает; обычные сайты открывались до и перестали после — «Для всех» возвращается в «Как сейчас».
        var (vm2, f2) = await PcAsync(running: true);
        await using var _2 = f2;
        int checks = 0;
        // Первая проверка (до применения) — всё открыто, вторая (после) — первый обычный сайт «упал».
        vm2.ProbeGroups = groups =>
        {
            int n = ++checks;
            return Task.FromResult<IReadOnlyList<TargetVerdict>>([.. groups.SelectMany(g => g.Targets.Select((t, i) => new TargetVerdict(g.Id, t, n == 1 || i != 0, 0.1, [])))]);
        };
        var b = vm2.AdvancedPc;
        b.Enabled = true;
        await WaitAsync(() => !b.Busy && b.Enabled);
        Assert.Equal(0, checks);   // «Как сейчас» не проверяется
        b.OthersAll = true;
        await b.ApplyAsync();
        Assert.Equal(2, checks);
        Assert.Contains("Обычные сайты перестали открываться", b.Message);
        Assert.Equal("lists", f2.Backend.AdvancedValue.Others);
        Assert.Contains("advanced_set True all 0", f2.Backend.Calls);
        Assert.False(b.OthersAll);
        Assert.True(b.OthersLists);
    }

    [Fact]
    public async Task Pc_RealHelper_RevertRestoresPreviousRows_ThroughJournal()
    {
        var shell = new Shell(new AppPaths(_tmp.Sub("root"), Repo.Data));
        shell.Data.Seed();
        var f = new HelperFixture(backend: PcHelper.Over(shell));
        await using var _ = f;
        var vm = TestWindow.Model(shell, await f.LinkAsync());
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsLoading))!.SetValue(vm, false);
        await vm.RefreshStatusAsync();
        var a = vm.AdvancedPc;
        await a.RefreshAsync(force: true);

        a.Enabled = true;
        await WaitAsync(() => !a.Busy && shell.State.Advanced.Enabled);
        AddRow(a, "example.com", "auto");
        await a.ApplyAsync();
        Assert.Single(AppState.Load(shell.Paths).Advanced.Rows);
        Assert.Single(shell.Journal.Active(ChangeKinds.Advanced));

        a.RevertCmd.Execute(null);
        await WaitAsync(() => !a.Busy && !AppState.Load(shell.Paths).Advanced.Enabled && a.HasMessage && a.Message.StartsWith(L.T("sites.adv.undo.restored", new { note = "" }).TrimEnd()));
        Assert.Empty(AppState.Load(shell.Paths).Advanced.Rows);
        Assert.False(a.Enabled);
    }

    // ---------- роутер ----------

    static async Task WaitAsync(Func<bool> cond)
    {
        for (int i = 0; i < 500 && !cond(); i++) await Task.Delay(10);
        Assert.True(cond(), "не дождались");
    }

    static FakeRouter Router(bool enabled = true)
    {
        var r = new FakeRouter { WarningsJson = "[]", Enabled = enabled, AdvancedField_On = true }.As020();
        r.UpdateFieldOn = false;
        r.StrategiesJson = """{ "catalog_version": 3, "items": [ { "id": "alt10", "title": "general (ALT10)", "source": "catalog", "runs": 7, "passed": 6 }, { "id": "alt9", "title": "general (ALT9)", "source": "catalog", "runs": 1, "passed": 1 } ] }""";
        return r;
    }

    async Task<(RouterViewModel Vm, ChangeJournal Journal)> RouterAsync(FakeRouter r, bool? sitesOpen = true)
    {
        var pc = new Shell(new AppPaths(_tmp.Sub("root"), Repo.Data));
        var settings = WindowSettings.Load(Path.Combine(_tmp.Sub("window"), "window.json"));
        var journal = new ChangeJournal(Path.Combine(_tmp.Sub("j"), "changes-window.jsonl"));
        var vm = new RouterViewModel(pc, settings)
        {
            AddressAuto = false, CustomAddress = r.Address,
            PollInterval = TimeSpan.FromMilliseconds(10), CheckTimeout = TimeSpan.FromSeconds(5),
            PcTimestamps = () => true, Gateway = () => throw new InvalidOperationException("в тестах — только фейковый роутер"),
            AppVersion = new Version(1, 0, 0), Say = s => { lock (_said) _said.Add(s); }, Confirm = _ => true,
            TrialTick = TimeSpan.FromMilliseconds(10), TrialGrace = TimeSpan.FromMilliseconds(10),
            VerifyFromPc = () => Task.FromResult(sitesOpen), Recorder = journal,
        };
        Assert.True(await vm.ConnectAsync("secret"));
        return (vm, journal);
    }

    [Fact]
    public async Task Router_LoadsState_SavesOnTrial_WithConfirmS_AndJournals()
    {
        using var r = Router();
        var (vm, journal) = await RouterAsync(r);
        var a = vm.Advanced;
        await a.RefreshAsync(force: true);
        Assert.True(a.Available);
        Assert.False(a.Enabled);
        Assert.Contains(a.Choices, c => c.Id == "alt9" && c.Title.Contains("ALT9"));

        AddRow(a, "example.com, example.org", "auto");
        AddRow(a, "bank.example", "direct");
        a.OthersAll = true;
        a.Enabled = true;
        await WaitAsync(() => r.AdvancedEnabled && !a.Busy);

        Assert.Equal(RouterViewModel.TrialSeconds, r.LastConfirmS);
        Assert.Equal("all", r.AdvancedOthers);
        Assert.Equal(["example.com", "example.org"], r.AdvancedRows[0].Domains);
        Assert.Equal(["auto", "direct"], r.AdvancedRows.Select(x => x.Strategy));
        Assert.Equal(1, r.ConfirmCalls);          // проба: сайты с ПК открылись — подтверждено без вопросов
        Assert.Null(r.Pending);
        Assert.Equal("Авто — сейчас general (ALT10)", a.Rows[0].Hint);
        var e = Assert.Single(journal.Active(ChangeKinds.RouterAdvanced));
        Assert.False(AdvancedConfig.FromJson(e.Before).Enabled);
        Assert.Equal("all", AdvancedConfig.FromJson(e.After).Others);

        // Откат записи: на роутер уходит прежнее.
        var undo = await vm.UndoAdvancedAsync(journal.Entries().Single(x => x.Id == e.Id));
        Assert.Equal(UndoStatus.Restored, undo.Status);
        Assert.False(r.AdvancedEnabled);
        Assert.Empty(r.AdvancedRows);
    }

    [Fact]
    public async Task Router_Untouched_Keeps_RouterRevertsRowsOnTimeout()
    {
        using var r = Router();
        var (vm, _) = await RouterAsync(r, sitesOpen: null);   // проверить с ПК нельзя — решает человек, плашка с отсчётом
        var a = vm.Advanced;
        await a.RefreshAsync(force: true);
        AddRow(a, "example.com", "alt9");
        await a.ApplyAsync();
        Assert.True(vm.TrialVisible);
        Assert.NotNull(r.Pending);

        // «Вернуть как было» на плашке возвращает и строки.
        await vm.RevertTrialAsync();
        Assert.False(r.AdvancedEnabled);
        Assert.Empty(r.AdvancedRows);
        Assert.Null(r.Pending);
    }

    [Fact]
    public async Task Router_InvalidFromRouter_ShowsItsText_ToggleReturns()
    {
        using var r = Router();
        r.InvalidOn = ("advanced_set", "rows[0].domains[1]");
        var (vm, journal) = await RouterAsync(r);
        var a = vm.Advanced;
        await a.RefreshAsync(force: true);
        AddRow(a, "a.example", "auto");
        a.Enabled = true;
        await WaitAsync(() => a.HasMessage && !a.Busy);
        Assert.False(a.Enabled);
        Assert.True(a.MessageIsError);
        Assert.Contains("Неверное значение", a.Message);
        Assert.Empty(journal.Active(ChangeKinds.RouterAdvanced));   // не состоялось — запись отменена
    }

    [Fact]
    public async Task Router_WithoutAdvancedMethods_ExplainsUpdate()
    {
        using var r = Router();
        r.MethodsJson = FakeRouter.Methods020.Replace("\"advanced_get\",\"advanced_set\",", "");
        var (vm, _) = await RouterAsync(r);
        await vm.Advanced.RefreshAsync(force: true);
        Assert.False(vm.Advanced.Available);
        Assert.Contains("обновите DpiShell на роутере", vm.Advanced.UnavailableText);
        Assert.False(vm.Advanced.ShowEditor);
    }

    // ---------- одно «Сохранить» и один статус на вкладку «Сайты» (UX-аудит 6, п. 2–3) ----------

    [Fact]
    public async Task Pc_SaveSites_SavesTargetsAndRows_UnderOneStatus()
    {
        var (vm, f) = await PcAsync();
        await using var _ = f;
        var a = vm.AdvancedPc;
        a.Enabled = true;
        await WaitAsync(() => !a.Busy && a.Enabled && f.Backend.Calls.Any(c => c.StartsWith("advanced_set")));
        Assert.False(vm.SitesDirty);
        Assert.True(vm.SitesSaved);

        AddRow(a, "example.com", "auto");   // несохранённая строка — это и есть «Не сохранено» вкладки
        Assert.True(vm.SitesDirty);
        Assert.False(vm.SitesSaved);
        vm.TargetsText = "mysite.example";
        vm.SaveSitesCmd.Execute(null);
        await WaitAsync(() => !a.Dirty && !a.Busy && f.Backend.Calls.Contains("advanced_set True lists 1"));

        Assert.Contains("user_targets_set mysite.example", f.Backend.Calls);
        Assert.False(a.Dirty);   // строки продвинутого режима сохранились той же кнопкой
        Assert.Equal(vm.TargetsDirty, vm.SitesDirty);   // а статус вкладки — один: свои сайты + строки
    }

    [Fact]
    public async Task Router_SitesDirty_FollowsAddressesListsAndRows_SaveClearsIt()
    {
        using var r = Router();
        var (vm, _) = await RouterAsync(r);
        await vm.LoadTargetsAsync();
        await vm.Advanced.RefreshAsync(force: true);
        Assert.False(vm.SitesDirty);

        vm.ListExcludeText = vm.ListExcludeText + "\nx.example";
        Assert.True(vm.SitesDirty);
        vm.AddTargetCmd.Execute(null);
        vm.Targets[^1].Url = "https://y.example/";
        AddRow(vm.Advanced, "a.example", "direct");
        await vm.SaveTargetsAsync();   // одна кнопка: адреса, списки и строки «Свои настройки для сайтов»
        await WaitAsync(() => !vm.SitesDirty && !vm.Advanced.Busy);

        Assert.False(vm.Advanced.Dirty);
        Assert.Contains("x.example", r.Lists["user-exclude"]);
        Assert.True(vm.SitesSaved);
    }

    [Fact]
    public async Task Router_AddSiteToExclude_GoesIntoTheSameExcludeField_KeepingUnsavedLines()
    {
        using var r = Router();
        var (vm, _) = await RouterAsync(r);
        await vm.LoadTargetsAsync();
        vm.ListExcludeText = "mine.example";   // правка в поле «Исключения» ещё не сохранена
        r.SiteWithBypassOk = false;
        vm.SiteUrl = "www.broken.example";
        await vm.SiteCheckAsync();
        await vm.AddSiteToExcludeAsync();

        Assert.Equal(["mine.example", "www.broken.example"], vm.ListExcludeText.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
        Assert.Contains("www.broken.example", r.Lists["user-exclude"]);
        Assert.Contains("«Исключения»", vm.SiteResult);
    }

    [Fact]
    public void Texts_NoWordBypass_InCardStrings()
    {
        var all = InfoTexts.Get(InfoTexts.Advanced) + new AdvancedViewModel(false).OthersHint + new AdvancedViewModel(true).StoredText;
        Assert.DoesNotContain("обход", all, StringComparison.OrdinalIgnoreCase);
        var a = new AdvancedViewModel(false);
        a.OthersAll = true;
        Assert.DoesNotContain("обход", a.OthersHint, StringComparison.OrdinalIgnoreCase);
    }
}
