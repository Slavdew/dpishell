using System.Text.Json;
using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.Tests;

/// <summary>
/// Находки аудита игровых профилей: профиль без адресов не ловит порты сайтов, порты своего фильтра — в его DstPort,
/// совместимость status с окном до 1.22, «Другой вариант» старого окна не стирает сигнатуру, подтверждение замены параметров из импорта.
/// </summary>
public sealed class GameProfilesAuditTests
{
    const string Mistfall = "outbound and tcp.DstPort = 12001 and tcp.PayloadLength = 79 and tcp.Payload32[0] = 0x0000044d";
    const string Pars = "--dpi-desync=fake --dpi-desync-any-protocol=1";
    static readonly HashSet<string> Fakes = ["stun.bin"];

    static ProfileImportResult Import(string filter, string pars, string subnets = "") => ProfileExchange.Import("Игра", filter, pars, subnets, Fakes);

    // ---------- 1. порты сайтов и порты своего фильтра ----------

    [Fact]
    public void Import_SignatureOn443_WithoutSubnets_Refused_WithSubnets_Accepted()
    {
        const string filter = "outbound and tcp.DstPort = 443 and tcp.PayloadLength = 100";
        var refused = Import(filter, "--filter-tcp=443 " + Pars);
        Assert.Null(refused.Profile);
        Assert.Contains("443", refused.Problem);
        Assert.Contains("подсети", refused.Problem);
        Assert.NotNull(Import(filter, "--filter-tcp=443 " + Pars, "203.0.113.0/24").Profile);
        // UDP 443 (QUIC) перехватывается тоже.
        Assert.Contains("443", Import("outbound and udp.DstPort = 443 and udp.PayloadLength = 20", "--filter-udp=443 " + Pars).Problem);
        // Диапазон, задевающий 8443, — тоже отказ; общий игровой диапазон (1024–65535) сам по себе не запрещён.
        Assert.Contains("8443", Import("outbound and tcp.DstPort >= 8000 and tcp.DstPort <= 9000 and tcp.PayloadLength = 100", "--filter-tcp=8000-9000 " + Pars).Problem);
    }

    [Fact]
    public void Import_Mistfall_12001_Accepted()
    {
        var r = Import(Mistfall, "--filter-tcp=12001 " + Pars);
        Assert.Null(r.Problem);
        Assert.Equal("12001", r.Profile!.TcpPorts);
    }

    [Fact]
    public void Import_CustomFilter_PortsWiderThanDstPort_Refused()
    {
        const string custom = "outbound and tcp.DstPort = 12001 and (tcp.PayloadLength = 79 or tcp.PayloadLength = 80)";
        var wide = Import(custom, "--filter-tcp=12001-12010 " + Pars);
        Assert.Null(wide.Profile);
        Assert.Contains("12001-12010", wide.Problem);
        Assert.Contains("DstPort", wide.Problem);
        Assert.NotNull(Import(custom, "--filter-tcp=12001 " + Pars).Profile);
    }

    [Fact]
    public void CustomFilter_DstPorts_ByRanges()
    {
        var r = CustomFilter.DstPorts("outbound and (tcp.DstPort = 80 or (tcp.DstPort >= 1000 and tcp.DstPort <= 2000)) and tcp.DstPort < 1500", true);
        Assert.Equal([(80, 80), (1000, 1499)], r);
        Assert.True(CustomFilter.Covers(r!, "80,1000-1499"));
        Assert.False(CustomFilter.Covers(r!, "80,1000-1500"));
        Assert.Null(CustomFilter.DstPorts("outbound and tcp.DstPort >= 0", true));   // все порты
        Assert.Contains("все порты", CustomFilter.Problem("outbound and tcp.DstPort >= 0", true));
        Assert.Contains("все порты", CustomFilter.Problem("outbound and tcp.DstPort < 0", true));   // ни одного
        // Повторный вызов — из кэша, тот же ответ.
        Assert.Equal(r, CustomFilter.DstPorts("outbound and (tcp.DstPort = 80 or (tcp.DstPort >= 1000 and tcp.DstPort <= 2000)) and tcp.DstPort < 1500", true));
    }

    [Fact]
    public void Profile_WithoutAddresses_OnWebPorts_Refused_InGameRange_Allowed()
    {
        var p = new AppProfile
        {
            Id = "p0a1b2c3d", Title = "x", TcpPorts = "2000-3000", TcpDesync = "--dpi-desync=fake", TcpPayloadLength = 10,
        };
        Assert.Contains("2053", AppProfiles.Problem(p));   // 2053 — порт Discord/Cloudflare из WfTcp
        p.TcpPorts = "27015-27030";
        Assert.Null(AppProfiles.Problem(p));
        p.Addresses = ["203.0.113.0/24"];
        p.TcpPorts = "443";
        Assert.Null(AppProfiles.Problem(p));   // с адресами — можно
    }

    // ---------- 2. status для окна до 1.22 ----------

    enum OldGameMode { Off, TcpUdp, Tcp, Udp }
    sealed record OldFeatures(IpsetMode Ipset, DateTime? AnyUntilUtc, IpsetMode ReturnTo, OldGameMode Game, bool GameActive, bool GameBlocked);

    [Fact]
    public void Status_GameField_OnlyOldValues_ReadableByOldWindow()
    {
        // «Общий» — прежними значениями; профили в status не входят (включаются каждый сам, их отдаёт app_profiles), лишних полей нет.
        foreach (var (mode, old) in new[] { (GameMode.Off, OldGameMode.Off), (GameMode.TcpUdp, OldGameMode.TcpUdp), (GameMode.Tcp, OldGameMode.Tcp), (GameMode.Udp, OldGameMode.Udp) })
        {
            var json = JsonSerializer.Serialize(new NetFeatures(IpsetMode.List, null, IpsetMode.List, mode, mode != GameMode.Off, false, null), HelperProtocol.Json);
            Assert.DoesNotContain("gameProfiles", json);
            Assert.Equal(old, JsonSerializer.Deserialize<OldFeatures>(json, HelperProtocol.Json)!.Game);   // старое окно: строгий enum
            Assert.Equal(mode, JsonSerializer.Deserialize<NetFeatures>(json, HelperProtocol.Json)!.Game);
        }
        Assert.Contains("\"game\":\"tcpUdp\"", JsonSerializer.Serialize(new NetFeatures(IpsetMode.List, null, IpsetMode.List, GameMode.TcpUdp, true, false, null), HelperProtocol.Json));
        Assert.Equal(GameMode.Udp, JsonSerializer.Deserialize<NetFeatures>("""{"ipset":"off","returnTo":"off","game":"udp","gameActive":false,"gameBlocked":false}""", HelperProtocol.Json)!.Game);
    }

    // ---------- 3. app_profile_set без полей 1.22 ----------

    [Fact]
    public async Task ProfileSet_WithoutNewFields_KeepsSignature()
    {
        using var t = new TempDir();
        var shell = new Shell(Make.Paths(t));
        shell.Data.Seed();
        var f = new HelperFixture(backend: PcHelper.Over(shell));
        await using var _ = f;
        var c = (await f.LinkAsync()).Client!;
        var info = await c.AppProfileSetAsync(null, "Игра", "12001", null, [], 0, new ProfileSignature(79, "0000044d", TcpFilter: Mistfall));
        // Старое окно: «Другой вариант» без единого поля 1.22.
        var again = await c.AppProfileSetAsync(info.Id, info.Title, info.TcpPorts, null, [], 0);
        Assert.Equal(79, again.TcpPayloadLength);
        Assert.Equal("0000044d", again.TcpPayloadHex);
        Assert.Equal(Mistfall, again.TcpFilter);
        Assert.Equal(0, again.Variant);
        // Новый профиль без полей — без сигнатуры (и без адресов на 12001 — отказ: адреса нужны).
        await Assert.ThrowsAsync<HelperException>(() => c.AppProfileSetAsync(null, "Другая", "12002", null, [], 0));
    }

    // ---------- 4. соединение по публичным адресам ----------

    [Fact]
    public async Task ConnectAny_NoAddresses_HostNotFound()
    {
        var e = await Assert.ThrowsAsync<System.Net.Sockets.SocketException>(async () => await HttpProbe.ConnectAnyAsync([], 443, CancellationToken.None));
        Assert.Equal(System.Net.Sockets.SocketError.HostNotFound, e.SocketErrorCode);
    }

    // ---------- 6. профиль из импорта: другая стратегия и обратно к «Свои» без потерь ----------

    [Fact]
    public async Task ImportedProfile_AlwaysOwn_OtherStrategyRefused_ParamsKept()
    {
        using var t = new TempDir();
        var shell = new Shell(Make.Paths(t));
        shell.Data.Seed();
        var f = new HelperFixture(backend: PcHelper.Over(shell));
        await using var _ = f;
        var link = await f.LinkAsync();
        var imp = ProfileExchange.Import("Игра", Mistfall, "--filter-tcp=12001 " + Pars, "", Fakes).Profile!;
        await link.Client!.AppProfileSetAsync(null, imp.Title, imp.TcpPorts, imp.UdpPorts, imp.Addresses, 0, imp.ToSignature());
        var asked = new List<string>();
        var apps = new AppsViewModel(link) { Say = _ => { }, Confirm = q => { asked.Add(q); return false; } };
        await apps.RefreshAsync();
        Assert.False(apps.Separate);   // свои параметры идут и без «Разных стратегий» — галка не включается
        Assert.False(apps.HasChoosable);   // один профиль, и тот со своими — галка не видна
        var row = Assert.Single(apps.Profiles);
        Assert.True(row.HasOwn);
        Assert.False(row.ShowChoice);
        Assert.Equal(imp.TcpDesync, shell.EffectiveProfile(shell.State.AppProfiles[0]).TcpDesync);

        // Другую стратегию помощник не принимает (окно её и не предлагает): параметры и сигнатура на месте, вопросов нет.
        var other = shell.ProfileVariants().First(v => v.Index > 0).StrategyId;
        await Assert.ThrowsAsync<HelperException>(() => link.Client!.AppProfileOptionsAsync(row.Id, strategy: other));
        Assert.Equal(AppProfiles.Own, shell.State.AppProfiles[0].Strategy);
        Assert.Equal(imp.TcpDesync, shell.State.AppProfiles[0].TcpDesync);
        Assert.Equal(79, shell.State.AppProfiles[0].TcpPayloadLength);
        Assert.Empty(asked);
    }
}
