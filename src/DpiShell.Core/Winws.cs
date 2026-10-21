using DpiShell.Core.Localization;
using System.Diagnostics;
using System.Text;

namespace DpiShell.Core;

/// <summary>winws как временный процесс — для подбора и «Проверить сейчас». Постоянная работа — через <see cref="ServiceManager"/>.</summary>
public sealed class WinwsProcess : IDisposable
{
    readonly Process _p;
    readonly StringBuilder _out = new();

    WinwsProcess(Process p)
    {
        _p = p;
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (_out) _out.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (_out) _out.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
    }

    public bool Alive => !_p.HasExited;
    public int Pid => _p.Id;
    public string Output { get { lock (_out) return _out.ToString(); } }

    public static WinwsProcess Start(AppPaths paths, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(paths.Winws)
        {
            WorkingDirectory = paths.Core,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi) ?? throw new InvalidOperationException(L.T("core.winws.not_started"));
        // Любой наш winws (подбор, режим без службы) завершается вместе с программой — даже при её аварийном выходе.
        try { Job.Add(p); }
        catch (InvalidOperationException) { if (!p.HasExited) p.Kill(); p.Dispose(); throw; }
        return new WinwsProcess(p);
    }

    static readonly Lazy<JobObject> LazyJob = new(() => new JobObject());
    static JobObject Job => LazyJob.Value;

    /// <summary>Ждём, что winws открыл WinDivert и не упал: процесс жив заданное время.</summary>
    public async Task<bool> WaitReadyAsync(TimeSpan settle, CancellationToken ct = default)
    {
        var until = DateTime.UtcNow + settle;
        while (DateTime.UtcNow < until)
        {
            if (_p.HasExited) return false;
            await Task.Delay(100, ct);
        }
        return !_p.HasExited;
    }

    public void Stop()
    {
        try { if (!_p.HasExited) { _p.Kill(entireProcessTree: true); _p.WaitForExit(5000); } }
        catch (InvalidOperationException) { }
    }

    public void Dispose() { Stop(); _p.Dispose(); }

    /// <summary>Проверка аргументов без перехвата трафика: winws --dry-run.</summary>
    public static async Task<(bool Ok, string Output)> DryRunAsync(AppPaths paths, IReadOnlyList<string> args, CancellationToken ct = default)
    {
        using var p = Start(paths, [.. args, "--dry-run"]);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        try { await p._p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { p.Stop(); return (false, L.T("core.winws.dry_run_timeout")); }
        p._p.WaitForExit(); // дочитать вывод
        return (p._p.ExitCode == 0, p.Output);
    }

    /// <summary>Все запущенные winws.exe (свои и чужие).</summary>
    public static IReadOnlyList<(int Pid, string? Path)> FindRunning(string name = "winws")
    {
        var r = new List<(int, string?)>();
        foreach (var p in Process.GetProcessesByName(name))
        {
            string? path = null;
            try { path = p.MainModule?.FileName; } catch { }
            r.Add((p.Id, path));
            p.Dispose();
        }
        return r;
    }
}
