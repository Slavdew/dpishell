using System.Text.Json.Nodes;
using DpiShell.Core;
using DpiShell.Core.Helper;
using DpiShell.Core.Router;

namespace DpiShell.Tests;

/// <summary>
/// Совместная работа: когда она включается, как продлевается и как ПК мгновенно переходит к полной настройке.
/// Роутер и ПК — в памяти, время и MAC подставляются.
/// </summary>
public sealed class JointCoordinatorTests
{
    const string PcId = "11111111-2222-4333-8444-555555555555";

    sealed class FakeRouterSide(List<string> order) : IJointRouter
    {
        public string Address => "192.168.1.1";
        public string? NotReady { get; set; }
        public bool Reachable { get; set; } = true;
        public List<string> Log { get; } = [];
        public List<string> Google { get; set; } = ["youtube.com", "googlevideo.com", "play.google.com"];
        public List<string> Discord { get; set; } = ["discord.com", "discord.gg"];
        public List<string> Ignore { get; set; } = [];
        public bool Active { get; set; } = true;
        public bool Present { get; private set; }
        public Exception? SetThrows { get; set; }
        public List<(IReadOnlyList<string> Macs, IReadOnlyList<string> Domains)> Sets { get; } = [];

        public Task<IReadOnlyList<string>> ListAsync(string name, CancellationToken ct)
        {
            Log.Add("list " + name);
            return Task.FromResult<IReadOnlyList<string>>(name == "google" ? Google : Discord);
        }

        public Task<RouterPcLinkSet> SetAsync(string id, IReadOnlyList<string> macs, IReadOnlyList<string> domains, CancellationToken ct)
        {
            Log.Add("set");
            order.Add("router set");
            if (SetThrows is { } e) throw e;
            Present = true;
            Sets.Add((macs, domains));
            return Task.FromResult(new RouterPcLinkSet(DateTime.UtcNow.AddHours(24), [.. domains.Where(d => Ignore.Contains(d))]));
        }

        public Task DelAsync(string id, CancellationToken ct)
        {
            Log.Add("del");
            order.Add("router del");
            Present = false;
            return Task.CompletedTask;
        }

        public Task<RouterPcLinks> GetAsync(CancellationToken ct)
        {
            Log.Add("get");
            var items = Present ? new List<RouterPcLink> { new(PcId, ["aa:bb:cc:dd:ee:01"], Sets[^1].Domains.Count, Ignore.Count(i => Sets[^1].Domains.Contains(i)), DateTime.UtcNow, DateTime.UtcNow.AddHours(24), Active) } : [];
            return Task.FromResult(new RouterPcLinks(items));
        }
    }

    sealed class FakePcSide(List<string> log) : IJointPc
    {
        public bool Running { get; set; } = true;
        public JointInfo? Info { get; set; } = Info0();
        public bool StartOk { get; set; } = true;
        public Exception? SetThrows { get; set; }
        public List<(bool Active, string[] Owned)> Sets { get; } = [];

        public Task<JointInfo?> GetAsync(CancellationToken ct) => Task.FromResult(Info);

        public Task<JointInfo?> SetAsync(bool active, IReadOnlyList<string> owned, CancellationToken ct)
        {
            log.Add("pc " + (active ? "on" : "off"));
            if (SetThrows is { } e && active) throw e;
            Sets.Add((active, [.. owned]));
            if (Info is not null) Info = Info with { Active = active, Owned = active ? owned : [] };
            return Task.FromResult(Info);
        }

        public Task<bool> StartAsync(CancellationToken ct) { log.Add("pc start"); if (StartOk) Running = true; return Task.FromResult(StartOk); }
        public Task StopAsync(CancellationToken ct) { log.Add("pc stop"); Running = false; return Task.CompletedTask; }
    }

    static JointInfo Info0() => new(PcId, false, [], ["mysite.example", "youtube.com"], [], ["www.google.com", "4pda.to", "max.ru"], true, [], "alt9", true,
        ["mysite.example", "youtube.com"], 0);

    sealed class Rec : IChangeRecorder
    {
        public List<string> Log { get; } = [];
        public string Begin(string kind, string title, JsonNode? before, JsonNode? after) { Log.Add("begin " + kind); return "r1"; }
        public void Done(string id, JsonNode? after = null) { if (id.Length > 0) Log.Add("done"); }
        public void Abort(string id) { if (id.Length > 0) Log.Add("abort"); }
        public void Update(string id, JsonNode? after) { }
        public void Close(string kind, string why, Func<ChangeEntry, bool>? filter = null) => Log.Add("close " + kind);
        public IReadOnlyList<ChangeEntry> Active(string kind) => [];
    }

    sealed class Rig
    {
        /// <summary>Что случилось и в каком порядке — и на роутере, и на ПК.</summary>
        public readonly List<string> Order = [];
        public FakeRouterSide Router;
        public FakePcSide Pc;
        public JointCoordinator J;
        public DateTime Now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        public string? Mac = "aa:bb:cc:dd:ee:01";
        public Rec Recorder = new();

        public Rig()
        {
            Router = new FakeRouterSide(Order);
            Pc = new FakePcSide(Order);
            J = new JointCoordinator(Router, Pc) { Now = () => Now, OwnMac = _ => Mac, Recorder = Recorder };
        }
    }

    // ---------- включение ----------

    [Fact]
    public async Task Enable_sets_link_with_decoys_first_then_switches_the_pc_to_own_sites()
    {
        var r = new Rig();
        Assert.Null(await r.J.EnableAsync());

        Assert.Equal(JointState.Active, r.J.State);
        var set = Assert.Single(r.Router.Sets);
        Assert.Equal(["aa:bb:cc:dd:ee:01"], set.Macs);
        Assert.Equal(["www.google.com", "4pda.to", "max.ru", "mysite.example"], set.Domains);     // youtube.com ведёт роутер — не отдаём
        Assert.DoesNotContain("youtube.com", set.Domains);
        var on = Assert.Single(r.Pc.Sets);
        Assert.True(on.Active);
        Assert.Equal(["youtube.com"], on.Owned);                                                    // на ПК youtube.com исключён: его ведёт роутер
        Assert.Equal(["youtube.com"], r.J.RouterOwned);
        Assert.Equal(["mysite.example"], r.J.Mine);
        Assert.Equal("Роутер ведёт YouTube и Discord, этот ПК — свои сайты (1)", r.J.Status);
        Assert.Contains(r.J.Notes, n => n.StartsWith("Ведёт роутер: youtube.com"));
        // Сначала роутер получил связку, потом ПК перешёл на «только своё» (иначе сайты ПК на миг остались бы без хозяина).
        Assert.Equal(["router set", "pc on"], r.Order);
        Assert.Equal(["begin router_joint", "done"], r.Recorder.Log);
    }

    [Fact]
    public async Task Enable_starts_the_pc_after_the_link_when_the_optimization_was_off()
    {
        var r = new Rig();
        r.Pc.Running = false;
        Assert.Null(await r.J.EnableAsync());
        Assert.Equal(["router set", "pc on", "pc start"], r.Order);        // связка → «только своё» → запуск: полной настройки на ПК не было ни на миг
        Assert.True(r.Pc.Running);
    }

    [Fact]
    public async Task Enable_refuses_with_a_reason_and_changes_nothing()
    {
        var r = new Rig();
        r.Router.NotReady = JointTexts.FallbackDisabled;
        Assert.Equal(JointTexts.FallbackDisabled, await r.J.EnableAsync());
        Assert.False(r.J.Wanted);
        Assert.Equal(JointState.Off, r.J.State);
        Assert.Empty(r.Router.Sets);
        Assert.Empty(r.Pc.Sets);

        r.Router.NotReady = null;
        r.Pc.Info = null;                                                    // помощника нет
        Assert.Equal(JointTexts.FallbackNotInstalled, await r.J.EnableAsync());

        r.Pc.Info = Info0() with { Own = [], Work = [] };                    // у ПК нет своих сайтов
        Assert.Equal(JointTexts.NoOwnSites, await r.J.EnableAsync());
        Assert.False(r.J.Wanted);
        Assert.True(r.Pc.Running);                                           // и ПК при отказе не выключается
        Assert.DoesNotContain(r.Pc.Sets, s => s.Active);
    }

    [Fact]
    public async Task Enable_that_cannot_start_the_pc_rolls_everything_back()
    {
        var r = new Rig { };
        r.Pc.Running = false;
        r.Pc.StartOk = false;
        Assert.Equal(JointTexts.StartFailed, await r.J.EnableAsync());
        Assert.False(r.J.Wanted);
        Assert.False(r.Router.Present);
        Assert.False(r.Pc.Info!.Active);
    }

    // ---------- продление ----------

    [Fact]
    public async Task Link_is_renewed_every_half_hour_not_every_minute()
    {
        var r = new Rig();
        await r.J.EnableAsync();
        r.Router.Log.Clear();

        r.Now += TimeSpan.FromMinutes(5);
        await r.J.TickAsync();
        Assert.Equal(["get"], r.Router.Log);                                  // только проверка, что связка действует
        Assert.Single(r.Router.Sets);

        r.Now += TimeSpan.FromMinutes(26);                                    // 31 минута с последнего pc_link_set
        await r.J.TickAsync();
        Assert.Equal(2, r.Router.Sets.Count);
        Assert.True(JointRules.RenewEvery <= TimeSpan.FromHours(1));          // договор: не реже раза в час
        Assert.Equal(JointState.Active, r.J.State);
        Assert.Equal([true, true], r.Pc.Sets.Select(s => s.Active));          // и срок у помощника продлён тем же ходом
    }

    [Fact]
    public async Task Changed_sites_or_adapter_send_the_link_again_at_once()
    {
        var r = new Rig();
        await r.J.EnableAsync();

        r.Pc.Info = r.Pc.Info! with { Own = ["mysite.example", "youtube.com", "new.example"], Work = ["mysite.example", "youtube.com", "new.example"] };
        await r.J.TickAsync();
        Assert.Equal(2, r.Router.Sets.Count);
        Assert.Contains("new.example", r.Router.Sets[1].Domains);

        r.Mac = "aa:bb:cc:dd:ee:02";                                          // Windows сменила MAC Wi-Fi (другая сеть) — pc_link_set заново с новым
        await r.J.TickAsync();
        Assert.Equal(3, r.Router.Sets.Count);
        Assert.Equal(["aa:bb:cc:dd:ee:02"], r.Router.Sets[2].Macs);
        Assert.Equal("aa:bb:cc:dd:ee:02", r.J.LastMac);
    }

    [Fact]
    public async Task Router_ignored_names_move_to_the_router_on_the_next_check()
    {
        var r = new Rig();
        r.Router.Ignore = ["mysite.example"];                                 // у роутера появилась такая строка продвинутого режима
        r.Pc.Info = Info0() with { Own = ["mysite.example", "mine.example"], Work = ["mysite.example", "mine.example"] };
        await r.J.EnableAsync();
        Assert.Equal(["mine.example"], r.J.Mine);
        Assert.Equal(["mysite.example"], r.J.RouterOwned);                    // роутер вернул в ignored — это его адрес, на ПК не применяется
        Assert.Equal(["mysite.example"], r.Pc.Sets.Single().Owned);
        Assert.Equal(JointState.Active, r.J.State);
        Assert.Equal("Роутер ведёт YouTube и Discord, этот ПК — свои сайты (1)", r.J.Status);

        // Если все сайты ПК оказались адресами роутера, вести нечего: связка не держится.
        var empty = new Rig();
        empty.Router.Ignore = ["mysite.example"];
        empty.J.Restore(wanted: true);
        empty.Pc.Info = Info0() with { Own = ["mysite.example"], Work = ["mysite.example"] };
        await empty.J.TickAsync();
        Assert.Equal(JointState.Waiting, empty.J.State);
        Assert.Equal(JointTexts.NoOwnSites, empty.J.Status);
        Assert.False(empty.Router.Present);
    }

    // ---------- возврат к полной настройке ----------

    static void AssertFallback(Rig r, string status)
    {
        Assert.Equal(JointState.Fallback, r.J.State);
        Assert.Equal(status, r.J.Status);
        Assert.False(r.Pc.Info!.Active);
        Assert.True(r.J.Wanted);                                              // переключатель остаётся: роутер вернётся — связка поднимется сама
    }

    [Fact]
    public async Task Router_gone_pc_goes_full_at_once_and_comes_back_with_the_router()
    {
        var r = new Rig();
        await r.J.EnableAsync();
        r.Order.Clear();
        r.Router.NotReady = JointTexts.Fallback;
        r.Router.Reachable = false;

        await r.J.TickAsync();
        AssertFallback(r, "Роутер не найден — этот ПК работает сам");
        Assert.Equal(["pc off"], r.Order);                                    // помощник — сразу, без ожидания роутера
        Assert.DoesNotContain("router del", r.Order);                         // недоступному роутеру снятие не шлём (оно истечёт само)

        r.Router.NotReady = null;
        r.Router.Reachable = true;
        await r.J.TickAsync();
        Assert.Equal(JointState.Active, r.J.State);
        Assert.True(r.Pc.Info!.Active);
        Assert.Equal(2, r.Router.Sets.Count);
    }

    [Fact]
    public async Task Router_optimization_off_is_a_fallback_and_the_link_is_removed()
    {
        var r = new Rig();
        await r.J.EnableAsync();
        r.Order.Clear();
        r.Router.Log.Clear();
        r.Router.NotReady = JointTexts.FallbackDisabled;

        await r.J.TickAsync();
        AssertFallback(r, "Оптимизация на роутере выключена — этот ПК работает сам");
        Assert.Equal(["del"], r.Router.Log);
        Assert.False(r.Router.Present);
        Assert.Equal(["pc off", "router del"], r.Order);
        Assert.Contains("close router_joint", r.Recorder.Log);
    }

    [Fact]
    public async Task Pc_goes_full_before_the_link_is_removed()
    {
        var r = new Rig();
        await r.J.EnableAsync();
        r.Order.Clear();
        r.Router.NotReady = JointTexts.FallbackDisabled;
        await r.J.TickAsync();
        Assert.Equal(["pc off", "router del"], r.Order);                      // сначала помощник (полная настройка), потом роутер
    }

    [Fact]
    public async Task Renewal_failure_goes_full_immediately()
    {
        var r = new Rig();
        await r.J.EnableAsync();
        r.Now += TimeSpan.FromMinutes(31);
        r.Router.SetThrows = new RouterException(RouterFailure.App, "Не удалось сохранить связку");
        await r.J.TickAsync();
        AssertFallback(r, JointTexts.FallbackRenew);
        Assert.False(r.Router.Present);

        r.Router.SetThrows = new RouterException(RouterFailure.Unreachable, "Роутер недоступен");
        await r.J.TickAsync();
        AssertFallback(r, JointTexts.Fallback);
    }

    [Fact]
    public async Task Link_not_active_is_set_again_once_and_a_second_failure_goes_full()
    {
        var r = new Rig();
        await r.J.EnableAsync();

        r.Router.Active = false;                                              // active=false: срок вышел или роутер перезагрузился
        await r.J.TickAsync();
        AssertFallback(r, JointTexts.FallbackRenew);                          // pc_link_set прошёл, а связка так и не действует
        Assert.Equal(2, r.Router.Sets.Count);

        r.Router.Active = true;
        await r.J.TickAsync();
        Assert.Equal(JointState.Active, r.J.State);
    }

    [Fact]
    public async Task Pc_optimization_off_means_no_link_and_it_returns_with_the_optimization()
    {
        var r = new Rig();
        await r.J.EnableAsync();
        r.Pc.Running = false;
        await r.J.TickAsync();
        Assert.Equal(JointState.Waiting, r.J.State);
        Assert.Equal(JointTexts.WaitingPc, r.J.Status);
        Assert.False(r.Router.Present);                                       // иначе сайты ПК не обработал бы никто
        Assert.False(r.Pc.Info!.Active);

        r.Pc.Running = true;
        await r.J.TickAsync();
        Assert.Equal(JointState.Active, r.J.State);
        Assert.True(r.Router.Present);
    }

    [Fact]
    public async Task All_own_sites_removed_while_on_stops_the_pc_like_without_the_joint_work()
    {
        var r = new Rig();
        await r.J.EnableAsync();
        r.Pc.Info = r.Pc.Info! with { Own = [], Work = [] };
        await r.J.TickAsync();
        Assert.Equal(JointState.Waiting, r.J.State);
        Assert.Equal(JointTexts.NoOwnSites, r.J.Status);
        Assert.False(r.Pc.Running);                                           // роутер ведёт всех, а ПК вести нечего
        Assert.False(r.Router.Present);
    }

    [Fact]
    public async Task Helper_gone_is_a_fallback_and_a_busy_helper_is_not()
    {
        var r = new Rig();
        await r.J.EnableAsync();
        r.Pc.Info = Info0() with { Active = true, Owned = ["x.example"] };      // помощник занят: команда отвечает busy — просто повторим
        r.Pc.SetThrows = new HelperException(HelperErrors.Busy, "Идёт подбор");
        await r.J.TickAsync();
        Assert.Equal(JointState.Active, r.J.State);

        r.Pc.SetThrows = null;
        r.Pc.Info = null;
        await r.J.TickAsync();
        Assert.Equal(JointState.Fallback, r.J.State);
        Assert.Equal(JointTexts.FallbackNotInstalled, r.J.Status);
    }

    [Fact]
    public async Task Helper_refusing_the_joint_is_a_fallback_with_its_reason()
    {
        var r = new Rig();
        r.Pc.SetThrows = new HelperException(HelperErrors.Failed, "winws не запустился");
        Assert.NotNull(await r.J.EnableAsync());
        Assert.False(r.J.Wanted);
        Assert.False(r.Router.Present);                                       // связка снята
        Assert.False(r.Pc.Info!.Active);
    }

    // ---------- выключение ----------

    [Fact]
    public async Task Stop_returns_the_pc_first_then_removes_the_link_and_the_journal_entry()
    {
        var r = new Rig();
        await r.J.EnableAsync();
        r.Order.Clear();
        r.Router.Log.Clear();
        await r.J.StopAsync();
        Assert.Equal(JointState.Off, r.J.State);
        Assert.False(r.J.Wanted);
        Assert.Equal(["pc off", "router del"], r.Order);
        Assert.Equal(["del"], r.Router.Log);
        Assert.Contains("close router_joint", r.Recorder.Log);

        await r.J.TickAsync();                                                // выключенная связка не оживает
        Assert.Equal(["del"], r.Router.Log);
    }

    [Fact]
    public async Task Restore_after_restart_raises_the_link_on_the_first_check()
    {
        var r = new Rig();
        r.J.Restore(wanted: true);
        Assert.Equal(JointState.Waiting, r.J.State);
        Assert.Equal(JointTexts.Starting, r.J.Status);
        await r.J.TickAsync();
        Assert.Equal(JointState.Active, r.J.State);
        Assert.True(r.Router.Present);

        var off = new Rig();
        off.J.Restore(wanted: false);                                         // выключена по умолчанию
        await off.J.TickAsync();
        Assert.Equal(JointState.Off, off.J.State);
        Assert.Empty(off.Router.Sets);
    }

    [Fact]
    public async Task Prepare_start_raises_the_link_before_the_pc_is_enabled()
    {
        var r = new Rig();
        r.J.Restore(wanted: true);
        r.Pc.Running = false;
        Assert.True(await r.J.PrepareStartAsync());
        Assert.True(r.Pc.Info!.Active);
        Assert.False(r.Pc.Running);                                           // сам запуск — дело окна, после этого

        r.Router.NotReady = JointTexts.Fallback;
        Assert.False(await r.J.PrepareStartAsync());
    }

    [Fact]
    public async Task Parallel_ticks_do_not_pile_up_and_are_not_lost()
    {
        var r = new Rig();
        await r.J.EnableAsync();
        r.Router.Log.Clear();
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => r.J.TickAsync()));
        await r.J.WhenIdleAsync();
        Assert.InRange(r.Router.Log.Count(l => l == "get"), 1, 5);
        Assert.Single(r.Router.Sets);
    }
}
