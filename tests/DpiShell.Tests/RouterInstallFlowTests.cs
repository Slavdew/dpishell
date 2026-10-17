using System.Text;
using DpiShell.Core.Router;

namespace DpiShell.Tests;

/// <summary>Установка целиком над подделкой SSH: предпроверка → (туннель) → ядро → install.sh.</summary>
public class RouterInstallFlowTests
{
    const string InstallOk = "# Ключ витрины DpiShell\n# Проверка\n{\"schema\":1,\"ok\":true,\"step\":\"done\",\"message\":\"DpiShell установлен\",\"version\":\"0.2.0-r3\",\"api\":\"1.0\",\"core\":true}\n";
    static string Vendor => Path.Combine(Path.GetDirectoryName(Repo.Data)!, "vendor", "zapret-v72.13.zip");

    static RouterInstallFlow Flow(FakeSsh ssh, TempDir d) =>
        new(new FakeSshConnector(ssh), RouterReleases.Load(Path.Combine(Repo.Data, "router-releases.json")),
            new RouterCoreArchive(RouterCoreManifest.Embedded(), d.Sub("cache")),
            new HttpClient(new NoNetwork()))
        { LocalCoreCandidates = [Vendor] };

    sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => throw new InvalidOperationException("сети быть не должно");
    }

    [Fact]
    public async Task GoodRouter_CoreUploadedBeforeInstall_NoTunnel()
    {
        using var d = new TempDir();
        var ssh = new FakeSsh { OnScript = (cmd, script) => script.Contains("dpishell-preflight") ? RouterSshScenarioTests.PreflightOk : InstallOk };
        var steps = new List<string>();
        var o = await Flow(ssh, d).InstallAsync(new("192.168.1.1"), () => "pw", new LineProgress(steps.Add));

        Assert.True(o.Ok);
        Assert.Equal("0.2.0", o.Verdict.Release!.Version);
        Assert.Empty(ssh.Forwards);
        var cmds = ssh.Runs.Select(x => x.Command).ToList();
        Assert.Equal("sh -s -- --update", cmds[0]);
        Assert.Equal("sh -s", cmds[^1]);
        Assert.True(ssh.Files.ContainsKey(RouterSsh.CoreZip));
        Assert.True(cmds.IndexOf("mkdir -p /tmp/dpishell/core && chmod 700 /tmp/dpishell /tmp/dpishell/core") < cmds.Count - 1);
        Assert.Contains("Ключ витрины DpiShell", steps);
        Assert.True(ssh.Disposed);
    }

    [Fact]
    public async Task NoNetwork_TunnelThroughPc_RecheckKmod()
    {
        using var d = new TempDir();
        var offline = RouterSshScenarioTests.PreflightOk
            .Replace("\"net\":{\"feed\":true,\"fw_feed\":true,\"github\":true}", "\"net\":{\"feed\":false,\"fw_feed\":false,\"github\":false}")
            .Replace("\"installed\":false,\"available\":true,\"version\":\"6.12.48-r1\",\"matches_kernel\":true", "\"installed\":false,\"available\":false,\"version\":null,\"matches_kernel\":false");
        var ssh = new FakeSsh
        {
            FeedFilesText = "https://downloads.openwrt.org/releases/25.12.0/packages/aarch64_cortex-a53/base/packages.adb\n",
            OnScript = (cmd, script) => !script.Contains("dpishell-preflight") ? InstallOk : cmd.Contains("https_proxy") ? RouterSshScenarioTests.PreflightOk : offline,
        };
        var o = await Flow(ssh, d).InstallAsync(new("192.168.1.1"), () => "pw");

        Assert.True(o.Ok);
        var (remote, _) = Assert.Single(ssh.Forwards);
        var env = RouterSsh.ProxyEnv(remote);
        var cmds = ssh.Runs.Select(x => x.Command).ToList();
        Assert.Contains(env + "sh -s -- --update", cmds);
        Assert.Equal(env + "sh -s", cmds[^1]);
        Assert.Equal(1, ssh.ForwardsClosed);
    }

    [Fact]
    public async Task Check_OnlyPreflight_NothingUploaded()
    {
        using var d = new TempDir();
        var ssh = new FakeSsh { OnScript = (_, _) => RouterSshScenarioTests.PreflightOk };
        var v = await Flow(ssh, d).CheckAsync(new("192.168.1.1"), () => "pw");
        Assert.True(v.CanInstall);
        Assert.False(v.NeedsTunnel);
        Assert.Equal("sh -s -- --update", Assert.Single(ssh.Runs).Command);
        Assert.Empty(ssh.Uploads);
        Assert.True(ssh.Disposed);
    }

    [Fact]
    public async Task Blocker_NothingInstalled()
    {
        using var d = new TempDir();
        var json = RouterSshScenarioTests.PreflightOk.Replace("\"foreign\":[]", "\"foreign\":[{\"kind\":\"package\",\"name\":\"zapret\"}]");
        var ssh = new FakeSsh { OnScript = (_, _) => json };
        var o = await Flow(ssh, d).InstallAsync(new("192.168.1.1"), () => "pw");
        Assert.False(o.Ok);
        Assert.Null(o.Install);
        Assert.Equal("foreign_package", Assert.Single(o.Verdict.Blockers).Code);
        Assert.Single(ssh.Runs);
        Assert.Empty(ssh.Uploads);
    }

    [Fact]
    public async Task Uninstall_BySsh_ThenAfterCheck()
    {
        using var d = new TempDir();
        FakeSsh ssh = null!;
        ssh = new FakeSsh
        {
            OnScript = (cmd, _) =>
            {
                if (cmd.EndsWith("--after-uninstall"))
                    return """{"schema":1,"mode":"after-uninstall","left":[],"kept":{"settings":false,"https_dns_proxy":false}}""";
                ssh.Files[RouterSsh.UninstallResultPath] = Encoding.UTF8.GetBytes("""{"running":false,"ok":true,"keep_settings":false,"message":"DpiShell удалён полностью","at":1}""");
                return "";
            },
        };
        var o = await Flow(ssh, d).UninstallAsync(new("192.168.1.1"), () => "pw", keepSettings: false);
        Assert.True(o.Result.Ok);
        Assert.True(o.Left!.Clean);
        Assert.Equal("sh -s -- --full", ssh.Runs[0].Command);
    }
}
