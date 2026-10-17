using System.Security.Cryptography;
using System.Text;
using DpiShell.Core.Router;

namespace DpiShell.Tests;

/// <summary>Ядро договора §5.4 через API (uninstall, update_install, update_check, status.update) и пакеты обновления по SSH.</summary>
public class RouterUpdateUninstallTests
{
    static async Task<RouterClient> Login(FakeRouter r)
    {
        var c = new RouterClient(r.Address);
        await c.LoginAsync(() => r.Password);
        return c;
    }

    [Fact]
    public async Task Status020_ParsesMethodsMinAppUpdate()
    {
        using var r = new FakeRouter().As020();
        using var c = await Login(r);
        var s = await c.StatusAsync();
        Assert.Equal(new ApiVersion(1, 0), s.Api);
        Assert.Equal("1.0.0", s.MinApp);
        Assert.Contains("update_install", s.Methods!);
        Assert.True(s.Update!.Available);
        Assert.Equal("0.2.1-r1", s.Update.Version);
        Assert.Equal(["Глубокий поиск стратегий на самом роутере"], s.Update.Notes);
        Assert.Null(s.Update.Job);
        Assert.False(s.Jobs!.Update);
    }

    [Fact]
    public async Task Status_OldRouter_NoUpdateField()
    {
        using var r = new FakeRouter();
        using var c = await Login(r);
        var s = await c.StatusAsync();
        Assert.Null(s.Update);
        Assert.Null(s.Methods);
    }

    [Theory]
    [InlineData(RouterUpdateSource.Feed, "feed")]
    [InlineData(RouterUpdateSource.Local, "local")]
    public async Task UpdateInstall_JobRunsThenDone(RouterUpdateSource from, string arg)
    {
        using var r = new FakeRouter().As020();
        r.LocalUpdateReady = true;
        using var c = await Login(r);
        Assert.True((await c.UpdateInstallAsync(from)).Started);
        Assert.Equal(arg, r.Calls.Last()["params"]![3]!["from"]!.GetValue<string>());

        var s1 = await c.StatusAsync();
        Assert.True(s1.Jobs!.Update);
        Assert.True(s1.Update!.Job!.Running);
        Assert.Equal(arg, s1.Update.Job.From);

        var s2 = await c.StatusAsync();
        Assert.False(s2.Update!.Job!.Running);
        Assert.True(s2.Update.Job.Ok);
        Assert.Equal("Обновлено до 0.2.1-r1", s2.Update.Job.Message);
        Assert.NotNull(s2.Update.Job.At);
        Assert.Equal("0.2.1-r1", s2.Version);
        Assert.False(s2.Update.Available);
    }

    [Fact]
    public async Task UpdateInstall_LocalWithoutFiles_Invalid()
    {
        using var r = new FakeRouter().As020();
        using var c = await Login(r);
        var e = await Assert.ThrowsAsync<RouterException>(() => c.UpdateInstallAsync(RouterUpdateSource.Local));
        Assert.Equal(RouterFailure.App, e.Failure);
        Assert.Equal("from", e.AppError!.Field);
    }

    [Fact]
    public async Task UpdateCheck_Started()
    {
        using var r = new FakeRouter().As020();
        using var c = await Login(r);
        Assert.True((await c.UpdateCheckAsync()).Started);
        Assert.Equal(1, r.UpdateCheckCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Uninstall_Started_ThenObjectGone(bool keep)
    {
        using var r = new FakeRouter().As020();
        using var c = await Login(r);
        Assert.True((await c.UninstallAsync(keep)).Started);
        Assert.Equal(keep, r.UninstallKeepSettings);
        // Объект dpishell исчез — дальше 4 («нет функции»), итог — по SSH из /tmp/dpishell-uninstall.json.
        var e = await Assert.ThrowsAsync<RouterException>(() => c.StatusAsync());
        Assert.Equal(RouterFailure.NotSupported, e.Failure);
    }

    [Fact]
    public async Task UploadUpdate_CleansDir_UploadsVerified_WritesSums()
    {
        using var d = new TempDir();
        var files = new List<RouterUpdateFile>();
        foreach (var name in new[] { "dpishell-0.2.1-r1.apk", "luci-app-dpishell-0.2.1-r1.apk" })
        {
            var path = d.Sub(name);
            File.WriteAllText(path, "пакет " + name);
            files.Add(new(name, path, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))));
        }
        var ssh = new FakeSsh();
        ssh.Files["/tmp/dpishell-update/old.apk"] = [1];
        await new RouterSsh(ssh).UploadUpdateAsync(files);

        Assert.Equal("rm -rf /tmp/dpishell-update && mkdir -p /tmp/dpishell-update && chmod 700 /tmp/dpishell-update", ssh.Runs[0].Command);
        Assert.False(ssh.Files.ContainsKey("/tmp/dpishell-update/old.apk"));
        Assert.Equal(["/tmp/dpishell-update/dpishell-0.2.1-r1.apk", "/tmp/dpishell-update/luci-app-dpishell-0.2.1-r1.apk", "/tmp/dpishell-update/SHA256SUMS"], ssh.Uploads);
        var sums = Encoding.UTF8.GetString(ssh.Files["/tmp/dpishell-update/SHA256SUMS"]);
        Assert.Equal($"{files[0].Sha256}  dpishell-0.2.1-r1.apk\n{files[1].Sha256}  luci-app-dpishell-0.2.1-r1.apk\n", sums);
    }

    [Theory]
    [InlineData("zapret-1.0.apk")]
    [InlineData("../dpishell-1.0.apk")]
    [InlineData("dpishell-beta.apk")]
    [InlineData("dpishell-1.0.apk; rm -rf /")]
    public async Task UploadUpdate_ForeignName_Rejected(string name)
    {
        using var d = new TempDir();
        var p = d.Sub("x.apk");
        File.WriteAllText(p, "x");
        var ssh = new FakeSsh();
        await Assert.ThrowsAsync<ArgumentException>(() => new RouterSsh(ssh).UploadUpdateAsync([new(name, p, "00")]));
        Assert.Empty(ssh.Runs);
    }

    [Fact]
    public async Task UploadUpdate_LocalHashMismatch_NothingSent()
    {
        using var d = new TempDir();
        var p = d.Sub("dpishell-0.2.1-r1.apk");
        File.WriteAllText(p, "подмена");
        var ssh = new FakeSsh();
        await Assert.ThrowsAsync<DpiShell.Core.SecurityException>(() =>
            new RouterSsh(ssh).UploadUpdateAsync([new("dpishell-0.2.1-r1.apk", p, new string('0', 64))]));
        Assert.Empty(ssh.Uploads);
    }
}
