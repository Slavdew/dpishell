using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DpiShell.Core;

namespace DpiShell.Tests;

/// <summary>Файл hosts — только копия во временной папке; системный hosts тесты не трогают.</summary>
public sealed class HostsFileTests : IDisposable
{
    readonly TempDir _t = new("-hosts");
    readonly string _hosts;
    readonly HostsFileStore _store;
    static readonly DateTime At = new(2026, 10, 17);

    static readonly HostsEntry Gh = new("github.com", "140.82.121.3");
    static readonly HostsEntry Raw = new("raw.githubusercontent.com", "146.75.22.132");
    static readonly HostsEntry Tg = new("web.telegram.org", "149.154.167.220");

    public HostsFileTests()
    {
        _hosts = _t.Sub("etc\\hosts");
        Directory.CreateDirectory(Path.GetDirectoryName(_hosts)!);
        _store = new HostsFileStore(_hosts, _t.Sub("data\\hosts"));
    }

    public void Dispose()
    {
        if (File.Exists(_hosts)) File.SetAttributes(_hosts, FileAttributes.Normal);
        _t.Dispose();
    }

    const string Foreign = "# Copyright (c) 1993-2009 Microsoft Corp.\r\n#\r\n127.0.0.1 localhost\r\n0.0.0.0 ads.example.com\r\n";

    void Put(string text) => File.WriteAllBytes(_hosts, Encoding.Latin1.GetBytes(text));
    string Get() => Encoding.Latin1.GetString(File.ReadAllBytes(_hosts));

    [Fact]
    public void Insert_AppendsBlock_KeepsForeignBytes_AndCrlf()
    {
        Put(Foreign);
        var r = _store.Apply([Gh, Raw], At);

        Assert.True(r.Changed);
        Assert.Equal([Gh, Raw], r.Added);
        var text = Get();
        Assert.StartsWith(Foreign, text);
        Assert.Equal(Foreign +
            "# DpiShell begin v1 2026-10-17\r\n" +
            "# Managed by DpiShell (Fix addresses). Lines inside this block are rewritten automatically.\r\n" +
            "140.82.121.3 github.com  # dpishell\r\n146.75.22.132 raw.githubusercontent.com  # dpishell\r\n# DpiShell end\r\n", text);
        Assert.NotNull(r.Backup);
        Assert.Equal(Foreign, File.ReadAllText(r.Backup!));
        Assert.True(File.Exists(Path.Combine(_store.BackupDir, HostsFileStore.OriginalBackup)));
        Assert.False(File.Exists(_hosts + ".dpishell.tmp"));
    }

    [Fact]
    public void Insert_LfFile_StaysLf()
    {
        Put("127.0.0.1 localhost\n::1 localhost\n");
        _store.Apply([Gh], At);
        var text = Get();
        Assert.DoesNotContain("\r", text);
        Assert.StartsWith("127.0.0.1 localhost\n::1 localhost\n# DpiShell begin v1 2026-10-17\n", text);
        Assert.EndsWith("140.82.121.3 github.com  # dpishell\n# DpiShell end\n", text);
    }

    [Fact]
    public void Replace_KeepsPosition_AndForeignLinesAround()
    {
        Put(Foreign);
        _store.Apply([Gh, Raw], At);
        File.AppendAllText(_hosts, "10.0.0.5 nas.local\r\n");   // чужая строка после нашего блока

        var r = _store.Apply([Tg], At.AddDays(1));
        Assert.Equal([Tg], r.Added);
        Assert.Equal([Gh, Raw], r.Removed);
        Assert.Equal(Foreign +
            "# DpiShell begin v1 2026-10-18\r\n" +
            "# Managed by DpiShell (Fix addresses). Lines inside this block are rewritten automatically.\r\n" +
            "149.154.167.220 web.telegram.org  # dpishell\r\n# DpiShell end\r\n" +
            "10.0.0.5 nas.local\r\n", Get());
        Assert.Single(Regex(Get(), "# DpiShell begin"));
    }

    static string[] Regex(string s, string what) => s.Split('\n').Where(l => l.StartsWith(what)).ToArray();

    [Fact]
    public void SameEntries_NoWrite_NoBackup()
    {
        Put(Foreign);
        _store.Apply([Gh], At);
        var before = File.ReadAllBytes(_hosts);
        var backups = Directory.GetFiles(_store.BackupDir).Length;

        var r = _store.Apply([Gh], At.AddDays(3));
        Assert.False(r.Changed);
        Assert.Equal(before, File.ReadAllBytes(_hosts));
        Assert.Equal(backups, Directory.GetFiles(_store.BackupDir).Length);
    }

    [Fact]
    public void Remove_RestoresOriginalBytes_EmptyResult_NoBlock()
    {
        Put(Foreign);
        _store.Apply([Gh, Tg], At);
        var r = _store.RemoveBlock();
        Assert.True(r.Changed);
        Assert.Equal([Gh, Tg], r.Removed);
        Assert.Equal(Foreign, Get());

        // «Нечего чинить» на чистом файле — файл не трогаем вовсе.
        Assert.False(_store.Apply([], At).Changed);
        Assert.Equal(Foreign, Get());
    }

    [Fact]
    public void DamagedBlock_BeginWithoutEnd_RemovesOnlyOurLines()
    {
        Put("127.0.0.1 localhost\r\n# DpiShell begin v1 2026-10-01\r\n140.82.121.3 github.com\r\n0.0.0.0 youtube.com\r\n# user\r\n");
        var block = _store.Read().Block();
        Assert.True(block.Present);
        Assert.True(block.Damaged);
        Assert.Equal([Gh], block.Entries);

        _store.RemoveBlock();
        Assert.Equal("127.0.0.1 localhost\r\n0.0.0.0 youtube.com\r\n# user\r\n", Get());
    }

    [Fact]
    public void DamagedBlock_StrayEnd_AndTwoBlocks_CollapseToOne()
    {
        Put("# DpiShell end\r\n1.1.1.1 a\r\n# DpiShell begin v1 x\r\n140.82.121.3 github.com\r\n# DpiShell end\r\n2.2.2.2 b\r\n# DpiShell begin v1 y\r\n149.154.167.220 web.telegram.org\r\n# DpiShell end\r\n");
        Assert.True(_store.Read().Block().Damaged);
        _store.Apply([Raw], At);
        var text = Get();
        Assert.Single(Regex(text, "# DpiShell begin"));
        Assert.Single(Regex(text, "# DpiShell end"));
        Assert.StartsWith("# DpiShell begin v1 2026-10-17\r\n", text);   // на месте первой нашей строки
        Assert.EndsWith("146.75.22.132 raw.githubusercontent.com  # dpishell\r\n# DpiShell end\r\n1.1.1.1 a\r\n2.2.2.2 b\r\n", text);
        Assert.DoesNotContain("github.com  # dpishell\r\n", text.Replace("raw.githubusercontent.com", ""));
    }

    // ---------- п. 77: наши строки помечены в самом hosts, «не от DpiShell» — только то, что без пометки ----------

    [Fact]
    public void TaggedLine_OutsideBlock_IsOurs_NotConflict_RemovedOnRestore()
    {
        Put(Foreign);
        _store.Apply([Gh, Raw], At);
        // Строку блока перенесли в начало файла (руками или другой программой) — она всё равно наша.
        var text = Get().Replace("140.82.121.3 github.com  # dpishell\r\n", "");
        Put("140.82.121.3 github.com  # dpishell\r\n" + text);
        var h = _store.Read();
        Assert.DoesNotContain(h.Conflicts(), c => c.Host == "github.com");
        Assert.DoesNotContain(h.ForeignLines(), l => l.Text.Contains("github.com"));
        Assert.Contains(Gh, h.Block().Entries);
        Assert.True(h.Block().Damaged);   // блок соберётся заново при записи

        _store.Apply([Gh, Raw], At);
        Assert.Single(Get().Split('\n'), l => l.Contains(" github.com"));
        _store.RemoveBlock();
        Assert.Equal(Foreign, Get());
    }

    [Fact]
    public void OwnLine_ByHand_WithoutTag_IsNotOurs()
    {
        Put(Foreign + "140.82.121.4 github.com\r\n");
        _store.Apply([Raw], At);
        var h = _store.Read();
        Assert.Contains(h.Conflicts(), c => c.Host == "github.com" && c.Ip == "140.82.121.4");
        Assert.DoesNotContain("140.82.121.4 github.com  # dpishell", Get());   // чужое в файле не помечаем
    }

    [Fact]
    public void OldBlock_WithoutTags_IsIntact_NotRewritten()
    {
        var old = Foreign + "# DpiShell begin v1 2026-10-01\r\n# Managed by DpiShell (Fix addresses). Lines inside this block are rewritten automatically.\r\n" +
            "140.82.121.3 github.com\r\n# DpiShell end\r\n";
        Put(old);
        Assert.False(_store.Read().Block().Damaged);
        Assert.False(_store.Apply([Gh], At).Changed);   // ради пометок hosts не переписываем
        Assert.Equal(old, Get());
        _store.Apply([Gh, Raw], At);   // со следующей правкой — с пометками
        Assert.Contains("140.82.121.3 github.com  # dpishell\r\n", Get());
    }

    [Fact]
    public void NoTrailingNewline_AndMissingFile()
    {
        Put("127.0.0.1 localhost");
        _store.Apply([Gh], At);
        Assert.StartsWith("127.0.0.1 localhost\r\n# DpiShell begin", Get());

        File.Delete(_hosts);
        var r = _store.Apply([Gh], At);
        Assert.True(r.Changed);
        Assert.Null(r.Backup);
        Assert.StartsWith("# DpiShell begin v1 2026-10-17\r\n", Get());
    }

    [Fact]
    public void Utf8Bom_AndNonAsciiForeignBytes_PreservedExactly()
    {
        var cp1251 = new byte[] { 0x23, 0x20, 0xCF, 0xF0, 0xE8, 0xE2, 0xE5, 0xF2, 0x0D, 0x0A };   // «# Привет» в cp1251
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var head = (byte[])[.. bom, .. Encoding.UTF8.GetBytes("# Комментарий\r\n"), .. cp1251];
        File.WriteAllBytes(_hosts, head);
        _store.Apply([Gh], At);
        var after = File.ReadAllBytes(_hosts);
        Assert.Equal(head, after[..head.Length]);
        _store.RemoveBlock();
        Assert.Equal(head, File.ReadAllBytes(_hosts));
    }

    [Fact]
    public void Preview_DoesNotWrite_AndReportsForeignConflicts()
    {
        Put(Foreign + "1.2.3.4 discord.com\r\n");
        var before = File.ReadAllBytes(_hosts);
        var p = _store.Preview([Gh, new HostsEntry("discord.com", "162.159.138.232")], At);
        Assert.True(p.Changed);
        Assert.Equal(2, p.Added.Count);
        Assert.Equal(5, p.BlockLines.Count);   // begin, пометка, 2 записи, end
        var c = Assert.Single(p.Conflicts);
        Assert.Equal(("discord.com", "1.2.3.4", 5), (c.Host, c.Ip, c.Line));
        Assert.Equal(before, File.ReadAllBytes(_hosts));
        Assert.False(Directory.Exists(_store.BackupDir));
    }

    [Fact]
    public void ReadOnlyAttribute_ClearedForWrite_AndRestored()
    {
        Put(Foreign);
        File.SetAttributes(_hosts, FileAttributes.ReadOnly);
        _store.Apply([Gh], At);
        Assert.Contains("github.com", Get());
        Assert.True(File.GetAttributes(_hosts).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public void LockedFile_FailsWithClearText_FileUntouched_NoTemp()
    {
        Put(Foreign);
        using (new FileStream(_hosts, FileMode.Open, FileAccess.Read, FileShare.Read))   // как антивирус, держащий файл
        {
            var e = Assert.Throws<HostsWriteException>(() => _store.Apply([Gh], At));
            Assert.Contains("Изменения не внесены", e.Message);
            Assert.Contains("занят", e.Message);
            Assert.NotNull(e.Backup);
        }
        Assert.Equal(Foreign, Get());
        Assert.False(File.Exists(_hosts + ".dpishell.tmp"));
    }

    [Fact]
    public void Backups_KeepTenPlusOriginal_RestoreOnRequest()
    {
        Put(Foreign);
        for (int i = 0; i < 12; i++) _store.Apply([i % 2 == 0 ? Gh : Tg], At);
        var list = _store.Backups();
        Assert.Equal(HostsFileStore.KeepBackups + 1, list.Count);
        Assert.EndsWith(HostsFileStore.OriginalBackup, list[^1]);

        var original = list[^1];
        var (added, removed) = _store.DiffWithBackup(original);
        Assert.Empty(added);
        Assert.Contains(removed, l => l.Contains("github.com") || l.Contains("telegram"));
        _store.RestoreBackup(original);
        Assert.Equal(Foreign, Get());
        Assert.Throws<ArgumentException>(() => _store.RestoreBackup(_hosts));
    }
}

public sealed class HostsCandidatesTests
{
    [Fact]
    public void Seed_HashMatches_AllInWhitelist()
    {
        using var t = new TempDir();
        var list = HostsCandidates.LoadSeed(Make.Paths(t));
        Assert.Equal(56, list.Count);
        Assert.All(list, c => Assert.True(HostsRules.IsAllowedHost(c.Host), c.Host));
        Assert.Contains(list, c => c.Host == "raw.githubusercontent.com" && c.Ips.Contains("2606:50c0:8000::154") && c.OwnerAnyOf.Contains("FASTLY"));
    }

    [Theory]
    [InlineData("github.com", true)]
    [InlineData("GitHub.com.", true)]
    [InlineData("raw.githubusercontent.com", true)]
    [InlineData("zws4-1.web.telegram.org", true)]
    [InlineData("oauth.tg.dev", true)]
    [InlineData("updates.discord.com", true)]
    [InlineData("githubusercontent.com", false)]
    [InlineData("evilgithub.com", false)]
    [InlineData("raw.githubusercontent.com.evil.ru", false)]
    [InlineData("cdn.discord.com", false)]
    [InlineData("youtube.com", false)]
    [InlineData("www.microsoft.com", false)]
    [InlineData("a..githubusercontent.com", false)]
    public void Whitelist(string host, bool ok) => Assert.Equal(ok, HostsRules.IsAllowedHost(host));

    [Theory]
    [InlineData("140.82.121.3", true)]
    [InlineData("2606:50c0:8000::154", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("192.168.0.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:140.82.121.3", false)]
    public void PublicAddress(string ip, bool ok) => Assert.Equal(ok, HostsRules.TryParsePublic(ip, out _));

    [Fact]
    public void Parse_RejectsForeignDomainOrPrivateIp()
    {
        const string bad = """{"version":1,"source":"x","source_sha256":"x","candidates":[{"host":"youtube.com","ips":["1.2.3.4"],"owner_any_of":["X"],"reason":"r","since":"s","source":"s"},{"host":"github.com","ips":["192.168.1.1"],"owner_any_of":["GITHU"],"reason":"r","since":"s","source":"s"}]}""";
        var e = Assert.Throws<InvalidDataException>(() => HostsCandidates.Parse(bad));
        Assert.Contains("youtube.com", e.Message);
        Assert.Contains("192.168.1.1", e.Message);
    }

    [Fact]
    public void Hash_MismatchThrows_CrlfSameAsLf()
    {
        using var t = new TempDir();
        var src = File.ReadAllText(Path.Combine(Repo.Data, "hosts", HostsCandidates.FileName));
        var crlf = t.Sub("c.json");
        File.WriteAllText(crlf, src.Replace("\r\n", "\n").Replace("\n", "\r\n"));
        var hash = File.ReadAllText(Path.Combine(Repo.Data, "hosts", HostsCandidates.HashFileName)).Split(' ')[0];
        Assert.NotEmpty(HostsCandidates.Load(crlf, hash));

        File.WriteAllText(crlf, src.Replace("140.82.121.3", "140.82.121.4"));
        Assert.Throws<InvalidDataException>(() => HostsCandidates.Load(crlf, hash));
    }

    [Fact]
    public void Rdap_Parse_AndMatch()
    {
        const string json = """{"handle":"NET-162-158-0-0-1","name":"CLOUDFLARENET","entities":[{"handle":"CLOUD14","roles":["registrant"]}]}""";
        var o = RdapClient.Parse(json)!;
        Assert.True(o.Matches(["CLOUDFLARE"]));
        Assert.True(o.Matches(["cloud14"]));
        Assert.False(o.Matches(["TELEGRAM"]));
        Assert.Null(RdapClient.Parse("{}"));
    }
}

/// <summary>Логика «чинит / не чинит» на подставных пробе, DNS и RDAP — без сети.</summary>
public sealed class HostsVerifierTests
{
    sealed class FakeProbe(Dictionary<string, EndpointOutcome> map) : IEndpointProbe
    {
        public List<string> Calls { get; } = [];
        public Task<EndpointCheck> CheckAsync(string host, IPAddress ip, CancellationToken ct = default)
        {
            lock (Calls) Calls.Add($"{host}@{ip}");
            var o = map.TryGetValue($"{host}@{ip}", out var v) ? v : EndpointOutcome.NoConnect;
            return Task.FromResult(new EndpointCheck(o, 0.1, o.ToString()));
        }
    }

    sealed class FakeDns(Dictionary<string, string[]> map) : IHostsDns
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct = default) =>
            Task.FromResult(map.TryGetValue(host, out var a) ? a.Select(IPAddress.Parse).ToArray() : []);
    }

    sealed class FakeRdap(Dictionary<string, string?> map) : IRdapClient
    {
        public Task<RdapOwner?> LookupAsync(IPAddress ip, CancellationToken ct = default) =>
            Task.FromResult(map.TryGetValue(ip.ToString(), out var n) && n is not null ? new RdapOwner(n, n, []) : null);
    }

    static HostsCandidate C(string host, string[] ips, params string[] owners) => new(host, [.. ips], [.. owners], "r", "s", "s");

    [Fact]
    public async Task Verdicts()
    {
        var probe = new FakeProbe(new()
        {
            ["github.com@140.82.121.3"] = EndpointOutcome.Ok,                // кандидат работает, DNS-адрес — нет → ставим
            ["github.com@185.199.108.133"] = EndpointOutcome.Timeout,
            ["discord.com@162.159.138.232"] = EndpointOutcome.Ok,            // кандидат работает, но и DNS работает → не нужно
            ["discord.com@162.159.136.232"] = EndpointOutcome.Ok,
            ["web.telegram.org@149.154.167.220"] = EndpointOutcome.BadCertificate,
            ["t.me@149.154.167.220"] = EndpointOutcome.Ok,                   // владелец не тот → не ставим
            ["telegram.org@149.154.167.220"] = EndpointOutcome.Ok,           // RDAP молчит, DoH молчит → ставим
        });
        var dns = new FakeDns(new()
        {
            ["github.com"] = ["185.199.108.133"],
            ["discord.com"] = ["162.159.136.232"],
            ["web.telegram.org"] = ["149.154.167.99"],
            ["t.me"] = ["149.154.167.99"],
        });
        var rdap = new FakeRdap(new() { ["140.82.121.3"] = "GITHU", ["162.159.138.232"] = "CLOUDFLARENET", ["149.154.167.220"] = null });
        var t_me_rdap = new FakeRdap(new() { ["149.154.167.220"] = "EVIL-NET" });

        var report = await new HostsVerifier(probe, dns, rdap).CheckAsync(
        [
            C("github.com", ["140.82.121.3"], "GITHU"),
            C("discord.com", ["162.159.138.232"], "CLOUDFLARE"),
            C("web.telegram.org", ["149.154.167.220"], "TELEGRAM"),
            C("telegram.org", ["149.154.167.220"], "TELEGRAM"),
            C("youtube.com", ["1.2.3.4"], "GOOGLE"),
            C("github.com", ["10.0.0.1"], "GITHU"),
        ]);
        HostsVerdict V(string host, string ip) => report.Rows.Single(r => r.Host == host && r.Ip == ip).Verdict;

        Assert.Equal(HostsVerdict.Install, V("github.com", "140.82.121.3"));
        Assert.Equal(DnsState.Fails, report.Rows.First(r => r.Host == "github.com").Dns);
        Assert.Equal(OwnerState.Match, report.Rows.First(r => r.Host == "github.com").Owner);
        Assert.Equal(HostsVerdict.NotNeeded, V("discord.com", "162.159.138.232"));
        Assert.Equal(HostsVerdict.BadCertificate, V("web.telegram.org", "149.154.167.220"));
        Assert.Equal(HostsVerdict.Install, V("telegram.org", "149.154.167.220"));
        Assert.Equal(DnsState.Unknown, report.Rows.Single(r => r.Host == "telegram.org").Dns);
        Assert.Equal(HostsVerdict.Rejected, V("youtube.com", "1.2.3.4"));
        Assert.Equal(HostsVerdict.Rejected, V("github.com", "10.0.0.1"));
        Assert.DoesNotContain(probe.Calls, c => c.StartsWith("youtube.com") || c.Contains("10.0.0.1"));
        Assert.Equal([new HostsEntry("github.com", "140.82.121.3"), new HostsEntry("telegram.org", "149.154.167.220")], report.ToInstall);

        var mismatch = await new HostsVerifier(probe, dns, t_me_rdap).CheckAsync([C("t.me", ["149.154.167.220"], "TELEGRAM")]);
        Assert.Equal(HostsVerdict.OwnerMismatch, mismatch.Rows.Single().Verdict);
        Assert.True(mismatch.NothingToFix);
    }

    [Fact]
    public async Task Manager_ApplyRecheckRevert_OnTempCopy()
    {
        using var t = new TempDir("-hm");
        var hosts = t.Sub("hosts");
        File.WriteAllText(hosts, "127.0.0.1 localhost\r\n");
        var outcomes = new Dictionary<string, EndpointOutcome>
        {
            ["github.com@140.82.121.3"] = EndpointOutcome.Ok,
            ["raw.githubusercontent.com@146.75.22.132"] = EndpointOutcome.Ok,
        };
        var probe = new FakeProbe(outcomes);
        var dns = new FakeDns(new() { ["github.com"] = ["185.199.108.133"], ["raw.githubusercontent.com"] = ["185.199.108.133"] });
        var rdap = new FakeRdap(new() { ["140.82.121.3"] = "GITHU", ["146.75.22.132"] = "FASTLY" });
        var cands = new List<HostsCandidate>
        {
            C("github.com", ["140.82.121.3"], "GITHU"),
            C("raw.githubusercontent.com", ["146.75.22.132", "2606:50c0:8000::154"], "FASTLY", "GITHU"),
        };
        var m = new HostsManager(Make.Paths(t), hosts, probe, dns, rdap, () => cands);

        var report = await m.CheckAsync();
        Assert.Equal(2, report.ToInstall.Count);   // IPv6 не ответил — не ставим
        Assert.Equal(2, m.Preview(report.ToInstall).Added.Count);
        Assert.DoesNotContain("DpiShell", File.ReadAllText(hosts));   // предпросмотр не пишет

        m.Apply(report);
        Assert.Equal(2, m.Status().Block.Entries.Count);
        Assert.Equal(2, m.Status().Applied.Count);

        // Через сутки github.com на старом адресе не отвечает — перепроверка снимает его, raw остаётся.
        outcomes["github.com@140.82.121.3"] = EndpointOutcome.Timeout;
        var re = await m.RecheckAsync();
        Assert.Equal([new HostsEntry("raw.githubusercontent.com", "146.75.22.132")], re.Kept);
        Assert.Equal("github.com", Assert.Single(re.Removed).Host);
        Assert.DoesNotContain("140.82.121.3", File.ReadAllText(hosts));

        // Блок пропал не по нашей воле (Защитник) — статус это видит, перепроверка сама не возвращает.
        File.WriteAllText(hosts, "127.0.0.1 localhost\r\n");
        Assert.True(m.Status().BlockLost);
        Assert.True((await m.RecheckAsync()).BlockLost);
        Assert.DoesNotContain("DpiShell", File.ReadAllText(hosts));

        m.Apply(await m.CheckAsync());
        m.Revert();
        Assert.Equal("127.0.0.1 localhost\r\n", File.ReadAllText(hosts));
        Assert.False(m.Status().BlockLost);
    }

    // ---------- аудит уведомлений №6: перепроверка без сети ничего не меняет ----------

    sealed class FlakyProbe(Func<string, int, EndpointOutcome> outcome) : IEndpointProbe
    {
        readonly Dictionary<string, int> _n = [];
        public Task<EndpointCheck> CheckAsync(string host, IPAddress ip, CancellationToken ct = default)
        {
            var key = $"{host}@{ip}";
            int n;
            lock (_n) { _n[key] = n = _n.GetValueOrDefault(key) + 1; }
            // По адресу из DNS домен не открывается (потому запись и нужна); открывается только по адресам кандидатов.
            var o = ip.ToString() is "140.82.121.3" or "146.75.22.132" ? outcome(key, n) : EndpointOutcome.NoConnect;
            return Task.FromResult(new EndpointCheck(o, 0.1, o.ToString()));
        }
    }

    async Task<(HostsManager M, string Hosts)> TwoHostBlock(TempDir t, IEndpointProbe probe, Func<CancellationToken, Task<bool>>? up = null)
    {
        var hosts = t.Sub("hosts");
        File.WriteAllText(hosts, "127.0.0.1 localhost\r\n");
        var dns = new FakeDns(new() { ["github.com"] = ["185.199.108.133"], ["raw.githubusercontent.com"] = ["185.199.108.133"] });
        var rdap = new FakeRdap(new() { ["140.82.121.3"] = "GITHU", ["146.75.22.132"] = "FASTLY" });
        var cands = new List<HostsCandidate> { C("github.com", ["140.82.121.3"], "GITHU"), C("raw.githubusercontent.com", ["146.75.22.132"], "FASTLY", "GITHU") };
        var m = new HostsManager(Make.Paths(t), hosts, probe, dns, rdap, () => cands);
        m.Apply(await m.CheckAsync());
        Assert.Equal(2, m.Status().Block.Entries.Count);
        if (up is not null) m.NetworkUp = up;
        return (m, hosts);
    }

    [Fact]
    public async Task Recheck_without_network_changes_nothing_and_says_it_was_skipped()
    {
        using var t = new TempDir("-hm");
        bool online = true;
        var (m, hosts) = await TwoHostBlock(t, new FlakyProbe((_, _) => online ? EndpointOutcome.Ok : EndpointOutcome.Timeout), _ => Task.FromResult(online));
        var before = File.ReadAllText(hosts);
        online = false;   // сеть пропала: все пробы «не отвечают»
        var r = await m.RecheckAsync();
        Assert.True(m.LastRecheckSkipped);
        Assert.Empty(r.Removed);
        Assert.False(r.BlockLost);
        Assert.Equal(before, File.ReadAllText(hosts));
        Assert.Equal(2, m.Status().Block.Entries.Count);
    }

    [Fact]
    public async Task Unreachable_address_is_removed_only_when_it_fails_twice_with_network_up()
    {
        using var t = new TempDir("-hm");
        // github.com при перепроверке: первая проба не прошла, повторная — прошла (случайный сбой) — запись остаётся.
        var (m, _) = await TwoHostBlock(t, new FlakyProbe((key, n) => key.StartsWith("github.com") && n == 2 ? EndpointOutcome.Timeout : EndpointOutcome.Ok));
        var r = await m.RecheckAsync();
        Assert.False(m.LastRecheckSkipped);
        Assert.Empty(r.Removed);
        Assert.Equal(2, m.Status().Block.Entries.Count);

        // Не прошла дважды подряд при живой сети — адрес устарел.
        using var t2 = new TempDir("-hm");
        var (m2, hosts2) = await TwoHostBlock(t2, new FlakyProbe((key, n) => key.StartsWith("github.com") && n >= 2 ? EndpointOutcome.Timeout : EndpointOutcome.Ok));
        var r2 = await m2.RecheckAsync();
        Assert.Equal("github.com", Assert.Single(r2.Removed).Host);
        Assert.DoesNotContain("140.82.121.3", File.ReadAllText(hosts2));
    }

    [Fact]
    public async Task Network_that_disappears_between_the_two_probes_keeps_the_record()
    {
        using var t = new TempDir("-hm");
        int asked = 0;
        var (m, hosts) = await TwoHostBlock(t, new FlakyProbe((key, n) => n >= 2 ? EndpointOutcome.Timeout : EndpointOutcome.Ok), _ => Task.FromResult(Interlocked.Increment(ref asked) <= 1));
        var r = await m.RecheckAsync();
        Assert.Empty(r.Removed);
        Assert.Contains("140.82.121.3", File.ReadAllText(hosts));
        Assert.Contains("146.75.22.132", File.ReadAllText(hosts));
    }
}

/// <summary>Живая проверка на локальном TLS-сервере с самоподписанным сертификатом: должна отклонить.</summary>
public sealed class TlsEndpointProbeTests
{
    static X509Certificate2 SelfSigned(string name)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(name);
        req.CertificateExtensions.Add(san.Build());
        using var cert = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);   // SChannel хочет ключ из PFX
    }

    static async Task<(int Port, Func<Task> Stop)> ServeAsync(X509Certificate2 cert)
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var cts = new CancellationTokenSource();
        var loop = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await l.AcceptTcpClientAsync(cts.Token); } catch { break; }
                _ = Task.Run(async () =>
                {
                    using (c)
                    using (var ssl = new SslStream(c.GetStream()))
                    {
                        try
                        {
                            await ssl.AuthenticateAsServerAsync(cert);
                            await ssl.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
                        }
                        catch { }
                    }
                });
            }
        });
        await Task.Yield();
        return (((IPEndPoint)l.LocalEndpoint).Port, async () => { cts.Cancel(); l.Stop(); try { await loop; } catch { } });
    }

    [Fact]
    public async Task SelfSignedCertificate_ForSameName_Rejected()
    {
        using var cert = SelfSigned("dpishell.test");
        var (port, stop) = await ServeAsync(cert);
        try
        {
            var r = await new TlsEndpointProbe { Port = port }.CheckAsync("dpishell.test", IPAddress.Loopback);
            Assert.Equal(EndpointOutcome.BadCertificate, r.Outcome);
            Assert.Equal("сертификат не прошёл проверку", r.Detail);
        }
        finally { await stop(); }
    }

    [Fact]
    public async Task ForeignCertificate_Rejected()
    {
        using var cert = SelfSigned("other.test");
        var (port, stop) = await ServeAsync(cert);
        try
        {
            var r = await new TlsEndpointProbe { Port = port }.CheckAsync("dpishell.test", IPAddress.Loopback);
            Assert.Equal(EndpointOutcome.BadCertificate, r.Outcome);
            Assert.Equal("чужой сертификат", r.Detail);
        }
        finally { await stop(); }
    }

    [Fact]
    public async Task ClosedPort_NoConnect()
    {
        var r = await new TlsEndpointProbe { Port = RawHttpServer.ClosedPort() }.CheckAsync("dpishell.test", IPAddress.Loopback);
        Assert.Equal(EndpointOutcome.NoConnect, r.Outcome);
        Assert.False(r.Ok);
    }
}
