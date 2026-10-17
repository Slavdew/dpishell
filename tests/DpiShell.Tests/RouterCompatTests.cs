using DpiShell.Core.Router;

namespace DpiShell.Tests;

/// <summary>Совместимость (router-api.md §5.4): уровни Ok / Warning / Blocked и доступность методов.</summary>
public class RouterCompatTests
{
    static readonly Version App = new(1, 0, 0, 0);
    static readonly string[] Methods020 = ["status", "enable", "disable", "select_start", "uninstall", "update_install", "update_check"];

    [Fact]
    public void SameApi_Ok_MethodsFromList()
    {
        var c = RouterCompatibility.Evaluate(new(1, 0), Methods020, "1.0.0", App);
        Assert.Equal(RouterCompatLevel.Ok, c.Level);
        Assert.Null(c.Text);
        Assert.True(c.CanCall("enable"));
        Assert.False(c.CanCall("dns_presets"));   // нет в status.methods — кнопка неактивна
        Assert.True(c.CanCall("uninstall"));
    }

    [Fact]
    public void OldRouterWithoutMethods_Ok_AllApi1()
    {
        var c = RouterCompatibility.Evaluate(new(1, 0), null, null, App);
        Assert.Equal(RouterCompatLevel.Ok, c.Level);
        Assert.True(c.CanCall("dns_presets"));
    }

    [Fact]
    public void NewerMinor_Warning_UpdateApp()
    {
        var c = RouterCompatibility.Evaluate(new(1, 3), [.. Methods020, "new_thing"], "1.0.0", App);
        Assert.Equal(RouterCompatLevel.Warning, c.Level);
        Assert.Contains("обновления приложения", c.Text);
        Assert.True(c.CanCall("enable"));
    }

    [Fact]
    public void OlderMinor_Warning_UpdateRouter()
    {
        var c = RouterCompatibility.Evaluate(new(1, 0), ["status", "enable"], null, App, supported: new(1, 2));
        Assert.Equal(RouterCompatLevel.Warning, c.Level);
        Assert.Contains("обновите DpiShell на роутере", c.Text);
        Assert.False(c.CanCall("select_start"));
        Assert.True(c.CanCall("update_install"));   // ядро договора — всегда
    }

    [Fact]
    public void NewerMajor_Blocked_OnlyCore()
    {
        var c = RouterCompatibility.Evaluate(new(2, 0), Methods020, "2.0.0", App);
        Assert.Equal(RouterCompatLevel.Blocked, c.Level);
        Assert.Contains("обновите приложение", c.Text);
        Assert.False(c.CanCall("enable"));
        Assert.True(c.CanCall("status"));
        Assert.True(c.CanCall("update_install"));
        Assert.True(c.CanCall("uninstall"));
        Assert.False(c.CanCall("update_check"));
    }

    [Fact]
    public void OlderMajor_Blocked_UpdateRouter()
    {
        var c = RouterCompatibility.Evaluate(new(1, 0), null, null, App, supported: new(2, 0));
        Assert.Equal(RouterCompatLevel.Blocked, c.Level);
        Assert.Contains("устарел", c.Text);
        Assert.False(c.CanCall("enable"));
        Assert.True(c.CanCall("uninstall"));
    }

    [Fact]
    public void NoApi_Blocked()
    {
        var c = RouterCompatibility.Evaluate(null, null, null, App);
        Assert.Equal(RouterCompatLevel.Blocked, c.Level);
        Assert.True(c.CanCall("status"));
    }

    [Theory]
    [InlineData("1.1.0", RouterCompatLevel.Blocked)]
    [InlineData("1.0.1", RouterCompatLevel.Blocked)]
    [InlineData("1.0.0", RouterCompatLevel.Ok)]
    [InlineData("1.0", RouterCompatLevel.Ok)]
    [InlineData("0.9.0-beta", RouterCompatLevel.Ok)]
    [InlineData("мусор", RouterCompatLevel.Ok)]
    [InlineData(null, RouterCompatLevel.Ok)]
    public void MinApp(string? minApp, RouterCompatLevel expected)
    {
        var c = RouterCompatibility.Evaluate(new(1, 0), Methods020, minApp, App);
        Assert.Equal(expected, c.Level);
        if (expected == RouterCompatLevel.Blocked)
        {
            Assert.Contains(minApp!, c.Text);
            Assert.False(c.CanCall("enable"));
            Assert.True(c.CanCall("update_install"));
        }
    }

    [Fact]
    public async Task FromStatus_FakeRouter020()
    {
        using var r = new FakeRouter().As020();
        using var cl = new RouterClient(r.Address);
        await cl.LoginAsync(() => r.Password);
        var c = RouterCompatibility.FromStatus(await cl.StatusAsync(), App);
        Assert.Equal(RouterCompatLevel.Ok, c.Level);
        Assert.True(c.CanCall("dns_set"));
        Assert.False(c.CanCall("нет_такого"));

        r.MinApp = "9.0.0";
        Assert.Equal(RouterCompatLevel.Blocked, RouterCompatibility.FromStatus(await cl.StatusAsync(), App).Level);
    }
}
