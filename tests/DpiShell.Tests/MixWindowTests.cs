using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.Tests;

/// <summary>Окно после подбора: смешанная предложена (и применена, как лучшая) или не предложена, когда хватило одиночной.</summary>
public sealed class MixWindowTests : IDisposable
{
    readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    static readonly Target T1 = new("https://www.youtube.com/"), T2 = new("https://discord.com/");

    static TargetVerdict V(string group, Target t, bool ok) => new(group, t, ok, ok ? 0.4 : 0, []);

    static StrategyScore Score(Strategy s, bool yt, bool ds) =>
        new(s, [V("youtube", T1, yt), V("discord", T2, ds)], (yt ? 1 : 0) + (ds ? 1 : 0), 2);

    static Strategy Single(string id) => new(id, $"general ({id.ToUpperInvariant()})", "flowseal", new() { ["general"] = "--dpi-desync=fake" });

    static Strategy Mix() => new("mix-alt-alt10", "Смешанная · YouTube — ALT · остальное — ALT10", "mix",
        new() { ["google"] = "--dpi-desync=multisplit --dpi-desync-split-pos=1", ["general"] = "--dpi-desync=fake" },
        new() { ["youtube"] = "alt", ["discord"] = "alt10" });

    async Task<(MainViewModel Vm, HelperFixture F)> RunAsync(params StrategyScore[] scores)
    {
        var f = new HelperFixture();
        var tested = new List<TargetGroup> { new("youtube", "YouTube", [T1]), new("discord", "Discord", [T2]) };
        f.Backend.ReportOverride = new([V("youtube", T1, false), V("discord", T2, false)], tested, scores, false);
        f.Backend.Release.TrySetResult();
        f.Backend.StatusValue = f.Backend.StatusValue with
        {
            Strategies = [.. f.Backend.StatusValue.Strategies, .. scores.Select(x => new StrategyInfo(x.Strategy.Id, x.Strategy.Title, x.Strategy.Source))],
        };
        foreach (var x in scores) ((HashSet<string>)f.Backend.StrategyIds).Add(x.Strategy.Id);
        var vm = TestWindow.Model(new Shell(new AppPaths(_tmp.Sub("root"), Repo.Data)), await f.LinkAsync());
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsLoading))!.SetValue(vm, false);
        vm.Confirm = _ => true;
        await vm.RefreshStatusAsync();
        foreach (var g in vm.Groups) g.IsChecked = g.Id == "youtube";
        await vm.StartSelectAsync();
        return (vm, f);
    }

    [Fact]
    public async Task Select_MixBeatsSingles_IsOffered_AndExplained()
    {
        var (vm, f) = await RunAsync(Score(Single("alt"), true, false), Score(Single("alt10"), false, true), Score(Mix(), true, true));
        await using var _ = f;

        Assert.StartsWith("Применена: Смешанная · YouTube — ALT · остальное — ALT10", vm.ResultText);   // лучшая применена, как обычно
        Assert.Contains("mix-alt-alt10", f.Backend.Calls.Where(c => c.StartsWith("enable")).Single());
        Assert.Contains(vm.Scores, s => s.Id == "mix-alt-alt10" && s.IsBest);
        Assert.False(vm.OfferDeep);   // смешанная открыла всё
    }

    [Fact]
    public async Task Select_MixInReportButNotPerfect_HintSaysItWasAssembled()
    {
        // Смешанная лучше одиночной, но открыла не всё: подсказка объясняет, откуда она.
        var partial = Score(Mix(), true, false) with { Verdicts = [V("youtube", T1, true), V("discord", T2, true), V("discord", new Target("https://x.example/"), false)], GroupsPassed = 1, GroupsTotal = 2 };
        var (vm, f) = await RunAsync(Score(Single("alt"), false, false), partial);
        await using var _ = f;

        Assert.StartsWith(L.T("select.note.mix").Substring(0, 1).ToUpperInvariant() + L.T("select.note.mix").Substring(1), vm.ResultHint);
    }

    [Fact]
    public async Task Select_SingleIsEnough_NoMixMentioned()
    {
        var (vm, f) = await RunAsync(Score(Single("alt"), true, true), Score(Single("alt10"), false, true));
        await using var _ = f;

        Assert.DoesNotContain("смешанн", (vm.ResultText + vm.ResultHint).ToLowerInvariant());
        Assert.DoesNotContain(vm.Scores, s => s.Id.StartsWith("mix-"));
        Assert.Contains("enable alt", f.Backend.Calls.Single(c => c.StartsWith("enable")));
    }
}
