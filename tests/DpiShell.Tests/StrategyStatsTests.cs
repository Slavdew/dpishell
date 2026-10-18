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
        Assert.Equal((stats["c"], 2, 1), (d["c"].Score, d["c"].Runs, d["c"].FullOk));   // старые файлы — только общие числа
        Assert.Empty(d["c"].Groups!);
        Assert.Null(d["c"].Last);
        Assert.Equal(0, d["c"].UserTotal);

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

    // ---------- по группам и адресам (новый формат results) ----------

    static readonly TargetGroup Yt = new("youtube", "YouTube", [new("https://www.youtube.com/", 20480), new("https://yt3.ggpht.com/", 0, true, Required: false)]);
    static readonly TargetGroup Dc = new("discord", "Discord", [new("https://discord.com/", 20480)]);
    static readonly TargetGroup Control = new("control", "Обычные сайты", [new("https://vk.com/")], Control: true);
    static TargetGroup Mine(int n) => TargetSet.UserGroup(Enumerable.Range(1, n).Select(i => $"site{i}.example"));

    /// <summary>Прогон стратегии: какие адреса не открылись (по URL), время открывшихся.</summary>
    static StrategyScore Run(string id, IReadOnlyList<TargetGroup> groups, double seconds, params string[] failed)
    {
        var verdicts = groups.SelectMany(g => g.Targets.Select(t =>
        {
            bool ok = !failed.Contains(t.Url);
            var probe = new ProbeResult(t, System.Security.Authentication.SslProtocols.Tls13, ok ? ProbeOutcome.Ok : ProbeOutcome.Reset, seconds, 0, ok ? "HTTP 200, 40 КБ" : "");
            return new TargetVerdict(g.Id, t, ok, ok ? seconds : 0, [probe]);
        })).ToList();
        return Selector.Score(S(id), groups, verdicts);
    }

    static void Save(AppPaths p, string name, IReadOnlyList<TargetGroup> groups, params StrategyScore[] scores)
    {
        var file = ResultStore.Save(p, new SelectionReport([], groups, scores, false));
        File.Move(file, Path.Combine(p.Results, name + ".json"), overwrite: true);
    }

    [Fact]
    public void Detailed_ByGroupsAndAddresses_UserShareNotAllOrNothing()
    {
        using var t = new TempDir();
        var p = new AppPaths(t.Sub("root"), Repo.Data);
        var groups = new[] { Yt, Dc, Mine(9), Control };
        // Старый прогон (до групп): числа идут в общие.
        Result(p, "2026-10-10_10-00-00", ("a", 2, null));
        Save(p, "2026-10-16_10-00-00", groups, Run("a", groups, 0.5, "https://discord.com/", "https://site1.example/"));
        Save(p, "2026-10-17_10-00-00", groups, Run("a", groups, 0.4, "https://site1.example/", "https://site2.example/", "https://yt3.ggpht.com/"),
            Run("b", groups, 0.3, "https://vk.com/"));   // b ломает обычные сайты

        var d = StrategyStats.LoadDetailed(p);
        var a = d["a"];
        Assert.Equal(3, a.Runs);
        Assert.Equal([new StrategyStats.GroupStat("youtube", 2, 2), new StrategyStats.GroupStat("discord", 2, 1)], a.Groups!);   // необязательный ggpht не роняет YouTube
        Assert.Equal((7, 9), (a.UserOk, a.UserTotal));   // последний прогон: 7 из 9, а не «не всё»
        Assert.InRange(a.UserShare!.Value, 0.77, 0.89);
        Assert.Equal(0, a.ControlBroken);
        Assert.Equal("2026-10-17_10-00-00.json", a.Last!.File);
        Assert.Equal(13, a.Last.Targets.Count);
        var site1 = a.Last.Targets.First(x => x.GroupId == "user" && !x.Ok);
        Assert.StartsWith("свой адрес №", site1.Url);   // свои адреса в истории не хранятся
        Assert.Equal("Reset", Assert.Single(site1.Probes).Outcome);
        Assert.True(a.Last.Targets.Single(x => x.GroupId == "control").Control);

        var b = d["b"];
        Assert.Equal((1, 1, 0.0), (b.Runs, b.ControlBroken, b.Score));   // сломала обычные сайты — прогон есть, для порядка ноль
        Assert.Equal([new StrategyStats.GroupStat("youtube", 1, 1), new StrategyStats.GroupStat("discord", 1, 1)], b.Groups!);
    }

    [Fact]
    public void Detailed_LastTenRunsPerStrategy()
    {
        using var t = new TempDir();
        var p = new AppPaths(t.Sub("root"), Repo.Data);
        var groups = new[] { Yt };
        // 12 подборов: «a» во всех, «b» только в первом (быстрый подбор дальше до неё не доходил).
        Save(p, "2026-10-01_00-00-00", groups, Run("a", groups, 0.5, "https://www.youtube.com/"), Run("b", groups, 0.5));
        for (int i = 2; i <= 12; i++) Save(p, $"2026-10-{i:00}_00-00-00", groups, Run("a", groups, 0.5));
        var d = StrategyStats.LoadDetailed(p);
        Assert.Equal((10, 10), (d["a"].Runs, d["a"].Groups![0].Ok));   // старая неудача за пределами 10 прогонов
        Assert.Equal(1, d["b"].Runs);   // у «b» — её единственный прогон, хоть он и старше 10 файлов
        Assert.Equal(("2026-10-12_00-00-00.json", 10), (d["a"].Newest!.File, d["a"].Streak));   // неудача 01.10 — за пределами 10 прогонов
        Assert.Equal("2026-10-01_00-00-00.json", d["b"].Newest!.File);
    }

    [Fact]
    public void Streak_StopsAtFailure_OldRecordsDoNotCount()
    {
        using var t = new TempDir();
        var p = new AppPaths(t.Sub("root"), Repo.Data);
        var groups = new[] { Yt, Dc };
        Result(p, "2026-10-01_00-00-00", ("a", 2, null), ("b", 2, null));   // старые без групп — серию не продолжают
        Save(p, "2026-10-02_00-00-00", groups, Run("a", groups, 0.5), Run("b", groups, 0.5));
        Save(p, "2026-10-03_00-00-00", groups, Run("a", groups, 0.5), Run("b", groups, 0.5));
        Save(p, "2026-10-04_00-00-00", groups, Run("a", groups, 0.5, "https://discord.com/"), Run("b", groups, 0.5));
        var d = StrategyStats.LoadDetailed(p);
        Assert.Equal(0, d["a"].Streak);   // свежий прогон провален
        Assert.Equal(3, d["b"].Streak);   // три прогона с группами, старый файл не в счёт
        Assert.Equal(new StrategyStats.NewestRun("2026-10-04_00-00-00.json", 1, 2, false), d["a"].Newest);
    }

    [Fact]
    public void Order_RequiredGroups_ThenUserShare_ThenTime()
    {
        using var t = new TempDir();
        var p = new AppPaths(t.Sub("root"), Repo.Data);
        var groups = new[] { Yt, Dc, Mine(4) };
        Save(p, "2026-10-17_10-00-00", groups,
            Run("slow-all", groups, 0.9),
            Run("fast-all", groups, 0.3),
            Run("users-half", groups, 0.2, "https://site1.example/", "https://site2.example/"),
            Run("no-discord", groups, 0.1, "https://discord.com/"),
            Run("broken", groups, 0.1, "https://www.youtube.com/", "https://discord.com/"));
        var order = StrategyStats.Order([S("broken"), S("no-discord"), S("new"), S("users-half"), S("slow-all"), S("fast-all")], StrategyStats.LoadDetailed(p));
        Assert.Equal(["fast-all", "slow-all", "users-half", "no-discord", "new", "broken"], order.Select(s => s.Id));
    }
}
