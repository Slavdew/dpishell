using System.Security.Authentication;
using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Router;

namespace DpiShell.Tests;

/// <summary>
/// «Не открылось ни с одной стратегией»: адрес молчит у всех проверенных стратегий — похоже на блокировку по адресу,
/// подбор тут не поможет. Признак — Core (<see cref="AddressBlockTracker"/>), модель ПК и роутера — его показывают.
/// </summary>
public sealed class AddressBlockTests : IDisposable
{
    readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    const string Video = "https://redirector.googlevideo.com/";
    const string Site = "https://www.youtube.com/";

    static ProbeResult P(string url, SslProtocols tls, ProbeOutcome o, string detail = "") => new(new Target(url), tls, o, 3, 0, detail);

    static TargetVerdict Verdict(string group, string url, ProbeOutcome o)
    {
        var ok = o == ProbeOutcome.Ok;
        return new(group, new Target(url), ok, ok ? 0.3 : 0,
            [P(url, SslProtocols.Tls13, o, ok ? "200" : "таймаут"), P(url, SslProtocols.Tls12, o, ok ? "200" : "таймаут")]);
    }

    static StrategyScore Score(int i, ProbeOutcome video, ProbeOutcome site = ProbeOutcome.Ok) => new(
        new Strategy("s" + i, "Стратегия " + i, "catalog", new Dictionary<string, string> { ["general"] = "--x" }),
        [Verdict("youtube", Site, site), Verdict("youtube", Video, video)], 0, 2);

    // ---- признак ----

    [Fact]
    public void Tracker_Timeouts_Everywhere_AreBlocked_Only_AfterMinChecks()
    {
        var t = new AddressBlockTracker();
        for (int i = 0; i < AddressBlockTracker.MinChecks - 1; i++) t.Add(Score(i, ProbeOutcome.Timeout));
        Assert.Empty(t.Blocked);   // мало проверок — вывод ненадёжен
        t.Add(Score(9, ProbeOutcome.Timeout));
        Assert.Equal([Video], t.Blocked);
    }

    [Theory]
    [InlineData(ProbeOutcome.Timeout, true)]
    [InlineData(ProbeOutcome.Reset, true)]
    [InlineData(ProbeOutcome.ConnectFailed, true)]
    [InlineData(ProbeOutcome.BadStatus, false)]        // 403/404 — сайт ответил
    [InlineData(ProbeOutcome.ForeignRedirect, false)]  // заглушка провайдера
    [InlineData(ProbeOutcome.Blocked, false)]          // 451
    [InlineData(ProbeOutcome.Truncated, false)]        // обрыв после 16 КБ — работа DPI, её лечит стратегия
    [InlineData(ProbeOutcome.TlsFailed, false)]
    public void Tracker_OnlyNoAnswer_CountsAsAddressBlock(ProbeOutcome o, bool blocked)
    {
        var t = new AddressBlockTracker();
        for (int i = 0; i < 6; i++) t.Add(Score(i, o));
        Assert.Equal(blocked, t.Blocked.Contains(Video));
    }

    [Fact]
    public void Tracker_OneOpening_OrOneOtherFailure_ClearsTheVerdict()
    {
        var opened = new AddressBlockTracker();
        for (int i = 0; i < 8; i++) opened.Add(Score(i, i == 3 ? ProbeOutcome.Ok : ProbeOutcome.Timeout));
        Assert.Empty(opened.Blocked);

        var mixed = new AddressBlockTracker();
        for (int i = 0; i < 8; i++) mixed.Add(Score(i, i == 3 ? ProbeOutcome.Truncated : ProbeOutcome.Timeout));
        Assert.Empty(mixed.Blocked);
    }

    [Fact]
    public void Tracker_OneTlsVersionAnswers_IsNotSilence()
    {
        var v = new TargetVerdict("youtube", new Target(Video), false, 0,
            [P(Video, SslProtocols.Tls13, ProbeOutcome.Timeout, "таймаут"), P(Video, SslProtocols.Tls12, ProbeOutcome.Truncated, "обрыв")]);
        Assert.False(AddressBlockTracker.IsSilent(v.Probes));
    }

    [Fact]
    public void Tracker_SameStrategyTwice_CountsOnce_AndFailedStrategyIsSkipped()
    {
        var t = new AddressBlockTracker();
        for (int i = 0; i < 10; i++) t.Add(Score(1, ProbeOutcome.Timeout));
        Assert.Empty(t.Blocked);
        var broken = new StrategyScore(new Strategy("bad", "bad", "catalog", []), [Verdict("youtube", Video, ProbeOutcome.Timeout)], 0, 1, "winws завершился");
        for (int i = 0; i < 10; i++) t.Add(broken);
        Assert.Empty(t.Blocked);
    }

    [Theory]
    [InlineData("таймаут", true)]
    [InlineData("TLS 1.3: таймаут; TLS 1.2: таймаут", true)]
    [InlineData("TLS 1.3: curl: (56) Recv failure: Connection reset by peer; TLS 1.2: таймаут", true)]
    [InlineData("TLS 1.3: curl: (7) Failed to connect to x port 443: Connection timed out; TLS 1.2: таймаут", true)]
    [InlineData("TLS 1.2: поток оборвался на 16384 байтах", false)]
    [InlineData("TLS 1.3: таймаут; TLS 1.2: поток оборвался на 16384 байтах", false)]
    [InlineData("HTTP 403", false)]
    [InlineData("редирект на blocked.provider.ru", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void RouterDetail_NoAnswer(string? detail, bool silent) => Assert.Equal(silent, AddressBlockTracker.IsSilent(detail));

    [Fact]
    public void Note_Text_And_YouTubeVideoRemark()
    {
        var t = new AddressBlockTracker();
        for (int i = 0; i < 6; i++) t.Add(Score(i, ProbeOutcome.Timeout));
        var note = t.Note();
        Assert.Equal("Не открылось ни с одной стратегией: redirector.googlevideo.com — похоже на блокировку по адресу, подбор стратегии тут, скорее всего, не поможет. "
            + "Сайт YouTube откроется, а видео может не воспроизводиться.", note);
        Assert.DoesNotContain("обход", note, StringComparison.OrdinalIgnoreCase);

        // www.youtube.com не открылся — оговорки про видео нет.
        var down = new AddressBlockTracker();
        for (int i = 0; i < 6; i++) down.Add(Score(i, ProbeOutcome.Timeout, ProbeOutcome.Timeout));
        Assert.DoesNotContain("видео", down.Note());
        Assert.Contains("www.youtube.com, redirector.googlevideo.com", down.Note());
        Assert.Equal("", new AddressBlockTracker().Note());
    }

    [Fact]
    public void Note_ManyHosts_AreCut()
    {
        var t = new AddressBlockTracker();
        for (int i = 0; i < 6; i++)
            for (int h = 0; h < 6; h++) t.Add("s" + i, $"https://h{h}.example.com/", false, true);
        Assert.Contains("h0.example.com, h1.example.com, h2.example.com, h3.example.com и ещё 2 —", t.Note());
    }

    // ---- ПК ----

    [Fact]
    public async Task Pc_Select_BlockedByAddress_ShowsNote_AndDoesNotOfferDeep()
    {
        await using var f = new HelperFixture();
        var scores = Enumerable.Range(0, 6).Select(i => Score(i, ProbeOutcome.Timeout)).ToList();
        // Лучшая открыла всё, кроме googlevideo, — а он молчит у всех.
        scores[0] = scores[0] with { GroupsPassed = 1 };   // 1 из 2 групп: не идеальная — глубокий поиск предложили бы
        var tested = new TargetGroup("youtube", "YouTube", [new Target(Site), new Target(Video)]);
        f.Backend.ReportOverride = new([Verdict("youtube", Site, ProbeOutcome.Timeout)], [tested], scores, false);
        f.Backend.Release.TrySetResult();
        var vm = TestWindow.Model(new Shell(new AppPaths(_tmp.Sub("root"), Repo.Data)), await f.LinkAsync());
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsLoading))!.SetValue(vm, false);
        vm.Confirm = _ => true;   // «Оптимизация включена, на время проверки приостановим» — без настоящего окна
        await vm.RefreshStatusAsync();
        foreach (var g in vm.Groups) g.IsChecked = g.Id == "youtube";

        await vm.StartSelectAsync();

        Assert.True(vm.HasAddressNote);
        Assert.Contains("redirector.googlevideo.com", vm.AddressNote);
        Assert.Contains("видео может не воспроизводиться", vm.AddressNote);
        Assert.False(vm.OfferDeep);   // кроме молчащего адреса всё открылось — искать глубже ради него незачем
    }

    [Fact]
    public async Task Pc_Select_RealFailure_StillOffersDeep()
    {
        await using var f = new HelperFixture();
        // Лучшая не открыла youtube-страницу (ответила 403), а googlevideo молчит: провал не только из-за адреса.
        var scores = Enumerable.Range(0, 6).Select(i => Score(i, ProbeOutcome.Timeout, ProbeOutcome.BadStatus)).ToList();
        scores[0] = scores[0] with { GroupsPassed = 1 };   // 1 из 2 групп: не идеальная — глубокий поиск предложили бы
        var tested = new TargetGroup("youtube", "YouTube", [new Target(Site), new Target(Video)]);
        f.Backend.ReportOverride = new([Verdict("youtube", Site, ProbeOutcome.Timeout)], [tested], scores, false);
        f.Backend.Release.TrySetResult();
        var vm = TestWindow.Model(new Shell(new AppPaths(_tmp.Sub("root"), Repo.Data)), await f.LinkAsync());
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsLoading))!.SetValue(vm, false);
        vm.Confirm = _ => true;   // «Оптимизация включена, на время проверки приостановим» — без настоящего окна
        await vm.RefreshStatusAsync();
        foreach (var g in vm.Groups) g.IsChecked = g.Id == "youtube";

        await vm.StartSelectAsync();

        Assert.True(vm.HasAddressNote);
        Assert.True(vm.OfferDeep);
    }

    // ---- роутер ----

    static string Rows(int n, string videoDetail, string siteDetail = "TLS 1.3 и TLS 1.2: 200, 512 КБ", bool siteOk = true) =>
        "[" + string.Join(",", Enumerable.Range(0, n).Select(i => $$"""
            { "id": "alt{{i}}", "title": "general (ALT{{i}})", "group_share": {{(i == 0 ? "1.0" : "0.5")}}, "target_share": 0.5, "avg_s": 0.7, "error": null,
              "verdicts": [
                { "group": "youtube", "url": "https://www.youtube.com/", "ok": {{(siteOk ? "true" : "false")}}, "detail": "{{siteDetail}}", "seconds": 0.5 },
                { "group": "youtube", "url": "https://redirector.googlevideo.com/", "ok": false, "detail": "{{videoDetail}}", "seconds": 6.1 } ] }
            """)) + "]";

    [Fact]
    public async Task Router_VerdictsTimeouts_ShowNote()
    {
        using var r = new FakeRouter { SelectRowsJson = Rows(6, "TLS 1.3: таймаут; TLS 1.2: таймаут"), BestId = "alt0" };
        var vm = Make(r);
        await vm.ConnectAsync("secret");
        await vm.StartSelectAsync(deep: false);
        Assert.True(vm.HasAddressNote);
        Assert.StartsWith("Не открылось ни с одной стратегией: redirector.googlevideo.com — похоже на блокировку по адресу", vm.AddressNote);
        Assert.Contains("видео может не воспроизводиться", vm.AddressNote);
    }

    [Fact]
    public async Task Router_Truncated_OrFewChecks_NoNote()
    {
        using var r = new FakeRouter { SelectRowsJson = Rows(6, "TLS 1.2: поток оборвался на 16384 байтах"), BestId = "alt0" };
        var vm = Make(r);
        await vm.ConnectAsync("secret");
        await vm.StartSelectAsync(deep: false);
        Assert.False(vm.HasAddressNote);

        using var few = new FakeRouter { SelectRowsJson = Rows(3, "TLS 1.3: таймаут; TLS 1.2: таймаут"), BestId = "alt0" };
        var vm2 = Make(few);
        await vm2.ConnectAsync("secret");
        await vm2.StartSelectAsync(deep: false);
        Assert.False(vm2.HasAddressNote);
    }

    [Fact]
    public async Task Router_NewSelect_ResetsCount_DeepKeepsIt()
    {
        using var r = new FakeRouter { SelectRowsJson = Rows(3, "таймаут"), BestId = "alt0" };
        var vm = Make(r);
        await vm.ConnectAsync("secret");
        await vm.StartSelectAsync(deep: false);
        Assert.False(vm.HasAddressNote);   // 3 проверки — мало
        r.SelectRowsJson = Rows(3, "таймаут").Replace("alt", "found");
        await vm.StartSelectAsync(deep: true);
        Assert.True(vm.HasAddressNote);    // глубокий поиск добавил ещё 3 — набралось 6
        await vm.StartSelectAsync(deep: false);
        Assert.False(vm.HasAddressNote);   // новый подбор начал счёт заново
    }

    RouterViewModel Make(FakeRouter r)
    {
        var pc = new Shell(new AppPaths(_tmp.Sub("root"), Repo.Data));
        var settings = WindowSettings.Load(Path.Combine(_tmp.Sub("window"), "window.json"));
        return new RouterViewModel(pc, settings)
        {
            FoundStrategies = () => UserStrategies.Load(pc.Paths),
            PcUserTargets = () => pc.State.UserTargets,
            AddressAuto = false,
            CustomAddress = r.Address,
            PollInterval = TimeSpan.FromMilliseconds(10),
            CheckTimeout = TimeSpan.FromSeconds(5),
            DnsTimeout = TimeSpan.FromSeconds(5),
            PcTimestamps = () => true,
            Gateway = () => throw new InvalidOperationException("в тестах — только фейковый роутер"),
        };
    }
}
