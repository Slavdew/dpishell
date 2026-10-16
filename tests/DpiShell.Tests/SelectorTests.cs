using System.Security.Authentication;
using DpiShell.Core;

namespace DpiShell.Tests;

public sealed class SelectorTests : IAsyncLifetime
{
    RawHttpServer _srv = null!;
    readonly TempDir _t = new();

    public Task InitializeAsync()
    {
        _srv = new RawHttpServer((path, s, ct) => path switch
        {
            "/ok" or "/ok2" => RawHttpServer.RespondAsync(s, 200, new byte[2_000]),
            _ => RawHttpServer.RespondAsync(s, 500, []),
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _srv.DisposeAsync();
        _t.Dispose();
    }

    Selector NewSelector() => new(Make.Paths(_t), new HttpProbe { Attempts = 1, ConnectTimeout = TimeSpan.FromSeconds(10), ReadTimeout = TimeSpan.FromSeconds(5) })
    { Tls = [SslProtocols.Tls12, SslProtocols.Tls13], ProbeParallelism = 3 };

    Target T(string path, bool required = true) => new(_srv.Url(path), 1000, Required: required);

    [Fact]
    public async Task CheckGroups_VerdictPerTarget_InInputOrder()
    {
        var groups = new List<TargetGroup>
        {
            new("a", "A", [T("/ok"), T("/bad")]),
            new("b", "B", [T("/ok2")]),
        };
        var v = await NewSelector().CheckGroupsAsync(groups, CancellationToken.None);

        Assert.Equal(3, v.Count);
        Assert.Equal(["a", "a", "b"], v.Select(x => x.GroupId));
        Assert.Equal([true, false, true], v.Select(x => x.Ok));
        Assert.All(v, x => Assert.Equal(2, x.Probes.Count));
        Assert.Equal(0, v[1].BestSeconds);
        Assert.True(v[0].BestSeconds > 0);
    }

    [Fact]
    public async Task Run_EverythingOpensWithoutBypass_NothingBlocked_NoWinws()
    {
        var groups = new List<TargetGroup> { new("a", "A", [T("/ok")]), new("b", "B", [T("/ok2")]) };
        // Стратегия-заглушка: если бы дошло до запуска winws, тест упал бы на отсутствующем exe.
        var r = await NewSelector().RunAsync([Make.Strategy(("general", "--dpi-desync=fake"))], groups, quick: false);
        Assert.True(r.NothingBlocked);
        Assert.Empty(r.TestedGroups);
        Assert.Empty(r.Scores);
        Assert.Null(r.Best);
        Assert.False(r.Cancelled);
        Assert.Equal(2, r.Baseline.Count);
    }

    [Fact]
    public async Task Run_OnlyBlockedTargetsRemain_InTestedGroups()
    {
        var groups = new List<TargetGroup>
        {
            new("a", "A", [T("/ok"), T("/bad"), T("/bad2", required: false)]),
            new("b", "B", [T("/ok2")]),
        };
        var r = await NewSelector().RunAsync([], groups, quick: true);
        Assert.False(r.NothingBlocked);
        var g = Assert.Single(r.TestedGroups);
        Assert.Equal("a", g.Id);
        Assert.Equal([_srv.Url("/bad"), _srv.Url("/bad2")], g.Targets.Select(x => x.Url));
        Assert.Empty(r.Scores);
        Assert.Null(r.Best);
    }

    [Fact]
    public async Task Run_NoGroups_NotNothingBlocked()
    {
        var r = await NewSelector().RunAsync([], [], quick: false);
        Assert.False(r.NothingBlocked);
        Assert.Empty(r.Baseline);
    }

    [Fact]
    public async Task Run_CancelledBeforeStart_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NewSelector().RunAsync([], [new("a", "A", [T("/ok")])], false, null, cts.Token));
    }
}

public class SelectionReportTests
{
    static Strategy S(string id) => new(id, id, "test", new() { ["general"] = "--dpi-desync=fake" });

    static TargetVerdict V(bool ok, double sec, string g = "g") => new(g, new Target("https://x.com/"), ok, sec, []);

    static StrategyScore Score(string id, int passed, int total, IReadOnlyList<TargetVerdict> v, string? err = null) =>
        new(S(id), v, passed, total, err);

    static SelectionReport Report(params StrategyScore[] s) => new([V(false, 0)], [new("g", "G", [])], s, false);

    [Fact]
    public void Best_PrefersGroupShare_ThenTargetShare_ThenSpeed()
    {
        var r = Report(
            Score("fast-but-1group", 1, 2, [V(true, 0.1), V(false, 0)]),
            Score("2groups-slow", 2, 2, [V(true, 2), V(true, 2), V(false, 0)]),
            Score("2groups-more-targets", 2, 2, [V(true, 3), V(true, 3)]),
            Score("2groups-more-targets-faster", 2, 2, [V(true, 1), V(true, 1.5)]));
        Assert.Equal("2groups-more-targets-faster", r.Best!.Strategy.Id);
    }

    [Fact]
    public void Best_SkipsErrorsAndZeroPassed()
    {
        var r = Report(
            Score("crashed", 2, 2, [V(true, 0.1), V(true, 0.1)], err: "winws упал"),
            Score("nothing", 0, 2, [V(false, 0)]),
            Score("one", 1, 2, [V(true, 5)]));
        Assert.Equal("one", r.Best!.Strategy.Id);
    }

    [Fact]
    public void Best_NullWhenAllFailed()
    {
        Assert.Null(Report(Score("a", 0, 1, []), Score("b", 1, 1, [], err: "x")).Best);
        Assert.Null(Report().Best);
    }

    [Fact]
    public void Score_Metrics()
    {
        var s = Score("a", 1, 2, [V(true, 1), V(true, 3), V(false, 0)]);
        Assert.Equal(0.5, s.GroupShare);
        Assert.Equal(2.0 / 3, s.TargetShare, 6);
        Assert.Equal(2, s.AvgSeconds);
        Assert.False(s.Perfect);
        Assert.Equal(99, Score("e", 0, 1, [V(false, 0)]).AvgSeconds);
        Assert.Equal(0, Score("z", 0, 0, []).GroupShare);
        Assert.True(Score("p", 2, 2, [V(true, 1)]).Perfect);
        Assert.False(Score("p", 2, 2, [V(true, 1)], err: "x").Perfect);
        Assert.False(Score("p", 0, 0, []).Perfect);
    }

    [Fact]
    public void NothingBlocked_OnlyWhenBaselineNonEmptyAndNoTestedGroups()
    {
        Assert.True(new SelectionReport([V(true, 1)], [], [], false).NothingBlocked);
        Assert.False(new SelectionReport([], [], [], false).NothingBlocked);
        Assert.False(new SelectionReport([V(false, 0)], [new("g", "G", [])], [], false).NothingBlocked);
    }
}
