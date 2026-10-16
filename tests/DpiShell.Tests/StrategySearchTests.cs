using DpiShell.Core;

namespace DpiShell.Tests;

public sealed class StrategySearchTests
{
    static Strategy With(string desync) => new("t", "t", "search", new() { ["general"] = desync });

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
