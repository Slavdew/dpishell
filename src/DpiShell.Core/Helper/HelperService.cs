using System.Security.Principal;
using System.ServiceProcess;

namespace DpiShell.Core.Helper;

/// <summary>
/// Служба Windows DpiShellHelper (SYSTEM): тот же DpiShell.exe с ключом --helper из закрытой папки
/// %ProgramData%\DpiShell\helper\. Держит канал и выполняет команды окна через <see cref="Shell"/>.
/// </summary>
public sealed class HelperService : ServiceBase
{
    readonly AppPaths _paths;
    HelperServer? _server;
    HelperLog? _log;

    public HelperService(AppPaths? paths = null)
    {
        _paths = paths ?? new AppPaths();
        ServiceName = HelperProtocol.ServiceName;
        CanStop = true;
        CanShutdown = true;
        AutoLog = true;
    }

    protected override void OnStart(string[] args)
    {
        _log = new HelperLog(_paths);
        _log.Write($"старт помощника {HelperProtocol.AppVersion}");

        // Запуск только из закрытой папки: иначе файл, который система запускает от SYSTEM, мог подменить пользователь.
        if (!HelperInstaller.IsInHelperDir(_paths, AppContext.BaseDirectory))
            throw new InvalidOperationException($"Помощник запущен не из {HelperInstaller.HelperDir(_paths)} — отказ.");
        if (!_paths.IsHardened())
        {
            _log.Write("папка программы оказалась открыта пользователям — закрываю заново");
            _paths.Harden();
        }

        var owner = HelperInstaller.ReadOwner(_paths);
        if (owner is null) _log.Write("нет SID пользователя — канал доступен только администраторам");

        var shell = new Shell(_paths);
        // Разложить данные, проверить ядро (хеши) и вернуть обход как было; сетевую часть — в фоне, чтобы не держать SCM.
        _ = Task.Run(async () =>
        {
            try
            {
                var check = await shell.PrepareAsync(new Progress<string>(_log.Write));
                _log.Write($"ядро: {check.State}");
            }
            catch (Exception e) { _log.Write("подготовка: " + e.Message); }
        });

        _server = new HelperServer(new ShellHelperBackend(shell), new HelperServerOptions
        {
            AllowedUser = owner,
            Log = _log.Write,
        });
        _server.Start();
        _log.Write("канал открыт");
    }

    protected override void OnStop() => StopServer();
    protected override void OnShutdown() => StopServer();

    void StopServer()
    {
        try { _server?.StopAsync().Wait(TimeSpan.FromSeconds(20)); }
        catch (Exception e) { _log?.Write("остановка: " + e.Message); }
        _server = null;
        _log?.Write("помощник остановлен");
    }
}

/// <summary>Журнал помощника: %ProgramData%\DpiShell\logs\helper.log (закрытая папка), с ограничением размера.</summary>
public sealed class HelperLog(AppPaths paths)
{
    const long MaxBytes = 1024 * 1024;
    readonly object _lock = new();
    string File => Path.Combine(paths.Logs, "helper.log");

    public void Write(string message)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(paths.Logs);
                if (System.IO.File.Exists(File) && new FileInfo(File).Length > MaxBytes)
                    System.IO.File.Move(File, File + ".old", overwrite: true);
                System.IO.File.AppendAllText(File, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

/// <summary>Входы для DpiShell.exe: --helper (служба), --install-helper, удаление.</summary>
public static class HelperEntry
{
    /// <summary>--helper: работа службой (вызывается SCM). Возвращает код выхода.</summary>
    public static int RunService(string[] args)
    {
        ServiceBase.Run(new HelperService());
        return 0;
    }

    /// <summary>
    /// --install-helper (процесс уже с правами администратора, запущен окном через runas).
    /// <paramref name="userSid"/> — SID пользователя окна: при вводе пароля другого администратора в UAC процесс
    /// установки работает от его имени, поэтому окно передаёт свой SID явно. null — SID текущего процесса.
    /// </summary>
    public static int Install(string? userSid = null, IProgress<string>? log = null)
    {
        try
        {
            var sid = userSid is null ? WindowsIdentity.GetCurrent().User! : new SecurityIdentifier(userSid);
            new HelperInstaller(new AppPaths()).Install(sid, log);
            return 0;
        }
        catch (Exception e)
        {
            log?.Report(e.Message);
            return 1;
        }
    }

    public static int Uninstall(IProgress<string>? log = null)
    {
        try
        {
            new HelperInstaller(new AppPaths()).Uninstall(log);
            return 0;
        }
        catch (Exception e)
        {
            log?.Report(e.Message);
            return 1;
        }
    }
}
