using System.Diagnostics;
using System.Management;
using DpiShell.Core.Localization;

namespace DpiShell.Core;

/// <summary>Что остановке чужих оптимизаций нужно от системы: службы, процессы. В тестах — подмена без sc и taskkill.</summary>
public interface IForeignSystem
{
    /// <summary>Найденные оптимизации (все, вместе с нашей — её отсеивает <see cref="ForeignStop"/>).</summary>
    IReadOnlyList<ZapretInstance> Find();
    SvcState Query(string service);
    /// <summary><c>sc stop</c>: код и вывод.</summary>
    (int Code, string Output) Stop(string service);
    bool WaitStopped(string service, TimeSpan timeout);
    /// <summary>PID процесса службы; 0 — процесса нет.</summary>
    int ServicePid(string service);
    /// <summary>Служба запускается вместе с Windows (тип запуска «автоматически»).</summary>
    bool AutoStart(string service);
    /// <summary>PID процессов с этим образом (полный путь).</summary>
    IReadOnlyList<int> ProcessesOf(string exe);
    /// <summary>Завершить процесс, если это тот же, что был при снимке (<see cref="Shell.IsSameProcess"/>). null — завершён, уже нет или не тот; иначе причина.</summary>
    string? Kill(int pid, string? exe, DateTime snapshotAt);
}

/// <summary>Итог «Остановить»: что остановлено, что нет и почему, что работает снова.</summary>
/// <param name="ComeBackOnReboot">Остановленные службы с автозапуском.</param>
/// <param name="Failed">«что: причина».</param>
/// <param name="StillRunning">После остановки снова найдено работающим.</param>
/// <param name="Hints">Как остановить вручную то, что не вышло.</param>
public sealed record ForeignStopOutcome(
    IReadOnlyList<string> Stopped, IReadOnlyList<string> ComeBackOnReboot,
    IReadOnlyList<string> Failed, IReadOnlyList<string> StillRunning, IReadOnlyList<string> Hints);

/// <summary>
/// «Остановить» похожие программы: службы останавливаются (не удаляются — чужие настройки не трогаем), процессы завершаются.
/// Служба не остановилась или остановилась, а её winws жив (Flowseal сам после <c>net stop</c> делает <c>taskkill</c>) —
/// завершается именно процесс этой службы: по PID службы и пути её образа; наш winws не трогается.
/// «Остановлено» — только то, что остановлено на деле.
/// </summary>
public static class ForeignStop
{
    public static readonly TimeSpan StopWait = TimeSpan.FromSeconds(10);
    static readonly TimeSpan AfterKillWait = TimeSpan.FromSeconds(5);

    /// <summary>Служба уже остановлена (sc 1062) — не ошибка.</summary>
    const int NotStarted = 1062;

    public static ForeignStopOutcome Run(IForeignSystem sys, string ourWinws)
    {
        var stopped = new List<string>(); var comeBack = new List<string>(); var failed = new List<string>(); var hints = new List<string>();
        var failedServices = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var failedPids = new HashSet<int>();
        bool Ours(string? exe) => exe is not null && exe.Equals(ourWinws, StringComparison.OrdinalIgnoreCase);
        void Fail(ZapretInstance z, string what, IEnumerable<string?> reasons)
        {
            failed.Add(L.T("core.shell.stop_failed_item", new { what, reason = string.Join("; ", reasons.Where(r => r is not null)) }));
            if (!hints.Contains(z.HowToStop)) hints.Add(z.HowToStop);
        }

        // Уже остановленная служба — не помеха и не «остановлено» (автозапуск окно показывает отдельной строкой).
        var found = sys.Find().Where(z => z.Owner != ZapretOwner.DpiShell && !Ours(z.ExePath) && z.Running).ToList();
        foreach (var z in found.Where(z => z.Kind == "service"))
        {
            var what = L.T("core.shell.stopped_service", new { owner = z.OwnerTitle, name = z.Name });
            var snapshotAt = DateTime.Now;   // процесс службы запущен не позже этого момента
            int pid = sys.ServicePid(z.Name);
            var state = sys.Query(z.Name);
            bool down = state is SvcState.Stopped or SvcState.NotInstalled;
            string? scError = null, timeout = null, killError = null;
            if (!down)
            {
                if (state != SvcState.StopPending)
                {
                    var (code, output) = sys.Stop(z.Name);
                    if (code != 0 && code != NotStarted) scError = L.T("core.shell.stop_reason_sc", new { code, output = OneLine(output) });
                }
                down = sys.WaitStopped(z.Name, StopWait);
                if (!down) timeout = L.T("core.shell.stop_reason_timeout", new { sec = (int)StopWait.TotalSeconds });
            }
            // Процесс службы — и когда служба не остановилась, и когда остановилась, а winws остался.
            var pids = new List<int>();
            if (pid > 0) pids.Add(pid);
            if (z.ExePath is { } exe && !Ours(exe)) pids.AddRange(sys.ProcessesOf(exe).Where(p => !pids.Contains(p)));
            foreach (var p in pids)
                if (sys.Kill(p, z.ExePath, snapshotAt) is { } err) killError ??= err;
            if (!down && pids.Count > 0 && killError is null) down = sys.WaitStopped(z.Name, AfterKillWait);

            if (down && killError is null)
            {
                stopped.Add(what);
                if (sys.AutoStart(z.Name)) comeBack.Add(z.Name);
            }
            else
            {
                failedServices.Add(z.Name);
                Fail(z, what, down ? [killError] : [scError, timeout, killError]);
            }
        }
        foreach (var z in found.Where(z => z.Kind == "process"))
        {
            if (!int.TryParse(z.Name.Replace("PID ", ""), out var pid)) continue;
            if (sys.Kill(pid, z.ExePath, DateTime.Now) is { } err)
            {
                failedPids.Add(pid);
                Fail(z, z.OwnerTitle, [err]);
            }
            else stopped.Add(z.OwnerTitle);
        }

        // Перепроверка: служба могла подняться снова (восстановление после сбоя), процесс — запуститься заново.
        var still = new List<string>();
        if (found.Count > 0)
            foreach (var z in sys.Find().Where(z => z.Owner != ZapretOwner.DpiShell && !Ours(z.ExePath)))
            {
                string? name = z.Kind == "service"
                    ? z.State is nameof(SvcState.Running) or nameof(SvcState.StartPending) && !failedServices.Contains(z.Name)
                        ? L.T("core.shell.stopped_service", new { owner = z.OwnerTitle, name = z.Name }) : null
                    : int.TryParse(z.Name.Replace("PID ", ""), out var pid) && failedPids.Contains(pid) ? null : z.OwnerTitle;
                if (name is null || still.Contains(name)) continue;
                still.Add(name);
                if (!hints.Contains(z.HowToStop)) hints.Add(z.HowToStop);
            }
        return new(stopped, comeBack, failed, still, hints);
    }

    /// <summary>Вывод sc одной строкой: «[SC] ControlService FAILED 1051: …» — переносы и пустые строки убраны, длинное обрезано.</summary>
    static string OneLine(string s)
    {
        var line = string.Join(' ', s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return line.Length > 200 ? line[..200] + "…" : line;
    }
}

/// <summary>Настоящая система: sc, реестр, WMI, завершение процесса с проверкой «тот же процесс».</summary>
public sealed class SystemForeign(AppPaths paths, Func<IReadOnlyList<Strategy>> catalog) : IForeignSystem
{
    public IReadOnlyList<ZapretInstance> Find() => ZapretDetector.Find(paths, catalog());
    public SvcState Query(string service) => ServiceManager.Query(service);
    public (int Code, string Output) Stop(string service) => ServiceManager.Sc("stop", service);
    public bool WaitStopped(string service, TimeSpan timeout) => ServiceManager.WaitFor(service, SvcState.Stopped, timeout);

    public int ServicePid(string service)
    {
        try
        {
            using var s = new ManagementObjectSearcher($"SELECT ProcessId FROM Win32_Service WHERE Name='{service.Replace("'", "")}'");
            foreach (var o in s.Get())
                using (o) return Convert.ToInt32(o["ProcessId"]);
        }
        catch (Exception e) when (e is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { }
        return 0;
    }

    public bool AutoStart(string service) => ServiceManager.IsAutoStart(service);

    public IReadOnlyList<int> ProcessesOf(string exe) =>
        [.. ZapretDetector.Processes(Path.GetFileName(exe)).Where(p => exe.Equals(p.Exe, StringComparison.OrdinalIgnoreCase)).Select(p => p.Pid)];

    public string? Kill(int pid, string? exe, DateTime snapshotAt)
    {
        var name = Path.GetFileName(exe) ?? "winws.exe";
        try
        {
            using var p = Process.GetProcessById(pid);
            // Между снимком и Kill номер мог достаться другому процессу (в т. ч. чужого пользователя) — тогда не трогаем.
            if (!Shell.IsSameProcess(p, exe, snapshotAt)) return null;
            p.Kill(entireProcessTree: true);
            return p.WaitForExit(5000) ? null : L.T("core.shell.stop_reason_alive", new { name });
        }
        catch (ArgumentException) { return null; }            // уже завершился
        catch (InvalidOperationException) { return null; }
        catch (System.ComponentModel.Win32Exception e) { return L.T("core.shell.stop_reason_kill", new { name, error = e.Message }); }
    }
}
