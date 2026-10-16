using System.Security.Authentication;
using DpiShell.Core;

namespace DpiShell.Tests;

/// <summary>Группа «Обычные сайты» (control): условие «не сломать», а не цель подбора — как на роутере (06-router-spec §4.1).</summary>
public sealed class ControlGroupTests
{
    static readonly Target Yt = new("https://www.youtube.com/");
    static readonly Target Bank = new("https://www.sberbank.ru/");
    static readonly Target Wiki = new("https://ru.wikipedia.org/");
    static readonly TargetGroup Youtube = new("youtube", "YouTube", [Yt]);
    static readonly TargetGroup Control = new("control", "Обычные сайты", [Bank, Wiki], Control: true);
    static readonly Strategy S = new("alt9", "general (ALT9)", "flowseal", new() { ["general"] = "--dpi-desync=multisplit --dpi-desync-split-pos=1" });

    static TargetVerdict V(string g, Target t, bool ok) =>
        new(g, t, ok, ok ? 0.1 : 0, [new ProbeResult(t, SslProtocols.Tls13, ok ? ProbeOutcome.Ok : ProbeOutcome.Timeout, 0.1, 0, "")]);

    [Fact]
    public void RealTargets_HaveControlGroup()
    {
        var set = TargetSet.Load(Path.Combine(Repo.Data, "targets.json"));
        var c = Assert.Single(set.Groups, g => g.Id == "control");
        Assert.True(c.Control);
        Assert.Contains(c.Targets, t => t.Url.Contains("gosuslugi"));
        Assert.All(set.Groups.Where(g => g.Id != "control"), g => Assert.False(g.Control));
    }

    [Fact]
    public void Keep_OrdinaryOnlyBlocked_ControlOnlyReachable()
    {
        // YouTube не открылся без обхода (оставить), банк открылся (оставить), Википедия не открылась и без обхода (выбросить).
        var baseline = new[] { V("youtube", Yt, false), V("control", Bank, true), V("control", Wiki, false) };
        var kept = Selector.KeepForSelection([Youtube, Control], baseline);
        Assert.Equal([Yt], kept.Single(g => g.Id == "youtube").Targets);
        Assert.Equal([Bank], kept.Single(g => g.Id == "control").Targets);
    }

    [Fact]
    public void Score_BreakingControl_IsZeroWithReason()
    {
        var groups = new[] { Youtube, Control with { Targets = [Bank] } };
        var good = Selector.Score(S, groups, [V("youtube", Yt, true), V("control", Bank, true)]);
        Assert.True(good.Perfect);
        Assert.Equal(1, good.GroupsTotal);   // контрольная — не цель

        var bad = Selector.Score(S, groups, [V("youtube", Yt, true), V("control", Bank, false)]);
        Assert.Equal(0, bad.GroupsPassed);
        Assert.Equal("ломает обычные сайты: www.sberbank.ru", bad.Error);
        var report = new SelectionReport([], groups, [bad, good with { Strategy = S with { Id = "alt13" } }], false);
        Assert.Equal("alt13", report.Best!.Strategy.Id);
    }

    [Fact]
    public void WithControl_AlwaysAddsControlOnce()
    {
        var all = new TargetSet([Youtube, Control]);
        Assert.Equal(["youtube", "control"], Shell.WithControl([Youtube], all).Select(g => g.Id));
        Assert.Equal(["youtube", "control"], Shell.WithControl([Youtube, Control], all).Select(g => g.Id));
        Assert.Equal(["control"], Shell.WithControl([], all).Select(g => g.Id));
    }

    [Fact]
    public void OnlyControlLeft_MeansNothingBlocked()
    {
        var r = new SelectionReport([V("youtube", Yt, true)], [Control], [], false);
        Assert.True(r.NothingBlocked);
    }
}
