using DpiShell.App;
using Xunit;

namespace DpiShell.Tests;

/// <summary>Выход из окна завершает процесс при любом сбое уборки (раньше процесс оставался висеть без окна и значка).</summary>
public class AppExitTests
{
    [Fact]
    public async Task RouterDisposeThrows_StillShutsDown_AndLogs()
    {
        var log = new List<Exception>();
        int shutdowns = 0, after = 0;
        var exit = new AppExit
        {
            Log = log.Add,
            Shutdown = () => shutdowns++,
            Kill = _ => { },
            Grace = TimeSpan.FromHours(1),
        };

        await exit.RunAsync(() => Task.CompletedTask,
            () => { },
            () => throw new ObjectDisposedException("router"),   // _vm.Router.Dispose()
            () => after++);

        Assert.Equal(1, shutdowns);
        Assert.Equal(1, after);                                    // следующие шаги уборки — всё равно
        Assert.IsType<ObjectDisposedException>(Assert.Single(log));
    }

    [Fact]
    public async Task PrepareThrows_StillShutsDown()
    {
        int shutdowns = 0;
        var exit = new AppExit { Shutdown = () => shutdowns++, Kill = _ => { }, Grace = TimeSpan.FromHours(1) };

        await exit.RunAsync(() => throw new InvalidOperationException("помощник не ответил"));

        Assert.Equal(1, shutdowns);
    }

    [Fact]
    public async Task ShutdownThrows_KillsAtOnce()
    {
        var killed = new List<int>();
        var exit = new AppExit { Shutdown = () => throw new InvalidOperationException(), Kill = c => { lock (killed) killed.Add(c); }, Grace = TimeSpan.FromHours(1) };

        await exit.RunAsync(() => Task.CompletedTask);

        Assert.Equal([1], killed);
    }

    [Fact]
    public async Task ProcessStillAlive_KilledAfterGrace()
    {
        var killed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exit = new AppExit { Shutdown = () => { }, Kill = c => killed.TrySetResult(c), Grace = TimeSpan.FromMilliseconds(50) };

        await exit.RunAsync(() => Task.CompletedTask);

        Assert.Equal(0, await killed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
