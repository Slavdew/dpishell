using System.Diagnostics;
using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Principal;
using DpiShell.Core;

namespace DpiShell.Tests;

/// <summary>Тест, которому нужны права администратора (смена владельца, закрытые права): без них — пропуск с причиной.</summary>
public sealed class AdminFactAttribute : FactAttribute
{
    public AdminFactAttribute()
    {
        if (!Shell.IsAdmin()) Skip = "нужны права администратора: Harden ставит владельца «Администраторы» и закрывает папку от обычного пользователя";
    }
}

/// <summary>
/// Аудит безопасности ПК (security-audit-pc.md): ссылки и чужие записи в %ProgramData%\DpiShell, хеш ядра перед запуском
/// службы, гонка PID в stop_foreign, предел распаковки обновления по фактическим байтам.
/// </summary>
public sealed class FolderSafetyTests : IDisposable
{
    readonly TempDir _t = new();

    public void Dispose() => _t.Dispose();

    /// <summary>Junction (mklink /J) прав администратора не требует.</summary>
    static void Junction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "/c", "mklink", "/J", link, target }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0, "junction не создалась: " + p.StandardError.ReadToEnd());
    }

    static void HardLink(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "/c", "mklink", "/H", link, target }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        Assert.True(File.Exists(link), "жёсткая ссылка не создалась: " + p.StandardError.ReadToEnd());
    }

    static string Sddl(string path) => (Directory.Exists(path)
        ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl()
        : new FileInfo(path).GetAccessControl()).GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner);

    static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    (AppPaths Paths, string Outside) Setup()
    {
        var outside = _t.Sub("attacker");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "чужое");
        return (new AppPaths(_t.Sub("root"), Repo.Data), outside);
    }

    [Fact]
    public void IsHardened_JunctionInside_NotHardened()
    {
        var (p, outside) = Setup();
        Directory.CreateDirectory(p.Root);
        Directory.CreateDirectory(p.Logs);
        Assert.True(p.IsHardened());   // временная папка пользователя закрыта от «всех пользователей», владелец — этот процесс
        Junction(p.Core, outside);
        Assert.False(p.IsHardened());
    }

    [Fact]
    public void IsHardened_RootIsJunction_NotHardened()
    {
        var (p, outside) = Setup();
        Junction(p.Root, outside);
        Assert.False(p.IsHardened());
    }

    [AdminFact]
    public void Harden_JunctionInside_LinkRemoved_TargetUntouched()
    {
        var (p, outside) = Setup();
        Directory.CreateDirectory(p.Root);
        Junction(p.Core, outside);
        Junction(p.Logs, outside);
        var before = Sddl(outside);
        var beforeFile = Sddl(Path.Combine(outside, "secret.txt"));

        var removed = p.Harden();

        Assert.Contains(p.Core, removed);
        Assert.Contains(p.Logs, removed);
        Assert.False(IsLink(p.Core));   // создана заново настоящей папкой
        Assert.False(IsLink(p.Logs));
        Assert.Equal(before, Sddl(outside));   // права цели ссылки не тронуты
        Assert.Equal(beforeFile, Sddl(Path.Combine(outside, "secret.txt")));
        Assert.Equal(["secret.txt"], Directory.GetFileSystemEntries(outside).Select(Path.GetFileName));
        Assert.True(p.IsHardened());
    }

    [AdminFact]
    public void Harden_RootIsJunction_ReplacedByRealFolder()
    {
        var (p, outside) = Setup();
        Junction(p.Root, outside);
        var before = Sddl(outside);

        var removed = p.Harden();

        Assert.Contains(p.Root, removed);
        Assert.False(IsLink(p.Root));
        Assert.True(Directory.Exists(p.Core));
        Assert.Equal(before, Sddl(outside));
        Assert.Equal(["secret.txt"], Directory.GetFileSystemEntries(outside).Select(Path.GetFileName));   // core, logs… не в чужой папке
        Assert.True(p.IsHardened());
    }

    [AdminFact]
    public void Harden_HardLinkInside_NameRemoved_OriginalUntouched()
    {
        var (p, outside) = Setup();
        Directory.CreateDirectory(p.Root);
        var original = Path.Combine(outside, "secret.txt");
        var link = Path.Combine(p.Root, "state.json");
        HardLink(link, original);
        var before = Sddl(original);

        var removed = p.Harden();

        Assert.Contains(link, removed);
        Assert.False(File.Exists(link));
        Assert.Equal("чужое", File.ReadAllText(original));
        Assert.Equal(before, Sddl(original));
    }

    [AdminFact]
    public void Harden_RootWasOpenToUsers_DownloadedDataRemoved_Closed()
    {
        var (p, _) = Setup();
        Directory.CreateDirectory(p.Updates);
        File.WriteAllText(Path.Combine(p.Updates, "data.json"), "{}");
        File.WriteAllText(p.StateFile, "{}");
        var acl = new DirectoryInfo(p.Root).GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(p.Root).SetAccessControl(acl);
        Assert.False(p.IsHardened());

        var removed = p.Harden();

        Assert.Contains(p.Updates, removed);
        Assert.False(Directory.Exists(p.Updates));   // скачанные данные мог положить кто угодно — в работе поставка
        Assert.True(File.Exists(p.StateFile));        // остальное остаётся, права — наследуемые от закрытого корня
        Assert.True(p.IsHardened());
        Assert.Equal(p.Package, p.Seed);
    }

    [AdminFact]
    public void Harden_ClosedRoot_NothingRemoved_DataKept()
    {
        var (p, _) = Setup();
        p.Harden();
        Directory.CreateDirectory(p.Updates);
        File.WriteAllText(Path.Combine(p.Updates, "data.json"), "{}");
        Assert.Empty(p.Harden());
        Assert.True(Directory.Exists(p.Updates));
    }

    // ---------- хеш ядра перед запуском службы ----------

    [Fact]
    public void ServiceApply_CoreWinwsChanged_RefusedBeforeSc()
    {
        var p = new AppPaths(_t.Sub("root"), Repo.Data);
        var shell = new Shell(p);
        Directory.CreateDirectory(p.Core);
        foreach (var name in shell.Installer.Manifest.Files.Keys) File.WriteAllText(Path.Combine(p.Core, name), "подменён");
        Assert.Contains("winws.exe", shell.CoreProblem());
        Assert.Contains("WinDivert64.sys", shell.CoreProblem());
        // Проверка — первым делом в Apply: до sc create/config/start дело не доходит.
        var e = Assert.Throws<SecurityException>(() => shell.Service.Apply(["--wf-tcp=443"]));
        Assert.Contains("winws.exe", e.Message);
    }

    [Fact]
    public void ServiceApply_CoreMissing_Refused()
    {
        var shell = new Shell(new AppPaths(_t.Sub("root"), Repo.Data));
        Assert.NotNull(shell.CoreProblem());
        Assert.Throws<SecurityException>(() => shell.Service.Apply(["--wf-tcp=443"]));
    }

    // ---------- stop_foreign: тот ли это процесс ----------

    [Fact]
    public void IsSameProcess_SameNamePathAndStartedBeforeSnapshot()
    {
        using var me = Process.GetCurrentProcess();
        Assert.True(Shell.IsSameProcess(me, Environment.ProcessPath, DateTime.Now));
    }

    [Fact]
    public void IsSameProcess_StartedAfterSnapshot_PidReused_No()
    {
        using var me = Process.GetCurrentProcess();
        Assert.False(Shell.IsSameProcess(me, Environment.ProcessPath, me.StartTime.AddSeconds(-1)));
    }

    [Fact]
    public void IsSameProcess_OtherImage_No()
    {
        using var me = Process.GetCurrentProcess();
        Assert.False(Shell.IsSameProcess(me, @"C:\zapret\bin\winws.exe", DateTime.Now));
        var samePathOtherDir = Path.Combine(_t.Path, Path.GetFileName(Environment.ProcessPath!));
        Assert.False(Shell.IsSameProcess(me, samePathOtherDir, DateTime.Now));
        Assert.False(Shell.IsSameProcess(me, null, DateTime.Now));   // путь неизвестен — только winws/goodbyedpi
    }

    // ---------- предел распаковки обновления ----------

    static string Zip(string file, byte[] data, CompressionLevel level = CompressionLevel.Optimal)
    {
        using (var z = ZipFile.Open(file, ZipArchiveMode.Create))
        using (var s = z.CreateEntry("DpiShell/big.bin", level).Open())
            s.Write(data);
        return file;
    }

    [Fact]
    public void ExtractLimited_CountsActualBytes_OverLimitRefused()
    {
        var zip = Zip(_t.Sub("a.zip"), new byte[10_000]);
        using var z = ZipFile.OpenRead(zip);
        var e = z.Entries.Single();
        Assert.Equal(10_000, AppVersionStore.ExtractLimited(e, _t.Sub("ok.bin"), 0, max: 10_000));
        Assert.Throws<InvalidDataException>(() => AppVersionStore.ExtractLimited(e, _t.Sub("big.bin"), 0, max: 4_096));
        Assert.Throws<InvalidDataException>(() => AppVersionStore.ExtractLimited(e, _t.Sub("sum.bin"), 9_000, max: 10_000));   // итог по всем записям
    }

    [Fact]
    public void ExtractLimited_LyingDeclaredSize_CountsWritten_LimitHolds()
    {
        var zip = Zip(_t.Sub("lie.zip"), new byte[100_000]);
        // Заявленный несжатый размер (локальный заголовок и каталог) — 10 байт вместо 100 000.
        var b = File.ReadAllBytes(zip);
        for (int i = 0; i + 4 <= b.Length; i++)
        {
            if (b[i] == 'P' && b[i + 1] == 'K' && b[i + 2] == 3 && b[i + 3] == 4) BitConverter.GetBytes(10u).CopyTo(b, i + 22);
            if (b[i] == 'P' && b[i + 1] == 'K' && b[i + 2] == 1 && b[i + 3] == 2) BitConverter.GetBytes(10u).CopyTo(b, i + 24);
        }
        File.WriteAllBytes(zip, b);
        using var z = ZipFile.OpenRead(zip);
        var e = z.Entries.Single();
        Assert.Equal(10, e.Length);
        // Что бы ни заявлял архив, счёт идёт по записанному на диск, и предел соблюдён.
        var x = _t.Sub("x.bin");
        var n = AppVersionStore.ExtractLimited(e, x, 0, max: 4_096);
        Assert.Equal(new FileInfo(x).Length, n);
        Assert.True(n <= 4_096);
    }
}
