using DpiShell.Core;

namespace DpiShell.Tests;

public class NormalizeDomainTests
{
    [Theory]
    [InlineData("https://Site.com/path", "site.com")]
    [InlineData("site.com", "site.com")]
    [InlineData("  SITE.COM  ", "site.com")]
    [InlineData("WWW.Example.COM", "example.com")]
    [InlineData("http://www.example.com:8080/x?y=1#z", "example.com")]
    [InlineData("example.com/path", "example.com")]
    [InlineData("example.com.", "example.com")]
    [InlineData("sub.example.co.uk", "sub.example.co.uk")]
    [InlineData("xn--80ak6aa92e.com", "xn--80ak6aa92e.com")]
    [InlineData("https://www.xn--d1acpjx3f.xn--p1ai/", "xn--d1acpjx3f.xn--p1ai")]
    [InlineData("a-b.example.org", "a-b.example.org")]
    public void Valid(string raw, string expected) => Assert.Equal(expected, DataStore.NormalizeDomain(raw));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# комментарий")]
    [InlineData("not a domain")]
    [InlineData("example")]
    [InlineData("-bad.com")]
    [InlineData("bad-.com")]
    [InlineData("exa_mple.com")]
    [InlineData("*.example.com")]
    [InlineData("example.c")]
    [InlineData("example.123")]
    public void Garbage_IsNull(string raw) => Assert.Null(DataStore.NormalizeDomain(raw));

    // Зафиксированное поведение: без точки и TLD из букв — не домен для hostlist.
    [Theory]
    [InlineData("localhost")]
    [InlineData("192.168.1.1")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("[::1]")]
    public void LocalhostAndIp_AreNull(string raw) => Assert.Null(DataStore.NormalizeDomain(raw));

    // БАГ: кириллический домен отбрасывается. Uri.Host отдаёт Unicode, а регэксп ждёт punycode;
    // winws сравнивает с SNI (там punycode), значит нужно Uri.IdnHost.
    [Theory]
    [InlineData("президент.рф", "xn--d1abbgf6aiiy.xn--p1ai")]
    [InlineData("https://Пример.РФ/", "xn--e1afmkfd.xn--p1ai")]
    public void Regression_CyrillicIdn_ShouldBecomePunycode(string raw, string expected) =>
        Assert.Equal(expected, DataStore.NormalizeDomain(raw));
}

public class DataStoreTests
{
    [Fact]
    public void Seed_CreatesStandardAndUserFilesWithPlaceholder()
    {
        using var t = new TempDir();
        var p = Make.Paths(t);
        new DataStore(p).Seed();

        foreach (var f in Directory.GetFiles(Path.Combine(Repo.Data, "lists")))
            Assert.True(File.Exists(Path.Combine(p.Lists, Path.GetFileName(f))));
        foreach (var f in Directory.GetFiles(Path.Combine(Repo.Data, "fake")))
            Assert.True(File.Exists(Path.Combine(p.Fake, Path.GetFileName(f))));

        foreach (var u in new[] { DataStore.UserGeneral, DataStore.UserExclude })
        {
            var text = File.ReadAllText(Path.Combine(p.Lists, u)).Trim();
            Assert.Equal("domain.example.abc", text);
        }
        Assert.Empty(new DataStore(p).ReadUserDomains());
        Assert.Empty(new DataStore(p).ReadUserDomains(DataStore.UserExclude));
    }

    [Fact]
    public void Seed_DoesNotOverwriteUserFiles_ButRefreshesStandard()
    {
        using var t = new TempDir();
        var p = Make.Paths(t);
        p.EnsureDirectories();
        File.WriteAllText(Path.Combine(p.Lists, DataStore.UserGeneral), "my.com\n");
        File.WriteAllText(Path.Combine(p.Lists, DataStore.UserExclude), "skip.com\n");
        File.WriteAllText(Path.Combine(p.Lists, "general.txt"), "stale\n");

        new DataStore(p).Seed();

        Assert.Equal("my.com\n", File.ReadAllText(Path.Combine(p.Lists, DataStore.UserGeneral)));
        Assert.Equal("skip.com\n", File.ReadAllText(Path.Combine(p.Lists, DataStore.UserExclude)));
        Assert.Equal(File.ReadAllBytes(Path.Combine(Repo.Data, "lists", "general.txt")), File.ReadAllBytes(Path.Combine(p.Lists, "general.txt")));
    }

    [Fact]
    public void Seed_MissingSeedData_Throws()
    {
        using var t = new TempDir();
        var p = new AppPaths(t.Sub("root"), t.Sub("noseed"));
        Assert.Throws<DirectoryNotFoundException>(() => new DataStore(p).Seed());
    }

    [Fact]
    public void WriteUserDomains_NormalizesDedupsAndReturnsRejected()
    {
        using var t = new TempDir();
        var p = Make.Paths(t);
        p.EnsureDirectories();
        var ds = new DataStore(p);

        var rejected = ds.WriteUserDomains(["https://A.com/x", "a.com", "www.a.com", "  junk!  ", "", "   ", "# note", "b.org"]);

        Assert.Equal(["junk!", "# note"], rejected);
        Assert.Equal(["a.com", "b.org"], File.ReadAllLines(Path.Combine(p.Lists, DataStore.UserGeneral)));
        Assert.Equal(["a.com", "b.org"], ds.ReadUserDomains());
    }

    [Fact]
    public void WriteUserDomains_Empty_WritesPlaceholder_ReadReturnsNothing()
    {
        using var t = new TempDir();
        var p = Make.Paths(t);
        p.EnsureDirectories();
        var ds = new DataStore(p);

        var rejected = ds.WriteUserDomains([], DataStore.UserExclude);

        Assert.Empty(rejected);
        Assert.Equal(["domain.example.abc"], File.ReadAllLines(Path.Combine(p.Lists, DataStore.UserExclude)));
        Assert.Empty(ds.ReadUserDomains(DataStore.UserExclude));
    }

    [Fact]
    public void WriteUserDomains_OnlyGarbage_WritesPlaceholder()
    {
        using var t = new TempDir();
        var p = Make.Paths(t);
        p.EnsureDirectories();
        var ds = new DataStore(p);
        Assert.Equal(["x y"], ds.WriteUserDomains(["x y"]));
        Assert.Equal(["domain.example.abc"], File.ReadAllLines(Path.Combine(p.Lists, DataStore.UserGeneral)));
    }

    [Fact]
    public void ReadUserDomains_SkipsCommentsBlanksPlaceholder_MissingFileEmpty()
    {
        using var t = new TempDir();
        var p = Make.Paths(t);
        p.EnsureDirectories();
        var ds = new DataStore(p);
        Assert.Empty(ds.ReadUserDomains());
        File.WriteAllLines(Path.Combine(p.Lists, DataStore.UserGeneral), ["# c", "", "domain.example.abc", "  x.com  "]);
        Assert.Equal(["x.com"], ds.ReadUserDomains());
    }
}
