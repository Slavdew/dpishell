using System.Diagnostics;

namespace DpiShell.Core.Helper;

/// <summary>Часы Windows; в тестах — подмена (настоящий w32tm не запускается).</summary>
public interface ITimeSystem
{
    /// <summary>w32tm /resync; код выхода и вывод.</summary>
    (int Code, string Output) Resync();
}

public sealed class W32tmTime : ITimeSystem
{
    public (int Code, string Output) Resync()
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "w32tm.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("/resync");
        try
        {
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            if (!p.WaitForExit(20000)) { try { p.Kill(); } catch (InvalidOperationException) { } return (-1, o); }
            return (p.ExitCode, o);
        }
        catch (System.ComponentModel.Win32Exception) { return (-1, ""); }
    }
}

public sealed partial class ShellHelperBackend
{
    public ITimeSystem Time { get; init; } = new W32tmTime();

    public TimeSyncResult TimeSync()
    {
        var (code, output) = Time.Resync();
        return new(code == 0, code == 0 ? "" : output.Trim().Split('\n').FirstOrDefault()?.Trim() ?? "");
    }
}
