using DpiShell.Core;
using DpiShell.Core.Helper;
using DpiShell.Core.Localization;

namespace DpiShell.Tests;

/// <summary>Установка помощника — только чистые функции: аргументы sc, пути, что копировать, SID владельца. sc не запускается.</summary>
public class HelperInstallerTests
{
    const string Exe = @"C:\ProgramData\DpiShell\helper\DpiShell.exe";

    [Fact]
    public void Sc_create_is_localsystem_auto_with_helper_switch()
    {
        var a = HelperInstaller.ScCreateArgs(Exe);
        Assert.Equal(["create", "DpiShellHelper"], a[..2]);
        Assert.Equal("\"" + Exe + "\" --helper", a[Array.IndexOf(a, "binPath=") + 1]);
        Assert.Equal("auto", a[Array.IndexOf(a, "start=") + 1]);
        Assert.Equal("LocalSystem", a[Array.IndexOf(a, "obj=") + 1]);
        // Путь с пробелами разбирается Windows обратно в exe + ключ.
        var parsed = Native.ParseCommandLine(HelperInstaller.BinPath(@"C:\Program Data\Dpi Shell\helper\DpiShell.exe"));
        Assert.Equal([@"C:\Program Data\Dpi Shell\helper\DpiShell.exe", "--helper"], parsed);
    }

    [Fact]
    public void Display_name_and_description_follow_window_language_service_name_stays()
    {
        using (L.Scope(L.En))
        {
            var a = HelperInstaller.ScCreateArgs(Exe);
            Assert.Equal("DpiShellHelper", a[1]);
            Assert.Equal("DpiShell Helper", a[Array.IndexOf(a, "DisplayName=") + 1]);
            Assert.DoesNotMatch("[А-Яа-яЁё]", HelperInstaller.ScDescriptionArgs()[2]);
            Assert.Equal("DpiShell Helper", HelperInstaller.ScConfigArgs(Exe)[Array.IndexOf(HelperInstaller.ScConfigArgs(Exe), "DisplayName=") + 1]);
        }
        using (L.Scope(L.Ru))
            Assert.Equal("DpiShell — помощник", HelperInstaller.ScCreateArgs(Exe)[Array.IndexOf(HelperInstaller.ScCreateArgs(Exe), "DisplayName=") + 1]);
    }

    [Fact]
    public void Install_lang_arg_is_parsed_from_command_line()
    {
        Assert.Equal("en", HelperInstaller.LangFrom(["S-1-5-21-1-2-3-1001", HelperInstaller.LangArg + "en"]));
        Assert.Null(HelperInstaller.LangFrom(["S-1-5-21-1-2-3-1001"]));
    }

    [Fact]
    public async Task Delete_waits_until_exe_is_released()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dpishell-del-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var held = File.Open(Path.Combine(dir, "DpiShell.exe"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        // Как уходящий процесс службы: отпускает файл чуть позже, чем SCM ответил «остановлена».
        var release = Task.Delay(600).ContinueWith(_ => held.Dispose());
        HelperInstaller.DeleteWhenReleased(dir, TimeSpan.FromSeconds(10));
        await release;
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void Delete_gives_up_with_original_error()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dpishell-del-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using (File.Open(Path.Combine(dir, "DpiShell.exe"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            Assert.ThrowsAny<IOException>(() => HelperInstaller.DeleteWhenReleased(dir, TimeSpan.FromMilliseconds(700)));
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void Sc_other_commands_target_helper_service_only()
    {
        Assert.Equal("config", HelperInstaller.ScConfigArgs(Exe)[0]);
        foreach (var a in new[] { HelperInstaller.ScConfigArgs(Exe), HelperInstaller.ScDescriptionArgs(), HelperInstaller.ScFailureArgs(),
                     HelperInstaller.ScStartArgs(), HelperInstaller.ScStopArgs(), HelperInstaller.ScDeleteArgs() })
            Assert.Equal(HelperProtocol.ServiceName, a[1]);
        var f = HelperInstaller.ScFailureArgs();
        Assert.StartsWith("restart/", f[Array.IndexOf(f, "actions=") + 1]);
        Assert.NotEqual(ServiceManager.Name, HelperProtocol.ServiceName);   // служба оптимизации остаётся отдельной
    }

    [Fact]
    public void Helper_dir_is_inside_closed_root()
    {
        using var t = new TempDir();
        var p = new AppPaths(t.Sub("root"), Repo.Data);
        var dir = HelperInstaller.HelperDir(p);
        Assert.Equal(Path.Combine(p.Root, "helper"), dir);
        Assert.True(HelperInstaller.IsInHelperDir(p, dir + Path.DirectorySeparatorChar));
        Assert.False(HelperInstaller.IsInHelperDir(p, p.Root));
        Assert.False(HelperInstaller.IsInHelperDir(p, t.Sub("elsewhere")));
        Assert.False(HelperInstaller.IsInHelperDir(p, dir + "-evil"));
    }

    [Fact]
    public void Files_to_copy_keep_data_and_skip_pdb()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(t.Sub(@"data\lists"));
        File.WriteAllText(t.Sub("DpiShell.exe"), "x");
        File.WriteAllText(t.Sub("DpiShell.pdb"), "x");
        File.WriteAllText(t.Sub(@"data\strategies.json"), "{}");
        File.WriteAllText(t.Sub(@"data\lists\general.txt"), "a");
        var files = HelperInstaller.FilesToCopy(t.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.True(files.SetEquals([@"data\lists\general.txt", @"data\strategies.json", "DpiShell.exe"]));
    }

    [Fact]
    public void Owner_sid_read_back_or_null()
    {
        using var t = new TempDir();
        var p = new AppPaths(t.Sub("root"), Repo.Data);
        Assert.Null(HelperInstaller.ReadOwner(p));
        Directory.CreateDirectory(HelperInstaller.HelperDir(p));
        File.WriteAllText(HelperInstaller.OwnerPath(p), "S-1-5-21-1-2-3-1001\r\n");
        Assert.Equal("S-1-5-21-1-2-3-1001", HelperInstaller.ReadOwner(p)!.Value);
        File.WriteAllText(HelperInstaller.OwnerPath(p), "C:\\not-a-sid");
        Assert.Null(HelperInstaller.ReadOwner(p));
    }

    [Fact]
    public void Shell_backend_catalog_and_user_targets()
    {
        using var t = new TempDir();
        var shell = new Shell(new AppPaths(t.Sub("root"), Repo.Data));
        var b = new ShellHelperBackend(shell);
        Assert.Contains("alt9", b.StrategyIds);
        Assert.Contains("user", b.GroupIds);
        Assert.Contains("youtube", b.GroupIds);

        var r = b.SetUserTargets(["example.com", "  ", "не домен!!", "Example.com"]);
        Assert.Equal(["example.com"], r.Saved);
        Assert.Contains("не домен!!", r.Rejected);
        Assert.Contains("user", shell.State.Groups);
        Assert.Equal(["example.com"], AppState.Load(shell.Paths).UserTargets);

        var results = b.ReadResults();
        Assert.Empty(results.Results);
    }
}
