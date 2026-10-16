using DpiShell.Core;

namespace DpiShell.Tests;

public sealed class StrategySearchTests
{
    static Strategy With(string desync) => new("t", "t", "search", new() { ["general"] = desync });

    /// <summary>Имитация провайдера: помогает только fooling=badsum, а целиком — badsum вместе с repeats=11.</summary>
    static (Func<string, Task<CandidateResult?>> Try, HashSet<string> Tried) TwoStepProvider()
    {
        var tried = new HashSet<string>();
        return (d =>
        {
            if (!tried.Add(d)) return Task.FromResult<CandidateResult?>(null);
            bool badsum = d.Contains("fooling=badsum"), r11 = d.Contains("repeats=11");
            return Task.FromResult<CandidateResult?>(new("general", d, (badsum ? 1 : 0) + (badsum && r11 ? 1 : 0), 2, 0.1));
        }, tried);
    }

    const string FakeTs = "--dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fooling=ts --dpi-desync-fake-tls={fake}/tls_clienthello_www_google_com.bin";

    [Fact]
    public async Task Refine_FindsTwoStepImprovement()
    {
        var (tryOne, tried) = TwoStepProvider();
        var seeds = new[] { "--dpi-desync=multisplit --dpi-desync-split-pos=1", "--dpi-desync=fake,multisplit --dpi-desync-repeats=6 --dpi-desync-fooling=ts --dpi-desync-split-pos=1 --dpi-desync-fake-tls={fake}/tls_clienthello_www_google_com.bin", FakeTs }
            .Select(d => new CandidateResult("general", d, 0, 2, 0.1)).Reverse().ToList();
        Assert.True(await StrategySearch.RefineAsync(seeds, tryOne, () => Task.FromResult(true), 30));
        Assert.Contains(FakeTs.Replace("fooling=ts", "fooling=badsum").Replace("repeats=6", "repeats=11"), tried);
    }

    [Fact]
    public async Task Refine_KeepsBudget_AndStopsOnControl()
    {
        var (tryOne, tried) = TwoStepProvider();
        var seed = new[] { new CandidateResult("general", FakeTs, 0, 2, 0.1) };
        Assert.True(await StrategySearch.RefineAsync(seed, tryOne, () => Task.FromResult(true), 5));
        Assert.Equal(5, tried.Count);

        var (tryAgain, _) = TwoStepProvider();
        Assert.False(await StrategySearch.RefineAsync(seed, tryAgain, () => Task.FromResult(false), 30));
    }

    [Fact]
    public void Coarse_AllValid_AndDistinct()
    {
        var c = CandidateGenerator.Coarse();
        Assert.True(c.Count >= 15);
        Assert.Equal(c.Count, c.Distinct().Count());
        foreach (var d in c) WinwsArgs.Validate(With(d));
    }

    [Fact]
    public void Neighbours_OfEveryCoarse_AllValid_TwoLevelsDeep()
    {
        int n = 0;
        foreach (var d in CandidateGenerator.Coarse())
            foreach (var a in CandidateGenerator.Neighbours(d))
            {
                WinwsArgs.Validate(With(a));
                foreach (var b in CandidateGenerator.Neighbours(a)) { WinwsArgs.Validate(With(b)); n++; }
            }
        Assert.True(n > 100);
    }

    [Fact]
    public void Neighbours_ChangeOneParameter()
    {
        const string d = "--dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fooling=ts --dpi-desync-fake-tls={fake}/tls_clienthello_www_google_com.bin";
        var ns = CandidateGenerator.Neighbours(d).ToList();
        Assert.Contains("--dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fooling=md5sig --dpi-desync-fake-tls={fake}/tls_clienthello_www_google_com.bin", ns);
        Assert.Contains("--dpi-desync=fake --dpi-desync-repeats=2 --dpi-desync-fooling=ts --dpi-desync-fake-tls={fake}/tls_clienthello_www_google_com.bin", ns);
        // autottl заменяет fooling, а не добавляется к нему
        Assert.Contains("--dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fake-tls={fake}/tls_clienthello_www_google_com.bin --dpi-desync-autottl=2", ns);
        Assert.DoesNotContain(ns, x => x.Contains("split-pos"));  // у fake без split позиции не появляются
    }

    [Fact]
    public void Cost_PrefersQuieter()
    {
        Assert.True(CandidateGenerator.Cost("--dpi-desync=multisplit --dpi-desync-split-pos=1")
                  < CandidateGenerator.Cost("--dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fooling=ts"));
        Assert.True(CandidateGenerator.Cost("--dpi-desync=fake --dpi-desync-repeats=2 --dpi-desync-fooling=ts")
                  < CandidateGenerator.Cost("--dpi-desync=fake --dpi-desync-repeats=11 --dpi-desync-fooling=ts"));
    }

    [Fact]
    public void UserStrategies_RoundTrip_AndInvalidFileIgnored()
    {
        using var t = new TempDir();
        var p = new AppPaths(t.Sub("root"), Repo.Data);
        UserStrategies.Add(p, [new Strategy("found-1", "Найденная 1", "search", new() { ["general"] = "--dpi-desync=multisplit --dpi-desync-split-pos=1" })]);
        UserStrategies.Add(p, [new Strategy("found-1", "Найденная 1b", "search", new() { ["general"] = "--dpi-desync=multisplit --dpi-desync-split-pos=2" })]);
        var l = UserStrategies.Load(p);
        Assert.Single(l);
        Assert.Equal("Найденная 1b", l[0].Title);

        // Подложенный файл с чужим параметром не загружается целиком.
        File.WriteAllText(UserStrategies.File(p), """[{"id":"x","title":"x","source":"search","roles":{"general":"--wf-raw=evil"}}]""");
        Assert.Empty(UserStrategies.Load(p));
    }
}
