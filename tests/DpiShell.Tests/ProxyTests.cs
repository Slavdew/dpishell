using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.Tests;

/// <summary>Белый список значений прокси: адрес, исключения, PAC. Всё — чистые функции, реестр не нужен.</summary>
public sealed class ProxyValuesTests
{
    [Theory]
    [InlineData("127.0.0.1:7897", "127.0.0.1:7897")]
    [InlineData("  Proxy.Example.COM:8080 ", "proxy.example.com:8080")]
    [InlineData("http=127.0.0.1:1080;https=127.0.0.1:1081", "http=127.0.0.1:1080;https=127.0.0.1:1081")]
    [InlineData("HTTPS=a.example:443;socks=127.0.0.1:1080", "https=a.example:443;socks=127.0.0.1:1080")]
    [InlineData("socks=127.0.0.1:1080", "socks=127.0.0.1:1080")]
    [InlineData("[::1]:8080", "[::1]:8080")]
    [InlineData("010.001.000.001:80", "10.1.0.1:80")]
    public void Server_valid_is_normalized(string input, string expected) => Assert.Equal(expected, ProxyValues.NormalizeServer(input));

    [Theory]
    [InlineData("")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1:0")]
    [InlineData("127.0.0.1:70000")]
    [InlineData("127.0.0.1:80a")]
    [InlineData("http://127.0.0.1:80")]
    [InlineData("a b:80")]
    [InlineData("evil.example:80;rm")]
    [InlineData("1.2.3.4.5:80")]
    [InlineData("999.1.1.1:80")]
    [InlineData("http=a.example:1;http=b.example:2")]
    [InlineData("a.example:1;https=b.example:2")]
    [InlineData("gopher=a.example:1")]
    [InlineData("host_name:80")]
    [InlineData(@"C:\Windows\x:80")]
    [InlineData("a.example:1\"")]
    [InlineData("-a.example:80")]
    [InlineData("[::g]:80")]
    [InlineData("[1.2.3.4]:80")]
    public void Server_invalid_is_rejected(string input) => Assert.Null(ProxyValues.NormalizeServer(input));

    [Fact]
    public void Server_too_long_is_rejected() =>
        Assert.Null(ProxyValues.NormalizeServer(new string('a', ProxyValues.MaxServerLength) + ".example:80"));

    [Fact]
    public void Server_checking_prefers_https_then_http_then_plain_then_socks()
    {
        Assert.Equal(("http", "b.example:2"), ProxyValues.ParseServer("http=a.example:1;https=b.example:2")!.ForCheck());
        Assert.Equal(("http", "a.example:1"), ProxyValues.ParseServer("a.example:1")!.ForCheck());
        Assert.Equal(("socks5", "a.example:9"), ProxyValues.ParseServer("socks=a.example:9")!.ForCheck());
    }

    [Fact]
    public void Bypass_is_split_deduplicated_and_joined()
    {
        var (v, ok) = ProxyValues.NormalizeBypass("<local>; *.example.com,192.168.*\n*.EXAMPLE.com  <-loopback>");
        Assert.True(ok);
        Assert.Equal("<local>;*.example.com;192.168.*;<-loopback>", v);
        Assert.Equal((null, true), ProxyValues.NormalizeBypass("  "));
        Assert.Equal((null, true), ProxyValues.NormalizeBypass(null));
    }

    [Theory]
    [InlineData(@"C:\Windows")]
    [InlineData("a\"b")]
    [InlineData("<script>")]
    [InlineData("$(calc)")]
    [InlineData("a|b")]
    [InlineData("%TEMP%")]
    public void Bypass_with_odd_characters_is_rejected(string input) => Assert.False(ProxyValues.NormalizeBypass(input).Ok);

    [Fact]
    public void Bypass_limit_is_100_entries()
    {
        string Many(int n) => string.Join(';', Enumerable.Range(0, n).Select(i => $"h{i}.example"));
        Assert.True(ProxyValues.NormalizeBypass(Many(100)).Ok);
        Assert.False(ProxyValues.NormalizeBypass(Many(101)).Ok);
    }

    [Theory]
    [InlineData("http://proxy.example.net/proxy.pac")]
    [InlineData("https://proxy.example.net:8443/a/b.pac?x=1")]
    public void Pac_http_and_https_pass(string url) => Assert.Equal((url, true), ProxyValues.NormalizePac(url));

    [Theory]
    [InlineData("file:///C:/proxy.pac")]
    [InlineData("ftp://proxy.example.net/p.pac")]
    [InlineData("http://user:pass@proxy.example.net/p.pac")]
    [InlineData("http://proxy.example.net/a b.pac")]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://")]
    [InlineData("proxy.example.net/p.pac")]
    [InlineData("http://x/p\"ac")]
    [InlineData(@"http://x\p.pac")]
    public void Pac_other_is_rejected(string url) => Assert.False(ProxyValues.NormalizePac(url).Ok);

    [Fact]
    public void Problem_requires_address_or_pac_and_explains()
    {
        Assert.Null(ProxyValues.Problem("127.0.0.1:7897", null, null));
        Assert.Null(ProxyValues.Problem(null, null, "http://x.example/p.pac"));
        Assert.NotNull(ProxyValues.Problem(null, null, null));
        Assert.Contains("хост:порт", ProxyValues.Problem("nonsense", null, null));
        Assert.Contains("Исключения", ProxyValues.Problem("127.0.0.1:1", @"C:\x", null));
        Assert.Contains("http", ProxyValues.Problem("127.0.0.1:1", null, "file:///x")!);
    }

    [Fact]
    public void Build_keeps_only_normalized_values_and_enabled_means_server()
    {
        var s = ProxyValues.Build(" Proxy.Example.net:3128", "<local>, *.lan", "https://p.example/p.pac");
        Assert.Equal(new ProxySettings(true, "proxy.example.net:3128", "<local>;*.lan", "https://p.example/p.pac"), s);
        Assert.Equal(new ProxySettings(false, null, null, "https://p.example/p.pac"), ProxyValues.Build(null, null, "https://p.example/p.pac"));
        Assert.True(s.IsOn);
        Assert.False(ProxySettings.Off.IsOn);
        Assert.False(s.Cleared().IsOn);
        Assert.Equal("proxy.example.net:3128", s.Cleared().Server);   // адрес остаётся, как у самой Windows
        Assert.Null(s.Cleared().Pac);
    }

    [Fact]
    public void WinHttp_takes_only_fixed_proxy()
    {
        Assert.Equal(new WinHttpInfo("a.example:1", "<local>"), WinHttpInfo.From(new ProxySettings(true, "a.example:1", "<local>", "https://x/p.pac")));
        Assert.True(WinHttpInfo.From(new ProxySettings(false, "a.example:1", null, "https://x/p.pac")).IsDirect);
    }

    [Fact]
    public void Hints_are_short_honest_and_have_no_banned_words()
    {
        Assert.All(ProxyHints.All, h =>
        {
            Assert.DoesNotContain("обход", h, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("у всех", h, StringComparison.OrdinalIgnoreCase);
            Assert.True(h.Length < 200, h);
        });
        Assert.Contains(ProxyHints.All, h => h.Contains("Firefox"));
        Assert.Contains(ProxyHints.All, h => h.Contains("LoopbackExempt"));
        // На виду — простые слова: жаргон только под «Подробнее».
        Assert.InRange(ProxyHints.Short.Count, 3, 4);
        Assert.All(ProxyHints.Short, h =>
        {
            foreach (var jargon in new[] { "WinINET", "loopback", ".NET", "SOCKS", "WinHTTP" })
                Assert.DoesNotContain(jargon, h, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Contains(ProxyHints.Details, h => h.Contains("<-loopback>"));
    }
}

/// <summary>Реестр прокси: белый список имён и веток, чтение, запись, политика.</summary>
public sealed class ProxyRegistryTests
{
    [Fact]
    public void Round_trip_through_memory_registry()
    {
        var r = new MemoryProxyRegistry();
        var s = new ProxySettings(true, "127.0.0.1:7897", "<local>", "http://p.example/p.pac");
        ProxyRegistryIo.Write(r, ProxyKey.UserSettings, null, s);
        Assert.Equal(s, ProxyRegistryIo.Read(r, ProxyKey.UserSettings));
        Assert.Equal(1, r.Values[(ProxyKey.UserSettings, "", ProxyNames.Enable)]);
        Assert.Equal(ProxySettings.Off, ProxyRegistryIo.Read(r, ProxyKey.MachineSettings));   // ветки раздельные
        ProxyRegistryIo.Write(r, ProxyKey.UserSettings, null, ProxySettings.Off);
        Assert.Equal(ProxySettings.Off, ProxyRegistryIo.Read(r, ProxyKey.UserSettings));
        Assert.DoesNotContain(r.Values.Keys, k => k.Item3 == ProxyNames.Server);   // пустые значения удалены
    }

    [Fact]
    public void Auto_detect_is_read_from_connection_blob_flags()
    {
        var r = new MemoryProxyRegistry();
        Assert.False(ProxyRegistryIo.AutoDetect(r, ProxyKey.UserSettings));
        var blob = new byte[24]; blob[8] = 0x09;   // direct + auto-detect
        r.Values[(ProxyKey.UserSettings, "", ProxyNames.ConnectionsBlob)] = blob;
        Assert.True(ProxyRegistryIo.AutoDetect(r, ProxyKey.UserSettings));
        blob[8] = 0x03;
        Assert.False(ProxyRegistryIo.AutoDetect(r, ProxyKey.UserSettings));
    }

    [Fact]
    public void Policy_flags_are_read()
    {
        var r = new MemoryProxyRegistry();
        Assert.False(ProxyRegistryIo.ChangeBlocked(r));
        Assert.Null(ProxyRegistryIo.PerUserPolicy(r));
        r.Values[(ProxyKey.UserIePolicy, "", ProxyNames.IeProxy)] = 1;
        r.Values[(ProxyKey.MachinePolicy, "", ProxyNames.PerUser)] = 0;
        Assert.True(ProxyRegistryIo.ChangeBlocked(r));
        Assert.Equal(0, ProxyRegistryIo.PerUserPolicy(r));
        var o = ProxyOverview.Read(r, new MemoryWinHttpProxy());
        Assert.True(o.MachineWide);
        Assert.True(o.PolicyBlocked);
        Assert.Equal(o.Machine, o.Effective);
    }

    [Fact]
    public void Only_whitelisted_names_and_branches_can_be_written()
    {
        var r = new MemoryProxyRegistry();
        Assert.Throws<ArgumentException>(() => r.SetString(ProxyKey.UserSettings, null, "Run", "x"));
        Assert.Throws<ArgumentException>(() => r.SetDword(ProxyKey.UserSettings, null, ProxyNames.PerUser, 0));   // политика — только в своей ветке
        Assert.Throws<ArgumentException>(() => r.SetString(ProxyKey.MachinePolicy, null, ProxyNames.Server, "x"));
        Assert.Throws<ArgumentException>(() => r.SetString(ProxyKey.UserSettings, null, ProxyNames.ConnectionsBlob, "x"));   // блоб — только чтение
        Assert.Throws<ArgumentException>(() => r.SetDword(ProxyKey.MachineIePolicy, null, ProxyNames.IeProxy, 1));
        Assert.Throws<ArgumentException>(() => r.Get(ProxyKey.UserSettings, null, @"..\Run\DpiShell"));
        Assert.Empty(r.Values);
        // Настоящий реестр: проверка имён и SID идёт до открытия ветки — ничего не открывается и не пишется.
        var w = new WindowsProxyRegistry();
        Assert.Throws<ArgumentException>(() => w.SetString(ProxyKey.UserSettings, null, "Evil", "x"));
        Assert.Throws<ArgumentException>(() => w.Delete(ProxyKey.MachineSettings, null, "Evil"));
        Assert.Throws<ArgumentException>(() => w.Get(ProxyKey.UserSettings, null, "Evil"));
        Assert.Throws<ArgumentException>(() => w.Get(ProxyKey.UserSettings, "S-1-5-18", ProxyNames.Enable));   // SYSTEM — не пользователь
        Assert.Throws<ArgumentException>(() => w.Get(ProxyKey.UserSettings, @"S-1-5-21-1\..\x", ProxyNames.Enable));
    }

    [Theory]
    [InlineData("S-1-5-21-1111111111-2222222222-3333333333-1001", true)]
    [InlineData("S-1-12-1-1-2-3-4", true)]
    [InlineData("S-1-5-18", false)]
    [InlineData("S-1-5-19", false)]
    [InlineData("S-1-5-32-544", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("S-1-5-21-1\\x", false)]
    public void User_sid_check(string? sid, bool ok) => Assert.Equal(ok, ProxySid.IsUserSid(sid));
}

/// <summary>Изменение прокси, журнал изменений, «прежний», откат — на реестре и WinHTTP в памяти.</summary>
public sealed class ProxyManagerTests : IDisposable
{
    readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    readonly MemoryProxyRegistry _reg = new();
    readonly MemoryWinHttpProxy _win = new();
    readonly NullProxyNotifier _notify = new();
    ChangeJournal Journal => _journal ??= new ChangeJournal(_tmp.Sub("changes.jsonl"));
    ChangeJournal? _journal;

    ProxyManager Mgr() => new(_reg, _win) { Recorder = Journal, Notifier = _notify, Backup = new ProxyBackupStore(_tmp.Sub("proxy-backup.json")) };
    ChangeUndoEngine Engine()
    {
        var e = new ChangeUndoEngine(Journal);
        e.Register(new ProxyUndoer(_reg, _win, _notify));
        return e;
    }

    static ProxySettings Clash => ProxyValues.Build("127.0.0.1:7897", "<local>", null);

    [Fact]
    public void User_change_is_written_journaled_and_system_is_notified()
    {
        var r = Mgr().ApplyUser(null, Clash, ChangeKinds.ProxyUser);
        Assert.True(r.Ok);
        Assert.True(r.Changed);
        Assert.Equal(Clash, ProxyRegistryIo.Read(_reg, ProxyKey.UserSettings));
        Assert.Equal(1, _notify.Calls);
        var e = Assert.Single(Journal.Active());
        Assert.Equal(ChangeKinds.ProxyUser, e.Kind);
        Assert.True(e.Completed);
        Assert.False(ProxySnapshot.Settings(e.Before).IsOn);
        Assert.Equal(Clash, ProxySnapshot.Settings(e.After));
        Assert.DoesNotContain("127.0.0.1", e.Title);   // адрес — в снимках, а не в заголовке списка
    }

    [Fact]
    public void Same_value_writes_nothing()
    {
        Mgr().ApplyUser(null, Clash, ChangeKinds.ProxyUser);
        int writes = _reg.Writes;
        var r = Mgr().ApplyUser(null, Clash, ChangeKinds.ProxyUser);
        Assert.True(r.Ok);
        Assert.False(r.Changed);
        Assert.Equal(writes, _reg.Writes);
        Assert.Single(Journal.Entries());
    }

    [Fact]
    public async Task Repeated_changes_share_one_entry_and_undo_goes_to_the_very_first_value()
    {
        Mgr().ApplyUser(null, Clash, ChangeKinds.ProxyUser);
        Mgr().ApplyUser(null, ProxyValues.Build("127.0.0.1:8888", null, "https://p.example/p.pac"), ChangeKinds.ProxyUser);
        var e = Assert.Single(Journal.Active());
        Assert.Equal("127.0.0.1:8888", ProxySnapshot.Settings(e.After).Server);
        Assert.False(ProxySnapshot.Settings(e.Before).IsOn);

        var report = await Engine().UndoAllAsync();
        Assert.Equal(UndoStatus.Restored, Assert.Single(report.Items).Status);
        Assert.Equal(ProxySettings.Off, ProxyRegistryIo.Read(_reg, ProxyKey.UserSettings));
        Assert.Empty(Journal.Active());
        Assert.Empty((await Engine().UndoAllAsync()).Items);   // откатывать больше нечего
    }

    [Fact]
    public void Going_back_to_the_original_value_closes_the_entry()
    {
        Mgr().ApplyUser(null, Clash, ChangeKinds.ProxyUser);
        Mgr().ApplyUser(null, ProxySettings.Off, ChangeKinds.ProxyUser);
        Assert.Empty(Journal.Active());
        Assert.Equal(ChangeState.Closed, Journal.Entries().Single().State);
    }

    [Fact]
    public async Task Undo_does_not_touch_a_value_changed_by_someone_else()
    {
        Mgr().ApplyUser(null, Clash, ChangeKinds.ProxyUser);
        _reg.Values[(ProxyKey.UserSettings, "", ProxyNames.Server)] = "10.0.0.1:3128";   // человек или другая программа
        var report = await Engine().UndoAllAsync();
        Assert.Equal(UndoStatus.ChangedByOthers, Assert.Single(report.Items).Status);
        Assert.Equal("10.0.0.1:3128", _reg.Values[(ProxyKey.UserSettings, "", ProxyNames.Server)]);
        Assert.Empty(Journal.Active());   // пункт закрыт «изменено после нас»
    }

    [Fact]
    public async Task Undo_notifies_the_system()
    {
        Mgr().ApplyUser(null, Clash, ChangeKinds.ProxyUser);
        int before = _notify.Calls;
        await Engine().UndoAllAsync();
        Assert.Equal(before + 1, _notify.Calls);
    }

    [Fact]
    public void Refused_write_is_reported_and_entry_aborted()
    {
        _reg.DenyWrites = true;
        var r = Mgr().ApplyUser(null, Clash, ChangeKinds.ProxyUser);
        Assert.False(r.Ok);
        Assert.Contains("Не удалось", r.Message);
        Assert.Empty(Journal.Active());
        Assert.Equal(ChangeState.Aborted, Journal.Entries().Single().State);
    }

    [Fact]
    public void Clear_keeps_address_but_switches_off_proxy_and_pac()
    {
        var m = Mgr();
        m.ApplyUser(null, ProxyValues.Build("127.0.0.1:7897", "<local>", "https://p.example/p.pac"), ChangeKinds.ProxyUser);
        var cur = ProxyRegistryIo.Read(_reg, ProxyKey.UserSettings);
        m.ApplyUser(null, cur.Cleared(), ChangeKinds.ProxyUser);
        var now = ProxyRegistryIo.Read(_reg, ProxyKey.UserSettings);
        Assert.False(now.IsOn);
        Assert.Equal("127.0.0.1:7897", now.Server);
        Assert.Null(now.Pac);
    }

    [Fact]
    public void User_in_other_hive_writes_there_only_and_needs_loaded_profile()
    {
        var m = Mgr();
        var no = m.ApplyUser(Sid, Clash, ChangeKinds.ProxyUserAdmin);
        Assert.False(no.Ok);
        Assert.Contains("не загружены", no.Message);
        Assert.Empty(_reg.Values);

        _reg.LoadedHives.Add(Sid);
        Assert.True(m.ApplyUser(Sid, Clash, ChangeKinds.ProxyUserAdmin).Ok);
        Assert.Equal(Clash, ProxyRegistryIo.Read(_reg, ProxyKey.UserSettings, Sid));
        Assert.Equal(ProxySettings.Off, ProxyRegistryIo.Read(_reg, ProxyKey.UserSettings));   // HKCU окна не тронут
        Assert.Equal(Sid, ProxySnapshot.Sid(Assert.Single(Journal.Active()).After));
    }

    // ---------- вся машина ----------

    [Fact]
    public async Task Machine_sets_hklm_policy_and_winhttp_and_undo_returns_all_three()
    {
        var next = ProxyValues.Build("127.0.0.1:7897", "<local>", null);
        var r = Mgr().ApplyMachine(next, WinHttpInfo.From(next));
        Assert.True(r.Ok, r.Message);
        Assert.Equal(next, ProxyRegistryIo.Read(_reg, ProxyKey.MachineSettings));
        Assert.Equal(0, ProxyRegistryIo.PerUserPolicy(_reg));
        Assert.Equal(new WinHttpInfo("127.0.0.1:7897", "<local>"), _win.Value);
        Assert.Equal(ProxySettings.Off, ProxyRegistryIo.Read(_reg, ProxyKey.UserSettings));
        var e = Assert.Single(Journal.Active());
        Assert.Equal(ChangeKinds.ProxyMachine, e.Kind);

        var rep = await Engine().UndoAllAsync();
        Assert.Equal(UndoStatus.Restored, Assert.Single(rep.Items).Status);
        Assert.Equal(ProxySettings.Off, ProxyRegistryIo.Read(_reg, ProxyKey.MachineSettings));
        Assert.Null(ProxyRegistryIo.PerUserPolicy(_reg));
        Assert.True(_win.Value.IsDirect);
    }

    [Fact]
    public void Machine_without_winhttp_leaves_winhttp_alone()
    {
        _win.Value = new WinHttpInfo("corp.example:8080", null);
        Assert.True(Mgr().ApplyMachine(Clash, null).Ok);
        Assert.Equal("corp.example:8080", _win.Value.Proxy);
        Assert.Null(ProxySnapshot.WinHttp(Journal.Active().Single().After));
    }

    [Fact]
    public async Task Machine_undo_skips_the_part_changed_by_others_and_says_so()
    {
        Mgr().ApplyMachine(Clash, WinHttpInfo.From(Clash));
        _reg.Values[(ProxyKey.MachinePolicy, "", ProxyNames.PerUser)] = 1;   // кто-то поменял политику
        var rep = await Engine().UndoAllAsync();
        var item = Assert.Single(rep.Items);
        Assert.Equal(UndoStatus.Restored, item.Status);
        Assert.Contains("не тронуто", item.Note);
        Assert.Equal(1, ProxyRegistryIo.PerUserPolicy(_reg));
        Assert.Equal(ProxySettings.Off, ProxyRegistryIo.Read(_reg, ProxyKey.MachineSettings));
    }

    [Fact]
    public void Machine_clear_remembers_previous_and_restore_puts_it_back_once()
    {
        var m = Mgr();
        m.ApplyMachine(Clash, WinHttpInfo.From(Clash));
        var cleared = m.ApplyMachine(ProxyRegistryIo.Read(_reg, ProxyKey.MachineSettings).Cleared(), WinHttpInfo.Direct);
        Assert.True(cleared.Ok, cleared.Message);
        Assert.False(ProxyRegistryIo.Read(_reg, ProxyKey.MachineSettings).IsOn);
        Assert.True(_win.Value.IsDirect);
        Assert.Equal(0, ProxyRegistryIo.PerUserPolicy(_reg));   // политику при «Очистить» не трогаем

        var backup = new ProxyBackupStore(_tmp.Sub("proxy-backup.json")).Load();
        Assert.NotNull(backup);
        Assert.Equal(Clash, backup!.Machine);
        Assert.Equal(new WinHttpInfo("127.0.0.1:7897", "<local>"), backup.WinHttp);

        var back = Mgr().RestoreMachine();
        Assert.True(back.Ok, back.Message);
        Assert.Equal(Clash, ProxyRegistryIo.Read(_reg, ProxyKey.MachineSettings));
        Assert.Equal("127.0.0.1:7897", _win.Value.Proxy);
        Assert.Null(new ProxyBackupStore(_tmp.Sub("proxy-backup.json")).Load());
        Assert.False(Mgr().RestoreMachine().Ok);   // второй раз возвращать нечего
    }

    [Fact]
    public void Clear_of_already_off_machine_keeps_older_previous()
    {
        var m = Mgr();
        m.ApplyMachine(Clash, null);
        m.ApplyMachine(Clash.Cleared(), null);
        Assert.Equal(Clash, new ProxyBackupStore(_tmp.Sub("proxy-backup.json")).Load()!.Machine);
        m.ApplyMachine(ProxySettings.Off, null);   // выключено уже — «прежний» не перезаписывается пустым
        Assert.Equal(Clash, new ProxyBackupStore(_tmp.Sub("proxy-backup.json")).Load()!.Machine);
    }

    [Fact]
    public async Task Winhttp_refusal_is_an_error_but_what_was_written_stays_in_the_journal()
    {
        _win.Refuse = true;
        var r = Mgr().ApplyMachine(Clash, WinHttpInfo.From(Clash));
        Assert.False(r.Ok);
        Assert.Contains("WinHTTP", r.Message);
        var e = Assert.Single(Journal.Active());   // реестр уже записан — откатить его можно
        Assert.Equal(Clash, ProxySnapshot.Settings(e.After));
        _win.Refuse = false;
        Assert.Equal(UndoStatus.Restored, Assert.Single((await Engine().UndoAllAsync()).Items).Status);
        Assert.Equal(ProxySettings.Off, ProxyRegistryIo.Read(_reg, ProxyKey.MachineSettings));
    }

    [Fact]
    public void Backup_store_survives_garbage()
    {
        var f = _tmp.Sub("b.json");
        File.WriteAllText(f, "{ not json");
        Assert.Null(new ProxyBackupStore(f).Load());
        Assert.Null(new ProxyBackupStore(_tmp.Sub("missing.json")).Load());
    }
}

/// <summary>Проверка прокси: HTTP CONNECT и SOCKS5 на локальных заглушках (петля 127.0.0.1, интернет не нужен).</summary>
public sealed class ProxyCheckerTests
{
    static (TcpListener L, int Port) Listen()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return (l, ((IPEndPoint)l.LocalEndpoint).Port);
    }

    static async Task ServeHttp(TcpListener l, string reply, int times)
    {
        for (int i = 0; i < times; i++)
        {
            using var c = await l.AcceptTcpClientAsync();
            var s = c.GetStream();
            var buf = new byte[2048];
            int n = await s.ReadAsync(buf);
            var req = Encoding.ASCII.GetString(buf, 0, n);
            Assert.StartsWith("CONNECT ", req);
            await s.WriteAsync(Encoding.ASCII.GetBytes(reply));
        }
    }

    [Fact]
    public async Task Http_proxy_that_connects_works_and_shows_time()
    {
        var (l, port) = Listen();
        try
        {
            var serve = ServeHttp(l, "HTTP/1.1 200 Connection established\r\n\r\n", 2);
            var r = await ProxyChecker.CheckAsync($"127.0.0.1:{port}", [("a.example", 443), ("b.example", 443)]);
            await serve;
            Assert.True(r.Ok);
            Assert.Equal("http", r.Kind);
            Assert.All(r.Probes, p => Assert.True(p.Ok));
            Assert.StartsWith("работает (HTTP): 2 из 2", r.Summary);
            Assert.Contains("мс", r.Summary);
        }
        finally { l.Stop(); }
    }

    [Theory]
    [InlineData("HTTP/1.1 407 Proxy Authentication Required\r\n\r\n", "логин и пароль")]
    [InlineData("HTTP/1.1 502 Bad Gateway\r\n\r\n", "502")]
    [InlineData("SSH-2.0-OpenSSH\r\n", "не HTTP-прокси")]
    public async Task Http_proxy_errors_are_explained(string reply, string expected)
    {
        var (l, port) = Listen();
        try
        {
            var serve = ServeHttp(l, reply, 1);
            var r = await ProxyChecker.CheckAsync($"127.0.0.1:{port}", [("a.example", 443)]);
            await serve;
            Assert.False(r.Ok);
            Assert.StartsWith("не отвечает", r.Summary);
            Assert.Contains(expected, r.Summary);
        }
        finally { l.Stop(); }
    }

    [Fact]
    public async Task Closed_port_does_not_respond()
    {
        var (l, port) = Listen();
        l.Stop();
        var r = await ProxyChecker.CheckAsync($"127.0.0.1:{port}", [("a.example", 443)], TimeSpan.FromSeconds(3));
        Assert.False(r.Ok);
        Assert.StartsWith("не отвечает", r.Summary);
    }

    [Fact]
    public async Task Silent_proxy_times_out()
    {
        var (l, port) = Listen();
        try
        {
            var accept = l.AcceptTcpClientAsync();   // принимает и молчит
            var r = await ProxyChecker.CheckAsync($"127.0.0.1:{port}", [("a.example", 443)], TimeSpan.FromMilliseconds(400));
            Assert.False(r.Ok);
            Assert.Contains("нет ответа", r.Summary);
            (await accept).Dispose();
        }
        finally { l.Stop(); }
    }

    [Fact]
    public async Task Socks5_proxy_is_checked_with_a_socks_handshake()
    {
        var (l, port) = Listen();
        try
        {
            var serve = Task.Run(async () =>
            {
                using var c = await l.AcceptTcpClientAsync();
                var s = c.GetStream();
                var hello = new byte[3];
                await s.ReadExactlyAsync(hello);
                Assert.Equal(new byte[] { 5, 1, 0 }, hello);
                await s.WriteAsync(new byte[] { 5, 0 });
                var head = new byte[5];
                await s.ReadExactlyAsync(head);
                Assert.Equal(new byte[] { 5, 1, 0, 3 }, head[..4]);
                var rest = new byte[head[4] + 2];
                await s.ReadExactlyAsync(rest);
                Assert.Equal("a.example", Encoding.ASCII.GetString(rest, 0, head[4]));
                await s.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
            });
            var r = await ProxyChecker.CheckAsync($"socks=127.0.0.1:{port}", [("a.example", 443)]);
            await serve;
            Assert.True(r.Ok, r.Summary);
            Assert.Equal("socks5", r.Kind);
            Assert.Contains("SOCKS5", r.Summary);
        }
        finally { l.Stop(); }
    }

    [Fact]
    public async Task Socks5_refusal_is_reported()
    {
        var (l, port) = Listen();
        try
        {
            var serve = Task.Run(async () =>
            {
                using var c = await l.AcceptTcpClientAsync();
                var s = c.GetStream();
                await s.ReadExactlyAsync(new byte[3]);
                await s.WriteAsync(new byte[] { 5, 2 });   // просит логин и пароль
            });
            var r = await ProxyChecker.CheckAsync($"socks=127.0.0.1:{port}", [("a.example", 443)]);
            await serve;
            Assert.False(r.Ok);
            Assert.Contains("логин и пароль", r.Summary);
        }
        finally { l.Stop(); }
    }

    [Fact]
    public async Task Bad_address_is_not_checked()
    {
        var r = await ProxyChecker.CheckAsync("не адрес");
        Assert.False(r.Ok);
        Assert.Empty(r.Probes);
    }
}

/// <summary>Команды помощника: строгая проверка аргументов, SID клиента, журнал помощника, откат.</summary>
public sealed class ProxyHelperTests : IDisposable
{
    readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    static HelperCall Parse(string json) => HelperCommands.Parse(json, new FakeHelperBackend());
    static string Rejected(string json) => Assert.Throws<HelperRequestException>(() => Parse(json)).Message;

    [Fact]
    public void Arguments_are_normalized_by_the_whitelist()
    {
        var c = Parse("""{"id":1,"cmd":"proxy_user_set","args":{"enabled":true,"server":" Proxy.Example.NET:3128","bypass":"<local>, *.lan","pac":"https://p.example/p.pac"}}""");
        Assert.Equal("proxy.example.net:3128", c.Str("server"));
        Assert.Equal("<local>;*.lan", c.Str("bypass"));
        var m = Parse("""{"id":2,"cmd":"proxy_machine_set","args":{"enabled":true,"server":"http=a.example:1;https=a.example:2","winhttp":true}}""");
        Assert.True(m.Bool("winhttp"));
        Assert.Null(m.ClientSid);
        Assert.Equal(new[] { "enabled", "server", "bypass", "pac", "winhttp" }, HelperCommands.Specs["proxy_machine_set"].Args.Select(a => a.Name).ToArray());
    }

    [Theory]
    [InlineData("""{"id":1,"cmd":"proxy_user_set","args":{"enabled":true}}""", "нужен адрес")]
    [InlineData("""{"id":1,"cmd":"proxy_user_set","args":{"enabled":false,"server":"127.0.0.1:80"}}""", "enabled = false")]
    [InlineData("""{"id":1,"cmd":"proxy_machine_set","args":{"enabled":false,"pac":"http://x.example/p.pac"}}""", "enabled = false")]
    [InlineData("""{"id":1,"cmd":"proxy_user_set","args":{"enabled":true,"server":"C:\\Windows\\x:80"}}""", "хост:порт")]
    [InlineData("""{"id":1,"cmd":"proxy_user_set","args":{"enabled":true,"server":"a.example:80;calc"}}""", "хост:порт")]
    [InlineData("""{"id":1,"cmd":"proxy_user_set","args":{"enabled":true,"server":"a.example:99999"}}""", "хост:порт")]
    [InlineData("""{"id":1,"cmd":"proxy_user_set","args":{"enabled":true,"pac":"file:///C:/x.pac"}}""", "http")]
    [InlineData("""{"id":1,"cmd":"proxy_user_set","args":{"enabled":true,"server":"a.example:80","bypass":"a\"b"}}""", "исключения")]
    [InlineData("""{"id":1,"cmd":"proxy_user_set","args":{"enabled":true,"server":"a.example:80","key":"HKLM\\SOFTWARE"}}""", "лишнее поле")]
    [InlineData("""{"id":1,"cmd":"proxy_user_set","args":{"enabled":true,"server":"a.example:80","sid":"S-1-5-18"}}""", "лишнее поле")]
    [InlineData("""{"id":1,"cmd":"proxy_user_set","args":{"enabled":"yes"}}""", "true или false")]
    [InlineData("""{"id":1,"cmd":"proxy_machine_set","args":{"enabled":true,"server":"a.example:80","winhttp":"1"}}""", "true или false")]
    [InlineData("""{"id":1,"cmd":"proxy_machine_restore","args":{"path":"x"}}""", "лишнее поле")]
    [InlineData("""{"id":1,"cmd":"proxy_status","args":{"sid":"x"}}""", "лишнее поле")]
    public void Bad_arguments_are_rejected(string json, string why) => Assert.Contains(why, Rejected(json), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Flags_status_reads_only_and_changes_are_exclusive()
    {
        Assert.False(HelperCommands.Specs["proxy_status"].Exclusive);
        foreach (var cmd in new[] { "proxy_user_set", "proxy_machine_set", "proxy_machine_restore" })
        {
            Assert.True(HelperCommands.Specs[cmd].Exclusive, cmd);
            Assert.False(HelperCommands.Specs[cmd].Long, cmd);
        }
        Assert.True(Version.Parse(HelperProtocol.Api) >= new Version(1, 7));
    }

    static string MySid() { using var me = WindowsIdentity.GetCurrent(); return me.User!.Value; }

    (Shell Shell, MemoryProxyRegistry Reg, MemoryWinHttpProxy Win, ShellHelperBackend Backend) Over()
    {
        var shell = new Shell(new AppPaths(_tmp.Sub("root"), Repo.Data));
        var reg = new MemoryProxyRegistry();
        var win = new MemoryWinHttpProxy();
        return (shell, reg, win, PcHelper.Over(shell, proxy: reg, winHttp: win));
    }

    [Fact]
    public async Task User_command_writes_to_the_channel_users_hive_not_to_an_argument()
    {
        var sid = MySid();
        Assert.True(ProxySid.IsUserSid(sid), "тест рассчитан на обычную учётную запись: " + sid);
        var (shell, reg, win, backend) = Over();
        reg.LoadedHives.Add(sid);
        await using var f = new HelperFixture(backend: backend);
        await using var c = await f.ConnectAsync();

        var r = await c.ProxyUserSetAsync(true, "127.0.0.1:7897", "<local>");
        Assert.True(r.Ok, r.Message);
        Assert.Equal("127.0.0.1:7897", reg.Values[(ProxyKey.UserSettings, sid, ProxyNames.Server)]);
        Assert.Equal(1, reg.Values[(ProxyKey.UserSettings, sid, ProxyNames.Enable)]);
        Assert.DoesNotContain(reg.Values.Keys, k => k.Item2 == "");   // HKCU помощника (SYSTEM) не тронут
        Assert.Equal("127.0.0.1:7897", r.Status.Now.User.Server);
        Assert.Equal(ChangeKinds.ProxyUserAdmin, Assert.Single(shell.Journal.Active()).Kind);

        // «Очистить»: выключено, адрес остался.
        var cleared = await c.ProxyUserSetAsync(false);
        Assert.True(cleared.Ok);
        Assert.Equal(0, reg.Values[(ProxyKey.UserSettings, sid, ProxyNames.Enable)]);
        Assert.False(cleared.Status.Now.User.IsOn);
    }

    [Fact]
    public async Task User_command_refuses_when_hive_is_not_loaded()
    {
        var (_, reg, _, backend) = Over();
        await using var f = new HelperFixture(backend: backend);
        await using var c = await f.ConnectAsync();
        var r = await c.ProxyUserSetAsync(true, "127.0.0.1:7897");
        Assert.False(r.Ok);
        Assert.Empty(reg.Values);
    }

    [Fact]
    public void Backend_refuses_user_command_without_a_user_sid()
    {
        var (_, reg, _, backend) = Over();
        foreach (var sid in new string?[] { null, "S-1-5-18", "S-1-5-32-544" })
        {
            var r = backend.ProxyUserSet(sid, true, "127.0.0.1:7897", null, null);
            Assert.False(r.Ok);
        }
        Assert.Empty(reg.Values);
    }

    [Fact]
    public async Task Machine_commands_clear_restore_and_undo_all_through_the_channel()
    {
        var (shell, reg, win, backend) = Over();
        await using var f = new HelperFixture(backend: backend);
        await using var c = await f.ConnectAsync();

        var on = await c.ProxyMachineSetAsync(true, "127.0.0.1:7897", "<local>", winhttp: true);
        Assert.True(on.Ok, on.Message);
        Assert.Equal("127.0.0.1:7897", reg.Values[(ProxyKey.MachineSettings, "", ProxyNames.Server)]);
        Assert.Equal(0, reg.Values[(ProxyKey.MachinePolicy, "", ProxyNames.PerUser)]);
        Assert.Equal("127.0.0.1:7897", win.Value.Proxy);
        Assert.True(on.Status.Now.MachineWide);
        Assert.Null(on.Status.Previous);

        var off = await c.ProxyMachineSetAsync(false, winhttp: true);
        Assert.True(off.Ok, off.Message);
        Assert.False(off.Status.Now.Machine.IsOn);
        Assert.True(win.Value.IsDirect);
        Assert.Equal("127.0.0.1:7897", off.Status.Previous!.Machine.Server);
        Assert.True(File.Exists(shell.Paths.ProxyBackup));   // «прежний» — у помощника
        Assert.Equal("127.0.0.1:7897", (await c.ProxyStatusAsync()).Previous!.Machine.Server);

        var back = await c.ProxyMachineRestoreAsync();
        Assert.True(back.Ok, back.Message);
        Assert.True(back.Status.Now.Machine.Enabled);
        Assert.Equal("127.0.0.1:7897", win.Value.Proxy);
        Assert.Null(back.Status.Previous);
        Assert.False((await c.ProxyMachineRestoreAsync()).Ok);

        // Откат всего: журнал помощника возвращает настройки, политику и WinHTTP.
        Assert.Contains(shell.Journal.Active(), e => e.Kind == ChangeKinds.ProxyMachine);
        var rep = await c.ChangesUndoAllAsync();
        Assert.All(rep.Items, i => Assert.Equal(UndoStatus.Restored, i.Status));
        Assert.False(ProxyRegistryIo.Read(reg, ProxyKey.MachineSettings).IsOn);
        Assert.Null(ProxyRegistryIo.PerUserPolicy(reg));
        Assert.True(win.Value.IsDirect);
    }

    [Fact]
    public async Task Status_is_read_only_and_reports_policy()
    {
        var (_, reg, _, backend) = Over();
        reg.Values[(ProxyKey.MachinePolicy, "", ProxyNames.PerUser)] = 0;
        reg.Values[(ProxyKey.MachineIePolicy, "", ProxyNames.IeConnectionsTab)] = 1;
        await using var f = new HelperFixture(backend: backend);
        await using var c = await f.ConnectAsync();
        var st = await c.ProxyStatusAsync();
        Assert.True(st.Now.MachineWide);
        Assert.True(st.Now.PolicyBlocked);
        Assert.Equal(0, reg.Writes);
    }
}

/// <summary>Вкладка «Прокси» без окна: список, три пути, «Очистить» / «Вернуть прежний», проверка, откат всего.</summary>
public sealed class ProxyViewModelTests : IDisposable
{
    readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    Shell NewShell() => new(new AppPaths(_tmp.Sub("root"), Repo.Data));

    (ProxyViewModel Vm, MemoryProxyRegistry Reg, NullProxyNotifier Note, ChangeJournal Journal, WindowSettings Settings) Make(HelperLink? link = null)
    {
        var shell = NewShell();
        var settings = TestWindow.Settings(shell);
        var reg = new MemoryProxyRegistry();
        var note = new NullProxyNotifier();
        var journal = new ChangeJournal(_tmp.Sub("changes-window.jsonl"));
        var vm = new ProxyViewModel(link ?? TestWindow.NoHelper(), settings) { Registry = reg, Notifier = note, Recorder = journal };
        return (vm, reg, note, journal, settings);
    }

    static void Add(ProxyViewModel vm, string name, string server, string bypass = "", string pac = "")
    {
        vm.EditName = name; vm.EditServer = server; vm.EditBypass = bypass; vm.EditPac = pac;
        vm.AddCmd.Execute(null);
    }

    [Fact]
    public void List_add_edit_delete_is_saved_to_window_settings()
    {
        var (vm, _, _, _, settings) = Make();
        Assert.True(vm.ListEmpty);
        Add(vm, "Clash Verge", " 127.0.0.1:7897 ", "<local>, *.lan");
        Assert.False(vm.HasFormError);
        var row = Assert.Single(vm.Items);
        Assert.Equal("127.0.0.1:7897", row.Address);
        Assert.Equal("<local>;*.lan", row.Entry.Bypass);

        var reloaded = WindowSettings.Load(settings.File);
        Assert.Equal(new ProxyEntry("Clash Verge", "127.0.0.1:7897", "<local>;*.lan", null), Assert.Single(reloaded.ProxyList));

        vm.Selected = row;
        Assert.Equal("Clash Verge", vm.EditName);
        vm.EditPac = "https://p.example/p.pac";
        vm.SaveCmd.Execute(null);
        Assert.Equal("https://p.example/p.pac", Assert.Single(WindowSettings.Load(settings.File).ProxyList).Pac);

        vm.DeleteCmd.Execute(null);
        Assert.Empty(vm.Items);
        Assert.Empty(WindowSettings.Load(settings.File).ProxyList);
        Assert.True(vm.ListEmpty);
    }

    [Fact]
    public void Form_mistakes_are_explained_and_nothing_is_added()
    {
        var (vm, _, _, _, _) = Make();
        Add(vm, "", "127.0.0.1:1");
        Assert.Contains("Название", vm.FormError);
        Add(vm, "A", "");
        Assert.Contains("адрес", vm.FormError, StringComparison.OrdinalIgnoreCase);
        Add(vm, "A", "not a proxy");
        Assert.Contains("хост:порт", vm.FormError);
        Add(vm, "A", "127.0.0.1:1", @"C:\x");
        Assert.Contains("Исключения", vm.FormError);
        Add(vm, "A", "127.0.0.1:1", "", "file:///x.pac");
        Assert.Contains("http", vm.FormError);
        Assert.Empty(vm.Items);
        Add(vm, "A", "127.0.0.1:1");
        Add(vm, "a", "127.0.0.1:2");   // то же название (без учёта регистра)
        Assert.Contains("уже есть", vm.FormError);
        Assert.Single(vm.Items);
    }

    [Fact]
    public async Task Enable_for_user_writes_hkcu_notifies_and_journals_in_window_journal()
    {
        var (vm, reg, note, journal, _) = Make();
        Add(vm, "Clash Verge", "127.0.0.1:7897", "<local>");
        await vm.EnableAsync(vm.Items[0]);
        Assert.Equal("127.0.0.1:7897", reg.Values[(ProxyKey.UserSettings, "", ProxyNames.Server)]);
        Assert.Equal(1, reg.Values[(ProxyKey.UserSettings, "", ProxyNames.Enable)]);
        Assert.Equal(1, note.Calls);
        Assert.Equal(ChangeKinds.ProxyUser, Assert.Single(journal.Active()).Kind);
        Assert.Contains("127.0.0.1:7897", vm.UserLine);
        Assert.DoesNotContain(reg.Values.Keys, k => k.Item1 is ProxyKey.MachineSettings or ProxyKey.MachinePolicy);
    }

    [Fact]
    public async Task Clear_remembers_previous_and_restore_returns_it()
    {
        var (vm, reg, _, _, settings) = Make();
        Add(vm, "Clash Verge", "127.0.0.1:7897", "<local>", "https://p.example/p.pac");
        await vm.EnableAsync(vm.Items[0]);
        Assert.False(vm.HasPrevious);

        await vm.ClearAsync();
        Assert.Equal(0, reg.Values[(ProxyKey.UserSettings, "", ProxyNames.Enable)]);
        Assert.DoesNotContain(reg.Values.Keys, k => k.Item3 == ProxyNames.Pac);
        Assert.NotNull(settings.ProxyPrevious);
        Assert.True(vm.HasPrevious);
        Assert.Contains("127.0.0.1:7897", vm.PreviousText);
        Assert.Equal("127.0.0.1:7897", WindowSettings.Load(settings.File).ProxyPrevious!.Server);   // хранится в настройках окна

        await vm.ClearAsync();   // уже выключено — прежний не затирается
        Assert.Equal("127.0.0.1:7897", settings.ProxyPrevious!.Server);

        await vm.RestoreAsync();
        var now = ProxyRegistryIo.Read(reg, ProxyKey.UserSettings);
        Assert.Equal(new ProxySettings(true, "127.0.0.1:7897", "<local>", "https://p.example/p.pac"), now);
        Assert.Null(settings.ProxyPrevious);
        Assert.False(vm.HasPrevious);
    }

    [Fact]
    public async Task Without_helper_machine_and_admin_paths_change_nothing()
    {
        var (vm, reg, _, journal, _) = Make();
        Add(vm, "Clash Verge", "127.0.0.1:7897");
        foreach (var t in new[] { ProxyTarget.UserAdmin, ProxyTarget.Machine })
        {
            vm.Target = t;
            await vm.EnableAsync(vm.Items[0]);
            await vm.ClearAsync();
        }
        Assert.Empty(reg.Values);
        Assert.Empty(journal.Entries());
    }

    [Fact]
    public async Task Admin_and_machine_paths_go_through_helper_commands()
    {
        await using var f = new HelperFixture();
        var link = await f.LinkAsync();
        var (vm, reg, note, _, settings) = Make(link);
        vm.Need = () => Task.FromResult(link.Client);
        vm.Confirm = _ => true;   // прокси на всю машину — с вопросом (HelperMachineTests)
        Add(vm, "Clash Verge", "127.0.0.1:7897", "<local>");

        vm.Target = ProxyTarget.UserAdmin;
        await vm.EnableAsync(vm.Items[0]);
        Assert.Contains("proxy_user_set True 127.0.0.1:7897 <local> ", f.Backend.Calls);
        Assert.Empty(reg.Values);                // окно само HKCU не писало — писал помощник
        Assert.True(note.Calls > 0);             // но систему уведомило
        Assert.True(f.Backend.ProxyUserValue.Enabled);
        await vm.ClearAsync();
        Assert.Contains("proxy_user_set False   ", f.Backend.Calls);
        Assert.False(f.Backend.ProxyUserValue.IsOn);

        vm.Target = ProxyTarget.Machine;
        vm.AlsoWinHttp = true;
        await vm.EnableAsync(vm.Items[0]);
        Assert.Contains("proxy_machine_set True 127.0.0.1:7897 <local>  True", f.Backend.Calls);
        Assert.True(f.Backend.ProxyMachineValue.Enabled);

        await vm.ClearAsync();
        Assert.Contains("proxy_machine_set False    True", f.Backend.Calls);
        Assert.Null(settings.ProxyPrevious);     // у машины «прежний» хранит помощник, не окно
        await vm.RefreshAsync();
        Assert.True(vm.HasPrevious);
        await vm.RestoreAsync();
        Assert.Contains("proxy_machine_restore", f.Backend.Calls);
        Assert.True(f.Backend.ProxyMachineValue.Enabled);
        Assert.Contains("127.0.0.1:7897 · исключения: <local>", vm.MachineLine);
    }

    [Fact]
    public async Task Policy_notes_and_hint_for_shared_settings()
    {
        var (vm, reg, _, _, _) = Make();
        reg.Values[(ProxyKey.MachinePolicy, "", ProxyNames.PerUser)] = 0;
        reg.Values[(ProxyKey.UserIePolicy, "", ProxyNames.IeProxy)] = 1;
        reg.Values[(ProxyKey.MachineSettings, "", ProxyNames.Server)] = "10.1.1.1:3128";
        reg.Values[(ProxyKey.MachineSettings, "", ProxyNames.Enable)] = 1;
        await vm.RefreshAsync();
        Assert.Equal(2, vm.Notes.Count);
        Assert.Contains(L.T("proxy.note.machine_wide"), vm.Notes);
        Assert.Contains(L.T("proxy.note.policy_blocked"), vm.Notes);
        Assert.Contains("10.1.1.1:3128", vm.MachineLine);

        string? said = null;
        vm.Say = s => said = s;
        Add(vm, "Clash", "127.0.0.1:7897");
        await vm.EnableAsync(vm.Items[0]);
        Assert.Equal(L.T("proxy.say.policy_suffix", new { message = L.T("proxy.out.user_on") }), said);   // для одного пользователя при общих настройках
    }

    [Fact]
    public async Task Line_shows_pac_autodetect_and_off()
    {
        var (vm, reg, _, _, _) = Make();
        await vm.RefreshAsync();
        Assert.Equal(L.T("proxy.off"), vm.UserLine);
        Assert.Equal(L.T("proxy.winhttp.direct"), vm.WinHttpLine);
        reg.Values[(ProxyKey.UserSettings, "", ProxyNames.Pac)] = "http://p.example/p.pac";
        var blob = new byte[24]; blob[8] = 0x0D;
        reg.Values[(ProxyKey.UserSettings, "", ProxyNames.ConnectionsBlob)] = blob;
        await vm.RefreshAsync();
        Assert.Contains(L.T("proxy.line.pac", new { value = "http://p.example/p.pac" }), vm.UserLine);
        Assert.Contains(L.T("proxy.line.autodetect"), vm.UserLine);
    }

    [Fact]
    public async Task Check_uses_selected_proxy_and_shows_result()
    {
        var (vm, _, _, _, _) = Make();
        string? asked = null;
        vm.CheckProxy = (s, _) => { asked = s; return Task.FromResult(new ProxyCheckResult(false, "http", [new("a.example:443", false, 10, "прокси отказал в соединении (не запущен?)")], "не отвечает: прокси отказал в соединении (не запущен?)")); };
        Assert.False(vm.CheckCmd.CanExecute(null));   // ничего не выбрано
        Add(vm, "Clash", "127.0.0.1:7897");
        vm.Selected = vm.Items[0];
        await vm.CheckAsync();
        Assert.Equal("127.0.0.1:7897", asked);
        Assert.Contains("Clash: не отвечает", vm.CheckText);
        Assert.Contains("a.example:443", vm.CheckText);
        Assert.False(vm.Checking);
    }

    [Fact]
    public async Task Window_changes_list_shows_proxy_and_undo_all_restores_it()
    {
        var shell = NewShell();
        var vm = TestWindow.Model(shell);
        var reg = new MemoryProxyRegistry();
        vm.Proxy.Registry = reg;
        vm.Proxy.EditName = "Clash"; vm.Proxy.EditServer = "127.0.0.1:7897";
        vm.Proxy.AddCmd.Execute(null);
        await vm.Proxy.EnableAsync(vm.Proxy.Items[0]);
        await vm.Changes.RefreshAsync();
        var row = Assert.Single(vm.Changes.Items);
        Assert.Contains("системный прокси", row.Title);
        Assert.True(vm.Changes.WindowEngine.Knows(ChangeKinds.ProxyUser));

        vm.Confirm = _ => true;
        vm.Changes.Confirm = _ => true;
        Assert.True(await vm.Changes.UndoAllAsync());
        Assert.Equal(ProxySettings.Off, ProxyRegistryIo.Read(reg, ProxyKey.UserSettings));
        Assert.Contains(vm.Changes.Report, r => r.Mark == "ok");
    }

    [Fact]
    public void Diagnostics_finding_links_to_the_proxy_tab()
    {
        var on = SystemChecks.ProxyFinding((true, "127.0.0.1:7897"));
        Assert.Equal(SystemFinding.GoProxy, on.Go);
        Assert.DoesNotContain("обход", on.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Null(SystemChecks.ProxyFinding((false, null)).Go);
        Assert.True(new FindingRow(false, on.Title, on.Text, on.Go).HasGo);
        Assert.False(new FindingRow(true, "Служба BFE", "работает").HasGo);

        var vm = TestWindow.Model(NewShell());
        vm.GoProxyCmd.Execute(null);
        Assert.Equal(Pages.Net, vm.Page);
        Assert.True(vm.ProxyBlockOpen);   // ссылка ведёт на «Сеть» с раскрытым блоком «Прокси»
        Assert.Equal(Pages.Net + 1, Pages.Diag);   // «Сеть» стоит перед «Диагностикой»
    }

    [Fact]
    public void Texts_have_no_banned_words_and_info_pair_exists()
    {
        var info = InfoTexts.Get(InfoTexts.Proxy);
        Assert.NotEmpty(info);
        Assert.DoesNotContain("обход", info, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("у всех", info, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ProxyViewModel.Example, "Clash Verge — 127.0.0.1:7897");
    }
}
