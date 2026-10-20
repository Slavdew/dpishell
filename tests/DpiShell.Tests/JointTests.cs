using DpiShell.Core;
using DpiShell.Core.Helper;
using DpiShell.Core.Router;

namespace DpiShell.Tests;

/// <summary>
/// Совместная работа ПК и роутера, сторона ПК (docs/research/mixed-strategies.md §7, договор роутера §5.2 pc_link_*):
/// имена приманок, «один хозяин», сборка winws «только своё», срок связки, переход к полной настройке.
/// </summary>
public sealed class JointTests : IDisposable
{
    readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    static readonly AppPaths P = new(@"C:\Root", @"C:\Seed");
    static string FakeDir => Path.Combine(Repo.Data, "fake");

    static byte[]? ReadFake(string name) => File.Exists(Path.Combine(FakeDir, name)) ? File.ReadAllBytes(Path.Combine(FakeDir, name)) : null;

    static DecoyNames Decoys(params (string Role, string Desync)[] d) => JointDecoys.Of(d, ReadFake, () => JointDecoys.SupplyNames(FakeDir));

    static StrategyCatalog Catalog => StrategyCatalog.Load(Path.Combine(Repo.Data, "strategies.json"));

    // ---------- имена приманок ----------

    [Theory]
    [InlineData("tls_clienthello_www_google_com.bin", "www.google.com")]
    [InlineData("tls_clienthello_4pda_to.bin", "4pda.to")]
    [InlineData("tls_clienthello_max_ru.bin", "www.onetrust.com")]       // имя файла врёт: SNI внутри — onetrust, поэтому имена читаются из содержимого
    [InlineData("tls_clienthello_sochi_park.bin", "staging-sochi-park.predict.mail.ru")]
    public void Sni_is_read_from_the_fake_file_itself(string file, string name) =>
        Assert.Equal(name, JointDecoys.NameOfBytes(File.ReadAllBytes(Path.Combine(FakeDir, file))));

    [Fact]
    public void Non_tls_fakes_have_no_name()
    {
        foreach (var f in new[] { "stun.bin", "stun2.bin", "ACTIVE_DISCORD_UDP.bin", "ACTIVE_GAME_UDP.bin" })
            Assert.Null(JointDecoys.NameOfBytes(File.ReadAllBytes(Path.Combine(FakeDir, f))));
        Assert.Null(JointDecoys.SniOf([]));
        Assert.Null(JointDecoys.SniOf([0x16, 3, 1, 0, 5, 1, 0, 0, 1, 0]));      // обрезанный ClientHello
        Assert.Equal("example.org", JointDecoys.NameOfBytes(System.Text.Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: Example.org\r\nAccept: */*\r\n\r\n")));
    }

    [Theory]
    [InlineData("quic_initial_www_google_com.bin", "www.google.com")]
    [InlineData("quic_initial_rutracker_org_kyber_1.bin", "rutracker.org")]
    [InlineData("quic_initial_facebook_com_quiche.bin", "facebook.com")]
    [InlineData("quic_initial_rr1---sn-xguxaxjvh-n8me_googlevideo_com_kyber_2.bin", "rr1---sn-xguxaxjvh-n8me.googlevideo.com")]
    public void Quic_name_comes_from_the_file_name(string file, string name) => Assert.Equal(name, JointDecoys.QuicNameOfFile(file));

    [Fact]
    public void Catalog_strategies_give_exact_names_for_every_role_that_stays_on_the_pc()
    {
        // Любая стратегия каталога: имена приманок известны точно (случайных SNI в каталоге нет), и все они — настоящие имена из файлов поставки.
        var supply = JointDecoys.SupplyNames(FakeDir);
        foreach (var s in Catalog.Strategies)
        {
            var d = Decoys(("general", s.Roles.GetValueOrDefault("general") ?? ""), ("quic", s.Roles.GetValueOrDefault("quic") ?? ""));
            Assert.True(d.Reliable, $"{s.Id}: имена приманок не точные ({string.Join("; ", d.Notes)})");
            Assert.All(d.Names, n => Assert.True(AdvancedSites.IsDomain(n), n));
            if (s.Roles.GetValueOrDefault("quic") is { Length: > 0 }) Assert.Contains("www.google.com", d.Names);
        }
        // Стратегия с fake-tls www.google.com и fake-http «max_ru» (внутри onetrust): оба настоящих имени в списке, в том числе www. — без обрезки (google.com родитель play.google.com роутера).
        var withFiles = Catalog.Strategies.First(s => s.Roles.GetValueOrDefault("general") is { } g && g.Contains("tls_clienthello_www_google_com.bin") && g.Contains("tls_clienthello_max_ru.bin"));
        var names = Decoys(("general", withFiles.Roles["general"])).Names;
        Assert.Contains("www.google.com", names);
        Assert.Contains("www.onetrust.com", names);
        Assert.DoesNotContain("max.ru", names);
        Assert.DoesNotContain("google.com", names);
        Assert.All(names, n => Assert.Contains(n, supply.Concat(["www.google.com"])));
    }

    [Fact]
    public void Sni_and_host_values_of_the_parameters_are_names_too()
    {
        // sni=ya.ru в fake-tls-mod и host=ya.ru у hostfakesplit; встроенный фейк при sni= своего имени не добавляет.
        var d = Decoys(("general", "--dpi-desync=fake,hostfakesplit --dpi-desync-fake-tls-mod=rnd,dupsid,sni=ya.ru --dpi-desync-hostfakesplit-mod=host=ya.ru,altorder=1 --dpi-desync-fooling=ts"));
        Assert.True(d.Reliable);
        Assert.Contains("ya.ru", d.Names);
        Assert.DoesNotContain(JointDecoys.BuiltinTls, d.Names);
        Assert.Contains(JointDecoys.BuiltinHttp, d.Names);   // порт 80: встроенный HTTP-фейк www.iana.org
        // alt9: hostfakesplit с host=ozon.ru, без fake — приманка только это имя (+ имя фейка паттерна нет).
        var alt9 = Catalog.Strategies.First(s => s.Id == "alt9");
        var a9 = Decoys(("general", alt9.Roles["general"]));
        Assert.True(a9.Reliable);
        Assert.Equal(["ozon.ru"], a9.Names);
    }

    [Fact]
    public void Builtin_fake_names_are_known_when_the_mod_is_none()
    {
        var d = Decoys(("general", "--dpi-desync=fake --dpi-desync-fake-tls-mod=none --dpi-desync-repeats=6"));
        Assert.True(d.Reliable);
        Assert.Equal([JointDecoys.BuiltinTls, JointDecoys.BuiltinHttp], d.Names);
        // '!' — встроенный фейк winws тоже.
        Assert.Contains(JointDecoys.BuiltinTls, Decoys(("general", "--dpi-desync=fake --dpi-desync-fake-tls=!")).Names);
    }

    [Fact]
    public void Hex_payload_of_a_fake_is_parsed_like_a_file()
    {
        var hex = "0x" + Convert.ToHexString(File.ReadAllBytes(Path.Combine(FakeDir, "tls_clienthello_4pda_to.bin")));
        var d = Decoys(("general", "--dpi-desync=fake --dpi-desync-fake-tls=" + hex));
        Assert.True(d.Reliable);
        Assert.Contains("4pda.to", d.Names);
    }

    [Fact]
    public void Pattern_files_of_seqovl_and_fakedsplit_are_names_too()
    {
        // Стенд: seqovl-pattern тоже даёт роутеру имя приманки.
        var d = Decoys(("general", "--dpi-desync=multisplit --dpi-desync-split-seqovl=681 --dpi-desync-split-seqovl-pattern={fake}/tls_clienthello_4pda_to.bin"));
        Assert.True(d.Reliable);
        Assert.Equal(["4pda.to"], d.Names);
        Assert.Equal(["www.onetrust.com"], Decoys(("general", "--dpi-desync=fakedsplit --dpi-desync-fakedsplit-pattern={fake}/tls_clienthello_max_ru.bin")).Names);
    }

    [Theory]
    [InlineData("general", "--dpi-desync=fake --dpi-desync-repeats=6")]                                                  // фейк без своего файла и без mod: rnd,rndsni,dupsid — SNI случайный
    [InlineData("general", "--dpi-desync=fake --dpi-desync-fake-tls-mod=rnd,rndsni,dupsid")]                              // rndsni явно
    [InlineData("general", "--dpi-desync=fake,hostfakesplit --dpi-desync-fake-tls-mod=none")]                             // hostfakesplit без host=
    [InlineData("quic", "--dpi-desync=fake --dpi-desync-repeats=6")]                                                      // QUIC-фейк без файла
    public void Random_names_cannot_be_listed_so_the_whole_supply_is_used(string role, string desync)
    {
        var d = Decoys((role, desync));
        Assert.False(d.Reliable);
        Assert.NotEmpty(d.Notes);
        // Консервативный список — все имена фейков из поставки.
        Assert.All(new[] { "www.google.com", "4pda.to", "www.onetrust.com", "staging-sochi-park.predict.mail.ru", JointDecoys.BuiltinTls, JointDecoys.BuiltinHttp }, n => Assert.Contains(n, d.Names));
        Assert.Contains("4pda.to", d.Names);
        Assert.Contains("www.onetrust.com", d.Names);
    }

    // ---------- один хозяин ----------

    [Theory]
    [InlineData("youtube.com", "youtube.com", true)]
    [InlineData("m.youtube.com", "youtube.com", true)]       // поддомен
    [InlineData("google.com", "play.google.com", true)]      // надомен
    [InlineData("www.google.com", "play.google.com", false)] // общий родитель — не один адрес
    [InlineData("notyoutube.com", "youtube.com", false)]
    [InlineData("example.com", "youtube.com", false)]
    public void Related_names_are_one_address(string a, string b, bool related)
    {
        Assert.Equal(related, JointRules.Related(a, b));
        Assert.Equal(related, JointRules.Related(b, a));
    }

    static JointInfo Pc(string[] own, string[]? rows = null, string[]? decoys = null, string[]? work = null, int profiles = 0, bool active = false, string[]? owned = null) =>
        new("11111111-2222-4333-8444-555555555555", active, owned ?? [], own, rows ?? [], decoys ?? ["www.google.com", "4pda.to"], true, [], "alt9", true,
            work ?? [.. own.Concat(rows ?? [])], profiles);

    [Fact]
    public void Selection_decoys_first_pc_sites_after_and_router_addresses_stay_with_the_router()
    {
        var pc = Pc(own: ["mysite.example", "youtube.com"], rows: ["bank.example", "m.discord.com"], decoys: ["www.google.com", "4pda.to"]);
        var sel = JointRules.Select(pc, ["youtube.com", "discord.com", "play.google.com"]);
        Assert.Equal(["www.google.com", "4pda.to", "mysite.example", "bank.example"], sel.Send);     // приманки первыми
        Assert.Equal(["youtube.com", "m.discord.com"], sel.RouterOwned);                            // «ведёт роутер»
        Assert.Equal(["mysite.example", "bank.example"], sel.Mine);
        Assert.DoesNotContain("youtube.com", sel.Send);
        Assert.Empty(sel.DecoyGaps);
    }

    [Fact]
    public void Decoy_that_equals_a_router_address_is_reported_and_not_sent()
    {
        var sel = JointRules.Select(Pc(own: ["mysite.example"], decoys: ["www.gstatic.com", "4pda.to"]), ["www.gstatic.com"]);
        Assert.Equal(["www.gstatic.com"], sel.DecoyGaps);
        Assert.Equal(["4pda.to", "mysite.example"], sel.Send);
    }

    [Fact]
    public void Ignored_by_the_router_becomes_router_owned_too()
    {
        // Строка продвинутого режима роутера (о ней ПК не знает) — роутер вернул её в ignored.
        var sel = JointRules.Select(Pc(own: ["mysite.example", "video.example"]), ["youtube.com"]).WithIgnored(["video.example"]);
        Assert.Equal(["video.example"], sel.RouterOwned);
        Assert.Equal(["mysite.example"], sel.Mine);
    }

    [Fact]
    public void More_than_500_names_trim_pc_sites_not_decoys()
    {
        var own = Enumerable.Range(0, 520).Select(i => $"site{i}.example").ToArray();
        var sel = JointRules.Select(Pc(own: own, decoys: ["www.google.com", "4pda.to"]), []);
        Assert.Equal(JointRules.MaxDomains, sel.Send.Count);
        Assert.Equal(["www.google.com", "4pda.to"], sel.Send.Take(2));
        Assert.Equal(522 - JointRules.MaxDomains, sel.Trimmed);
    }

    // ---------- сборка winws «только своё» ----------

    static IEnumerable<IReadOnlyList<string>> Profiles(IEnumerable<string> args)
    {
        var all = new List<List<string>> { new() };
        foreach (var a in args.Where(a => !a.StartsWith("--wf-")))
            if (a == "--new") all.Add([]); else all[^1].Add(a);
        return all;
    }

    [Fact]
    public void Joint_build_keeps_only_own_sites_and_rows_and_drops_what_the_router_leads()
    {
        var s = Catalog.Strategies.First(x => x.Roles.ContainsKey("google") && x.Roles.ContainsKey("quic") && x.Roles.ContainsKey("discord_media"));
        var other = Catalog.Strategies.First(x => x.Id != s.Id);
        var adv = new AdvancedPlan(AdvancedSites.OthersAll, [new(["bank.example", "youtube.com"], other), new(["gone.example"], null)]);
        var args = WinwsArgs.Build(s, P, new BuildOptions(IpsetMode.List, GameMode.TcpUdp, null, adv, new JointBuild(["youtube.com", "m.discord.com"])));
        var profiles = Profiles(args).ToList();

        // Роли: только quic и general (по user-general), роли YouTube, Discord, IP-списков и игр — у роутера.
        var text = string.Join(" ", args);
        Assert.DoesNotContain("google.txt", text);
        Assert.DoesNotContain("discord", text.Replace("m.discord.com", ""));
        Assert.DoesNotContain("--ipset=", text);
        Assert.DoesNotContain("1024-65535", text);
        var general = profiles.Single(p => p.Contains("--filter-tcp=80,443") && !p.Any(x => x.StartsWith("--hostlist-domains=")));
        Assert.Contains("--hostlist=" + Path.Combine(P.Lists, "user-general.txt"), general);
        Assert.DoesNotContain("--hostlist=" + Path.Combine(P.Lists, "general.txt"), general);
        Assert.Contains("--hostlist-exclude=" + Path.Combine(P.Lists, "exclude.txt"), general);
        // Сайты, записанные у ПК, но которые ведёт роутер, исключены и здесь, и в профилях.
        Assert.Contains(general, x => x.StartsWith("--hostlist-exclude-domains=") && x.Contains("youtube.com") && x.Contains("m.discord.com"));
        Assert.Contains(general, x => x.StartsWith("--hostlist-exclude-domains=") && x.Contains("gone.example"));   // строка «напрямую» — исключение и здесь
        // Строка: youtube.com выпал («ведёт роутер»), bank.example остался; строка только с чужим адресом не создаёт профиля.
        var row = profiles.Single(p => p.Any(x => x.StartsWith("--hostlist-domains=")) && p.Contains("--filter-tcp=80,443"));
        Assert.Contains("--hostlist-domains=bank.example", row);
        Assert.DoesNotContain(row, x => x.Contains("youtube.com") && x.StartsWith("--hostlist-domains="));
        Assert.DoesNotContain(profiles, p => p.Any(x => x.StartsWith("--hostlist-domains=") && x.Contains("gone.example")));
        // «Остальные — для всех» на ПК при связке не действует: общая роль не без списка.
        Assert.All(profiles.Where(p => p.Contains("--filter-tcp=80,443") && !p.Any(x => x.StartsWith("--hostlist-domains="))),
            p => Assert.Contains(p, x => x.StartsWith("--hostlist=")));
    }

    [Fact]
    public void Joint_build_ignores_others_direct_and_does_not_add_game_ports()
    {
        var s = Catalog.Strategies[0];
        var args = WinwsArgs.Build(s, P, new BuildOptions(IpsetMode.List, GameMode.TcpUdp, null, new AdvancedPlan(AdvancedSites.OthersDirect, []), new JointBuild([])));
        Assert.Contains(args, x => x == "--filter-tcp=80,443");
        Assert.Contains(args, x => x == "--wf-tcp=" + WinwsArgs.WfTcp);
        Assert.Contains(args, x => x == "--wf-udp=" + WinwsArgs.WfUdp);
    }

    [Fact]
    public void Without_a_link_the_command_line_is_exactly_the_usual_one()
    {
        foreach (var s in Catalog.Strategies)
            Assert.Equal(WinwsArgs.Build(s, P), WinwsArgs.Build(s, P, new BuildOptions(Joint: null)));
    }

    // ---------- состояние у помощника ----------

    Shell NewShell(string name = "root")
    {
        var shell = new Shell(new AppPaths(_t.Sub(name), Repo.Data));
        shell.Data.Seed();
        return shell;
    }

    [Fact]
    public void Joint_state_is_off_by_default_and_expires_with_the_lease()
    {
        var shell = NewShell();
        Assert.False(shell.State.Joint.Active);
        Assert.Null(shell.CurrentOptions.Joint);

        shell.SetJoint(true, ["youtube.com"]);
        Assert.NotNull(shell.CurrentOptions.Joint);
        Assert.Equal(["youtube.com"], shell.CurrentOptions.Joint!.Owned);
        Assert.True(shell.State.Joint.Until > DateTime.UtcNow.AddMinutes(100));

        // Срок вышел — командная строка снова полная, даже если никто ничего не выключал.
        var past = BuildOptions.From(shell.State, DateTime.UtcNow + JointRules.Lease + TimeSpan.FromMinutes(1), shell.FindStrategy, shell.State.StrategyId);
        Assert.Null(past.Joint);
        shell.State.Joint.Until = DateTime.UtcNow.AddSeconds(-1);
        Assert.True(shell.ExpireJoint());
        Assert.False(shell.State.Joint.Active);
        Assert.False(shell.ExpireJoint());
    }

    [Fact]
    public async Task Joint_state_is_written_to_the_journal_and_undone_with_it()
    {
        var shell = NewShell();
        shell.SetJoint(true, []);
        shell.SetJoint(true, ["youtube.com"]);                    // смена списка правит ту же запись
        var open = shell.Journal.Active(ChangeKinds.Joint);
        Assert.Single(open);
        Assert.False(JointConfig.FromJson(open[0].Before).Active);
        Assert.Equal(["youtube.com"], JointConfig.FromJson(open[0].After).Owned);

        var backend = PcHelper.Over(shell);
        var report = await backend.ChangesUndoAllAsync(CancellationToken.None);
        Assert.Contains(report.Items, i => i.Status == UndoStatus.Restored && i.Note.Contains("совместная работа выключена"));
        Assert.False(shell.State.Joint.Active);
    }

    [Fact]
    public void Switching_off_closes_the_journal_entry_and_remove_all_clears_the_link()
    {
        var shell = NewShell();
        shell.SetJoint(true, []);
        shell.SetJoint(false, []);
        Assert.Empty(shell.Journal.Active(ChangeKinds.Joint));
        var id = shell.JointInfo().PcId;
        shell.SetJoint(true, []);
        PcHelper.Over(shell).RemoveAll();
        Assert.False(shell.State.Joint.Active);
        Assert.Equal(id, shell.State.Joint.PcId);                              // номер ПК постоянный
    }

    [Fact]
    public void Joint_get_gives_pc_sites_rows_decoys_and_a_permanent_id()
    {
        var shell = NewShell();
        shell.Data.WriteUserDomains(["mysite.example", "youtube.com"]);
        shell.State.StrategyId = Catalog.Strategies.First(s => s.Roles.GetValueOrDefault("general")?.Contains("tls_clienthello_www_google_com.bin") == true).Id;
        shell.State.Advanced = new AdvancedConfig
        {
            Enabled = true,
            Rows = [new() { Domains = ["bank.example"], Strategy = AdvancedSites.Direct }, new() { Domains = ["row.example"], Strategy = shell.State.StrategyId! }],
        };
        var i = shell.JointInfo();
        Assert.Equal(["mysite.example", "youtube.com"], i.Own);
        Assert.Equal(["bank.example", "row.example"], i.Rows);                                  // «напрямую» тоже: роутеру их трогать не стоит
        Assert.Equal(["mysite.example", "youtube.com", "row.example"], i.Work);                 // а вести ПК будет только свои и строку со стратегией
        Assert.Contains("www.google.com", i.Decoys);
        Assert.True(i.DecoysReliable);
        Assert.True(Guid.TryParse(i.PcId, out _));
        Assert.Equal(i.PcId, NewShellSame(shell).JointInfo().PcId);                            // постоянный: после перезапуска помощника тот же
        Assert.False(i.Active);
    }

    Shell NewShellSame(Shell shell) => new(shell.Paths);

    [Fact]
    public void Nothing_to_do_without_own_sites_rows_and_profiles()
    {
        var shell = NewShell();
        shell.SetJoint(true, ["youtube.com"]);
        Assert.True(shell.JointNothingToDo(shell.CurrentOptions));                              // user-general пуст
        shell.Data.WriteUserDomains(["youtube.com"]);
        Assert.True(shell.JointNothingToDo(shell.CurrentOptions));                              // единственный сайт ведёт роутер
        shell.Data.WriteUserDomains(["youtube.com", "mine.example"]);
        Assert.False(shell.JointNothingToDo(shell.CurrentOptions));
        Assert.False(shell.JointNothingToDo(BuildOptions.Default));                              // без связки такого отказа нет
    }

    // ---------- команды помощника ----------

    [Fact]
    public void Protocol_validates_owned_sites()
    {
        var catalog = new CatalogStub();
        Assert.Equal(["youtube.com"], HelperCommands.Parse("""{"id":1,"cmd":"joint_set","args":{"active":true,"owned":["youtube.com","youtube.com"]}}""", catalog).List("owned"));
        Assert.Null(HelperCommands.Parse("""{"id":2,"cmd":"joint_set","args":{"active":false}}""", catalog).List("owned"));
        foreach (var bad in new[] { """["C:\\x"]""", """["a b.com"]""", """["YouTube.com"]""", """["-rf"]""", """[""]""", """["x.com", null]""", "\"youtube.com\"", "1" })
            Assert.Throws<HelperRequestException>(() => HelperCommands.Parse("{\"id\":3,\"cmd\":\"joint_set\",\"args\":{\"active\":true,\"owned\":" + bad + "}}", catalog));
        Assert.Throws<HelperRequestException>(() => HelperCommands.Parse("""{"id":4,"cmd":"joint_set","args":{"owned":[]}}""", catalog));
        Assert.Throws<HelperRequestException>(() => HelperCommands.Parse("""{"id":5,"cmd":"joint_set","args":{"active":true,"extra":1}}""", catalog));
        Assert.Throws<HelperRequestException>(() => HelperCommands.Parse("""{"id":6,"cmd":"joint_get","args":{"x":1}}""", catalog));
        var many = string.Join(",", Enumerable.Range(0, 501).Select(i => $"\"s{i}.example\""));
        Assert.Throws<HelperRequestException>(() => HelperCommands.Parse("{\"id\":7,\"cmd\":\"joint_set\",\"args\":{\"active\":true,\"owned\":[" + many + "]}}", catalog));
        Assert.False(HelperCommands.Specs["joint_get"].Exclusive);
        Assert.True(HelperCommands.Specs["joint_set"].Exclusive);
        Assert.False(HelperCommands.Specs["joint_set"].Long);
    }

    sealed class CatalogStub : IHelperCatalog
    {
        public IReadOnlyCollection<string> StrategyIds { get; } = new HashSet<string> { "alt9" };
        public IReadOnlyCollection<string> GroupIds { get; } = new HashSet<string> { "youtube" };
    }

    [Fact]
    public void Contract_documents_match_the_code()
    {
        var root = Path.GetDirectoryName(Repo.Data)!;
        var api = File.ReadAllText(Path.Combine(root, "docs", "dev", "router-api.md"));
        Assert.All(RouterClient.PcLinkMethods, m => Assert.Contains("`" + m + "`", api));
        Assert.Equal(["pc_link_set", "pc_link_del", "pc_link_get"], RouterClient.PcLinkMethods);
        Assert.All(RouterClient.PcLinkMethods, m => Assert.Contains($"\"{m}\"", FakeRouter.Methods020));
        Assert.DoesNotContain("pc_link", FakeRouter.MethodsNoPcLink);
        var hp = File.ReadAllText(Path.Combine(root, "docs", "dev", "helper-protocol.md"));
        Assert.Contains("1.9 — совместная работа с роутером", hp);
        var arch = File.ReadAllText(Path.Combine(root, "docs", "dev", "architecture.md"));
        Assert.Contains(L.T("router.joint.title"), arch);
        Assert.Contains("joint_get", arch);
    }

    // ---------- договор: клиент ----------

    [Theory]
    [InlineData("AA-BB-CC-DD-EE-FF", "aa:bb:cc:dd:ee:ff")]
    [InlineData("aa:bb:cc:dd:ee:ff", "aa:bb:cc:dd:ee:ff")]
    [InlineData("AABBCCDDEEFF", "aa:bb:cc:dd:ee:ff")]
    [InlineData("aa:bb:cc:dd:ee", null)]
    [InlineData("zz:bb:cc:dd:ee:ff", null)]
    [InlineData(null, null)]
    public void Mac_is_normalized_like_the_router_wants(string? mac, string? expected) => Assert.Equal(expected, RouterClient.NormalizeMac(mac));

    [Fact]
    public async Task Client_talks_pc_link_by_the_contract()
    {
        using var fake = new FakeRouter { WarningsJson = "[]" }.As020();
        using var c = new RouterClient(fake.Address);
        await c.LoginAsync(() => "secret");
        const string id = "11111111-2222-4333-8444-555555555555";

        Assert.Equal(["youtube.com", "googlevideo.com", "ytimg.com", "play.google.com", "google.ru"], (await c.ListsGetAsync("google")).Lines);
        Assert.False((await c.ListsGetAsync("discord")).Editable);

        var set = await c.PcLinkSetAsync(id, ["AA-BB-CC-DD-EE-01"], ["www.google.com", "mysite.example", "youtube.com"]);
        Assert.Equal(["youtube.com"], set.Ignored);
        Assert.NotNull(set.ExpiresAt);
        Assert.Equal("aa:bb:cc:dd:ee:01", fake.LastPcLinkSet!["macs"]![0]!.GetValue<string>());

        var links = await c.PcLinkGetAsync();
        var l = Assert.Single(links.Items);
        Assert.True(l.Active);
        Assert.Equal(2, l.Domains);
        Assert.Equal(1, l.Ignored);
        Assert.Equal(1, (await c.StatusAsync()).PcLinks);

        fake.Enabled = false;                                   // оптимизация на роутере выключена — связка не действует
        Assert.False(Assert.Single((await c.PcLinkGetAsync()).Items).Active);

        await c.PcLinkDelAsync(id);
        await c.PcLinkDelAsync(id);                             // неизвестный id — тоже успех
        Assert.Empty((await c.PcLinkGetAsync()).Items);

        Assert.Throws<ArgumentException>(() => { _ = c.PcLinkSetAsync("short", ["aa:bb:cc:dd:ee:01"], []); });
        Assert.Throws<ArgumentException>(() => { _ = c.PcLinkSetAsync(id, [], []); });
        Assert.Throws<ArgumentException>(() => { _ = c.PcLinkSetAsync(id, ["aa:bb:cc:dd:ee:01"], ["Bad Name"]); });
        Assert.Throws<ArgumentException>(() => { _ = c.PcLinkSetAsync(id, ["aa:bb:cc:dd:ee:01"], Enumerable.Range(0, 501).Select(i => $"s{i}.example").ToList()); });
    }
}
