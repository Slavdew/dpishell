using System.Net;
using System.Security.Authentication;
using DpiShell.App;
using DpiShell.Core;

namespace DpiShell.Tests;

/// <summary>
/// Путь браузера в «Проверить сейчас» (architecture.md «Проверка одной цели», п. 6): по DoH открылось, а DNS системы даёт другой адрес —
/// цель проверяется ещё раз по нему. Резолверы и проверка — подделки, без сети.
/// </summary>
public sealed class BrowserCheckTests
{
    sealed class FakeResolver(Dictionary<string, string[]> map) : IAddressResolver
    {
        public List<string> Asked { get; } = [];
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct = default)
        {
            Asked.Add(host);
            return Task.FromResult(map.TryGetValue(host, out var a) ? a.Select(IPAddress.Parse).ToArray() : []);
        }
    }

    static readonly Target Yt = new("https://www.youtube.com/");

    static IReadOnlyList<TargetVerdict> Ok(params Target[] targets) =>
        targets.Select(t => new TargetVerdict("youtube", t, true, 0.3, [new ProbeResult(t, SslProtocols.Tls13, ProbeOutcome.Ok, 0.3, 100, "HTTP 200")])).ToList();

    /// <summary>Подделка проверки: считает вызовы и отвечает по резолверу — системный путь «работает» или нет.</summary>
    sealed class FakeProbe(bool systemOk)
    {
        public List<(Target, IAddressResolver)> Calls { get; } = [];
        public Task<IReadOnlyList<ProbeResult>> Run(Target t, IAddressResolver r, CancellationToken ct)
        {
            Calls.Add((t, r));
            var o = systemOk ? ProbeOutcome.Ok : ProbeOutcome.ForeignRedirect;
            return Task.FromResult<IReadOnlyList<ProbeResult>>([new ProbeResult(t, SslProtocols.Tls13, o, 0.1, 0, systemOk ? "HTTP 200" : "302: редирект на warning.rt.ru")]);
        }
    }

    static readonly FakeResolver Doh = new(new() { ["www.youtube.com"] = ["142.250.74.14", "142.250.74.46"] });

    [Fact]
    public async Task SameAddresses_OneProbeOnly()
    {
        // Другой адрес той же подсети /24 (соседний узел CDN) — не расхождение.
        var sys = new FakeResolver(new() { ["www.youtube.com"] = ["142.250.74.78"] });
        var probe = new FakeProbe(systemOk: false);
        var v = await BrowserCheck.AddAsync(Ok(Yt), sys, Doh, probe.Run, _ => null);
        Assert.Empty(probe.Calls);
        Assert.Null(v[0].Browser);
        Assert.False(v[0].BrowserFails);
    }

    [Fact]
    public async Task Differs_SystemPathFails_BrowserWontOpen_DnsReason()
    {
        var sys = new FakeResolver(new() { ["www.youtube.com"] = ["195.208.4.1"] });
        var probe = new FakeProbe(systemOk: false);
        var v = await BrowserCheck.AddAsync(Ok(Yt), sys, Doh, probe.Run, _ => null);
        var (t, r) = Assert.Single(probe.Calls);
        Assert.Same(sys, r);   // повторная проверка — через DNS системы
        Assert.True(v[0].Ok);  // для программы (и подбора) — открывается
        Assert.True(v[0].BrowserFails);
        Assert.Equal(BrowserPath.DnsReason, v[0].Browser!.Reason);

        var row = CheckRow.From(v[0], _ => "YouTube");
        Assert.Equal("⚠", row.Mark);
        Assert.Equal("✓ для программы, ✗ для браузера", row.Note);
        Assert.Contains("142.250.74.14", row.Detail);
        Assert.Contains("195.208.4.1", row.Detail);
        Assert.Contains(BrowserPath.DnsReason, row.Detail);
    }

    [Fact]
    public async Task Differs_SystemPathWorks_PlainOk()
    {
        var sys = new FakeResolver(new() { ["www.youtube.com"] = ["173.194.220.91"] });
        var probe = new FakeProbe(systemOk: true);
        var v = await BrowserCheck.AddAsync(Ok(Yt), sys, Doh, probe.Run, _ => null);
        Assert.Single(probe.Calls);
        Assert.False(v[0].BrowserFails);
        var row = CheckRow.From(v[0], _ => "YouTube");
        Assert.Equal("✓", row.Mark);
        Assert.False(row.HasBrowser);
    }

    [Fact]
    public async Task AddressFromHosts_HostsReason()
    {
        var sys = new FakeResolver(new() { ["www.youtube.com"] = ["10.0.0.5"] });
        var probe = new FakeProbe(systemOk: false);
        var v = await BrowserCheck.AddAsync(Ok(Yt), sys, Doh, probe.Run, h => h == "www.youtube.com" ? "10.0.0.5" : null);
        Assert.True(v[0].BrowserFails);
        Assert.True(v[0].Browser!.FromHosts);
        Assert.Equal(BrowserPath.HostsReason, v[0].Browser!.Reason);
        Assert.Contains("hosts", CheckRow.From(v[0], _ => "YouTube").Detail);
    }

    [Fact]
    public async Task FailedByDoh_Or_NoDohAnswer_NotRechecked()
    {
        var other = new Target("https://unknown.example/");
        var verdicts = Ok(other).Concat([new TargetVerdict("youtube", Yt, false, 0, [])]).ToList();
        var sys = new FakeResolver(new() { ["www.youtube.com"] = ["195.208.4.1"], ["unknown.example"] = ["1.2.3.4"] });
        var probe = new FakeProbe(systemOk: false);
        var v = await BrowserCheck.AddAsync(verdicts, sys, Doh, probe.Run, _ => null);
        Assert.Empty(probe.Calls);   // DoH не ответил — сравнить не с чем; по DoH не открылось — и так ✗
        Assert.All(v, x => Assert.Null(x.Browser));
    }

    static ProbeResult Fail(ProbeOutcome o, string detail) => new(Yt, SslProtocols.Tls13, o, 0.1, 0, detail);

    static BrowserPath Path(string sys, params ProbeResult[] probes) =>
        new([IPAddress.Parse(sys)], [IPAddress.Parse("142.250.74.14")], false, null, probes);

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("192.168.0.9")]
    [InlineData("172.20.0.1")]
    [InlineData("100.64.5.5")]
    public void PrivateAddress_IsSpoof(string ip)
    {
        var b = Path(ip, Fail(ProbeOutcome.Timeout, "таймаут"));
        Assert.True(b.Spoofed);
        Assert.Equal(BrowserPath.DnsReason, b.Reason);
    }

    [Fact]
    public void OtherPublicAddress_NotSpoof_WindowsOnlyText()
    {
        var b = Path("173.194.220.91", Fail(ProbeOutcome.Timeout, "таймаут"));
        Assert.False(b.Spoofed);
        Assert.Equal(BrowserPath.WindowsOnlyReason, b.Reason);
        Assert.DoesNotContain("подмен", b.Reason);
    }

    [Fact]
    public void PublicAddress_WrongCertificate_IsSpoof()
    {
        Assert.True(Path("173.194.220.91", Fail(ProbeOutcome.TlsFailed, "The remote certificate is invalid according to the validation procedure.")).Spoofed);
        Assert.False(Path("173.194.220.91", Fail(ProbeOutcome.TlsFailed, "handshake failed")).Spoofed);
    }

    [Fact]
    public async Task Differs_PublicAddress_Fails_WindowsOnlyRow()
    {
        var sys = new FakeResolver(new() { ["www.youtube.com"] = ["173.194.220.91"] });
        var v = await BrowserCheck.AddAsync(Ok(Yt), sys, Doh, (t, r, c) => Task.FromResult<IReadOnlyList<ProbeResult>>([Fail(ProbeOutcome.Timeout, "таймаут")]), _ => null);
        Assert.True(v[0].BrowserFails);
        var row = CheckRow.From(v[0], _ => "YouTube");
        Assert.Equal(BrowserPath.WindowsOnlyReason, row.Browser);
        Assert.DoesNotContain("подмен", row.Detail);
    }

    [Fact]
    public void SameAddresses_Rules()
    {
        IPAddress[] A(params string[] s) => s.Select(IPAddress.Parse).ToArray();
        Assert.True(BrowserCheck.SameAddresses(A("1.2.3.4"), A("1.2.3.4", "5.6.7.8")));
        Assert.True(BrowserCheck.SameAddresses(A("1.2.3.200"), A("1.2.3.4")));
        Assert.False(BrowserCheck.SameAddresses(A("1.2.4.4"), A("1.2.3.4")));
        Assert.False(BrowserCheck.SameAddresses(A(), A("1.2.3.4")));
        Assert.True(BrowserCheck.SameAddresses(A("1.2.3.4"), A()));
    }
}
