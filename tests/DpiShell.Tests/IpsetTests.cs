using DpiShell.Core;

namespace DpiShell.Tests;

static class Ipset
{
    /// <summary>n разных публичных /24 из 11.0.0.0/8 (≈14 байт строка: 4 000 строк ≈ 56 КБ).</summary>
    public static List<string> Nets(int n, int offset = 0) =>
        Enumerable.Range(offset, n).Select(i => $"11.{i / 256 % 256}.{i % 256}.0/24").ToList();

    public static string Text(IEnumerable<string> lines) => string.Join("\n", lines) + "\n";
}

public class IpsetValidatorTests
{
    static IpsetVerdict User(params string[] lines) => IpsetValidator.Validate(Ipset.Text(lines), IpsetKind.User);

    [Fact]
    public void Cidr_HostBitsZeroed_Overlap()
    {
        Assert.True(Cidr.TryParse("1.2.3.4/24", true, out var c, out var fixedHost, out _));
        Assert.True(fixedHost);
        Assert.Equal("1.2.3.0/24", c.ToString());
        Assert.True(Cidr.Parse("10.0.0.0/8").Overlaps(Cidr.Parse("10.1.2.0/24")));
        Assert.True(Cidr.Parse("10.1.2.0/24").Overlaps(Cidr.Parse("10.0.0.0/8")));
        Assert.False(Cidr.Parse("11.0.0.0/8").Overlaps(Cidr.Parse("10.0.0.0/8")));
        Assert.False(Cidr.Parse("::/0").Overlaps(Cidr.Parse("0.0.0.0/0")));   // разные семейства
        Assert.Equal("2a00:1450::/32", Cidr.Parse("2a00:1450:4001::1/32").ToString());
    }

    [Fact]
    public void CommentsEmptyAndDuplicates_Skipped_HostBitsCountedAsFixed()
    {
        var v = User("# комментарий", "", "  1.2.3.0/24  ", "1.2.3.0/24", "1.2.3.9/24", "2a00:1450::/32");
        Assert.True(v.Accepted);
        Assert.Equal(["1.2.3.0/24", "2a00:1450::/32"], v.Entries);
        Assert.Equal(2, v.Duplicates);
        Assert.Equal(1, v.Fixed);
        Assert.Equal(0, v.Dropped);
        Assert.Equal(1, v.Ipv4);
        Assert.Equal(1, v.Ipv6);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1.2.3/24")]
    [InlineData("1.2.3.4.5/32")]
    [InlineData("010.1.1.1/32")]      // ведущий ноль — восьмеричное у старых парсеров
    [InlineData("1.2.3.256/32")]
    [InlineData("1.2.3.4/33")]
    [InlineData("1.2.3.4/-1")]
    [InlineData("1.2.3.4/24/1")]
    [InlineData("2a00::/129")]
    [InlineData("fe80::1%3/128")]
    [InlineData("1.2.3.4 /24")]
    [InlineData("1.2.3.4/24 # x")]
    public void BadFormat_Dropped(string line)
    {
        var v = User(line, "1.2.3.0/24");
        Assert.Equal(1, v.Dropped);
        Assert.Equal(["1.2.3.0/24"], v.Entries);
        Assert.Single(v.Problems);
        Assert.Contains("строка 1", v.Problems[0]);
    }

    [Fact]
    public void BareAddress_AllowedInUser_RejectedInMain()
    {
        Assert.Equal(["8.8.8.8/32", "2001:db8::1/128"], User("8.8.8.8", "2001:db8::1").Entries);
        var main = IpsetValidator.Validate(Ipset.Text(["8.8.8.8", .. Ipset.Nets(4000)]), IpsetKind.Main);
        Assert.Equal(1, main.Dropped);
        Assert.Contains("нет длины префикса", main.Problems[0]);
    }

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("3.0.0.0/7")]
    [InlineData("::/0")]
    [InlineData("2000::/3")]
    [InlineData("2a00::/18")]
    public void TooWide_Dropped(string line)
    {
        var v = User(line);
        Assert.Equal(1, v.Dropped);
        Assert.Contains("слишком широкая", v.Problems[0]);
    }

    [Theory]
    [InlineData("3.0.0.0/8")]
    [InlineData("2a00::/19")]
    public void NarrowestAllowedWidth_Passes(string line) => Assert.Equal(0, User(line).Dropped);

    [Theory]
    [InlineData("0.0.0.0/8")]
    [InlineData("10.1.0.0/16")]
    [InlineData("100.64.1.0/24")]
    [InlineData("127.0.0.1/32")]
    [InlineData("169.254.1.0/24")]
    [InlineData("172.16.5.0/24")]
    [InlineData("172.0.0.0/8")]       // шире частной сети — тоже нельзя: содержит её
    [InlineData("192.168.1.1/32")]
    [InlineData("224.0.0.0/8")]
    [InlineData("239.1.1.1/32")]
    [InlineData("240.0.0.0/8")]
    [InlineData("255.255.255.255/32")]
    [InlineData("::1/128")]
    [InlineData("::/128")]
    [InlineData("::ffff:10.0.0.0/104")]
    [InlineData("fd12:3456::/32")]
    [InlineData("fe80::/64")]
    [InlineData("ff02::1/128")]
    public void ServiceNetworks_Dropped(string line)
    {
        var v = User(line);
        Assert.Equal(1, v.Dropped);
        Assert.Empty(v.Entries);
        Assert.Contains("служебная сеть", v.Problems[0]);
    }

    [Fact]
    public void ForbiddenNetworks_MatchShippedExcludeFile()
    {
        // Всё, что winws исключает по ipset-exclude.txt, валидатор тоже не пускает в список.
        foreach (var line in File.ReadAllLines(Path.Combine(Repo.Data, "lists", "ipset-exclude.txt")).Where(l => l.Trim().Length > 0))
            Assert.Contains(IpsetValidator.Forbidden, f => f.Net.Overlaps(Cidr.Parse(line.Trim())));
    }

    [Fact]
    public void ProblemsSample_Capped()
    {
        var v = User([.. Enumerable.Range(0, 50).Select(i => "junk" + i)]);
        Assert.Equal(50, v.Dropped);
        Assert.Equal(20, v.Problems.Count);
    }

    // ---------- список целиком (Main) ----------

    [Fact]
    public void Main_GoodList_Accepted()
    {
        var v = IpsetValidator.Validate(Ipset.Text(Ipset.Nets(4000)), IpsetKind.Main);
        Assert.True(v.Accepted, string.Join(" ", v.Errors));
        Assert.False(v.NeedsConfirm);
        Assert.Equal(4000, v.Entries.Count);
        Assert.Equal(4000 * 256 / 4294967296.0, v.Ipv4Share, 9);
    }

    [Fact]
    public void Main_TooFewLines_Rejected()
    {
        // 999 строк, но файл «нормального» размера — ловит именно правило числа строк.
        var v = IpsetValidator.Validate(Ipset.Text(Ipset.Nets(999)), IpsetKind.Main, bytes: 100_000);
        Assert.False(v.Accepted);
        Assert.Contains(v.Errors, e => e.Contains("меньше 1000"));
    }

    [Fact]
    public void Main_TooManyLines_Rejected()
    {
        var lines = Enumerable.Range(0, IpsetValidator.MaxLines + 1).Select(i => $"11.{i / 65536}.{i / 256 % 256}.{i % 256}/32");
        var v = IpsetValidator.Validate(Ipset.Text(lines), IpsetKind.Main);
        Assert.Contains(v.Errors, e => e.Contains("больше 100000"));
    }

    [Fact]
    public void Main_FileTooSmall_Rejected()
    {
        var v = IpsetValidator.Validate(Ipset.Text(Ipset.Nets(2000)), IpsetKind.Main);   // ≈28 КБ
        Assert.Contains(v.Errors, e => e.Contains("КБ"));
    }

    [Fact]
    public void Main_FileTooBig_RejectedWithoutReading()
    {
        using var t = new TempDir();
        var f = t.Sub("big.txt");
        File.WriteAllText(f, Ipset.Text(Ipset.Nets(4000)) + new string('#', (int)IpsetValidator.MaxBytes));
        var v = IpsetValidator.ValidateFile(f, IpsetKind.Main);
        Assert.False(v.Accepted);
        Assert.Contains("МБ", v.Errors[0]);
        Assert.Empty(v.Entries);
    }

    [Fact]
    public void Main_Ipv4ShareOver15Percent_Rejected()
    {
        // 40 сетей /8 = 15,6 % адресов IPv4.
        var wide = Enumerable.Range(11, 40).Where(a => a != 100 && a != 127).Select(a => $"{a}.0.0.0/8");
        var v = IpsetValidator.Validate(Ipset.Text([.. wide, .. Ipset.Nets(4000)]), IpsetKind.Main);
        Assert.True(v.Ipv4Share > 0.15);
        Assert.Contains(v.Errors, e => e.Contains("адресов IPv4"));
    }

    [Fact]
    public void Ipv4Share_OverlappingCountedOnce()
    {
        var v = User("11.0.0.0/8", "11.1.0.0/16", "11.1.2.0/24", "2a00::/19");
        Assert.Equal(1 / 256.0, v.Ipv4Share, 12);
    }

    [Fact]
    public void Main_MoreThanOnePercentBroken_Rejected()
    {
        var v = IpsetValidator.Validate(Ipset.Text([.. Ipset.Nets(4000), .. Enumerable.Range(0, 50).Select(i => "junk" + i)]), IpsetKind.Main);
        Assert.Equal(50, v.Dropped);
        Assert.Contains(v.Errors, e => e.Contains("повреждён"));
    }

    [Fact]
    public void Main_FewBrokenLines_AcceptedAndCounted()
    {
        var v = IpsetValidator.Validate(Ipset.Text([.. Ipset.Nets(4000), "10.0.0.0/8", "junk"]), IpsetKind.Main);
        Assert.True(v.Accepted);
        Assert.Equal(2, v.Dropped);
    }

    [Fact]
    public void Main_ChangeOver40Percent_NeedsConfirm()
    {
        var prev = Ipset.Nets(4000).ToHashSet();
        var v = IpsetValidator.Validate(Ipset.Text([.. Ipset.Nets(2000), .. Ipset.Nets(2000, 10_000)]), IpsetKind.Main, prev);
        Assert.True(v.Accepted);
        Assert.True(v.NeedsConfirm);
        Assert.Equal(2000, v.Added);
        Assert.Equal(2000, v.Removed);
        Assert.Contains("+2000/−2000", v.Confirm[0]);
    }

    [Fact]
    public void Main_ChangeUnder40Percent_NoConfirm()
    {
        var prev = Ipset.Nets(4000).ToHashSet();
        var v = IpsetValidator.Validate(Ipset.Text([.. Ipset.Nets(3500), .. Ipset.Nets(500, 10_000)]), IpsetKind.Main, prev);
        Assert.False(v.NeedsConfirm);
        Assert.Equal(500, v.Added);
        Assert.Equal(500, v.Removed);
    }

    // ---------- свои подсети (User) ----------

    [Fact]
    public void User_Over10000_Rejected() =>
        Assert.Contains(IpsetValidator.Validate(Ipset.Text(Ipset.Nets(10_001)), IpsetKind.User).Errors, e => e.Contains("10000"));

    [Fact]
    public void User_WideV4_NeedsConfirm()
    {
        var v = User("11.0.0.0/12");
        Assert.True(v.NeedsConfirm);
        Assert.False(User("11.0.0.0/16").NeedsConfirm);
    }

    // ---------- реальные данные ----------

    [Fact]
    public void RealList_PassesValidatorClean()
    {
        var f = Path.Combine(Repo.Data, "lists", IpLists.AllFile);
        var v = IpsetValidator.ValidateFile(f, IpsetKind.Main);
        Assert.True(v.Accepted, string.Join(" ", v.Errors));
        Assert.Empty(v.Confirm);
        Assert.Equal(0, v.Dropped);
        Assert.Equal(0, v.Fixed);
        Assert.Equal(0, v.Duplicates);
        Assert.InRange(v.Ipv4Share, 0.01, IpsetValidator.MaxIpv4Share);

        var m = DataManifest.Load(Path.Combine(Repo.Data, DataManifest.FileName)).Files[DataManifest.IpsetKey];
        Assert.Equal(m.Lines, v.Entries.Count);
        Assert.Equal(m.Ipv4Share!.Value, v.Ipv4Share, 3);
        Assert.StartsWith("https://", m.Url);
    }

    [Fact]
    public void DataManifest_TextHashIgnoresCrLf_BinaryDoesNot()
    {
        using var t = new TempDir();
        File.WriteAllText(t.Sub("a.txt"), "1.2.3.0/24\n4.5.6.0/24\n");
        File.WriteAllText(t.Sub("b.txt"), "1.2.3.0/24\r\n4.5.6.0/24\r\n");
        File.WriteAllText(t.Sub("a.bin"), "x\n");
        File.WriteAllText(t.Sub("b.bin"), "x\r\n");
        Assert.Equal(DataManifest.Sha256(t.Sub("a.txt")), DataManifest.Sha256(t.Sub("b.txt")));
        Assert.Equal(CoreInstaller.Sha256(t.Sub("a.txt")), DataManifest.Sha256(t.Sub("b.txt")));
        Assert.NotEqual(DataManifest.Sha256(t.Sub("a.bin")), DataManifest.Sha256(t.Sub("b.bin")));
    }

    [Fact]
    public void DataManifest_HashesMatchShippedFiles()
    {
        var m = DataManifest.Load(Path.Combine(Repo.Data, DataManifest.FileName));
        Assert.Contains(DataManifest.IpsetKey, m.Files.Keys);
        Assert.Contains("fake/ACTIVE_GAME_UDP.bin", m.Files.Keys);
        Assert.Empty(m.Mismatches(Repo.Data));
    }
}

public class IpListsTests
{
    static (TempDir T, AppPaths P, IpLists L) Seeded()
    {
        var t = new TempDir();
        var p = Make.Paths(t);
        new DataStore(p).Seed();
        return (t, p, new IpLists(p));
    }

    static string F(AppPaths p, string name) => Path.Combine(p.Lists, name);

    [Fact]
    public void Seed_InstallsPackageListAndModeFiles()
    {
        var (t, p, l) = Seeded();
        using var _ = t;
        Assert.True(l.HasList);
        var meta = l.Meta!;
        Assert.Equal("package", meta.Source);
        Assert.Equal(33048, meta.Lines);
        Assert.Equal(new DateTime(2026, 9, 26, 9, 41, 16, DateTimeKind.Utc), meta.Updated!.Value.ToUniversalTime());
        Assert.Equal(IpLists.Placeholder, File.ReadAllText(F(p, IpLists.NoneFile)).Trim());
        Assert.Equal("", File.ReadAllText(F(p, IpLists.AnyFile)));
        Assert.Equal(IpLists.Placeholder, File.ReadAllText(F(p, IpLists.UserFile)).Trim());
        Assert.Equal(IpLists.Placeholder, File.ReadAllText(F(p, IpLists.UserExcludeFile)).Trim());
        Assert.False(File.Exists(F(p, IpLists.PrevFile)));
    }

    [Fact]
    public void Rejected_OldListStays()
    {
        var (t, p, l) = Seeded();
        using var _ = t;
        var before = File.ReadAllBytes(F(p, IpLists.AllFile));
        var bad = t.Sub("bad.txt");
        File.WriteAllText(bad, Ipset.Text(["0.0.0.0/0", .. Ipset.Nets(100)]));
        var r = l.UpdateFromFile(bad);
        Assert.False(r.Applied);
        Assert.False(r.NeedsConfirm);
        Assert.Contains("оставлен прежний", r.Message);
        Assert.Equal(before, File.ReadAllBytes(F(p, IpLists.AllFile)));
        Assert.True(l.HasList);
    }

    [Fact]
    public void BigChange_NeedsConfirm_ThenAppliedWithPrev()
    {
        var (t, p, l) = Seeded();
        using var _ = t;
        var before = File.ReadAllBytes(F(p, IpLists.AllFile));
        var other = t.Sub("other.txt");
        File.WriteAllText(other, Ipset.Text(Ipset.Nets(4000)));

        var r = l.UpdateFromFile(other);
        Assert.False(r.Applied);
        Assert.True(r.NeedsConfirm);
        Assert.Equal(before, File.ReadAllBytes(F(p, IpLists.AllFile)));

        r = l.UpdateFromFile(other, confirmed: true);
        Assert.True(r.Applied, r.Message);
        Assert.Equal(4000, l.Meta!.Lines);
        Assert.Equal("file", l.Meta.Source);
        Assert.Equal(before, File.ReadAllBytes(F(p, IpLists.PrevFile)));
        Assert.True(l.HasList);
    }

    [Fact]
    public void AppliedList_IsCanonical()
    {
        var (t, p, l) = Seeded();
        using var _ = t;
        var f = t.Sub("x.txt");
        File.WriteAllText(f, "# шапка\r\n" + string.Join("\r\n", Ipset.Nets(4000).Select(n => n.Replace(".0/24", ".7/24"))) + "\r\n10.0.0.0/8\r\n");
        var r = l.UpdateFromFile(f, confirmed: true);
        Assert.True(r.Applied, r.Message);
        Assert.Equal(4000, r.Verdict!.Fixed);
        var text = File.ReadAllText(F(p, IpLists.AllFile));
        Assert.DoesNotContain("#", text);
        Assert.DoesNotContain("\r", text);
        Assert.DoesNotContain("10.0.0.0", text);
        Assert.StartsWith("11.0.0.0/24\n", text);
    }

    [Fact]
    public void TamperedList_RestoredFromPackageOnSeed()
    {
        var (t, p, l) = Seeded();
        using var _ = t;
        File.AppendAllText(F(p, IpLists.AllFile), "0.0.0.0/0\n");
        Assert.False(l.HasList);
        Assert.True(l.SeedFromPackage());
        Assert.True(l.HasList);
        Assert.DoesNotContain("0.0.0.0/0", File.ReadAllText(F(p, IpLists.AllFile)));
    }

    [Fact]
    public void Seed_KeepsNewerDownloadedList()
    {
        var (t, p, l) = Seeded();
        using var _ = t;
        var f = t.Sub("new.txt");
        File.WriteAllText(f, Ipset.Text(Ipset.Nets(4000)));
        Assert.True(l.UpdateFromFile(f, confirmed: true, updated: new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc)).Applied);
        new DataStore(p).Seed();
        Assert.Equal(4000, l.Meta!.Lines);
        Assert.False(l.SeedFromPackage());
    }

    [Fact]
    public void PackageHashMismatch_NotInstalled()
    {
        using var t = new TempDir();
        var seed = t.Sub("seed");
        Directory.CreateDirectory(Path.Combine(seed, "lists"));
        File.WriteAllText(Path.Combine(seed, "lists", IpLists.AllFile), Ipset.Text(Ipset.Nets(4000)));
        File.WriteAllText(Path.Combine(seed, DataManifest.FileName),
            """{"version":"x","files":{"lists/ipset-all.txt":{"sha256":"00","updated":"2026-01-01T00:00:00Z"}}}""");
        var l = new IpLists(new AppPaths(t.Sub("root"), seed));
        Assert.False(l.SeedFromPackage());
        Assert.False(l.HasList);
    }

    [Fact]
    public async Task Manifest_NonHttpsUrl_Refused()
    {
        var (t, p, l) = Seeded();
        using var _ = t;
        var m = new DataManifest("x", new() { [DataManifest.IpsetKey] = new("00", "http://example.com/list.txt") });
        using var http = new HttpClient();
        var r = await l.UpdateFromManifestAsync(http, m);
        Assert.False(r.Applied);
        Assert.Contains("HTTPS", r.Message);
    }

    [Fact]
    public void WriteUser_KeepsValid_PlaceholderWhenEmpty_WideNeedsConfirm()
    {
        var (t, p, l) = Seeded();
        using var _ = t;
        var v = l.WriteUser(["8.8.8.0/24", "192.168.0.0/24", "junk"]);
        Assert.Equal(2, v.Dropped);
        Assert.Equal(["8.8.8.0/24"], l.Read(IpLists.UserFile));

        v = l.WriteUser(["11.0.0.0/12"]);
        Assert.True(v.NeedsConfirm);
        Assert.Equal(["8.8.8.0/24"], l.Read(IpLists.UserFile));   // без подтверждения не записано
        l.WriteUser(["11.0.0.0/12"], confirmed: true);
        Assert.Equal(["11.0.0.0/12"], l.Read(IpLists.UserFile));

        l.WriteUser([], exclude: true);
        Assert.Equal(IpLists.Placeholder, File.ReadAllText(F(p, IpLists.UserExcludeFile)).Trim());
    }
}
