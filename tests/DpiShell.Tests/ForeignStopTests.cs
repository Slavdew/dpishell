using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.Tests;

/// <summary>
/// «Остановить» похожие программы (BUG-stop-flowseal): итог честный, процесс службы завершается, наш winws не трогается.
/// Система подменена — ни sc, ни taskkill, ни чужих служб.
/// </summary>
public sealed class ForeignStopTests
{
    const string Ours = @"C:\ProgramData\DpiShell\core\winws.exe";
    const string FlowsealExe = @"C:\zapret-discord-youtube\bin\winws.exe";

    /// <summary>Подставная система: одна служба zapret Flowseal, её процесс, по желанию — отдельные процессы.</summary>
    sealed class FakeSystem : IForeignSystem
    {
        public SvcState State = SvcState.Running;
        public (int, string) StopReply = (0, "");
        /// <summary>sc stop действительно останавливает службу.</summary>
        public bool StopWorks = true;
        /// <summary>После sc stop процесс службы остаётся жив (служба уже «остановлена»).</summary>
        public bool ProcessSurvivesStop;
        public int Pid = 4242;
        public bool Alive = true;
        public bool Auto = true;
        public string? KillError;
        public bool RestartsAfter;
        public readonly List<(int Pid, string? Exe)> Killed = [];
        public readonly List<string> ProcessQueries = [];
        public readonly List<ZapretInstance> Processes = [];
        int _finds;

        public IReadOnlyList<ZapretInstance> Find()
        {
            _finds++;
            var state = _finds > 1 && RestartsAfter ? SvcState.Running : State;
            return [
                new("service", "zapret", ZapretOwner.Flowseal, FlowsealExe, $"\"{FlowsealExe}\" --wf-tcp=443", null, state.ToString()),
                new("service", ServiceManager.Name, ZapretOwner.DpiShell, Ours, $"\"{Ours}\"", null, "Running"),
                .. Processes,
            ];
        }
        public SvcState Query(string service) => service == "zapret" ? State : SvcState.Running;
        public (int Code, string Output) Stop(string service)
        {
            Assert.Equal("zapret", service);
            if (StopWorks) { State = SvcState.Stopped; Alive = ProcessSurvivesStop; }
            return StopReply;
        }
        public bool WaitStopped(string service, TimeSpan timeout) => State == SvcState.Stopped;
        public int ServicePid(string service) => service == "zapret" && State != SvcState.Stopped ? Pid : 0;
        public bool AutoStart(string service) => Auto;
        public IReadOnlyList<int> ProcessesOf(string exe)
        {
            ProcessQueries.Add(exe);
            return exe == FlowsealExe && Alive ? [Pid] : [];
        }
        public string? Kill(int pid, string? exe, DateTime snapshotAt)
        {
            Assert.NotEqual(Ours, exe);
            Killed.Add((pid, exe));
            if (KillError is not null) return KillError;
            if (pid == Pid) { Alive = false; State = SvcState.Stopped; }
            Processes.RemoveAll(p => p.Name == $"PID {pid}");
            return null;
        }
    }

    static ForeignStopOutcome Run(FakeSystem s) => ForeignStop.Run(s, Ours);

    [Fact]
    public void Stopped_ServiceAndProcessGone_SaysStopped_AndAutostart()
    {
        var s = new FakeSystem();
        var r = Run(s);
        Assert.Equal(["zapret от Flowseal (служба zapret)"], r.Stopped);
        Assert.Equal(["zapret"], r.ComeBackOnReboot);
        Assert.Empty(r.Failed);
        Assert.Empty(r.StillRunning);
        Assert.Empty(r.Hints);
        Assert.DoesNotContain(Ours, s.ProcessQueries);   // наш winws даже не ищется
    }

    [Fact]
    public void NotAutostart_NoComeBack()
    {
        var r = Run(new FakeSystem { Auto = false });
        Assert.Single(r.Stopped);
        Assert.Empty(r.ComeBackOnReboot);
    }

    [Fact]
    public void ServiceStopped_ButWinwsAlive_KillsThatProcess()
    {
        var s = new FakeSystem { ProcessSurvivesStop = true };
        var r = Run(s);
        Assert.Equal([(4242, FlowsealExe)], s.Killed);   // по пути службы, не все winws подряд
        Assert.False(s.Alive);
        Assert.Single(r.Stopped);
        Assert.Empty(r.Failed);
    }

    [Fact]
    public void StopSucceeded_ButServiceStillRunning_KillsServicePid_ThenStopped()
    {
        var s = new FakeSystem { StopWorks = false };
        var r = Run(s);
        Assert.Contains((4242, FlowsealExe), s.Killed);
        Assert.Single(r.Stopped);
        Assert.Empty(r.Failed);
    }

    [Fact]
    public void StopFailed_AndKillDenied_SaysNotStopped_WithReasons_AndHowTo()
    {
        var s = new FakeSystem
        {
            StopWorks = false, KillError = "процесс winws.exe не завершён: Отказано в доступе",
            StopReply = (1051, "[SC] ControlService FAILED 1051:\r\n\r\nA stop control has been sent to a service that other running services are dependent on.\r\n"),
        };
        var r = Run(s);
        Assert.Empty(r.Stopped);
        Assert.Empty(r.ComeBackOnReboot);
        var f = Assert.Single(r.Failed);
        Assert.StartsWith("zapret от Flowseal (служба zapret): sc stop — код 1051: [SC] ControlService FAILED 1051: A stop control", f);
        Assert.Contains("не остановлена за 10 с", f);
        Assert.Contains("Отказано в доступе", f);
        Assert.Equal(["Запустите service.bat из папки Flowseal от администратора → «Remove Services»."], r.Hints);
        Assert.Empty(r.StillRunning);   // уже названо в «не остановлено»
    }

    [Fact]
    public void AlreadyStopped_Code1062_NotAnError()
    {
        var s = new FakeSystem { StopReply = (1062, "[SC] ControlService FAILED 1062") };
        var r = Run(s);
        Assert.Single(r.Stopped);
        Assert.Empty(r.Failed);
    }

    [Fact]
    public void ServiceStartsAgain_SaidAsRunningAgain()
    {
        var r = Run(new FakeSystem { RestartsAfter = true });
        Assert.Single(r.Stopped);
        Assert.Equal(["zapret от Flowseal (служба zapret)"], r.StillRunning);
        Assert.Single(r.Hints);
    }

    [Fact]
    public void Process_KillDenied_Failed_OurProcessUntouched()
    {
        var s = new FakeSystem { State = SvcState.Stopped, Alive = false };
        s.Processes.Add(new("process", "PID 77", ZapretOwner.Flowseal, FlowsealExe, null, null, null));
        s.Processes.Add(new("process", "PID 88", ZapretOwner.OtherZapret, Ours, null, null, null));   // путь нашего — не трогаем, даже если владелец не узнан
        s.KillError = "процесс winws.exe не завершён: Отказано в доступе";
        var r = Run(s);
        Assert.Equal([(77, FlowsealExe)], s.Killed);
        Assert.Equal(["zapret от Flowseal: процесс winws.exe не завершён: Отказано в доступе"], r.Failed);
        Assert.Empty(r.Stopped);   // служба уже стояла — не «остановлено»
        Assert.Empty(r.StillRunning);
    }

    [Fact]
    public void AlreadyStoppedService_NotTouched_NotReported()
    {
        var s = new FakeSystem { State = SvcState.Stopped, Alive = false };
        var r = Run(s);
        Assert.Empty(r.Stopped);
        Assert.Empty(r.ComeBackOnReboot);
        Assert.Empty(r.Failed);
        Assert.Empty(s.Killed);
        Assert.Empty(s.ProcessQueries);
        Assert.Equal("Нечего останавливать.", MainViewModel.StopForeignText(new(r.Stopped, r.ComeBackOnReboot, r.Failed, r.StillRunning, r.Hints)));
    }

    // ---------- что считать помехой (живая проверка 23.10: после «Остановить» карточка оставалась) ----------

    static ZapretInstance Svc(string state, bool auto = false, ZapretOwner owner = ZapretOwner.Flowseal) =>
        new("service", "zapret", owner, FlowsealExe, null, null, state) { AutoStart = auto };

    [Fact]
    public void Conflicts_RunningAndStarting_Interfere()
    {
        foreach (var st in new[] { SvcState.Running, SvcState.StartPending, SvcState.StopPending, SvcState.Unknown })
        {
            var c = Assert.Single(Conflicts.From([Svc(st.ToString())]));
            Assert.False(c.Dormant);
            Assert.Equal("Запустите service.bat из папки Flowseal от администратора → «Remove Services».", c.DetailNow);
        }
        var p = Assert.Single(Conflicts.From([new ZapretInstance("process", "PID 7", ZapretOwner.GoodbyeDpi, null, null, null, null)]));
        Assert.False(p.Dormant);
    }

    [Fact]
    public void Conflicts_StoppedWithAutostart_Dormant_WithoutAutostart_Hidden()
    {
        var c = Assert.Single(Conflicts.From([Svc("Stopped", auto: true)]));
        Assert.True(c.Dormant);
        Assert.Equal("zapret от Flowseal (служба zapret)", c.NameNow);
        Assert.Empty(Conflicts.From([Svc("Stopped", auto: false)]));
        var other = Assert.Single(Conflicts.From([Svc("Stopped", auto: true, owner: ZapretOwner.OtherZapret)]));
        Assert.Equal("Убрать насовсем: удалить службу «zapret» программой, которая её поставила.", other.DetailNow);
        // Наше — никогда
        Assert.Empty(Conflicts.From([new ZapretInstance("service", ServiceManager.Name, ZapretOwner.DpiShell, Ours, null, null, "Running")]));
    }

    [Fact]
    public void HelperStatus_SplitsDormant_AndRoundTrips()
    {
        using var tmp = new TempDir();
        var shell = new Shell(new AppPaths(tmp.Sub("root"), Repo.Data));
        var running = Conflicts.From([new ZapretInstance("process", "PID 7", ZapretOwner.GoodbyeDpi, null, null, null, null)]);
        var dormant = Conflicts.From([Svc("Stopped", auto: true)]);
        var backend = new ShellHelperBackend(shell)
        {
            InspectService = () => new ServiceInfo(SvcState.NotInstalled, null, null, false), IsActive = () => false,
            FindForeign = () => [.. running, .. dormant],
        };
        var st = backend.Status();
        Assert.Equal("GoodbyeDPI", Assert.Single(st.Foreign).Name);
        var d = Assert.Single(st.ForeignDormant!);
        Assert.True(d.Dormant);
        var back = System.Text.Json.JsonSerializer.Deserialize<HelperStatus>(System.Text.Json.JsonSerializer.Serialize(st, HelperProtocol.Json), HelperProtocol.Json)!;
        Assert.True(Assert.Single(back.ForeignDormant!).Dormant);
        Assert.False(Assert.Single(back.Foreign).Dormant);
    }

    // ---------- окно: текст итога ----------

    [Fact]
    public void Text_Failure_HasNoStopped_AndHowTo()
    {
        var t = MainViewModel.StopForeignText(new([], [], ["zapret от Flowseal (служба zapret): не остановлена за 10 с"], [], ["Запустите service.bat → «Remove Services»."]));
        Assert.Equal("Не остановлено: zapret от Flowseal (служба zapret): не остановлена за 10 с. Запустите service.bat → «Remove Services».", t);
        Assert.DoesNotContain("Остановлено", t);
    }

    [Fact]
    public void Text_OldHelper_WithoutNewFields_AsBefore()
    {
        Assert.Equal("Остановлено: GoodbyeDPI.", MainViewModel.StopForeignText(new(["GoodbyeDPI"], [])));
        Assert.Equal("Нечего останавливать.", MainViewModel.StopForeignText(new([], [])));
    }

    [Fact]
    public void Result_RoundTrips_AndOldReplyHasNulls()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new StopForeignResult(["a"], ["zapret"], ["b: c"], ["d"], ["e"]), HelperProtocol.Json);
        var back = System.Text.Json.JsonSerializer.Deserialize<StopForeignResult>(json, HelperProtocol.Json)!;
        Assert.Equal(["b: c"], back.Failed);
        Assert.Equal(["e"], back.Hints);
        var old = System.Text.Json.JsonSerializer.Deserialize<StopForeignResult>("""{"stopped":["a"],"comeBackOnReboot":[]}""", HelperProtocol.Json)!;
        Assert.Null(old.Failed);
    }
}
