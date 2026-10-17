using DpiShell.App;
using Xunit;

namespace DpiShell.Tests;

/// <summary>Повторный запуск: показать работающую копию; не отвечает — предложить её завершить (без настоящих процессов).</summary>
public class SingleInstanceTests
{
    sealed class World
    {
        public bool Free, Responds, Agree, KillWorks;
        public int Signals, Kills;
        public string? Asked, Told;
        public TimeSpan Waited;

        public SingleInstance Gate() => new()
        {
            Acquire = () => Free,
            SignalShow = () => Signals++,
            WaitShown = t => { Waited = t; return Responds; },
            Ask = q => { Asked = q; return Agree; },
            KillOld = () => { Kills++; if (KillWorks) Free = true; return KillWorks; },
            Tell = s => Told = s,
        };
    }

    [Fact]
    public void First_Runs_WithoutSignal()
    {
        var w = new World { Free = true };
        Assert.True(w.Gate().ShouldRun());
        Assert.Equal(0, w.Signals);
    }

    [Fact]
    public void Running_Responds_ShowsItAndExits()
    {
        var w = new World { Responds = true };
        Assert.False(w.Gate().ShouldRun());
        Assert.Equal(1, w.Signals);
        Assert.Equal(TimeSpan.FromSeconds(3), w.Waited);
        Assert.Null(w.Asked);
        Assert.Equal(0, w.Kills);
    }

    [Fact]
    public void NotResponding_Agree_KillsAndRuns()
    {
        var w = new World { Agree = true, KillWorks = true };
        Assert.True(w.Gate().ShouldRun());
        Assert.Equal(SingleInstance.NotRespondingText, w.Asked);
        Assert.Equal(1, w.Kills);
        Assert.Null(w.Told);
    }

    [Fact]
    public void NotResponding_Decline_ExitsWithoutKill()
    {
        var w = new World();
        Assert.False(w.Gate().ShouldRun());
        Assert.NotNull(w.Asked);
        Assert.Equal(0, w.Kills);
    }

    [Fact]
    public void NotResponding_KillFails_Tells()
    {
        var w = new World { Agree = true };
        Assert.False(w.Gate().ShouldRun());
        Assert.Equal(SingleInstance.KillFailedText, w.Told);
    }
}
