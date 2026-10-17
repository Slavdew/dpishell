using DpiShell.Core.Router;

namespace DpiShell.Tests;

/// <summary>«Годится ли роутер» и выбор выпуска — чистые функции над ответом preflight и data/router-releases.json.</summary>
public class RouterSuitabilityTests
{
    static RouterReleases Releases => RouterReleases.Load(Path.Combine(Repo.Data, "router-releases.json"));
    static RouterPreflight Ok => RouterScriptJson.ParsePreflight(RouterSshScenarioTests.PreflightOk);
    static readonly string[] Arches = RouterCoreManifest.Embedded().Nfqws.Keys.ToArray();

    static RouterVerdict Eval(RouterPreflight p) => RouterSuitability.Evaluate(p, Releases, Arches);

    [Fact]
    public void GoodRouter_CanInstall_WithRelease()
    {
        var v = Eval(Ok);
        Assert.True(v.CanInstall);
        Assert.Empty(v.Blockers);
        Assert.False(v.NeedsTunnel);
        Assert.False(v.KmodPending);
        Assert.False(v.AlreadyInstalled);
        Assert.Equal("0.2.0", v.Release!.Version);
        Assert.Equal("router-v0.2.0", v.Release.Tag);
    }

    [Fact]
    public void Opkg_NotSupportedYet_Wait()
    {
        var v = Eval(Ok with { Pkg = "opkg", Openwrt = Ok.Openwrt! with { Release = "24.10.2" } });
        Assert.False(v.CanInstall);
        var b = Assert.Single(v.Blockers);
        Assert.Equal("opkg", b.Code);
        Assert.Contains("пока не поддерживается", b.Text);
        Assert.Contains("ждите", b.Text);
        Assert.Null(v.Release);
    }

    [Fact]
    public void NotOpenWrt()
    {
        var v = Eval(Ok with { Openwrt = null, Pkg = null });
        Assert.False(v.CanInstall);
        Assert.Equal("not_openwrt", Assert.Single(v.Blockers).Code);
    }

    [Fact]
    public void NoFw4()
    {
        var v = Eval(Ok with { Fw4 = false });
        Assert.Equal("no_fw4", Assert.Single(v.Blockers).Code);
        Assert.Contains("fw4", v.Blockers[0].Text);
    }

    [Fact]
    public void Kmod_MismatchKernel_Stops()
    {
        var v = Eval(Ok with { KmodNftQueue = new(false, true, "6.12.50-r1", false) });
        var b = Assert.Single(v.Blockers);
        Assert.Equal("kmod_mismatch", b.Code);
        Assert.Contains("6.12.50-r1", b.Text);
        Assert.Contains("6.12.48", b.Text);
    }

    [Fact]
    public void Kmod_Missing_WithFeed_Stops()
    {
        var v = Eval(Ok with { KmodNftQueue = new(false, false, null, false) });
        Assert.Equal("kmod_missing", Assert.Single(v.Blockers).Code);
    }

    [Fact]
    public void Kmod_Unknown_WithoutFeed_PendingTunnel()
    {
        var v = Eval(Ok with { KmodNftQueue = new(false, false, null, false), Net = new(false, false, false) });
        Assert.True(v.CanInstall);
        Assert.True(v.KmodPending);
        Assert.True(v.NeedsTunnel);
        Assert.Contains(v.Warnings, w => w.Code == "tunnel");
    }

    [Fact]
    public void Kmod_BuiltIntoKernel_Ok()
    {
        // Модуль в ядре: пакета нет, но matches_kernel — true (preflight проверил /lib/modules).
        Assert.True(Eval(Ok with { KmodNftQueue = new(false, false, null, true) }).CanInstall);
    }

    [Fact]
    public void NoGithub_NeedsTunnel_NotBlocker()
    {
        var v = Eval(Ok with { Net = new(true, true, false) });
        Assert.True(v.CanInstall);
        Assert.True(v.NeedsTunnel);
    }

    [Fact]
    public void ForeignPackage_Stops_WithRemoveText()
    {
        var v = Eval(Ok with { Foreign = [new("package", "luci-app-zapret"), new("service", "podkop")] });
        var b = Assert.Single(v.Blockers);
        Assert.Equal("foreign_package", b.Code);
        Assert.StartsWith("Удалите пакет «luci-app-zapret»", b.Text);
        Assert.Contains(v.Warnings, w => w.Code == "foreign_service" && w.Text.Contains("podkop"));
    }

    [Fact]
    public void LowSpace_Stops()
    {
        var v = Eval(Ok with { Space = new(1200, 9000) });
        Assert.Equal(["space_overlay", "space_tmp"], v.Blockers.Select(b => b.Code));
        Assert.Contains("1,2 МБ", v.Blockers[0].Text.Replace('.', ','));
    }

    [Fact]
    public void UnknownSpace_Warning()
    {
        var v = Eval(Ok with { Space = new(null, null) });
        Assert.True(v.CanInstall);
        Assert.Contains(v.Warnings, w => w.Code == "space_unknown");
    }

    [Fact]
    public void UnsupportedArch_Stops()
    {
        var v = Eval(Ok with { Openwrt = Ok.Openwrt! with { Arch = "riscv64_generic" } });
        Assert.Equal("arch", Assert.Single(v.Blockers).Code);
    }

    [Fact]
    public void AlreadyInstalled_Warning()
    {
        var v = Eval(Ok with { Dpishell = new("0.2.0-r1", new ApiVersion(1, 0)) });
        Assert.True(v.CanInstall);
        Assert.True(v.AlreadyInstalled);
        Assert.Equal("0.2.0-r1", v.InstalledVersion);
    }

    [Fact]
    public void OldOpenWrtWithApk_NoRelease()
    {
        var v = Eval(Ok with { Openwrt = Ok.Openwrt! with { Release = "25.10.0" } });
        var b = Assert.Single(v.Blockers);
        Assert.Equal("no_release", b.Code);
        Assert.Contains("25.12", b.Text);
    }

    [Theory]
    [InlineData("aarch64_cortex-a53", "arm64")]
    [InlineData("arm_cortex-a7_neon-vfpv4", "arm")]
    [InlineData("mipsel_24kc", "mipsel")]
    [InlineData("mips64el_octeonplus", "mips64")]
    [InlineData("mips_24kc", "mips")]
    [InlineData("x86_64", "x86_64")]
    [InlineData("i386_pentium4", "x86")]
    [InlineData("riscv64_generic", null)]
    public void CoreArch_AsCoreUc(string distribArch, string? expected) => Assert.Equal(expected, RouterSuitability.CoreArch(distribArch));

    [Theory]
    [InlineData("apk", "25.12.0", "0.2.0")]
    [InlineData("apk", "25.12.0-rc1", "0.2.0")]
    [InlineData("apk", "26.06.1", "0.2.0")]
    [InlineData("apk", "SNAPSHOT", "0.2.0")]
    [InlineData("apk", "25.12-SNAPSHOT", "0.2.0")]
    [InlineData("apk", "24.10.2", null)]
    [InlineData("opkg", "24.10.2", null)]
    // Сборка на основе OpenWrt со своей версией вместо версии OpenWrt — выбор по пакетному менеджеру (живой роутер 2026-10-17).
    [InlineData("apk", "1.4.8", "0.2.0")]
    [InlineData("apk", "непонятно", "0.2.0")]
    [InlineData("opkg", "1.4.8", null)]
    [InlineData(null, "25.12.0", null)]
    public void ChooseRelease(string? pkg, string release, string? expected) => Assert.Equal(expected, Releases.Choose(pkg, release)?.Version);

    [Fact]
    public void ChooseRelease_NewestMatching_SnapshotFlag()
    {
        var r = Releases;
        var newer = r.Releases[0] with { Version = "0.3.0", Tag = "router-v0.3.0", Openwrt = new("26.06", false) };
        var set = r with { Releases = [.. r.Releases, newer] };
        Assert.Equal("0.3.0", set.Choose("apk", "26.06.0")!.Version);
        Assert.Equal("0.2.0", set.Choose("apk", "25.12.3")!.Version);
        Assert.Equal("0.2.0", set.Choose("apk", "SNAPSHOT")!.Version);   // у 0.3.0 snapshot: false
    }

    [Fact]
    public void Releases_FeedMatchesInstallScript()
    {
        // Ключ витрины в install.sh и в router-releases.json — один и тот же.
        var script = RouterSsh.Script(RouterSsh.InstallScript);
        Assert.Contains("KEY_SHA=" + Releases.Feed.KeySha256, script);
        Assert.Contains("FEED=" + Releases.Feed.Url, script);
    }
}
