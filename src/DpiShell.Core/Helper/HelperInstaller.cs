using System.Security.Principal;

namespace DpiShell.Core.Helper;

/// <summary>
/// Установка помощника (11-helper-spec §2): копия программы и data\ в закрытую %ProgramData%\DpiShell\helper\,
/// служба DpiShellHelper (LocalSystem, автозапуск, перезапуск при сбое), SID пользователя окна — там же.
/// Работает только с правами администратора. Сборка аргументов sc и путей — чистые функции (их проверяют тесты).
/// </summary>
public sealed class HelperInstaller(AppPaths paths)
{
    public const string DisplayName = "DpiShell — помощник";
    public const string Description = "Выполняет действия DpiShell, которым нужны права администратора (обход DPI, подбор, hosts). Управляется окном DpiShell.";
    public const string OwnerFile = "owner.sid";

    public static string HelperDir(AppPaths p) => Path.Combine(p.Root, "helper");
    public static string OwnerPath(AppPaths p) => Path.Combine(HelperDir(p), OwnerFile);

    /// <summary>Командная строка службы: exe из закрытой папки с ключом --helper.</summary>
    public static string BinPath(string exe) => $"\"{exe}\" --helper";

    public static string[] ScCreateArgs(string exe) =>
        ["create", HelperProtocol.ServiceName, "binPath=", BinPath(exe), "DisplayName=", DisplayName, "start=", "auto", "obj=", "LocalSystem"];

    /// <summary>Служба уже есть (обновление) — перенастроить на новый путь.</summary>
    public static string[] ScConfigArgs(string exe) =>
        ["config", HelperProtocol.ServiceName, "binPath=", BinPath(exe), "DisplayName=", DisplayName, "start=", "auto", "obj=", "LocalSystem"];

    public static string[] ScDescriptionArgs() => ["description", HelperProtocol.ServiceName, Description];

    /// <summary>Перезапуск при сбое: дважды через 5 с, счётчик сбрасывается через сутки.</summary>
    public static string[] ScFailureArgs() => ["failure", HelperProtocol.ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/5000/none/0"];

    public static string[] ScStartArgs() => ["start", HelperProtocol.ServiceName];
    public static string[] ScStopArgs() => ["stop", HelperProtocol.ServiceName];
    public static string[] ScDeleteArgs() => ["delete", HelperProtocol.ServiceName];

    /// <summary>Лежит ли каталог внутри закрытой папки помощника (служба отказывается стартовать из другого места).</summary>
    public static bool IsInHelperDir(AppPaths p, string dir)
    {
        var helper = Path.TrimEndingDirectorySeparator(Path.GetFullPath(HelperDir(p)));
        var d = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        return d.Equals(helper, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Какие файлы копировать: всё из каталога программы (exe, для обычной сборки — dll, runtimes\, data\), кроме отладочных
    /// и логов. Пути — относительные. Для single-file публикации это exe и data\.
    /// </summary>
    public static IReadOnlyList<string> FilesToCopy(string sourceDir) =>
        Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(sourceDir, f))
            .Where(r => !r.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) && !r.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>SID пользователя окна, записанный при установке; нет или битый — null (канал только администраторам).</summary>
    public static SecurityIdentifier? ReadOwner(AppPaths p)
    {
        try { return File.Exists(OwnerPath(p)) ? new SecurityIdentifier(File.ReadAllText(OwnerPath(p)).Trim()) : null; }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
    }

    public void Install(SecurityIdentifier user, IProgress<string>? log = null)
    {
        if (!Shell.IsAdmin()) throw new UnauthorizedAccessException("Установка помощника — только с правами администратора.");
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Не найден исполняемый файл программы.");
        var source = Path.GetDirectoryName(exe)!;
        var dir = HelperDir(paths);
        if (IsInHelperDir(paths, source)) throw new InvalidOperationException("Установка запущена из папки помощника — запустите её из папки программы.");

        // Сначала закрыть папку, потом класть в неё исполняемый файл.
        log?.Report("Закрываю папку программы от изменений…");
        paths.Harden();

        bool exists = ServiceManager.Query(HelperProtocol.ServiceName) != SvcState.NotInstalled;
        if (exists)
        {
            log?.Report("Останавливаю прежнюю версию помощника…");
            ServiceManager.Sc(ScStopArgs());
            ServiceManager.WaitFor(HelperProtocol.ServiceName, SvcState.Stopped, TimeSpan.FromSeconds(30));
        }

        log?.Report("Копирую помощника…");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        foreach (var rel in FilesToCopy(source))
        {
            var to = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(Path.Combine(source, rel), to, overwrite: true);
        }
        File.WriteAllText(OwnerPath(paths), user.Value);
        // Права наследуются от закрытого корня; явные права у скопированного сбрасываем.
        paths.Harden();
        if (!paths.IsHardened()) throw new InvalidOperationException("Не удалось закрыть папку помощника от пользователей.");

        var helperExe = Path.Combine(dir, Path.GetFileName(exe));
        Check(ServiceManager.Sc(exists ? ScConfigArgs(helperExe) : ScCreateArgs(helperExe)), exists ? "перенастроить службу помощника" : "создать службу помощника");
        ServiceManager.Sc(ScDescriptionArgs());
        ServiceManager.Sc(ScFailureArgs());
        log?.Report("Запускаю помощника…");
        Check(ServiceManager.Sc(ScStartArgs()), "запустить помощника");
        if (!ServiceManager.WaitFor(HelperProtocol.ServiceName, SvcState.Running, TimeSpan.FromSeconds(20)))
            throw new InvalidOperationException("Помощник не запустился за 20 секунд — см. logs\\helper.log.");
        log?.Report("Помощник установлен.");
    }

    /// <summary>Удаление: остановить и удалить службу, стереть папку помощника. Данные программы (state, результаты) остаются.</summary>
    public void Uninstall(IProgress<string>? log = null)
    {
        if (!Shell.IsAdmin()) throw new UnauthorizedAccessException("Удаление помощника — только с правами администратора.");
        if (ServiceManager.Query(HelperProtocol.ServiceName) != SvcState.NotInstalled)
        {
            log?.Report("Останавливаю помощника…");
            ServiceManager.Sc(ScStopArgs());
            ServiceManager.WaitFor(HelperProtocol.ServiceName, SvcState.Stopped, TimeSpan.FromSeconds(30));
            Check(ServiceManager.Sc(ScDeleteArgs()), "удалить службу помощника");
        }
        var dir = HelperDir(paths);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        log?.Report("Помощник удалён.");
    }

    static void Check((int Code, string Output) r, string what)
    {
        if (r.Code != 0) throw new InvalidOperationException($"Не удалось {what}: код {r.Code}. {r.Output.Trim()}");
    }
}
