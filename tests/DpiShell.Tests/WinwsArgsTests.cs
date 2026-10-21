using DpiShell.Core;

namespace DpiShell.Tests;

public class WinwsArgsValidateTests
{
    [Fact]
    public void RealCatalog_LoadsAndValidates()
    {
        var c = StrategyCatalog.Load(Repo.Strategies);
        Assert.Equal("winws", c.Engine);
        Assert.NotEmpty(c.Strategies);
        Assert.Equal(c.Strategies.Count, c.Strategies.Select(s => s.Id).Distinct().Count());
        foreach (var s in c.Strategies) WinwsArgs.Validate(s);
    }

    [Fact]
    public void RealCatalog_EveryFakeFileExistsInSeed()
    {
        var c = StrategyCatalog.Load(Repo.Strategies);
        var fakeDir = Path.Combine(Repo.Data, "fake");
        var missing = c.Strategies.SelectMany(s => s.Roles.Values)
            .SelectMany(a => a.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(t => t.Contains('=') ? t.Split('=', 2)[1] : "")
            .Where(v => v.StartsWith("{fake}/"))
            .Select(v => v["{fake}/".Length..])
            .Distinct()
            .Where(f => !File.Exists(Path.Combine(fakeDir, f)))
            .ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void ValidStrategy_Passes()
    {
        WinwsArgs.Validate(Make.Strategy(("general", "--dpi-desync=fake --dpi-desync-fake-tls={fake}/x.bin --dpi-desync-fakedsplit-pattern=0x00 --dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com")));
    }

    [Fact]
    public void UnknownRole_Rejected() =>
        Assert.Throws<InvalidDataException>(() => WinwsArgs.Validate(Make.Strategy(("everything", "--dpi-desync=fake"))));

    [Theory]
    [InlineData("--wf-raw=tcp")]
    [InlineData("--hostlist=x.txt")]
    [InlineData("--debug")]
    [InlineData("--debug=1")]
    [InlineData("--ipset=x.txt")]
    [InlineData("--new")]
    public void NotAllowedOption_Rejected(string tok) =>
        Assert.Throws<InvalidDataException>(() => WinwsArgs.Validate(Make.Strategy(("general", "--dpi-desync=fake " + tok))));

    [Theory]
    [InlineData(@"--dpi-desync-fake-tls=C:\x.bin")]
    [InlineData("--dpi-desync-fake-tls=C:/x.bin")]
    [InlineData("--dpi-desync-fake-tls=../x")]
    [InlineData("--dpi-desync-fake-tls=/etc/x")]
    [InlineData("--dpi-desync-fake-tls=lists/x.bin")]
    [InlineData("--dpi-desync-fake-tls={fake}/../core/winws.exe")]
    [InlineData(@"--dpi-desync-fake-tls={fake}/..\x")]
    [InlineData(@"--dpi-desync-fake-tls=\\server\share\x.bin")]
    [InlineData("--dpi-desync-split-pos=..")]
    [InlineData("--dpi-desync-fake-tls=..x")]
    public void PathOutsideFakeOrDotDot_Rejected(string tok) =>
        Assert.Throws<InvalidDataException>(() => WinwsArgs.Validate(Make.Strategy(("general", tok))));

    // БАГ: значение с табом проходит Validate. Build оставляет его одним аргументом, но служба получает
    // командную строку через WinwsArgs.Join, а Windows режет её и по табу — в winws попадает любой параметр.
    [Fact]
    public void Regression_TabInsideValue_AllowsInjectingOptions_ShouldBeRejected() =>
        Assert.Throws<InvalidDataException>(() => WinwsArgs.Validate(Make.Strategy(("general", "--dpi-desync=fake\t--wf-raw=x"))));

    // БАГ: кавычка в значении проходит Validate и ломает разбор строки службы (Join не экранирует кавычки).
    [Fact]
    public void Regression_QuoteInsideValue_ShouldBeRejected() =>
        Assert.Throws<InvalidDataException>(() => WinwsArgs.Validate(Make.Strategy(("general", "--dpi-desync=fake\"x"))));

    // БАГ: голое имя файла (без {fake}/) проходит Validate — winws прочитает файл из рабочего каталога
    // (core\ или System32 у службы), т.е. каталог всё-таки может указать на файл вне папки фейков.
    [Fact]
    public void Regression_BareFileNameForFakeOption_ShouldBeRejected() =>
        Assert.Throws<InvalidDataException>(() => WinwsArgs.Validate(Make.Strategy(("general", "--dpi-desync-fake-tls=cygwin1.dll"))));
}

public class WinwsArgsBuildTests
{
    static readonly AppPaths P = new(@"C:\Root", @"C:\Seed");

    [Fact]
    public void Build_WfFirst_ProfilesInRoleOrder_NewOnlyBetween()
    {
        // Порядок в словаре нарочно не совпадает с RoleOrder.
        var s = Make.Strategy(("general", "--dpi-desync=G"), ("quic", "--dpi-desync=Q"), ("google", "--dpi-desync=O"));
        var a = WinwsArgs.Build(s, P);

        Assert.Equal($"--wf-tcp={WinwsArgs.WfTcp}", a[0]);
        Assert.Equal($"--wf-udp={WinwsArgs.WfUdp}", a[1]);
        Assert.NotEqual("--new", a[2]);
        Assert.NotEqual("--new", a[^1]);
        Assert.Equal(2, a.Count(x => x == "--new"));

        var order = a.Where(x => x.StartsWith("--dpi-desync=")).ToList();
        Assert.Equal(["--dpi-desync=Q", "--dpi-desync=O", "--dpi-desync=G"], order);

        // Каждый профиль начинается с фильтра своей роли сразу после --new.
        var news = a.Select((x, i) => (x, i)).Where(t => t.x == "--new").Select(t => t.i).ToList();
        Assert.Equal("--filter-udp=443", a[2]);
        Assert.Equal("--filter-tcp=443", a[news[0] + 1]);
        Assert.Equal("--filter-tcp=80,443", a[news[1] + 1]);
        Assert.DoesNotContain(a, x => x.Contains("--new") && x != "--new");
    }

    [Fact]
    public void Build_SingleRole_NoNew()
    {
        var a = WinwsArgs.Build(Make.Strategy(("discord_udp", "--dpi-desync=fake")), P);
        Assert.DoesNotContain("--new", a);
        Assert.Equal("--filter-udp=19294-19344,50000-50100", a[2]);
        Assert.Equal("--dpi-desync=fake", a[^1]);
    }

    [Fact]
    public void Build_FakePlaceholderReplaced()
    {
        var a = WinwsArgs.Build(Make.Strategy(("general", "--dpi-desync=fake --dpi-desync-fake-tls={fake}/tls.bin")), P);
        Assert.Contains("--dpi-desync-fake-tls=" + Path.Combine(P.Fake, "tls.bin"), a);
        Assert.DoesNotContain(a, x => x.Contains("{fake}"));
    }

    [Fact]
    public void Build_ListsPointToPathsLists()
    {
        var a = WinwsArgs.Build(Make.Strategy(("general", "--dpi-desync=fake")), P);
        Assert.Contains("--hostlist=" + Path.Combine(P.Lists, "general.txt"), a);
        Assert.Contains("--hostlist=" + Path.Combine(P.Lists, DataStore.UserGeneral), a);
        Assert.Contains("--hostlist-exclude=" + Path.Combine(P.Lists, DataStore.UserExclude), a);
    }

    [Fact]
    public void Build_AllRealStrategies_NoPlaceholdersAndBalancedNew()
    {
        foreach (var s in StrategyCatalog.Load(Repo.Strategies).Strategies)
        {
            var a = WinwsArgs.Build(s, P);
            Assert.DoesNotContain(a, x => x.Contains("{fake}"));
            // Игровые роли каталога без игрового режима в строку не попадают.
            Assert.Equal(s.Roles.Keys.Count(r => !r.StartsWith("game_")) - 1, a.Count(x => x == "--new"));
            Assert.NotEqual("--new", a[^1]);
            Assert.DoesNotContain(a, x => x.Contains(' '));
        }
    }

    [Fact]
    public void Build_DiscordTcp_AfterDiscordMedia_BeforeGoogleAndGeneral_WithOwnListAndExcludes()
    {
        var s = Make.Strategy(("general", "--dpi-desync=G"), ("google", "--dpi-desync=O"), ("discord_tcp", "--dpi-desync=D"), ("discord_media", "--dpi-desync=M"), ("quic", "--dpi-desync=Q"));
        var a = WinwsArgs.Build(s, P).ToList();
        var order = a.Where(x => x.StartsWith("--dpi-desync=")).ToList();
        Assert.Equal(["--dpi-desync=Q", "--dpi-desync=M", "--dpi-desync=D", "--dpi-desync=O", "--dpi-desync=G"], order);

        var start = a.IndexOf("--dpi-desync=M") + 1;                       // после media идёт --new и профиль discord_tcp
        Assert.Equal("--new", a[start]);
        Assert.Equal("--filter-tcp=80,443", a[start + 1]);
        Assert.Equal("--hostlist=" + Path.Combine(P.Lists, "discord.txt"), a[start + 2]);
        var profile = a.Skip(start + 1).TakeWhile(x => x != "--new").ToList();
        Assert.Contains("--hostlist-exclude=" + Path.Combine(P.Lists, "exclude.txt"), profile);
        Assert.Contains("--hostlist-exclude=" + Path.Combine(P.Lists, DataStore.UserExclude), profile);
        Assert.Contains("--ipset-exclude=" + Path.Combine(P.Lists, IpLists.ExcludeFile), profile);
        Assert.Contains("--ipset-exclude=" + Path.Combine(P.Lists, IpLists.UserExcludeFile), profile);
        Assert.DoesNotContain(profile, x => x.Contains("general.txt") || x.Contains(DataStore.UserGeneral));
    }

    [Theory]
    [InlineData(IpsetMode.Off)]
    [InlineData(IpsetMode.List)]
    [InlineData(IpsetMode.Any)]
    public void Build_DiscordTcp_KeepsItsHostlist_InEveryIpsetMode(IpsetMode mode)
    {
        var a = WinwsArgs.Build(Make.Strategy(("discord_tcp", "--dpi-desync=D"), ("general", "--dpi-desync=G")), P, new BuildOptions(mode));
        Assert.Contains("--hostlist=" + Path.Combine(P.Lists, "discord.txt"), a);
    }

    [Fact]
    public void Build_NoDiscordTcpRole_NoSuchProfile_CatalogStrategiesUnchanged()
    {
        foreach (var s in StrategyCatalog.Load(Repo.Strategies).Strategies)
            Assert.DoesNotContain(WinwsArgs.Build(s, P), x => x.Contains("discord.txt"));
    }

    /// <summary>Эталон роутера (router/test/golden.json) строится из этого же кода: у 20 каталожных стратегий командная строка не должна измениться.</summary>
    [Fact]
    public void Build_CatalogStrategies_MatchRouterGolden()
    {
        var root = Directory.GetParent(Repo.Data)!.FullName;
        var file = Path.Combine(root, "router", "test", "golden.json");
        if (!File.Exists(file)) return;   // тесты запущены не из репозитория
        var golden = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file)).RootElement.GetProperty("strategies");
        var paths = new AppPaths(root: "ROOT");
        string Norm(string a) => a.Replace(@"ROOT\lists\", "{lists}/").Replace(@"ROOT\fake\", "{fake}/");
        var catalog = StrategyCatalog.Load(Repo.Strategies).Strategies;
        Assert.Equal(catalog.Count, golden.EnumerateObject().Count());
        foreach (var s in catalog)
            Assert.Equal(golden.GetProperty(s.Id).EnumerateArray().Select(x => x.GetString()!).ToList(), WinwsArgs.Build(s, paths).Select(Norm).ToList());
    }

    [Fact]
    public void Join_QuotesOnlyArgsWithSpaces()
    {
        Assert.Equal("--a=1 --b=\"x y\" \"p q\"", WinwsArgs.Join(["--a=1", "--b=x y", "p q"]));
    }

    // БАГ: Build режет фильтры и desync по пробелу ПОСЛЕ подстановки путей (Strategies.cs:94, 96),
    // поэтому путь с пробелом распадается на куски, и Join уже нечего брать в кавычки.
    [Fact]
    public void Regression_Build_SpacedRoot_PathMustStayOneArgument()
    {
        var p = new AppPaths(@"C:\Dpi Shell", @"C:\Seed");
        var a = WinwsArgs.Build(Make.Strategy(("general", "--dpi-desync=fake --dpi-desync-fake-tls={fake}/tls.bin")), p);
        Assert.Contains("--hostlist=" + Path.Combine(p.Lists, "general.txt"), a);
        Assert.Contains("--dpi-desync-fake-tls=" + Path.Combine(p.Fake, "tls.bin"), a);
    }

    [Fact]
    public void Regression_Join_WithSpacedRoot_RoundTripsThroughWindowsParser()
    {
        using var t = new TempDir(" with space");
        var p = new AppPaths(t.Sub("Dpi Shell"), Repo.Data);
        foreach (var s in StrategyCatalog.Load(Repo.Strategies).Strategies)
        {
            var args = WinwsArgs.Build(s, p);
            var line = WinwsArgs.Join(args);
            Assert.Contains("--wf-tcp=" + WinwsArgs.WfTcp + " ", line); // без кавычек
            Assert.DoesNotContain("\"--new\"", line);
            Assert.True(line.Contains("=\"" + p.Lists), line); // путь с пробелом — в кавычках после «=»
            var parsed = Native.ParseCommandLine("winws.exe " + line).Skip(1).ToArray();
            Assert.Equal(args.ToArray(), parsed);
        }
    }
}
