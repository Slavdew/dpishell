using System.Text;
using DpiShell.Core.Router;

namespace DpiShell.Tests;

/// <summary>Сценарии по SSH (router-api.md §5.5) над подделкой ISshSession; ответы — в формате, который печатают сами скрипты.</summary>
public class RouterSshScenarioTests
{
    static string Root => Path.GetDirectoryName(Repo.Data)!;

    // Строка, которую печатает dpishell-preflight.sh на чистом OpenWrt 25.12 (arm64), без DpiShell.
    public const string PreflightOk =
        """{"schema":1,"mode":"check","openwrt":{"release":"25.12.0","revision":"r31257-2a6b4e1f3c","arch":"aarch64_cortex-a53","target":"mediatek/filogic"},"pkg":"apk","fw4":true,"nft":true,"kernel":"6.12.48","kmod_nft_queue":{"installed":false,"available":true,"version":"6.12.48-r1","matches_kernel":true},"net":{"feed":true,"fw_feed":true,"github":true},"space":{"overlay_free_kb":81234,"tmp_free_kb":221000},"foreign":[],"dpishell":null,"luci":true}""";

    [Theory]
    [InlineData(RouterSsh.PreflightScript, "router/tools/dpishell-preflight.sh")]
    [InlineData(RouterSsh.InstallScript, "router/tools/dpishell-install.sh")]
    [InlineData(RouterSsh.UninstallScript, "router/dpishell/files/usr/libexec/dpishell/uninstall.sh")]
    public void Scripts_AreEmbeddedFromRepo(string name, string path)
    {
        var embedded = RouterSsh.Script(name);
        Assert.Equal(File.ReadAllText(Path.Combine(Root, path)).Replace("\r\n", "\n"), embedded);
        Assert.StartsWith("#!/bin/sh\n", embedded);
        Assert.DoesNotContain('\r', embedded);
    }

    [Fact]
    public async Task Preflight_SendsScriptOnStdin_ParsesSchema1()
    {
        var ssh = new FakeSsh { OnScript = (_, _) => PreflightOk + "\n" };
        var p = await new RouterSsh(ssh).PreflightAsync();
        Assert.Equal("sh -s", ssh.Runs[0].Command);
        Assert.Equal(RouterSsh.Script(RouterSsh.PreflightScript), ssh.Runs[0].Stdin);
        Assert.Equal("25.12.0", p.Openwrt!.Release);
        Assert.Equal("aarch64_cortex-a53", p.Openwrt.Arch);
        Assert.Equal("apk", p.Pkg);
        Assert.True(p.Fw4 && p.Nft && p.Luci);
        Assert.True(p.KmodNftQueue!.MatchesKernel);
        Assert.Equal(81234, p.Space!.OverlayFreeKb);
        Assert.Empty(p.Foreign!);
        Assert.Null(p.Dpishell);
    }

    [Fact]
    public async Task Preflight_UpdateThroughTunnel_SetsProxyEnv()
    {
        var ssh = new FakeSsh { OnScript = (_, _) => PreflightOk };
        await new RouterSsh(ssh).PreflightAsync(updateIndexes: true, proxyPort: 41234);
        Assert.Equal("https_proxy=http://127.0.0.1:41234 http_proxy=http://127.0.0.1:41234 sh -s -- --update", ssh.Runs[0].Command);
    }

    [Fact]
    public async Task Preflight_InstalledDpiShell_AndForeign()
    {
        var json = PreflightOk.Replace("\"foreign\":[]", "\"foreign\":[{\"kind\":\"package\",\"name\":\"luci-app-zapret\"},{\"kind\":\"process\",\"name\":\"/usr/bin/byedpi\"}]")
                              .Replace("\"dpishell\":null", "\"dpishell\":{\"version\":\"0.2.0-r3\",\"api\":\"1.0\"}");
        var p = await new RouterSsh(new FakeSsh { OnScript = (_, _) => json }).PreflightAsync();
        Assert.Equal(2, p.Foreign!.Count);
        Assert.Equal("package", p.Foreign[0].Kind);
        Assert.Equal("0.2.0-r3", p.Dpishell!.Version);
        Assert.Equal(new ApiVersion(1, 0), p.Dpishell.Api);
    }

    [Fact]
    public async Task Preflight_NotJson_IsProtocolError()
    {
        var e = await Assert.ThrowsAsync<RouterSshException>(() => new RouterSsh(new FakeSsh { OnScript = (_, _) => "sh: not found\n" }).PreflightAsync());
        Assert.Equal(RouterSshFailure.Protocol, e.Failure);
    }

    [Fact]
    public async Task AfterUninstall_ParsesLeftAndKept()
    {
        var ssh = new FakeSsh
        {
            OnScript = (_, _) => """{"schema":1,"mode":"after-uninstall","left":[{"what":"file","detail":"/opt/zapret"},{"what":"dns","detail":"dnsmasq → 127.0.0.1#505x"}],"kept":{"settings":true,"https_dns_proxy":false}}""",
        };
        var r = await new RouterSsh(ssh).AfterUninstallAsync();
        Assert.Equal("sh -s -- --after-uninstall", ssh.Runs[0].Command);
        Assert.False(r.Clean);
        Assert.Equal("/opt/zapret", r.Left[0].Detail);
        Assert.True(r.Kept!.Settings);

        var clean = await new RouterSsh(new FakeSsh { OnScript = (_, _) => """{"schema":1,"mode":"after-uninstall","left":[],"kept":{"settings":false,"https_dns_proxy":false}}""" }).AfterUninstallAsync();
        Assert.True(clean.Clean);
    }

    [Fact]
    public async Task Install_ProgressLines_ThenResult()
    {
        var ssh = new FakeSsh
        {
            OnScript = (_, _) => """
                # Ключ витрины DpiShell
                # Витрина пакетов
                # Пакеты DpiShell и зависимости
                # Ядро обхода (zapret) — загрузка и проверка хеша
                # Проверка
                {"schema":1,"ok":true,"step":"done","message":"DpiShell установлен. Дальше — «Подобрать»","version":"0.2.0-r3","api":"1.0","core":true}
                """,
        };
        var steps = new List<string>();
        var r = await new RouterSsh(ssh).InstallAsync(proxyPort: 3128, progress: new LineProgress(steps.Add));
        Assert.StartsWith("https_proxy=http://127.0.0.1:3128 ", ssh.Runs[0].Command);
        Assert.EndsWith("sh -s", ssh.Runs[0].Command);
        Assert.Equal(["Ключ витрины DpiShell", "Витрина пакетов", "Пакеты DpiShell и зависимости", "Ядро обхода (zapret) — загрузка и проверка хеша", "Проверка"], steps);
        Assert.True(r.Ok);
        Assert.Equal("0.2.0-r3", r.Version);
        Assert.Equal(new ApiVersion(1, 0), r.Api);
        Assert.True(r.Core);
    }

    [Fact]
    public async Task Install_Failure_KeepsStepAndMessage()
    {
        var ssh = new FakeSsh { OnScript = (_, _) => "# Ключ витрины DpiShell\n{\"schema\":1,\"ok\":false,\"step\":\"check\",\"message\":\"Нужен OpenWrt с apk (25.12+); сборка для opkg (24.10) — позже\",\"version\":null,\"api\":null,\"core\":null}\n" };
        var r = await new RouterSsh(ssh).InstallAsync();
        Assert.False(r.Ok);
        Assert.Equal("check", r.Step);
        Assert.Contains("opkg", r.Message);
        Assert.Null(r.Api);
        Assert.Null(r.Core);
    }

    [Theory]
    [InlineData(true, "--keep", "DpiShell удалён, настройки сохранены")]
    [InlineData(false, "--full", "DpiShell удалён полностью")]
    public async Task Uninstall_BySsh_ReadsResultFile(bool keep, string arg, string message)
    {
        FakeSsh ssh = null!;
        ssh = new FakeSsh
        {
            OnScript = (cmd, _) =>
            {
                ssh.Files[RouterSsh.UninstallResultPath] = Encoding.UTF8.GetBytes(
                    $$"""{"running":false,"ok":true,"keep_settings":{{(keep ? "true" : "false")}},"message":"{{message}}","at":1760716800}""" + "\n");
                return "";
            },
        };
        var r = await new RouterSsh(ssh).UninstallAsync(keep);
        Assert.Equal("sh -s -- " + arg, ssh.Runs[0].Command);
        Assert.Equal(RouterSsh.Script(RouterSsh.UninstallScript), ssh.Runs[0].Stdin);
        Assert.False(r.Running);
        Assert.True(r.Ok);
        Assert.Equal(keep, r.KeepSettings);
        Assert.Equal(message, r.Message);
    }

    [Fact]
    public async Task ReadUninstallResult_NoFile_Null()
    {
        Assert.Null(await new RouterSsh(new FakeSsh()).ReadUninstallResultAsync());
    }

    [Fact]
    public void FeedHosts_FromApkAndOpkgFiles()
    {
        var hosts = RouterSsh.ParseFeedHosts("""
            https://downloads.openwrt.org/releases/25.12.0/targets/mediatek/filogic/packages/packages.adb
            https://downloads.openwrt.org/releases/25.12.0/packages/aarch64_cortex-a53/base/packages.adb
            # https://commented.example.org/x
            https://slavdew.github.io/dpishell/feed/packages.adb
            src/gz openwrt_core https://mirror.yandex.ru/openwrt/releases/24.10.2/targets/x/packages
            https://Mirror.Example.NET:8443/feed/packages.adb
            """);
        Assert.Equal(["downloads.openwrt.org", "slavdew.github.io", "mirror.yandex.ru", "mirror.example.net"], hosts);
    }

    [Fact]
    public async Task FeedHostsAsync_ReadsRouterFiles()
    {
        var ssh = new FakeSsh { FeedFilesText = "https://downloads.openwrt.org/releases/25.12.0/packages/x/base/packages.adb\n" };
        Assert.Equal(["downloads.openwrt.org"], await new RouterSsh(ssh).FeedHostsAsync());
        Assert.StartsWith("cat /etc/apk/repositories.d/*.list", ssh.Runs[0].Command);
    }
}

/// <summary>IProgress без SynchronizationContext — сразу в том же потоке.</summary>
public sealed class LineProgress(Action<string> a) : IProgress<string>
{
    public void Report(string value) => a(value);
}
