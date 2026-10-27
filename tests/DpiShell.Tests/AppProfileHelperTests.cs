using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.Tests;

/// <summary>Команды записи и профилей программ (протокол 1.4): белый список аргументов, канал, настоящий помощник над временной папкой.</summary>
public sealed class AppProfileHelperTests
{
    static readonly FakeHelperBackend Catalog = new();
    static HelperCall Parse(string json) => HelperCommands.Parse(json, Catalog);
    static string Rejected(string json) => Assert.Throws<HelperRequestException>(() => Parse(json)).Message;

    // ---------- белый список ----------

    [Fact]
    public void Valid_requests_are_typed()
    {
        var a = Parse("""{"id":1,"cmd":"rec_start","args":{"mode":"new"}}""");
        Assert.Equal("new", a.Str("mode"));
        Assert.True(a.Bool("driver", true));
        var b = Parse("""{"id":2,"cmd":"rec_start","args":{"mode":"process","pid":4321,"driver":false}}""");
        Assert.Equal(4321, b.Int("pid"));
        Assert.False(b.Bool("driver", true));

        var s = Parse("""{"id":3,"cmd":"app_profile_set","args":{"title":"Игра","tcp":"443","udp":"27030,27015-27016","addresses":["203.0.113.50","198.51.100.0/24"],"variant":2}}""");
        Assert.Equal("27015-27016,27030", s.Str("udp"));   // порты приведены к каноническому виду
        Assert.Equal(2, s.Int("variant"));
        Assert.Equal(2, s.List("addresses")!.Count);
        var r = Parse("""{"id":4,"cmd":"app_profile_remove","args":{"id":"p0a1b2c3d"}}""");
        Assert.Equal("p0a1b2c3d", r.Str("id"));
    }

    [Theory]
    [InlineData("""{"id":1,"cmd":"rec_start","args":{}}""", "обязательного")]
    [InlineData("""{"id":1,"cmd":"rec_start","args":{"mode":"all"}}""", "допустимо")]
    [InlineData("""{"id":1,"cmd":"rec_start","args":{"mode":"process"}}""", "нужен pid")]
    [InlineData("""{"id":1,"cmd":"rec_start","args":{"mode":"new","pid":5}}""", "только для режима process")]
    [InlineData("""{"id":1,"cmd":"rec_start","args":{"mode":"process","pid":-1}}""", "целое число")]
    [InlineData("""{"id":1,"cmd":"rec_start","args":{"mode":"process","pid":"123"}}""", "целое число")]
    [InlineData("""{"id":1,"cmd":"rec_start","args":{"mode":"process","pid":1.5}}""", "целое число")]
    [InlineData("""{"id":1,"cmd":"rec_start","args":{"mode":"process","pid":99999999999}}""", "целое число")]
    [InlineData("""{"id":1,"cmd":"rec_start","args":{"mode":"new","exe":"C:\\x.exe"}}""", "лишнее поле")]
    [InlineData("""{"id":1,"cmd":"rec_start","args":{"mode":"new","driver":"no"}}""", "true или false")]
    [InlineData("""{"id":1,"cmd":"rec_start","args":{"mode":"new","seconds":99999}}""", "лишнее поле")]
    [InlineData("""{"id":1,"cmd":"rec_stop","args":{"keep":true}}""", "лишнее поле")]
    [InlineData("""{"id":1,"cmd":"rec_status","args":{"x":1}}""", "лишнее поле")]
    [InlineData("""{"id":1,"cmd":"app_profiles","args":{"x":1}}""", "лишнее поле")]
    public void Recording_abuse_rejected(string json, string part) => Assert.Contains(part, Rejected(json), StringComparison.OrdinalIgnoreCase);

    [Theory]
    [InlineData("""{"id":1,"cmd":"app_profile_set","args":{"title":"x","addresses":["203.0.113.5"],"variant":0}}""", "порты")]                      // нет портов
    [InlineData("""{"id":1,"cmd":"app_profile_set","args":{"title":"x","tcp":"443","addresses":["203.0.113.5"]}}""", "обязательного")]            // нет variant
    [InlineData("""{"id":1,"cmd":"app_profile_set","args":{"title":"x","tcp":"443","addresses":["203.0.113.5"],"variant":8}}""", "целое число")]   // за пределом
    [InlineData("""{"id":1,"cmd":"app_profile_set","args":{"title":"x","tcp":"443","addresses":["203.0.113.5"],"variant":-1}}""", "целое число")]
    [InlineData("""{"id":1,"cmd":"app_profile_set","args":{"title":"x","tcp":"443; --new","addresses":["203.0.113.5"],"variant":0}}""", "порты")]
    [InlineData("""{"id":1,"cmd":"app_profile_set","args":{"title":"x","tcp":"0-70000","addresses":["203.0.113.5"],"variant":0}}""", "порты")]
    [InlineData("""{"id":1,"cmd":"app_profile_set","args":{"title":"x","udp":"1,3,5,7,9,11,13,15,17,19,21,23,25,27,29,31,33","addresses":["203.0.113.5"],"variant":0}}""", "порты")]
    [InlineData("""{"id":1,"cmd":"app_profile_set","args":{"title":"x","tcp":443,"addresses":["203.0.113.5"],"variant":0}}""", "строка")]
    [InlineData("""{"id":1,"cmd":"app_profile_set","args":{"title":"x","tcp":"443","addresses":["C:\\lists\\x.txt"],"variant":0}}""", "путь или командную строку")]
    [InlineData("""{"id":1,"cmd":"app_profile_set","args":{"title":"x","tcp":"443","addresses":["1.2.3.4\"; calc"],"variant":0}}""", "путь или командную строку")]
    [InlineData("""{"id":1,"cmd":"app_profile_set","args":{"title":"x","tcp":"443","addresses":"203.0.113.5","variant":0}}""", "список")]
    [InlineData("""{"id":1,"cmd":"app_profile_set","args":{"title":"x","tcp":"443","addresses":["203.0.113.5"],"variant":0,"desync":"--debug"}}""", "лишнее поле")]
    [InlineData("""{"id":1,"cmd":"app_profile_set","args":{"title":"x","tcp":"443","addresses":["203.0.113.5"],"variant":0,"args":"--wf-tcp=1"}}""", "лишнее поле")]
    [InlineData("""{"id":1,"cmd":"app_profile_set","args":{"id":"../x","title":"x","tcp":"443","addresses":["203.0.113.5"],"variant":0}}""", "номер профиля")]
    [InlineData("""{"id":1,"cmd":"app_profile_set","args":{"title":"","tcp":"443","addresses":["203.0.113.5"],"variant":0}}""", "пустая")]
    [InlineData("""{"id":1,"cmd":"app_profile_remove","args":{}}""", "обязательного")]
    [InlineData("""{"id":1,"cmd":"app_profile_remove","args":{"id":"p0a1b2c3d4"}}""", "номер профиля")]
    [InlineData("""{"id":1,"cmd":"app_profile_remove","args":{"id":"..\\winws"}}""", "номер профиля")]
    public void Profile_abuse_rejected(string json, string part) => Assert.Contains(part, Rejected(json), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void TooManyAddresses_Rejected()
    {
        var addrs = string.Join(',', Enumerable.Range(0, AppProfiles.MaxAddresses + 1).Select(i => $"\"198.51.{i / 250}.{i % 250 + 1}\""));
        Assert.Contains("больше", Rejected("""{"id":1,"cmd":"app_profile_set","args":{"title":"x","tcp":"443","addresses":[""" + addrs + """],"variant":0}}"""));
    }

    [Fact]
    public void Commands_AreExclusive_ExceptStatusReads()
    {
        Assert.All(new[] { "rec_start", "rec_stop", "app_profile_set", "app_profile_remove" }, n => Assert.True(HelperCommands.Specs[n].Exclusive, n));
        Assert.All(new[] { "rec_status", "app_profiles" }, n => Assert.False(HelperCommands.Specs[n].Exclusive, n));
        Assert.All(new[] { "rec_start", "rec_stop", "rec_status", "app_profiles", "app_profile_set", "app_profile_remove" }, n => Assert.False(HelperCommands.Specs[n].Long, n));
    }

    [Fact]
    public void Protocol_IsNextMinor_AndDocumented()
    {
        Assert.True(Version.Parse(HelperProtocol.Api) >= new Version(1, 5));   // 1.5 — подключения; дальше миноры растут (1.6 — откат всего)
        var doc = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Repo.Data)!, "docs", "dev", "helper-protocol.md"));
        foreach (var c in new[] { "rec_start {mode, pid?, driver?}", "rec_status", "rec_stop", "app_profiles", "app_profile_set {id?, title, tcp?, udp?, addresses, variant}", "app_profile_remove {id}" })
            Assert.Contains("`" + c + "`", doc);
        Assert.Contains("1.5 — подключения программ", doc);
    }

    // ---------- настоящий помощник над временной папкой ----------

    static async Task<(HelperFixture, HelperLink, Shell, FakeFlowSource, FakeProcesses)> Setup(TempDir t)
    {
        var shell = new Shell(Make.Paths(t));
        shell.Data.Seed();
        var flows = new FakeFlowSource();
        var procs = new FakeProcesses(new ProcInfo(4, 0, "System"));
        var f = new HelperFixture(backend: PcHelper.Over(shell, flows, procs));
        return (f, await f.LinkAsync(), shell, flows, procs);
    }

    [Fact]
    public async Task Channel_Record_Stop_ProfileSet_WritesFile_AndBuildsArgs()
    {
        using var t = new TempDir();
        var (f, link, shell, flows, procs) = await Setup(t);
        await using var _ = f;
        var c = link.Client!;

        var st = await c.RecStartAsync(RecordingMode.NewProcesses);
        Assert.True(st.Running);
        procs.List.Add(new ProcInfo(2000, 4, "game.exe"));
        flows.Push("udp", "203.0.113.50", 27015, 2000);
        flows.Push("udp", "203.0.113.51", 27016, 2000);
        flows.Push("tcp", "198.51.100.7", 443, 2000, FlowHint.Established);
        Assert.Equal(3, (await c.RecStatusAsync()).Flows);
        await Assert.ThrowsAsync<HelperException>(() => c.RecStartAsync(RecordingMode.NewProcesses));   // вторая запись — отказ

        var rec = await c.RecStopAsync();
        Assert.Equal(3, rec.Flows.Count);
        Assert.False((await c.RecStatusAsync()).Active);        // помощник записанное забыл
        await Assert.ThrowsAsync<HelperException>(() => c.RecStopAsync());

        var draft = AppProfileBuilder.FromRecording(rec);
        var info = await c.AppProfilesAsync();
        Assert.True(info.Variants.Count >= 2);
        Assert.Empty(info.Profiles);

        var p = await c.AppProfileSetAsync(null, draft.Title, draft.TcpPorts, draft.UdpPorts, draft.Addresses, 0);
        Assert.True(AppProfiles.IsValidId(p.Id));
        Assert.Equal("443", p.TcpPorts);
        Assert.Equal("27015-27016", p.UdpPorts);

        // В state.json у помощника, файл адресов — в закрытой папке, командная строка winws получила профиль первым.
        var state = AppState.Load(shell.Paths);
        var saved = Assert.Single(state.AppProfiles);
        Assert.Equal(p.Id, saved.Id);
        var file = Path.Combine(shell.Paths.Lists, AppProfiles.FileName(p.Id));
        Assert.Equal(["198.51.100.7/32", "203.0.113.50/32", "203.0.113.51/32"], File.ReadAllLines(file).Order(StringComparer.Ordinal));
        var args = WinwsArgs.Build(shell.Catalog.Strategies[0], shell.Paths, shell.CurrentOptions);
        Assert.Contains("--filter-tcp=443", args);
        Assert.Equal($"--ipset={file}", args.First(a => a.StartsWith("--ipset=") && a.EndsWith(".txt") && a.Contains("app-")));
        Assert.True(args.ToList().IndexOf("--filter-udp=27015-27016") < args.ToList().IndexOf("--filter-udp=443"));   // раньше общих ролей

        // «Вернуть как было»: профиль, файл и аргументы исчезли.
        Assert.True(await c.AppProfileRemoveAsync(p.Id));
        Assert.False(await c.AppProfileRemoveAsync(p.Id));
        Assert.Empty(AppState.Load(shell.Paths).AppProfiles);
        Assert.False(File.Exists(file));
        Assert.DoesNotContain(WinwsArgs.Build(shell.Catalog.Strategies[0], shell.Paths, shell.CurrentOptions), a => a.Contains("app-"));
    }

    [Fact]
    public async Task ProfileSet_SameId_ChangesVariantOnly_AndLimitsCount()
    {
        using var t = new TempDir();
        var (f, link, shell, _, _) = await Setup(t);
        await using var _ = f;
        var c = link.Client!;
        var p0 = await c.AppProfileSetAsync(null, "Игра", "443", null, ["203.0.113.50"], 0);
        var p1 = await c.AppProfileSetAsync(p0.Id, "Игра", "443", null, ["203.0.113.50"], 1);
        Assert.Equal(p0.Id, p1.Id);
        Assert.Equal(1, p1.Variant);
        Assert.Single((await c.AppProfilesAsync()).Profiles);
        Assert.NotEqual(AppState.Load(shell.Paths).AppProfiles[0].TcpDesync, shell.ProfileVariants()[0].Tcp);

        // Чужой id — отказ; лимит профилей — отказ с понятной причиной.
        await Assert.ThrowsAsync<HelperException>(() => c.AppProfileSetAsync("p00000000", "x", "443", null, ["203.0.113.50"], 0));
        for (int i = 1; i < AppProfiles.MaxProfiles; i++) await c.AppProfileSetAsync(null, "Игра " + i, "443", null, ["203.0.113.50"], 0);
        var e = await Assert.ThrowsAsync<HelperException>(() => c.AppProfileSetAsync(null, "Лишняя", "443", null, ["203.0.113.50"], 0));
        Assert.Contains("Профилей уже", e.Message);
    }

    [Fact]
    public async Task ProfileSet_RejectsLocalAndWideAddresses_AndOutOfRangeVariant()
    {
        using var t = new TempDir();
        var (f, link, _, _, _) = await Setup(t);
        await using var _ = f;
        var c = link.Client!;
        // Служебные адреса валидатор молча убирает; остаться ничего не должно — профиль не создаётся.
        await Assert.ThrowsAsync<HelperException>(() => c.AppProfileSetAsync(null, "x", "443", null, ["192.168.1.5", "10.0.0.1"], 0));
        await Assert.ThrowsAsync<HelperException>(() => c.AppProfileSetAsync(null, "x", "443", null, ["203.0.0.0/8"], 0));
        // Номер варианта сверх имеющихся (каталог даёт до 8) — отказ.
        int count = (await c.AppProfilesAsync()).Variants.Count;
        if (count < AppProfiles.MaxVariants)
        {
            var e = await Assert.ThrowsAsync<HelperException>(() => c.AppProfileSetAsync(null, "x", "443", null, ["203.0.113.50"], count));
            Assert.Contains("варианта", e.Message);
        }
        Assert.Empty((await c.AppProfilesAsync()).Profiles);
    }

    [Fact]
    public async Task Recording_StopsWhenWindowDisconnects_AndOnRemoveAll()
    {
        using var t = new TempDir();
        var shell = new Shell(Make.Paths(t));
        shell.Data.Seed();
        var flows = new FakeFlowSource();
        var backend = PcHelper.Over(shell, flows);
        backend.RecStart(RecordingMode.NewProcesses, 0, true);
        Assert.True(backend.RecStatus().Active);
        backend.OnClientsGone();
        Assert.False(backend.RecStatus().Active);
        Assert.True(flows.Stopped && flows.Disposed);

        backend.AppProfileSet(null, "Игра", "443", null, ["203.0.113.50"], 0);
        backend.RecStart(RecordingMode.NewProcesses, 0, true);
        backend.RemoveAll();
        Assert.False(backend.RecStatus().Active);
        Assert.Empty(backend.AppProfilesInfo().Profiles);
    }

    [Fact]
    public async Task ProcessMode_UnknownPid_Refused()
    {
        using var t = new TempDir();
        var (f, link, _, _, _) = await Setup(t);
        await using var _ = f;
        var e = await Assert.ThrowsAsync<HelperException>(() => link.Client!.RecStartAsync(RecordingMode.Process, 424242));
        Assert.Contains("процесса сейчас нет", e.Message);
    }

    /// <summary>Профиль в state.json, подделанный на диске, до winws не доходит: Build пропускает его.</summary>
    [Fact]
    public void TamperedStateProfile_NeverReachesWinws()
    {
        using var t = new TempDir();
        var shell = new Shell(Make.Paths(t));
        shell.Data.Seed();
        shell.State.AppProfiles.Add(new AppProfile
        {
            Id = "p0a1b2c3d", Title = "x", TcpPorts = "443", Addresses = ["203.0.113.5/32"],
            TcpDesync = "--dpi-desync=fake --wf-raw=true --debug",
        });
        shell.State.Save(shell.Paths);
        var again = new Shell(Make.Paths(t));
        var args = WinwsArgs.Build(again.Catalog.Strategies[0], again.Paths, again.CurrentOptions);
        Assert.DoesNotContain(args, a => a.Contains("app-") || a.Contains("--debug") || a.Contains("--wf-raw"));
    }
}
