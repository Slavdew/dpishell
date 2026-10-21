using System.Text.Json.Nodes;
using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Helper;
using DpiShell.Core.Router;

namespace DpiShell.Tests;

/// <summary>
/// Совместная работа в окне: переключатель «Совместная работа с роутером» на вкладке «Роутер» — модель окна, подделка роутера по договору
/// (pc_link_set/del/get, lists_get google/discord) и подделка помощника. Настоящие сеть, прокси и службы машины не трогаются.
/// </summary>
public sealed class JointWindowTests : IDisposable
{
    readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    const string Mac = "aa:bb:cc:dd:ee:01";
    const string PcId = "00000000-0000-4000-8000-0000000000a1";

    static async Task WaitAsync(Func<bool> cond, string what = "не дождались")
    {
        for (int i = 0; i < 600 && !cond(); i++) await Task.Delay(10);
        Assert.True(cond(), what);
    }

    static FakeRouter NewRouter(bool enabled = true)
    {
        var r = new FakeRouter { WarningsJson = "[]", Enabled = enabled }.As020();
        r.UpdateFieldOn = false;
        return r;
    }

    /// <summary>Окно с помощником-подделкой (оптимизация на ПК работает) и подключённым роутером.</summary>
    async Task<(MainViewModel Vm, HelperFixture F, List<string> Said)> OpenAsync(FakeRouter router, bool pcRunning = false, bool helper = true, WindowSettings? settings = null, Shell? shellIn = null)
    {
        var shell = shellIn ?? new Shell(new AppPaths(_tmp.Sub("root-" + Guid.NewGuid().ToString("N")[..6]), Repo.Data));
        var f = new HelperFixture();
        f.Backend.TrackEnabled = true;
        f.Backend.StatusValue = f.Backend.StatusValue with
        {
            Enabled = pcRunning, Active = pcRunning, StrategyId = "alt9", Service = pcRunning ? SvcState.Running : SvcState.Stopped,
            UserTargets = ["mysite.example", "youtube.com"],
        };
        var vm = TestWindow.Model(shell, helper ? await f.LinkAsync() : null, settings);
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsLoading))!.SetValue(vm, false);
        vm.Confirm = _ => true;
        await vm.RefreshStatusAsync();
        var said = new List<string>();
        vm.Router.Fail = said.Add;
        vm.Router.AddressAuto = false;
        vm.Router.CustomAddress = router.Address;
        vm.Router.PcTimestamps = () => true;
        vm.Router.OwnMac = _ => Mac;
        await vm.Router.ConnectAsync("secret");
        return (vm, f, said);
    }

    static bool Settled(MainViewModel vm) => vm.Router.JointStatus.Length > 0;

    /// <summary>Связка действует и включение закончено (переключатель снова доступен).</summary>
    static Task ActiveAsync(MainViewModel vm) => WaitAsync(() => vm.Router.JointStatus.StartsWith("Роутер ведёт") && vm.Router.JointAvailable, "связка не поднялась: " + vm.Router.JointStatus);

    // ---------- по умолчанию выключена ----------

    [Fact]
    public async Task Default_is_off_nothing_is_sent_and_the_card_is_available_once_connected()
    {
        using var router = NewRouter();
        var (vm, f, _) = await OpenAsync(router);
        await using var _ = f;
        Assert.False(vm.Router.JointWanted);
        Assert.False(vm.Router.HasJointStatus);
        Assert.True(vm.Router.JointAvailable);
        Assert.Equal(L.T("router.joint.hint"), vm.Router.JointHint);
        Assert.False(new WindowSettings().JointWanted);
        await vm.Router.JointTickAsync();
        Assert.Empty(router.Links);
        Assert.Equal(0, router.PcLinkSetCalls);
        Assert.DoesNotContain(f.Backend.Calls, c => c.StartsWith("joint_"));
    }

    // ---------- включение ----------

    [Fact]
    public async Task Enabling_sets_the_link_with_decoys_and_own_sites_and_the_pc_keeps_running()
    {
        using var router = NewRouter();
        var (vm, f, said) = await OpenAsync(router);
        await using var _ = f;

        vm.Router.JointWanted = true;
        await ActiveAsync(vm);
        await vm.Router.JointWhenIdleAsync();

        Assert.Equal("Роутер ведёт YouTube и Discord, этот ПК — свои сайты (1)", vm.Router.JointStatus);
        var link = Assert.Single(router.Links.Values);
        Assert.Equal(PcId, link.Id);
        Assert.Equal([Mac], link.Macs);
        // Приманки первыми, потом свои сайты; youtube.com ведёт роутер — в связку не идёт и пришёл в ignored.
        Assert.Equal(["www.google.com", "4pda.to", "max.ru", "mysite.example"], router.LastPcLinkSet!["domains"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(["youtube.com"], link.Ignored.Concat(vm.Router.JointRouterOwned).Distinct());
        // Помощник переведён на «только своё»; youtube.com на ПК исключён.
        Assert.True(f.Backend.JointValue.Active);
        Assert.Equal(["youtube.com"], f.Backend.JointValue.Owned);
        Assert.Contains("joint_set True youtube.com", f.Backend.Calls);
        // Оптимизацию на ПК окно не выключает: ПК ведёт свои сайты рядом с роутером.
        Assert.True(vm.IsRunning);
        Assert.DoesNotContain("disable", f.Backend.Calls);
        Assert.False(vm.Router.BothOn);
        Assert.Contains("свои сайты (1)", vm.StatusDetail);
        Assert.Empty(said);
        // Журнал окна: связка на роутере записана.
        Assert.Equal(JointState.Active, vm.Router.Joint!.State);
    }

    [Fact]
    public async Task The_switch_is_remembered_by_the_window_settings_and_comes_back_after_a_restart()
    {
        using var router = NewRouter();
        var shell = new Shell(new AppPaths(_tmp.Sub("root-keep"), Repo.Data));
        var settings = TestWindow.Settings(shell);
        var (vm, f, _) = await OpenAsync(router, settings: settings, shellIn: shell);
        await using var _ = f;
        Assert.False(settings.JointWanted);
        vm.Router.JointWanted = true;
        await ActiveAsync(vm);
        Assert.True(settings.JointWanted);
        Assert.True(WindowSettings.Load(settings.File).JointWanted);             // записано на диск

        // Окно перезапустили: переключатель включён, а связку первая проверка поднимет сама.
        var (vm2, f2, _) = await OpenAsync(router, pcRunning: true, settings: WindowSettings.Load(settings.File), shellIn: shell);
        await using var _2 = f2;
        Assert.True(vm2.Router.JointWanted);
        await vm2.Router.JointTickAsync();
        await ActiveAsync(vm2);
        Assert.True(vm2.IsRunning);                                               // ПК при перезапуске окна не выключается: роутер уже ведёт, но связка включена
        Assert.True(f2.Backend.JointValue.Active);

        vm2.Router.JointWanted = false;
        await WaitAsync(() => !vm2.Router.JointWanted && router.Links.Count == 0 && !WindowSettings.Load(settings.File).JointWanted);
    }

    [Fact]
    public async Task Enabling_refuses_with_the_reason_when_the_router_cannot_do_it()
    {
        // Старый пакет без pc_link_*.
        using var old = NewRouter();
        old.MethodsJson = FakeRouter.MethodsNoPcLink;
        var (vm, f, said) = await OpenAsync(old);
        await using var _ = f;
        vm.Router.JointWanted = true;
        await WaitAsync(() => said.Count > 0);
        Assert.Equal([JointTexts.FallbackOld], said);
        Assert.False(vm.Router.JointWanted);
        Assert.Empty(old.Links);
        Assert.DoesNotContain(f.Backend.Calls, c => c.StartsWith("joint_set"));

        // Оптимизация на роутере выключена.
        using var off = NewRouter(enabled: false);
        var (vm2, f2, said2) = await OpenAsync(off);
        await using var _2 = f2;
        vm2.Router.JointWanted = true;
        await WaitAsync(() => said2.Count > 0);
        Assert.Equal([JointTexts.FallbackDisabled], said2);
        Assert.False(vm2.Router.JointWanted);
    }

    [Fact]
    public async Task Enabling_without_a_helper_says_so()
    {
        using var router = NewRouter();
        var (vm, f, said) = await OpenAsync(router, helper: false);
        await using var _ = f;
        vm.Router.JointWanted = true;
        await WaitAsync(() => said.Count > 0);
        Assert.Equal([JointTexts.FallbackNotInstalled], said);
        Assert.Empty(router.Links);
    }

    [Fact]
    public async Task Without_own_sites_it_refuses_and_nothing_changes()
    {
        using var router = NewRouter();
        var (vm, f, said) = await OpenAsync(router);
        await using var _ = f;
        f.Backend.JointValue = f.Backend.JointValue with { Own = [], Work = [] };
        vm.Router.JointWanted = true;
        await WaitAsync(() => said.Count > 0);
        Assert.Equal([JointTexts.NoOwnSites], said);
        Assert.Empty(router.Links);
        Assert.False(vm.IsRunning);                                        // отказ ничего не включил
    }

    // ---------- возврат к полной настройке ----------

    [Fact]
    public async Task Router_optimization_off_then_on_pc_goes_full_at_once_and_comes_back()
    {
        using var router = NewRouter();
        var (vm, f, _) = await OpenAsync(router);
        await using var _ = f;
        vm.Router.JointWanted = true;
        await ActiveAsync(vm);

        router.Enabled = false;                                            // на роутере выключили оптимизацию
        await vm.Router.RefreshAsync();
        await WaitAsync(() => vm.Router.JointStatus == JointTexts.FallbackDisabled);
        await vm.Router.JointWhenIdleAsync();
        Assert.False(f.Backend.JointValue.Active);                        // ПК — по полной настройке
        Assert.Empty(router.Links);                                       // связка снята
        Assert.True(vm.Router.JointWanted);                               // переключатель остаётся
        Assert.True(vm.Router.JointIsFallback);
        Assert.True(vm.IsRunning);
        Assert.DoesNotContain("disable", f.Backend.Calls);
        Assert.False(vm.Router.BothOn);                                   // роутер выключен — двойной обработки нет

        router.Enabled = true;
        await vm.Router.RefreshAsync();
        await ActiveAsync(vm);
        Assert.True(f.Backend.JointValue.Active);
        Assert.Single(router.Links);
        Assert.False(vm.Router.BothOn);
    }

    [Fact]
    public async Task Router_gone_says_so_and_the_pc_works_alone()
    {
        using var router = NewRouter();
        var (vm, f, _) = await OpenAsync(router);
        await using var _ = f;
        vm.Router.JointWanted = true;
        await ActiveAsync(vm);

        router.Remove();                                                  // роутер выключен / ноутбук вне дома
        await vm.Router.RefreshAsync();
        await WaitAsync(() => vm.Router.JointStatus == "Роутер не найден — этот ПК работает сам");
        await vm.Router.JointWhenIdleAsync();
        Assert.False(f.Backend.JointValue.Active);
        Assert.True(vm.IsRunning);                                        // и сам ПК не выключается из-за пропавшего роутера
        Assert.DoesNotContain("disable", f.Backend.Calls);
    }

    [Fact]
    public async Task Renewal_failure_and_expired_link_go_full()
    {
        using var router = NewRouter();
        var (vm, f, _) = await OpenAsync(router);
        await using var _ = f;
        vm.Router.JointWanted = true;
        await ActiveAsync(vm);

        router.ExpireLinks();                                             // active: false
        router.PcLinkSetFails = true;                                     // и роутер не принимает связку
        await vm.Router.JointTickAsync();
        await vm.Router.JointWhenIdleAsync();
        Assert.Equal(JointTexts.FallbackRenew, vm.Router.JointStatus);
        Assert.False(f.Backend.JointValue.Active);
        Assert.True(vm.Router.BothOn);                                    // роутер включён, а связка не держится: «включено и там, и там» видно человеку

        router.PcLinkSetFails = false;
        await vm.Router.JointTickAsync();
        await vm.Router.JointWhenIdleAsync();
        Assert.StartsWith("Роутер ведёт", vm.Router.JointStatus);
        Assert.True(f.Backend.JointValue.Active);
    }

    [Fact]
    public async Task New_own_site_or_strategy_goes_to_the_router_at_once_not_in_a_minute()
    {
        using var router = NewRouter();
        var (vm, f, _) = await OpenAsync(router);
        await using var _ = f;
        vm.Router.JointWanted = true;
        await ActiveAsync(vm);
        Assert.Equal(1, router.PcLinkSetCalls);

        // Свой сайт добавили, а у стратегии другие приманки (имена из файлов её фейков).
        f.Backend.JointValue = f.Backend.JointValue with { Own = ["mysite.example", "youtube.com", "new.example"], Work = ["mysite.example", "youtube.com", "new.example"], Decoys = ["ya.ru"] };
        vm.Router.JointRecheck();
        await WaitAsync(() => router.PcLinkSetCalls == 2);
        await vm.Router.JointWhenIdleAsync();
        Assert.Equal(["ya.ru", "mysite.example", "new.example"], router.LastPcLinkSet!["domains"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal("Роутер ведёт YouTube и Discord, этот ПК — свои сайты (2)", vm.Router.JointStatus);
    }

    [Fact]
    public async Task A_changed_adapter_sends_the_link_again_with_the_new_mac()
    {
        using var router = NewRouter();
        var (vm, f, _) = await OpenAsync(router);
        await using var _ = f;
        vm.Router.JointWanted = true;
        await ActiveAsync(vm);
        Assert.Equal([Mac], router.LastPcLinkSet!["macs"]!.AsArray().Select(x => x!.GetValue<string>()));

        vm.Router.OwnMac = _ => "aa:bb:cc:dd:ee:77";                       // Windows дала Wi-Fi другой MAC в другой сети
        vm.Router.JointRecheck();
        await WaitAsync(() => router.PcLinkSetCalls == 2);
        await vm.Router.JointWhenIdleAsync();
        Assert.Equal(["aa:bb:cc:dd:ee:77"], router.LastPcLinkSet!["macs"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(["aa:bb:cc:dd:ee:77"], router.Links[PcId].Macs);
    }

    // ---------- выключение, «Удалить полностью», «Отменить всё» ----------

    [Fact]
    public async Task Switching_off_turns_the_pc_off_first_when_the_router_leads_then_deletes_the_link()
    {
        using var router = NewRouter();
        var (vm, f, _) = await OpenAsync(router);
        await using var _ = f;
        vm.Router.JointWanted = true;
        await ActiveAsync(vm);

        vm.Router.JointWanted = false;
        await WaitAsync(() => router.PcLinkDelCalls > 0 && !vm.Router.JointWanted && vm.Router.JointAvailable);
        Assert.Empty(router.Links);
        Assert.False(f.Backend.JointValue.Active);
        var calls = f.Backend.Calls.ToList();
        Assert.True(calls.IndexOf("disable") >= 0, "оптимизацию ведёт роутер: на ПК она выключается, как до совместной работы");
        Assert.True(calls.IndexOf("disable") < calls.LastIndexOf("joint_set False "), "сначала ПК, потом связка — полной настройки рядом с роутером нет");
        Assert.False(vm.Router.HasJointStatus);
    }

    [Fact]
    public async Task Remove_service_deletes_the_link_and_turns_the_switch_off()
    {
        using var router = NewRouter();
        var (vm, f, _) = await OpenAsync(router);
        await using var _ = f;
        vm.Router.JointWanted = true;
        await ActiveAsync(vm);

        vm.RemoveServiceCmd.Execute(null);
        await WaitAsync(() => f.Backend.Calls.Contains("remove_all"));
        Assert.Empty(router.Links);
        Assert.Equal(1, router.PcLinkDelCalls);
        Assert.False(vm.Router.JointWanted);
        Assert.Contains("joint_set False ", f.Backend.Calls);
    }

    [Fact]
    public async Task Undo_of_the_router_entry_deletes_the_link()
    {
        using var router = NewRouter();
        var (vm, f, _) = await OpenAsync(router);
        await using var _ = f;
        vm.Router.JointWanted = true;
        await ActiveAsync(vm);

        var entry = new ChangeEntry("e1", DateTime.UtcNow, ChangeKinds.RouterJoint, JointTexts.RouterJournalTitle, new JsonObject(), new JsonObject { ["id"] = PcId }, ChangeState.Active, true);
        var outcome = await vm.Router.UndoJointAsync(entry);
        Assert.Equal(UndoStatus.Restored, outcome.Status);
        Assert.Empty(router.Links);
        Assert.False(vm.Router.JointWanted);
        Assert.False(f.Backend.JointValue.Active);

        // Повторный откат: делать нечего.
        Assert.Equal(UndoStatus.AlreadyBack, (await vm.Router.UndoJointAsync(entry)).Status);
    }

    // ---------- ПК включают при включённой связке ----------

    [Fact]
    public async Task Turning_the_pc_on_while_joint_is_on_raises_the_link_before_winws_starts()
    {
        using var router = NewRouter();
        var (vm, f, _) = await OpenAsync(router, pcRunning: false);
        await using var _ = f;
        // Роутер ведёт, на ПК выключено: переключатель сам включает ПК после связки.
        vm.Router.JointWanted = true;
        await ActiveAsync(vm);
        var calls = f.Backend.Calls.ToList();
        Assert.True(calls.IndexOf("joint_set True youtube.com") < calls.IndexOf("enable alt9"), "связка раньше запуска winws: " + string.Join(" | ", calls));
        Assert.Single(router.Links);
    }

    // ---------- «Включить» на ПК при работающем роутере: «Работать вместе» (ответ владельца к п. 1) ----------

    [Fact]
    public async Task Enable_with_router_on_offers_work_together_first_and_it_enables_joint_and_the_pc()
    {
        using var router = NewRouter();
        var (vm, f, _) = await OpenAsync(router, pcRunning: false);
        await using var _ = f;
        ChoiceRequest? asked = null;
        vm.ChooseDetailed = r => { asked = r; return (int)MainViewModel.RouterChoice.Joint; };
        await vm.EnableAsync();
        Assert.NotNull(asked);
        Assert.Equal([L.T("router.choice.joint"), L.T("router.choice.select_router"), L.T("router.choice.only_pc")], asked!.Options.Select(o => o.Title));
        Assert.True(asked.Options[0].Enabled);
        Assert.All(asked.Options, o => Assert.NotEmpty(o.Detail));
        Assert.Equal(L.T("router.both.footer"), asked.Footer);
        await ActiveAsync(vm);
        Assert.True(vm.Router.JointWanted);   // выбор запомнен переключателем «Совместная работа»
        Assert.Contains("enable alt9", f.Backend.Calls);
        Assert.True(router.Enabled);          // роутер не выключали
        // Следующее включение — без вопроса.
        asked = null;
        await vm.DisableAsync();
        await vm.EnableAsync();
        Assert.Null(asked);
    }

    [Fact]
    public async Task Work_together_is_grey_with_a_hint_when_the_pc_has_nothing_to_handle()
    {
        using var router = NewRouter();
        var (vm, f, _) = await OpenAsync(router, pcRunning: false);
        await using var _ = f;
        f.Backend.JointValue = f.Backend.JointValue with { Own = [], Work = [] };
        ChoiceRequest? asked = null;
        vm.ChooseDetailed = r => { asked = r; return -1; };
        await vm.EnableAsync();
        Assert.False(asked!.Options[0].Enabled);
        Assert.Equal(L.T("router.choice.joint.nothing"), asked.Options[0].Shown);
        Assert.False(vm.IsRunning);
        Assert.Empty(router.Links);
    }

    // ---------- строки продвинутого режима ----------

    [Fact]
    public async Task A_pc_row_that_matches_a_router_address_is_marked_as_led_by_the_router()
    {
        using var router = NewRouter();
        var (vm, f, _) = await OpenAsync(router);
        await using var _ = f;
        await vm.AdvancedPc.RefreshAsync(force: true);
        vm.AdvancedPc.AddRowCmd.Execute(null);
        vm.AdvancedPc.Rows[^1].DomainsText = "youtube.com, bank.example";
        vm.AdvancedPc.RefreshHints();
        Assert.DoesNotContain("ведёт роутер", vm.AdvancedPc.Rows[^1].Hint);           // связки нет — подсказки о роутере нет

        vm.Router.JointWanted = true;
        await ActiveAsync(vm);
        vm.AdvancedPc.RefreshHints();
        Assert.Contains("youtube.com — ведёт роутер, на этом ПК не применяется.", vm.AdvancedPc.Rows[^1].Hint);
    }
}
