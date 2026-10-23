using System.ComponentModel;
using System.Net;
using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.Tests;

/// <summary>
/// Обрыв действий антивирусом (ТЗ av-exclusion, дополнение 2): понятная причина вместо общей ошибки, подтверждение по журналу Защитника
/// (подставной), без повторных скачиваний по кругу. «Антивирус» — подставная файловая система: файл удаляется сразу после записи.
/// </summary>
public class AvBlockTests
{
    static string VendorZip => Path.Combine(Path.GetDirectoryName(Repo.Data)!, "vendor", "zapret-v72.13.zip");

    sealed class ZipServer(byte[] body) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body), RequestMessage = r });
        }
    }

    static (Shell Shell, ZipServer Net) NewShell(TempDir t)
    {
        var shell = new Shell(new AppPaths(t.Sub("root"), Repo.Data));
        var net = new ZipServer(File.ReadAllBytes(VendorZip));
        shell.DownloadClient = () => new HttpClient(net, disposeHandler: false);
        return (shell, net);
    }

    [Fact]
    public void Virus_error_codes_are_recognised()
    {
        Assert.True(AvBlock.IsVirusError(new Win32Exception(225)));
        Assert.True(AvBlock.IsVirusError(new Win32Exception(226)));
        Assert.True(AvBlock.IsVirusError(new IOException("x", unchecked((int)0x800700E1))));
        Assert.True(AvBlock.IsVirusError(new IOException("x", unchecked((int)0x800700E2))));
        Assert.False(AvBlock.IsVirusError(new IOException("занят", unchecked((int)0x80070020))));
        Assert.True(AvBlock.IsVanished(new FileNotFoundException()));
        Assert.True(AvBlock.IsVanished(new Win32Exception(2)));
        Assert.False(AvBlock.IsVanished(new InvalidDataException()));
    }

    [Theory]
    [InlineData(@"file:_C:\ProgramData\DpiShell\core.download\core.zip", true)]
    [InlineData(@"containerfile:_C:\ProgramData\DpiShell\core.download\core.zip", true)]
    [InlineData(@"file:_C:\ProgramData\DpiShell\core.download\core.zip->zapret-v72.13/binaries/windows-x86_64/WinDivert64.sys", true)]
    [InlineData(@"file:_C:\ProgramData\DpiShellOther\core.zip", false)]
    [InlineData(@"file:_C:\Users\x\Downloads\other.zip", false)]
    public void Detection_resource_matches_path_or_folder(string resource, bool touches)
    {
        Assert.Equal(touches, AvBlock.Touches(resource, @"C:\ProgramData\DpiShell\core.download\core.zip"));
        // По папке — всё, что внутри.
        Assert.Equal(touches, AvBlock.Touches(resource, @"C:\ProgramData\DpiShell"));
    }

    /// <summary>Архив удалён сразу после скачивания: одна попытка, «удалил Защитник: угроза — файл», если журнал подтверждает; иначе «возможно».</summary>
    [Fact]
    public async Task Downloaded_zip_deleted_names_defender_threat_once()
    {
        using var t = new TempDir();
        var (shell, net) = NewShell(t);
        var d = FakeDefender.Active();
        shell.Defender = d;
        shell.Installer.Written = f =>
        {
            if (!f.EndsWith("core.zip")) return;
            File.Delete(f);
            d.ThreatsValue.Add(new("Trojan:Win32/Tecabans.STV!cl", ["file:_" + f], DateTime.UtcNow));
        };
        var e = await Assert.ThrowsAsync<AvBlockedException>(() => shell.RepairCoreAsync());
        Assert.Equal("Trojan:Win32/Tecabans.STV!cl", e.Threat);
        Assert.Contains("Защитник: Trojan:Win32/Tecabans.STV!cl", e.Message);
        Assert.Contains("core.zip", e.Message);
        Assert.Equal(1, net.Requests);   // не качать снова по кругу
        Assert.Equal(CoreState.Missing, shell.Installer.Check().State);

        // Журнал не читается (нет прав, другой антивирус) — без утверждений.
        d.ThreatsUnreadable = true;
        var e2 = await Assert.ThrowsAsync<AvBlockedException>(() => shell.RepairCoreAsync());
        Assert.Null(e2.Threat);
        Assert.Contains("возможно, антивирусом", e2.Message);
        Assert.DoesNotContain("Защитник", e2.Message);
    }

    /// <summary>Файл ядра удалён сразу после распаковки (WinDivert64.sys): прямо, старое ядро не тронуто.</summary>
    [Fact]
    public async Task Extracted_driver_deleted_keeps_old_core()
    {
        using var t = new TempDir();
        var (shell, _) = NewShell(t);
        await shell.RepairCoreAsync();
        Assert.Equal(CoreState.Ok, shell.Installer.Check().State);
        shell.Installer.Written = f => { if (f.EndsWith("WinDivert64.sys")) File.Delete(f); };
        var e = await Assert.ThrowsAsync<AvBlockedException>(() => shell.RepairCoreAsync());
        Assert.Contains("WinDivert64.sys", e.What);
        Assert.Contains("возможно, антивирусом", e.Message);
        Assert.Equal(CoreState.Ok, shell.Installer.Check().State);   // атомарная замена: прежнее ядро на месте
    }

    /// <summary>winws.exe исчез между проверкой и запуском: «удалён или заблокирован», а не «не запустился».</summary>
    [Fact]
    public void Winws_missing_at_start_is_av_blocked()
    {
        using var t = new TempDir();
        var p = new AppPaths(t.Sub("root"), Repo.Data);
        Directory.CreateDirectory(p.Core);
        var e = Assert.Throws<AvBlockedException>(() => WinwsProcess.Start(p, ["--version"]));
        Assert.Equal(p.Winws, e.FilePath);
        Assert.Contains("winws.exe", e.Message);
    }

    [Theory]
    [InlineData("windivert: error opening filter: 577", 577, "577")]
    [InlineData("WinDivertOpen failed, error 1275", 1275, "1275")]
    [InlineData("windivert: error 2", 2, "антивирус")]
    [InlineData("windivert init: error code 225", 225, "антивирус")]
    [InlineData("windivert: error 5", 5, "код 5")]
    [InlineData("windivert: error 87", 87, "87")]
    public void Driver_failure_is_explained_by_code(string output, int code, string expected)
    {
        Assert.Equal(code, WinwsFailure.DriverCode(output));
        Assert.Contains(expected, WinwsFailure.Explain(output));
    }

    [Fact]
    public void Unrelated_winws_output_is_not_a_driver_failure()
    {
        Assert.Null(WinwsFailure.Explain("unknown option --foo"));
        Assert.Null(WinwsFailure.Explain(""));
    }

    /// <summary>Служба помощника есть, а exe удалён — «возможно, его удалил антивирус» и «Переустановить помощника».</summary>
    [Fact]
    public async Task Helper_exe_deleted_says_so_and_offers_reinstall()
    {
        using var t = new TempDir();
        var exe = @"C:\ProgramData\DpiShell\helper\DpiShell.exe";
        var link = new HelperLink
        {
            Connector = _ => throw new HelperUnavailableException(HelperUnavailableReason.NotRunning),
            Setup = new NoSetup(), MissingHelperExe = () => exe,
        };
        await link.ConnectAsync();
        Assert.Equal(HelperState.NotResponding, link.State);
        Assert.Equal(L.T("shell.helper.exe_missing", new { path = exe }), link.Problem);
        var vm = TestWindow.Model(new Shell(new AppPaths(t.Sub("root"), Repo.Data)), link);
        Assert.Equal(link.Problem, vm.HelperProblem);
        Assert.Equal(L.T("about.helper.fix_reinstall"), vm.FixHelperText);

        // exe на месте (или не узнать) — прежняя причина.
        link.MissingHelperExe = () => null;
        await link.ConnectAsync();
        Assert.DoesNotContain(exe, link.Problem);
        await link.DisposeAsync();
    }

    /// <summary>Распакованный exe обновления удалён — «возможно, антивирусом», программа не изменена (не «битый архив»).</summary>
    [Fact]
    public async Task Update_exe_deleted_while_unpacking()
    {
        using var t = new TempDir();
        var store = new AppVersionStore(t.Sub("versions"));
        store.Written = f => { if (f.EndsWith("DpiShell.exe")) File.Delete(f); };
        var h = AppUpdateTests.Serve(new MapHandler(), AppUpdateTests.A, "1.2.0");
        var up = new AppUpdater(AppUpdateTests.Client(h, AppUpdateTests.A), store) { CurrentVersion = "1.0.0", CurrentExe = t.Sub("portable\\DpiShell.exe"), CurrentPid = 1 };
        var p = await up.PrepareAsync(await up.CheckAsync());
        Assert.False(p.Ok);
        Assert.Equal(L.T("core.upd.av_blocked", new { path = store.Root }), p.Message);
        Assert.False(store.IsInstalled("1.2.0"));
    }
}
