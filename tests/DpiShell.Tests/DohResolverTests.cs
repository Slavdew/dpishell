using System.Net;
using DpiShell.Core;

namespace DpiShell.Tests;

public sealed class DohResolverTests
{
    [Fact]
    public void Parse_GoogleJson_OnlyARecords_SkipsCname()
    {
        const string json = """
            {"Status":0,"Answer":[
              {"name":"www.youtube.com.","type":5,"TTL":300,"data":"youtube-ui.l.google.com."},
              {"name":"youtube-ui.l.google.com.","type":1,"TTL":300,"data":"142.251.152.4"},
              {"name":"youtube-ui.l.google.com.","type":1,"TTL":300,"data":"142.251.155.4"}]}
            """;
        Assert.Equal(["142.251.152.4", "142.251.155.4"], DohResolver.Parse(json).Select(a => a.ToString()));
    }

    [Fact]
    public void Parse_NxDomain_Empty() =>
        Assert.Empty(DohResolver.Parse("""{"Status":3,"Authority":[{"name":"x.","type":6,"data":"a"}]}"""));

    [Theory]
    [InlineData(new string[0], false)]                             // система не ответила (старт, сон, смена сети) — не подмена
    [InlineData(new[] { "127.0.0.1" }, true)]                       // заглушка
    [InlineData(new[] { "0.0.0.0" }, true)]
    [InlineData(new[] { "10.10.34.36" }, true)]                     // страница-заглушка в частной сети
    [InlineData(new[] { "142.251.150.4" }, false)]                  // другой адрес CDN — не подмена
    [InlineData(new[] { "142.251.152.4", "127.0.0.1" }, false)]
    public void Spoofed_ComparedWithDoh(string[] system, bool spoofed)
    {
        var d = new DnsComparison("www.youtube.com", system.Select(IPAddress.Parse).ToArray(), [IPAddress.Parse("142.251.152.4")], null);
        Assert.Equal(spoofed, d.Spoofed);
    }

    [Fact]
    public void Spoofed_DohFoundNothing_NotBlamedOnSystem() =>
        Assert.False(new DnsComparison("x.example", [], [], "нет").Spoofed);

    [Fact]
    public async Task Probe_WithResolver_LocalhostTargetStillWorks()
    {
        await using var srv = new RawHttpServer((path, s, ct) => RawHttpServer.RespondAsync(s, 200, new byte[100]));
        using var doh = new DohResolver();
        var probe = new HttpProbe { Resolver = doh, ConnectTimeout = TimeSpan.FromSeconds(2), ReadTimeout = TimeSpan.FromSeconds(2) };
        var r = await probe.OnceAsync(new Target(srv.Url("/").Replace("127.0.0.1", "localhost")), System.Security.Authentication.SslProtocols.Tls12);
        Assert.Equal(ProbeOutcome.Ok, r.Outcome);
    }
}
