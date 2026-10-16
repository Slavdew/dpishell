using DpiShell.Core;

namespace DpiShell.Tests;

public class WatchdogTests
{
    static readonly DateTime Now = new(2026, 10, 16, 12, 0, 0);

    static AppState State(string current, params string[] fallbacks) =>
        new() { StrategyId = current, Enabled = true, Fallbacks = [.. fallbacks] };

    static StrategyScore Score(string id, int passed, int total = 2, double share = 1, string? error = null, double seconds = 1)
    {
        var t = new Target("https://x/");
        var ok = (int)Math.Round(share * 10);
        var verdicts = Enumerable.Range(0, 10).Select(i => new TargetVerdict("g", t, i < ok, seconds, [])).ToList();
        return new(new Strategy(id, id, "flowseal", []), verdicts, passed, total, error);
    }

    static SelectionReport Report(params StrategyScore[] scores) => new([], [], scores, false);

    static TargetVerdict V(bool ok) => new("g", new Target("https://x/"), ok, 1, []);

    [Fact]
    public void OneFailure_Waits_SecondSwitchesToNext()
    {
        var s = State("a", "a", "b", "c");
        Assert.Equal(WatchAction.None, WatchdogPolicy.Decide(s, WatchOutcome.Failed, Now).Action);
        Assert.Equal(1, s.WatchFails);
        var d = WatchdogPolicy.Decide(s, WatchOutcome.Failed, Now);
        Assert.Equal(new WatchDecision(WatchAction.Switch, "b"), d);
        Assert.Equal(0, s.WatchFails);
        Assert.Equal(Now, s.WatchCheckedAt);
    }

    [Fact]
    public void SuccessBetweenFailures_ResetsCounter()
    {
        var s = State("a", "a", "b");
        WatchdogPolicy.Decide(s, WatchOutcome.Failed, Now);
        WatchdogPolicy.Decide(s, WatchOutcome.Ok, Now);
        Assert.Equal(0, s.WatchFails);
        Assert.Equal(WatchAction.None, WatchdogPolicy.Decide(s, WatchOutcome.Failed, Now).Action);
    }

    [Fact]
    public void NoNetwork_DoesNotCount()
    {
        var s = State("a", "a", "b");
        WatchdogPolicy.Decide(s, WatchOutcome.Failed, Now);
        Assert.Equal(WatchAction.None, WatchdogPolicy.Decide(s, WatchOutcome.NoNetwork, Now.AddMinutes(30)).Action);
        Assert.Equal(1, s.WatchFails);
        Assert.Equal(Now, s.WatchCheckedAt);
    }

    [Fact]
    public void LastInList_GivesUpOnce_ThenSilent_UntilSuccess()
    {
        var s = State("c", "a", "b", "c");
        WatchdogPolicy.Decide(s, WatchOutcome.Failed, Now);
        Assert.Equal(WatchAction.GiveUp, WatchdogPolicy.Decide(s, WatchOutcome.Failed, Now).Action);
        Assert.True(s.WatchGaveUp);
        for (int i = 0; i < 4; i++)
            Assert.Equal(WatchAction.None, WatchdogPolicy.Decide(s, WatchOutcome.Failed, Now).Action);

        WatchdogPolicy.Decide(s, WatchOutcome.Ok, Now);
        Assert.False(s.WatchGaveUp);
        WatchdogPolicy.Decide(s, WatchOutcome.Failed, Now);
        Assert.Equal(WatchAction.GiveUp, WatchdogPolicy.Decide(s, WatchOutcome.Failed, Now).Action);
    }

    [Fact]
    public void EmptyFallbacks_GivesUp()
    {
        var s = State("a");
        WatchdogPolicy.Decide(s, WatchOutcome.Failed, Now);
        Assert.Equal(WatchAction.GiveUp, WatchdogPolicy.Decide(s, WatchOutcome.Failed, Now).Action);
    }

    [Fact]
    public void Next_CurrentNotInList_TakesFirst_SkipsMissing()
    {
        Assert.Equal("a", WatchdogPolicy.Next(["a", "b"], "x"));
        Assert.Equal("a", WatchdogPolicy.Next(["a", "b"], null));
        Assert.Equal("c", WatchdogPolicy.Next(["a", "b", "c"], "a", id => id != "b"));
        Assert.Null(WatchdogPolicy.Next(["a", "b"], "a", id => id != "b"));
        Assert.Null(WatchdogPolicy.Next(["a", "b"], "b"));
    }

    [Fact]
    public void Rank_UsableOnly_InRatingOrder_UpToThree()
    {
        var r = Report(
            Score("half", passed: 1),
            Score("broken", passed: 2, error: "winws упал"),
            Score("none", passed: 0, share: 0),
            Score("slow", passed: 2, seconds: 3),
            Score("fast", passed: 2, seconds: 1),
            Score("lessTargets", passed: 2, share: 0.8),
            Score("extra", passed: 1, share: 0.5));
        Assert.Equal(["fast", "slow", "lessTargets"], WatchdogPolicy.Rank(r));
    }

    [Fact]
    public void Remember_KeepsOldList_WhenNothingWorks()
    {
        var s = State("a", "a", "b");
        s.WatchGaveUp = true;
        WatchdogPolicy.Remember(s, Report(Score("x", passed: 0, share: 0)));
        Assert.Equal(["a", "b"], s.Fallbacks);
        Assert.True(s.WatchGaveUp);

        WatchdogPolicy.Remember(s, Report(Score("y", passed: 2), Score("z", passed: 1)));
        Assert.Equal(["y", "z"], s.Fallbacks);
        Assert.False(s.WatchGaveUp);
    }

    [Fact]
    public void Applied_PutsFirst_CapsAtThree_ResetsCounters()
    {
        var s = State("a", "a", "b", "c");
        s.WatchFails = 1; s.WatchGaveUp = true;
        WatchdogPolicy.Applied(s, "c");
        Assert.Equal(["c", "a", "b"], s.Fallbacks);
        WatchdogPolicy.Applied(s, "new");
        Assert.Equal(["new", "c", "a"], s.Fallbacks);
        Assert.Equal(0, s.WatchFails);
        Assert.False(s.WatchGaveUp);
    }

    [Fact]
    public void ShouldCheck_OnlyEnabledRunningIdle()
    {
        var s = State("a");
        Assert.True(WatchdogPolicy.ShouldCheck(s, serviceRunning: true, busy: false));
        Assert.False(WatchdogPolicy.ShouldCheck(s, serviceRunning: false, busy: false));
        Assert.False(WatchdogPolicy.ShouldCheck(s, serviceRunning: true, busy: true));
        s.Enabled = false;
        Assert.False(WatchdogPolicy.ShouldCheck(s, serviceRunning: true, busy: false));
    }

    [Fact]
    public void Classify_NoNetworkOnlyWhenNothingOpensAndDohSilent()
    {
        Assert.Equal(WatchOutcome.Ok, WatchdogPolicy.Classify([V(true), V(true)], dohAlive: false));
        Assert.Equal(WatchOutcome.Ok, WatchdogPolicy.Classify([], dohAlive: false));
        Assert.Equal(WatchOutcome.Failed, WatchdogPolicy.Classify([V(true), V(false)], dohAlive: false));
        Assert.Equal(WatchOutcome.Failed, WatchdogPolicy.Classify([V(false), V(false)], dohAlive: true));
        Assert.Equal(WatchOutcome.NoNetwork, WatchdogPolicy.Classify([V(false), V(false)], dohAlive: false));
    }

    [Fact]
    public void State_FallbackFields_RoundTrip()
    {
        using var t = new TempDir();
        var p = Make.Paths(t);
        var s = State("a", "a", "b");
        s.WatchFails = 1; s.WatchGaveUp = true; s.WatchCheckedAt = Now;
        s.Save(p);
        var l = AppState.Load(p);
        Assert.Equal(["a", "b"], l.Fallbacks);
        Assert.Equal(1, l.WatchFails);
        Assert.True(l.WatchGaveUp);
        Assert.Equal(Now, l.WatchCheckedAt);
    }

    [Fact]
    public async Task Probe_NoRequiredTargets_IsOkWithoutNetwork()
    {
        using var t = new TempDir();
        var groups = new List<TargetGroup> { new("g", "G", [new Target("https://x.invalid/", Required: false)]) };
        Assert.Equal(WatchOutcome.Ok, await WatchdogProbe.CheckAsync(groups, Make.Paths(t)));
    }
}
