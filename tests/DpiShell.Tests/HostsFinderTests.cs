using System.Net;
using DpiShell.Core;

namespace DpiShell.Tests;

public sealed class HostsFinderTests
{
    [Fact]
    public void ParseHosts_KeepsNamesOnly()
    {
        var text = """
            # Набор адресов
            0.0.0.0 site.one
            0.0.0.0 site.two   # комментарий
            :: site.one
            127.0.0.1 localhost
            1.2.3.4 a.example.com b.example.com
            https://Site.Three/path?x=1
            2606:4700::1111 v6.example.org
            *.mask.com
            """;
        Assert.Equal(["site.one", "site.two", "a.example.com", "b.example.com", "site.three", "v6.example.org"], HostsInput.ParseHosts(text));
    }

    [Fact]
    public void DnsWire_ParsesAnswerWithCompression()
    {
        var q = DnsWire.Query("site.one", DnsWire.TypeA, 0x1234);
        // Ответ: заголовок с QR=1, тот же вопрос, две записи A со ссылкой на имя (0xC00C).
        var resp = new List<byte>(q);
        resp[2] = 0x81; resp[3] = 0x80; resp[7] = 2;
        foreach (var ip in new[] { new byte[] { 104, 16, 1, 1 }, new byte[] { 104, 16, 1, 2 } })
            resp.AddRange([0xC0, 0x0C, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4, .. ip]);
        Assert.Equal([IPAddress.Parse("104.16.1.1"), IPAddress.Parse("104.16.1.2")], DnsWire.Parse(resp.ToArray(), 0x1234));
        Assert.Throws<InvalidDataException>(() => DnsWire.Parse(resp.ToArray(), 0x9999));
    }

    [Fact]
    public void DnsWire_NxDomainIsEmpty()
    {
        var q = DnsWire.Query("nope.example", DnsWire.TypeA, 7);
        q[2] = 0x81; q[3] = 0x83;
        Assert.Empty(DnsWire.Parse(q, 7));
    }

    static readonly IPAddress Blocked = IPAddress.Parse("203.0.113.1"), Good = IPAddress.Parse("198.51.100.7"), Good2 = IPAddress.Parse("198.51.100.8");

    sealed class FakeDns(string name, Dictionary<string, IPAddress[]> map) : IDnsSource
    {
        public string Name { get; } = name;
        public List<string> Asked { get; } = [];
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct = default)
        {
            lock (Asked) Asked.Add(host);
            return Task.FromResult(map.TryGetValue(host, out var ips) ? ips : []);
        }
    }

    sealed class FakeProbe(params IPAddress[] working) : IEndpointProbe
    {
        public Task<EndpointCheck> CheckAsync(string host, IPAddress ip, CancellationToken ct = default) =>
            Task.FromResult(working.Contains(ip) ? new EndpointCheck(EndpointOutcome.Ok, 0.2, "HTTP 200") : new EndpointCheck(EndpointOutcome.Timeout, 5, "не отвечает"));
    }

    [Fact]
    public async Task FirstMode_AsksNextDnsOnlyForUnresolved()
    {
        var sys = new FakeDns("Система", new() { ["a.com"] = [Good], ["b.com"] = [IPAddress.Any], ["c.com"] = [Blocked] });
        var g = new FakeDns("Google UDP", new() { ["b.com"] = [Good2], ["c.com"] = [Blocked] });
        var c = new FakeDns("Comss DoH", new() { ["c.com"] = [Good] });
        var r = await new HostsFinder([sys, g, c], new FakeProbe(Good, Good2)).RunAsync(["a.com", "b.com", "c.com"], all: false);

        Assert.Equal(["b.com", "c.com"], g.Asked.Order());
        Assert.Equal(["c.com"], c.Asked);
        Assert.Equal(["a.com"], r.WorkAsIs.Select(d => d.Host));
        Assert.Equal([("b.com", Good2), ("c.com", Good)], r.HostsLines().Select(x => (x.Domain.Host, x.Address.Ip)));
        Assert.Contains("198.51.100.7 c.com", HostsFindText.Report(r));
    }

    [Fact]
    public async Task AllMode_PicksBestDnsAndHostsForRest()
    {
        var sys = new FakeDns("Система", new() { ["a.com"] = [Blocked], ["b.com"] = [Blocked], ["c.com"] = [Blocked] });
        var g = new FakeDns("Google UDP", new() { ["a.com"] = [Good], ["b.com"] = [Blocked], ["c.com"] = [Blocked] });
        var cf = new FakeDns("Cloudflare DoH", new() { ["a.com"] = [Good], ["b.com"] = [Good2], ["c.com"] = [Blocked] });
        var r = await new HostsFinder([sys, g, cf], new FakeProbe(Good, Good2)).RunAsync(["a.com", "b.com", "c.com"], all: true);

        Assert.Equal(3, g.Asked.Count);
        Assert.Equal("Cloudflare DoH", r.BestDns?.Source);
        Assert.Empty(r.HostsLines("Cloudflare DoH"));
        Assert.Equal(["c.com"], r.Unreachable.Select(d => d.Host));
        Assert.Equal(["Google UDP", "Cloudflare DoH"], r.Domains[0].Best!.Sources);
    }

    [Fact]
    public void DnsSources_ParseCustomList()
    {
        var list = DnsSources.Parse("system\n8.8.8.8 Google\nhttps://dns.example/dns-query Мой DoH  # коммент\n");
        Assert.Equal(["Система", "Google", "Мой DoH"], list.Select(s => s.Name));
        Assert.IsType<DohDnsSource>(list[2]);
        Assert.Throws<FormatException>(() => DnsSources.Parse("dns.google"));
    }
}
