using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Helper;
using DpiShell.Core.Router;

namespace DpiShell.Tests;

/// <summary>
/// UX-аудит 7 («первый запуск обычным человеком»): строка «стало ли лучше» после включения, «Сайт не открывается?» на Главной и простой список
/// «Открывать без оптимизации» на ПК, «Проверить, подходит ли мой роутер», «Установить» недоступна у неподходящего роутера, «Включить оптимизацию» в «Играх».
/// Помощник — фейк по каналу, проверка сайтов и роутер — подмены без сети.
/// </summary>
public sealed class FirstRunAuditTests : IDisposable
{
    readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    Shell NewShell() => new(new AppPaths(_tmp.Sub("root"), Repo.Data));

    MainViewModel Model(HelperLink link)
    {
        var vm = TestWindow.Model(NewShell(), link);
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsLoading))!.SetValue(vm, false);
        return vm;
    }

    static HelperStatus Status(bool active) => FakeHelperBackend.DefaultStatus with { Enabled = active, Active = active };

    /// <summary>YouTube открывается всегда, остальное — когда «on».</summary>
    static Func<IReadOnlyList<TargetGroup>, Task<IReadOnlyList<TargetVerdict>>> Probe(Func<bool> on, Action? called = null) => groups =>
    {
        called?.Invoke();
        return Task.FromResult<IReadOnlyList<TargetVerdict>>(groups.SelectMany(g => g.Targets.Select(t =>
            new TargetVerdict(g.Id, t, g.Id == "youtube" || on(), 0.3, []))).ToList());
    };

    static async Task Until(Func<bool> cond, string what)
    {
        for (int i = 0; i < 500 && !cond(); i++) await Task.Delay(10);
        Assert.True(cond(), what);
    }

    // ---------- №1: после «Включить» — «Открывается N из M» и «было K из M» ----------

    [Fact]
    public async Task AfterEnable_SummaryShows_WhatItWasBefore()
    {
        await using var f = new HelperFixture();
        f.Backend.StatusValue = Status(active: false);
        var vm = Model(await f.LinkAsync());
        bool on = false;
        vm.ProbeGroups = Probe(() => on);
        await vm.RefreshStatusAsync();
        foreach (var g in vm.Groups) g.IsChecked = g.Id is "youtube" or "discord";
        await vm.CheckNowAsync();
        Assert.Equal("Открывается 1 из 2", vm.CheckSummary);
        Assert.DoesNotContain("было", vm.CheckedAt);   // до включения сравнивать не с чем

        on = true;
        f.Backend.StatusValue = Status(active: true);
        await vm.EnableAsync();
        Assert.Equal("Открывается 2 из 2", vm.CheckSummary);
        Assert.EndsWith(" · было 1 из 2", vm.CheckedAt);

        // Повторная проверка в том же состоянии — «было» уже не про включение.
        await vm.CheckNowAsync();
        Assert.DoesNotContain("было", vm.CheckedAt);
    }

    /// <summary>Оптимизация заработала не от «Включить» (совместная работа, служба, роутер): проверка идёт сама, один раз.</summary>
    [Fact]
    public async Task WhenOptimizationStartsElsewhere_CheckRunsItself_Once()
    {
        await using var f = new HelperFixture();
        f.Backend.StatusValue = Status(active: false);
        var vm = Model(await f.LinkAsync());
        vm.AutoCheck = true;
        int probes = 0;
        vm.ProbeGroups = Probe(() => true, () => probes++);
        await vm.RefreshStatusAsync();
        foreach (var g in vm.Groups) g.IsChecked = g.Id is "youtube" or "discord";
        await Task.Delay(50);
        Assert.Equal(0, probes);   // выключено — проверять нечего
        Assert.False(vm.HasCheck);

        f.Backend.StatusValue = Status(active: true);
        await vm.RefreshStatusAsync();
        await Until(() => vm.HasCheck && !vm.Checking && vm.CheckSummary.StartsWith("Открывается"), "после включения проверка не пошла сама");
        Assert.Equal("Открывается 2 из 2", vm.CheckSummary);
        int after = probes;
        Assert.True(after >= 1);

        // Ещё обновления состояния — новых проверок нет.
        await vm.RefreshStatusAsync();
        await vm.RefreshStatusAsync();
        await Task.Delay(50);
        Assert.Equal(after, probes);
    }

    [Fact]
    public async Task WithoutAutoCheck_NothingRunsBehindTheScenes()
    {
        await using var f = new HelperFixture();
        f.Backend.StatusValue = Status(active: true);
        var vm = Model(await f.LinkAsync());
        int probes = 0;
        vm.ProbeGroups = Probe(() => true, () => probes++);
        await vm.RefreshStatusAsync();
        await Task.Delay(50);
        Assert.Equal(0, probes);
        Assert.False(vm.HasCheck);
    }

    // ---------- №2–3: главная ----------

    [Fact]
    public async Task Home_SiteFixLink_OnlyWhileRunning_AndRouterLinkOnlyWithRouter()
    {
        await using var f = new HelperFixture();
        f.Backend.StatusValue = Status(active: false);
        var vm = Model(await f.LinkAsync());
        await vm.RefreshStatusAsync();
        Assert.False(vm.ShowSiteFix);
        Assert.False(vm.ShowRouterSelectLink);   // без роутера ссылка «Подобрать на роутере» ничего не говорит
        f.Backend.StatusValue = Status(active: true);
        await vm.RefreshStatusAsync();
        Assert.True(vm.ShowSiteFix);
    }

    [Fact]
    public void DnsReason_IsPlainRussian_NoJargon()
    {
        Assert.Equal("провайдер подсовывает не тот адрес", BrowserPath.DnsReason);
        Assert.DoesNotContain("DNS", BrowserPath.DnsReason);
    }

    // ---------- №5 (аудит): «Открыть без оптимизации» ----------

    [Fact]
    public async Task OpenDirect_AddsSiteToPcExclusions_KeepingTheRest()
    {
        await using var f = new HelperFixture();
        f.Backend.StatusValue = Status(active: true) with { UserExclude = ["old.example"] };
        var vm = Model(await f.LinkAsync());
        await vm.RefreshStatusAsync();
        vm.ApplyHelperData();
        Assert.Equal("old.example", vm.ExcludeText);

        vm.DirectSite = "https://Bank.Example/login";
        await vm.OpenDirectAsync();

        Assert.Contains("user_exclude_set old.example,bank.example", f.Backend.Calls);
        Assert.Contains("bank.example", vm.DirectMessage);
        Assert.Equal(L.T("home.direct.done_pc", new { site = "bank.example" }), vm.DirectMessage);
        Assert.DoesNotContain("обход", vm.DirectMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("bank.example", vm.DirectMessage.Split(' ')[0]);
        Assert.Equal("old.example" + Environment.NewLine + "bank.example", vm.ExcludeText);
        Assert.False(vm.ExcludeDirty);
        Assert.Equal("", vm.DirectSite);
        // Строки «Напрямую» продвинутого режима не тронуты.
        Assert.DoesNotContain(f.Backend.Calls, c => c.StartsWith("advanced_set"));
    }

    [Fact]
    public async Task OpenDirect_BadAddress_SaysSo_AndChangesNothing()
    {
        await using var f = new HelperFixture();
        f.Backend.StatusValue = Status(active: true);
        var vm = Model(await f.LinkAsync());
        await vm.RefreshStatusAsync();
        vm.DirectSite = "не адрес";
        await vm.OpenDirectAsync();
        Assert.StartsWith("Впишите адрес сайта", vm.DirectMessage);
        Assert.DoesNotContain(f.Backend.Calls, c => c.StartsWith("user_exclude_set"));
    }

    [Fact]
    public async Task SitesTab_ExcludeList_SavedWithTheSingleSaveButton_AndCountsAsDirty()
    {
        await using var f = new HelperFixture();
        f.Backend.StatusValue = Status(active: false);
        var vm = Model(await f.LinkAsync());
        await vm.RefreshStatusAsync();
        vm.ApplyHelperData();
        Assert.False(vm.SitesDirty);

        vm.ExcludeText = "bank.example\ngov.example";
        Assert.True(vm.ExcludeDirty);
        Assert.True(vm.SitesDirty);
        Assert.False(vm.SitesSaved);

        vm.SaveSitesCmd.Execute(null);
        await Until(() => !vm.SitesDirty, "«Сохранить» не сохранило список исключений");
        Assert.Contains("user_exclude_set bank.example,gov.example", f.Backend.Calls);
        Assert.False(vm.SitesDirty);
        Assert.StartsWith("Сохранено сайтов без оптимизации: 2", vm.ExcludeMessage);
    }

    [Fact]
    public async Task SitesTab_TooLongExcludeList_IsRefusedWithReason()
    {
        await using var f = new HelperFixture();
        f.Backend.StatusValue = Status(active: false);
        var vm = Model(await f.LinkAsync());
        await vm.RefreshStatusAsync();
        vm.ExcludeText = string.Join("\n", Enumerable.Range(0, AdvancedSites.MaxExclude + 1).Select(i => $"s{i}.example"));
        vm.SaveSitesCmd.Execute(null);
        await Until(() => vm.ExcludeMessage.Contains(AdvancedSites.MaxExclude.ToString()), "нет причины отказа");
        Assert.Contains(AdvancedSites.MaxExclude.ToString(), vm.Message);
        Assert.True(vm.ExcludeDirty);   // список не принят — «Не сохранено» остаётся
        Assert.DoesNotContain(f.Backend.Calls, c => c.StartsWith("user_exclude_set"));
    }

    // ---------- ядро: список исключений ПК попадает во все роли ----------

    static readonly AppPaths P = new(@"C:\Root", @"C:\Seed");

    [Fact]
    public void Build_UserExclude_GoesToEveryCatalogRole_AndEmptyListChangesNothing()
    {
        var s = Make.Strategy(("google", "--dpi-desync=O"), ("discord_media", "--dpi-desync=M"), ("general", "--dpi-desync=G"), ("quic", "--dpi-desync=Q"));
        var plain = WinwsArgs.Build(s, P).ToList();
        Assert.DoesNotContain(plain, a => a.StartsWith("--hostlist-exclude-domains"));
        Assert.Equal(plain, WinwsArgs.Build(s, P, BuildOptions.From(new AppState(), DateTime.UtcNow)));   // пустой список — как раньше (эталон роутера)

        var state = new AppState { UserExclude = ["bank.example", "bad host!", "gov.example"] };
        var a = WinwsArgs.Build(s, P, BuildOptions.From(state, DateTime.UtcNow)).ToList();
        var profiles = new List<List<string>> { new() };
        foreach (var x in a.Where(x => !x.StartsWith("--wf-"))) if (x == "--new") profiles.Add([]); else profiles[^1].Add(x);
        Assert.Equal(4, profiles.Count);
        Assert.All(profiles, p => Assert.Contains("--hostlist-exclude-domains=bank.example,gov.example", p));   // негодное имя отброшено
    }

    [Fact]
    public void ShellBackend_SetUserExclude_WritesFileAndState_AndRejectsNonDomains()
    {
        using var t = new TempDir();
        var shell = new Shell(new AppPaths(t.Sub("root"), Repo.Data));
        var b = new ShellHelperBackend(shell);
        var r = b.SetUserExclude(["Bank.Example", "  ", "не домен!!", "https://gov.example/x", "bank.example"]);
        Assert.Equal(["bank.example", "gov.example"], r.Saved);
        Assert.Contains("не домен!!", r.Rejected);
        Assert.Equal(["bank.example", "gov.example"], AppState.Load(shell.Paths).UserExclude);
        Assert.Equal(["bank.example", "gov.example"], shell.Data.ReadUserDomains(DataStore.UserExclude));
        Assert.Equal(["bank.example", "gov.example"], b.Status().UserExclude);
        // Пустой список — файл с заглушкой, список в состоянии пуст.
        Assert.Empty(b.SetUserExclude([]).Saved);
        Assert.Empty(shell.Data.ReadUserDomains(DataStore.UserExclude));
    }

    [Fact]
    public void Protocol_UserExcludeSet_IsStrict()
    {
        var catalog = new TestCatalog();
        var ok = HelperCommands.Parse("""{"id":1,"cmd":"user_exclude_set","args":{"lines":["bank.example"]}}""", catalog);
        Assert.Equal(["bank.example"], ok.List("lines"));
        Assert.True(HelperCommands.Specs["user_exclude_set"].Exclusive);
        Assert.Throws<HelperRequestException>(() => HelperCommands.Parse("""{"id":2,"cmd":"user_exclude_set","args":{}}""", catalog));
        Assert.Throws<HelperRequestException>(() => HelperCommands.Parse("""{"id":3,"cmd":"user_exclude_set","args":{"lines":["\"C:\\x.exe\" --run"]}}""", catalog));
        var many = string.Join(",", Enumerable.Range(0, AdvancedSites.MaxExclude + 1).Select(i => $"\"s{i}.example\""));
        Assert.Throws<HelperRequestException>(() => HelperCommands.Parse("{\"id\":4,\"cmd\":\"user_exclude_set\",\"args\":{\"lines\":[" + many + "]}}", catalog));
        Assert.Equal("1.11", HelperProtocol.Api);
    }

    sealed class TestCatalog : IHelperCatalog
    {
        public IReadOnlyCollection<string> StrategyIds => ["alt9"];
        public IReadOnlyCollection<string> GroupIds => ["youtube", "discord", "user"];
    }

    // ---------- №7: заголовок итога подбора ----------

    [Fact]
    public void ResultTitle_DoesNotPromiseSuccess_NextToAddressBlockNote()
    {
        // Заголовок — «Подходит» только при полном успехе без оговорки «подбор, скорее всего, не поможет».
        Assert.Equal("Подходит: general (ALT)", MainViewModel.BestTitle(perfect: true, addressNote: false, "general (ALT)"));
        Assert.Equal("Лучшая из проверенных: general (ALT)", MainViewModel.BestTitle(perfect: true, addressNote: true, "general (ALT)"));
        Assert.Equal("Лучшая из проверенных: general (ALT)", MainViewModel.BestTitle(perfect: false, addressNote: false, "general (ALT)"));
    }

    // ---------- №10: «Проверить, подходит ли мой роутер» ----------

    static RouterViewModel NewRouter(TempDir t)
    {
        var shell = new Shell(new AppPaths(t.Sub("root"), Repo.Data));
        return new RouterViewModel(shell, TestWindow.Settings(shell));
    }

    [Theory]
    [InlineData(RouterFitResult.Yes, "router.fit.yes")]
    [InlineData(RouterFitResult.No, "router.fit.no")]
    [InlineData(RouterFitResult.Unknown, "router.fit.unknown")]
    public async Task RouterFit_AnswersYesNoDontKnow_InPlainWords(RouterFitResult probe, string expected)
    {
        using var t = new TempDir();
        var vm = NewRouter(t);
        vm.Gateway = () => "192.168.1.1";
        vm.FitProbe = _ => Task.FromResult(probe);
        Assert.False(vm.HasFit);
        await vm.CheckFitAsync();
        Assert.Equal(L.T(expected, new { address = "192.168.1.1" }), vm.FitText);
        Assert.True(vm.HasFit);
        Assert.False(vm.FitChecking);
        Assert.DoesNotContain("LuCI", vm.FitText);   // слов для знатоков в ответе нет
    }

    [Fact]
    public async Task RouterFit_NoGateway_SaysSo()
    {
        using var t = new TempDir();
        var vm = NewRouter(t);
        vm.Gateway = () => null;
        vm.FitProbe = _ => throw new InvalidOperationException("шлюза нет — страницу не открываем");
        await vm.CheckFitAsync();
        Assert.Equal(L.T("router.fit.no_network"), vm.FitText);
    }

    [Fact]
    public void RouterFit_LuciMarkers()
    {
        Assert.True(RouterFit.LooksLikeLuci("/cgi-bin/luci/", "<html>"));
        Assert.True(RouterFit.LooksLikeLuci("/", "<link href=\"/luci-static/bootstrap/cascade.css\">"));
        Assert.False(RouterFit.LooksLikeLuci("/", "<html><title>Router admin</title></html>"));
        Assert.False(RouterFit.LooksLikeLuci(null, null));
    }

    [Fact]
    public async Task RouterFit_ProbeReadsRealPage_ThroughHttp()
    {
        using var yes = new StubHttp("<html><link rel=\"stylesheet\" href=\"/luci-static/x.css\"></html>");
        Assert.Equal(RouterFitResult.Yes, await RouterFit.ProbeAsync("router.test", yes.Client));
        using var no = new StubHttp("<html>Vendor firmware</html>");
        Assert.Equal(RouterFitResult.No, await RouterFit.ProbeAsync("router.test", no.Client));
        using var down = new StubHttp(null);
        Assert.Equal(RouterFitResult.Unknown, await RouterFit.ProbeAsync("router.test", down.Client));
    }

    sealed class StubHttp : IDisposable
    {
        public HttpClient Client { get; }
        public StubHttp(string? body) => Client = new HttpClient(new Handler(body));
        public void Dispose() => Client.Dispose();

        sealed class Handler(string? body) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                body is null ? throw new HttpRequestException("нет связи")
                    : Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body), RequestMessage = request });
        }
    }

    // ---------- №11: «Установить» недоступна, пока роутер не подходит ----------

    [Fact]
    public void InstallButton_Disabled_WhenRouterChecked_AndUnsuitable_WithCheckAsMainAction()
    {
        using var t = new TempDir();
        var router = NewRouter(t);
        var vm = new RouterInstallViewModel(router);
        // Не проверяли: «Установить» активна (она проверит сама), «Проверить роутер» — обычная.
        Assert.True(vm.CanTryInstall);
        Assert.False(vm.Unsuitable);
        Assert.True(vm.ShowCheckPlain);
        Assert.False(vm.ShowCheckPrimary);

        var verdict = typeof(RouterInstallViewModel).GetMethod("ShowVerdict", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var stage = typeof(RouterInstallViewModel).GetProperty(nameof(RouterInstallViewModel.Stage))!;
        verdict.Invoke(vm, [new RouterVerdict(false, [new RouterIssue("memory", "Мало временной памяти на роутере")], [], false, false, null, null)]);
        stage.SetValue(vm, RouterInstallStage.Checked);

        Assert.True(vm.Unsuitable);
        Assert.False(vm.CanTryInstall);   // причина — в итоге «Роутер не подходит»
        Assert.False(vm.ShowCheckPlain);
        Assert.True(vm.ShowCheckPrimary);
        Assert.Equal("Роутер не подходит", vm.VerdictTitle);
        Assert.Contains("Мало временной памяти", vm.VerdictText);
    }

    // ---------- №15–16: «Игры» ----------

    [Fact]
    public async Task Apps_EnableButton_AfterProfileSaved_WhenOptimizationIsOff()
    {
        await using var f = new HelperFixture();
        f.Backend.StatusValue = Status(active: false);
        var vm = Model(await f.LinkAsync());
        await vm.RefreshStatusAsync();
        var apps = vm.Apps;
        Assert.False(apps.ShowEnable);   // профиля нет — кнопки нет

        typeof(AppsViewModel).GetProperty(nameof(AppsViewModel.Step))!.SetValue(apps, AppsStep.Done);
        Assert.True(apps.OptimizationOff);
        Assert.True(apps.ShowEnable);
        Assert.DoesNotContain("на Главной", apps.OffHint);

        // Оптимизацию включили — кнопка пропала.
        f.Backend.StatusValue = Status(active: true);
        await vm.RefreshStatusAsync();
        vm.ApplyHelperData();
        Assert.False(apps.ShowEnable);
    }

    [Fact]
    public async Task Apps_EnableCmd_EnablesOptimization_AndStaysOnGamesTab()
    {
        await using var f = new HelperFixture();
        f.Backend.StatusValue = Status(active: false);
        f.Backend.TrackEnabled = true;
        var vm = Model(await f.LinkAsync());
        vm.ProbeGroups = Probe(() => true);
        await vm.RefreshStatusAsync();
        vm.Page = Pages.Apps;
        typeof(AppsViewModel).GetProperty(nameof(AppsViewModel.Step))!.SetValue(vm.Apps, AppsStep.Done);
        Assert.True(vm.Apps.EnableCmd.CanExecute(null));

        vm.Apps.EnableCmd.Execute(null);
        // Включили и вернулись на «Игры» (само «Включить» уводит на Главную).
        // «Включить» кончается проверкой сайтов — после неё человек должен остаться на «Играх», а не на Главной.
        await Until(() => f.Backend.Calls.Contains("enable alt9") && vm.IsRunning && vm.CheckSummary.StartsWith("Открывается") && !vm.Checking, "«Включить оптимизацию» не включила");
        await Until(() => vm.Page == Pages.Apps, "«Включить оптимизацию» увела с вкладки «Игры»");

        Assert.Contains("enable alt9", f.Backend.Calls);
        Assert.Equal(Pages.Apps, vm.Page);   // «Включить» с Главной уводит на Главную, отсюда — нет
        Assert.False(vm.Apps.ShowEnable);
    }
}
