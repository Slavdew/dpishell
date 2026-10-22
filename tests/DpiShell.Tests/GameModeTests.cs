using DpiShell.Core;

namespace DpiShell.Tests;

public class GameModeBuildTests
{
    static readonly AppPaths P = new(@"C:\Root", @"C:\Seed");
    static string L(string f) => Path.Combine(P.Lists, f);
    static readonly BuildOptions Game = new(IpsetMode.List, GameMode.TcpUdp);

    static List<List<string>> Profiles(IReadOnlyList<string> a)
    {
        var r = new List<List<string>> { new() };
        foreach (var x in a.Skip(2)) { if (x == "--new") r.Add([]); else r[^1].Add(x); }
        return r;
    }

    static Strategy Alt => StrategyCatalog.Load(Repo.Strategies).Find("alt")!;

    [Fact]
    public void Default_NoGameProfiles_BaseWf_IpsetPlaceholder()
    {
        var a = WinwsArgs.Build(Alt, P);
        Assert.Equal($"--wf-tcp={WinwsArgs.WfTcp}", a[0]);
        Assert.Equal($"--wf-udp={WinwsArgs.WfUdp}", a[1]);
        Assert.DoesNotContain(a, x => x.Contains(WinwsArgs.GamePorts));
        Assert.DoesNotContain("--dpi-desync-any-protocol=1", a);
        Assert.Contains("--ipset=" + L(IpLists.NoneFile), a);
        Assert.DoesNotContain("--ipset=" + L(IpLists.AllFile), a);
        Assert.Equal(Alt.Roles.Keys.Count(r => !r.StartsWith("game_")) - 1, a.Count(x => x == "--new"));
    }

    [Fact]
    public void ListMode_IpsetProfilesUseLoadedAndUserList()
    {
        var a = WinwsArgs.Build(Alt, P, new(IpsetMode.List));
        var ipsetTcp = Profiles(a).Single(p => p[0] == "--filter-tcp=80,443,8443");
        Assert.Contains("--ipset=" + L(IpLists.AllFile), ipsetTcp);
        Assert.Contains("--ipset=" + L(IpLists.UserFile), ipsetTcp);
        Assert.Contains("--ipset-exclude=" + L(IpLists.ExcludeFile), ipsetTcp);
        Assert.Contains("--ipset-exclude=" + L(IpLists.UserExcludeFile), ipsetTcp);
        Assert.DoesNotContain("--ipset=" + L(IpLists.NoneFile), a);
        Assert.DoesNotContain(a, x => x.Contains(WinwsArgs.GamePorts));   // список без игрового режима порты не расширяет
    }

    [Fact]
    public void AnyMode_OnlyEmptyIncludeList()
    {
        var a = WinwsArgs.Build(Alt, P, new(IpsetMode.Any, GameMode.TcpUdp));
        var includes = a.Where(x => x.StartsWith("--ipset=")).Distinct().ToList();
        // Свой список с заглушкой сделал бы include непустым — «все адреса» перестали бы быть всеми.
        Assert.Equal(["--ipset=" + L(IpLists.AnyFile)], includes);
        // Игровой режим при «Все адреса» запрещён: профилей нет, порты базовые.
        Assert.DoesNotContain(a, x => x.Contains(WinwsArgs.GamePorts));
    }

    [Fact]
    public void GameOn_WfExtended_TwoProfilesLast_FromCatalog()
    {
        var a = WinwsArgs.Build(Alt, P, Game);
        Assert.Equal($"--wf-tcp={WinwsArgs.WfTcp},{WinwsArgs.GamePorts}", a[0]);
        Assert.Equal($"--wf-udp={WinwsArgs.WfUdp},{WinwsArgs.GamePorts}", a[1]);
        var ps = Profiles(a);
        Assert.Equal(Alt.Roles.Count, ps.Count);
        Assert.Equal(Alt.Roles.Count - 1, a.Count(x => x == "--new"));

        var tcp = ps[^2]; var udp = ps[^1];
        Assert.Equal($"--filter-tcp={WinwsArgs.GamePorts}", tcp[0]);
        Assert.Equal($"--filter-udp={WinwsArgs.GamePorts}", udp[0]);
        foreach (var p in new[] { tcp, udp })
        {
            Assert.Contains("--ipset=" + L(IpLists.AllFile), p);
            Assert.Contains("--ipset-exclude=" + L(IpLists.ExcludeFile), p);
            Assert.Contains("--dpi-desync-any-protocol=1", p);
            Assert.Contains(p, x => x.StartsWith("--dpi-desync-cutoff="));
            Assert.DoesNotContain(p, x => x.StartsWith("--hostlist"));
        }
        // Десинк — ровно роль каталога (профиль game filter из general (ALT).bat).
        Assert.Equal(Alt.Roles["game_tcp"].Split(' ').Select(t => t.Replace("{fake}/", P.Fake + Path.DirectorySeparatorChar)), tcp.Skip(5));   // фильтр, 2 include, 2 exclude
        Assert.Contains("--dpi-desync-fake-unknown-udp=" + Path.Combine(P.Fake, "ACTIVE_GAME_UDP.bin"), udp);
    }

    [Theory]
    [InlineData(GameMode.Tcp, true, false)]
    [InlineData(GameMode.Udp, false, true)]
    public void GameSingleProtocol_OnlyItsWfAndProfile(GameMode mode, bool tcp, bool udp)
    {
        var a = WinwsArgs.Build(Alt, P, new(IpsetMode.List, mode));
        Assert.Equal(tcp, a[0].EndsWith("," + WinwsArgs.GamePorts));
        Assert.Equal(udp, a[1].EndsWith("," + WinwsArgs.GamePorts));
        Assert.Equal(tcp, a.Contains($"--filter-tcp={WinwsArgs.GamePorts}"));
        Assert.Equal(udp, a.Contains($"--filter-udp={WinwsArgs.GamePorts}"));
    }

    [Fact]
    public void GameWithIpsetOff_DoesNothing()
    {
        var off = WinwsArgs.Build(Alt, P, new(IpsetMode.Off, GameMode.TcpUdp));
        Assert.Equal(WinwsArgs.Build(Alt, P), off);
    }

    [Fact]
    public void StrategyWithoutGameRoles_DerivedFromGeneral()
    {
        var s = Make.Strategy(("general", "--dpi-desync=fake --dpi-desync-fooling=badseq"));
        var a = WinwsArgs.Build(s, P, Game);
        var ps = Profiles(a);
        Assert.Equal(3, ps.Count);
        Assert.Equal(["--dpi-desync=fake", "--dpi-desync-fooling=badseq", "--dpi-desync-any-protocol=1",
            "--dpi-desync-cutoff=n3", "--dpi-desync-badseq-increment=0x80000000"], ps[1].Skip(5));
        Assert.Equal(WinwsArgs.GameUdpDefault.Split(' ').Select(t => t.Replace("{fake}/", P.Fake + Path.DirectorySeparatorChar)), ps[2].Skip(5));
    }

    [Fact]
    public void DeriveGameTcp_KeepsExistingIncrementAndCutoff()
    {
        Assert.Null(WinwsArgs.DeriveGameTcp(null));
        Assert.Equal("--dpi-desync=fake --dpi-desync-fooling=badseq --dpi-desync-badseq-increment=1000 --dpi-desync-cutoff=n2 --dpi-desync-any-protocol=1",
            WinwsArgs.DeriveGameTcp("--dpi-desync=fake --dpi-desync-fooling=badseq --dpi-desync-badseq-increment=1000 --dpi-desync-cutoff=n2"));
        Assert.DoesNotContain("badseq-increment", WinwsArgs.DeriveGameTcp("--dpi-desync=fake --dpi-desync-fooling=ts"));
    }

    [Fact]
    public void NoGeneral_NoGameTcp()
    {
        var a = WinwsArgs.Build(Make.Strategy(("google", "--dpi-desync=fake")), P, Game);
        Assert.DoesNotContain($"--filter-tcp={WinwsArgs.GamePorts}", a);
        Assert.Equal($"--wf-tcp={WinwsArgs.WfTcp}", a[0]);
        Assert.Contains($"--filter-udp={WinwsArgs.GamePorts}", a);
    }

    [Fact]
    public void AllRealStrategies_AllModes_BalancedAndRoundTrip()
    {
        using var t = new TempDir(" with space");
        var p = new AppPaths(t.Sub("Dpi Shell"), Repo.Data);
        foreach (var s in StrategyCatalog.Load(Repo.Strategies).Strategies)
        {
            Assert.True(s.Roles.ContainsKey("game_tcp") && s.Roles.ContainsKey("game_udp"), s.Id);
            foreach (var ipset in Enum.GetValues<IpsetMode>())
                foreach (var game in Enum.GetValues<GameMode>())
                {
                    var o = new BuildOptions(ipset, game);
                    var a = WinwsArgs.Build(s, p, o);
                    var roles = WinwsArgs.ActiveRoles(s, o);
                    Assert.Equal(roles.Count - 1, a.Count(x => x == "--new"));
                    Assert.NotEqual("--new", a[^1]);
                    Assert.DoesNotContain(a, x => x.Contains("{fake}") || x.Contains('^'));
                    Assert.Equal(o.GameTcp, a[0].Contains(WinwsArgs.GamePorts));
                    Assert.Equal(o.GameUdp, a[1].Contains(WinwsArgs.GamePorts));
                    Assert.Equal(a.ToArray(), Native.ParseCommandLine("winws.exe " + WinwsArgs.Join(a)).Skip(1).ToArray());
                    // Опознание стратегии по строке службы не ломается игровыми профилями.
                    Assert.Equal(s.Id, ZapretDetector.Match(a, [s])?.Id);
                }
        }
    }

    [Fact]
    public void RealCatalog_FakeTlsAuto_GetsBadseqIncrement() =>
        Assert.Contains("--dpi-desync-badseq-increment=0x80000000", StrategyCatalog.Load(Repo.Strategies).Find("fake-tls-auto")!.Roles["game_tcp"]);

    [Theory]
    [InlineData("--dpi-desync-any-protocol=1")]
    [InlineData("--dpi-desync-cutoff=n3")]
    [InlineData("--dpi-desync-fake-unknown-udp={fake}/x.bin")]
    [InlineData("--dpi-desync-fake-unknown=0x00")]
    public void GameOnlyOptions_RejectedOutsideGameRoles(string tok)
    {
        Assert.Throws<InvalidDataException>(() => WinwsArgs.Validate(Make.Strategy(("general", "--dpi-desync=fake " + tok))));
        WinwsArgs.Validate(Make.Strategy(("game_tcp", "--dpi-desync=fake " + tok)));
        WinwsArgs.Validate(Make.Strategy(("game_udp", "--dpi-desync=fake " + tok)));
    }

    [Theory]
    [InlineData("--dpi-desync-fake-unknown=cygwin1.dll")]
    [InlineData("--dpi-desync-fake-unknown=C:/x.bin")]
    [InlineData("--dpi-desync-fake-unknown-udp={fake}/../x.bin")]
    [InlineData("--filter-tcp=1-65535")]
    [InlineData("--ipset=x.txt")]
    [InlineData("--wf-tcp=1-65535")]
    public void GameRoles_StillCheckPathsAndFilters(string tok) =>
        Assert.Throws<InvalidDataException>(() => WinwsArgs.Validate(Make.Strategy(("game_tcp", "--dpi-desync=fake " + tok))));
}

public class BuildOptionsTests
{
    static AppState S(string mode, DateTime? until = null, string before = "list", string game = "all") =>
        new() { IpsetMode = mode, IpsetAnyUntil = until, IpsetModeBeforeAny = before, GameMode = game };

    [Fact]
    public void Parse_UnknownIsOff()
    {
        Assert.Equal(new BuildOptions(), BuildOptions.From(new AppState(), DateTime.UtcNow));
        Assert.Equal(new BuildOptions(IpsetMode.Off, GameMode.Off), BuildOptions.From(S("???", game: "x"), DateTime.UtcNow));
        foreach (var m in Enum.GetValues<IpsetMode>()) Assert.Equal(m, BuildOptions.ParseIpset(BuildOptions.Format(m)));
        foreach (var g in Enum.GetValues<GameMode>()) Assert.Equal(g, BuildOptions.ParseGame(BuildOptions.Format(g)));
    }

    [Fact]
    public void Any_BeforeDeadline_Active()
    {
        var now = DateTime.UtcNow;
        var o = BuildOptions.From(S("any", now.AddMinutes(5)), now);
        Assert.Equal(IpsetMode.Any, o.Ipset);
        Assert.False(o.GameTcp);
        Assert.False(o.GameUdp);
    }

    [Fact]
    public void Any_Expired_FallsBackToPreviousMode()
    {
        var now = DateTime.UtcNow;
        var o = BuildOptions.From(S("any", now.AddSeconds(-1)), now);
        Assert.Equal(IpsetMode.List, o.Ipset);
        Assert.True(o.GameTcp && o.GameUdp);
        Assert.Equal(IpsetMode.List, BuildOptions.From(S("any", null), now).Ipset);      // без срока — сразу прежний
        Assert.Equal(IpsetMode.Off, BuildOptions.From(S("any", null, before: "any"), now).Ipset);
    }
}

/// <summary>API окна на Shell. Обход в тестах выключен (Enabled = false) — служба и winws не трогаются.</summary>
public class ShellNetFeaturesTests
{
    static (TempDir T, Shell S) NewShell()
    {
        var t = new TempDir();
        var s = new Shell(Make.Paths(t));
        s.Data.Seed();
        return (t, s);
    }

    [Fact]
    public void Defaults_AllOff()
    {
        var (t, s) = NewShell();
        using var _ = t;
        var f = s.Features;
        Assert.Equal(IpsetMode.Off, f.Ipset);
        Assert.Equal(GameMode.Off, f.Game);
        Assert.False(f.GameActive);
        Assert.Null(f.AnyUntilUtc);
        Assert.Equal(33048, f.List!.Lines);
    }

    [Fact]
    public async Task GameOn_WithIpsetOff_SwitchesIpsetToList()
    {
        var (t, s) = NewShell();
        using var _ = t;
        var r = await s.SetGameModeAsync(GameMode.TcpUdp);
        Assert.True(r.IpsetSwitched);
        Assert.NotNull(r.Note);
        Assert.Equal(IpsetMode.List, s.Features.Ipset);
        Assert.True(s.Features.GameActive);
        // Сохранено в state.json.
        var again = AppState.Load(s.Paths);
        Assert.Equal("list", again.IpsetMode);
        Assert.Equal("all", again.GameMode);

        Assert.False((await s.SetGameModeAsync(GameMode.TcpUdp)).IpsetSwitched);   // повтор — ничего
        var off = await s.SetGameModeAsync(GameMode.Off);
        // Включить → выключить = как было (правило владельца об откате): список включил сам игровой режим — он и выключается.
        Assert.Equal(IpsetMode.Off, s.Features.Ipset);
        Assert.Equal(L.T("core.shell.game_list_off"), off.Note);
        Assert.False(s.Features.GameActive);
        Assert.False(s.State.IpsetByGame);
    }

    [Fact]
    public async Task GameOff_keeps_the_list_the_person_chose()
    {
        var (t, s) = NewShell();
        using var _ = t;
        // Список включён рукой до игрового режима — остаётся.
        s.SetIpsetMode(IpsetMode.List);
        await s.SetGameModeAsync(GameMode.TcpUdp);
        await s.SetGameModeAsync(GameMode.Off);
        Assert.Equal(IpsetMode.List, s.Features.Ipset);
        Assert.False(s.State.IpsetByGame);
    }

    [Fact]
    public async Task AllAddresses_TenMinutes_BlocksGame_RevertsToPrevious()
    {
        var (t, s) = NewShell();
        using var _ = t;
        await s.SetGameModeAsync(GameMode.TcpUdp);
        var before = DateTime.UtcNow;
        s.StartAllAddresses();
        var f = s.Features;
        Assert.Equal(IpsetMode.Any, f.Ipset);
        Assert.Equal(IpsetMode.List, f.ReturnTo);
        Assert.InRange(f.AnyUntilUtc!.Value, before.AddMinutes(10).AddSeconds(-1), DateTime.UtcNow.AddMinutes(10).AddSeconds(1));
        Assert.True(f.GameBlocked);
        Assert.False(f.GameActive);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.SetGameModeAsync(GameMode.Tcp));

        Assert.False(s.RevertExpiredAllAddresses());   // срок не вышел
        s.State.IpsetAnyUntil = DateTime.UtcNow.AddSeconds(-1);
        Assert.Equal(IpsetMode.List, s.CurrentOptions.Ipset);   // сборка уже не видит «все адреса»
        Assert.True(s.RevertExpiredAllAddresses());
        Assert.Equal("list", s.State.IpsetMode);
        Assert.Null(s.State.IpsetAnyUntil);
        Assert.True(s.Features.GameActive);
        Assert.False(s.RevertExpiredAllAddresses());
    }

    [Fact]
    public void AllAddresses_RepeatExtends_StopReturnsNow()
    {
        var (t, s) = NewShell();
        using var _ = t;
        s.StartAllAddresses();
        s.State.IpsetAnyUntil = DateTime.UtcNow.AddMinutes(1);
        s.StartAllAddresses();
        Assert.True(s.State.IpsetAnyUntil > DateTime.UtcNow.AddMinutes(9));
        Assert.Equal("off", s.State.IpsetModeBeforeAny);   // повтор не запоминает «any» как прежний
        Assert.True(s.StopAllAddresses());
        Assert.Equal(IpsetMode.Off, s.Features.Ipset);
        Assert.False(s.StopAllAddresses());
    }

    [Fact]
    public async Task IpsetOff_TurnsGameOff()
    {
        var (t, s) = NewShell();
        using var _ = t;
        await s.SetGameModeAsync(GameMode.TcpUdp);
        s.SetIpsetMode(IpsetMode.Off);
        Assert.Equal(GameMode.Off, s.Features.Game);
        s.SetIpsetMode(IpsetMode.Any);
        Assert.Equal(IpsetMode.Any, s.Features.Ipset);
        s.SetIpsetMode(IpsetMode.List);
        Assert.Equal(IpsetMode.List, s.Features.Ipset);
        Assert.Null(s.State.IpsetAnyUntil);
    }

    [Fact]
    public void SetList_WithoutPackageList_Throws()
    {
        using var t = new TempDir();
        var seed = t.Sub("seed");
        foreach (var f in new[] { "core.json", "strategies.json", "targets.json" })
        {
            Directory.CreateDirectory(seed);
            File.Copy(Path.Combine(Repo.Data, f), Path.Combine(seed, f));
        }
        var s = new Shell(new AppPaths(t.Sub("root"), seed));
        Assert.Throws<InvalidOperationException>(() => s.SetIpsetMode(IpsetMode.List));
        Assert.Equal("off", s.State.IpsetMode);
    }
}
