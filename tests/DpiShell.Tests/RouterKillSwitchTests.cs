using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Router;

namespace DpiShell.Tests;

/// <summary>
/// Аварийный выключатель роутера в окне (router-api §5.1, §5.2): включение на пробу с confirm_s, проверка с этого ПК,
/// «Да, оставить» / «Вернуть как было», откат самим роутером, сторож и кнопка на корпусе, старый роутер без этих полей.
/// </summary>
public sealed class RouterKillSwitchTests : IDisposable
{
    readonly TempDir _tmp = new();
    readonly List<string> _said = [];
    DateTime _now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
    int _verifyCalls;

    public void Dispose() => _tmp.Dispose();

    async Task<RouterViewModel> Connected(FakeRouter r, bool? sitesOpen)
    {
        var pc = new Shell(new AppPaths(_tmp.Sub("root"), Repo.Data));
        var settings = WindowSettings.Load(Path.Combine(_tmp.Sub("window"), "window.json"));
        var vm = new RouterViewModel(pc, settings)
        {
            AddressAuto = false,
            CustomAddress = r.Address,
            PollInterval = TimeSpan.FromMilliseconds(10),
            CheckTimeout = TimeSpan.FromSeconds(5),
            PcTimestamps = () => true,
            Gateway = () => throw new InvalidOperationException("в тестах — только фейковый роутер"),
            AppVersion = new Version(1, 0, 0),
            Say = s => { lock (_said) _said.Add(s); },
            Confirm = _ => true,
            TrialTick = TimeSpan.FromMilliseconds(10),
            TrialGrace = TimeSpan.FromMilliseconds(10),
            UtcNow = () => _now,
            VerifyFromPc = () => { _verifyCalls++; return Task.FromResult(sitesOpen); },
        };
        Assert.True(await vm.ConnectAsync("secret"));
        return vm;
    }

    /// <summary>Роутер 0.2.0 со сторожем (confirm в methods), без посторонних предупреждений.</summary>
    static FakeRouter Router(bool enabled = false)
    {
        var r = new FakeRouter { WarningsJson = "[]", Enabled = enabled }.As020();
        r.UpdateFieldOn = false;
        return r;
    }

    static async Task Until(Func<bool> cond)
    {
        for (int i = 0; i < 500 && !cond(); i++) await Task.Delay(10);
        Assert.True(cond());
    }

    string LastSaid() { lock (_said) return _said[^1]; }

    [Fact]
    public async Task Enable_SitesOpen_ConfirmedWithoutAsking()
    {
        using var r = Router();
        using var vm = await Connected(r, sitesOpen: true);
        await vm.ToggleAsync();
        Assert.Equal(RouterViewModel.TrialSeconds, r.LastConfirmS);
        Assert.Equal(1, _verifyCalls);
        Assert.Equal(1, r.ConfirmCalls);
        Assert.Null(r.Pending);
        Assert.True(r.Enabled);
        Assert.False(vm.TrialVisible);
        Assert.Equal("Проверено — оставил. Обход на роутере включён.", LastSaid());
    }

    [Fact]
    public async Task Enable_WhileChecking_TextWithCountdown_NoButtons()
    {
        using var r = Router();
        using var vm = await Connected(r, sitesOpen: true);
        string? text = null; bool? ask = null, visible = null;
        vm.VerifyFromPc = () => { text = vm.TrialText; ask = vm.TrialAsk; visible = vm.TrialVisible; return Task.FromResult<bool?>(true); };
        await vm.ToggleAsync();
        Assert.Equal("Проверяю, что сайты открываются. Если не подтвердить — роутер сам вернёт прежнее через 3:00.", text);
        Assert.False(ask);
        Assert.True(visible);
        Assert.False(vm.TrialVisible);
    }

    [Fact]
    public async Task Enable_SitesFail_Banner_Keep_Confirms()
    {
        using var r = Router();
        using var vm = await Connected(r, sitesOpen: false);
        await vm.ToggleAsync();
        Assert.Equal(0, r.ConfirmCalls);
        Assert.True(vm.TrialVisible);
        Assert.Equal("Часть сайтов с этого ПК не открылась. Оставить обход?", vm.TrialText);
        Assert.True(vm.TrialAsk);
        // Отсчёт — от left_s по часам ПК, не от until роутера.
        Assert.Equal("вернётся само через 3:00", vm.TrialCountdown);
        _now = _now.AddSeconds(15);
        Assert.Equal("вернётся само через 2:45", vm.TrialCountdown);
        Assert.True(vm.TrialKeepCmd.CanExecute(null));

        await vm.KeepTrialAsync();
        Assert.Equal(1, r.ConfirmCalls);
        Assert.False(vm.TrialVisible);
        Assert.True(r.Enabled);
        Assert.Equal("Оставлено: обход на роутере работает.", LastSaid());
    }

    [Fact]
    public async Task Enable_CannotCheck_Revert_DisablesAtOnce()
    {
        using var r = Router();
        using var vm = await Connected(r, sitesOpen: null);
        await vm.EnableAsync();
        Assert.Equal("Проверить сайты с этого ПК не удалось. Всё работает?", vm.TrialText);
        await vm.RevertTrialAsync();
        Assert.False(r.Enabled);
        Assert.False(vm.TrialVisible);
        Assert.False(vm.HasOff);   // выключил человек — не «выключено без вас»
        Assert.Equal("Вернул как было: обход на роутере выключен.", LastSaid());
    }

    [Fact]
    public async Task StrategyChange_Revert_BringsPreviousStrategy()
    {
        using var r = Router(enabled: true);
        r.StrategiesJson = """{ "catalog_version": 3, "items": [ { "id": "alt10", "title": "general (ALT10)", "source": "catalog", "runs": 7, "passed": 6 }, { "id": "alt9", "title": "general (ALT9)", "source": "catalog", "runs": 3, "passed": 3 } ] }""";
        using var vm = await Connected(r, sitesOpen: false);
        await vm.ChooseStrategyAsync("alt9");
        Assert.Equal(("alt9", 180, "alt10"), (r.Strategy, r.LastConfirmS, r.Pending?.RevertTo));
        Assert.True(vm.TrialVisible);
        await vm.RevertTrialAsync();
        Assert.Equal("alt10", r.Strategy);
        Assert.True(r.Enabled);
        Assert.Null(r.Pending);
        Assert.Equal("Вернул как было: на роутере стратегия general (ALT10).", LastSaid());
    }

    [Fact]
    public async Task RouterRevertedItself_AfterCountdown_Message_AndEnableAgain()
    {
        using var r = Router();
        using var vm = await Connected(r, sitesOpen: false);
        await vm.ToggleAsync();
        Assert.True(vm.TrialVisible);
        // Никто не ответил: роутер вернул сам; окно узнаёт это одним запросом после срока (без опроса).
        r.ExpireConfirm();
        _now = _now.AddSeconds(200);
        await Until(() => vm.HasOff);
        Assert.False(vm.TrialVisible);
        Assert.False(vm.RouterEnabled);
        Assert.Equal($"Роутер вернул как было в {RouterTexts.Time(new DateTime(2026, 10, 19, 12, 3, 0, DateTimeKind.Utc))}: включение не подтвердили вовремя", vm.OffText);
        Assert.Equal(vm.OffText, vm.HomeLine);
        Assert.DoesNotContain(vm.WarningRows, w => w.Code == "confirm_reverted");
        Assert.True(vm.CanReEnable);

        // «Включить снова» — снова на пробу с проверкой с ПК; обход включён — сообщение о выключении ушло.
        _verifyCalls = 0;
        await vm.ToggleAsync();
        Assert.Equal(1, _verifyCalls);
        Assert.False(vm.HasOff);
    }

    [Fact]
    public async Task WatchdogOff_Message_Details_AndSetting()
    {
        using var r = Router(enabled: true);
        using var vm = await Connected(r, sitesOpen: true);
        Assert.True(vm.WatchdogAvailable);
        Assert.True(vm.WatchdogEnabled);
        Assert.Equal($"проверено в {RouterTexts.Time(new DateTime(2026, 10, 19, 12, 0, 1, DateTimeKind.Utc))} · всё в порядке", vm.WatchdogLine);

        r.WatchdogTrip();
        await vm.RefreshAsync();
        Assert.Equal($"Обход на роутере выключен сторожем в {RouterTexts.Time(new DateTime(2026, 10, 19, 11, 32, 0, DateTimeKind.Utc))} — с обходом пропал интернет.", vm.OffText);
        Assert.Equal(vm.OffText, vm.HomeLine);
        // Предложение подобрать другую стратегию; «Не сейчас» прячет его до следующего срабатывания сторожа.
        Assert.True(vm.OfferSelect);
        Assert.False(vm.OfferSelectButton);
        vm.DismissOfferCmd.Execute(null);
        Assert.False(vm.OfferSelect);
        await vm.RefreshAsync();
        Assert.False(vm.OfferSelect);   // тот же last_off — не возвращается
        Assert.True(vm.CanReEnable);    // «Попробовать снова» остаётся
        r.WatchdogTrip("2026-10-19T11:50:00Z");
        await vm.RefreshAsync();
        Assert.True(vm.OfferSelect);    // новое срабатывание — предложение снова
        Assert.Equal("С обходом интернет пропал, без обхода есть", vm.OffDetail);
        Assert.Equal(["https://www.google.com/generate_204", "https://ya.ru/"], vm.OffTargets.Select(t => t.Url));
        Assert.Equal("таймаут 5 с", vm.OffTargets[0].Detail);
        Assert.True(vm.HasOffDetails);
        Assert.Empty(vm.WarningRows);
        Assert.EndsWith("· с обходом пропадал интернет", vm.WatchdogLine);

        // Галочка — schedule_set с watchdog, расписание то же.
        vm.WatchdogEnabled = false;
        await Until(() => !r.WatchdogEnabled && !vm.Busy);
        Assert.False(vm.WatchdogEnabled);
        Assert.Equal("", vm.WatchdogLine);
        Assert.True(r.ScheduleEnabled);
        // Другая настройка расписания не сбрасывает сторожа.
        vm.AutoSelect = true;
        await Until(() => r.AutoSelect && !vm.Busy);
        Assert.False(r.WatchdogEnabled);
    }

    [Fact]
    public async Task ButtonOff_Message_WithoutDetails()
    {
        using var r = Router(enabled: true);
        using var vm = await Connected(r, sitesOpen: true);
        r.ButtonOff();
        await vm.RefreshAsync();
        Assert.StartsWith("Обход на роутере выключен кнопкой на корпусе в ", vm.OffText);
        Assert.Empty(vm.OffTargets);
        Assert.True(vm.CanReEnable);
    }

    [Fact]
    public async Task OldRouter_NoFields_AsBefore()
    {
        using var r = new FakeRouter { Enabled = false };   // 0.1.0: без confirm, pending_confirm, last_off, watchdog
        using var vm = await Connected(r, sitesOpen: false);
        await vm.ToggleAsync();
        Assert.True(r.Enabled);
        Assert.Null(r.LastConfirmS);
        Assert.Equal(0, r.ConfirmCalls);
        Assert.Equal(0, _verifyCalls);
        Assert.False(vm.TrialVisible);
        Assert.False(vm.HasOff);
        Assert.False(vm.WatchdogAvailable);
        Assert.Equal("Обход на роутере включён.", LastSaid());
        lock (r.Calls) Assert.DoesNotContain(r.RawBodies, b => b.Contains("confirm_s") || b.Contains("watchdog"));
    }

    [Fact]
    public async Task WatchdogSelect_Running_Found_Failed_AndLegacy()
    {
        using var r = Router(enabled: true);
        using var vm = await Connected(r, sitesOpen: true);

        // Старый роутер без watchdog.select: прежнее предложение «Подобрать / Не сейчас».
        r.WatchdogTrip();
        await vm.RefreshAsync();
        Assert.Equal(RouterViewModel.WatchdogSelectState.None, vm.WdState);
        Assert.True(vm.OfferSelect);

        // Роутер сам подбирает после сторожа: фраза, ход подбора, без кнопок возврата и предложения.
        r.SelectSteps = 1000;
        r.WatchdogSelectRunning();
        await vm.RefreshAsync();
        Assert.Equal(RouterViewModel.WatchdogSelectState.Running, vm.WdState);
        Assert.Equal("Сторож выключил оптимизацию — роутер подбирает другую стратегию…", vm.OffText);
        Assert.True(vm.WdRunning);
        Assert.False(vm.OfferSelect);
        Assert.False(vm.CanReEnable);
        await Until(() => vm.Selecting && vm.SelectProgressText.StartsWith("Роутер подбирает сам."));
        await vm.CancelSelectAsync();

        // Нашёл и включил: фраза с названием, без кнопок; «Подробнее» — если роутер отдал last_off.
        r.WatchdogSelectFound("alt10");
        await vm.RefreshAsync();
        Assert.Equal(RouterViewModel.WatchdogSelectState.Found, vm.WdState);
        Assert.StartsWith("Сторож выключил прежнюю стратегию, роутер подобрал и включил ", vm.OffText);
        Assert.EndsWith(".", vm.OffText);
        Assert.False(vm.OffAlert);
        Assert.False(vm.OfferSelect);
        Assert.False(vm.CanReEnable);
        Assert.Empty(vm.WarningRows);   // auto_selected не дублирует фразу

        // Не нашёл: оптимизация выключена, кнопки «Подобрать на роутере» и «Попробовать прежнюю».
        r.WatchdogTrip("2026-10-19T12:10:00Z");
        r.WatchdogSelectFailed();
        await vm.RefreshAsync();
        Assert.Equal(RouterViewModel.WatchdogSelectState.Failed, vm.WdState);
        Assert.Equal("Сторож выключил оптимизацию, подходящей стратегии не нашлось. Оптимизация выключена, пока вы её не включите.", vm.OffText);
        Assert.True(vm.OffAlert);
        Assert.False(vm.OfferSelect);
        Assert.True(vm.OfferSelectButton);
        Assert.True(vm.CanReEnable);
        Assert.Equal("Попробовать прежнюю (на пробу)", vm.ReEnableText);
        Assert.Empty(vm.WarningRows);
    }

    [Fact]
    public async Task Client_ReadsWatchdogSelect_AndGroups()
    {
        using var r = Router(enabled: false);
        r.WatchdogSelectFound("alt9");
        r.SelectRowsJson = """[ { "id": "alt9", "title": "x", "group_share": 0.67, "target_share": 0.9, "avg_s": 0.5, "error": null, "groups_ok": 2, "groups_total": 3 } ]""";
        using var c = new RouterClient(r.Address);
        await c.LoginAsync(() => "secret");
        var st = await c.StatusAsync();
        Assert.Equal(new RouterWatchdogSelect(new DateTime(2026, 10, 19, 11, 35, 0, DateTimeKind.Utc), true, "alt9"), st.Watchdog!.Select);
        var row = (await c.SelectProgressAsync()).Rows![0];
        Assert.Equal((2, 3), (row.GroupsOk, row.GroupsTotal));
        Assert.Equal("2 из 3", new RouterScoreRow(row, "x", null, 5).Groups);
        // Старый роутер: полей нет — «N из M» по числу групп в разбивке или долей, как раньше.
        var old = row with { GroupsOk = null, GroupsTotal = null };
        Assert.Equal("3 из 5", new RouterScoreRow(old, "x", null, 5).Groups);
        Assert.Equal("67 %", new RouterScoreRow(old, "x").Groups);
    }

    [Fact]
    public async Task RouterPage_FirstVisit_SearchesOnce()
    {
        using var r = Router(enabled: true);
        var pc = new Shell(new AppPaths(_tmp.Sub("root"), Repo.Data));
        var settings = WindowSettings.Load(Path.Combine(_tmp.Sub("window"), "window.json"));
        using var vm = new RouterViewModel(pc, settings)
        {
            AddressAuto = false, CustomAddress = r.Address,
            PcTimestamps = () => true, AppVersion = new Version(1, 0, 0), Say = _ => { }, Confirm = _ => true,
        };
        Assert.False(vm.Searched);
        Assert.Equal("Роутер не проверялся", vm.EmptyTitle);
        await vm.RefreshRouterPageAsync();
        Assert.True(vm.Searched);
        Assert.True(vm.Found);
    }

    [Fact]
    public async Task RouterPage_NotFound_SearchesOnlyOnce()
    {
        var pc = new Shell(new AppPaths(_tmp.Sub("root"), Repo.Data));
        var settings = WindowSettings.Load(Path.Combine(_tmp.Sub("window"), "window.json"));
        using var vm = new RouterViewModel(pc, settings)
        {
            AddressAuto = false, CustomAddress = "localhost:1",
            PcTimestamps = () => true, AppVersion = new Version(1, 0, 0), Say = _ => { }, Confirm = _ => true,
        };
        await vm.RefreshRouterPageAsync();
        Assert.True(vm.Searched);
        Assert.Equal("Роутер с DpiShell не найден", vm.EmptyTitle);
        Assert.False(vm.Found);
        await vm.RefreshRouterPageAsync();   // повторный заход — без нового поиска, состояние то же
        Assert.Equal("Роутер с DpiShell не найден", vm.EmptyTitle);
    }

    [Fact]
    public async Task Client_Status_WithoutWatchdogSelect_IsNull()
    {
        using var r = Router(enabled: true);
        using var c = new RouterClient(r.Address);
        await c.LoginAsync(() => "secret");
        Assert.Null((await c.StatusAsync()).Watchdog!.Select);
    }

    [Fact]
    public async Task Client_ConfirmS_InContractRange()
    {
        using var r = Router();
        using var c = new RouterClient(r.Address);
        await c.LoginAsync(() => "secret");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => c.EnableAsync(30));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => c.StrategySetAsync("alt9", 601));
        await c.EnableAsync(60);
        var st = await c.StatusAsync();
        Assert.Equal(60, st.PendingConfirm!.LeftS);
        Assert.True(st.PendingConfirm.RevertsToOff);
        Assert.True(st.Schedule!.Watchdog);
        Assert.Equal(5, st.Watchdog!.IntervalMin);
        await c.ConfirmAsync();
        Assert.Null((await c.StatusAsync()).PendingConfirm);
        Assert.Equal("2:45", RouterTexts.Countdown(TimeSpan.FromSeconds(164.2)));
    }
}
