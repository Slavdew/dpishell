using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Router;

namespace DpiShell.Tests;

/// <summary>
/// status.packages и status.core.installed_version (router-api §5.1, роутер 0.2.0-r8): необязательные поля модели
/// и строки «DpiShell на роутере» / «Ядро на роутере» на вкладке «Информация и обновления». Старый роутер без полей — как раньше.
/// </summary>
public sealed class RouterPackagesTests : IDisposable
{
    readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    RouterViewModel Make(FakeRouter r)
    {
        var shell = new Shell(new AppPaths(_tmp.Sub("root"), Repo.Data));
        var settings = WindowSettings.Load(Path.Combine(_tmp.Sub("window"), "window.json"));
        return new RouterViewModel(shell, settings)
        {
            AddressAuto = false, CustomAddress = r.Address, PollInterval = TimeSpan.FromMilliseconds(10),
            PcTimestamps = () => true,
            Gateway = () => throw new InvalidOperationException("в тестах — только фейковый роутер"),
        };
    }

    [Fact]
    public async Task R8Status_ParsesPackagesAndInstalledCore()
    {
        using var r = new FakeRouter().As020().AsR8(dpishell: "0.2.0-r8", luci: "0.2.0-r5", core: "v72.13");
        using var client = new RouterClient(r.Address);
        await client.LoginAsync(() => "secret");
        var s = await client.StatusAsync();
        Assert.Equal("0.2.0-r8", s.Packages!.Dpishell);
        Assert.Equal("0.2.0-r5", s.Packages.LuciAppDpishell);       // «luci-app-dpishell» с дефисами в договоре
        Assert.Equal("v72.13", s.Core!.InstalledVersion);
        Assert.Equal("v72.13", s.Core.ShownVersion);
    }

    [Fact]
    public async Task OldRouter_NoNewFields_ModelAndTextsAsBefore()
    {
        using var r = new FakeRouter().As020();
        using var client = new RouterClient(r.Address);
        await client.LoginAsync(() => "secret");
        var s = await client.StatusAsync();
        Assert.Null(s.Packages);
        Assert.Null(s.Core!.InstalledVersion);
        Assert.Equal("v72.13", s.Core.ShownVersion);                // из пакета, как раньше

        var vm = Make(r);
        Assert.True(await vm.ConnectAsync("secret"));
        Assert.Equal("0.2.0-r3", vm.PackageVersionText);
        Assert.Equal("v72.13", vm.RouterCoreText);
        Assert.True(vm.HasRouterCore);
    }

    [Fact]
    public async Task PackageText_ShowsLuciOnlyWhenDifferent()
    {
        using var r = new FakeRouter().As020().AsR8(dpishell: "0.2.0-r8", luci: "0.2.0-r5");
        var vm = Make(r);
        Assert.True(await vm.ConnectAsync("secret"));
        Assert.Equal("0.2.0-r8 · страница LuCI 0.2.0-r5", vm.PackageVersionText);

        r.Packages = ("0.2.0-r8", "0.2.0-r8");
        await vm.RefreshAsync();
        Assert.Equal("0.2.0-r8", vm.PackageVersionText);

        r.Packages = ("0.2.0-r8", null);                            // страницы нет — не выдумываем
        await vm.RefreshAsync();
        Assert.Equal("0.2.0-r8", vm.PackageVersionText);
    }

    [Fact]
    public async Task CoreRow_InstalledMissingAndDifferentFromPackage()
    {
        using var r = new FakeRouter().As020().AsR8();
        var vm = Make(r);
        Assert.True(await vm.ConnectAsync("secret"));
        Assert.Equal("v72.13", vm.RouterCoreText);

        r.CoreInstalledVersion = "none";                            // installed: false, installed_version: null
        await vm.RefreshAsync();
        Assert.Equal("не установлено", vm.RouterCoreText);
        Assert.True(vm.HasRouterCore);

        r.CoreInstalledVersion = "v72.12";                          // в /opt/zapret старее закреплённого в пакете
        await vm.RefreshAsync();
        Assert.Equal("v72.12 · в пакете v72.13", vm.RouterCoreText);
    }

    [Fact]
    public void NotConnected_NoCoreRow()
    {
        using var r = new FakeRouter().As020();
        var vm = Make(r);
        Assert.False(vm.HasRouterCore);
        Assert.Equal("", vm.RouterCoreText);
    }
}
