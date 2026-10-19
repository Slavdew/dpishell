using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.Tests;

/// <summary>
/// Продвинутый режим «сайты → стратегия» на ПК (docs/research/mixed-strategies.md §6, договор роутера §5.2): сборка аргументов winws,
/// белый список, хранение у помощника, журнал отката, итог подбора для «auto».
/// </summary>
public sealed class AdvancedSitesTests : IDisposable
{
    readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    static readonly AppPaths P = new(@"C:\Root", @"C:\Seed");
    const string Fake = "--dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fooling=ts --dpi-desync-fake-tls={fake}/tls_clienthello_www_google_com.bin";
    const string Split = "--dpi-desync=multisplit --dpi-desync-split-pos=1";
    const string Quic = "--dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fake-quic={fake}/quic_initial_www_google_com.bin";

    static Strategy Strat(string id, params (string Role, string Args)[] roles) =>
        new(id, id, "flowseal", roles.ToDictionary(r => r.Role, r => r.Args));

    static readonly Strategy Main = Strat("main", ("google", Fake), ("general", Fake), ("quic", Quic));
    static readonly Strategy Other = Strat("other", ("general", Split));                                // без quic

    static AdvancedPlan Plan(string others, params (string[] Domains, Strategy? S)[] rows) =>
        new(others, [.. rows.Select(r => new AdvancedPlanRow(r.Domains, r.S))]);

    static List<string> Build(AdvancedPlan? adv, Strategy? s = null, BuildOptions? baseOptions = null) =>
        [.. WinwsArgs.Build(s ?? Main, P, (baseOptions ?? new BuildOptions()) with { Advanced = adv })];

    /// <summary>Профили командной строки: списки токенов между --new (без --wf-*).</summary>
    static List<List<string>> Profiles(IEnumerable<string> args)
    {
        var all = new List<List<string>> { new() };
        foreach (var a in args.Where(a => !a.StartsWith("--wf-")))
            if (a == "--new") all.Add([]); else all[^1].Add(a);
        return all;
    }

    // ---------- сборка командной строки ----------

    [Fact]
    public void Build_Disabled_IsExactlyTheUsualCommandLine()
    {
        var usual = WinwsArgs.Build(Main, P);
        Assert.Equal(usual, WinwsArgs.Build(Main, P, new BuildOptions(Advanced: null)));
        Assert.Null(AdvancedSites.Plan(new AdvancedConfig { Enabled = false, Rows = [new() { Domains = ["a.com"], Strategy = "main" }] }, _ => Main, "main"));
    }

    [Fact]
    public void Build_RowsFirst_TcpThenQuic_WithHostlistDomains_ThenCatalogRoles()
    {
        var a = Build(Plan("lists", (["example.com", "example.org"], Main)));
        var pr = Profiles(a);

        // Строка: TCP-профиль и (у стратегии есть quic) UDP-профиль — раньше всех ролей каталога.
        Assert.Equal(["--filter-tcp=80,443", "--hostlist-domains=example.com,example.org"], pr[0].Take(2));
        Assert.Contains("--dpi-desync-fake-tls=" + Path.Combine(P.Fake, "tls_clienthello_www_google_com.bin"), pr[0]);
        Assert.Equal(["--filter-udp=443", "--hostlist-domains=example.com,example.org"], pr[1].Take(2));
        Assert.Contains("--dpi-desync-fake-quic=" + Path.Combine(P.Fake, "quic_initial_www_google_com.bin"), pr[1]);
        // Дальше — роли каталога как обычно: quic, google, general.
        Assert.Equal("--filter-udp=443", pr[2][0]);
        Assert.Contains("--hostlist=" + Path.Combine(P.Lists, "general.txt"), pr[2]);
        Assert.Equal(["--filter-tcp=443", "--hostlist=" + Path.Combine(P.Lists, "google.txt")], pr[3].Take(2));
        Assert.Equal(5, pr.Count);
        Assert.DoesNotContain(a, x => x.Contains("{fake}") || x.Contains("hostlist-exclude-domains"));
    }

    [Fact]
    public void Build_RowWithoutQuic_OnlyTcpProfile()
    {
        var pr = Profiles(Build(Plan("lists", (["a.example"], Other)), Strat("cur", ("general", Fake))));
        Assert.Equal(["--filter-tcp=80,443", "--hostlist-domains=a.example", .. Split.Split(' ')], pr[0]);
        Assert.Equal(2, pr.Count);   // строка + общая роль каталога
        Assert.DoesNotContain(pr, p => p.Contains("--filter-udp=443"));
    }

    [Fact]
    public void Build_TwoRows_InOrderOfRows_AndBeforeAppProfiles()
    {
        var app = new AppProfile { Id = "p0a1b2c3d", Title = "Игра", TcpPorts = "27015", Addresses = ["203.0.113.50/32"], TcpDesync = WinwsArgs.DeriveGameTcp(Fake)! };
        var a = Build(Plan("lists", (["first.example"], Other), (["second.example"], Main)), baseOptions: new BuildOptions(Profiles: [app]));
        var hosts = a.Where(x => x.StartsWith("--hostlist-domains=")).ToList();
        Assert.Equal(["--hostlist-domains=first.example", "--hostlist-domains=second.example", "--hostlist-domains=second.example"], hosts);
        int firstRow = a.IndexOf("--hostlist-domains=first.example"), game = a.IndexOf("--filter-tcp=27015"), quic = a.IndexOf("--hostlist=" + Path.Combine(P.Lists, "general.txt"));
        Assert.True(firstRow < game && game < quic);   // строки → профили программ → роли каталога
    }

    [Fact]
    public void Build_Direct_ExcludesDomainsInEveryCatalogRole_NotInRows()
    {
        var game = new BuildOptions(IpsetMode.List, GameMode.TcpUdp);
        var a = Build(Plan("lists", (["bank.example", "pay.example"], null), (["watch.example"], Other)), Strat("full",
            ("quic", Quic), ("discord_udp", "--dpi-desync=fake"), ("discord_media", Split), ("discord_tcp", Split), ("google", Fake), ("general", Fake),
            ("ipset_udp", Quic), ("ipset_tcp", Fake)), game);
        var pr = Profiles(a);
        var exclude = "--hostlist-exclude-domains=bank.example,pay.example";

        Assert.DoesNotContain(exclude, pr[0]);                       // профиль строки со своей стратегией исключения не несёт
        Assert.Equal(1 + 8 + 2, pr.Count);                           // строка + 8 ролей каталога + игровые (режим «Все»: TCP и UDP)
        foreach (var role in pr.Skip(1)) Assert.Contains(exclude, role);
        Assert.DoesNotContain(a, x => x == "--hostlist-domains=bank.example,pay.example");   // direct не получает профиля со стратегией
    }

    [Fact]
    public void Build_OthersDirect_NoCatalogRoles_OnlyRows()
    {
        var a = Build(Plan("direct", (["only.example"], Main)));
        var pr = Profiles(a);
        Assert.Equal(2, pr.Count);                                   // TCP и QUIC этой строки
        Assert.All(pr, p => Assert.Contains("--hostlist-domains=only.example", p));
        Assert.DoesNotContain(a, x => x.Contains("general.txt") || x.Contains("google.txt"));

        // Строк со стратегией нет вовсе — командная строка пустая (Shell.Enable в этом случае отказывает: оптимизировать нечего).
        Assert.Empty(Profiles(Build(Plan("direct", (["a.example"], null)))).SelectMany(p => p));
    }

    [Fact]
    public void Build_OthersAll_GeneralAndQuicWithoutNameList_ButKeepOwnListsAndExcludes()
    {
        var s = Strat("full", ("quic", Quic), ("discord_tcp", Split), ("google", Fake), ("general", Fake));
        var pr = Profiles(Build(Plan("all"), s));
        var quic = pr.Single(p => p[0] == "--filter-udp=443");
        var general = pr.Single(p => p[0] == "--filter-tcp=80,443" && !p.Any(x => x.Contains("discord.txt")));
        foreach (var p in new[] { quic, general })
        {
            Assert.DoesNotContain(p, x => x.Contains("general.txt") || x.Contains(DataStore.UserGeneral));
            Assert.Contains("--hostlist-exclude=" + Path.Combine(P.Lists, "exclude.txt"), p);       // исключения остаются
            Assert.Contains("--hostlist-exclude=" + Path.Combine(P.Lists, DataStore.UserExclude), p);
        }
        Assert.Contains(pr, p => p.Contains("--hostlist=" + Path.Combine(P.Lists, "google.txt")));   // свои списки YouTube и Discord — на месте
        Assert.Contains(pr, p => p.Contains("--hostlist=" + Path.Combine(P.Lists, "discord.txt")));
        // В режиме «по спискам» общий список имён есть.
        Assert.Contains(Profiles(Build(Plan("lists"), s)).Single(p => p[0] == "--filter-udp=443"), x => x.Contains("general.txt"));
    }

    [Fact]
    public void Build_RowsAreBuiltFromWhitelistedTokensOnly()
    {
        // Стратегия с неразрешённым параметром (подложили файл) — профиль строки не собирается.
        var evil = Strat("evil", ("general", "--dpi-desync=fake --wf-raw=x"));
        var a = Build(Plan("lists", (["a.example"], evil)));
        Assert.DoesNotContain(a, x => x.Contains("wf-raw") || x.Contains("a.example"));
        // Домен не по шаблону в плане — в командную строку не попадает.
        var b = Build(Plan("lists", (["bad domain.example", "x,y.example", "ok.example"], Main)));
        Assert.Contains("--hostlist-domains=ok.example", b);
        Assert.DoesNotContain(b, x => x.Contains("bad") || x.Contains("x,y"));
    }

    [Fact]
    public void Build_JoinStaysSafe_NoQuotesOrSpacesInRowArguments()
    {
        var a = Build(Plan("lists", (["a.example", "b.example"], Main), (["c.example"], null)));
        var line = WinwsArgs.Join(a);
        Assert.Contains("--hostlist-domains=a.example,b.example", line);
        Assert.Contains("--hostlist-exclude-domains=c.example", line);
        Assert.DoesNotContain("\"--hostlist-domains", line);
    }

    // ---------- конфигурация → план ----------

    [Theory]
    [InlineData("example.com", "example.com")]
    [InlineData("  Example.COM ", "example.com")]
    [InlineData("https://www.example.com/path?q=1", "example.com")]
    [InlineData("a-b.c1.example.org", "a-b.c1.example.org")]
    public void Domain_Accepted_InNormalForm(string raw, string expected) => Assert.Equal(expected, AdvancedSites.Domain(raw));

    [Fact]
    public void Domain_Russian_BecomesPunycode()
    {
        var ascii = new System.Globalization.IdnMapping().GetAscii("мойсайт.рф");
        Assert.StartsWith("xn--", ascii);
        Assert.Equal(ascii, AdvancedSites.Domain("мойсайт.рф"));
        Assert.Equal(ascii, AdvancedSites.Domain("https://МойСайт.рф/страница"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("localhost")]
    [InlineData("exa mple.com")]
    [InlineData("a.com,b.com")]
    [InlineData("^example.com")]
    [InlineData("*.example.com")]
    [InlineData("example.com\\x")]
    [InlineData("exa\"mple.com")]
    [InlineData("-bad-.com")]
    [InlineData("1.2.3.4")]
    public void Domain_Rejected(string raw) => Assert.Null(AdvancedSites.Domain(raw));

    [Fact]
    public void Plan_AutoUsesResolved_ThenCurrent_DirectHasNoStrategy_MissingIsSkipped()
    {
        var all = new[] { Main, Other }.ToDictionary(s => s.Id);
        Strategy? Find(string id) => all.GetValueOrDefault(id);
        var c = new AdvancedConfig
        {
            Enabled = true, Others = "lists",
            Rows =
            [
                new() { Domains = ["a.example"], Strategy = "auto", Resolved = "other" },
                new() { Domains = ["b.example"], Strategy = "auto" },
                new() { Domains = ["c.example"], Strategy = "direct" },
                new() { Domains = ["d.example"], Strategy = "main" },
                new() { Domains = ["e.example"], Strategy = "deleted" },
                new() { Domains = ["Bad Domain"], Strategy = "main" },
            ],
        };
        var plan = AdvancedSites.Plan(c, Find, "main")!;
        Assert.Equal(["other", "main", null, "main"], plan.Rows.Select(r => r.Strategy?.Id));
        Assert.Equal(["c.example"], plan.Direct);
        Assert.Equal(3, plan.Applied.Count());
        // «auto» без итога и без текущей стратегии — не применяется.
        Assert.Equal(1, AdvancedSites.Plan(c, Find, null)!.Rows.Count(r => r.Strategy is null)) ;
        Assert.DoesNotContain(AdvancedSites.Plan(c, Find, null)!.Rows, r => r.Domains.Contains("b.example"));
    }

    [Fact]
    public void Plan_Limits_TwentyRows_FiftyDomains()
    {
        var rows = Enumerable.Range(0, 25).Select(i => new AdvancedRow { Domains = [$"s{i}.example"], Strategy = "main" }).ToList();
        Assert.Equal(AdvancedSites.MaxRows, AdvancedSites.Plan(new() { Enabled = true, Rows = rows }, _ => Main, null)!.Rows.Count);
        var many = new AdvancedConfig { Enabled = true, Rows = [new() { Domains = [.. Enumerable.Range(0, 51).Select(i => $"s{i}.example")], Strategy = "main" }] };
        Assert.Empty(AdvancedSites.Plan(many, _ => Main, null)!.Rows);   // 51 сайт — строка негодная целиком
        Assert.Contains("больше", AdvancedSites.Problem(many)!);
    }

    [Fact]
    public void BuildOptionsFrom_State_TakesAdvancedOnlyWhenEnabled()
    {
        var st = new AppState { StrategyId = "main", Advanced = new() { Enabled = true, Others = "direct", Rows = [new() { Domains = ["a.example"], Strategy = "auto" }] } };
        Strategy? Find(string id) => id == "main" ? Main : null;
        var o = BuildOptions.From(st, DateTime.UtcNow, Find, st.StrategyId);
        Assert.Equal("direct", o.Advanced!.Others);
        Assert.Equal("main", Assert.Single(o.Advanced.Rows).Strategy!.Id);
        st.Advanced.Enabled = false;
        Assert.Null(BuildOptions.From(st, DateTime.UtcNow, Find, st.StrategyId).Advanced);
        Assert.Null(BuildOptions.From(st, DateTime.UtcNow).Advanced);   // без каталога режим не учитывается
        // В state.json строки хранятся и при выключенном режиме.
        var round = System.Text.Json.JsonSerializer.Serialize(st, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower });
        Assert.Contains("a.example", round);
    }

    // ---------- итог подбора для «auto» ----------

    static TargetVerdict V(string group, bool ok, double s = 0.4) => new(group, new Target($"https://{group}.example/"), ok, s, []);

    [Fact]
    public void Resolve_BestStrategyPerRow_ErrorsAndUntestedRowsStay()
    {
        var groups = new List<TargetGroup>
        {
            new("youtube", "YouTube", [new("https://yt.example/")]),
            new("adv-0", "Сайты строки 1", [new("https://a.example/")], Extra: true),
            new("adv-2", "Сайты строки 3", [new("https://c.example/")], Extra: true),
        };
        StrategyScore S(string id, bool a, bool c, double sec = 0.4, string? error = null) =>
            new(Strat(id, ("general", Fake)), [V("youtube", true), V("adv-0", a, sec), V("adv-2", c, sec)], 1, 1, error);
        var report = new SelectionReport([], groups, [S("slow", true, false, 1.0), S("fast", true, false, 0.2), S("broken", true, true, 0.1, "ломает обычные сайты"), S("c-only", false, true)], false);
        var cfg = new AdvancedConfig
        {
            Enabled = true,
            Rows =
            [
                new() { Domains = ["a.example"], Strategy = "auto", Resolved = "old" },
                new() { Domains = ["b.example"], Strategy = "auto", Resolved = "keep" },   // группа adv-1 не проверялась — итог остаётся
                new() { Domains = ["c.example"], Strategy = "auto" },
                new() { Domains = ["d.example"], Strategy = "direct" },
            ],
        };

        Assert.True(AdvancedSites.Resolve(cfg, report));
        Assert.Equal("fast", cfg.Rows[0].Resolved);       // открылось у slow и fast — быстрее fast; broken ломает обычные сайты и не берётся
        Assert.Equal("keep", cfg.Rows[1].Resolved);
        Assert.Equal("c-only", cfg.Rows[2].Resolved);
        Assert.Null(cfg.Rows[3].Resolved);
        Assert.False(AdvancedSites.Resolve(cfg, report));  // повторно — без изменений

        // Ни у кого ничего не открылось — итога нет, строка идёт по текущей стратегии.
        var none = new SelectionReport([], groups, [S("x", false, false)], false);
        Assert.True(AdvancedSites.Resolve(cfg, none));
        Assert.Null(cfg.Rows[0].Resolved);
    }

    [Fact]
    public void Groups_ForAutoRows_AreExtra_FirstDomain_OnlyWhenEnabled()
    {
        var c = new AdvancedConfig { Enabled = true, Rows = [new() { Domains = ["a.example", "b.example"], Strategy = "auto" }, new() { Domains = ["c.example"], Strategy = "main" }, new() { Domains = ["d.example"], Strategy = "auto" }] };
        var g = AdvancedSites.Groups(c);
        Assert.Equal(["adv-0", "adv-2"], g.Select(x => x.Id));
        Assert.All(g, x => { Assert.True(x.Extra); Assert.False(x.Goal); });
        Assert.Equal("https://a.example/", g[0].Targets.Single().Url);
        c.Enabled = false;
        Assert.Empty(AdvancedSites.Groups(c));
    }

    [Fact]
    public void ExtraGroup_DoesNotCountAgainstStrategy_AndNotInHistoryWithAddresses()
    {
        var groups = new List<TargetGroup> { new("youtube", "YouTube", [new("https://yt.example/")]), new("adv-0", "Сайты строки 1", [new("https://secret-site.example/")], Extra: true) };
        var score = Selector.Score(Main, groups, [V("youtube", true), new("adv-0", new Target("https://secret-site.example/"), false, 0, [])]);
        Assert.Equal(1, score.GroupsTotal);          // adv-0 в «из M» не входит
        Assert.True(score.Perfect);                  // и стратегию не бракует
        var p = Make.Paths(_t);
        var file = ResultStore.Save(p, new SelectionReport([], groups, [score], false));
        Assert.DoesNotContain("secret-site.example", File.ReadAllText(file));
    }

    // ---------- Shell: хранение, журнал, откат ----------

    Shell NewShell()
    {
        var shell = new Shell(Make.Paths(_t));
        shell.Data.Seed();
        return shell;
    }

    static AdvancedRowInput Row(string strategy, params string[] domains) => new(domains, strategy);

    [Fact]
    public void SetAdvanced_NormalizesSavesAndKeepsResolved()
    {
        var shell = NewShell();
        var id = shell.Catalog.Strategies[0].Id;
        var saved = shell.SetAdvanced(true, "lists", [Row("auto", "HTTPS://WWW.Example.com/a", "example.com", "Другой.рф"), Row(id, "b.example"), Row("direct", "c.example")]);

        Assert.Equal(["example.com", new System.Globalization.IdnMapping().GetAscii("другой.рф")], saved.Rows[0].Domains);   // нормальный вид, без повторов
        var state = AppState.Load(shell.Paths);
        Assert.True(state.Advanced.Enabled);
        Assert.Equal(3, state.Advanced.Rows.Count);
        Assert.Equal(id, state.Advanced.Rows[1].Strategy);

        // Итог подбора ставит подбор; повторное сохранение тех же сайтов его не стирает, изменённые сайты — стирают.
        shell.State.Advanced.Rows[0].Resolved = id;
        shell.SetAdvanced(true, "lists", [Row("auto", "example.com", "другой.рф"), Row(id, "b.example"), Row("direct", "c.example")]);
        Assert.Equal(id, shell.State.Advanced.Rows[0].Resolved);
        shell.SetAdvanced(true, "lists", [Row("auto", "new.example")]);
        Assert.Null(shell.State.Advanced.Rows[0].Resolved);

        // Командная строка теперь с профилем строки (стратегия «auto» без итога → текущая).
        shell.State.StrategyId = id;
        Assert.Contains("--hostlist-domains=new.example", WinwsArgs.Build(shell.Catalog.Strategies[0], shell.Paths, shell.CurrentOptions));
    }

    [Theory]
    [InlineData("lists", "bad domain", "auto", "Домен вида example.com")]
    [InlineData("lists", "example.com", "no-such-strategy", "такой стратегии нет")]
    [InlineData("lists", "example.com", "--wf-raw=x", "такой стратегии нет")]
    [InlineData("wide", "example.com", "auto", "Остальные адреса")]
    public void SetAdvanced_Rejects_AndLeavesStateAlone(string others, string domain, string strategy, string part)
    {
        var shell = NewShell();
        var e = Assert.Throws<InvalidOperationException>(() => shell.SetAdvanced(true, others, [Row(strategy, domain)]));
        Assert.Contains(part, e.Message);
        Assert.False(AppState.Load(shell.Paths).Advanced.Enabled);
        Assert.Empty(shell.Journal.Entries());
    }

    [Fact]
    public void SetAdvanced_LimitsAndNothingToDo()
    {
        var shell = NewShell();
        var tooMany = Enumerable.Range(0, 21).Select(i => Row("auto", $"s{i}.example")).ToList();
        Assert.Contains("Строк больше", Assert.Throws<InvalidOperationException>(() => shell.SetAdvanced(true, "lists", tooMany)).Message);
        Assert.Contains("Строка 1", Assert.Throws<InvalidOperationException>(() => shell.SetAdvanced(true, "lists", [new AdvancedRowInput([], "auto")])).Message);
        Assert.Contains("оптимизировать нечего", Assert.Throws<InvalidOperationException>(() => shell.SetAdvanced(true, "direct", [Row("direct", "a.example")])).Message);
        // Выключенный режим хранит что угодно допустимое, в том числе «остальные напрямую» без строк.
        shell.SetAdvanced(false, "direct", [Row("direct", "a.example")]);
        Assert.False(shell.State.Advanced.Enabled);
        Assert.Single(shell.State.Advanced.Rows);
        Assert.Null(shell.CurrentOptions.Advanced);
        // Профиль программы — тоже «есть что обрабатывать».
        shell.State.AppProfiles.Add(new AppProfile { Id = "p0a1b2c3d", Title = "Игра", TcpPorts = "27015", Addresses = ["203.0.113.50/32"], TcpDesync = WinwsArgs.DeriveGameTcp(Fake)! });
        shell.SetAdvanced(true, "direct", [Row("direct", "a.example")]);
        Assert.True(shell.State.Advanced.Enabled);
    }

    [Fact]
    public async Task Journal_FirstChangeRecorded_NextOnesUpdate_UndoRestoresPrevious()
    {
        var shell = NewShell();
        var id = shell.Catalog.Strategies[0].Id;
        shell.SetAdvanced(true, "lists", [Row(id, "a.example")]);
        shell.SetAdvanced(true, "all", [Row(id, "a.example"), Row("direct", "b.example")]);

        var entry = Assert.Single(shell.Journal.Active(ChangeKinds.Advanced));
        Assert.Contains("включён", entry.Title);
        Assert.False(AdvancedConfig.FromJson(entry.Before).Enabled);                 // было: выключено
        Assert.Equal("all", AdvancedConfig.FromJson(entry.After).Others);            // стало: последнее сохранённое

        var undo = new AdvancedUndoer(() => shell.State.Advanced, shell.RestoreAdvanced);
        var r = await undo.UndoAsync(entry, default);
        Assert.Equal(UndoStatus.Restored, r.Status);
        Assert.False(shell.State.Advanced.Enabled);
        Assert.Empty(shell.State.Advanced.Rows);
        Assert.False(AppState.Load(shell.Paths).Advanced.Enabled);
        Assert.Equal(UndoStatus.AlreadyBack, (await undo.UndoAsync(entry, default)).Status);

        // Сейчас стоит не то, что ставили мы, — не трогаем.
        shell.SetAdvanced(true, "lists", [Row("direct", "x.example"), Row(id, "y.example")]);
        var changed = await undo.UndoAsync(entry, default);
        Assert.Equal(UndoStatus.ChangedByOthers, changed.Status);
        Assert.True(shell.State.Advanced.Enabled);
    }

    [Fact]
    public void Journal_BackToInitial_ClosesEntry_AndRemoveAllClearsEverything()
    {
        var shell = NewShell();
        shell.SetAdvanced(true, "lists", [Row("auto", "a.example")]);
        Assert.Single(shell.Journal.Active(ChangeKinds.Advanced));
        shell.SetAdvanced(false, "lists", []);                                       // вернули исходный вид
        Assert.Empty(shell.Journal.Active(ChangeKinds.Advanced));

        shell.SetAdvanced(true, "lists", [Row("auto", "a.example")]);
        var backend = PcHelper.Over(shell);
        backend.RemoveAll();
        Assert.False(AppState.Load(shell.Paths).Advanced.Enabled);
        Assert.Empty(AppState.Load(shell.Paths).Advanced.Rows);
        Assert.Empty(shell.Journal.Active(ChangeKinds.Advanced));
    }

    [Fact]
    public void AdvancedInfo_ShowsWhatEachRowUses()
    {
        var shell = NewShell();
        var id = shell.Catalog.Strategies[0].Id;
        shell.State.StrategyId = id;
        shell.SetAdvanced(true, "lists", [Row("auto", "a.example"), Row("direct", "b.example"), Row(shell.Catalog.Strategies[1].Id, "c.example")]);
        shell.State.Advanced.Rows[0].Resolved = shell.Catalog.Strategies[2].Id;
        var info = shell.AdvancedInfo();
        Assert.Equal([shell.Catalog.Strategies[2].Id, null, shell.Catalog.Strategies[1].Id], info.Rows.Select(r => r.Resolved));
        shell.State.Advanced.Rows[0].Resolved = null;
        Assert.Equal(id, shell.AdvancedInfo().Rows[0].Resolved);   // до подбора — текущая стратегия
    }

    // ---------- команды помощника: белый список ----------

    static readonly FakeHelperBackend Catalog = new();
    static HelperCall Parse(string json) => HelperCommands.Parse(json, Catalog);
    static string Rejected(string json) => Assert.Throws<HelperRequestException>(() => Parse(json)).Message;

    [Fact]
    public void Helper_AdvancedSet_ValidRequest_IsTyped()
    {
        var c = Parse("""{"id":1,"cmd":"advanced_set","args":{"enabled":true,"others":"direct","rows":[{"domains":["example.com","example.org","example.com"],"strategy":"alt9"},{"domains":["b.example"]},{"domains":["c.example"],"strategy":"direct"}]}}""");
        var rows = c.Rows("rows")!;
        Assert.Equal(["example.com", "example.org"], rows[0].Domains);   // повтор убран
        Assert.Equal(["alt9", "auto", "direct"], rows.Select(r => r.Strategy));
        Assert.Equal("direct", c.Str("others"));
        Assert.Empty(Parse("""{"id":2,"cmd":"advanced_set","args":{"enabled":false,"others":"lists","rows":[]}}""").Rows("rows")!);
        Parse("""{"id":3,"cmd":"advanced_get"}""");
    }

    [Theory]
    [InlineData("""{"id":1,"cmd":"advanced_set","args":{"enabled":true,"others":"lists"}}""", "обязательного")]
    [InlineData("""{"id":1,"cmd":"advanced_set","args":{"enabled":true,"rows":[]}}""", "обязательного")]
    [InlineData("""{"id":1,"cmd":"advanced_set","args":{"enabled":"yes","others":"lists","rows":[]}}""", "true или false")]
    [InlineData("""{"id":1,"cmd":"advanced_set","args":{"enabled":true,"others":"everything","rows":[]}}""", "допустимо")]
    [InlineData("""{"id":1,"cmd":"advanced_set","args":{"enabled":true,"others":"lists","rows":{"a":1}}}""", "список")]
    [InlineData("""{"id":1,"cmd":"advanced_set","args":{"enabled":true,"others":"lists","rows":["a.com"]}}""", "объект")]
    [InlineData("""{"id":1,"cmd":"advanced_set","args":{"enabled":true,"others":"lists","rows":[{"domains":[]}]}}""", "от 1 до 50")]
    [InlineData("""{"id":1,"cmd":"advanced_set","args":{"enabled":true,"others":"lists","rows":[{"strategy":"auto"}]}}""", "от 1 до 50")]
    [InlineData("""{"id":1,"cmd":"advanced_set","args":{"enabled":true,"others":"lists","rows":[{"domains":["a.com"],"args":"--debug"}]}}""", "лишнее поле")]
    [InlineData("""{"id":1,"cmd":"advanced_set","args":{"enabled":true,"others":"lists","rows":[{"domains":["a.com"],"resolved":"alt9"}]}}""", "лишнее поле")]
    [InlineData("""{"id":1,"cmd":"advanced_set","args":{"enabled":true,"others":"lists","rows":[{"domains":["a.com"],"hostlist":"C:\\x.txt"}]}}""", "лишнее поле")]
    [InlineData("""{"id":1,"cmd":"advanced_set","args":{"enabled":true,"others":"lists","extra":1,"rows":[]}}""", "лишнее поле")]
    [InlineData("""{"id":1,"cmd":"advanced_set","args":{"enabled":true,"others":"lists","rows":[{"domains":["a.com,b.com"]}]}}""", "не сайт")]
    [InlineData("""{"id":1,"cmd":"advanced_set","args":{"enabled":true,"others":"lists","rows":[{"domains":["a.com --new --debug"]}]}}""", "не сайт")]
    [InlineData("""{"id":1,"cmd":"advanced_set","args":{"enabled":true,"others":"lists","rows":[{"domains":["A.com"]}]}}""", "не сайт")]
    [InlineData("""{"id":1,"cmd":"advanced_set","args":{"enabled":true,"others":"lists","rows":[{"domains":["https://a.com/x"]}]}}""", "не сайт")]
    [InlineData("""{"id":1,"cmd":"advanced_set","args":{"enabled":true,"others":"lists","rows":[{"domains":["^a.com"]}]}}""", "не сайт")]
    [InlineData("""{"id":1,"cmd":"advanced_set","args":{"enabled":true,"others":"lists","rows":[{"domains":["a.com"],"strategy":"no-such"}]}}""", "из каталога")]
    [InlineData("""{"id":1,"cmd":"advanced_set","args":{"enabled":true,"others":"lists","rows":[{"domains":["a.com"],"strategy":"--wf-raw=x"}]}}""", "из каталога")]
    [InlineData("""{"id":1,"cmd":"advanced_set","args":{"enabled":true,"others":"lists","rows":[{"domains":["a.com"],"strategy":"..\\x"}]}}""", "из каталога")]
    [InlineData("""{"id":1,"cmd":"advanced_set","args":{"enabled":true,"others":"lists","rows":[{"domains":["a.com"],"strategy":5}]}}""", "строка")]
    [InlineData("""{"id":1,"cmd":"advanced_get","args":{"x":1}}""", "лишнее поле")]
    public void Helper_AdvancedSet_Abuse_Rejected(string json, string part) => Assert.Contains(part, Rejected(json), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Helper_AdvancedSet_TooManyRowsOrDomains_Rejected()
    {
        var rows = string.Join(',', Enumerable.Range(0, 21).Select(i => "{\"domains\":[\"s" + i + ".example\"]}"));
        Assert.Contains("больше 20", Rejected("{\"id\":1,\"cmd\":\"advanced_set\",\"args\":{\"enabled\":true,\"others\":\"lists\",\"rows\":[" + rows + "]}}"));
        var domains = string.Join(',', Enumerable.Range(0, 51).Select(i => $"\"s{i}.example\""));
        Assert.Contains("от 1 до 50", Rejected("{\"id\":1,\"cmd\":\"advanced_set\",\"args\":{\"enabled\":true,\"others\":\"lists\",\"rows\":[{\"domains\":[" + domains + "]}]}}"));
    }

    [Fact]
    public void Helper_AdvancedCommands_Exclusivity_AndDocumented()
    {
        Assert.True(HelperCommands.Specs["advanced_set"].Exclusive);
        Assert.False(HelperCommands.Specs["advanced_get"].Exclusive);
        Assert.False(HelperCommands.Specs["advanced_set"].Long);
        Assert.True(Version.Parse(HelperProtocol.Api) >= new Version(1, 8));
        var doc = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Repo.Data)!, "docs", "dev", "helper-protocol.md"));
        Assert.All(new[] { "advanced_get", "advanced_set {enabled, others, rows}" }, c => Assert.Contains("`" + c + "`", doc));
        Assert.Contains("1.8 — продвинутый режим", doc);
    }

    // ---------- настоящий помощник над временной папкой ----------

    [Fact]
    public async Task Channel_AdvancedSet_SavesAtHelper_GetReturnsIt_ErrorsAreReadable()
    {
        var shell = NewShell();
        await using var f = new HelperFixture(backend: PcHelper.Over(shell));
        var c = (await f.LinkAsync()).Client!;
        var id = shell.Catalog.Strategies[0].Id;
        Assert.False((await c.AdvancedGetAsync()).Enabled);

        var info = await c.AdvancedSetAsync(true, "lists", [new(["example.com", "example.org"], "auto"), new(["bank.example"], "direct"), new(["x.example"], id)]);
        Assert.True(info.Enabled);
        Assert.Null(info.Rows[1].Resolved);
        Assert.Equal(id, info.Rows[2].Resolved);

        // Состояние — у помощника (state.json), не в окне.
        var saved = AppState.Load(shell.Paths).Advanced;
        Assert.Equal(["example.com", "example.org"], saved.Rows[0].Domains);
        Assert.Equal(3, (await c.AdvancedGetAsync()).Rows.Count);

        // Отказ помощника (нет стратегии) понятен и ничего не ломает.
        await Assert.ThrowsAsync<HelperException>(() => c.AdvancedSetAsync(true, "lists", [new(["a.example"], "no-such")]));
        var e = await Assert.ThrowsAsync<HelperException>(() => c.AdvancedSetAsync(true, "direct", []));
        Assert.Contains("оптимизировать нечего", e.Message);
        Assert.Equal(3, AppState.Load(shell.Paths).Advanced.Rows.Count);
    }
}
