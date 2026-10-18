using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Router;

namespace DpiShell.Tests;

/// <summary>Логика окна для роутера (router-api §6) на фейковом роутере: вход, пароль, подбор, стратегия с ПК, busy.</summary>
public sealed class RouterViewModelTests : IDisposable
{
    readonly TempDir _tmp = new();
    readonly List<string> _said = [];

    public void Dispose() => _tmp.Dispose();

    /// <summary>Настройки окна последнего <see cref="Make"/> (%LocalAppData% — здесь во временной папке).</summary>
    WindowSettings Settings = new();

    (RouterViewModel Vm, Shell Shell) Make(FakeRouter r, Shell? shell = null)
    {
        var pc = shell ?? new Shell(new AppPaths(_tmp.Sub("root"), Repo.Data));
        Settings = WindowSettings.Load(Path.Combine(_tmp.Sub("window"), "window.json"));
        // Как в окне: найденные стратегии и свои адреса ПК отдаёт помощник — здесь прямо из временной папки.
        var vm = new RouterViewModel(pc, Settings)
        {
            FoundStrategies = () => UserStrategies.Load(pc.Paths),
            PcUserTargets = () => pc.State.UserTargets,
            AddressAuto = false,
            CustomAddress = r.Address,
            PollInterval = TimeSpan.FromMilliseconds(10),
            CheckTimeout = TimeSpan.FromSeconds(5),
            DnsTimeout = TimeSpan.FromSeconds(5),
            PcTimestamps = () => true,   // netsh в тестах не зовём
            Gateway = () => throw new InvalidOperationException("в тестах — только фейковый роутер"),
        };
        vm.Say = s => { lock (_said) _said.Add(s); };
        return (vm, pc);
    }

    void AssertNoSecretSaid(string secret)
    {
        lock (_said) Assert.DoesNotContain(_said, s => s.Contains(secret));
    }

    [Fact]
    public async Task Connect_Success_SavesPasswordEncryptedOnly()
    {
        using var r = new FakeRouter();
        var (vm, shell) = Make(r);
        Assert.True(await vm.ConnectAsync("secret"));
        Assert.True(vm.Connected);
        Assert.Equal("Подключено, DpiShell 0.1.0", vm.ConnectionText);
        Assert.Equal("secret", RouterSecrets.Unprotect(Settings.RouterPassword));
        var json = File.ReadAllText(Settings.File);
        Assert.DoesNotContain("secret", json);
        Assert.Contains(r.Address, json);
        // Главная: статус, стратегия, предупреждения.
        Assert.True(vm.Found);
        Assert.True(vm.RouterEnabled);
        Assert.Equal("Обход делает роутер", vm.HomeTitle);
        // Есть предупреждение — на главной число и его текст вместо стратегии и времени (UX-аудит 2, п. 7).
        Assert.Equal("· 1 предупреждение", vm.HomeFixText);
        Assert.Equal("Ускорение сети на роутере мешает обходу", vm.HomeLine);
        Assert.StartsWith("Стратегия general (ALT10), проверено в ", vm.HomeDetail);   // на вкладке — как было
        Assert.Equal("Ускорение сети на роутере мешает обходу", vm.Warnings);
        Assert.Equal("Исправить", Assert.Single(vm.WarningRows).Fix);
        Assert.Single(vm.Strategies);
        AssertNoSecretSaid("secret");

        // Новый запуск: сохранённый пароль, тихий вход.
        var (again, _) = Make(r, new Shell(new AppPaths(shell.Paths.Root, Repo.Data)));
        again.AddressAuto = false;
        again.CustomAddress = Settings.RouterAddress!;
        await again.RefreshAsync();
        Assert.True(again.Connected);
        Assert.Equal(2, r.Logins);
    }

    [Fact]
    public async Task Connect_WrongPassword_NotSaved()
    {
        using var r = new FakeRouter();
        var (vm, shell) = Make(r);
        Assert.False(await vm.ConnectAsync("wrong-pass"));
        Assert.False(vm.Connected);
        Assert.Equal("Неверный пароль", vm.ConnectionText);
        Assert.Null(Settings.RouterPassword);
        Assert.DoesNotContain("wrong-pass", File.ReadAllText(Settings.File));
        Assert.True(vm.NeedLogin);
        AssertNoSecretSaid("wrong-pass");
    }

    [Fact]
    public async Task SavedPasswordNoLongerValid_IsForgotten()
    {
        using var r = new FakeRouter();
        var (vm, shell) = Make(r);
        Settings.RouterPassword = RouterSecrets.Protect("old-pass");
        await vm.RefreshAsync();
        Assert.False(vm.Connected);
        Assert.Equal("Неверный пароль", vm.ConnectionText);
        Assert.Null(Settings.RouterPassword);
    }

    [Fact]
    public async Task Connect_NotFound_BlockHidden()
    {
        using var r = new FakeRouter { StatusJson = null };
        var (vm, _) = Make(r);
        Assert.False(await vm.ConnectAsync("secret"));
        Assert.False(vm.Found);
        Assert.Equal("DpiShell на роутере не найден", vm.ConnectionText);
        Assert.Equal(0, r.Logins);
    }

    [Theory]
    [InlineData("""{ "product": "dpishell-router", "version": "0.0.9", "enabled": true, "strategy": "alt10" }""", "\"0.9\"", "Обновите пакет на роутере", "устарел", false)]
    [InlineData("""{ "product": "dpishell-router", "api": 2, "enabled": true, "strategy": "alt10" }""", "\"2.0\"", "Обновите программу: роутер новее", "новее программы", true)]
    public async Task Connect_OtherApi_LoginOnlyForStatusUpdateUninstall(string statusJson, string rpcApi, string text, string compat, bool needsApp)
    {
        using var r = new FakeRouter { StatusJson = statusJson, StatusApi = rpcApi };
        var (vm, _) = Make(r);
        // Без пароля — только поиск: блок на главной со статусом из status.json, «Войти» есть (обновить и удалить можно только после входа).
        Assert.False(await vm.ConnectAsync(""));
        Assert.Equal(text, vm.ConnectionText);
        Assert.True(vm.Found);
        Assert.True(vm.NeedLogin);
        Assert.Equal("Обход делает роутер", vm.HomeTitle);
        Assert.Contains(text.ToLowerInvariant()[..10], vm.HomeDetail);
        Assert.Equal(0, r.Logins);

        // Вход: совместимость Blocked (§5.4) — доступны состояние, обновление, удаление; остальные команды неактивны.
        Assert.True(await vm.ConnectAsync("secret"));
        Assert.True(vm.Connected);
        Assert.True(vm.Blocked);
        Assert.False(vm.CanAct);
        Assert.Contains(compat, vm.CompatText);
        Assert.Equal(needsApp, vm.CompatNeedsApp);
        Assert.Equal(!needsApp, vm.CompatNeedsRouter);
        Assert.False(vm.ToggleCmd.CanExecute(null));
        Assert.False(vm.CheckCmd.CanExecute(null));
        Assert.True(vm.UpdateCmd.CanExecute(null));
        Assert.True(vm.UninstallCmd.CanExecute(null));
        Assert.Equal(["status"], r.Calls.Where(c => Method(c) != "login").Select(Method).Distinct());
        Assert.Contains(needsApp ? "нужна новая версия программы" : "обновите DpiShell на роутере", vm.HomeDetail);
        await vm.ToggleAsync();
        Assert.DoesNotContain(r.Calls, c => Method(c) is "enable" or "disable");
    }

    [Fact]
    public async Task Connect_WithoutPassword_DetectOnly()
    {
        using var r = new FakeRouter();
        var (vm, _) = Make(r);
        Assert.False(await vm.ConnectAsync(""));
        Assert.True(vm.Found);
        Assert.True(vm.NeedLogin);
        Assert.Equal("DpiShell 0.1.0 найден — введите пароль роутера", vm.ConnectionText);
        Assert.Equal(0, r.Logins);
        Assert.Empty(r.Calls);
    }

    /// <summary>UX-аудит 2, п. 11: у свежей OpenWrt пароль root пустой — «Пароль не задан» входит с пустым, не путая с «взять сохранённый».</summary>
    [Fact]
    public async Task Connect_NoPasswordSet_FreshOpenWrt()
    {
        using var r = new FakeRouter { Password = "" };
        var (vm, _) = Make(r);
        Assert.False(vm.HasSavedPassword);
        Assert.False(await vm.ConnectAsync(""));   // пустое поле без выбора — только поиск, как раньше
        Assert.Equal(0, r.Logins);

        Assert.True(await vm.ConnectAsync(null, noPassword: true));
        Assert.True(vm.Connected);
        Assert.Equal(1, r.Logins);
        // Запомнили «пароль не задан»: в следующий раз пустое поле — вход с ним.
        Assert.True(vm.HasSavedPassword);
        Assert.Equal("", RouterSecrets.Unprotect(Settings.RouterPassword));

        // Пароль на роутере всё же есть — говорим прямо, а не «неверный пароль».
        using var r2 = new FakeRouter();
        var (vm2, _) = Make(r2);
        Assert.False(await vm2.ConnectAsync(null, noPassword: true));
        Assert.False(vm2.Connected);
        Assert.Equal("У роутера есть пароль — введите его", vm2.ConnectionText);
    }

    [Fact]
    public async Task Select_RunningToBestApplied_ThenChooseAnother()
    {
        using var r = new FakeRouter
        {
            SelectSteps = 2,
            BackupsJson = """["alt9", "alt10"]""",
            SelectRowsJson = """
                [ { "id": "alt", "group_share": 1.0, "target_share": 0.88, "avg_s": 0.74, "error": null },
                  { "id": "alt9", "group_share": 1.0, "target_share": 0.80, "avg_s": 0.60, "error": null },
                  { "id": "alt2", "group_share": 0.0, "target_share": 0.0, "avg_s": 0, "error": "не открылось" },
                  { "id": "alt10", "group_share": 0.5, "target_share": 0.5, "avg_s": 0.9, "error": null },
                  { "id": "d-17", "title": "fake + multisplit, ts — YouTube", "group_share": 0.5, "target_share": 0.4, "avg_s": 0.9, "error": null } ]
                """,
        };
        var (vm, _) = Make(r);
        await vm.ConnectAsync("secret");
        Assert.True(vm.SelectOnRouter);   // подключён — по умолчанию подбор на роутере
        var progress = new List<string>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.SelectProgressText) && vm.SelectProgressText.Length > 0) lock (progress) progress.Add(vm.SelectProgressText); };
        await vm.StartSelectAsync(deep: false);

        Assert.False(vm.Selecting);
        Assert.Contains("Проверено 1 из 20: alt3: YouTube · осталось ~2 мин", progress);
        // Роутер сам включил лучшую (редакция §9): «Применить на роутере» не нужно, можно выбрать другую.
        Assert.Equal("alt", vm.Best);
        Assert.True(vm.Applied);
        Assert.False(vm.HasBest);
        Assert.StartsWith("Включена на роутере: ", vm.ResultText);
        Assert.StartsWith("Запасные: ", vm.ResultHint);
        Assert.Equal("alt", vm.Status!.Strategy);
        Assert.Equal("alt", vm.Scores[0].Id);
        Assert.True(vm.Scores[0].IsBest);
        Assert.Equal("fake + multisplit, ts — YouTube", vm.Scores.Single(s => s.Id == "d-17").Title);   // title из rows (кандидат глубокого поиска)
        Assert.Equal(3, r.Calls.Count(c => Method(c) == "select_progress"));
        var start = r.Calls.Single(c => Method(c) == "select_start")["params"]![3]!;
        Assert.Equal("full", start["mode"]!.GetValue<string>());
        Assert.Equal("main", start["depth"]!.GetValue<string>());

        await vm.ApplyAsync("alt9");
        var apply = r.Calls.Single(c => Method(c) == "apply")["params"]![3]!;
        Assert.Equal("alt9", apply["id"]!.GetValue<string>());
        Assert.Equal(["alt", "alt10"], apply["backups"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal("alt9", vm.Status!.Strategy);
        Assert.True(vm.Scores.Single(s => s.Id == "alt9").IsBest);
        Assert.StartsWith("Включена на роутере: ", vm.ResultText);
    }

    [Fact]
    public async Task Select_ProgressErrorsRightAfterStart_ShowStarting_NotError()
    {
        using var r = new FakeRouter { SelectSteps = 2, ProgressErrors = 3, ProgressZeros = 2 };
        var (vm, _) = Make(r);
        await vm.ConnectAsync("secret");
        var texts = new List<string>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.SelectProgressText) && vm.SelectProgressText.Length > 0) lock (texts) texts.Add(vm.SelectProgressText); };
        await vm.StartSelectAsync(deep: false);
        Assert.Contains("Запускаю подбор…", texts);
        Assert.DoesNotContain(texts, t => t.StartsWith("Проверено 0 из"));
        Assert.StartsWith("Включена на роутере: ", vm.ResultText);   // дошёл до результата, а не «не удалось узнать ход»
        lock (_said) Assert.DoesNotContain(_said, s => s.Contains("Подбор ещё не поднялся") || s.Contains("Ошибка на роутере"));
    }

    [Fact]
    public async Task Select_ProgressErrorsAfterGrace_AreErrors()
    {
        using var r = new FakeRouter { ProgressErrors = 1000 };
        var (vm, _) = Make(r);
        vm.SelectStartGrace = TimeSpan.Zero;
        await vm.ConnectAsync("secret");
        await vm.StartSelectAsync(deep: false);
        Assert.Equal("Не удалось узнать ход подбора на роутере.", vm.ResultText);
    }

    [Fact]
    public async Task Select_AlreadyRunning_ShowsRunningSelect_NotError()
    {
        using var r = new FakeRouter { SelectSteps = 2, Applied = false, BusyOn = "select_start", SelectRunningJob = true };
        var (vm, _) = Make(r);
        await vm.ConnectAsync("secret");
        await Task.Delay(100);   // status увидел идущий подбор и сам начал опрос — дождёмся конца
        while (vm.Selecting) await Task.Delay(10);
        await vm.StartSelectAsync(deep: false);
        lock (_said) Assert.DoesNotContain(_said, s => s.StartsWith("Роутер занят"));
        Assert.False(string.IsNullOrEmpty(vm.ResultText));
    }

    [Fact]
    public async Task Select_Cancel_ShowsStopped()
    {
        using var r = new FakeRouter { SelectSteps = 1000 };
        var (vm, _) = Make(r);
        await vm.ConnectAsync("secret");
        var run = vm.StartSelectAsync(deep: true);
        for (int i = 0; i < 200 && !vm.Selecting; i++) await Task.Delay(10);
        while (vm.Busy) await Task.Delay(10);
        await vm.CancelSelectAsync();
        await run;
        Assert.Equal("Подбор на роутере остановлен.", vm.ResultText);
        Assert.Null(vm.Best);
        Assert.Equal("deep", r.Calls.Single(c => Method(c) == "select_start")["params"]![3]!["depth"]!.GetValue<string>());
    }

    [Fact]
    public async Task Schedule_FromStatus_AndSet()
    {
        using var r = new FakeRouter();
        var (vm, _) = Make(r);
        await vm.ConnectAsync("secret");
        Assert.True(vm.ScheduleEnabled);
        Assert.Equal(1, vm.ScheduleHours);
        await vm.SetScheduleAsync(true, 0.5);
        var args = r.Calls.Single(c => Method(c) == "schedule_set")["params"]![3]!;
        Assert.Equal(0.5, args["hours"]!.GetValue<double>());
        Assert.Equal(0.5, vm.ScheduleHours);
        Assert.Equal("Роутер будет проверять сайты каждые 30 минут.", _said[^1]);
        await vm.SetScheduleAsync(false, 0.5);
        Assert.False(vm.ScheduleEnabled);
    }

    [Fact]
    public async Task Select_AlreadyRunningOnRouter_ShowsProgress()
    {
        using var r = new FakeRouter { SelectRunningJob = true, SelectSteps = 1 };
        var (vm, _) = Make(r);
        await vm.ConnectAsync("secret");
        // status.jobs.select = true — ход показывается сам; дождаться конца опроса.
        for (int i = 0; i < 200 && (vm.Selecting || vm.Best is null); i++) await Task.Delay(20);
        r.SelectRunningJob = false;
        Assert.Equal("alt", vm.Best);
        Assert.DoesNotContain(r.Calls, c => Method(c) == "select_start");
    }

    [Fact]
    public async Task SendStrategy_FromPc_UserSetThenStrategySetThenCheck()
    {
        using var r = new FakeRouter();
        var (vm, shell) = Make(r);
        var found = shell.Catalog.Strategies[0] with { Id = "found-261016-2213-2", Title = "Найденная: fake + split2", Source = "search" };
        UserStrategies.Add(shell.Paths, [found]);
        await vm.ConnectAsync("secret");
        r.Calls.Clear();

        await vm.SendStrategyAsync(found.Id);

        var methods = r.Calls.Select(Method).ToList();
        int set = methods.IndexOf("strategies_user_set"), choose = methods.IndexOf("strategy_set"), check = methods.IndexOf("check_now");
        Assert.True(set >= 0 && set < choose && choose < check, string.Join(", ", methods));
        var items = r.Calls[set]["params"]![3]!["items"]!.AsArray();
        Assert.Equal("u-found-261016-2213-2", items[0]!["id"]!.GetValue<string>());
        Assert.Equal("u-found-261016-2213-2", r.Calls[choose]["params"]![3]!["id"]!.GetValue<string>());
        Assert.Equal("u-found-261016-2213-2", r.Strategy);
        Assert.Contains(_said, s => s.StartsWith("Стратегия «Найденная: fake + split2» включена на роутере. Роутер проверил: всё открывается."));
    }

    [Fact]
    public async Task SendStrategy_RouterTooOld_ClearText()
    {
        using var r = new FakeRouter();
        r.MissingMethods.Add("strategies_user_set");
        var (vm, shell) = Make(r);
        UserStrategies.Add(shell.Paths, [shell.Catalog.Strategies[0] with { Id = "found-1", Title = "Найденная", Source = "search" }]);
        await vm.ConnectAsync("secret");
        await vm.SendStrategyAsync("found-1");
        Assert.Contains("Роутер не умеет принимать стратегии с ПК — обновите пакет DpiShell на роутере", _said);
        Assert.DoesNotContain(r.Calls, c => Method(c) == "strategy_set");
    }

    [Fact]
    public async Task Busy_ShortTextWithoutPanic()
    {
        using var r = new FakeRouter { BusyOn = "disable" };
        var (vm, _) = Make(r);
        await vm.ConnectAsync("secret");
        await vm.ToggleAsync();
        Assert.Equal("Роутер занят: идёт подбор", _said[^1]);
        Assert.True(vm.Connected);
        Assert.False(vm.Busy);
    }

    [Fact]
    public async Task Check_WaitsForNewLastCheck()
    {
        using var r = new FakeRouter();
        var (vm, _) = Make(r);
        await vm.ConnectAsync("secret");
        var before = vm.Status!.LastCheck!.At;
        await vm.CheckAsync();
        Assert.Equal(before!.Value.AddMinutes(1), vm.Status!.LastCheck!.At);
        Assert.Equal("Роутер проверил: всё открывается.", _said[^1]);
    }

    [Fact]
    public async Task Targets_LoadCopyFromPcSave()
    {
        using var r = new FakeRouter();
        var (vm, shell) = Make(r);
        shell.State.UserTargets = ["example.com", "my-site.ru"];
        await vm.ConnectAsync("secret");
        await vm.LoadTargetsAsync();
        Assert.Equal("https://example.com/", Assert.Single(vm.Targets).Url);
        Assert.Equal("example.com", vm.ListGeneralText);
        vm.CopyFromPc();
        Assert.Equal(["https://example.com/", "https://my-site.ru/"], vm.Targets.Select(t => t.Url));
        await vm.SaveTargetsAsync();
        var set = r.Calls.Single(c => Method(c) == "targets_set")["params"]![3]!["items"]!.AsArray();
        Assert.Equal(2, set.Count);
        Assert.True(set[0]!["important"]!.GetValue<bool>());
        Assert.Equal(2, r.Calls.Count(c => Method(c) == "lists_set"));
    }

    [Fact]
    public async Task Diag_ConflictStopAndReport()
    {
        using var r = new FakeRouter();
        var (vm, _) = Make(r);
        var report = Path.Combine(_tmp.Path, "report.txt");
        vm.Confirm = _ => true;
        vm.AskReportPath = () => report;
        await vm.ConnectAsync("secret");
        await vm.RefreshDiagAsync();
        Assert.Equal("zapret2", Assert.Single(vm.Conflicts).Name);
        Assert.Equal("включено — мешает обходу", vm.FlowOffloadText);
        Assert.StartsWith("провайдер подменяет", vm.DnsText);
        await vm.StopConflictAsync("zapret2");
        Assert.Equal("zapret2", r.Calls.Single(c => Method(c) == "conflict_stop")["params"]![3]!["name"]!.GetValue<string>());
        await vm.SaveReportAsync();
        Assert.Equal("отчёт роутера", File.ReadAllText(report));
    }

    [Fact]
    public async Task Diag_AfterFlowOffloadFix_HandedToBypass()
    {
        using var r = new FakeRouter();
        var (vm, _) = Make(r);
        vm.Confirm = _ => true;
        await vm.ConnectAsync("secret");
        await vm.FixWarningAsync("flow_offload");
        await vm.RefreshDiagAsync();
        Assert.Equal("передано обходу — не мешает", vm.FlowOffloadText);
    }

    [Fact]
    public async Task PcEnable_WhenRouterBypasses_AsksAboutDoubleProcessing()
    {
        using var r = new FakeRouter();
        var vm = TestWindow.Model(new Shell(new AppPaths(_tmp.Sub("main"), Repo.Data)));
        vm.Router.PcTimestamps = () => true;
        vm.Router.AddressAuto = false;
        vm.Router.CustomAddress = r.Address;
        await vm.Router.ConnectAsync("secret");
        // Не «Выключено» — обход работает, только через роутер (UX-аудит 2, п. 3).
        Assert.Equal("Работает через роутер", vm.StatusTitle);
        Assert.Equal("На этом ПК включать не нужно", vm.StatusDetail);
        string? asked = null;
        vm.Choose = (t, _) => { asked = t; return -1; };   // «Отмена» — на ПК ничего не включаем (и не трогаем sc.exe)
        await vm.EnableAsync();
        Assert.Contains("обрабатывается дважды", asked);
        Assert.False(vm.Busy);
        Assert.False(vm.IsRunning);
    }

    [Fact]
    public async Task Targets_Invalid_NamesTheAddress()
    {
        using var r = new FakeRouter { InvalidOn = ("targets_set", "items[1].url") };
        var (vm, shell) = Make(r);
        shell.State.UserTargets = ["bad_host!"];
        await vm.ConnectAsync("secret");
        await vm.LoadTargetsAsync();
        vm.CopyFromPc();
        await vm.SaveTargetsAsync();
        Assert.Equal("Роутер не принял адрес «https://bad_host!/» — проверьте, что это домен или https-адрес", _said[^1]);
    }

    static string Method(System.Text.Json.Nodes.JsonObject call) => call["params"]![2]!.GetValue<string>();
}
