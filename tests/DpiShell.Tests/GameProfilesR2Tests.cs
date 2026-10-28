using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.Tests;

/// <summary>
/// Профили включаются по отдельности, «Общий» — отдельно, выбор стратегии профиля («По умолчанию» / стратегия / «Свои») и «Разные стратегии»,
/// миграция прежнего режима без потерь, командная строка winws, строка обмена <c>dpishell-game:1:</c>, команды помощника 1.22 и совместимость.
/// </summary>
public sealed class GameProfilesR2Tests
{
    const string Own = "--dpi-desync=fake --dpi-desync-any-protocol=1 --dpi-desync-fooling=ts --dpi-desync-repeats=8";
    /// <summary>Строка профиля Mistfall Hunter, собранная независимо (Python zlib, raw deflate): формат версии 1 читается не только своим кодировщиком.</summary>
    const string MistfallCode = "dpishell-game:1:VcrBCsIwEATQXyl7biCRghrI3YsfEZKtBpdsSLZgKf13I17s3ObNbCBJCMHCPTWZPdFwW7JghREkFLAbFK7S-sGctDadCTPY83WEJ74762-mKfYlYltz6KZULEn9qpv9C4d_UT6vqlQWDkzOHLeZmVJ-OGlHr1jQS3MX2PcP";

    static AppProfile Mistfall(Action<AppProfile>? tweak = null)
    {
        var p = new AppProfile { Id = "p0a1b2c3d", Title = "Mistfall Hunter", TcpPorts = "12001", TcpPayloadLength = 79, TcpPayloadHex = "0000044d", TcpDesync = Own, Strategy = AppProfiles.Own };
        tweak?.Invoke(p);
        return p;
    }

    static AppProfile Voice(Action<AppProfile>? tweak = null)
    {
        var p = new AppProfile { Id = "p0a1b2c3e", Title = "Voice", UdpPorts = "50000-50100", Addresses = ["203.0.113.0/24"], Strategy = AppProfiles.Default };
        tweak?.Invoke(p);
        return p;
    }

    static readonly JsonSerializerOptions Snake = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    // ---------- миграция состояния ----------

    [Theory]
    // schema, режим → «Общий», профили включены
    [InlineData(1, "profiles", "off", true)]
    [InlineData(1, "all", "all", true)]
    [InlineData(1, "tcp", "tcp", true)]
    [InlineData(1, "off", "off", false)]   // «Выкл» с профилями: они не действовали — выключены
    [InlineData(0, "off", "off", true)]    // до режима «Свои профили» профили действовали всегда
    [InlineData(0, "all", "all", true)]
    public void Migration_OldMode_ToPerProfileSwitches_NothingLost(int schema, string mode, string general, bool on)
    {
        using var t = new TempDir();
        var paths = Make.Paths(t);
        paths.EnsureDirectories();
        var json = JsonNode.Parse(JsonSerializer.Serialize(new AppState { AppProfiles = [Mistfall(), Voice()] }, Snake))!.AsObject();
        json["game_mode"] = mode;
        json["game_mode_schema"] = schema;
        foreach (var p in json["app_profiles"]!.AsArray()) { p!.AsObject().Remove("enabled"); p.AsObject().Remove("strategy"); }   // записи до 1.22
        File.WriteAllText(paths.StateFile, json.ToJsonString());
        var st = AppState.Load(paths);
        Assert.Equal(general, st.GameMode);
        Assert.Equal(2, st.AppProfiles.Count);
        Assert.All(st.AppProfiles, p => Assert.Equal(on, p.Enabled));
        Assert.Equal(Own, st.AppProfiles[0].TcpDesync);   // параметры, сигнатура, адреса — на месте
        Assert.Equal(79, st.AppProfiles[0].TcpPayloadLength);
        Assert.Equal(["203.0.113.0/24"], st.AppProfiles[1].Addresses);
        // Записанное заново больше не мигрирует: выключенный рукой профиль остаётся выключенным.
        st.AppProfiles[0].Enabled = false;
        st.AppProfiles[1].Enabled = true;
        st.Save(paths);
        var again = AppState.Load(paths);
        Assert.False(again.AppProfiles[0].Enabled);
        Assert.True(again.AppProfiles[1].Enabled);
        Assert.Equal(AppState.CurrentGameSchema, again.GameModeSchema);
    }

    [Fact]
    public void Migration_Variants_ToChoices_OwnKept_SeparateOnWhenNeeded()
    {
        using var t = new TempDir();
        var paths = Make.Paths(t);
        var shell0 = new Shell(paths);
        var all = shell0.AllStrategies;
        var current = all[0];
        var other = all.First(s => AppProfiles.GameParts(s) != AppProfiles.GameParts(current) && AppProfiles.GameParts(s).Tcp is not null);
        var (oTcp, oUdp) = AppProfiles.GameParts(other);
        paths.EnsureDirectories();
        var st = new AppState
        {
            StrategyId = current.Id,
            AppProfiles =
            [
                Mistfall(p => { p.Strategy = null; p.Variant = -1; }),
                Voice(p => { p.Id = "p00000001"; p.Strategy = null; p.Variant = 0; p.UdpDesync = AppProfiles.GameParts(current).Udp; }),
                Voice(p => { p.Id = "p00000002"; p.Strategy = null; p.Variant = 3; p.UdpDesync = oUdp; p.TcpPorts = "27015"; p.TcpDesync = oTcp!; }),
                Voice(p => { p.Id = "p00000003"; p.Strategy = null; p.Variant = 2; p.UdpDesync = "--dpi-desync=fake --dpi-desync-any-protocol=1 --dpi-desync-repeats=3"; }),
            ],
        };
        File.WriteAllText(paths.StateFile, JsonSerializer.Serialize(st, Snake).Replace("\"strategy\":null,", ""));
        var shell = new Shell(paths);
        var ps = shell.State.AppProfiles;
        Assert.Equal(AppProfiles.Own, ps[0].Strategy);
        Assert.Equal(Own, ps[0].TcpDesync);
        Assert.Equal(AppProfiles.Default, ps[1].Strategy);
        Assert.Equal("", ps[1].UdpDesync);   // «как у текущей» — это и есть «По умолчанию»
        Assert.Equal((oTcp, oUdp), AppProfiles.GameParts(shell.FindStrategy(ps[2].Strategy!)!));   // стратегия с теми же параметрами
        Assert.Equal("", ps[2].TcpDesync);
        Assert.Equal(AppProfiles.Own, ps[3].Strategy);   // не нашлась стратегия с такими параметрами — остались «Своими», не потерялись
        Assert.Equal("--dpi-desync=fake --dpi-desync-any-protocol=1 --dpi-desync-repeats=3", ps[3].UdpDesync);
        Assert.True(shell.State.ProfilesSeparate);   // выбор не «По умолчанию» — чтобы всё шло как раньше
    }

    // ---------- командная строка winws ----------

    static List<string> Build(Strategy s, AppState st, TempDir t, Func<string, Strategy?>? find = null) =>
        [.. WinwsArgs.Build(s, Make.Paths(t), BuildOptions.From(st, DateTime.UtcNow, find))];

    [Fact]
    public void Build_DisabledProfile_NotInCommandLine()
    {
        using var t = new TempDir();
        var s = Make.Strategy(("general", "--dpi-desync=fake"));
        var st = new AppState { AppProfiles = [Mistfall(), Voice(p => p.Enabled = false)] };
        var args = Build(s, st, t);
        Assert.Contains("--filter-tcp=12001", args);
        Assert.DoesNotContain("--filter-udp=50000-50100", args);
        Assert.DoesNotContain(args, a => a.Contains("app-p0a1b2c3e"));
        Assert.DoesNotContain("50000-50100", args[1].Replace(WinwsArgs.WfUdp, ""));   // и в фильтр WinDivert его порты не добавлены
    }

    [Fact]
    public void Build_Default_TakesGamePartOfPcStrategy_AndFollowsIt()
    {
        using var t = new TempDir();
        var st = new AppState { AppProfiles = [Voice(p => { p.TcpPorts = "27015"; })] };
        var a = Make.Strategy(("general", "--dpi-desync=multisplit"));
        var b = Make.Strategy(("general", "--dpi-desync=fake"), ("game_udp", "--dpi-desync=fake --dpi-desync-any-protocol=1 --dpi-desync-repeats=6"));
        var argsA = Build(a, st, t);
        var tcp = argsA.IndexOf("--filter-tcp=27015");
        Assert.True(tcp >= 0);
        Assert.Contains("--dpi-desync=multisplit", argsA.Skip(tcp).TakeWhile(x => x != "--new"));
        Assert.Contains("--dpi-desync-cutoff=n3", argsA.Skip(tcp).TakeWhile(x => x != "--new"));   // выведено из general, как у «Общего»
        var udp = argsA.IndexOf("--filter-udp=50000-50100");
        Assert.Contains("--dpi-desync-repeats=12", argsA.Skip(udp).TakeWhile(x => x != "--new"));   // GameUdpDefault
        // Другая стратегия ПК — другая игровая часть у «По умолчанию».
        var argsB = Build(b, st, t);
        udp = argsB.IndexOf("--filter-udp=50000-50100");
        Assert.Contains("--dpi-desync-repeats=6", argsB.Skip(udp).TakeWhile(x => x != "--new"));
        Assert.Contains("--dpi-desync=fake", argsB.Skip(argsB.IndexOf("--filter-tcp=27015")).TakeWhile(x => x != "--new"));
    }

    [Fact]
    public void Build_Own_UsesOwnParams_OnlyWithSeparate_StrategyChoice_UsesThatStrategy()
    {
        using var t = new TempDir();
        var pc = Make.Strategy(("general", "--dpi-desync=multisplit"));
        var x = new Strategy("xstrat", "X", "catalog", new() { ["general"] = "--dpi-desync=fakedsplit", ["game_udp"] = "--dpi-desync=fake --dpi-desync-any-protocol=1 --dpi-desync-repeats=4" });
        Strategy? Find(string id) => id == "xstrat" ? x : null;
        var st = new AppState { AppProfiles = [Mistfall(), Voice(p => p.Strategy = "xstrat")] };
        // «Разные стратегии» выключены — все «По умолчанию», выбор хранится.
        var args = Build(pc, st, t, Find);
        var mist = args.Skip(args.IndexOf("--filter-tcp=12001")).TakeWhile(a => a != "--new").ToList();
        Assert.DoesNotContain("--dpi-desync-fooling=ts", mist);
        Assert.Contains("--dpi-desync=multisplit", mist);
        Assert.DoesNotContain("--dpi-desync-repeats=4", args);
        // Включены — «Свои» идут своими параметрами, выбранная стратегия — своей игровой частью.
        st.ProfilesSeparate = true;
        args = Build(pc, st, t, Find);
        mist = args.Skip(args.IndexOf("--filter-tcp=12001")).TakeWhile(a => a != "--new").ToList();
        Assert.Contains("--dpi-desync-fooling=ts", mist);
        Assert.Contains("--dpi-desync-repeats=8", mist);
        Assert.Contains("--dpi-desync-repeats=4", args.Skip(args.IndexOf("--filter-udp=50000-50100")).TakeWhile(a => a != "--new"));
        // Стратегии из выбора больше нет — «По умолчанию».
        args = Build(pc, st, t, _ => null);
        Assert.Contains("--dpi-desync-repeats=12", args.Skip(args.IndexOf("--filter-udp=50000-50100")).TakeWhile(a => a != "--new"));
    }

    [Fact]
    public void Build_GeneralWithProfiles_ProfilesFirst()
    {
        using var t = new TempDir();
        var s = Make.Strategy(("general", "--dpi-desync=fake"));
        var st = new AppState { IpsetMode = "list", GameMode = "all", AppProfiles = [Mistfall(), Voice()] };
        var args = Build(s, st, t);
        int profile = Math.Max(args.IndexOf("--filter-tcp=12001"), args.IndexOf("--filter-udp=50000-50100"));
        int gameTcp = args.IndexOf("--filter-tcp=" + WinwsArgs.GamePorts), gameUdp = args.IndexOf("--filter-udp=" + WinwsArgs.GamePorts);
        Assert.True(gameTcp > 0 && gameUdp > 0, "«Общий» не включился");
        Assert.True(profile < gameTcp && profile < gameUdp, "профили не первыми");
        Assert.True(args.IndexOf("--filter-tcp=12001") < args.IndexOf("--filter-tcp=80,443"));
    }

    // ---------- строка обмена ----------

    [Fact]
    public void Code_RoundTrip_WithoutChoiceAndSwitch()
    {
        var p = Mistfall(x => { x.Enabled = false; x.Strategy = "xstrat"; x.TcpCustomFilter = ""; x.Addresses = ["203.0.113.0/24"]; x.UdpPorts = "50000"; x.UdpDesync = ""; });
        var code = ProfileCode.Encode(p);
        Assert.StartsWith(ProfileCode.Prefix, code);
        var json = Unpack(code);
        Assert.Equal(["title", "tcp", "udp", "addresses"], json.Select(kv => kv.Key));
        Assert.Equal(["ports", "len", "hex", "desync"], json["tcp"]!.AsObject().Select(kv => kv.Key));
        Assert.Equal(["ports"], json["udp"]!.AsObject().Select(kv => kv.Key));   // своих параметров у UDP нет — их нет и в строке
        Assert.DoesNotContain("enabled", code);
        var back = ProfileCode.Decode(code, _ => true);
        Assert.Equal((p.Title, p.TcpPorts, p.UdpPorts, p.TcpPayloadLength, p.TcpPayloadHex, p.TcpDesync, p.UdpDesync), (back.Title, back.TcpPorts, back.UdpPorts, back.TcpPayloadLength, back.TcpPayloadHex, back.TcpDesync, back.UdpDesync));
        Assert.Equal(p.Addresses, back.Addresses);
        Assert.True(back.Enabled);                       // «вкл» — настройка этого ПК
        Assert.Equal(AppProfiles.Own, back.Strategy);    // свои параметры пришли — «Свои»
        Assert.Equal(AppProfiles.Default, ProfileCode.Decode(ProfileCode.Encode(Voice()), _ => true).Strategy);
    }

    [Fact]
    public void Code_IndependentlyBuiltLine_Decodes()
    {
        var p = ProfileCode.Decode(MistfallCode, _ => true);
        Assert.Equal("Mistfall Hunter", p.Title);
        Assert.Equal("12001", p.TcpPorts);
        Assert.Equal(79, p.TcpPayloadLength);
        Assert.Equal(Own, p.TcpDesync);
        Assert.Equal("outbound and tcp.DstPort = 12001 and tcp.PayloadLength = 79 and tcp.Payload32[0] = 0x0000044d", AppProfiles.Filter(p, true));
    }

    static JsonObject Unpack(string code)
    {
        var b64 = code[ProfileCode.Prefix.Length..].Replace('-', '+').Replace('_', '/');
        var packed = Convert.FromBase64String(b64 + new string('=', (4 - b64.Length % 4) % 4));
        using var d = new DeflateStream(new MemoryStream(packed), CompressionMode.Decompress);
        using var r = new StreamReader(d, Encoding.UTF8);
        return JsonNode.Parse(r.ReadToEnd())!.AsObject();
    }

    static string Pack(string json)
    {
        using var ms = new MemoryStream();
        using (var d = new DeflateStream(ms, CompressionLevel.SmallestSize, leaveOpen: true)) d.Write(Encoding.UTF8.GetBytes(json));
        return ProfileCode.Prefix + Convert.ToBase64String(ms.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    [Theory]
    [InlineData("""{"title":"x","tcp":{"ports":"12001","len":79},"enabled":true}""", "enabled")]                       // чужое поле
    [InlineData("""{"title":"x","tcp":{"ports":"12001","len":79,"strategy":"alt"}}""", "strategy")]
    [InlineData("""{"title":"x","tcp":{"ports":"12001","len":79,"desync":"--wf-tcp=1"}}""", "--wf-tcp")]           // параметр не из белого списка
    [InlineData("""{"title":"x","tcp":{"ports":"12001","len":79,"desync":"--dpi-desync=fake --dpi-desync-fake-unknown={fake}/evil.bin"}}""", "evil.bin")]   // фейк не из папки программы
    [InlineData("""{"title":"x","udp":{"ports":"5000"},"addresses":["10.0.0.0/24"]}""", "10.0.0.0")]                // служебная сеть
    [InlineData("""{"title":"x","udp":{"ports":"5000"},"addresses":["11.0.0.0/8"]}""", "11.0.0.0")]                 // слишком широкая
    [InlineData("""{"title":"x","tcp":{"ports":"443"}}""", "500")]                                                  // без адресов и сигнатуры — отказ (ловил бы всё на порту)
    [InlineData("""{"title":"x","tcp":{"ports":"443","len":100}}""", "443")]                                        // сигнатура без адресов на порту сайтов
    [InlineData("""{"title":"x","tcp":{"ports":"12001","len":"79"}}""", "len")]                                     // чужой тип
    [InlineData("""{"title":"x"}""", "")]                                                                            // ни одного протокола
    public void Code_Refused_Strictly(string json, string mention)
    {
        var e = Assert.Throws<StrategyFormatException>(() => ProfileCode.Decode(Pack(json), f => f == "stun.bin"));
        Assert.Contains(mention, e.Message);
    }

    [Fact]
    public void Code_Refused_ForeignBrokenTooLong()
    {
        string Why(string s) => Assert.Throws<StrategyFormatException>(() => ProfileCode.Decode(s, _ => true)).Message;
        Assert.Equal(L.T("core.game_code.is_strategy"), Why(StrategyExchange.Encode("Alt", new Dictionary<string, string> { ["general"] = "--dpi-desync=fake" })));
        Assert.Equal(L.T("core.game_code.not_code"), Why("--filter-tcp=12001 --dpi-desync=fake"));
        Assert.Contains("2", Why("dpishell-game:2:AAAA"));                      // другая версия — «обновите DpiShell»
        Assert.Equal(L.T("core.xchg.damaged"), Why(ProfileCode.Prefix + "AAAA!!"));
        Assert.Equal(L.T("core.xchg.damaged"), Why(ProfileCode.Prefix + "AAAA"));   // не deflate
        Assert.Contains("КБ", Why(ProfileCode.Prefix + new string('A', ProfileCode.MaxText)));
        // «Бомба»: маленькая строка, огромный JSON после распаковки.
        Assert.Equal(L.T("core.xchg.too_big_unpacked"), Why(Pack("""{"title":" """ + new string(' ', ProfileCode.MaxJson) + "\"}")));
        // Строку профиля, вставленную вместо стратегии, «Подбор» узнаёт и говорит, куда её нести.
        Assert.Equal(L.T("core.xchg.is_game"), Assert.Throws<StrategyFormatException>(() => StrategyExchange.Parse(MistfallCode)).Message);
    }

    // ---------- помощник: команды 1.22 ----------

    [Fact]
    public async Task Helper_Options_Separate_AndChecks()
    {
        using var t = new TempDir();
        var shell = new Shell(Make.Paths(t));
        shell.Data.Seed();
        var f = new HelperFixture(backend: PcHelper.Over(shell));
        await using var _ = f;
        var c = (await f.LinkAsync()).Client!;
        var rec = await c.AppProfileSetAsync(null, "Voice", null, "50000-50100", ["203.0.113.0/24"], 0);
        Assert.True(rec.Enabled);
        Assert.Equal(AppProfiles.Default, rec.Strategy);
        Assert.False(shell.State.ProfilesSeparate);   // без своих параметров «Разные стратегии» не трогаются
        Assert.Equal("", rec.UdpOwn);

        var off = await c.AppProfileOptionsAsync(rec.Id, enabled: false);
        Assert.False(off.Enabled);
        Assert.False(shell.State.AppProfiles.Single().Enabled);
        // «Свои» без своих параметров — отказ помощника; чужой id стратегии — отказ на входе.
        await Assert.ThrowsAsync<HelperException>(() => c.AppProfileOptionsAsync(rec.Id, strategy: AppProfiles.Own));
        await Assert.ThrowsAsync<HelperException>(() => c.AppProfileOptionsAsync(rec.Id, strategy: "no-such-strategy"));
        var variant = (await c.AppProfilesAsync()).Variants.First(v => v.Index > 0);
        var chosen = await c.AppProfileOptionsAsync(rec.Id, strategy: variant.StrategyId);
        Assert.Equal(variant.StrategyId, chosen.Strategy);
        Assert.Equal(0, chosen.Variant);   // «Разные стратегии» выключены — действует «По умолчанию»
        var info = await c.AppProfilesSeparateAsync(true);
        Assert.True(info.Separate);
        Assert.Equal(variant.Index, info.Profiles.Single().Variant);   // окну до 1.22 — номер действующего варианта
        Assert.Equal(variant.Udp, info.Profiles.Single().UdpDesync);

        // Профиль со своими параметрами включает «Разные стратегии» сам и выбирает «Свои».
        await c.AppProfilesSeparateAsync(false);
        var own = await c.AppProfileSetAsync(null, "Mistfall", "12001", null, [], 0, new ProfileSignature(79, "0000044d", TcpDesync: Own));
        Assert.Equal(AppProfiles.Own, own.Strategy);
        Assert.Equal(Own, own.TcpOwn);
        Assert.Equal(-1, own.Variant);
        Assert.True(shell.State.ProfilesSeparate);
    }

    [Fact]
    public async Task Helper_OldWindow_GameModeProfilesRefused_VariantBecomesChoice()
    {
        using var t = new TempDir();
        var shell = new Shell(Make.Paths(t));
        shell.Data.Seed();
        var f = new HelperFixture(backend: PcHelper.Over(shell));
        await using var _ = f;
        await f.LinkAsync();
        using var raw = await f.RawAsync();
        // «profiles» больше не режим: окно 1.22 его не шлёт, окно до 1.22 его не знало.
        var r = JsonDocument.Parse(await raw.AskAsync("""{"id":1,"cmd":"game_mode","args":{"mode":"profiles"}}""")).RootElement;
        Assert.False(r.GetProperty("ok").GetBoolean());
        // Старое окно: «Другой вариант» — номер варианта; он становится выбором стратегии, «Разные стратегии» включаются, чтобы он действовал.
        var variants = shell.ProfileVariants();
        r = JsonDocument.Parse(await raw.AskAsync("""{"id":2,"cmd":"app_profile_set","args":{"title":"Old","udp":"50000-50100","addresses":["203.0.113.0/24"],"variant":1}}""")).RootElement;
        Assert.True(r.GetProperty("ok").GetBoolean(), r.ToString());
        Assert.Equal(variants[1].StrategyId, shell.State.AppProfiles.Single().Strategy);
        Assert.True(shell.State.ProfilesSeparate);
        Assert.Equal(1, r.GetProperty("result").GetProperty("variant").GetInt32());
    }

    [Fact]
    public async Task AddWindow_PasteOfProfileLine_OpensImportWithIt()
    {
        var f = new HelperFixture();
        await using var _ = f;
        var apps = new AppsViewModel(await f.LinkAsync()) { Say = _ => { }, ReadText = () => "  " + MistfallCode + "\n" };
        string? opened = null;
        bool addClosed = false;
        apps.AddClosed += () => addClosed = true;
        apps.AddProfileDialog = a => a.PasteAllCmd.Execute(null);   // «Вставить из буфера» в «Новый профиль»
        apps.ImportDialog = a => opened = a.CodeText;
        apps.OpenAddProfile();
        Assert.True(addClosed);   // «Новый профиль» закрылся…
        Assert.Equal(MistfallCode, opened);   // …и строка открылась в «Импорт профиля»
        Assert.Equal("", apps.ImportFilter);
        Assert.Equal("", apps.CodeText);   // закрыли без добавления — не хранится
    }

    // ---------- окно: «Разные стратегии» и выбор у строк ----------

    [Fact]
    public async Task Window_Choices_DefaultVariantsOwn()
    {
        var f = new HelperFixture();
        await using var _ = f;
        var link = await f.LinkAsync();
        f.Backend.ProfilesValue.Add(new AppProfileInfo("p00000001", "A", "12001", "", [], -1, "свои", DateTime.Now, TcpOwn: Own, Strategy: AppProfiles.Own));
        f.Backend.ProfilesValue.Add(new AppProfileInfo("p00000002", "B", "", "5000", ["203.0.113.0/24"], 0, "", DateTime.Now));
        var apps = new AppsViewModel(link) { Say = _ => { } };
        await apps.RefreshAsync();
        var a = apps.Profiles[0];
        var b = apps.Profiles[1];
        Assert.Equal(AppProfiles.Own, a.Selected!.Key);
        Assert.Contains(a.Choices, c => c.Key == AppProfiles.Own);
        Assert.DoesNotContain(b.Choices, c => c.Key == AppProfiles.Own);   // своих параметров нет — «Свои» нет
        Assert.Equal(AppProfiles.Default, b.Choices[0].Key);
        Assert.Equal(f.Backend.VariantsValue.Count - 1, b.Choices.Count - 1);   // варианты, кроме совпадающего со стратегией ПК
        Assert.False(a.ShowChoice);
        apps.Separate = true;
        for (int i = 0; i < 200 && !apps.Separate; i++) await Task.Delay(15);
        Assert.Contains(f.Backend.Calls, x => x == "app_profiles_separate True");
        Assert.True(apps.Profiles[0].ShowChoice);
        apps.Profiles[1].Selected = apps.Profiles[1].Choices[1];
        for (int i = 0; i < 200 && !f.Backend.Calls.Any(x => x.StartsWith("app_profile_options p00000002")); i++) await Task.Delay(15);
        Assert.Contains(f.Backend.Calls, x => x == "app_profile_options p00000002  " + f.Backend.VariantsValue[1].StrategyId);
    }
}
