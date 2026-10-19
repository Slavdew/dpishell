using System.Net;
using System.Runtime.InteropServices;
using DpiShell.Core;

namespace DpiShell.Tests;

/// <summary>Профили программ (ТЗ 13): порты, проверка профиля, сборка командной строки winws с профилем, профиль из записи.</summary>
public sealed class AppProfileTests
{
    const string Tcp = "--dpi-desync=fake --dpi-desync-any-protocol=1 --dpi-desync-cutoff=n3";
    const string Udp = "--dpi-desync=fake --dpi-desync-repeats=10 --dpi-desync-any-protocol=1 --dpi-desync-fake-unknown-udp={fake}/ACTIVE_GAME_UDP.bin --dpi-desync-cutoff=n2";

    static AppProfile Profile(Action<AppProfile>? tweak = null)
    {
        var p = new AppProfile
        {
            Id = "p0a1b2c3d", Title = "Игра", TcpPorts = "443", UdpPorts = "27015-27030",
            Addresses = ["203.0.113.50/32", "198.51.100.0/24"], TcpDesync = Tcp, UdpDesync = Udp,
        };
        tweak?.Invoke(p);
        return p;
    }

    // ---------- порты ----------

    [Theory]
    [InlineData("443", "443")]
    [InlineData("27030-27015", null)]
    [InlineData("443,27015-27030", "443,27015-27030")]
    [InlineData("27030,27015,27016-27029,443", "443,27015-27030")]   // порядок и склейка соседних
    [InlineData("80,80,80", "80")]
    public void NormalizePorts_Canonical(string input, string? expected) => Assert.Equal(expected, AppProfiles.NormalizePorts(input));

    [Theory]
    [InlineData("")]
    [InlineData(" 443")]
    [InlineData("443 ")]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("1-70000")]
    [InlineData("443,,80")]
    [InlineData("443,")]
    [InlineData("-443")]
    [InlineData("443-")]
    [InlineData("1-2-3")]
    [InlineData("443;calc")]
    [InlineData("443 --new")]
    [InlineData("0x1BB")]
    [InlineData("４４３")]   // полноширинные цифры
    [InlineData("+443")]
    [InlineData("443\n80")]
    public void NormalizePorts_Garbage_Rejected(string input) => Assert.Null(AppProfiles.NormalizePorts(input));

    [Fact]
    public void NormalizePorts_TooManyRanges_Rejected()
    {
        var many = string.Join(',', Enumerable.Range(0, AppProfiles.MaxPortRanges + 1).Select(i => 2000 + i * 10));
        Assert.Null(AppProfiles.NormalizePorts(many));
        Assert.NotNull(AppProfiles.NormalizePorts(string.Join(',', Enumerable.Range(0, AppProfiles.MaxPortRanges).Select(i => 2000 + i * 10))));
    }

    [Fact]
    public void CompressPorts_MergesRuns_AndCollapsesScatteredHighPorts()
    {
        Assert.Equal("443,27015-27017", AppProfiles.CompressPorts([27015, 443, 27016, 27017, 443]));
        // 40 разбросанных верхних портов не раздувают фильтр: один диапазон.
        var scattered = Enumerable.Range(0, 40).Select(i => 30000 + i * 7).ToList();
        Assert.Equal($"{scattered[0]}-{scattered[^1]}", AppProfiles.CompressPorts(scattered));
        Assert.Null(AppProfiles.CompressPorts([]));
        Assert.Null(AppProfiles.CompressPorts([0, 70000]));
    }

    // ---------- проверка профиля ----------

    [Fact]
    public void ValidProfile_Passes() => Assert.Null(AppProfiles.Problem(Profile()));

    [Theory]
    [InlineData("p0a1b2c3")]          // короче
    [InlineData("P0A1B2C3D")]         // регистр
    [InlineData("p0a1b2c3d4")]
    [InlineData("../../x")]
    [InlineData("p0a1b2c3g")]
    [InlineData("")]
    public void BadId_Rejected(string id) => Assert.NotNull(AppProfiles.Problem(Profile(p => p.Id = id)));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Игра\nВторая")]
    public void BadTitle_Rejected(string title) => Assert.NotNull(AppProfiles.Problem(Profile(p => p.Title = title)));

    [Fact]
    public void LongTitle_Rejected() => Assert.NotNull(AppProfiles.Problem(Profile(p => p.Title = new string('я', 61))));

    [Theory]
    [InlineData("192.168.1.10/32")]      // локальная сеть
    [InlineData("10.0.0.0/24")]
    [InlineData("127.0.0.1/32")]
    [InlineData("224.0.0.251/32")]
    [InlineData("fe80::1/128")]
    [InlineData("203.0.113.0/8")]        // слишком широкая
    [InlineData("203.0.0.0/15")]
    [InlineData("0.0.0.0/0")]
    [InlineData("203.0.113.50/33")]
    [InlineData("203.0.113.5/24")]       // не канонический вид (host-биты)
    [InlineData("example.com")]
    [InlineData("203.0.113.50; --new")]
    [InlineData("203.0.113.50 /32")]
    [InlineData("C:\\lists\\x.txt")]
    public void BadAddress_Rejected(string address) =>
        Assert.NotNull(AppProfiles.Problem(Profile(p => p.Addresses = ["198.51.100.7/32", address])));

    [Fact]
    public void NoAddresses_OrTooMany_Rejected()
    {
        Assert.NotNull(AppProfiles.Problem(Profile(p => p.Addresses = [])));
        var many = Enumerable.Range(0, AppProfiles.MaxAddresses + 1).Select(i => $"198.51.{i / 250}.{i % 250 + 1}/32").ToList();
        Assert.NotNull(AppProfiles.Problem(Profile(p => p.Addresses = many)));
    }

    [Fact]
    public void NoPorts_Rejected() => Assert.NotNull(AppProfiles.Problem(Profile(p => { p.TcpPorts = ""; p.UdpPorts = ""; })));

    [Fact]
    public void NonCanonicalPorts_Rejected() => Assert.NotNull(AppProfiles.Problem(Profile(p => p.UdpPorts = "27030,27015")));

    /// <summary>Параметры профиля идут через тот же белый список, что и каталог: произвольный параметр winws не пройдёт.</summary>
    [Theory]
    [InlineData("--wf-tcp=1-65535")]
    [InlineData("--wf-raw=true")]
    [InlineData("--debug")]
    [InlineData("--new")]
    [InlineData("--hostlist=C:/Windows/x.txt")]
    [InlineData("--ipset=lists/other.txt")]
    [InlineData("--filter-tcp=1-65535")]
    [InlineData("--dpi-desync-fake-unknown-udp=C:/x.bin")]
    [InlineData("--dpi-desync-fake-unknown-udp=lists/x.bin")]
    [InlineData("--dpi-desync-fake-tls={fake}/../core/winws.exe")]
    [InlineData("--dpi-desync=fake\u00a0--debug")]
    [InlineData("--dpi-desync=fake\t--debug")]
    public void ForeignDesyncOption_Rejected(string extra)
    {
        Assert.NotNull(AppProfiles.Problem(Profile(p => p.TcpDesync = Tcp + " " + extra)));
        Assert.NotNull(AppProfiles.Problem(Profile(p => p.UdpDesync = Udp + " " + extra)));
    }

    [Fact]
    public void EmptyDesync_ForUsedProto_Rejected()
    {
        Assert.NotNull(AppProfiles.Problem(Profile(p => p.TcpDesync = "")));
        Assert.NotNull(AppProfiles.Problem(Profile(p => p.UdpDesync = "")));
        // Не используемый протокол может быть пустым целиком.
        Assert.Null(AppProfiles.Problem(Profile(p => { p.UdpPorts = ""; p.UdpDesync = ""; })));
    }

    // ---------- сборка командной строки ----------

    static Strategy General() => Make.Strategy(("general", "--dpi-desync=multisplit --dpi-desync-split-pos=1"), ("quic", "--dpi-desync=fake --dpi-desync-repeats=6"));

    [Fact]
    public void Build_WithoutProfiles_IsUnchanged()
    {
        using var t = new TempDir();
        var p = Make.Paths(t);
        var s = General();
        Assert.Equal(WinwsArgs.Build(s, p, BuildOptions.Default), WinwsArgs.Build(s, p, new BuildOptions(IpsetMode.Off, GameMode.Off, [])));
    }

    [Fact]
    public void Build_ProfileComesFirst_WithOwnFilterIpsetAndDesync()
    {
        using var t = new TempDir();
        var p = Make.Paths(t);
        var args = WinwsArgs.Build(General(), p, new BuildOptions(IpsetMode.Off, GameMode.Off, [Profile()])).ToList();

        // Порты профиля — в фильтре WinDivert.
        Assert.Equal($"--wf-tcp={WinwsArgs.WfTcp},443", args[0]);
        Assert.Equal($"--wf-udp={WinwsArgs.WfUdp},27015-27030", args[1]);
        // Первым — TCP-профиль, потом UDP, потом общие роли: у winws срабатывает первый подходящий.
        var firstNew = args.IndexOf("--new");
        Assert.Equal("--filter-tcp=443", args[2]);
        Assert.Equal($"--ipset={Path.Combine(p.Lists, "app-p0a1b2c3d.txt")}", args[3]);
        var udpAt = args.IndexOf("--filter-udp=27015-27030");
        var quicAt = args.IndexOf("--filter-udp=443");
        var generalAt = args.IndexOf("--filter-tcp=80,443");
        Assert.True(firstNew > 3 && firstNew < udpAt && udpAt < quicAt && udpAt < generalAt);
        // Игровые параметры, а не от общей роли; файлы фейков — из папки программы.
        Assert.Contains("--dpi-desync-any-protocol=1", args.Take(firstNew));
        Assert.Contains($"--dpi-desync-fake-unknown-udp={p.Fake}{Path.DirectorySeparatorChar}ACTIVE_GAME_UDP.bin", args);
        // Исключения пользователя действуют и в профиле.
        Assert.Contains($"--ipset-exclude={Path.Combine(p.Lists, IpLists.ExcludeFile)}", args.Take(firstNew));
        // Общие роли на месте и после профиля.
        Assert.True(args.Count(a => a == "--new") >= 3);
    }

    [Fact]
    public void Build_ProfileOnlyForUsedProtocol()
    {
        using var t = new TempDir();
        var args = WinwsArgs.Build(General(), Make.Paths(t), new BuildOptions(Profiles: [Profile(p => { p.UdpPorts = ""; p.UdpDesync = ""; })]));
        Assert.DoesNotContain("--filter-udp=27015-27030", args);
        Assert.Equal($"--wf-udp={WinwsArgs.WfUdp}", args[1]);
        Assert.Contains("--filter-tcp=443", args);
    }

    [Fact]
    public void Build_InvalidProfile_IsSkipped_NotPassedToWinws()
    {
        using var t = new TempDir();
        var p = Make.Paths(t);
        var bad = Profile(x => x.TcpDesync = "--debug");
        Assert.Equal(WinwsArgs.Build(General(), p), WinwsArgs.Build(General(), p, new BuildOptions(Profiles: [bad])));
        var evil = Profile(x => { x.Id = "p00000000"; x.Addresses = ["198.51.100.7/32\n--debug"]; });
        Assert.Equal(WinwsArgs.Build(General(), p), WinwsArgs.Build(General(), p, new BuildOptions(Profiles: [evil])));
    }

    [Fact]
    public void Build_AllTokensOfProfile_AreSafeForServiceCommandLine()
    {
        using var t = new TempDir();
        var p = Make.Paths(t);
        var args = WinwsArgs.Build(General(), p, new BuildOptions(Profiles: [Profile()]));
        var line = WinwsArgs.Join(args);
        // Строка службы разбирается Windows так же, как список: ни один аргумент профиля не склеился и не разошёлся.
        Assert.Equal(args, Native.ParseCommandLine("winws.exe " + line).Skip(1));
    }

    [Fact]
    public void Build_ProfilesWithGameMode_StillFirstAndWfKeepsGamePorts()
    {
        using var t = new TempDir();
        var s = Make.Strategy(("general", "--dpi-desync=fake"));
        var args = WinwsArgs.Build(s, Make.Paths(t), new BuildOptions(IpsetMode.List, GameMode.TcpUdp, [Profile()]));
        Assert.Equal($"--wf-tcp={WinwsArgs.WfTcp},{WinwsArgs.GamePorts},443", args[0]);
        Assert.Equal("--filter-tcp=443", args[2]);
        Assert.Contains($"--filter-tcp={WinwsArgs.GamePorts}", args);
    }

    [Fact]
    public void BuildOptions_FromState_TakesOnlyValidProfiles_AtMostLimit()
    {
        var st = new AppState();
        st.AppProfiles.Add(Profile());
        st.AppProfiles.Add(Profile(p => { p.Id = "p11111111"; p.UdpDesync = "--debug"; }));
        var o = BuildOptions.From(st, DateTime.UtcNow);
        Assert.Single(o.Profiles!);
        for (int i = 0; i < 20; i++) st.AppProfiles.Add(Profile(p => p.Id = "p" + i.ToString("x8")));
        Assert.Equal(AppProfiles.MaxProfiles, BuildOptions.From(st, DateTime.UtcNow).Profiles!.Count);
    }

    // ---------- варианты ----------

    [Fact]
    public void Variants_CurrentFirst_Distinct_AllValid()
    {
        var cat = StrategyCatalog.Load(Repo.Strategies);
        var current = cat.Strategies[3];
        var v = AppProfiles.Variants(cat.Strategies, current.Id);
        Assert.InRange(v.Count, 2, AppProfiles.MaxVariants);
        Assert.Contains("сейчас выбрана", v[0].Title);
        Assert.Equal(v.Count, v.Select(x => (x.Tcp, x.Udp)).Distinct().Count());
        Assert.All(v, x =>
        {
            Assert.Null(WinwsArgs.Problem("game_tcp", x.Tcp));
            Assert.Null(WinwsArgs.Problem("game_udp", x.Udp));
        });
        Assert.Equal(Enumerable.Range(0, v.Count), v.Select(x => x.Index));
    }

    // ---------- профиль из записи ----------

    static RecordedFlow F(string proto, string addr, int port, string proc = "game.exe", bool fresh = true, int count = 5, string state = "") =>
        new(proto, addr, port, 100, proc, fresh, count, 1, state);

    static RecordingResult Rec(string mode, bool udp, params RecordedFlow[] flows) => new(mode, false, 30, udp, false, false, [], flows);

    [Fact]
    public void Draft_DropsNoise_KeepsGame()
    {
        var rec = Rec("new", true,
            F("udp", "203.0.113.50", 27015), F("udp", "203.0.113.50", 27016), F("tcp", "198.51.100.7", 443),
            F("udp", "192.168.1.1", 53),                                   // локальная сеть и DNS
            F("udp", "198.51.100.8", 123),                                 // NTP
            F("tcp", "198.51.100.60", 443, "chrome.exe"),                  // браузер
            F("tcp", "198.51.100.61", 443, "svchost.exe"),                 // служба
            F("tcp", "198.51.100.62", 443, "OneDrive.exe"),
            F("tcp", "198.51.100.63", 443, "steamwebhelper.exe"),
            F("tcp", "198.51.100.64", 443, "old.exe", fresh: false));      // был до записи
        var d = AppProfileBuilder.FromRecording(rec);
        Assert.Equal("game", d.Title);
        Assert.Equal("443", d.TcpPorts);
        Assert.Equal("27015-27016", d.UdpPorts);
        Assert.Equal(["198.51.100.7/32", "203.0.113.50/32"], d.Addresses.Order(StringComparer.Ordinal));
        Assert.Equal(3, d.Kept);
        Assert.Equal(1, d.Removed["локальная сеть"]);
        Assert.Equal(1, d.Removed["служебный порт"]);
        Assert.Equal(4, d.Removed["системная служба или браузер"]);
        Assert.Equal(1, d.Removed["процесс был до записи"]);
        Assert.False(d.IsEmpty);
    }

    [Fact]
    public void Draft_AddressSeenByBackgroundProcess_IsDropped()
    {
        var rec = Rec("new", true,
            F("tcp", "198.51.100.7", 443, "launcher.exe"),       // тот же адрес был у браузера до игры
            F("tcp", "198.51.100.7", 443, "chrome-old.exe", fresh: false),
            F("udp", "203.0.113.50", 27015));
        var d = AppProfileBuilder.FromRecording(rec);
        Assert.Equal("", d.TcpPorts);
        Assert.Equal(["203.0.113.50/32"], d.Addresses);
        Assert.Equal(1, d.Removed["адрес был до игры"]);
    }

    [Fact]
    public void Draft_ProcessMode_ChosenBrowserIsNotNoise()
    {
        var rec = Rec("process", true, F("tcp", "198.51.100.60", 443, "chrome.exe"), F("udp", "198.51.100.60", 3478, "chrome.exe"));
        var d = AppProfileBuilder.FromRecording(rec);
        Assert.Equal("443", d.TcpPorts);
        Assert.Equal("3478", d.UdpPorts);
        Assert.Equal("chrome", d.Title);
    }

    [Fact]
    public void Draft_FourAddressesInOneSubnet_BecomeSubnet()
    {
        var rec = Rec("new", true, Enumerable.Range(10, 4).Select(i => F("udp", $"203.0.113.{i}", 30000 + i)).Append(F("udp", "198.51.100.9", 30100)).ToArray());
        var d = AppProfileBuilder.FromRecording(rec);
        Assert.Contains("203.0.113.0/24", d.Addresses);
        Assert.Contains("198.51.100.9/32", d.Addresses);
        Assert.Equal(2, d.Addresses.Count);
    }

    [Fact]
    public void Draft_NoUdpRecorded_SaysSo_AndHasNoUdpPorts()
    {
        var d = AppProfileBuilder.FromRecording(Rec("new", false, F("tcp", "198.51.100.7", 443)));
        Assert.Equal("", d.UdpPorts);
        Assert.Contains(d.Notes, n => n.Contains("UDP не записан"));
    }

    [Fact]
    public void Draft_OnlyNoise_IsEmpty()
    {
        var d = AppProfileBuilder.FromRecording(Rec("new", true, F("udp", "192.168.1.1", 53), F("tcp", "198.51.100.7", 443, "msedge.exe")));
        Assert.True(d.IsEmpty);
        Assert.Empty(d.Addresses);
    }

    [Fact]
    public void Draft_ManualSelection_OverridesAutoSort_ButNeverLocalNetwork()
    {
        var browser = F("tcp", "198.51.100.60", 443, "chrome.exe");
        var local = F("udp", "10.0.0.5", 27015);
        var game = F("udp", "203.0.113.50", 27015);
        var rec = Rec("new", true, browser, local, game);
        var d = AppProfileBuilder.FromRecording(rec, [browser, local]);
        Assert.Equal("443", d.TcpPorts);
        Assert.Equal("", d.UdpPorts);
        Assert.Equal(["198.51.100.60/32"], d.Addresses);
        Assert.Equal(1, d.Removed["снято вручную"]);
        Assert.Equal(1, d.Removed["локальная сеть или служебный порт"]);
    }

    [Fact]
    public void Draft_ResultIsAcceptedByProfileValidator()
    {
        var rec = Rec("new", true, F("udp", "203.0.113.50", 27015), F("udp", "203.0.113.51", 27016), F("tcp", "198.51.100.7", 443));
        var d = AppProfileBuilder.FromRecording(rec);
        var p = new AppProfile { Id = AppProfiles.NewId(), Title = d.Title, TcpPorts = d.TcpPorts, UdpPorts = d.UdpPorts, Addresses = [.. d.Addresses], TcpDesync = Tcp, UdpDesync = Udp };
        Assert.Null(AppProfiles.Problem(p));
    }

    [Fact]
    public void Sort_GivesReasonPerFlow()
    {
        var game = F("udp", "203.0.113.50", 27015);
        var dns = F("udp", "8.8.8.8", 53);
        var old = F("tcp", "198.51.100.64", 443, "old.exe", fresh: false);
        var s = AppProfileBuilder.Sort(Rec("new", true, game, dns, old));
        Assert.Null(s[game]);
        Assert.Equal("служебный порт", s[dns]);
        Assert.Equal("процесс был до записи", s[old]);
    }

    [Theory]
    [InlineData("10.1.2.3", true)]
    [InlineData("192.168.0.1", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("169.254.1.1", true)]
    [InlineData("224.0.0.251", true)]
    [InlineData("255.255.255.255", true)]
    [InlineData("::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("::ffff:192.168.1.5", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("203.0.113.50", false)]
    [InlineData("2606:4700::1111", false)]
    public void IsLocal_Classification(string ip, bool local) => Assert.Equal(local, FlowNoise.IsLocal(IPAddress.Parse(ip)));

    // ---------- разбор адресов WinDivert и таблицы TCP (без драйвера) ----------

    [Fact]
    public void WinDivertAddress_Ipv4Flow_Parsed()
    {
        var a = Marshal.AllocHGlobal(80);
        try
        {
            for (int i = 0; i < 80; i++) Marshal.WriteByte(a, i, 0);
            Marshal.WriteByte(a, 8, 2);             // layer FLOW
            Marshal.WriteByte(a, 9, 0);             // event ESTABLISHED
            Marshal.WriteInt32(a, 32, 4321);        // pid
            Marshal.WriteInt32(a, 52, unchecked((int)0xCB007132));   // 203.0.113.50 в порядке хоста
            Marshal.WriteInt16(a, 70, 27015);
            Marshal.WriteByte(a, 72, 17);
            var o = WinDivertFlowSource.Parse(a)!.Value;
            Assert.Equal(("udp", "203.0.113.50", 27015, 4321), (o.Proto, o.Remote.ToString(), o.Port, o.Pid));
            // Закрытие потока и loopback — не наблюдения.
            Marshal.WriteByte(a, 9, 1);
            Assert.Null(WinDivertFlowSource.Parse(a));
            Marshal.WriteByte(a, 9, 0);
            Marshal.WriteByte(a, 10, 0x04);
            Assert.Null(WinDivertFlowSource.Parse(a));
            // Не TCP и не UDP (ICMP) — тоже нет.
            Marshal.WriteByte(a, 10, 0);
            Marshal.WriteByte(a, 72, 1);
            Assert.Null(WinDivertFlowSource.Parse(a));
        }
        finally { Marshal.FreeHGlobal(a); }
    }

    [Fact]
    public void WinDivertAddress_Ipv6_WordsAreReversed()
    {
        // 2001:db8::1 → слова сетевого порядка 2001:0db8, 0, 0, 0001; WinDivert хранит от младшего к старшему.
        var ip = WinDivertFlowSource.FlowAddress([0x00000001, 0, 0, 0x20010db8], v6: true);
        Assert.Equal(IPAddress.Parse("2001:db8::1"), ip);
    }

    [Fact]
    public void TcpTable_Rows_ListenAndEmptyAreSkipped_States()
    {
        Assert.Null(TcpTableSource.Make(2, [1, 2, 3, 4], 80, 1));            // LISTEN
        Assert.Null(TcpTableSource.Make(5, [0, 0, 0, 0], 80, 1));            // пустой удалённый адрес
        Assert.Null(TcpTableSource.Make(5, [1, 2, 3, 4], 0, 1));
        Assert.Equal(FlowHint.Connecting, TcpTableSource.Make(3, [1, 2, 3, 4], 443, 7)!.Value.Hint);
        Assert.Equal(FlowHint.Established, TcpTableSource.Make(5, [1, 2, 3, 4], 443, 7)!.Value.Hint);
        Assert.Equal(FlowHint.Unknown, TcpTableSource.Make(11, [1, 2, 3, 4], 443, 7)!.Value.Hint);
    }

    /// <summary>Настоящая таблица TCP этой машины читается без исключений (только чтение, права не нужны); процессы — тоже.</summary>
    [Fact]
    public void RealTcpTable_AndProcessList_AreReadable()
    {
        var rows = TcpTableSource.Read();
        Assert.All(rows, r => Assert.True(r.Port is > 0 and <= 65535 && r.Proto == "tcp"));
        var procs = new ToolhelpProcessCatalog().Snapshot();
        Assert.Contains(procs, p => p.Pid == Environment.ProcessId);
    }
}
