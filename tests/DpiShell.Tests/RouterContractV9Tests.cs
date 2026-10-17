using DpiShell.Core;
using DpiShell.Core.Router;

namespace DpiShell.Tests;

/// <summary>Дополнения договора router-api §5 на фейковом роутере.</summary>
public sealed class RouterContractV9Tests
{
    static async Task<RouterClient> LoggedIn(FakeRouter r)
    {
        var c = new RouterClient(r.Address);
        await c.LoginAsync(() => r.Password);
        return c;
    }

    [Fact]
    public async Task Status_ModeAndTcpTimestampsAndAutoSelect()
    {
        using var r = new FakeRouter();
        using var c = await LoggedIn(r);
        var s = await c.StatusAsync();
        Assert.Equal("all", s.Mode);
        Assert.True(s.NeedsTcpTimestamps);
        Assert.False(s.Schedule!.AutoSelect);
        Assert.False(s.Jobs!.SiteCheck);
    }

    [Fact]
    public async Task ModeSet_And_ScheduleAutoSelect()
    {
        using var r = new FakeRouter();
        using var c = await LoggedIn(r);
        Assert.True((await c.ModeSetAsync(allSites: false)).Started);
        Assert.Equal("lists", (await c.StatusAsync()).Mode);
        await c.ScheduleSetAsync(true, 0.5, autoSelect: true);
        var sch = (await c.StatusAsync()).Schedule!;
        Assert.Equal(0.5, sch.Hours);
        Assert.True(sch.AutoSelect);
    }

    [Fact]
    public async Task SiteCheck_ResultSuggestsExclude_BareNameAllowed()
    {
        using var r = new FakeRouter();
        using var c = await LoggedIn(r);
        Assert.True((await c.SiteCheckAsync("example.com")).Started);
        var res = await c.SiteCheckResultAsync();
        Assert.Equal("example.com", res.Url);
        Assert.False(res.WithBypass!.Ok);
        Assert.True(res.WithoutBypass!.Ok);
        Assert.True(res.SuggestExclude);
        await Assert.ThrowsAsync<ArgumentException>(() => c.SiteCheckAsync("a b"));
    }

    [Fact]
    public async Task Dns_StatusAndBackgroundJobs()
    {
        using var r = new FakeRouter();
        using var c = await LoggedIn(r);
        Assert.True((await c.DnsEnableAsync()).Started);
        Assert.True((await c.DnsRestoreAsync()).Started);
        Assert.True((await c.DnsAnalyzeAsync()).Started);
        var d = await c.DnsStatusAsync();
        Assert.Equal("provider", d.Mode);
        Assert.Equal(2, d.Servers!.Count);
        // job — последнее DNS-задание: сначала идёт, потом итог.
        Assert.Equal("dns_analyze", d.Job!.Action);
        Assert.True(d.Job.Running);
        d = await c.DnsStatusAsync();
        Assert.False(d.Job!.Running);
        Assert.True(d.Job.Ok);
        Assert.NotNull(d.Job.At);
    }

    [Fact]
    public async Task ListsGet_StandardExclude_ReadOnly()
    {
        using var r = new FakeRouter();
        using var c = await LoggedIn(r);
        Assert.False((await c.ListsGetAsync("exclude")).Editable);
        Assert.True((await c.ListsGetAsync("user-exclude")).Editable);
        await Assert.ThrowsAsync<ArgumentException>(() => c.ListsSetAsync("exclude", ["x.ru"]));
    }

    [Theory]
    [InlineData("found-261016-2213-2", "u-found-261016-2213-2")]
    [InlineData("u-already", "u-already")]
    [InlineData("Found_X.Y", "u-found-x-y")]
    [InlineData("found-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "u-found-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void ToRouter_IdMatchesContractPattern(string id, string expected)
    {
        var u = RouterClient.ToRouter(new Strategy(id, new string('Н', 100), "search", new() { ["google"] = "--dpi-desync=multisplit" }));
        Assert.Equal(expected, u.Id);
        Assert.Matches("^u-[a-z0-9-]{1,48}$", u.Id);
        Assert.Equal(80, u.Title.Length);
    }

    [Theory]
    [InlineData("192.168.1.1", true)]
    [InlineData("10.0.0.1", true)]
    [InlineData("172.20.1.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("100.64.0.1", false)]   // CGNAT провайдера — не наш роутер
    [InlineData("8.8.8.8", false)]
    public void IsPrivate_OnlyRfc1918(string ip, bool expected) =>
        Assert.Equal(expected, RouterClient.IsPrivate(System.Net.IPAddress.Parse(ip)));
}
