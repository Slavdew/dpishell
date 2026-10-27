using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.Tests;

/// <summary>
/// Игровой режим «Выкл / Свои профили / Общий», сигнатура пакета в профиле, свой фильтр WinDivert, импорт и экспорт в формате Flowseal.
/// Образец — профиль форка Mistfall Hunter (docs: game-mistfall-hunter.md): TCP 12001, 79 байт, начало 00 00 04 4D.
/// </summary>
public sealed class GameProfilesTests
{
    const string Mistfall = "outbound and tcp.DstPort = 12001 and tcp.PayloadLength = 79 and tcp.Payload32[0] = 0x0000044d";
    const string MistfallParams = "--wf-tcp=12001 --filter-tcp=12001 --dpi-desync=fake --dpi-desync-any-protocol=1 --dpi-desync-fooling=ts --dpi-desync-repeats=8 "
        + "--dpi-desync-fake-tls=\"%BIN%tls_clienthello_max_ru.bin\" --dpi-desync-fake-unknown=\"%BIN%stun.bin\" --ipset=\"%LISTS%ipset-aws.txt\"";
    const string MistfallDesync = "--dpi-desync=fake --dpi-desync-any-protocol=1 --dpi-desync-fooling=ts --dpi-desync-repeats=8 "
        + "--dpi-desync-fake-tls={fake}/tls_clienthello_max_ru.bin --dpi-desync-fake-unknown={fake}/stun.bin";
    static readonly HashSet<string> Fakes = ["tls_clienthello_max_ru.bin", "stun.bin"];

    static AppProfile Sig(Action<AppProfile>? tweak = null)
    {
        var p = new AppProfile
        {
            Id = "p0a1b2c3d", Title = "Mistfall", TcpPorts = "12001", TcpDesync = MistfallDesync,
            TcpPayloadLength = 79, TcpPayloadHex = "0000044d",
        };
        tweak?.Invoke(p);
        return p;
    }

    // ---------- фильтр из полей ----------

    [Fact]
    public void Filter_Mistfall_LikeFork() => Assert.Equal(Mistfall, AppProfiles.Filter(Sig(), true));

    [Fact]
    public void Filter_RangesTailAndUdp()
    {
        var p = new AppProfile { TcpPorts = "443,20001-21000", TcpPayloadHex = "0102030405", UdpPorts = "5000-5010", UdpPayloadLength = 12 };
        Assert.Equal("outbound and (tcp.DstPort = 443 or (tcp.DstPort >= 20001 and tcp.DstPort <= 21000)) and tcp.Payload32[0] = 0x01020304 and tcp.Payload[4] = 0x05",
            AppProfiles.Filter(p, true));
        Assert.Equal("outbound and udp.DstPort >= 5000 and udp.DstPort <= 5010 and udp.PayloadLength = 12", AppProfiles.Filter(p, false));
        Assert.Null(AppProfiles.Filter(new AppProfile { TcpPorts = "443" }, true));   // сигнатуры нет — фильтра нет
    }

    [Theory]
    [InlineData(0, "0000044D")]        // заглавные
    [InlineData(0, "000")]             // нечётное число знаков
    [InlineData(0, "zz")]
    [InlineData(0, "000000000000000000000000000000000000")]   // 18 байт
    [InlineData(1501, "")]
    [InlineData(-1, "")]
    [InlineData(3, "00000000")]        // начало длиннее нагрузки
    [InlineData(0, "00 00")]
    public void Signature_Garbage_Rejected(int length, string hex)
    {
        var p = Sig(x => { x.TcpPayloadLength = length; x.TcpPayloadHex = hex; });
        Assert.NotNull(AppProfiles.Problem(p));
        Assert.Null(AppProfiles.Filter(p, true));
    }

    [Fact]
    public void Signature_Limits_Accepted()
    {
        Assert.Null(AppProfiles.Problem(Sig(x => { x.TcpPayloadLength = 1500; x.TcpPayloadHex = new string('a', 32); })));
        Assert.Null(AppProfiles.Problem(Sig(x => { x.TcpPayloadLength = 1; x.TcpPayloadHex = ""; })));
    }

    [Fact]
    public void Addresses_OptionalOnlyWhenEveryProtocolHasSignature()
    {
        Assert.Null(AppProfiles.Problem(Sig()));   // адресов нет, сигнатура есть
        Assert.NotNull(AppProfiles.Problem(Sig(p => { p.TcpPayloadLength = 0; p.TcpPayloadHex = ""; })));   // без сигнатуры адреса обязательны
        Assert.NotNull(AppProfiles.Problem(Sig(p => { p.UdpPorts = "5000"; p.UdpDesync = "--dpi-desync=fake --dpi-desync-any-protocol=1"; })));   // у UDP сигнатуры нет
        Assert.Null(AppProfiles.Problem(Sig(p => { p.UdpPorts = "5000"; p.UdpDesync = "--dpi-desync=fake --dpi-desync-any-protocol=1"; p.UdpPayloadLength = 20; })));
        Assert.NotNull(AppProfiles.Problem(Sig(p => p.Addresses = ["10.0.0.0/24"])));   // служебная сеть остаётся отказом
    }

    // ---------- командная строка winws ----------

    [Fact]
    public void Build_SignatureProfile_RawPartInsteadOfPorts_NoIpset()
    {
        using var t = new TempDir();
        var paths = Make.Paths(t);
        var s = Make.Strategy(("general", "--dpi-desync=fake"));
        var udp = Sig(p => { p.Id = "p0a1b2c3e"; p.TcpPorts = ""; p.TcpPayloadLength = 0; p.TcpPayloadHex = ""; p.TcpDesync = ""; p.UdpPorts = "20001-21000"; p.UdpDesync = "--dpi-desync=fake --dpi-desync-any-protocol=1"; p.Addresses = ["198.51.100.0/24"]; });
        var args = WinwsArgs.Build(s, paths, new BuildOptions(IpsetMode.Off, GameMode.Profiles, [Sig(), udp])).ToList();

        Assert.Equal($"--wf-tcp={WinwsArgs.WfTcp}", args[0]);   // порт 12001 в общий фильтр не попал
        Assert.Equal($"--wf-udp={WinwsArgs.WfUdp},20001-21000", args[1]);   // у UDP-профиля без сигнатуры порты на месте
        var file = Path.Combine(paths.Lists, AppProfiles.FilterFileName("p0a1b2c3d", true));
        Assert.Contains($"--wf-raw-part=@{file}", args);
        Assert.DoesNotContain(args, a => a.Contains("p0a1b2c3d.txt"));   // адресов у профиля нет — --ipset не подаётся
        Assert.Contains("--filter-tcp=12001", args);
        Assert.Contains(args, a => a.StartsWith("--ipset=") && a.EndsWith("app-p0a1b2c3e.txt"));
        Assert.True(args.IndexOf($"--wf-raw-part=@{file}") < args.IndexOf("--filter-tcp=12001"));
        Assert.Contains("--wf-raw-part=@", WinwsArgs.Join(args));
        Assert.Equal(args, Native.ParseCommandLine("winws.exe " + WinwsArgs.Join(args)).Skip(1));
    }

    [Fact]
    public void Shell_WritesFilterFile_AndCleansIt()
    {
        using var t = new TempDir();
        var shell = new Shell(Make.Paths(t));
        shell.Data.Seed();
        var info = shell.SetAppProfile(null, "Mistfall", "12001", null, [], 0, new ProfileSignature(79, "0000044d"));
        var file = Path.Combine(shell.Paths.Lists, AppProfiles.FilterFileName(info.Id, true));
        Assert.Equal(Mistfall + "\n", File.ReadAllText(file));
        Assert.Equal(GameMode.Profiles, BuildOptions.ParseGame(shell.State.GameMode));
        var args = WinwsArgs.Build(shell.Catalog.Strategies[0], shell.Paths, shell.CurrentOptions);
        Assert.Contains($"--wf-raw-part=@{file}", args);
        Assert.True(shell.RemoveAppProfile(info.Id));
        Assert.False(File.Exists(file));
        Assert.Equal("off", shell.State.GameMode);   // профилей не осталось — режим вернулся
    }

    [Fact]
    public void Shell_NoSignatureNoAddresses_Rejected()
    {
        using var t = new TempDir();
        var shell = new Shell(Make.Paths(t));
        shell.Data.Seed();
        Assert.Throws<InvalidOperationException>(() => shell.SetAppProfile(null, "x", "443", null, [], 0));
    }

    [Fact]
    public void FilterLimit_TooManyBigFilters_RejectedAndTrimmed()
    {
        var ranges = string.Join(',', Enumerable.Range(0, AppProfiles.MaxPortRanges).Select(i => $"{2000 + i * 100}-{2050 + i * 100}"));
        AppProfile Big(int n) => Sig(p =>
        {
            p.Id = "p0000000" + n; p.TcpPorts = ranges; p.UdpPorts = ranges; p.UdpDesync = "--dpi-desync=fake --dpi-desync-any-protocol=1";
            p.TcpPayloadHex = "00112233445566778899aabbccddeeff"; p.UdpPayloadHex = "00112233445566778899aabbccddeeff";
        });
        var list = Enumerable.Range(0, AppProfiles.MaxProfiles).Select(Big).ToList();
        Assert.Null(AppProfiles.Problem(list[0]));
        Assert.True(AppProfiles.FiltersSize(list) > AppProfiles.MaxFilterBytes);
        var text = AppProfiles.TotalProblem(list);
        Assert.NotNull(text);
        Assert.Contains("16 КБ", text);
        Assert.Null(AppProfiles.TotalProblem(list.Take(2)));

        // Из state.json в командную строку идут только те, что помещаются.
        var st = new AppState { GameMode = "profiles", AppProfiles = list };
        var o = BuildOptions.From(st, DateTime.UtcNow);
        Assert.InRange(o.Profiles!.Count, 1, AppProfiles.MaxProfiles - 1);
        Assert.True(AppProfiles.FiltersSize(o.Profiles) <= AppProfiles.MaxFilterBytes);

        using var t = new TempDir();
        var shell = new Shell(Make.Paths(t));
        shell.Data.Seed();
        shell.State.AppProfiles.AddRange(list.Take(AppProfiles.MaxProfiles - 1));
        var last = list[^1];
        var e = Assert.Throws<InvalidOperationException>(() =>
            shell.SetAppProfile(null, "Ещё", last.TcpPorts, last.UdpPorts, [], 0, new ProfileSignature(0, last.TcpPayloadHex, 0, last.UdpPayloadHex)));
        Assert.Contains("16 КБ", e.Message);
    }

    // ---------- переключатель режима ----------

    [Theory]
    [InlineData("off", GameMode.Off)]
    [InlineData("profiles", GameMode.Profiles)]
    [InlineData("all", GameMode.TcpUdp)]
    [InlineData("tcp", GameMode.Tcp)]    // прежние значения — части «Общего»
    [InlineData("udp", GameMode.Udp)]
    [InlineData("мусор", GameMode.Off)]
    [InlineData(null, GameMode.Off)]
    public void GameMode_Parse(string? text, GameMode expected)
    {
        Assert.Equal(expected, BuildOptions.ParseGame(text));
        Assert.Equal(expected is GameMode.Tcp or GameMode.Udp or GameMode.TcpUdp, expected.IsGeneral());
        if (text is "off" or "profiles" or "all" or "tcp" or "udp") Assert.Equal(text, BuildOptions.Format(expected));
    }

    [Fact]
    public void Profiles_WorkInProfilesAndGeneral_NotWhenOff_AndGeneralIsNotInProfiles()
    {
        var st = new AppState { IpsetMode = "list" };
        st.AppProfiles.Add(Sig());
        Assert.Null(BuildOptions.From(st, DateTime.UtcNow).Profiles);   // «Выкл»: профили хранятся, но не подаются
        st.GameMode = "profiles";
        var o = BuildOptions.From(st, DateTime.UtcNow);
        Assert.Single(o.Profiles!);
        Assert.False(o.GameTcp);   // широкий фильтр выключен
        Assert.False(o.GameUdp);
        st.GameMode = "all";
        o = BuildOptions.From(st, DateTime.UtcNow);
        Assert.Single(o.Profiles!);
        Assert.True(o.GameTcp && o.GameUdp);

        using var t = new TempDir();
        var s = Make.Strategy(("general", "--dpi-desync=fake"));
        var args = WinwsArgs.Build(s, Make.Paths(t), BuildOptions.From(new AppState { IpsetMode = "list", GameMode = "profiles", AppProfiles = [Sig()] }, DateTime.UtcNow));
        Assert.DoesNotContain(args, a => a.StartsWith("--filter-tcp=" + WinwsArgs.GamePorts));
        Assert.DoesNotContain(WinwsArgs.GamePorts, args[0]);
    }

    [Fact]
    public void OldState_WithProfilesAndOff_BecomesProfiles_ProfilesNotLost()
    {
        using var t = new TempDir();
        var paths = Make.Paths(t);
        paths.EnsureDirectories();
        var old = new AppState { GameMode = "off", AppProfiles = [Sig()] };
        File.WriteAllText(paths.StateFile, System.Text.Json.JsonSerializer.Serialize(old, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower }));
        var loaded = AppState.Load(paths);
        Assert.Equal("profiles", loaded.GameMode);
        Assert.Single(loaded.AppProfiles);
        loaded.GameMode = "off";   // выбрали «Выкл» уже после — больше не трогается
        loaded.Save(paths);
        Assert.Equal("off", AppState.Load(paths).GameMode);
        Assert.Single(AppState.Load(paths).AppProfiles);
        // Без профилей «off» остаётся «off»; старое «all» — «Общий».
        File.WriteAllText(paths.StateFile, """{"game_mode":"all"}""");
        Assert.Equal(GameMode.TcpUdp, BuildOptions.ParseGame(AppState.Load(paths).GameMode));
    }

    [Fact]
    public async Task Shell_ModeSwitch_ProfilesNeedNoList_GeneralDoes_AndBackIsAsBefore()
    {
        using var t = new TempDir();
        var shell = new Shell(Make.Paths(t));
        shell.Data.Seed();
        await shell.SetGameModeAsync(GameMode.Profiles);
        Assert.Equal("profiles", shell.State.GameMode);
        Assert.Equal("off", shell.State.IpsetMode);   // список не включался
        await shell.SetGameModeAsync(GameMode.TcpUdp);
        Assert.Equal("all", shell.State.GameMode);
        Assert.Equal("list", shell.State.IpsetMode);   // «Общему» нужен список
        Assert.True(shell.State.IpsetByGame);
        await shell.SetGameModeAsync(GameMode.Profiles);   // ушли из «Общего» — список, который включил он, выключился
        Assert.Equal("off", shell.State.IpsetMode);
        await shell.SetGameModeAsync(GameMode.Off);
        Assert.Equal("off", shell.State.GameMode);
    }

    [Fact]
    public async Task NetViewModel_ThreeWayChoice_ThroughHelper()
    {
        using var t = new TempDir();
        var shell = new Shell(Make.Paths(t));
        shell.Data.Seed();
        var f = new HelperFixture(backend: PcHelper.Over(shell));
        await using var _ = f;
        var link = await f.LinkAsync();
        var net = new NetViewModel(link) { Say = _ => { } };
        Assert.True(net.GameOff);
        Assert.False(net.GameProfiles);
        Assert.False(net.GameGeneral);

        await net.SetGameModeAsync(GameMode.Profiles);
        Assert.True(net.GameProfiles);
        Assert.False(net.GameOff);
        Assert.False(net.GameGeneral);
        Assert.True(net.IpsetOff);   // список не тронут
        Assert.StartsWith("Свои профили включатся", net.GameStatus);
        Assert.Contains("профиль", net.GamePitfalls);
        Assert.Contains("свой фильтр", net.GamePitfalls, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("торрент", net.GamePitfalls);

        await net.SetGameModeAsync(GameMode.TcpUdp);
        Assert.True(net.GameGeneral);
        Assert.True(net.IpsetList);
        Assert.Contains("торрент", net.GamePitfalls);
        Assert.Contains("500+", net.GamePitfalls);

        await net.SetGameModeAsync(GameMode.Off);
        Assert.True(net.GameOff);
        Assert.Contains("торрент", net.GamePitfalls);   // при «Выкл» видны оба списка
        Assert.Contains("свой фильтр", net.GamePitfalls, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GamesUrl_FollowsLanguage_AndIsOnlyHandedToOpener()
    {
        Assert.EndsWith("/docs/windows/games.md", NetViewModel.GamesUrl);
        using var t = new TempDir();
        var shell = new Shell(Make.Paths(t));
        var f = new HelperFixture(backend: PcHelper.Over(shell));
        await using var _ = f;
        var link = await f.LinkAsync();
        var opened = new List<string>();
        var net = new NetViewModel(link) { OpenUrl = opened.Add };   // настоящее открытие под тестом запрещено (UiGuard)
        net.OpenGamesCmd.Execute(null);
        var apps = new AppsViewModel(link) { OpenUrl = opened.Add };
        apps.OpenGamesCmd.Execute(null);
        Assert.Equal([NetViewModel.GamesUrl, NetViewModel.GamesUrl], opened);
        Assert.StartsWith("https://github.com/", NetViewModel.GamesUrl);
    }

    // ---------- импорт и экспорт (формат Flowseal) ----------

    static ProfileImport Import(string filter, string pars, string subnets = "")
    {
        var r = ProfileExchange.Import("Mistfall", filter, pars, subnets, Fakes);
        Assert.True(r.Profile is not null, r.Problem);
        return r.Profile!;
    }

    static string Refused(string filter, string pars, string subnets = "")
    {
        var r = ProfileExchange.Import("x", filter, pars, subnets, Fakes);
        Assert.Null(r.Profile);
        return r.Problem!;
    }

    /// <summary>winws на any-protocol без cutoff предупреждает «TRASH FLOOD»: без сигнатуры профиль ловит все пакеты своих портов.</summary>
    [Fact]
    public void Import_NoSignature_AnyProtocol_GetsCutoff()
    {
        var p = Import("", "--filter-udp=20001-21000 --dpi-desync=fake --dpi-desync-any-protocol=1 --dpi-desync-repeats=12", "3.120.1.0/24");
        Assert.EndsWith("--dpi-desync-cutoff=n2", p.UdpDesync);
        Assert.Contains(p.Notes, n => n.Contains("cutoff"));
        // Свой cutoff не трогается; с сигнатурой — не добавляется.
        Assert.Contains("--dpi-desync-cutoff=n4", Import("", "--filter-udp=20001-21000 --dpi-desync=fake --dpi-desync-any-protocol=1 --dpi-desync-cutoff=n4", "3.120.1.0/24").UdpDesync);
        Assert.DoesNotContain("cutoff", Import(Mistfall, "--filter-tcp=12001 --dpi-desync=fake --dpi-desync-any-protocol=1").TcpDesync);
    }

    [Fact]
    public void Import_Mistfall_Whole()
    {
        var p = Import(Mistfall, MistfallParams, "203.0.113.0/24\n198.51.100.7\n");
        Assert.Equal("12001", p.TcpPorts);
        Assert.Equal("", p.UdpPorts);
        Assert.Equal(79, p.Signature.TcpLength);
        Assert.Equal("0000044d", p.Signature.TcpHex);
        Assert.Equal(MistfallDesync, p.TcpDesync);
        Assert.Equal(["198.51.100.7/32", "203.0.113.0/24"], p.Addresses.Order(StringComparer.Ordinal));
        Assert.Contains(p.Notes, n => n.Contains("--wf-tcp") && n.Contains("--ipset"));   // отброшено и сказано
        Assert.Equal(MistfallDesync, p.ToSignature().TcpDesync);
    }

    [Fact]
    public void Import_Mistfall_ExportsBackEquivalent()
    {
        var p = Import(Mistfall, MistfallParams, "203.0.113.0/24");
        var profile = new AppProfile
        {
            Id = "p0a1b2c3d", Title = p.Title, TcpPorts = p.TcpPorts, TcpDesync = p.TcpDesync, Addresses = [.. p.Addresses],
            TcpPayloadLength = p.Signature.TcpLength, TcpPayloadHex = p.Signature.TcpHex,
        };
        Assert.Null(AppProfiles.Problem(profile));
        var text = ProfileExchange.Export(profile);
        Assert.Equal(Mistfall, text.Filter);
        Assert.Equal("--filter-tcp=12001 " + MistfallDesync.Replace("{fake}/", "%BIN%"), text.Params);
        Assert.Equal("203.0.113.0/24", text.Subnets);
        // Тот же текст снова импортируется в тот же профиль, и через буфер (одним куском) — тоже.
        var again = Import(text.Filter, text.Params, text.Subnets);
        Assert.Equal(p with { Notes = [] }, again with { Notes = [] }, new ImportComparer());
        var combined = ProfileExchange.Combine(text);
        Assert.Equal(text, ProfileExchange.SplitCombined(combined));
        Assert.Null(ProfileExchange.SplitCombined("просто текст"));
    }

    sealed class ImportComparer : IEqualityComparer<ProfileImport>
    {
        public bool Equals(ProfileImport? a, ProfileImport? b) =>
            a!.Title == b!.Title && a.TcpPorts == b.TcpPorts && a.UdpPorts == b.UdpPorts && a.TcpDesync == b.TcpDesync && a.UdpDesync == b.UdpDesync
            && a.Signature == b.Signature && a.Addresses.SequenceEqual(b.Addresses);
        public int GetHashCode(ProfileImport p) => p.Title.GetHashCode();
    }

    [Fact]
    public void Import_TcpAndUdpGroups_AndRangeFilter()
    {
        var pars = "--filter-tcp=12001 --dpi-desync=fake --dpi-desync-any-protocol=1 --new "
            + "--filter-udp=20001-21000 --ipset=\"%LISTS%aws.txt\" --dpi-desync=fake --dpi-desync-repeats=12 --dpi-desync-ttl=11 --dpi-desync-cutoff=n4 --dpi-desync-any-protocol=1";
        var filter = Mistfall + "\noutbound and udp.DstPort >= 20001 and udp.DstPort <= 21000 and udp.PayloadLength = 20\n# комментарий";
        var p = Import(filter, pars);
        Assert.Equal("20001-21000", p.UdpPorts);
        Assert.Equal(20, p.Signature.UdpLength);
        Assert.Equal("", p.Signature.UdpHex);
        Assert.Contains("--dpi-desync-ttl=11", p.UdpDesync);
        Assert.Empty(p.Addresses);   // подсетей нет, у обоих протоколов сигнатура
    }

    [Fact]
    public void Import_ParamsWithoutProtocol_Refused()
    {
        Assert.Contains("TCP", Refused("", "--dpi-desync=fake --dpi-desync-any-protocol=1"));
        Assert.Contains("Нет ни фильтра, ни параметров", Refused("", ""));
    }

    [Fact]
    public void Import_ParamsWithoutFilter_NeedAddresses()
    {
        var pars = "--filter-tcp=443 --dpi-desync=fake --dpi-desync-any-protocol=1";
        Assert.NotNull(Refused("", pars));   // без сигнатуры адреса обязательны
        var p = Import("", pars, "203.0.113.0/24");
        Assert.Equal("443", p.TcpPorts);
        Assert.Equal(0, p.Signature.TcpLength);
    }

    [Theory]
    [InlineData("true", "протокол")]
    [InlineData("outbound and tcp.DstPort = 12001 or udp.DstPort = 5", "протокол")]
    [InlineData("tcp.DstPort = 12001 and tcp.PayloadLength = 5", "outbound")]
    [InlineData("outbound and tcp.PayloadLength = 5", "DstPort")]
    [InlineData("outbound and tcp.DstPort = 12001 and udp.DstPort = 5", "протокол")]
    [InlineData("outbound and tcp.DstPort = 12001; calc", "calc")]
    [InlineData("outbound and tcp.DstPort = 12001 and tcp.Syn = 1", "tcp.Syn")]
    [InlineData("outbound and tcp.DstPort = 70000", "порт")]
    [InlineData("outbound and not tcp.DstPort = 1", "DstPort")]
    public void Import_FilterOutsideGrammar_RefusedWithReason(string filter, string part)
    {
        var msg = Refused(filter, "--filter-tcp=12001 --dpi-desync=fake --dpi-desync-any-protocol=1");
        Assert.Contains(part, msg, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Не раскладывается на простые поля, но проходит проверку своего фильтра — принимается как свой, с пометкой.</summary>
    [Theory]
    [InlineData("outbound and tcp.DstPort = 12001 and ip.Length > 4")]
    [InlineData("outbound and tcp.DstPort = 12001 and tcp.Payload[0] = 1 and tcp.Payload[2] = 3")]
    [InlineData("outbound and tcp.DstPort = 12001 and tcp.Payload32[4] = 1")]
    [InlineData("outbound and tcp.DstPort = 12001 and tcp.PayloadLength = 5000")]
    public void Import_FilterOutsideSimpleFields_AcceptedAsCustom(string filter)
    {
        var p = Import(filter, "--filter-tcp=12001 --dpi-desync=fake --dpi-desync-any-protocol=1");
        Assert.Equal(filter, p.Signature.TcpFilter);
        Assert.Contains(p.Notes, n => n.Contains("ответственность"));
    }

    [Theory]
    [InlineData("--filter-tcp=12001 --dpi-desync=fake --debug", "--debug")]
    [InlineData("--filter-tcp=12001 --dpi-desync=fake --dpi-desync-fake-tls=\"%BIN%чужой.bin\"", "чужой")]
    [InlineData("--filter-tcp=12001 --dpi-desync=fake --dpi-desync-fake-tls=\"%BIN%evil.bin\"", "evil.bin")]   // нет в наборе
    [InlineData("--filter-tcp=%GameFilterTCP% --dpi-desync=fake", "%")]
    [InlineData("--filter-tcp=12001 --dpi-desync=fake --dpi-desync-fake-tls=C:\\Windows\\system32\\x.dll", "")]
    [InlineData("--filter-tcp=12001 --dpi-desync-repeats=8", "")]   // без десинхронизации — это не профиль
    [InlineData("--filter-tcp=12001 --dpi-desync=fake --filter-udp=5", "одной группе")]
    [InlineData("--filter-tcp=12001 stray --dpi-desync=fake", "stray")]
    [InlineData("--filter-tcp=12001 --dpi-desync=fake & calc", "&")]
    public void Import_ParamsOutsideWhitelist_Refused(string pars, string part)
    {
        var msg = Refused(Mistfall, pars);
        Assert.Contains(part, msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Import_Subnets_UseIpListValidator_AndLimit()
    {
        var pars = "--filter-tcp=12001 --dpi-desync=fake --dpi-desync-any-protocol=1";
        Assert.Contains("10.0.0.0/8", Refused(Mistfall, pars, "10.0.0.0/8"), StringComparison.Ordinal);   // служебная сеть
        Assert.NotNull(Refused(Mistfall, pars, "не адрес"));
        Assert.NotNull(Refused(Mistfall, pars, "8.0.0.0/8"));   // слишком широкая
        var many = string.Join('\n', Enumerable.Range(0, AppProfiles.MaxAddresses + 1).Select(i => $"203.{i / 250}.{i % 250}.0/24"));
        Assert.Contains("500", Refused(Mistfall, pars, many));
    }

    [Fact]
    public void Import_FromWholeGeneralBatLine_SkipsShellWrapper()
    {
        var line = "start \"zapret: %~n0\" /min \"%BIN%winws.exe\" --wf-tcp=80,443,12001 --wf-udp=443 ^\r\n--filter-tcp=12001 --dpi-desync=fake --dpi-desync-any-protocol=1 --dpi-desync-repeats=8";
        var p = Import(Mistfall, line);
        Assert.Equal("--dpi-desync=fake --dpi-desync-any-protocol=1 --dpi-desync-repeats=8", p.TcpDesync);
    }

    // ---------- свой фильтр WinDivert ----------

    [Theory]
    [InlineData(Mistfall)]
    [InlineData("outbound and tcp.DstPort >= 1000 and tcp.DstPort <= 2000 and (tcp.PayloadLength = 10 or tcp.PayloadLength = 20) and ip.DstAddr = 203.0.113.5")]
    [InlineData("outbound and (tcp.DstPort = 80 or tcp.DstPort = 443) and tcp.Payload16[0] = 0x1603 and not tcp.Payload[5] = 0xff")]
    [InlineData("OUTBOUND AND TCP.DSTPORT = 443")]
    public void CustomFilter_Accepted(string filter) => Assert.Null(CustomFilter.Problem(filter, true));

    [Theory]
    [InlineData("true", "true")]
    [InlineData("tcp", "tcp")]
    [InlineData("outbound and tcp", "tcp")]
    [InlineData("outbound", "TCP")]
    [InlineData("outbound and tcp.PayloadLength = 5", "DstPort")]
    [InlineData("tcp.DstPort = 443", "outbound")]
    [InlineData("outbound and tcp.DstPort = 443 or outbound", "верхнем")]
    [InlineData("outbound and tcp.DstPort != 443", "DstPort")]
    [InlineData("outbound and not tcp.DstPort = 443", "DstPort")]
    [InlineData("outbound and tcp.DstPort >= 0", "порту")]
    [InlineData("outbound and tcp.DstPort < 65536", "порту")]
    [InlineData("outbound and tcp.DstPort = 443 and tcp.Foo = 1", "tcp.Foo")]
    [InlineData("outbound and tcp.DstPort = 443 and ip.TTL = 1", "ip.TTL")]
    [InlineData("outbound and tcp.DstPort = 443 and ifIdx = 1", "ifIdx")]
    [InlineData("outbound and tcp.DstPort = 443 and udp.DstPort = 5", "TCP")]
    [InlineData("outbound and tcp.DstPort = 443; calc", ";")]
    [InlineData("outbound and tcp.DstPort = 443 and \"x\"", "\"")]
    [InlineData("outbound and tcp.DstPort = 443\nand tcp.PayloadLength = 1", "перевод")]
    [InlineData("outbound and tcp.DstPort = 443\t", "управляющий")]
    [InlineData("outbound and (tcp.DstPort = 443", "скобка")]
    [InlineData("outbound and tcp.DstPort =", "после сравнения")]
    [InlineData("outbound and tcp.DstPort = 99999999999", "9999999999")]
    [InlineData("outbound and tcp.DstPort 443", "сравнение")]
    [InlineData("", "пустой")]
    public void CustomFilter_Refused(string filter, string part) =>
        Assert.Contains(part, CustomFilter.Problem(filter, true) ?? "ПРИНЯТ", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void CustomFilter_LengthLimit_4Kb()
    {
        var ok = "outbound and tcp.DstPort = 443" + string.Concat(Enumerable.Repeat(" and tcp.PayloadLength = 1", 100));
        Assert.Null(CustomFilter.Problem(ok, true));
        var big = ok + string.Concat(Enumerable.Repeat(" and tcp.PayloadLength = 1", 200));
        Assert.True(big.Length > CustomFilter.MaxLength);
        Assert.Contains("4 КБ", CustomFilter.Problem(big, true));
        Assert.Equal(true, CustomFilter.ProtocolOf(Mistfall));
        Assert.Null(CustomFilter.ProtocolOf("outbound and tcp.DstPort = 1 and udp.DstPort = 2"));
    }

    [Fact]
    public void CustomFilter_ReplacesSimpleFields_InProfileAndBuild()
    {
        var custom = "outbound and tcp.DstPort = 12001 and (tcp.PayloadLength = 79 or tcp.PayloadLength = 80)";
        var p = Sig(x => { x.TcpPayloadLength = 5; x.TcpPayloadHex = "ff"; x.TcpCustomFilter = custom; });
        Assert.Null(AppProfiles.Problem(p));
        Assert.Equal(custom, AppProfiles.Filter(p, true));   // простые поля заменены
        Assert.NotNull(AppProfiles.Problem(Sig(x => x.TcpCustomFilter = "true")));
        Assert.NotNull(AppProfiles.Problem(Sig(x => x.UdpCustomFilter = Mistfall)));   // у профиля нет UDP
        Assert.NotNull(AppProfiles.Problem(Sig(x => x.TcpCustomFilter = Mistfall.Replace("tcp.", "udp."))));
        using var t = new TempDir();
        var args = WinwsArgs.Build(Make.Strategy(("general", "--dpi-desync=fake")), Make.Paths(t), new BuildOptions(IpsetMode.Off, GameMode.Profiles, [p]));
        Assert.Equal($"--wf-tcp={WinwsArgs.WfTcp}", args[0]);
        Assert.Contains(args, a => a.StartsWith("--wf-raw-part=@") && a.EndsWith("app-p0a1b2c3d-tcp.filter.txt"));
        // Экспорт отдаёт свой фильтр как есть.
        Assert.Equal(custom, ProfileExchange.Export(p).Filter);
    }

    [Fact]
    public void Import_UnparsableFilter_BecomesCustomFilter_WithNote()
    {
        var custom = "outbound and tcp.DstPort = 12001 and (tcp.PayloadLength = 79 or tcp.PayloadLength = 80)";
        var pars = "--filter-tcp=12001 --dpi-desync=fake --dpi-desync-any-protocol=1";
        var p = Import(custom, pars, "");
        Assert.Equal(custom, p.ToSignature().TcpFilter);
        Assert.Equal("12001", p.TcpPorts);
        Assert.Contains(p.Notes, n => n.Contains("ответственность"));   // предупреждение в итоге импорта
        // Свой фильтр проходит свою проверку.
        Assert.Contains("верхнем", Refused("outbound and tcp.PayloadLength = 5 or tcp.DstPort = 1", pars, ""));
        Assert.Contains("outbound", Refused("tcp.DstPort = 1 and (tcp.PayloadLength = 5 or tcp.PayloadLength = 6)", pars, ""));
        // Простой фильтр остаётся простым, без пометки.
        var simple = Import(Mistfall, pars, "");
        Assert.Equal("", simple.ToSignature().TcpFilter);
        Assert.DoesNotContain(simple.Notes, n => n.Contains("ответственность"));
    }

    // ---------- протокол помощника ----------

    static readonly FakeHelperBackend Catalog = new();
    static string Rejected(string json) => Assert.Throws<HelperRequestException>(() => HelperCommands.Parse(json, Catalog)).Message;

    [Fact]
    public void Protocol_1_22_ArgsValidated()
    {
        var ok = HelperCommands.Parse("""{"id":1,"cmd":"app_profile_set","args":{"title":"M","tcp":"12001","variant":0,"tcpLen":79,"tcpHex":"0000044d","tcpDesync":"--dpi-desync=fake","tcpFilter":"outbound and tcp.DstPort = 1"}}""", Catalog);
        Assert.Equal(79, ok.Int("tcpLen"));
        Assert.Equal("0000044d", ok.Str("tcpHex"));
        Assert.Null(ok.List("addresses"));   // адреса необязательны (для протокола с сигнатурой)
        Assert.Equal("profiles", HelperCommands.Parse("""{"id":2,"cmd":"game_mode","args":{"mode":"profiles"}}""", Catalog).Str("mode"));
        Assert.Contains("hex", Rejected("""{"id":1,"cmd":"app_profile_set","args":{"title":"M","tcp":"1","variant":0,"tcpHex":"0000044D"}}"""), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hex", Rejected("""{"id":1,"cmd":"app_profile_set","args":{"title":"M","tcp":"1","variant":0,"udpHex":"000000000000000000000000000000000000"}}"""), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("целое число", Rejected("""{"id":1,"cmd":"app_profile_set","args":{"title":"M","tcp":"1","variant":0,"tcpLen":1501}}"""));
        Assert.Contains("управляющ", Rejected("""{"id":1,"cmd":"app_profile_set","args":{"title":"M","tcp":"1","variant":0,"tcpFilter":"outbound\nand tcp.DstPort = 1"}}"""));
        Assert.Contains("управляющ", Rejected("""{"id":1,"cmd":"app_profile_set","args":{"title":"M","tcp":"1","variant":0,"tcpDesync":"--a\n--b"}}"""));
        Assert.Contains("лишнее поле", Rejected("""{"id":1,"cmd":"app_profile_set","args":{"title":"M","tcp":"1","variant":0,"wfFilter":"true"}}"""));
        Assert.NotEmpty(Rejected("""{"id":1,"cmd":"game_mode","args":{"mode":"tcp"}}"""));   // tcp/udp окно не шлёт
        Assert.Equal(HelperProtocol.ProfilesApi, HelperProtocol.Api);
    }

    [Fact]
    public async Task Channel_SignatureProfile_RoundTrip_AndOldHelperRefused()
    {
        using var t = new TempDir();
        var shell = new Shell(Make.Paths(t));
        shell.Data.Seed();
        var f = new HelperFixture(backend: PcHelper.Over(shell));
        await using var _ = f;
        var link = await f.LinkAsync();
        var c = link.Client!;
        var imp = Import(Mistfall, MistfallParams, "");
        var info = await c.AppProfileSetAsync(null, imp.Title, imp.TcpPorts, imp.UdpPorts, imp.Addresses, 0, imp.ToSignature());
        Assert.Equal(79, info.TcpPayloadLength);
        Assert.Equal("0000044d", info.TcpPayloadHex);
        Assert.Equal(MistfallDesync, info.TcpDesync);
        Assert.Equal(-1, info.Variant);   // свои параметры, не вариант каталога
        Assert.Equal("profiles", shell.State.GameMode);
        Assert.Equal(Mistfall + "\n", File.ReadAllText(Path.Combine(shell.Paths.Lists, AppProfiles.FilterFileName(info.Id, true))));
        var listed = Assert.Single((await c.AppProfilesAsync()).Profiles);
        Assert.Equal("0000044d", listed.TcpPayloadHex);

    }

    [Fact]
    public async Task Channel_BadFilterOrParams_Rejected_ByHelper()
    {
        using var t = new TempDir();
        var shell = new Shell(Make.Paths(t));
        shell.Data.Seed();
        var f = new HelperFixture(backend: PcHelper.Over(shell));
        await using var _ = f;
        var c = (await f.LinkAsync()).Client!;
        await Assert.ThrowsAsync<HelperException>(() => c.AppProfileSetAsync(null, "x", "443", null, [], 0, new ProfileSignature(TcpFilter: "true")));
        await Assert.ThrowsAsync<HelperException>(() => c.AppProfileSetAsync(null, "x", "443", null, [], 0, new ProfileSignature(TcpFilter: "outbound and tcp.PayloadLength = 1")));
        await Assert.ThrowsAsync<HelperException>(() => c.AppProfileSetAsync(null, "x", "443", null, [], 0, new ProfileSignature(TcpDesync: "--debug --dpi-desync=fake", TcpHex: "00")));
        await Assert.ThrowsAsync<HelperException>(() => c.AppProfileSetAsync(null, "x", "443", null, [], 0, new ProfileSignature(TcpDesync: "--dpi-desync=fake --dpi-desync-fake-tls=C:/x.bin", TcpHex: "00")));
        Assert.Empty(shell.State.AppProfiles);
        var ok = await c.AppProfileSetAsync(null, "x", "12001", null, [], 0, new ProfileSignature(TcpFilter: "outbound and tcp.DstPort = 12001 and tcp.PayloadLength = 100"));
        Assert.Equal("outbound and tcp.DstPort = 12001 and tcp.PayloadLength = 100", ok.TcpFilter);
        // Без адресов на порту сайтов (443) — отказ: профиль ловил бы весь HTTPS.
        await Assert.ThrowsAsync<HelperException>(() => c.AppProfileSetAsync(null, "y", "443", null, [], 0, new ProfileSignature(TcpFilter: "outbound and tcp.DstPort = 443 and tcp.PayloadLength = 100")));
    }
}
