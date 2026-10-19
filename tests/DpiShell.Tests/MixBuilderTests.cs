using System.Security.Authentication;
using DpiShell.Core;
using DpiShell.Core.Router;

namespace DpiShell.Tests;

/// <summary>Смешанные стратегии, шаг 1 (docs/research/mixed-strategies.md): сборка из вердиктов, части, связанные роли, id, проверочный прогон.</summary>
public sealed class MixBuilderTests : IDisposable
{
    readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    const string Split = "--dpi-desync=multisplit --dpi-desync-split-pos=1";
    const string Fake = "--dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fooling=ts";
    const string Disorder = "--dpi-desync=multidisorder --dpi-desync-split-pos=1,midsld";

    static readonly TargetGroup Yt = new("youtube", "YouTube", [new("https://yt.example/a"), new("https://yt.example/b")]);
    static readonly TargetGroup Ds = new("discord", "Discord", [new("https://ds.example/a"), new("https://ds.example/b")]);
    static readonly TargetGroup Us = new("user", "Свои", [new("https://us.example/a")]);
    static readonly TargetGroup Ctl = new("control", "Обычные сайты", [new("https://ok.example/")], Control: true);

    static Strategy Strat(string id, string title, params (string Role, string Args)[] roles) =>
        new(id, title, "flowseal", roles.ToDictionary(r => r.Role, r => r.Args));

    /// <summary>Оценка: открытые адреса по группам — сколько первых адресов группы открылось; control открыт всегда.</summary>
    static StrategyScore Scored(Strategy s, IReadOnlyList<TargetGroup> groups, Dictionary<string, int> open, string? broken = null)
    {
        var verdicts = groups.SelectMany(g => g.Targets.Select((t, i) =>
            new TargetVerdict(g.Id, t, g.Control ? broken is null : i < open.GetValueOrDefault(g.Id), 0.5, []))).ToList();
        return Selector.Score(s, groups, verdicts);
    }

    static SelectionReport Report(IReadOnlyList<TargetGroup> groups, params StrategyScore[] scores) => new([], groups, scores, false);

    static (Strategy A, Strategy B, Strategy C, SelectionReport R) Typical()
    {
        var groups = new[] { Yt, Ds, Us, Ctl };
        // A — хороша для YouTube, B — для остального, C — посредственна. Идеальной одиночной нет.
        var a = Strat("alt", "general (ALT)", ("google", Split), ("general", Fake), ("ipset_tcp", Fake), ("quic", Fake));
        var b = Strat("alt10", "general (ALT10)", ("google", Fake), ("general", Disorder), ("ipset_tcp", Disorder), ("game_tcp", Disorder), ("quic", Split));
        var c = Strat("alt2", "general (ALT2)", ("google", Fake), ("general", Fake));
        var r = Report(groups,
            Scored(a, groups, new() { ["youtube"] = 2, ["discord"] = 0, ["user"] = 0 }),
            Scored(b, groups, new() { ["youtube"] = 0, ["discord"] = 2, ["user"] = 1 }),
            Scored(c, groups, new() { ["youtube"] = 1, ["discord"] = 1, ["user"] = 0 }));
        return (a, b, c, r);
    }

    [Fact]
    public void Build_TakesYoutubeFromBestForYoutube_RestFromBestForRest()
    {
        var (a, b, _, r) = Typical();
        var plan = MixBuilder.Build(r)!;

        Assert.NotNull(plan);
        Assert.Equal("mix", plan.Mix.Source);
        Assert.Same(r.Scores[0], plan.Youtube);
        Assert.Same(r.Scores[1], plan.Rest);
        Assert.Equal(a.Roles["google"], plan.Mix.Roles["google"]);
        Assert.Equal(b.Roles["general"], plan.Mix.Roles["general"]);
        Assert.Equal(new Dictionary<string, string> { ["youtube"] = "alt", ["discord"] = "alt10", ["user"] = "alt10" }, plan.Mix.Parts);
    }

    [Fact]
    public void Build_RelatedRoles_AllFromOnePart_OnlyGoogleFromYoutubePart()
    {
        var (a, b, _, r) = Typical();
        var mix = MixBuilder.Build(r)!.Mix;

        // ipset_*, game_*, quic, general — из той же части, что и general (нулевая фаза ipset не расходится с общей частью).
        foreach (var role in new[] { "general", "ipset_tcp", "game_tcp", "quic" })
            Assert.Equal(b.Roles[role], mix.Roles[role]);
        Assert.Equal(b.Roles.Keys.Order(), mix.Roles.Keys.Order());   // роли YouTube-части (ipset_tcp, quic из A) в смесь не попали
        Assert.NotEqual(a.Roles["ipset_tcp"], mix.Roles["ipset_tcp"]);
        Assert.Null(StrategyExchange.CheckRoles(mix.Roles));
    }

    [Fact]
    public void Build_IdByParts_Deterministic_AndTitleShowsParts()
    {
        var (a, b, _, r) = Typical();
        var m1 = MixBuilder.Build(r)!.Mix;
        var m2 = MixBuilder.Build(r)!.Mix;

        Assert.Equal("mix-alt-alt10", m1.Id);
        Assert.Equal(m1.Id, m2.Id);
        Assert.Equal("Смешанная · YouTube — ALT · остальное — ALT10", m1.Title);
        Assert.Equal("Смешанная · YouTube — ALT · остальное — ALT10", MixBuilder.MakeTitle(a, b));
    }

    [Fact]
    public void MakeId_LongParts_AreHashedInto64()
    {
        var x = Strat("found-261016-2213-1-with-a-very-long-suffix-aaaaa", "x", ("general", Split));
        var y = Strat("found-261016-2213-2-with-a-very-long-suffix-bbbbb", "y", ("general", Split));
        var id = MixBuilder.MakeId(x, y);
        Assert.True(id.Length <= 64);
        Assert.Equal(id, MixBuilder.MakeId(x, y));
        Assert.NotEqual(id, MixBuilder.MakeId(y, x));
    }

    [Fact]
    public void Build_PerfectSingleExists_NoMix()
    {
        var (_, _, _, r) = Typical();
        var groups = new[] { Yt, Ds, Us, Ctl };
        var perfect = Strat("alt3", "general (ALT3)", ("google", Fake), ("general", Fake));
        var r2 = r with { Scores = [.. r.Scores, Scored(perfect, groups, new() { ["youtube"] = 2, ["discord"] = 2, ["user"] = 1 })] };
        Assert.Null(MixBuilder.Build(r2));
    }

    [Fact]
    public void Build_SameStrategyBestForBoth_NoMix()
    {
        var groups = new[] { Yt, Ds, Ctl };
        var a = Strat("alt", "general (ALT)", ("google", Split), ("general", Fake));
        var b = Strat("alt2", "general (ALT2)", ("google", Fake), ("general", Fake));
        var r = Report(groups,
            Scored(a, groups, new() { ["youtube"] = 2, ["discord"] = 1 }),
            Scored(b, groups, new() { ["youtube"] = 1, ["discord"] = 0 }));
        Assert.Null(MixBuilder.Build(r));
    }

    [Fact]
    public void Build_BreakingOrderedStrategies_AreNeverParts()
    {
        var groups = new[] { Yt, Ds, Ctl };
        var a = Strat("alt", "general (ALT)", ("google", Split), ("general", Fake));
        var b = Strat("alt10", "general (ALT10)", ("google", Fake), ("general", Disorder));
        var bad = Strat("alt5", "general (ALT5)", ("google", Disorder), ("general", Disorder));
        // bad открыла бы всё, но ломает обычные сайты (Score → ошибка и ноль групп).
        var r = Report(groups,
            Scored(a, groups, new() { ["youtube"] = 2, ["discord"] = 0 }),
            Scored(b, groups, new() { ["youtube"] = 0, ["discord"] = 2 }),
            Scored(bad, groups, new() { ["youtube"] = 2, ["discord"] = 2 }, broken: "x"));
        var plan = MixBuilder.Build(r)!;
        Assert.Equal("alt", plan.Youtube.Strategy.Id);
        Assert.Equal("alt10", plan.Rest.Strategy.Id);
    }

    [Fact]
    public void Build_NoYoutubeOrNoRestGroup_NoMix()
    {
        var a = Strat("alt", "general (ALT)", ("google", Split), ("general", Fake));
        var b = Strat("alt10", "general (ALT10)", ("google", Fake), ("general", Disorder));
        var onlyYt = new[] { Yt, Ctl };
        Assert.Null(MixBuilder.Build(Report(onlyYt, Scored(a, onlyYt, new() { ["youtube"] = 1 }), Scored(b, onlyYt, new() { ["youtube"] = 0 }))));
        var onlyDs = new[] { Ds, Ctl };
        Assert.Null(MixBuilder.Build(Report(onlyDs, Scored(a, onlyDs, new() { ["discord"] = 1 }), Scored(b, onlyDs, new() { ["discord"] = 0 }))));
    }

    [Fact]
    public void Build_Tie_PrefersOverallBest_ThenQuieter()
    {
        var groups = new[] { Yt, Ds, Ctl };
        var loud = Strat("loud", "general (LOUD)", ("google", "--dpi-desync=fake --dpi-desync-repeats=11 --dpi-desync-fooling=ts"), ("general", Fake));
        var quiet = Strat("quiet", "general (QUIET)", ("google", Split), ("general", Fake));
        var other = Strat("other", "general (OTHER)", ("google", Fake), ("general", Disorder));
        var r = Report(groups,
            Scored(loud, groups, new() { ["youtube"] = 2, ["discord"] = 0 }),
            Scored(quiet, groups, new() { ["youtube"] = 2, ["discord"] = 0 }),
            Scored(other, groups, new() { ["youtube"] = 0, ["discord"] = 2 }));
        // Лучшей одиночной по порядку Best станет первая из равных (loud) — берём её; без этого выбрала бы тихую.
        Assert.Equal(r.Best!.Strategy.Id, MixBuilder.Build(r)!.Youtube.Strategy.Id);

        var r2 = Report(groups,
            Scored(other, groups, new() { ["youtube"] = 0, ["discord"] = 2 }),
            Scored(loud, groups, new() { ["youtube"] = 1, ["discord"] = 0 }),
            Scored(quiet, groups, new() { ["youtube"] = 1, ["discord"] = 0 }));
        Assert.Equal("quiet", MixBuilder.Build(r2)!.Youtube.Strategy.Id);   // равны по доле — тише приём
    }

    [Fact]
    public void Build_PreviousMixesAreNotParts()
    {
        var (a, b, c, r) = Typical();
        var groups = new[] { Yt, Ds, Us, Ctl };
        var old = new Strategy("mix-x-y", "Смешанная", "mix", new(a.Roles));
        var r2 = r with { Scores = [.. r.Scores, Scored(old, groups, new() { ["youtube"] = 2, ["discord"] = 2, ["user"] = 0 })] };
        var plan = MixBuilder.Build(r2)!;
        Assert.DoesNotContain(plan.Mix.Parts!.Values, v => v == "mix-x-y");
    }

    [Fact]
    public void BeatsSingle_RequiresStrictGain()
    {
        var groups = new[] { Yt, Ds, Ctl };
        var s = Strat("s", "s", ("general", Fake));
        var one = Scored(s, groups, new() { ["youtube"] = 2, ["discord"] = 0 });
        var oneMore = Scored(s, groups, new() { ["youtube"] = 2, ["discord"] = 1 });   // группа не прошла (обязательный адрес не открыт), адресов больше
        var same = Scored(s, groups, new() { ["youtube"] = 2, ["discord"] = 0 });
        var both = Scored(s, groups, new() { ["youtube"] = 2, ["discord"] = 2 });
        Assert.False(MixBuilder.BeatsSingle(same, one));
        Assert.True(MixBuilder.BeatsSingle(oneMore, one));
        Assert.True(MixBuilder.BeatsSingle(both, oneMore));
        Assert.False(MixBuilder.BeatsSingle(one, both));
        Assert.True(MixBuilder.BeatsSingle(one, null));
        Assert.False(MixBuilder.BeatsSingle(one with { Error = "ломает" }, null));
    }

    // ---- проверочный прогон в Selector ----

    sealed class FakeSelector(AppPaths paths, HttpProbe probe, Func<Strategy, TargetGroup, int, bool> opens) : Selector(paths, probe)
    {
        public List<string> Ran { get; } = [];

        public override Task<StrategyScore> RunOneAsync(Strategy s, IReadOnlyList<TargetGroup> groups, CancellationToken ct)
        {
            Ran.Add(s.Id);
            var verdicts = groups.SelectMany(g => g.Targets.Select((t, i) => new TargetVerdict(g.Id, t, g.Control || opens(s, g, i), 0.4, []))).ToList();
            return Task.FromResult(Score(s, groups, verdicts));
        }
    }

    async Task<(SelectionReport R, FakeSelector Sel)> RunMixAsync(Func<Strategy, TargetGroup, int, bool> opens, bool quick = false, bool mix = true, int count = 3)
    {
        await using var srv = new RawHttpServer((path, s, ct) => path == "/ok"
            ? RawHttpServer.RespondAsync(s, 200, new byte[2_000]) : RawHttpServer.RespondAsync(s, 500, []));
        var groups = new List<TargetGroup>
        {
            new("youtube", "YouTube", [new(srv.Url("/y1")), new(srv.Url("/y2"))]),
            new("discord", "Discord", [new(srv.Url("/d1")), new(srv.Url("/d2"))]),
            new("control", "Обычные сайты", [new(srv.Url("/ok"))], Control: true),
        };
        var strategies = new[]
        {
            Strat("alt", "general (ALT)", ("google", Split), ("general", Fake)),
            Strat("alt10", "general (ALT10)", ("google", Fake), ("general", Disorder)),
            Strat("alt2", "general (ALT2)", ("google", Fake), ("general", Fake)),
        }.Take(count).ToList();
        var sel = new FakeSelector(Make.Paths(_t), new HttpProbe { Attempts = 1, ConnectTimeout = TimeSpan.FromSeconds(5), ReadTimeout = TimeSpan.FromSeconds(5) }, opens)
        { Tls = [SslProtocols.Tls13], Settle = TimeSpan.Zero };
        var r = await sel.RunAsync(strategies, groups, quick, mix: mix);
        return (r, sel);
    }

    /// <summary>alt открывает YouTube, alt10 — Discord; смесь alt + alt10 открывает всё, и только её google из alt.</summary>
    static bool Complementary(Strategy s, TargetGroup g, int i) => s.Id switch
    {
        "alt" => g.Id == "youtube",
        "alt10" => g.Id == "discord",
        // Смесь открывает обе группы только когда google от alt, а general от alt10.
        var id when id.StartsWith("mix-") => s.Roles["google"] == Split && s.Roles["general"] == Disorder,
        _ => false,
    };

    [Fact]
    public async Task Selector_NoPerfectSingle_MixIsVerifiedOnce_AndBecomesBest()
    {
        var (r, sel) = await RunMixAsync(Complementary);

        Assert.Equal(["alt", "alt10", "alt2", "mix-alt-alt10"], sel.Ran);   // ровно один проверочный прогон смеси, после каталога
        var mix = Assert.Single(r.Scores, s => s.Strategy.Source == "mix");
        Assert.True(mix.Perfect);
        Assert.Equal(mix.Strategy.Id, r.Best!.Strategy.Id);
        Assert.Equal("alt", mix.Strategy.Parts!["youtube"]);
    }

    [Fact]
    public async Task Selector_MixNotBetter_NotInReport()
    {
        // Каждая открывает свою группу, а смесь не открывает ничего (части мешают друг другу): прогон был, в отчёте её нет.
        var (r, sel) = await RunMixAsync((s, g, i) => s.Id switch { "alt" => g.Id == "youtube", "alt10" => g.Id == "discord", _ => false });

        Assert.Contains("mix-alt-alt10", sel.Ran);
        Assert.DoesNotContain(r.Scores, s => s.Strategy.Source == "mix");
        Assert.Equal(3, r.Scores.Count);
    }

    [Fact]
    public async Task Selector_PerfectSingle_NoMixRun_EvenWhenFullRun()
    {
        var (r, sel) = await RunMixAsync((s, g, i) => s.Id == "alt2" || s.Id == "alt" && g.Id == "youtube");

        Assert.DoesNotContain(sel.Ran, id => id.StartsWith("mix-"));
        Assert.DoesNotContain(r.Scores, s => s.Strategy.Source == "mix");
    }

    [Fact]
    public async Task Selector_QuickStopsOnPerfect_DoesNotReachMix()
    {
        var (r, sel) = await RunMixAsync((s, g, i) => s.Id == "alt", quick: true);

        Assert.Equal(["alt"], sel.Ran);
        Assert.Single(r.Scores);
    }

    [Fact]
    public async Task Selector_MixOff_ByDefault_NoMix()
    {
        var (r, sel) = await RunMixAsync(Complementary, mix: false);
        Assert.DoesNotContain(sel.Ran, id => id.StartsWith("mix-"));
        Assert.Equal(3, r.Scores.Count);
    }

    // ---- файл своих стратегий ----

    [Fact]
    public void AddMix_SavesWithParts_ReplacesSameId_KeepsFive_ProtectsChosen()
    {
        var p = Make.Paths(_t);
        var roles = new Dictionary<string, string> { ["google"] = Split, ["general"] = Fake };
        Strategy M(int i) => new($"mix-a-b{i}", "Смешанная · " + i, "mix", new(roles), new() { ["youtube"] = "a", ["discord"] = "b" + i });

        for (int i = 1; i <= 5; i++) UserStrategies.AddMix(p, M(i));
        UserStrategies.AddMix(p, M(3));   // повтор — на месте, без копий
        Assert.Equal(5, UserStrategies.Load(p).Count);

        UserStrategies.AddMix(p, M(6), keepId: "mix-a-b1");   // выбранная сейчас остаётся, уходит следующая по старшинству
        var ids = UserStrategies.Load(p).Select(s => s.Id).ToList();
        Assert.Contains("mix-a-b1", ids);
        Assert.DoesNotContain("mix-a-b2", ids);
        Assert.Contains("mix-a-b6", ids);
        Assert.Equal(5, ids.Count);

        var loaded = UserStrategies.Load(p).First(s => s.Id == "mix-a-b6");
        Assert.Equal("mix", loaded.Source);
        Assert.Equal("b6", loaded.Parts!["discord"]);
        Assert.Equal(Fake, loaded.Roles["general"]);
    }

    [Fact]
    public void AddMix_DoesNotTouchPersonalOrFound_AndRejectsOtherSources()
    {
        var p = Make.Paths(_t);
        var own = UserStrategies.AddPersonal(p, "Моя", new Dictionary<string, string> { ["general"] = Fake }, "user");
        for (int i = 0; i < 7; i++)
            UserStrategies.AddMix(p, new($"mix-a-b{i}", "m", "mix", new() { ["general"] = Fake }, new() { ["youtube"] = "a" }));
        var all = UserStrategies.Load(p);
        Assert.Contains(all, s => s.Id == own.Id);
        Assert.Equal(5, all.Count(s => s.Source == "mix"));
        Assert.Throws<ArgumentException>(() => UserStrategies.AddMix(p, new("x", "x", "user", new() { ["general"] = Fake })));
        Assert.Throws<InvalidDataException>(() => UserStrategies.AddMix(p, new("x", "x", "mix", new() { ["general"] = "--evil=1" })));
    }

    [Fact]
    public void ToRouter_MixKeepsRoles_TitleCarriesParts()
    {
        var (_, _, _, r) = Typical();
        var mix = MixBuilder.Build(r)!.Mix;
        var sent = RouterClient.ToRouter(mix);

        Assert.Equal("u-mix-alt-alt10", sent.Id);
        Assert.Equal(mix.Roles, sent.Roles);
        Assert.Equal(mix.Title, sent.Title);
        Assert.Contains("YouTube — ALT", sent.Title);
        Assert.True(sent.Title.Length <= 80);
    }

    // ---- данные ----

    static string Norm(string l) => l.Trim().ToLowerInvariant();

    static HashSet<string> Domains(string name) =>
        File.ReadLines(Path.Combine(Repo.Data, "lists", name)).Select(Norm).Where(l => l.Length > 0 && !l.StartsWith('#')).ToHashSet();

    /// <summary>
    /// Домен не должен быть в двух списках ролей: первый подходящий профиль забирает его (google раньше general), и часть Discord
    /// ушла бы в YouTube-часть смешанной. Известный долг — stable.dl2.discordapp.net в google.txt (из списка Flowseal): он разводится на шаге 2
    /// (роль discord_tcp); тест держит долг в пределах одной записи и падает, если появилась новая или старая исчезла.
    /// </summary>
    [Fact]
    public void RoleLists_GoogleAndGeneral_DoNotOverlap_ExceptKnownDebt()
    {
        var google = Domains("google.txt");
        var general = Domains("general.txt");
        var overlap = google.Where(g => general.Any(d => g == d || g.EndsWith("." + d) || d.EndsWith("." + g)))
            .Concat(general.Where(d => google.Any(g => d.EndsWith("." + g)))).Distinct().Order().ToList();

        Assert.Equal(["stable.dl2.discordapp.net"], overlap);
    }
}
