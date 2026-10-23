using System.ComponentModel;
using System.Security.Authentication;
using DpiShell.Core;

namespace DpiShell.Tests;

/// <summary>
/// Windows 10 не умеет клиентский TLS 1.3 (Schannel, SEC_E_ALGORITHM_MISMATCH): проверки — по TLS 1.2, несовпадение — не провал стратегии.
/// Сеть не трогается: подставная проверка, состояние TlsSupport — своё на тест (<see cref="TlsSupport.Assume"/>).
/// </summary>
public sealed class TlsSupportTests : IDisposable
{
    readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    /// <summary>Как Windows 10: на TLS 1.3 — несовпадение алгоритмов; на TLS 1.2 — открывается, если «стратегия» рабочая.</summary>
    internal sealed class Win10Probe : HttpProbe
    {
        public volatile string? Strategy;
        public Func<string?, bool> Works { get; init; } = _ => true;
        public readonly List<SslProtocols> Calls = [];

        public override Task<ProbeResult> OnceAsync(Target t, SslProtocols tls, CancellationToken ct = default)
        {
            lock (Calls) Calls.Add(tls);
            if (tls == SslProtocols.Tls13)
                return Task.FromResult(new ProbeResult(t, tls, ProbeOutcome.Tls13Unsupported, 0.01, 0, "The client and server cannot communicate, because they do not possess a common algorithm"));
            var ok = Works(Strategy);
            return Task.FromResult(new ProbeResult(t, tls, ok ? ProbeOutcome.Ok : ProbeOutcome.Reset, 0.2, ok ? 40000 : 0, ok ? "HTTP 200" : ""));
        }
    }

    sealed class StepRun(AppPaths paths, Win10Probe probe) : Selector(paths, probe)
    {
        public override async Task<StrategyScore> RunOneAsync(Strategy s, IReadOnlyList<TargetGroup> groups, CancellationToken ct)
        {
            probe.Strategy = s.Id;
            try { return Score(s, groups, await CheckGroupsAsync(groups, ct)); }
            finally { probe.Strategy = null; }
        }
    }

    [Theory]
    [InlineData(10, 0, 19045, false)]   // Windows 10 22H2
    [InlineData(10, 0, 17763, false)]   // Windows 10 1809 / Server 2019
    [InlineData(10, 0, 20348, true)]    // Server 2022
    [InlineData(10, 0, 22631, true)]    // Windows 11
    public void Build_decides_tls13(int major, int minor, int build, bool has) =>
        Assert.Equal(has, TlsSupport.BuildHasTls13(new Version(major, minor, build)));

    [Fact]
    public void Algorithm_mismatch_is_recognized_in_exception_chain()
    {
        var e = new HttpRequestException("The SSL connection could not be established",
            new AuthenticationException("Authentication failed", new Win32Exception(TlsSupport.AlgorithmMismatch)));
        Assert.True(TlsSupport.IsAlgorithmMismatch(e));
        Assert.False(TlsSupport.IsAlgorithmMismatch(new HttpRequestException("x", new AuthenticationException("y", new Win32Exception(unchecked((int)0x80090326))))));
    }

    [Fact]
    public async Task Without_tls13_only_tls12_is_checked_everywhere()
    {
        using var _ = TlsSupport.Assume(tls13: false);
        Assert.Equal([SslProtocols.Tls12], HttpProbe.DefaultTls);
        Assert.Equal([SslProtocols.Tls12], TlsSupport.Watch);
        Assert.Equal([SslProtocols.Tls12], TlsSupport.Effective([SslProtocols.Tls13]));
        var probe = new Win10Probe();
        var r = await probe.CheckAsync(new Target("https://www.youtube.com/"), [SslProtocols.Tls13, SslProtocols.Tls12]);
        Assert.Equal([SslProtocols.Tls12], r.Select(p => p.Tls));
        Assert.Equal([SslProtocols.Tls12], probe.Calls);   // 1.3 даже не пробуется
        Assert.True(r.All(p => p.Ok));
    }

    [Fact]
    public async Task Mismatch_on_tls13_is_not_a_failure_and_turns_tls13_off_after_two_sites()
    {
        using var _ = TlsSupport.Assume(tls13: true);
        var probe = new Win10Probe();
        var one = await probe.CheckAsync(new Target("https://a.example/"), [SslProtocols.Tls13]);
        Assert.Equal([SslProtocols.Tls12], one.Select(p => p.Tls));   // вместо 1.3 — 1.2
        Assert.True(one.Single().Ok);
        Assert.True(TlsSupport.Tls13Client);                          // один сайт — ещё не вывод
        await probe.CheckAsync(new Target("https://b.example/"));
        Assert.False(TlsSupport.Tls13Client);
        Assert.True(TlsSupport.DisabledHere);
        Assert.Equal([SslProtocols.Tls12], HttpProbe.DefaultTls);
    }

    [Fact]
    public async Task Selection_on_windows10_picks_best_by_tls12()
    {
        using var _ = TlsSupport.Assume(tls13: true);   // даже если сборка «умеет», mismatch не бракует стратегии
        var probe = new Win10Probe { Attempts = 1, Works = s => s == "s2" };
        var selector = new StepRun(Make.Paths(_tmp), probe) { Settle = TimeSpan.Zero };
        var yt = new TargetGroup("youtube", "YouTube", [new("https://www.youtube.com/"), new("https://i.ytimg.com/")]);
        var dc = new TargetGroup("discord", "Discord", [new("https://discord.com/")]);
        var strategies = Enumerable.Range(1, 3).Select(i => new Strategy($"s{i}", $"S{i}", "test", new Dictionary<string, string>())).ToList();

        var r = await selector.RunAsync(strategies, [yt, dc], quick: false);

        Assert.Equal("s2", r.Best?.Strategy.Id);
        Assert.True(r.Best!.Perfect);
        Assert.All(r.Scores.SelectMany(s => s.Verdicts).SelectMany(v => v.Probes), p => Assert.Equal(SslProtocols.Tls12, p.Tls));
        Assert.DoesNotContain(r.Scores.SelectMany(s => s.Verdicts).SelectMany(v => v.Probes), p => p.Outcome == ProbeOutcome.Tls13Unsupported);
        Assert.True(TlsSupport.Tls12Only(r));
    }

    [Fact]
    public void Report_with_tls13_probes_is_not_tls12_only()
    {
        using var _ = TlsSupport.Assume(tls13: true);
        var t = new Target("https://www.youtube.com/");
        TargetVerdict V(params SslProtocols[] v) => new("youtube", t, true, 0.2, [.. v.Select(x => new ProbeResult(t, x, ProbeOutcome.Ok, 0.2, 0, ""))]);
        var g = new TargetGroup("youtube", "YouTube", [t]);
        var s = new Strategy("s", "S", "test", []);
        Assert.False(TlsSupport.Tls12Only(new([], [g], [Selector.Score(s, [g], [V(SslProtocols.Tls13, SslProtocols.Tls12)])], false)));
        Assert.True(TlsSupport.Tls12Only(new([], [g], [Selector.Score(s, [g], [V(SslProtocols.Tls12)])], false)));
        using (TlsSupport.Assume(tls13: false))
            Assert.True(TlsSupport.Tls12Only(new([], [g], [], false)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Watchdog_does_not_fire_when_only_tls13_fails(bool buildHasTls13)
    {
        using var _ = TlsSupport.Assume(buildHasTls13);
        var probe = new Win10Probe();
        var groups = new List<TargetGroup> { new("youtube", "YouTube", [new("https://www.youtube.com/"), new("https://i.ytimg.com/")]) };
        var outcome = await WatchdogProbe.CheckAsync(groups, Make.Paths(_tmp), _ => probe);
        Assert.Equal(WatchOutcome.Ok, outcome);
        Assert.Contains(SslProtocols.Tls12, probe.Calls);
        if (!buildHasTls13) Assert.DoesNotContain(SslProtocols.Tls13, probe.Calls);
    }
}
