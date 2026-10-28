using System.Net;
using System.Text;
using DpiShell.Core;

namespace DpiShell.Tests;

/// <summary>Разбор вставки из гайда и строгий разбор пары «адрес имя» (ТЗ 12).</summary>
public sealed class HostsPasteTests
{
    [Fact]
    public void Parse_KeepsPairs_DropsCommentsMasksPortsAndLocalhost()
    {
        var r = HostsPaste.Parse("""
            # Гайд: вставьте в hosts
            127.0.0.1 localhost
            ::1 localhost
            140.82.121.3   github.com   www.github.com   # два имени на строке
            0.0.0.0 ads.example.com
            0.0.0.0 ads.example.com
            1.2.3.4 *.wild.example.com
            1.2.3.4 host.example.com:8080
            1.2.3.4 https://url.example.com/path
            2606:4700::1111 one.one.one.one

            просто текст без адреса
            999.1.1.1 bad-ip.example.com
            """);
        Assert.Equal(
        [
            new HostsEntry("github.com", "140.82.121.3"), new HostsEntry("www.github.com", "140.82.121.3"),
            new HostsEntry("ads.example.com", "0.0.0.0"), new HostsEntry("one.one.one.one", "2606:4700::1111"),
        ], r.Entries);
        // Пропущены: два localhost (имя без точки), маска, имя с портом, URL, текст, неверный адрес.
        Assert.Equal(7, r.SkippedLines);
        Assert.False(r.Truncated);
    }

    [Fact]
    public void Parse_NormalizesCaseAndIp_AndHandlesCrLfAndTabs()
    {
        var r = HostsPaste.Parse("140.82.121.3\tGitHub.COM.\r\n  0000:0000:0000:0000:0000:0000:0000:0001   Foo.Example.com\r\n");
        Assert.Equal([new HostsEntry("github.com", "140.82.121.3"), new HostsEntry("foo.example.com", "::1")], r.Entries);
    }

    [Fact]
    public void Parse_Limit_ReportsTruncation()
    {
        var text = string.Join("\n", Enumerable.Range(0, HostsPaste.MaxEntries + 20).Select(i => $"1.2.3.4 h{i}.example.com"));
        var r = HostsPaste.Parse(text);
        Assert.Equal(HostsPaste.MaxEntries, r.Entries.Count);
        Assert.True(r.Truncated);
    }

    [Theory]
    [InlineData("1.2.3.4 github.com", false, true)]
    [InlineData("0.0.0.0 github.com", false, true)]
    [InlineData("0.0.0.0 github.com", true, false)]       // заглушка не публичный адрес — записать нельзя
    [InlineData("192.168.1.5 nas.example.com", true, false)]
    [InlineData("1.2.3.4 github.com extra", false, false)]  // ровно два слова
    [InlineData("1.2.3.4  github.com", false, false)]
    [InlineData("1.2.3.4 ..\\evil", false, false)]
    [InlineData("1.2.3.4 a.com:443", false, false)]
    [InlineData("1.2.3.4%5 a.com", false, false)]
    [InlineData("github.com 1.2.3.4", false, false)]
    public void TryEntry_IsStrict(string line, bool publicOnly, bool ok) => Assert.Equal(ok, HostsPaste.TryEntry(line, publicOnly, out _));
}

/// <summary>Вердикты примерки на подставных пробе, DNS и RDAP — без сети.</summary>
public sealed class HostsTryoutTests
{
    sealed class Probe(Dictionary<string, EndpointOutcome> map) : IEndpointProbe
    {
        public List<string> Calls { get; } = [];
        public Task<EndpointCheck> CheckAsync(string host, IPAddress ip, CancellationToken ct = default)
        {
            lock (Calls) Calls.Add($"{host}@{ip}");
            var o = map.TryGetValue($"{host}@{ip}", out var v) ? v : EndpointOutcome.NoConnect;
            return Task.FromResult(new EndpointCheck(o, 0.1, o == EndpointOutcome.Ok ? "HTTP 200" : o.ToString()));
        }
    }

    sealed class Dns(Dictionary<string, string[]> map) : IHostsDns
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct = default) =>
            Task.FromResult(map.TryGetValue(host, out var a) ? a.Select(IPAddress.Parse).ToArray() : []);
    }

    sealed class Rdap : IRdapClient
    {
        public Task<RdapOwner?> LookupAsync(IPAddress ip, CancellationToken ct = default) =>
            Task.FromResult<RdapOwner?>(ip.ToString() == "3.3.3.3" ? new RdapOwner("H", "ACME-NET", []) : null);
    }

    static HostsTryReport Run(string[] lines, Dictionary<string, EndpointOutcome> probe, Dictionary<string, string[]> dns, out Probe p)
    {
        p = new Probe(probe);
        var items = lines.Select(l => { HostsPaste.TryEntry(l, false, out var e); return new HostsTryItem(e.Host, e.Ip); }).ToList();
        return new HostsTryout(p, new Dns(dns), new Rdap()).TryAsync(items).GetAwaiter().GetResult();
    }

    [Fact]
    public void AllSixVerdicts()
    {
        var report = Run(
        [
            "3.3.3.3 fix.example.com",          // DNS не открывается, адрес из строки — да
            "5.5.5.5 fine.example.com",         // DNS и так открывается
            "7.7.7.7 was.example.com",          // DNS открывается, адрес из строки мёртв
            "0.0.0.0 stub.example.com",         // заглушка на рабочий сайт
            "9.9.9.9 gone.example.com",         // не работает ни то ни другое
            "8.8.8.8 fake.example.com",         // чужой сертификат
            "0.0.0.0 dead.example.com",         // заглушка на сайт, который и так не открывался
        ],
        new()
        {
            ["fix.example.com@3.3.3.3"] = EndpointOutcome.Ok,
            ["fine.example.com@4.4.4.4"] = EndpointOutcome.Ok, ["fine.example.com@5.5.5.5"] = EndpointOutcome.Ok,
            ["was.example.com@6.6.6.6"] = EndpointOutcome.Ok,
            ["stub.example.com@6.6.6.7"] = EndpointOutcome.Ok,
            ["fake.example.com@8.8.8.8"] = EndpointOutcome.BadCertificate,
        },
        new()
        {
            ["fix.example.com"] = ["2.2.2.2"], ["fine.example.com"] = ["4.4.4.4"], ["was.example.com"] = ["6.6.6.6"],
            ["stub.example.com"] = ["6.6.6.7"], ["gone.example.com"] = ["2.2.2.3"], ["fake.example.com"] = ["2.2.2.4"],
            ["dead.example.com"] = ["2.2.2.5"],
        }, out var probe);

        Assert.Equal(
        [
            HostsTryVerdict.Helps, HostsTryVerdict.NotNeeded, HostsTryVerdict.Breaks, HostsTryVerdict.Breaks,
            HostsTryVerdict.Useless, HostsTryVerdict.Dangerous, HostsTryVerdict.Blocks,
        ], report.Rows.Select(r => r.Verdict));
        Assert.Equal([report.Rows[0].Entry], report.Writable);              // записать можно только «Поможет»
        Assert.Equal("ACME-NET", report.Rows[0].OwnerName);                  // владелец адреса показан
        Assert.Equal(DnsState.Fails, report.Rows[0].Dns);
        Assert.Equal(DnsState.Works, report.Rows[1].Dns);
        // Заглушки наружу не проверяются: ни одного обращения к 0.0.0.0.
        Assert.DoesNotContain(probe.Calls, c => c.EndsWith("@0.0.0.0"));
    }

    [Fact]
    public void DnsSilent_StubIsBlock_AndRealAddressStillHelps()
    {
        var report = Run(["0.0.0.0 mute.example.com", "3.3.3.3 mute2.example.com"],
            new() { ["mute2.example.com@3.3.3.3"] = EndpointOutcome.Ok }, [], out _);
        Assert.Equal(HostsTryVerdict.Blocks, report.Rows[0].Verdict);
        Assert.Equal(DnsState.Unknown, report.Rows[0].Dns);
        Assert.Equal(HostsTryVerdict.Helps, report.Rows[1].Verdict);   // DNS молчит — адрес рабочий, сертификат верный
        Assert.Equal(DnsMatch.Unknown, report.Rows[1].Match);
    }

    [Fact]
    public void DnsReturnsStub_CountsAsNotWorking()
    {
        var report = Run(["3.3.3.3 poisoned.example.com"], new() { ["poisoned.example.com@3.3.3.3"] = EndpointOutcome.Ok },
            new() { ["poisoned.example.com"] = ["0.0.0.0"] }, out _);
        Assert.Equal(HostsTryVerdict.Helps, report.Rows[0].Verdict);
        Assert.Equal(DnsState.Fails, report.Rows[0].Dns);
        Assert.Contains("заглушку", report.Rows[0].DnsDetail);
    }

    [Fact]
    public void MatchWithDns_SameSameNetDiffers()
    {
        var ok = new Dictionary<string, EndpointOutcome>
        {
            ["a.example.com@1.1.1.1"] = EndpointOutcome.Ok, ["a.example.com@1.1.1.9"] = EndpointOutcome.Ok, ["a.example.com@1.1.2.9"] = EndpointOutcome.Ok,
        };
        var report = Run(["1.1.1.1 a.example.com", "1.1.1.9 a.example.com", "1.1.2.9 a.example.com"], ok,
            new() { ["a.example.com"] = ["1.1.1.1"] }, out _);
        Assert.Equal([DnsMatch.Same, DnsMatch.SameNet, DnsMatch.Differs], report.Rows.Select(r => r.Match));
    }

    [Fact]
    public void SameHost_IsResolvedOnce()
    {
        var report = Run(["3.3.3.3 many.example.com", "3.3.3.4 many.example.com"],
            new() { ["many.example.com@3.3.3.3"] = EndpointOutcome.Ok, ["many.example.com@2.2.2.2"] = EndpointOutcome.Ok },
            new() { ["many.example.com"] = ["2.2.2.2"] }, out var probe);
        Assert.Equal(2, report.Rows.Count);
        // DNS-адрес 2.2.2.2 проверен один раз на оба ряда.
        Assert.Single(probe.Calls, c => c == "many.example.com@2.2.2.2");
    }
}
