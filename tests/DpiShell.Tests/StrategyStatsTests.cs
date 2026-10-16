using DpiShell.Core;

namespace DpiShell.Tests;

public sealed class StrategyStatsTests
{
    static Strategy S(string id) => new(id, id, "test", new() { ["general"] = "--dpi-desync=multisplit --dpi-desync-split-pos=1" });

    static void Result(AppPaths p, string name, params (string Id, int Passed, string? Error)[] scores)
    {
        Directory.CreateDirectory(p.Results);
        var body = string.Join(",", scores.Select(s =>
            $$"""{"Id":"{{s.Id}}","GroupsPassed":{{s.Passed}},"GroupsTotal":2,"Error":{{(s.Error is null ? "null" : $"\"{s.Error}\"")}}}"""));
        File.WriteAllText(Path.Combine(p.Results, name + ".json"), $$"""{"scores":[{{body}}]}""");
    }

    [Fact]
    public void Order_StableFirst_UnknownBetween_FreshRunsWeighMore()
    {
        using var t = new TempDir();
        var p = new AppPaths(t.Sub("root"), Repo.Data);
        Result(p, "2026-10-16_10-00-00", ("a", 2, null), ("b", 0, null), ("c", 2, null));
        Result(p, "2026-10-16_12-00-00", ("a", 2, null), ("b", 0, null), ("c", 0, null));   // свежий: c перестала работать
        File.WriteAllText(Path.Combine(p.Results, "2026-10-16_13-00-00.json"), "{битый");     // не мешает

        var stats = StrategyStats.Load(p);
        Assert.Equal(1.0, stats["a"]);
        Assert.Equal(0.0, stats["b"]);
        Assert.True(stats["c"] < 0.5);   // свежая неудача весит больше старого успеха
        var d = StrategyStats.LoadDetailed(p);
        Assert.Equal(new StrategyStats.Stat(stats["c"], 2, 1), d["c"]);   // подпись: «открыла всё в 1 из 2 подборов»

        var order = StrategyStats.Order([S("b"), S("new1"), S("c"), S("a"), S("new2")], stats).Select(s => s.Id);
        Assert.Equal(["a", "new1", "new2", "c", "b"], order);
    }

    [Fact]
    public void Load_IgnoresCrashedRuns_AndMissingDir()
    {
        using var t = new TempDir();
        var p = new AppPaths(t.Sub("root"), Repo.Data);
        Assert.Empty(StrategyStats.Load(p));
        Result(p, "2026-10-16_10-00-00", ("a", 0, "winws завершился"));
        Assert.False(StrategyStats.Load(p).ContainsKey("a"));
    }
}
