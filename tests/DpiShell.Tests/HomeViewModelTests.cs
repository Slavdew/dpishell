using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.Tests;

/// <summary>
/// Главная и «Свои сайты» в модели окна (UX-аудит 2): итог «Проверить сейчас» — всегда про текущее состояние обхода,
/// несохранённые свои сайты не теряются молча. Помощник — фейк по каналу, проверка сайтов — подмена без сети.
/// </summary>
public sealed class HomeViewModelTests : IDisposable
{
    readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    Shell NewShell() => new(new AppPaths(_tmp.Sub("root"), Repo.Data));

    /// <summary>Модель после загрузки (заставка снята — состояние перечитывается).</summary>
    MainViewModel Model(HelperLink link)
    {
        var vm = TestWindow.Model(NewShell(), link);
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsLoading))!.SetValue(vm, false);
        return vm;
    }

    static HelperStatus Status(bool active) => FakeHelperBackend.DefaultStatus with { Enabled = active, Active = active };

    /// <summary>Проверка без сети: YouTube открывается всегда, остальное — только когда обход «включён».</summary>
    static Func<IReadOnlyList<TargetGroup>, Task<IReadOnlyList<TargetVerdict>>> Probe(Func<bool> on, List<string>? log = null) => groups =>
    {
        log?.AddRange(groups.SelectMany(g => g.Targets.Select(t => t.Url)));
        return Task.FromResult<IReadOnlyList<TargetVerdict>>(groups.SelectMany(g => g.Targets.Select(t =>
            new TargetVerdict(g.Id, t, g.Id == "youtube" || on(), 0.3, []))).ToList());
    };

    [Fact]
    public async Task StatusMessage_BackToReady_OnTabChange()
    {
        await using var f = new HelperFixture();
        f.Backend.StatusValue = Status(active: false);
        var vm = Model(await f.LinkAsync());
        vm.Message = "Параметры скопированы: general (ALT10)";
        vm.GoTo(Pages.Diag);
        Assert.Equal(MainViewModel.ReadyText, vm.Message);
        vm.GoTo(Pages.Home);
        Assert.Equal(MainViewModel.ReadyText, vm.Message);
    }

    [Fact]
    public async Task CheckResult_DroppedOnToggle_AndRunsItselfAfterEnable()
    {
        await using var f = new HelperFixture();
        f.Backend.StatusValue = Status(active: false);
        var vm = Model(await f.LinkAsync());
        bool on = false;
        vm.ProbeGroups = Probe(() => on);
        await vm.RefreshStatusAsync();
        foreach (var g in vm.Groups) g.IsChecked = g.Id is "youtube" or "discord";
        Assert.False(vm.IsRunning);

        // Выключен: Discord не открывается. Счёт — в группах, как отметки рядом (п. 5), а не в адресах.
        await vm.CheckNowAsync();
        Assert.True(vm.HasCheck);
        Assert.Equal("Открывается 1 из 2", vm.CheckSummary);
        Assert.Contains(vm.CheckGroups, g => g.Title == "Discord" && !g.Ok);

        // «Включить»: прежний «✗ Discord» убран, проверка пошла сама — итог про включённый обход.
        on = true;
        f.Backend.StatusValue = Status(active: true);
        await vm.EnableAsync();
        Assert.Contains("enable alt9", f.Backend.Calls);
        Assert.True(vm.IsRunning);
        Assert.False(vm.Checking);
        Assert.Equal("Открывается 2 из 2", vm.CheckSummary);
        Assert.All(vm.CheckGroups, g => Assert.True(g.Ok));
        Assert.EndsWith(" · было 1 из 2", vm.CheckedAt);   // UX-аудит 7, п. 1: «стало лучше или нет» — рядом с прежним итогом

        // «Выключить»: итог про включённый обход больше не верен — скрыт.
        on = false;
        f.Backend.StatusValue = Status(active: false);
        await vm.DisableAsync();
        Assert.False(vm.IsRunning);
        Assert.False(vm.HasCheck);
        Assert.Equal("", vm.CheckSummary);
        Assert.Empty(vm.CheckGroups);
    }

    /// <summary>UX-аудит 6, п. 7: оптимизация работает, а проверка нашла ✗ — заголовок не зелёный «Работает», а жёлтый «Открывается не всё» (в подзаголовке — что именно).</summary>
    [Fact]
    public async Task Status_RunningButSomethingNotOpening_SaysSo_AndTurnsBackWhenAllOpen()
    {
        await using var f = new HelperFixture();
        f.Backend.StatusValue = Status(active: false);
        var vm = Model(await f.LinkAsync());
        bool on = false;
        vm.ProbeGroups = Probe(() => on);   // YouTube открывается всегда, Discord — только когда «on»
        await vm.RefreshStatusAsync();
        foreach (var g in vm.Groups) g.IsChecked = g.Id is "youtube" or "discord";

        f.Backend.StatusValue = Status(active: true);
        await vm.EnableAsync();   // проверка идёт сама
        Assert.Equal("Открывается не всё", vm.StatusTitle);   // жёлтый, а не зелёный «Работает»
        Assert.StartsWith(L.T("home.status.partial_plain", new { groups = "Discord" }), vm.StatusDetail);

        on = true;
        await vm.CheckNowAsync();
        Assert.Equal("Работает", vm.StatusTitle);
    }

    /// <summary>UX-аудит 2, п. 15 (п. 33 первого круга): дописали сайт и не нажали «Сохранить» — проверка берёт поле, уход с вкладки и подбор сохраняют сами.</summary>
    [Fact]
    public async Task UnsavedTargets_CheckedAsTyped_SavedOnLeave_AndBeforeSelect()
    {
        await using var f = new HelperFixture();
        f.Backend.StatusValue = Status(active: false);
        var vm = Model(await f.LinkAsync());
        vm.Confirm = _ => false;
        await vm.RefreshStatusAsync();
        foreach (var g in vm.Groups) g.IsChecked = g.Id == "user";

        vm.GoTo(Pages.Sites);
        vm.TargetsText = "example.com";
        Assert.True(vm.TargetsDirty);
        var urls = new List<string>();
        vm.ProbeGroups = Probe(() => true, urls);
        await vm.CheckNowAsync();
        Assert.Contains(urls, u => u.Contains("example.com"));

        // Ушли с вкладки — помощник на связи, сохраняем сами и говорим об этом.
        vm.GoTo(Pages.Home);
        for (int i = 0; i < 300 * 10 && !vm.HasToast; i++) await Task.Delay(10);
        Assert.Contains("user_targets_set example.com", f.Backend.Calls);
        Assert.Equal("Правки на вкладке «Сайты» сохранены.", vm.Toast);

        // Дописали ещё и сразу в подбор: сначала сохранить, потом подбирать — иначе новый сайт молча не учтётся.
        f.Backend.StatusValue = Status(active: false) with { UserTargets = ["example.com"] };
        await vm.RefreshStatusAsync();
        Assert.False(vm.TargetsDirty);
        vm.TargetsText = "example.com\r\nnew-site.org";
        Assert.True(vm.TargetsDirty);
        f.Backend.StatusValue = Status(active: false) with { UserTargets = ["example.com", "new-site.org"] };
        var select = vm.StartSelectAsync();
        await f.Backend.LongStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var calls = f.Backend.Calls.ToList();
        int saved = calls.IndexOf("user_targets_set example.com,new-site.org"), started = calls.FindIndex(c => c.StartsWith("select "));
        Assert.True(saved >= 0 && saved < started, string.Join("; ", calls));
        Assert.False(vm.TargetsDirty);
        vm.StopSelectCmd.Execute(null);
        await select;
    }

    /// <summary>UX-аудит, п. 46: помощник молчит — через срок заставка говорит «дольше обычного» и даёт «Повторить» / «Открыть без помощника».</summary>
    [Fact]
    public async Task Loading_HelperSilent_OffersWayOut()
    {
        var link = new HelperLink
        {
            Connector = async ct => { await Task.Delay(Timeout.Infinite, ct); throw new OperationCanceledException(); },
            Setup = new NoSetup(),
        };
        var vm = TestWindow.Model(NewShell(), link);
        vm.LoadSlowAfter = TimeSpan.FromMilliseconds(200);   // запас: под нагрузкой поток между Init и проверкой «ещё не медленно» может встать
        var init = vm.InitializeAsync();
        Assert.True(vm.IsLoading);
        Assert.False(vm.ShowLoadActions);
        for (int i = 0; i < 300 * 10 && !vm.LoadSlow; i++) await Task.Delay(10);
        Assert.True(vm.ShowLoadActions);
        Assert.Equal(L.T("shell.loading.slow"), vm.LoadingText);
        Assert.True(vm.RetryCmd.CanExecute(null));
        Assert.True(vm.OpenWithoutHelperCmd.CanExecute(null));
        // «Повторить» — заново, с новым сроком; прежнее ожидание отменено и заставку не трогает.
        var again = vm.InitializeAsync();
        await init;
        Assert.True(vm.IsLoading);
        Assert.False(vm.LoadSlow);
        Assert.False(vm.LoadFailed);
        for (int i = 0; i < 300 * 10 && !vm.LoadSlow; i++) await Task.Delay(10);
        Assert.True(vm.LoadSlow);
        GC.KeepAlive(again);
    }

    [Fact]
    public async Task CheckResult_StateChangedWhileChecking_IsNotShown()
    {
        await using var f = new HelperFixture();
        f.Backend.StatusValue = Status(active: true);
        var vm = Model(await f.LinkAsync());
        var release = new TaskCompletionSource();
        vm.ProbeGroups = async groups =>
        {
            await release.Task;
            return groups.SelectMany(g => g.Targets.Select(t => new TargetVerdict(g.Id, t, true, 0.3, []))).ToList();
        };
        await vm.RefreshStatusAsync();
        var check = vm.CheckNowAsync();
        Assert.True(vm.Checking);
        Assert.True(vm.HasCheck);
        Assert.Equal(L.T("home.check.running"), vm.CheckSummary);

        // Пока проверяли, обход выключился (из трея, сбой) — итог про включённый не показываем.
        f.Backend.StatusValue = Status(active: false);
        await vm.RefreshStatusAsync();
        release.SetResult();
        await check;
        Assert.False(vm.HasCheck);
        Assert.Empty(vm.CheckRows);
    }

    /// <summary>По DoH YouTube открылся, по адресу DNS провайдера — нет: в итоге ⚠, причина и кнопка защищённого DNS; из hosts — без кнопки.</summary>
    [Fact]
    public async Task CheckResult_BrowserFails_WarningAndDnsButton()
    {
        await using var f = new HelperFixture();
        f.Backend.StatusValue = Status(active: true);
        var vm = Model(await f.LinkAsync());
        string? hostsIp = null; var sysIp = "10.1.1.1";
        vm.ProbeGroups = groups => Task.FromResult<IReadOnlyList<TargetVerdict>>(groups.SelectMany(g => g.Targets.Select(t =>
            new TargetVerdict(g.Id, t, true, 0.3, [], g.Id == "youtube"
                ? new BrowserPath([System.Net.IPAddress.Parse(sysIp)], [System.Net.IPAddress.Parse("142.250.74.14")], false, hostsIp, [])
                : null))).ToList());
        await vm.RefreshStatusAsync();
        foreach (var g in vm.Groups) g.IsChecked = g.Id is "youtube" or "discord";

        await vm.CheckNowAsync();
        Assert.Equal("Открывается 1 из 2", vm.CheckSummary);   // как в заголовке: в браузере YouTube не откроется
        Assert.Equal("⚠", vm.CheckGroups.Single(g => g.Title == "YouTube").Mark);
        Assert.Equal("✓", vm.CheckGroups.Single(g => g.Title == "Discord").Mark);
        Assert.True(vm.HasBrowserWarning);
        Assert.Equal("⚠ В браузере не откроется: YouTube — провайдер подсовывает не тот адрес.", vm.BrowserWarning);
        Assert.True(vm.ShowBrowserDnsFix);

        hostsIp = "10.1.1.1";
        await vm.CheckNowAsync();
        Assert.Contains("запись в hosts", vm.BrowserWarning);
        Assert.False(vm.ShowBrowserDnsFix);

        // Чужого адреса нет, просто не открылось через DNS Windows — без слов «подмена» и «браузер».
        hostsIp = null; sysIp = "173.194.220.91";
        await vm.CheckNowAsync();
        Assert.Equal("⚠ " + L.T("home.browser.windows_only", new { groups = "YouTube" }), vm.BrowserWarning);
        Assert.DoesNotContain("подмен", vm.BrowserWarning);
        Assert.True(vm.ShowBrowserDnsFix);
    }
}
