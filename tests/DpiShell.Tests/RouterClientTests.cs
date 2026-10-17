using DpiShell.Core;
using DpiShell.Core.Router;

namespace DpiShell.Tests;

/// <summary>Случаи docs/07-router-api.md §7 на фейковом роутере.</summary>
public sealed class RouterClientTests
{
    static async Task<RouterClient> LoggedIn(FakeRouter r)
    {
        var c = new RouterClient(r.Address);
        await c.LoginAsync(() => r.Password);
        return c;
    }

    // ---------- §2 поиск ----------

    [Fact]
    public async Task Detect_Found_ApiOk()
    {
        using var r = new FakeRouter();
        using var c = new RouterClient(r.Address);
        var d = await c.DetectAsync();
        Assert.Equal(RouterFound.Ok, d.Found);
        Assert.True(d.CanControl);
        Assert.Equal("0.1.0", d.Status!.Version);
        Assert.Equal("alt10", d.Status.Strategy);
        Assert.True(d.Status.Enabled);
    }

    [Fact]
    public async Task Detect_NotFound_NoServer()
    {
        // Порт, на котором никто не слушает.
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start(); var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        using var c = new RouterClient($"localhost:{port}");
        Assert.Equal(RouterFound.NotFound, (await c.DetectAsync()).Found);
    }

    [Theory]
    [InlineData(null)]                                                     // 404
    [InlineData("""{ "product": "luci", "api": 1 }""")]                    // другой product
    [InlineData("<html>это меню стокового роутера</html>")]                // не JSON
    public async Task Detect_NotFound_Variants(string? statusJson)
    {
        using var r = new FakeRouter { StatusJson = statusJson };
        using var c = new RouterClient(r.Address);
        Assert.Equal(RouterFound.NotFound, (await c.DetectAsync()).Found);
    }

    [Fact]
    public async Task Detect_StatusWithoutApi_RouterTooOld()
    {
        using var r = new FakeRouter { StatusJson = """{ "product": "dpishell-router", "version": "0.0.9", "enabled": false }""" };
        using var c = new RouterClient(r.Address);
        var d = await c.DetectAsync();
        Assert.Equal(RouterFound.RouterTooOld, d.Found);
        Assert.False(d.CanControl);
    }

    [Fact]
    public async Task Detect_NewerApi_AppTooOld()
    {
        using var r = new FakeRouter { StatusJson = """{ "product": "dpishell-router", "api": 2, "enabled": true }""" };
        using var c = new RouterClient(r.Address);
        Assert.Equal(RouterFound.AppTooOld, (await c.DetectAsync()).Found);
    }

    /// <summary>Роутеры до 0.2.0 отдают api числом, новые — строкой «мажор.минор»; совместимость решает мажор.</summary>
    [Theory]
    [InlineData("1", RouterFound.Ok)]
    [InlineData("\"1.0\"", RouterFound.Ok)]
    [InlineData("\"1.7\"", RouterFound.Ok)]
    [InlineData("\"2.0\"", RouterFound.AppTooOld)]
    [InlineData("\"0.9\"", RouterFound.RouterTooOld)]
    [InlineData("\"abc\"", RouterFound.NotFound)]
    public async Task Detect_ApiNumberOrString_ByMajor(string api, RouterFound expected)
    {
        using var r = new FakeRouter { StatusJson = $$"""{ "product": "dpishell-router", "api": {{api}}, "enabled": true, "min_app": "1.0.0" }""" };
        using var c = new RouterClient(r.Address);
        var d = await c.DetectAsync();
        Assert.Equal(expected, d.Found);
        if (expected == RouterFound.Ok) Assert.Equal("1.0.0", d.Status!.MinApp);
    }

    [Theory]
    [InlineData("http://192.168.1.1")]
    [InlineData("192.168.1.1/cgi-bin")]
    public void Address_WithSchemeOrPath_Rejected(string a) => Assert.Throws<ArgumentException>(() => new RouterClient(a));

    // ---------- §3 вход ----------

    [Fact]
    public async Task Login_WrongPassword_AccessDenied_AndPasswordNotInMessage()
    {
        using var r = new FakeRouter();
        using var c = new RouterClient(r.Address);
        var e = await Assert.ThrowsAsync<RouterException>(() => c.LoginAsync(() => "wrong-pass-123"));
        Assert.Equal(RouterFailure.AccessDenied, e.Failure);
        Assert.DoesNotContain("wrong-pass-123", e.Message);
        Assert.Contains("Неверный пароль", e.Message);
    }

    [Fact]
    public async Task Login_SendsRootAndTimeout_AsInContract()
    {
        using var r = new FakeRouter();
        using var c = await LoggedIn(r);
        var p = r.Calls[0]["params"]!.AsArray();
        Assert.Equal("00000000000000000000000000000000", p[0]!.GetValue<string>());
        Assert.Equal("session", p[1]!.GetValue<string>());
        Assert.Equal("login", p[2]!.GetValue<string>());
        Assert.Equal("root", p[3]!["username"]!.GetValue<string>());
        Assert.Equal(600, p[3]!["timeout"]!.GetValue<int>());
        Assert.Equal("2.0", r.Calls[0]["jsonrpc"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false)]   // код 6 в result, как в договоре
    [InlineData(true)]    // «Access denied» -32002 — так отвечает настоящий uhttpd-mod-ubus (вопрос 2)
    public async Task ExpiredSession_ReloginOnce_ThenSuccess(bool rpcError)
    {
        using var r = new FakeRouter { UseRpcAccessDeniedError = rpcError };
        using var c = await LoggedIn(r);
        r.ExpireSessionOnce = true;
        var st = await c.StatusAsync();
        Assert.Equal("alt10", st.Strategy);
        Assert.Equal(2, r.Logins);
    }

    [Fact]
    public async Task ExpiredSession_PasswordChangedMeanwhile_AccessDenied()
    {
        using var r = new FakeRouter();
        using var c = new RouterClient(r.Address);
        await c.LoginAsync(() => "secret");          // сохранённый на ПК пароль
        r.ExpireSessionOnce = true;
        r.Password = "changed-on-router";            // а на роутере его сменили
        var e = await Assert.ThrowsAsync<RouterException>(() => c.StatusAsync());
        Assert.Equal(RouterFailure.AccessDenied, e.Failure);
    }

    // ---------- §4–5 вызовы ----------

    [Fact]
    public async Task Status_ParsesContractExample()
    {
        using var r = new FakeRouter();
        using var c = await LoggedIn(r);
        var s = await c.StatusAsync();
        Assert.Equal(new ApiVersion(1, 0), s.Api);
        Assert.True(s.Core!.HashOk);
        Assert.Equal(["alt9", "alt13"], s.Backups);
        Assert.Equal("timer", s.LastCheck!.Source);
        Assert.Equal(new DateTime(2026, 10, 16, 21, 0, 0, DateTimeKind.Utc), s.LastCheck.At!.Value.ToUniversalTime());
        Assert.Equal(2, s.LastCheck.Groups!.Count);
        Assert.Equal(1, s.Schedule!.Hours);
        Assert.False(s.Jobs!.CoreInstall);
        Assert.Equal("flow_offload", s.Warnings![0].Code);
        var p = r.Calls[^1]["params"]!.AsArray();
        Assert.Equal("dpishell", p[1]!.GetValue<string>());
        Assert.Equal("status", p[2]!.GetValue<string>());
    }

    [Fact]
    public async Task ReadMethods_ParseContractExamples()
    {
        using var r = new FakeRouter();
        using var c = await LoggedIn(r);
        Assert.Equal(6, (await c.StrategiesAsync()).Items[0].Passed);
        Assert.True((await c.TargetsGetAsync()).Items[0].Important);
        Assert.Equal(["example.com"], (await c.ListsGetAsync("user-general")).Lines);
        var d = await c.DiagAsync();
        Assert.Equal("zapret2", d.Conflicts![0].Name);
        Assert.True(d.Dns!.Spoofed);
        Assert.Equal(18000, d.OverlayFreeKb);
        Assert.Equal("отчёт роутера", (await c.ReportAsync()).Text);
    }

    [Fact]
    public async Task MethodMissing_Code4_NotSupported()
    {
        using var r = new FakeRouter();
        r.MissingMethods.Add("strategies_user_set");
        using var c = await LoggedIn(r);
        var e = await Assert.ThrowsAsync<RouterException>(() =>
            c.StrategiesUserSetAsync([new RouterUserStrategy("u-x", "x", new() { ["google"] = "--dpi-desync=multisplit" })]));
        Assert.Equal(RouterFailure.NotSupported, e.Failure);
        Assert.Equal(4, e.UbusCode);
    }

    [Fact]
    public async Task Busy_AppError()
    {
        using var r = new FakeRouter { BusyOn = "enable" };
        using var c = await LoggedIn(r);
        var e = await Assert.ThrowsAsync<RouterException>(() => c.EnableAsync());
        Assert.Equal(RouterFailure.App, e.Failure);
        Assert.Equal("busy", e.AppError!.Code);
        Assert.Equal("Идёт подбор", e.Message);
    }

    [Fact]
    public async Task Select_FromRunningToBestApplied_ThenApplyOther()
    {
        using var r = new FakeRouter { SelectSteps = 3 };
        using var c = await LoggedIn(r);
        Assert.True((await c.SelectStartAsync(["youtube", "discord"], full: true, deep: false)).Started);
        var args = r.Calls[^1]["params"]![3]!;
        Assert.Equal("full", args["mode"]!.GetValue<string>());
        Assert.Equal("main", args["depth"]!.GetValue<string>());

        RouterSelectProgress p;
        int polls = 0;
        do { p = await c.SelectProgressAsync(); polls++; } while (p.Running && polls < 10);
        Assert.Equal(4, polls);
        Assert.Equal("alt", p.Best);
        Assert.True(p.Applied);
        Assert.False(p.Cancelled);
        Assert.Equal("app", p.StartedBy);
        Assert.Equal(["alt9", "alt13"], p.Backups);
        Assert.Equal(0.88, p.Rows![0].TargetShare);

        await c.ApplyAsync(p.Best!, ["alt9"]);
        var a = r.Calls[^1]["params"]![3]!;
        Assert.Equal("alt", a["id"]!.GetValue<string>());
        Assert.Equal("alt9", a["backups"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task Limits_CheckedOnPc()
    {
        using var r = new FakeRouter();
        using var c = await LoggedIn(r);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => c.ScheduleSetAsync(true, 5));
        await Assert.ThrowsAsync<ArgumentException>(() => c.TargetsSetAsync(Enumerable.Range(0, 51).Select(i => new RouterTarget($"https://s{i}.example/", false)).ToList()));
        await Assert.ThrowsAsync<ArgumentException>(() => c.ListsGetAsync("../../etc/passwd"));
        await c.ScheduleSetAsync(true, 6);
    }

    [Fact]
    public async Task PasswordNeverLeavesExceptLogin()
    {
        using var r = new FakeRouter();
        using var c = await LoggedIn(r);
        await c.StatusAsync();
        await c.EnableAsync();
        Assert.Single(r.RawBodies, b => b.Contains(r.Password));   // только сам вход
    }

    [Fact]
    public void ToRouter_KeepsOnlyRoles_PrefixesId()
    {
        var s = new Strategy("found-261016-2213-2", "Найденная 2", "search", new() { ["google"] = "--dpi-desync=multidisorder --dpi-desync-split-pos=midsld" });
        var u = RouterClient.ToRouter(s);
        Assert.Equal("u-found-261016-2213-2", u.Id);
        Assert.Equal(s.Roles["google"], u.Roles["google"]);
    }

    // ---------- пароль ----------

    [Fact]
    public void Secrets_RoundTrip_AndCiphertextHidesPassword()
    {
        var enc = RouterSecrets.Protect("пароль роутера");
        Assert.DoesNotContain("пароль", enc);
        Assert.Equal("пароль роутера", RouterSecrets.Unprotect(enc));
        Assert.Null(RouterSecrets.Unprotect("не-base64!"));
        Assert.Null(RouterSecrets.Unprotect(Convert.ToBase64String([1, 2, 3])));
        Assert.Null(RouterSecrets.Unprotect(null));
    }
}
