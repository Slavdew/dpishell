using DpiShell.Core.Localization;
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
            foreach (var r in _paths.Harden()) _log.Write("убрано из папки программы: " + r);
        }

        var owner = HelperInstaller.ReadOwner(_paths);
        if (owner is null) _log.Write("нет SID пользователя — канал доступен только администраторам");

        var shell = new Shell(_paths);
        // Разложить данные, проверить ядро (хеши) и вернуть обход как было; сетевую часть — в фоне, чтобы не держать SCM.
        _ = Task.Run(async () =>
        {
            try
            {
                var log = new Progress<string>(_log.Write);
                var check = await shell.PrepareAsync(log);
                _log.Write($"ядро: {check.State}");
                // Только что установлен, ядра нет: ждём команды окна (после вопроса об исключении), не дольше CoreWait.Limit — потом качаем сами.
                await shell.InstallAfterWaitAsync(log);
            }
            catch (Exception e) { _log.Write("подготовка: " + e.Message); }
        });

        // Действия на весь компьютер — только от окна той же сборки, что эта копия (её положило окно, установившее помощника).
        var image = Environment.ProcessPath is { } self ? new HelperClientImage(self) : null;
        if (image is null) _log.Write("путь своего exe не известен — действия на весь компьютер не принимаются ни от кого");
        else _ = Task.Run(image.Warm);
        _server = new HelperServer(new ShellHelperBackend(shell), new HelperServerOptions
        {
            AllowedUser = owner,
            CheckClient = image is null ? _ => ClientBuild.Other : image.Check,
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

    /// <summary>Сколько последних строк журнала отдаёт <c>log_tail</c> («Сохранить отчёт»).</summary>
    public const int TailLines = 200;

    /// <summary>Последние <paramref name="count"/> строк helper.log (читается и пока помощник в него пишет); нет журнала или не читается — пусто.</summary>
    public static IReadOnlyList<string> Tail(AppPaths paths, int count)
    {
        try
        {
            var file = Path.Combine(paths.Logs, "helper.log");
            if (!System.IO.File.Exists(file)) return [];
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, System.Text.Encoding.UTF8);
            var queue = new Queue<string>();
            while (sr.ReadLine() is { } line)
            {
                queue.Enqueue(line);
                if (queue.Count > count) queue.Dequeue();
            }
            return [.. queue];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

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
    /// <param name="lang">Язык окна (--lang=): на нём журнал установки и имя службы; нет — по языку Windows.</param>
    public static int Install(string? userSid = null, IProgress<string>? log = null, string? lang = null)
    {
        using var scope = L.Scope(lang ?? L.DefaultFor(System.Globalization.CultureInfo.CurrentUICulture));
        try
        {
            var sid = userSid is null ? WindowsIdentity.GetCurrent().User! : new SecurityIdentifier(userSid);
            new HelperInstaller(new AppPaths()).Install(sid, log);
            return 0;
        }
        catch (ServiceMarkedForDeleteException e)
        {
            log?.Report(e.Message);
            return HelperInstaller.MarkedForDelete;
        }
        catch (Exception e)
        {
            log?.Report(e.Message);
            return 1;
        }
    }

    /// <param name="purge">--uninstall-helper --purge: стереть и данные программы (%ProgramData%\DpiShell).</param>
    public static int Uninstall(IProgress<string>? log = null, bool purge = false)
    {
        try
        {
            new HelperInstaller(new AppPaths()).Uninstall(log, purge);
            return 0;
        }
        catch (ServiceMarkedForDeleteException e)
        {
            log?.Report(e.Message);
            return HelperInstaller.MarkedForDelete;
        }
        catch (Exception e)
        {
            log?.Report(e.Message);
            return 1;
        }
    }
}
