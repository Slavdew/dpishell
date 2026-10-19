using System.Runtime.CompilerServices;
using DpiShell.Core;

namespace DpiShell.Tests;

/// <summary>Настоящий системный прокси под тестами недоступен: реестр Internet Settings, WinHTTP и InternetSetOption — исключение.</summary>
static class NoRealProxy
{
    [ModuleInitializer]
    internal static void Init() => SystemProxyGuard.Forbidden = true;
}

public sealed class NoRealProxyTests
{
    [Fact]
    public void Real_registry_winhttp_and_notifier_are_forbidden_under_tests()
    {
        Assert.True(SystemProxyGuard.Forbidden);
        var reg = new WindowsProxyRegistry();
        Assert.Throws<InvalidOperationException>(() => reg.Get(ProxyKey.UserSettings, null, ProxyNames.Enable));
        Assert.Throws<InvalidOperationException>(() => reg.Get(ProxyKey.MachineSettings, null, ProxyNames.Server));
        Assert.Throws<InvalidOperationException>(() => reg.SetDword(ProxyKey.UserSettings, null, ProxyNames.Enable, 1));
        Assert.Throws<InvalidOperationException>(() => reg.SetString(ProxyKey.MachineSettings, null, ProxyNames.Server, "127.0.0.1:1"));
        Assert.Throws<InvalidOperationException>(() => reg.SetDword(ProxyKey.MachinePolicy, null, ProxyNames.PerUser, 0));
        Assert.Throws<InvalidOperationException>(() => reg.Delete(ProxyKey.UserSettings, null, ProxyNames.Server));
        Assert.Throws<InvalidOperationException>(() => reg.UserHiveLoaded("S-1-5-21-1-2-3-1001"));
        Assert.Throws<InvalidOperationException>(() => new WindowsWinHttpProxy().Get());
        Assert.Throws<InvalidOperationException>(() => new WindowsWinHttpProxy().Set(WinHttpInfo.Direct));
        Assert.Throws<InvalidOperationException>(() => new WininetNotifier().Changed());
    }
}
