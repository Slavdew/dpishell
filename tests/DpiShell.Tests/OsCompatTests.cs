using DpiShell.Core;
using DpiShell.Core.Localization;

namespace DpiShell.Tests;

/// <summary>
/// Развилки по версии Windows (docs: win10-compat.md): 19045 — Windows 10 22H2, 22631 — Windows 11 23H2. Система не спрашивается — сборка подменяется
/// через <see cref="OsInfo.Assume"/>; разбор вывода netsh / sc — на примерах вывода Windows 10 (ru, en).
/// </summary>
[Collection(LangCollection.Name)]
public sealed class OsCompatTests : IDisposable
{
    readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    const int Win10 = 19045, Win11 = 22631;

    [Theory]
    [InlineData(17134, false)]   // 1803
    [InlineData(17763, false)]   // 1809 — нижняя граница
    [InlineData(19045, false)]   // 22H2
    [InlineData(21999, false)]
    [InlineData(22000, true)]    // 21H2 Windows 11
    [InlineData(22631, true)]    // 23H2
    [InlineData(26100, true)]    // 24H2
    public void Build_Fork_Windows11AndDoh(int build, bool win11)
    {
        using (OsInfo.Assume(build))
        {
            Assert.Equal(win11, OsInfo.IsWindows11);
            Assert.Equal(win11, OsInfo.DohSupported);
            Assert.Equal(win11, new WindowsDnsSystem().DohSupported);
        }
    }

    [Fact]
    public void Assume_RestoresPrevious_AndNests()
    {
        var real = OsInfo.Build;
        using (OsInfo.Assume(Win10))
        {
            using (OsInfo.Assume(Win11)) Assert.Equal(Win11, OsInfo.Build);
            Assert.Equal(Win10, OsInfo.Build);
        }
        Assert.Equal(real, OsInfo.Build);
    }

    /// <summary>Тёмный заголовок: Windows 10 1809–1909 — атрибут 19 (недокументированный), с 18985 — 20; раньше 1809 — нет.</summary>
    [Theory]
    [InlineData(17134, 0)]
    [InlineData(17763, 19)]
    [InlineData(18363, 19)]   // 1909
    [InlineData(18985, 20)]
    [InlineData(19041, 20)]   // 2004
    [InlineData(19045, 20)]   // 22H2
    [InlineData(22631, 20)]
    public void DarkTitleAttribute_ByBuild(int build, int attr) => Assert.Equal(attr, OsInfo.DarkTitleAttribute(build));

    /// <summary>Клиентский TLS 1.3 в Schannel — с 20348: на 19045 только TLS 1.2, на 22631 — оба.</summary>
    [Fact]
    public void Tls13_ByBuild()
    {
        Assert.False(TlsSupport.BuildHasTls13(new Version(10, 0, Win10)));
        Assert.True(TlsSupport.BuildHasTls13(new Version(10, 0, Win11)));
        using (TlsSupport.Assume(tls13: false))
            Assert.Equal([System.Security.Authentication.SslProtocols.Tls12], TlsSupport.Versions);
        using (TlsSupport.Assume(tls13: true))
            Assert.Equal(2, TlsSupport.Versions.Length);
    }

    /// <summary>Подписи вариантов DNS: на Windows 10 «без шифрования», на 11 — «защищённый» (ru и en).</summary>
    [Theory]
    [InlineData(Win10, "ru", "без шифрования")]
    [InlineData(Win11, "ru", "защищённый")]
    [InlineData(Win10, "en", "unencrypted")]
    [InlineData(Win11, "en", "secure")]
    public void DnsPresetDetail_ByBuild(int build, string lang, string expected)
    {
        using var _ = L.Scope(lang);
        using var __ = OsInfo.Assume(build);
        var vm = TestWindow.Model(new Shell(new AppPaths(_tmp.Sub("root"), Repo.Data)));
        var google = vm.PcDns.Options.Single(o => o.Id == "google");
        Assert.Contains(expected, google.Detail);
        Assert.Equal(build == Win11, vm.PcDns.ShowFallback);
    }

    // ---- netsh interface tcp show global ----

    /// <summary>
    /// Примеры вывода. en: Microsoft Learn «netsh interface tcp» + вывод Windows 10 из обсуждений (superuser.com/questions/1203106, serverfault «TCP Global Parameters»);
    /// ru: вывод русской Windows 10 («Метки времени RFC 1323»); de — по образцу, подпись переведена, RFC 1323 остаётся.
    /// </summary>
    [Theory]
    [InlineData("Querying active state...\r\n\r\nTCP Global Parameters\r\n----------------------------------------------\r\nReceive-Side Scaling State          : enabled\r\nReceive Window Auto-Tuning Level    : normal\r\nAdd-On Congestion Control Provider  : default\r\nECN Capability                      : disabled\r\nRFC 1323 Timestamps                 : disabled\r\nInitial RTO                         : 1000\r\nReceive Segment Coalescing State    : enabled\r\nNon Sack Rtt Resiliency             : disabled\r\nMax SYN Retransmissions             : 2\r\nFast Open                           : enabled\r\n", false)]
    [InlineData("Querying active state...\r\n\r\nTCP Global Parameters\r\n----------------------------------------------\r\nReceive-Side Scaling State          : enabled\r\nRFC 1323 Timestamps                 : enabled\r\n", true)]
    [InlineData("Запрос активного состояния...\r\n\r\nГлобальные параметры TCP\r\n----------------------------------------------\r\nСостояние масштабирования на стороне приема : enabled\r\nУровень автонастройки окна приема   : normal\r\nМетки времени RFC 1323              : disabled\r\nНачальное значение RTO              : 1000\r\n", false)]
    [InlineData("Запрос активного состояния...\r\nМетки времени RFC 1323              : enabled\r\n", true)]
    [InlineData("Aktiven Zustand abfragen...\r\nRFC 1323-Zeitstempel                 : disabled\r\n", false)]
    public void NetshTcpGlobal_Windows10_RuEn(string output, bool enabled) => Assert.Equal(enabled, TcpTimestamps.Parse(output));

    // ---- sc query ----

    /// <summary>
    /// Вывод <c>sc query</c> (Windows 10/11 одинаков): Microsoft Learn «sc query» и обсуждения; ru — «СОСТОЯНИЕ» в cp866 уже перекодирован в строку.
    /// </summary>
    [Theory]
    [InlineData("SERVICE_NAME: DpiShell\r\n        TYPE               : 10  WIN32_OWN_PROCESS\r\n        STATE              : 4  RUNNING\r\n                                (STOPPABLE, NOT_PAUSABLE, ACCEPTS_SHUTDOWN)\r\n        WIN32_EXIT_CODE    : 0  (0x0)\r\n", SvcState.Running)]
    [InlineData("SERVICE_NAME: DpiShell\r\n        TYPE               : 10  WIN32_OWN_PROCESS\r\n        STATE              : 1  STOPPED\r\n        WIN32_EXIT_CODE    : 0  (0x0)\r\n", SvcState.Stopped)]
    [InlineData("SERVICE_NAME: DpiShell\r\n        STATE              : 2  START_PENDING\r\n", SvcState.StartPending)]
    [InlineData("SERVICE_NAME: DpiShell\r\n        STATE              : 3  STOP_PENDING\r\n", SvcState.StopPending)]
    [InlineData("SERVICE_NAME: DpiShell\r\n        ТИП                : 10  WIN32_OWN_PROCESS\r\n        СОСТОЯНИЕ          : 4  RUNNING\r\n                                (STOPPABLE, NOT_PAUSABLE, ACCEPTS_SHUTDOWN)\r\n        КОД_ВЫХОДА_WIN32   : 0  (0x0)\r\n", SvcState.Running)]
    [InlineData("SERVICE_NAME: DpiShell\r\n        СОСТОЯНИЕ          : 1  STOPPED\r\n", SvcState.Stopped)]
    [InlineData("SERVICE_NAME: DpiShell\r\n        ZUSTAND            : 4  RUNNING\r\n", SvcState.Running)]
    [InlineData("[SC] EnumQueryServicesStatus:OpenService: ошибка: 1060:\r\nУказанная служба не установлена.\r\n", SvcState.Unknown)]
    [InlineData("", SvcState.Unknown)]
    public void ScQuery_Windows10_RuEn(string output, SvcState expected) => Assert.Equal(expected, ServiceManager.ParseState(output));

    /// <summary>Манифест окна объявляет Windows 10/11 — иначе Windows включает режим совместимости 8.1.</summary>
    [Fact]
    public void Manifest_DeclaresWindows10()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "DpiShell.App", "app.manifest"));
        if (!File.Exists(path)) return;
        Assert.Contains("8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a", File.ReadAllText(path));
    }
}
