using System.Security.Authentication;
using DpiShell.Core;

namespace DpiShell.Tests;

public sealed class ZapretDetectorTests
{
    static IReadOnlyList<Strategy> Catalog => StrategyCatalog.Load(Repo.Strategies).Strategies;

    /// <summary>Командная строка, как её видит Windows у процесса Flowseal: пути с пробелами в кавычках, свои имена списков.</summary>
    static string FlowsealCommandLine(Strategy s)
    {
        const string bin = @"C:\Users\User\Desktop\zapret-discord-youtube-1.10.3\bin\";
        const string lists = @"C:\Users\User\Desktop\zapret-discord-youtube-1.10.3\lists\";
        string Fake(string d) => d.Replace("{fake}/", "\"" + bin).Replace(".bin", ".bin\"");
        var g = s.Roles["general"]; var y = s.Roles["google"];
        return $"\"{bin}winws.exe\" --wf-tcp=80,443,2053,2083,2087,2096,8443, --wf-udp=443,19294-19344,50000-50100, " +
               $"--filter-udp=443 --hostlist=\"{lists}list-general.txt\" --dpi-desync=fake --new " +
               $"--filter-tcp=443 --hostlist=\"{lists}list-google.txt\" {Fake(y)} --new " +
               $"--filter-tcp=80,443 --hostlist=\"{lists}list-general.txt\" --hostlist-exclude=\"{lists}list-exclude.txt\" {Fake(g)} --new " +
               $"--filter-tcp=80,443,8443 --ipset=\"{lists}ipset-all.txt\" --dpi-desync=multisplit";
    }

    [Theory]
    [InlineData("alt11")]
    [InlineData("alt9")]
    [InlineData("alt13")]
    [InlineData("base")]
    [InlineData("fake-tls-auto")]   // в каталоге «!» (стандартный фейк), в bat было «^!»
    public void Match_FlowsealCommandLine_FindsStrategy(string id)
    {
        var s = Catalog.First(x => x.Id == id);
        var (exe, args) = ZapretDetector.SplitCommandLine(FlowsealCommandLine(s));
        Assert.EndsWith(@"bin\winws.exe", exe);
        Assert.Equal(id, ZapretDetector.Match(args, Catalog)?.Id);
    }

    [Fact]
    public void Match_OurOwnBuild_FindsStrategy_AndUnknownGivesNull()
    {
        using var t = new TempDir();
        var p = new AppPaths(t.Sub("Dpi Shell"), Repo.Data);   // путь с пробелом — как кавычки в строке службы
        foreach (var s in Catalog)
        {
            var (_, args) = ZapretDetector.SplitCommandLine($"\"{p.Winws}\" " + WinwsArgs.Join(WinwsArgs.Build(s, p)));
            var found = ZapretDetector.Match(args, Catalog);
            Assert.True(found is not null, $"не узнана {s.Id}: {string.Join(" | ", ZapretDetector.RolesOf(args).Select(kv => kv.Key + "=" + kv.Value))}");
            // Разные id с одинаковыми general+google частями считаются одной стратегией — проверяем совпадение частей.
            Assert.Equal(s.Roles.GetValueOrDefault("general"), found!.Roles.GetValueOrDefault("general"));
            Assert.Equal(s.Roles.GetValueOrDefault("google"), found.Roles.GetValueOrDefault("google"));
        }
        var (_, odd) = ZapretDetector.SplitCommandLine("winws.exe --filter-tcp=80,443 --dpi-desync=split2 --dpi-desync-split-pos=7");
        Assert.Null(ZapretDetector.Match(odd, Catalog));
    }

    [Fact]
    public void SplitCommandLine_Quotes()
    {
        var (exe, args) = ZapretDetector.SplitCommandLine("\"C:\\A B\\winws.exe\" --a=1 --hostlist=\"C:\\x y\\l.txt\"  --new");
        Assert.Equal("C:\\A B\\winws.exe", exe);
        Assert.Equal(["--a=1", "--hostlist=C:\\x y\\l.txt", "--new"], args);
    }

    [Fact]
    public void LooksLikeFlowseal_ByFolderLayout()
    {
        using var t = new TempDir();
        var root = t.Sub("zapret");
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        var exe = Path.Combine(root, "bin", "winws.exe");
        Assert.False(ZapretDetector.LooksLikeFlowseal(exe));
        File.WriteAllText(Path.Combine(root, "service.bat"), "");
        File.WriteAllText(Path.Combine(root, "general (ALT11).bat"), "");
        Assert.True(ZapretDetector.LooksLikeFlowseal(exe));
    }

    [Fact]
    public void ResultStore_MasksUserTargets_ClearHistoryRemovesFiles()
    {
        using var t = new TempDir();
        var p = new AppPaths(t.Sub("root"), Repo.Data);
        var secret = new Target("https://secret-site.example/path");
        var yt = new Target("https://www.youtube.com/");
        ProbeResult Pr(Target x, string d) => new(x, SslProtocols.Tls13, ProbeOutcome.Timeout, 1, 0, d);
        var v1 = new TargetVerdict("user", secret, false, 0, [Pr(secret, "таймаут (secret-site.example:443)")]);
        var v2 = new TargetVerdict("youtube", yt, true, 0.1, [Pr(yt, "HTTP 200")]);
        var s = Catalog[0];
        var report = new SelectionReport([v1, v2], [], [new StrategyScore(s, [v1, v2], 1, 2)], false);

        var file = ResultStore.Save(p, report);
        var text = File.ReadAllText(file);
        Assert.DoesNotContain("secret-site", text);
        Assert.Contains("свой адрес №1", text);
        Assert.Contains("www.youtube.com", text);   // общие цели остаются как есть
        Assert.True(StrategyStats.Load(p).ContainsKey(s.Id));   // статистике адреса не нужны

        Directory.CreateDirectory(p.Logs);
        File.WriteAllText(Path.Combine(p.Logs, "app-errors.log"), "x");
        Assert.Equal(2, ResultStore.ClearHistory(p));
        Assert.Empty(StrategyStats.Load(p));
    }
}
