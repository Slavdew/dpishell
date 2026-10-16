using System.Diagnostics;
using DpiShell.Core;

namespace DpiShell.Tests;

public sealed class JobObjectTests
{
    /// <summary>Режим без службы: winws не переживает программу. Вместо winws — безобидный долгий ping на себя.</summary>
    [Fact]
    public void ProcessInJob_DiesWhenJobClosed()
    {
        using var p = Process.Start(new ProcessStartInfo("ping", "-n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        var job = new JobObject();
        job.Add(p);
        Assert.False(p.HasExited);
        job.Dispose();   // так происходит при выходе программы, в том числе аварийном
        Assert.True(p.WaitForExit(5000), "процесс должен завершиться вместе с заданием");
    }
}
