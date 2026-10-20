using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Helper;
using DpiShell.Core.Router;

namespace DpiShell.Tests;

/// <summary>
/// Вкладка «Роутер» и связь с ПК на фейковом роутере (router-api §5–6, §9): режим, «Сайт не открывается», DNS для всей сети,
/// автоподбор, метки TCP, запрет одновременного обхода ПК и роутера, кандидаты глубокого поиска, flow offload / IPv6 / чужой обход.
/// </summary>
public sealed class RouterWindowFeaturesTests : IDisposable
{
    readonly TempDir _tmp = new();
    readonly List<string> _said = [];

    public void Dispose() => _tmp.Dispose();

    RouterViewModel Make(FakeRouter r)
    {
        var pc = new Shell(new AppPaths(_tmp.Sub("root"), Repo.Data));
        var settings = WindowSettings.Load(Path.Combine(_tmp.Sub("window"), "window.json"));
        return new RouterViewModel(pc, settings)
        {
            AddressAuto = false,
            CustomAddress = r.Address,
            PollInterval = TimeSpan.FromMilliseconds(10),
            CheckTimeout = TimeSpan.FromSeconds(5),
            DnsTimeout = TimeSpan.FromSeconds(5),
            PcTimestamps = () => true,
            Gateway = () => throw new InvalidOperationException("в тестах — только фейковый роутер"),
            Say = s => { lock (_said) _said.Add(s); },
        };
    }

    async Task<RouterViewModel> Connected(FakeRouter r)
    {
        var vm = Make(r);
        Assert.True(await vm.ConnectAsync("secret"));
        return vm;
    }

    static string Method(System.Text.Json.Nodes.JsonObject call) => call["params"]![2]!.GetValue<string>();
    static int Count(FakeRouter r, string method) { lock (r.Calls) return r.Calls.Count(c => Method(c) == method); }
    static System.Text.Json.Nodes.JsonNode Args(FakeRouter r, string method) { lock (r.Calls) return r.Calls.Last(c => Method(c) == method)["params"]![3]!; }

    static async Task Until(Func<bool> cond)
    {
        for (int i = 0; i < 500 && !cond(); i++) await Task.Delay(10);
        Assert.True(cond());
    }

    // ---------- 1. режим ----------

    [Fact]
    public async Task Mode_RadioSwitch_OneModeSet_ThenFromStatus()
    {
        using var r = new FakeRouter();
        var vm = await Connected(r);
        Assert.True(vm.ModeAll);
        Assert.StartsWith("Оптимизация для всех сайтов", vm.ModeHint);
        // Как радиокнопки: отметили одну — снялась другая. Запрос — один.
        vm.ModeLists = true;
        vm.ModeAll = false;
        await Until(() => !vm.Busy && Count(r, "mode_set") == 1);
        Assert.Equal("lists", Args(r, "mode_set")["mode"]!.GetValue<string>());
        Assert.Equal("lists", r.Mode);
        Assert.False(vm.ModeAll);
        Assert.True(vm.ModeLists);
        Assert.Equal(L.T("router.mode.lists_done"), _said[^1]);
        await vm.SetModeAsync(true);
        Assert.True(vm.ModeAll);
        Assert.Equal("all", r.Mode);
    }

    // ---------- 2. «Сайт не открывается» ----------

    [Fact]
    public async Task SiteCheck_OnlyWithoutBypass_PollsThenAddsToExclude()
    {
        using var r = new FakeRouter { SiteCheckRunningPolls = 2 };
        var vm = await Connected(r);
        vm.SiteUrl = "https://www.example.org/page";
        await vm.SiteCheckAsync();
        Assert.Equal("https://www.example.org/page", r.SiteCheckUrl);
        Assert.True(Count(r, "site_check_result") >= 3);
        Assert.Equal("Сайт открывается только без оптимизации", vm.SiteResult);
        Assert.True(vm.SiteSuggestExclude);
        Assert.Equal("С оптимизацией: не открылся (сброс после 16 КБ). Без оптимизации: открылся.", vm.SiteDetail);

        await vm.AddSiteToExcludeAsync();
        Assert.Equal(new[] { "example.com", "www.example.org" }, r.Lists["user-exclude"]);
        Assert.False(vm.SiteSuggestExclude);
        Assert.Equal("www.example.org — добавлен в «Исключения»", vm.SiteResult);
    }

    [Fact]
    public async Task SiteCheck_OpensWithBypass_NoExclude_EmptyAsksForAddress()
    {
        using var r = new FakeRouter { SiteWithBypassOk = true };
        var vm = await Connected(r);
        vm.SiteUrl = "  ";
        await vm.SiteCheckAsync();
        Assert.Equal(0, Count(r, "site_check"));
        Assert.StartsWith("Впишите адрес сайта", vm.SiteResult);

        vm.SiteUrl = "example.com";
        await vm.SiteCheckAsync();
        Assert.Equal("example.com", r.SiteCheckUrl);   // голое имя роутер дополнит сам (§9)
        Assert.Equal("С оптимизацией сайт открывается", vm.SiteResult);
        Assert.False(vm.SiteSuggestExclude);
    }

    // ---------- 3. DNS для всей сети ----------

    [Fact]
    public async Task Dns_StatusEnableRestore_BackgroundJobs()
    {
        using var r = new FakeRouter { DnsJobRunningPolls = 2 };
        var vm = await Connected(r);
        await vm.RefreshRouterPageAsync();
        Assert.StartsWith("Провайдер подменяет адреса сайтов · проверено в ", vm.DnsState);
        Assert.Equal(new[] { "Google: отвечает, 38 мс", "Cloudflare: отвечает, 41 мс" }, vm.DnsServers);
        Assert.False(vm.DnsProtected);

        // «Нет» в вопросе о последствиях — ничего не меняем.
        vm.Confirm = _ => false;
        await vm.DnsEnableAsync();
        Assert.Equal(0, Count(r, "dns_enable"));

        string? asked = null;
        vm.Confirm = q => { asked = q; return true; };
        await vm.DnsEnableAsync();
        Assert.Contains("родительский контроль", asked);
        Assert.Equal("doh", r.DnsMode);
        Assert.True(vm.DnsProtected);
        Assert.Equal(L.T("router.dns.on"), vm.DnsState);
        Assert.Equal(L.T("router.dns.enable_done"), _said[^1]);
        Assert.False(vm.DnsRunning);

        await vm.DnsRestoreAsync();
        Assert.Equal("provider", r.DnsMode);
        Assert.False(vm.DnsProtected);
        Assert.Equal(L.T("router.dns.restore_done"), _said[^1]);

        await vm.DnsAnalyzeAsync();
        Assert.Equal(1, Count(r, "dns_analyze"));
        Assert.Equal(L.T("router.dns.analyze_done"), _said[^1]);
    }

    [Fact]
    public async Task Dns_JobFailed_ShowsWhy_And_WithoutJobAt_StillFinishes()
    {
        using var r = new FakeRouter { DnsJobOk = false, DnsJobAt = false };
        var vm = await Connected(r);
        vm.Confirm = _ => true;
        await vm.RefreshRouterPageAsync();
        await vm.DnsEnableAsync();
        Assert.Equal("provider", r.DnsMode);
        Assert.Equal(L.T("router.dns.job.failed_msg", new { action = L.T("router.dns.job.dns_enable"), message = "Не удалось установить пакет" }), vm.DnsJobText);
        Assert.Equal(vm.DnsJobText, _said[^1]);
    }

    // ---------- 3а. варианты DNS для всей сети (dns_presets / dns_set, §9 2026-10-17) ----------

    [Fact]
    public async Task DnsPresets_ChoosePreset_ThenRestore()
    {
        using var r = new FakeRouter();
        var vm = await Connected(r);
        await vm.RefreshRouterPageAsync();
        Assert.True(vm.DnsPresetsAvailable);
        Assert.False(vm.DnsSingleButton);
        Assert.Equal(["provider", "default", "google", "yandex"], vm.DnsPick.Options.Select(o => o.Id));
        // Подмена найдена, стоит DNS провайдера — в списке сразу рекомендованный Google, «Применить» включает его.
        Assert.Equal("provider", vm.DnsPick.CurrentId);
        Assert.Equal("google", vm.DnsPick.Selected!.Id);
        Assert.True(vm.DnsPick.ApplyCmd.CanExecute(null));
        vm.DnsPick.Selected = vm.DnsPick.Options.Single(o => o.Id == "provider");
        Assert.False(vm.DnsPick.ApplyCmd.CanExecute(null));   // выбрано то, что уже стоит, — «Применить» неактивна
        Assert.False(vm.DnsPick.CanRestore);
        Assert.Contains("реестр запрещённых сайтов", vm.DnsPick.Options.Single(o => o.Id == "yandex").Detail);
        // Описание варианта роутера: оба шаблона у «Google + Cloudflare», кто держит — по id из пресетов ПК, «рекомендуем» у Google.
        var both = vm.DnsPick.Options.Single(o => o.Id == "default");
        Assert.Equal(["https://dns.google/dns-query", "https://cloudflare-dns.com/dns-query"], both.DohAll);
        Assert.Contains(both.Info, l => l.Label == "Оператор" && l.Value == "Google и Cloudflare (США)");
        Assert.Contains(vm.DnsPick.Options.Single(o => o.Id == "yandex").Info, l => l.Label == "Оператор" && l.Value == "Яндекс (Россия)");
        Assert.StartsWith("рекомендуется", vm.DnsPick.Options.Single(o => o.Id == "google").Note);

        string? asked = null;
        vm.Confirm = q => { asked = q; return true; };
        vm.DnsPick.Selected = vm.DnsPick.Options.Single(o => o.Id == "yandex");
        await vm.DnsApplyAsync(vm.DnsPick.Selected);
        Assert.Contains("«Яндекс»", asked);
        Assert.Equal("yandex", Args(r, "dns_set")["preset"]!.GetValue<string>());
        Assert.Equal("yandex", r.DnsPreset);
        Assert.Equal("Сейчас: Яндекс, защищённый", vm.DnsPick.CurrentText);
        Assert.True(vm.DnsPick.CanRestore);
        Assert.Equal("DNS для всей сети: Яндекс.", _said[^1]);

        await vm.DnsRestoreAsync();
        Assert.Equal("provider", r.DnsPreset);
        Assert.False(vm.DnsPick.CanRestore);
    }

    [Fact]
    public async Task DnsPresets_CustomFromWindowJson_SentAsCustom()
    {
        using var r = new FakeRouter();
        var vm = await Connected(r);
        await vm.RefreshRouterPageAsync();
        var pick = vm.DnsPick;
        pick.NewName = "AdGuard"; pick.NewIps = "94.140.14.14, 94.140.15.15"; pick.NewDoh = "https://dns.adguard-dns.com/dns-query";
        pick.AddCustom();
        Assert.Equal("", pick.NewError);
        Assert.Equal("my:AdGuard", pick.Selected!.Id);
        // Свой вариант — в window.json.
        var saved = WindowSettings.Load(Path.Combine(_tmp.Sub("window"), "window.json"));
        Assert.Equal("AdGuard", saved.DnsCustom.Single().Name);

        vm.Confirm = _ => true;
        await vm.DnsApplyAsync(pick.Selected);
        var custom = Args(r, "dns_set")["custom"]!;
        Assert.Equal("AdGuard", custom["title"]!.GetValue<string>());
        Assert.Equal("https://dns.adguard-dns.com/dns-query", custom["doh"]!.GetValue<string>());
        Assert.Equal(["94.140.14.14", "94.140.15.15"], custom["bootstrap"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Null(custom["plain"]);
        Assert.Equal("custom", r.DnsPreset);
        Assert.Equal("my:AdGuard", pick.CurrentId);
        Assert.Equal("Сейчас: AdGuard, защищённый", pick.CurrentText);

        // Без шаблона — обычный DNS (plain).
        pick.NewName = "Свой"; pick.NewIps = "10.0.0.53"; pick.NewDoh = "";
        pick.AddCustom();
        await vm.DnsApplyAsync(pick.Selected!);
        Assert.Equal(["10.0.0.53"], Args(r, "dns_set")["custom"]!["plain"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal("Сейчас: Свой, без шифрования", pick.CurrentText);
    }

    [Fact]
    public async Task DnsPresets_OldRouter_SingleButton()
    {
        using var r = new FakeRouter();
        r.MissingMethods.Add("dns_presets");
        var vm = await Connected(r);
        await vm.RefreshRouterPageAsync();
        Assert.False(vm.DnsPresetsAvailable);
        Assert.True(vm.DnsSingleButton);
        Assert.True(vm.DnsLoaded);
    }

    [Theory]
    [InlineData("", "1.1.1.1", "", "Название")]
    [InlineData("A\"b", "1.1.1.1", "", "Название")]
    [InlineData("Мой", "", "", "один или два")]
    [InlineData("Мой", "1.1.1.1 1.0.0.1 8.8.8.8", "", "один или два")]
    [InlineData("Мой", "dns.google", "", "не адрес IPv4")]
    [InlineData("Мой", "2001:db8::1", "", "не адрес IPv4")]
    [InlineData("Мой", "1.1.1.1", "http://x.example/q", "https://")]
    [InlineData("Google", "1.1.1.1", "", "уже есть")]
    public async Task DnsPicker_CustomRejected(string name, string ips, string doh, string error)
    {
        using var r = new FakeRouter();
        var vm = await Connected(r);
        await vm.RefreshRouterPageAsync();
        var pick = vm.DnsPick;
        int before = pick.Options.Count;
        pick.NewName = name; pick.NewIps = ips; pick.NewDoh = doh;
        pick.AddCustom();
        Assert.Contains(error, pick.NewError);
        Assert.Equal(before, pick.Options.Count);
    }

    // ---------- 4. автоподбор по расписанию ----------

    [Fact]
    public async Task Schedule_AutoSelect_SentAndShown()
    {
        using var r = new FakeRouter();
        var vm = await Connected(r);
        Assert.False(vm.AutoSelect);
        await vm.SetScheduleAsync(true, 3, autoSelect: true);
        Assert.True(Args(r, "schedule_set")["auto_select"]!.GetValue<bool>());
        Assert.True(r.AutoSelect);
        Assert.True(vm.AutoSelect);
        Assert.Equal("Роутер будет проверять сайты каждые 3 часа, а если запасные не помогут — подберёт заново.", _said[^1]);
        // Смена часов не сбрасывает автоподбор.
        await vm.SetScheduleAsync(true, 1, vm.AutoSelect);
        Assert.True(r.AutoSelect);
    }

    // ---------- 5. метки TCP ----------

    [Fact]
    public async Task Timestamps_RouterNeeds_PcOff_EnabledByItself_Once()
    {
        using var r = new FakeRouter { NeedsTcpTimestamps = true };
        var vm = Make(r);
        var asks = new List<bool>();
        vm.PcTimestamps = () => false;
        vm.EnablePcTimestamps = ask => { asks.Add(ask); return Task.FromResult(true); };
        await vm.ConnectAsync("secret");
        Assert.Equal(new[] { false }, asks);   // сам, без вопроса об установке помощника
        Assert.Equal(RouterViewModel.TimestampsOn, vm.TimestampsNote);
        Assert.True(vm.TimestampsDone);
        Assert.False(vm.TimestampsNeedAction);
        await vm.RefreshAsync();
        Assert.Single(asks);
    }

    [Fact]
    public async Task Timestamps_NoHelper_HintAndButton_ThenEnabled()
    {
        using var r = new FakeRouter { NeedsTcpTimestamps = true };
        var vm = Make(r);
        bool helper = false;
        vm.PcTimestamps = () => false;
        vm.EnablePcTimestamps = ask => Task.FromResult(ask && (helper = true));
        await vm.ConnectAsync("secret");
        Assert.True(vm.TimestampsNeedAction);
        // Простыми словами, без «временных меток» (п. 8); на главной — число и текст первого предупреждения (п. 7).
        Assert.Equal(RouterViewModel.TimestampsNeed, vm.TimestampsNote);
        Assert.Equal("· 2 предупреждения", vm.HomeFixText);   // и ускорение сети у фейкового роутера
        Assert.DoesNotContain("Ускорение сети", vm.HomeLine);   // работает — текст предупреждений не на главной
        await vm.CheckTimestampsAsync(ask: true);   // кнопка «Включить»
        Assert.True(helper);
        Assert.False(vm.TimestampsNeedAction);
        Assert.Equal("· 1 предупреждение", vm.HomeFixText);   // счётчик на главной снимается
        Assert.Equal(RouterViewModel.TimestampsOn, vm.TimestampsNote);
    }

    [Fact]
    public async Task Timestamps_NotNeeded_NothingShownNorChecked()
    {
        using var r = new FakeRouter { NeedsTcpTimestamps = false };
        var vm = Make(r);
        vm.PcTimestamps = () => throw new InvalidOperationException("не нужно — не проверяем");
        await vm.ConnectAsync("secret");
        Assert.False(vm.HasTimestampsNote);
    }

    // ---------- 6. ПК и роутер вместе (§6) ----------

    [Fact]
    public async Task RouterEnable_WhilePcOn_AsksAndStopsPcFirst()
    {
        using var r = new FakeRouter { Enabled = false };
        var vm = await Connected(r);
        bool pc = true;
        vm.PcRunning = () => pc;
        vm.StopPc = () => { pc = false; return Task.FromResult(true); };
        vm.Confirm = _ => false;
        await vm.ToggleAsync();
        Assert.True(pc);
        Assert.False(r.Enabled);

        string? asked = null;
        vm.Confirm = q => { asked = q; return true; };
        await vm.ToggleAsync();
        Assert.Equal(L.T("router.enable.confirm"), asked);
        Assert.False(pc);
        Assert.True(r.Enabled);
        Assert.False(vm.BothOn);
    }

    [Fact]
    public async Task BothOn_Warning_StopRouterButton()
    {
        using var r = new FakeRouter();
        var vm = Make(r);
        vm.PcRunning = () => true;
        await vm.ConnectAsync("secret");
        Assert.True(vm.BothOn);
        vm.StopRouterCmd.Execute(null);
        await Until(() => !vm.Busy && !r.Enabled);
        Assert.False(vm.BothOn);
    }

    MainViewModel Main(FakeRouter r)
    {
        var vm = TestWindow.Model(new Shell(new AppPaths(_tmp.Sub("main"), Repo.Data)));
        vm.AskHelper = _ => false;   // без установки помощника и UAC
        vm.Router.PcTimestamps = () => true;
        vm.Router.AddressAuto = false;
        vm.Router.CustomAddress = r.Address;
        vm.Router.PollInterval = TimeSpan.FromMilliseconds(10);
        return vm;
    }

    [Fact]
    public async Task PcEnable_RouterOn_ChoosesSelectOnRouter()
    {
        using var r = new FakeRouter();
        var vm = Main(r);
        await vm.Router.ConnectAsync("secret");
        IReadOnlyList<string>? options = null;
        vm.Choose = (_, o) => { options = o; return 0; };
        await vm.EnableAsync();
        Assert.Equal(new[] { L.T("router.choice.select_router"), L.T("router.choice.swap") }, options);
        Assert.Equal(Pages.Select, vm.Page);
        Assert.True(vm.Router.SelectOnRouter);
        Assert.True(r.Enabled);
    }

    [Fact]
    public async Task PcEnable_RouterOn_DisablesRouterThenEnablesPc()
    {
        using var r = new FakeRouter();
        var vm = Main(r);
        await vm.Router.ConnectAsync("secret");
        vm.Choose = (_, _) => 1;
        await vm.EnableAsync();
        Assert.False(r.Enabled);
        Assert.Equal(1, Count(r, "disable"));
    }

    [Fact]
    public async Task PcSelect_RouterOn_PausedThenAskedToEnableBack()
    {
        using var r = new FakeRouter();
        var vm = Main(r);
        await vm.Router.ConnectAsync("secret");
        IReadOnlyList<string>? options = null;
        vm.Choose = (_, o) => { options = o; return 1; };
        Assert.True(await vm.RouterGateAsync(select: true));
        Assert.Equal("Выключить на роутере на время подбора", options![1]);
        Assert.False(r.Enabled);
        string? asked = null;
        vm.Confirm = q => { asked = q; return true; };
        await vm.RouterResumeAsync();
        Assert.Equal("Подбор закончен. Включить оптимизацию на роутере обратно?", asked);
        Assert.True(r.Enabled);
        Assert.Equal("Оптимизация снова работает на роутере.", vm.Message);
        // Второй раз не спрашивает: роутер выключали один раз.
        asked = null;
        await vm.RouterResumeAsync();
        Assert.Null(asked);
    }

    [Fact]
    public async Task PcEnable_RouterOnWithoutLogin_OffersLogin()
    {
        using var r = new FakeRouter();
        var vm = Main(r);
        await vm.Router.ConnectAsync(null);   // найден, пароля нет
        Assert.True(vm.Router.RouterEnabled);
        Assert.False(vm.Router.Connected);
        IReadOnlyList<string>? options = null;
        vm.Choose = (_, o) => { options = o; return -1; };
        await vm.EnableAsync();
        Assert.Equal(new[] { "Войти на роутер" }, options);
        Assert.Equal(0, Count(r, "disable"));
    }

    [Fact]
    public async Task PcRunning_RouterReportsEnabled_PcSwitchesOff_OrWarnsWithoutHelper()
    {
        using var r = new FakeRouter();
        var vm = Main(r);
        typeof(MainViewModel).GetField("_active", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(vm, true);
        await vm.Router.ConnectAsync("secret");
        // Помощника нет — выключить обход ПК нечем: остаётся плашка «включён и здесь, и на роутере» с двумя кнопками.
        Assert.True(vm.Router.BothOn);
        Assert.NotEqual("Оптимизация перешла на роутер: на этом ПК выключена.", vm.Message);
    }

    // ---------- обход «живёт» на роутере: старт окна, роутер не отвечает (живой случай: обновление роутера) ----------

    /// <summary>Окно с фейковым помощником (служба ПК в заданном состоянии) и роутером по адресу; заставка ещё не снята.</summary>
    async Task<(MainViewModel Vm, HelperFixture Helper, WindowSettings Settings)> MainWithHelper(string routerAddress, HelperStatus pc, RouterBypassMark? mark, string? macNow)
    {
        var f = new HelperFixture();
        f.Backend.StatusValue = pc;
        var shell = new Shell(new AppPaths(_tmp.Sub("main"), Repo.Data));
        var settings = TestWindow.Settings(shell);
        settings.RouterBypass = mark;
        var vm = TestWindow.Model(shell, await f.LinkAsync(), settings);
        vm.AskHelper = _ => false;
        vm.Router.PcTimestamps = () => true;
        vm.Router.AddressAuto = false;
        vm.Router.CustomAddress = routerAddress;
        vm.Router.PollInterval = TimeSpan.FromMilliseconds(10);
        vm.Router.MacOf = _ => macNow;
        return (vm, f, settings);
    }

    static string DeadAddress()
    {
        using var gone = new FakeRouter();
        return gone.Address;   // роутер «выключен»: по адресу никто не отвечает
    }

    static HelperStatus Pc(bool active, string mode = "service") =>
        FakeHelperBackend.DefaultStatus with { Enabled = true, Active = active, RunMode = mode, Service = active ? SvcState.Running : SvcState.Stopped };

    [Fact]
    public async Task Router_On_RemembersBypass_Off_Forgets()
    {
        using var r = new FakeRouter();
        var vm = Main(r);
        vm.Router.MacOf = _ => "AABBCCDDEEFF";
        await vm.Router.ConnectAsync("secret");
        await Until(() => vm.Router.BypassRemembered && Settings(vm).RouterBypass?.Mac is not null);
        Assert.Equal(new RouterBypassMark(r.Address, "AABBCCDDEEFF"), Settings(vm).RouterBypass);
        Assert.True(await vm.Router.DisableAsync());
        Assert.Null(Settings(vm).RouterBypass);
    }

    static WindowSettings Settings(MainViewModel vm) =>
        (WindowSettings)typeof(RouterViewModel).GetField("_settings", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(vm.Router)!;

    [Theory]
    [InlineData("AABBCCDDEEFF", true)]    // тот же роутер, просто не отвечает (обновляется) — служба ПК, поднявшаяся с Windows, выключается
    [InlineData("001122334455", false)]   // другая сеть с тем же адресом шлюза — оптимизация ПК не трогаем
    [InlineData(null, false)]             // MAC не узнать — не уверены, не трогаем
    public async Task Start_ServiceUp_RouterAbsent_SameRouter_PcOff(string? macNow, bool off)
    {
        var address = DeadAddress();
        var (vm, f, _) = await MainWithHelper(address, Pc(active: true), new RouterBypassMark(address, "AABBCCDDEEFF"), macNow);
        await using var _f = f;
        await vm.InitializeAsync();
        if (off) await Until(() => vm.Message == L.T("router.pc_off_router_silent"));
        else await Task.Delay(300);
        Assert.Equal(off, f.Backend.Calls.Contains("disable"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Start_ProcessMode_RouterAbsent_NotResumedOnPc_UnlessOtherNetwork(bool sameRouter)
    {
        var address = DeadAddress();
        var (vm, f, _) = await MainWithHelper(address, Pc(active: false, mode: "process"), new RouterBypassMark(address, "AABBCCDDEEFF"),
            sameRouter ? "AABBCCDDEEFF" : "001122334455");
        await using var _f = f;
        await vm.InitializeAsync();
        await Until(() => f.Backend.Calls.Contains("disable") || f.Backend.Calls.Contains("enable alt9"));
        Assert.Equal(sameRouter, f.Backend.Calls.Contains("disable"));
        Assert.Equal(!sameRouter, f.Backend.Calls.Contains("enable alt9"));   // в гостях — как раньше: включён был — включается
    }

    [Fact]
    public async Task Start_ProcessMode_NoRouterMemory_ResumesAsBefore()
    {
        var (vm, f, _) = await MainWithHelper(DeadAddress(), Pc(active: false, mode: "process"), null, null);
        await using var _f = f;
        await vm.InitializeAsync();
        Assert.Contains("enable alt9", f.Backend.Calls);
    }

    [Fact]
    public async Task PcStateArrivesAfterRouter_On_PcSwitchesOff()
    {
        using var r = new FakeRouter();
        var (vm, f, _) = await MainWithHelper(r.Address, Pc(active: false), null, null);
        await using var _f = f;
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsLoading))!.SetValue(vm, false);
        await vm.Router.ConnectAsync("secret");   // роутер «включён», а ПК ещё не знаем (выключен)
        Assert.DoesNotContain("disable", f.Backend.Calls);
        f.Backend.StatusValue = Pc(active: true);  // служба ПК поднялась с Windows
        await vm.RefreshStatusAsync();
        await Until(() => f.Backend.Calls.Contains("disable"));
        await Until(() => vm.Message == "Оптимизация перешла на роутер: на этом ПК выключена.");
    }

    [Fact]
    public async Task PcEnable_RouterAbsent_ForgetsRouterBypass()
    {
        var address = DeadAddress();
        var (vm, f, settings) = await MainWithHelper(address, Pc(active: false), new RouterBypassMark(address, "AABBCCDDEEFF"), null);
        await using var _f = f;
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsLoading))!.SetValue(vm, false);
        await vm.RefreshStatusAsync();
        await vm.EnableAsync();
        Assert.Null(settings.RouterBypass);   // человек выбрал оптимизация здесь — при следующем старте его не выключать
    }

    // ---------- 7. глубокий поиск на роутере ----------

    [Fact]
    public async Task DeepSearch_CandidatesMarked_FoundInStrategies()
    {
        using var r = new FakeRouter
        {
            SelectSteps = 1,
            BestId = "r-261017-1",
            SelectRowsJson = """
                [ { "id": "alt", "title": "general (ALT)", "group_share": 1.0, "target_share": 0.88, "avg_s": 0.74, "error": null },
                  { "id": "c-3", "title": "fake + multisplit, ts — YouTube", "group_share": 0, "target_share": 0, "avg_s": 0.5, "error": null,
                    "candidate": true, "ok": 3, "total": 4 },
                  { "id": "r-261017-1", "title": "fake + multisplit, ts", "group_share": 1.0, "target_share": 0.95, "avg_s": 0.6, "error": null } ]
                """,
            StrategiesJson = """
                { "catalog_version": 3, "items": [ { "id": "alt10", "title": "general (ALT10)", "source": "catalog", "runs": 7, "passed": 6 },
                  { "id": "r-261017-1", "title": "fake + multisplit, ts", "source": "user", "runs": 1, "passed": 1,
                    "last": { "at": "2026-10-17T11:30:00Z", "ok": false, "failed": ["Discord"] },
                    "roles": { "general": "--dpi-desync=fake --dpi-desync-fake-tls={fake}/stun.bin", "quic": "--dpi-desync=fake" } },
                  { "id": "u-found-1", "title": "С ПК", "source": "user", "runs": 0, "passed": 0 } ] }
                """,
        };
        var vm = await Connected(r);
        await vm.StartSelectAsync(deep: true);
        var cand = vm.Scores.Single(s => s.Id == "c-3");
        Assert.True(cand.IsCandidate);
        Assert.Equal("проверяется в поиске", cand.Kind);
        Assert.Equal("75 %", cand.Share);   // процент, как у остальных строк столбца «Открылось»
        // Группы — как на ПК: «N из M», когда роутер прислал разбивку по адресам (M — группы в ней).
        var withVerdicts = new RouterSelectRow("x", 0.5, 0.75, 0.6, null, Verdicts: [new("youtube", "https://a/", true), new("discord", "https://b/", false)]);
        Assert.Equal("1 из 2", new RouterScoreRow(withVerdicts, "x", null, 2).Groups);
        Assert.Equal("50 %", new RouterScoreRow(withVerdicts, "x").Groups);   // без разбивки числа групп нет — доля
        Assert.False(cand.Usable);   // применить вариант нельзя — его нет в strategies
        Assert.Equal("c-3", vm.Scores[^1].Id);   // варианты — после стратегий
        Assert.Equal("Включена на роутере: fake + multisplit, ts", vm.ResultText);
        // Только последняя проверка, без истории «удачных N из M»; без last — ничего о проверках.
        var at = new DateTime(2026, 10, 17, 11, 30, 0, DateTimeKind.Utc).ToLocalTime();
        Assert.Equal($"найдена на роутере · ✗ проверено {at:dd.MM} в {at:HH:mm}, не открылось: Discord", vm.Strategies.Single(s => s.Id == "r-261017-1").Stats);
        Assert.Equal("", vm.Strategies.Single(s => s.Id == "alt10").Stats);
        // Роли — только если роутер их отдал; плейсхолдер как есть, порядок — как в командной строке.
        var found = vm.Strategies.Single(s => s.Id == "r-261017-1");
        Assert.Equal("Параметры по ролям:\nquic: --dpi-desync=fake\ngeneral: --dpi-desync=fake --dpi-desync-fake-tls={fake}/stun.bin", found.Params);
        Assert.False(vm.Strategies.Single(s => s.Id == "alt10").HasParams);
        string? copied = null;
        vm.CopyText = t => copied = t;
        vm.CopyParams(found);
        Assert.Equal("fake + multisplit, ts\n" + found.Params, copied);
        Assert.Equal("с ПК", vm.Strategies.Single(s => s.Id == "u-found-1").Stats);
        vm.SelectedScore = cand;
        Assert.False(vm.ApplySelectedCmd.CanExecute(null));
    }

    // ---------- договор §9: flow offload, IPv6, чужой обход ----------

    [Fact]
    public async Task Warnings_FlowOffloadAndIpv6_FixCallsRouter()
    {
        using var r = new FakeRouter
        {
            WarningsJson = """[ { "code": "flow_offload", "text": "Включён flow offloading" }, { "code": "ipv6", "text": "IPv6 мимо оптимизации" }, { "code": "switched_backup", "text": "Стратегия перестала работать — включена запасная alt9" } ]""",
        };
        var vm = await Connected(r);
        Assert.Equal(new[] { "Исправить", "Исправить", null }, vm.WarningRows.Select(w => w.Fix));
        Assert.Equal("Стратегия перестала работать — включена запасная alt9", vm.WarningRows[2].Text);
        Assert.True(vm.FlowOffloadFixable);
        await vm.FixWarningAsync("flow_offload");
        Assert.True(r.FlowOffloadFixed);
        Assert.Equal("Готово: ускорение сети работает вместе с оптимизацией; скорость обычно почти не меняется.", _said[^1]);
        Assert.Empty(vm.WarningRows);   // роутер больше не предупреждает

        r.WarningsJson = """[ { "code": "ipv6", "text": "IPv6 мимо оптимизации" } ]""";
        await vm.RefreshAsync();
        await vm.FixWarningAsync("ipv6");
        Assert.True(r.Ipv6Enabled);
        Assert.True(Args(r, "ipv6_set")["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Conflict_OnEnable_ClearText_PackageHasNoStop()
    {
        using var r = new FakeRouter
        {
            Enabled = false,
            ConflictOnEnable = true,
            ConflictsJson = """[ { "kind": "package", "name": "zapret-manager-beam", "running": true }, { "kind": "service", "name": "zapret2", "running": true } ]""",
            WarningsJson = """[ { "code": "conflict", "text": "Работает чужой оптимизация" } ]""",
        };
        var vm = await Connected(r);
        await vm.ToggleAsync();
        Assert.Equal(RouterTexts.Conflict, _said[^1]);
        Assert.False(r.Enabled);
        Assert.Equal("Подробнее", vm.WarningRows.Single().Fix);

        int page = -1;
        vm.Navigate = p => page = p;
        await vm.FixWarningAsync("conflict");   // «Подробнее» — к «Роутер: что мешает» (теперь на вкладке «Роутер»)
        Assert.Equal(Pages.Router, page);
        var pkg = vm.Conflicts.Single(c => c.Name == "zapret-manager-beam");
        Assert.False(pkg.CanStop);
        Assert.Contains("удалите его", pkg.Detail);
        Assert.True(vm.Conflicts.Single(c => c.Name == "zapret2").CanStop);
    }

    /// <summary>«Роутер: что мешает» живёт на «Роутере»: вход на вкладку читает diag; на «Диагностике» ПК роутер больше не опрашивается.</summary>
    [Fact]
    public async Task RouterTab_Entry_RefreshesRouterDiag_DiagTabDoesNot()
    {
        using var r = new FakeRouter { ConflictsJson = """[ { "kind": "service", "name": "zapret2", "running": true } ]""" };
        var vm = Main(r);
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsLoading))!.SetValue(vm, false);
        await vm.Router.ConnectAsync("secret");
        Assert.False(vm.Router.DiagLoaded);
        int before = Count(r, "diag");

        vm.Page = Pages.Diag;
        await Task.Delay(300);
        Assert.Equal(before, Count(r, "diag"));
        Assert.False(vm.Router.DiagLoaded);

        vm.Page = Pages.Router;
        await Until(() => vm.Router.DiagLoaded);
        Assert.Equal("zapret2", vm.Router.Conflicts.Single().Name);
        Assert.True(Count(r, "diag") > before);
    }

    /// <summary>«Обновить программу» с «Роутера» — ссылка на «Информация и обновления», где обновление; страницу выпусков команда по-прежнему открывает.</summary>
    [Fact]
    public async Task UpdateApp_LinkLeadsToSettings()
    {
        using var r = new FakeRouter();
        var vm = await Connected(r);
        int page = -1;
        vm.Navigate = p => page = p;
        vm.GoUpdatesCmd.Execute(null);
        Assert.Equal(Pages.About, page);
    }

    // ---------- свои сайты роутера пусты (router-api §5.2) ----------

    [Fact]
    public async Task SelectOnlyUser_RouterListEmpty_OffersPcTargets_SendsAndRetries()
    {
        using var r = new FakeRouter { UserTargets = [] };
        var vm = await Connected(r);
        vm.SelectOptions = () => (["user"], true);
        vm.PcUserTargets = () => ["example.org", "news.example.net"];
        string? asked = null;
        vm.Confirm = q => { asked = q; return true; };

        await vm.StartSelectAsync(deep: false);

        Assert.Equal(L.T("router.targets.offer", new { count = 2 }), asked);
        Assert.Equal(2, Count(r, "select_start"));          // отказ, отправка, повтор
        Assert.Equal(1, Count(r, "targets_set"));
        Assert.Equal(["https://example.org/", "https://news.example.net/"], r.UserTargets.Select(t => t.Url));
        Assert.Equal(2, vm.Targets.Count);
        Assert.StartsWith("Включена на роутере: ", vm.ResultText);
    }

    [Fact]
    public async Task SelectOnlyUser_KnownEmpty_AsksBeforeStart_DeclineSendsNothing()
    {
        using var r = new FakeRouter { UserTargets = [] };
        var vm = await Connected(r);
        await vm.LoadTargetsAsync();                         // список роутера известен: пуст
        Assert.Empty(vm.Targets);
        vm.SelectOptions = () => (["user"], true);
        vm.PcUserTargets = () => ["example.org"];
        int asks = 0;
        vm.Confirm = _ => { asks++; return false; };

        await vm.StartSelectAsync(deep: false);

        Assert.Equal(1, asks);
        Assert.Equal(0, Count(r, "select_start"));           // спросили заранее, без лишнего отказа роутера
        Assert.Equal(0, Count(r, "targets_set"));
        Assert.Contains("нет своих сайтов", _said[^1]);
    }

    /// <summary>Выбор «только Свои сайты» остался от прежней установки, а адресов нет нигде — снять их и подобрать по умолчанию.</summary>
    [Fact]
    public async Task SelectOnlyUser_EmptyEverywhere_DropsUser_SelectsDefaults()
    {
        using var r = new FakeRouter { UserTargets = [] };
        var vm = await Connected(r);
        vm.SelectOptions = () => (["user"], true);
        IReadOnlyList<string>? marked = null;
        vm.SetSelectGroups = g => marked = g;
        vm.Confirm = _ => throw new InvalidOperationException("на ПК адресов нет — спрашивать нечего");

        await vm.StartSelectAsync(deep: false);

        Assert.Equal(2, Count(r, "select_start"));          // отказ роутера, повтор без «Своих сайтов»
        Assert.Equal(["youtube", "discord"], Args(r, "select_start")["groups"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(["youtube", "discord"], marked);
        Assert.Contains(_said, s => s.Contains("Скопировать с ПК"));
        Assert.StartsWith("Включена на роутере: ", vm.ResultText);
    }

    [Fact]
    public async Task SelectOnlyUser_KnownEmptyEverywhere_DropsUserBeforeStart()
    {
        using var r = new FakeRouter { UserTargets = [] };
        var vm = await Connected(r);
        await vm.LoadTargetsAsync();
        vm.SelectOptions = () => (["user"], true);
        IReadOnlyList<string>? marked = null;
        vm.SetSelectGroups = g => marked = g;

        await vm.StartSelectAsync(deep: false);

        Assert.Equal(1, Count(r, "select_start"));          // без лишнего отказа роутера
        Assert.Equal(["youtube", "discord"], marked);
    }

    /// <summary>Удалили DpiShell с роутера и поставили заново — выбор групп и свои сайты прежнего роутера забыты.</summary>
    [Fact]
    public async Task ForgetRouterSetup_ResetsGroupsAndTargets()
    {
        using var r = new FakeRouter();
        var vm = await Connected(r);
        await vm.LoadTargetsAsync();
        Assert.NotEmpty(vm.Targets);
        IReadOnlyList<string>? marked = null;
        vm.SetSelectGroups = g => marked = g;

        vm.ForgetRouterSetup();

        Assert.Empty(vm.Targets);
        Assert.Equal(["youtube", "discord"], marked);
    }

    [Fact]
    public async Task Client_FlowOffloadFixAndIpv6Set()
    {
        using var r = new FakeRouter();
        using var c = new RouterClient(r.Address);
        await c.LoginAsync(() => r.Password);
        var fix = await c.FlowOffloadFixAsync();
        Assert.True(fix.Changed);
        Assert.Equal("software", fix.Flowoffload);
        await c.Ipv6SetAsync(false);
        Assert.False(r.Ipv6Enabled);
        var row = (await c.SelectProgressAsync()).Rows![0];
        Assert.False(row.Candidate);
        Assert.Null(row.Total);
    }
}
