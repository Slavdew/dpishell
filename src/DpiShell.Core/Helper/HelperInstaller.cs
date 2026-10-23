using DpiShell.Core.Localization;
using System.Security.Principal;

namespace DpiShell.Core.Helper;

/// <summary>
/// Установка помощника (helper-protocol §2): копия программы и data\ в закрытую %ProgramData%\DpiShell\helper\,
/// служба DpiShellHelper (LocalSystem, автозапуск, перезапуск при сбое), SID пользователя окна — там же.
/// Работает только с правами администратора. Сборка аргументов sc и путей — чистые функции (их проверяют тесты).
/// </summary>
public sealed class HelperInstaller(AppPaths paths)
{
    /// <summary>Имя и описание в «Службах» — на языке окна при установке (ключ --lang); имя службы <see cref="HelperProtocol.ServiceName"/> не меняется (аудит-2, П-17).</summary>
    public static string DisplayName => L.T("core.helper.svc.name");
    public static string Description => L.T("core.helper.svc.description");
    /// <summary>Ключ к --install-helper: язык окна (<c>--lang=en</c>) — на нём журнал установки и имя службы в «Службах».</summary>
    public const string LangArg = "--lang=";
    public static string? LangFrom(IEnumerable<string> args) =>
        args.FirstOrDefault(a => a.StartsWith(LangArg, StringComparison.OrdinalIgnoreCase)) is { } a ? a[LangArg.Length..] : null;
    public const string OwnerFile = "owner.sid";
    /// <summary>
    /// Ключ к --install-helper: до запуска службы (она сразу скачивает ядро) добавить %ProgramData%\DpiShell в исключения Защитника — человек согласился
    /// в окне до запроса прав. Путь — свой, у ключа значения нет.
    /// </summary>
    public const string AvExclusionArg = "--av-exclusion";

    /// <summary>Защитник для <see cref="AvExclusionArg"/> и снятия своего исключения при <c>--purge</c>.</summary>
    public IDefenderSystem Defender { get; init; } = new WmiDefender();

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

    /// <param name="avExclusion">Добавить папку программы в исключения Защитника до запуска службы (<see cref="AvExclusionArg"/>).</param>
    public void Install(SecurityIdentifier user, IProgress<string>? log = null, bool avExclusion = false)
    {
        if (!Shell.IsAdmin()) throw new UnauthorizedAccessException(L.T("core.helper.need_admin_install"));
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException(L.T("core.helper.no_exe"));
        var source = Path.GetDirectoryName(exe)!;
        var dir = HelperDir(paths);
        if (IsInHelperDir(paths, source)) throw new InvalidOperationException(L.T("core.helper.install_from_helper_dir"));

        // Сначала закрыть папку, потом класть в неё исполняемый файл.
        log?.Report(L.T("core.helper.inst.harden"));
        foreach (var r in paths.Harden()) log?.Report(L.T("core.shell.unsafe_removed", new { item = r }));

        bool exists = ServiceManager.Query(HelperProtocol.ServiceName) != SvcState.NotInstalled;
        if (exists)
        {
            log?.Report(L.T("core.helper.inst.stop_old"));
            ServiceManager.Sc(ScStopArgs());
            ServiceManager.WaitFor(HelperProtocol.ServiceName, SvcState.Stopped, TimeSpan.FromSeconds(30));
        }

        log?.Report(L.T("core.helper.inst.copy"));
        DeleteWhenReleased(dir);
        Directory.CreateDirectory(dir);
        foreach (var rel in FilesToCopy(source))
        {
            var to = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(Path.Combine(source, rel), to, overwrite: true);
        }
        File.WriteAllText(OwnerPath(paths), user.Value);
        // Права наследуются от закрытого корня; явные права у скопированного сбрасываем.
        foreach (var r in paths.Harden()) log?.Report(L.T("core.shell.unsafe_removed", new { item = r }));
        if (!paths.IsHardened()) throw new InvalidOperationException(L.T("core.helper.lock_failed"));

        // Служба при запуске сразу скачивает ядро — исключение должно стоять раньше, иначе Защитник успевает удалить архив.
        if (avExclusion) AddExclusion(log);

        var helperExe = Path.Combine(dir, Path.GetFileName(exe));
        Check(ServiceManager.Sc(exists ? ScConfigArgs(helperExe) : ScCreateArgs(helperExe)), L.T(exists ? "core.helper.sc_reconfigure" : "core.helper.sc_create"));
        ServiceManager.Sc(ScDescriptionArgs());
        ServiceManager.Sc(ScFailureArgs());
        log?.Report(L.T("core.helper.inst.start"));
        Check(ServiceManager.Sc(ScStartArgs()), L.T("core.helper.sc_start"));
        if (!ServiceManager.WaitFor(HelperProtocol.ServiceName, SvcState.Running, TimeSpan.FromSeconds(20)))
            throw new InvalidOperationException(L.T("core.helper.start_timeout"));
        log?.Report(L.T("core.helper.inst.done"));
    }

    /// <summary>
    /// Удаление: остановить и удалить службу, стереть папку помощника. Данные программы (state, результаты, журнал изменений) остаются —
    /// кроме <paramref name="purge"/> («Удалить DpiShell с этого ПК»): тогда стирается вся %ProgramData%\DpiShell (<see cref="AppPaths.Purge"/>).
    /// </summary>
    public void Uninstall(IProgress<string>? log = null, bool purge = false)
    {
        if (!Shell.IsAdmin()) throw new UnauthorizedAccessException(L.T("core.helper.need_admin_uninstall"));
        if (ServiceManager.Query(HelperProtocol.ServiceName) != SvcState.NotInstalled)
        {
            log?.Report(L.T("core.helper.inst.stop"));
            ServiceManager.Sc(ScStopArgs());
            ServiceManager.WaitFor(HelperProtocol.ServiceName, SvcState.Stopped, TimeSpan.FromSeconds(30));
            Check(ServiceManager.Sc(ScDeleteArgs()), L.T("core.helper.sc_delete"));
        }
        DeleteWhenReleased(HelperDir(paths));
        log?.Report(L.T("core.helper.inst.removed"));
        // Последним: журнал помощника лежит там же — после этого в него ничего не пишется. Своё исключение Защитника — снять до того (чужое остаётся).
        if (purge)
        {
            RemoveOwnExclusion(log);
            paths.Purge();
        }
    }

    /// <summary>Исключение для папки программы по согласию из окна; отказ Защитника установку не останавливает (окно покажет ручной путь).</summary>
    public void AddExclusion(IProgress<string>? log)
    {
        try
        {
            // Не внутри log?.Report(…): при log = null аргумент не вычислялся бы вовсе.
            var r = new AvExclusions(Defender, paths.Root, new ChangeJournal(paths.ChangesFile)).Add();
            log?.Report(r.Message);
        }
        catch (Exception e) when (AvExclusions.IsWmiError(e) || e is IOException) { log?.Report(e.Message); }
    }

    /// <summary>Снять исключение, если его добавил DpiShell (по журналу); ошибка Защитника удаление не останавливает.</summary>
    public void RemoveOwnExclusion(IProgress<string>? log)
    {
        try
        {
            foreach (var i in AvExclusionUndoer.UndoOwn(new ChangeJournal(paths.ChangesFile), Defender, paths.Root).Items) log?.Report($"{i.Title}: {i.Note}");
        }
        catch (Exception e) when (AvExclusions.IsWmiError(e) || e is IOException) { log?.Report(e.Message); }
    }

    /// <summary>
    /// Стереть папку помощника, дождавшись, пока её отпустят. Служба отвечает «остановлена» раньше, чем процесс выходит
    /// и отпускает свой exe (иначе обновление падает с «Access denied» на занятом DpiShell.exe); файл может
    /// держать и антивирус. Повтор до 20 с, потом — исходная ошибка.
    /// </summary>
    public static void DeleteWhenReleased(string dir, TimeSpan? timeout = null)
    {
        var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (true)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && DateTime.UtcNow < until)
            {
                Thread.Sleep(300);
            }
        }
    }

    /// <summary>
    /// ERROR_SERVICE_MARKED_FOR_DELETE: службу уже удаляют, но её держит открытой «Службы» (services.msc) или другой процесс.
    /// Процесс установки/удаления выходит с этим кодом — окно говорит, что делать, словами.
    /// </summary>
    public const int MarkedForDelete = 1072;

    static void Check((int Code, string Output) r, string what)
    {
        if (r.Code == MarkedForDelete)
            throw new ServiceMarkedForDeleteException(L.T("core.helper.sc_failed", new { what, code = r.Code, output = r.Output.Trim() }));
        if (r.Code != 0) throw new InvalidOperationException(L.T("core.helper.sc_failed", new { what, code = r.Code, output = r.Output.Trim() }));
    }
}

/// <summary>sc вернул 1072: служба помечена на удаление (<see cref="HelperInstaller.MarkedForDelete"/>).</summary>
public sealed class ServiceMarkedForDeleteException(string message) : InvalidOperationException(message);
