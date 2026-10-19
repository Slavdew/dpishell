using System.Net;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.Tests;

/// <summary>Источник записи в тестах: наблюдения подаёт тест.</summary>
public sealed class FakeFlowSource(string name = "фейк", bool udp = true, string? failWith = null) : IFlowSource
{
    Action<FlowObservation>? _sink;
    public string Name => name;
    public bool CoversUdp => udp;
    public bool Stopped { get; private set; }
    public bool Disposed { get; private set; }

    public string? Start(Action<FlowObservation> onFlow)
    {
        if (failWith is not null) return failWith;
        _sink = onFlow;
        return null;
    }

    public void Push(string proto, string ip, int port, int pid, FlowHint hint = FlowHint.Unknown) =>
        _sink!(new(proto, IPAddress.Parse(ip), port, pid, hint));

    public void Stop() => Stopped = true;
    public void Dispose() => Disposed = true;
}

public sealed class FakeProcesses(params ProcInfo[] procs) : IProcessCatalog
{
    public List<ProcInfo> List { get; } = [.. procs];
    public IReadOnlyList<ProcInfo> Snapshot() => [.. List];
}

public sealed class RecordingTests
{
    sealed class Clock { public DateTime Now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc); public DateTime Get() => Now; }

    static ProcInfo P(int pid, int parent, string name) => new(pid, parent, name);

    [Fact]
    public void NewProcesses_BaselineIsBackground_NewOnesAreFresh()
    {
        var clock = new Clock();
        var procs = new FakeProcesses(P(4, 0, "System"), P(1000, 4, "chrome.exe"));
        var src = new FakeFlowSource();
        using var rec = new ConnectionRecorder(RecordingMode.NewProcesses, 0, [src], procs, clock.Get);
        rec.Start();

        src.Push("tcp", "198.51.100.1", 443, 1000);                     // старый процесс: фон
        procs.List.Add(P(2000, 1000, "game.exe"));                       // запущен после «Начать»
        clock.Now += TimeSpan.FromSeconds(3);
        src.Push("udp", "203.0.113.50", 27015, 2000);
        src.Push("udp", "203.0.113.50", 27015, 2000);

        var r = rec.Result();
        Assert.Equal("new", r.Mode);
        Assert.Equal(2, r.Flows.Count);
        var bg = r.Flows.Single(f => f.Pid == 1000);
        Assert.False(bg.Fresh);
        Assert.Equal("chrome.exe", bg.Process);
        var game = r.Flows.Single(f => f.Pid == 2000);
        Assert.True(game.Fresh);
        Assert.Equal(2, game.Count);
        Assert.Equal(3.0, game.FirstSeenSec);
        Assert.True(r.UdpRecorded);
    }

    [Fact]
    public void ProcessMode_RecordsOnlyTreeOfChosenProcess()
    {
        var procs = new FakeProcesses(P(10, 4, "launcher.exe"), P(11, 10, "game.exe"), P(12, 11, "voice.exe"), P(99, 4, "chrome.exe"));
        var src = new FakeFlowSource();
        using var rec = new ConnectionRecorder(RecordingMode.Process, 10, [src], procs);
        rec.Start();
        src.Push("tcp", "198.51.100.1", 443, 10);
        src.Push("udp", "203.0.113.5", 3478, 12);     // внук
        src.Push("tcp", "198.51.100.2", 443, 99);     // чужой
        var r = rec.Result();
        Assert.Equal(["launcher.exe", "voice.exe"], r.Flows.Select(f => f.Process).Order());
        Assert.All(r.Flows, f => Assert.True(f.Fresh));
    }

    [Fact]
    public void Tcp_ConnectingForLong_IsNoReply_Established_IsWorks()
    {
        var clock = new Clock();
        var procs = new FakeProcesses();
        var src = new FakeFlowSource(udp: false);
        using var rec = new ConnectionRecorder(RecordingMode.NewProcesses, 0, [src], procs, clock.Get);
        rec.Start();
        procs.List.Add(P(5, 4, "game.exe"));
        src.Push("tcp", "198.51.100.1", 443, 5, FlowHint.Connecting);
        src.Push("tcp", "198.51.100.2", 443, 5, FlowHint.Connecting);
        src.Push("tcp", "198.51.100.3", 443, 5, FlowHint.Connecting);
        clock.Now += TimeSpan.FromSeconds(5);
        src.Push("tcp", "198.51.100.1", 443, 5, FlowHint.Connecting);   // всё ещё без ответа
        src.Push("tcp", "198.51.100.2", 443, 5, FlowHint.Established);  // дождался
        var r = rec.Result();
        string State(string ip) => r.Flows.Single(f => f.Address == ip).State;
        Assert.Equal("noreply", State("198.51.100.1"));
        Assert.Equal("works", State("198.51.100.2"));
        Assert.Equal("", State("198.51.100.3"));   // одна попытка — рано судить
        Assert.False(r.UdpRecorded);
        Assert.Contains(r.Notes, n => n.Contains("UDP не записывается"));
    }

    [Fact]
    public void Recording_StopsByItself_AtLimit_AndKeepsWhatWasRecorded()
    {
        var clock = new Clock();
        var procs = new FakeProcesses();
        var src = new FakeFlowSource();
        using var rec = new ConnectionRecorder(RecordingMode.NewProcesses, 0, [src], procs, clock.Get);
        rec.Start();
        procs.List.Add(P(5, 4, "game.exe"));
        src.Push("udp", "203.0.113.50", 27015, 5);

        clock.Now += ConnectionRecorder.MaxDuration - TimeSpan.FromSeconds(1);
        rec.Tick();
        Assert.True(rec.Status().Running);
        clock.Now += TimeSpan.FromSeconds(2);
        rec.Tick();
        var st = rec.Status();
        Assert.False(st.Running);
        Assert.True(src.Stopped);
        var r = rec.Result();
        Assert.True(r.TimedOut);
        Assert.False(r.Running);
        Assert.Single(r.Flows);
        // После остановки новые наблюдения не принимаются.
        src.Push("udp", "203.0.113.51", 27016, 5);
        Assert.Single(rec.Result().Flows);
        Assert.Equal(300, ConnectionRecorder.MaxDuration.TotalSeconds);
    }

    [Fact]
    public void Stop_StopsSources_ResultStays()
    {
        var src = new FakeFlowSource();
        var procs = new FakeProcesses();
        var rec = new ConnectionRecorder(RecordingMode.NewProcesses, 0, [src], procs);
        rec.Start();
        procs.List.Add(P(5, 4, "game.exe"));
        src.Push("udp", "203.0.113.50", 27015, 5);
        rec.Stop();
        Assert.True(src.Stopped);
        Assert.Single(rec.Result().Flows);
        rec.Dispose();
        Assert.True(src.Disposed);
    }

    [Fact]
    public void FlowLimit_Truncates()
    {
        var procs = new FakeProcesses();
        var src = new FakeFlowSource();
        using var rec = new ConnectionRecorder(RecordingMode.NewProcesses, 0, [src], procs);
        rec.Start();
        procs.List.Add(P(5, 4, "game.exe"));
        for (int i = 0; i < ConnectionRecorder.MaxFlows + 10; i++)
            src.Push("udp", $"203.{i / 65536}.{i / 256 % 256}.{i % 256}", 30000, 5);
        var r = rec.Result();
        Assert.Equal(ConnectionRecorder.MaxFlows, r.Flows.Count);
        Assert.True(r.Truncated);
    }

    [Fact]
    public void OneSourceFails_OtherWorks_NoteSaysWhy()
    {
        var driver = new FakeFlowSource("драйвер", udp: true, failWith: "нет прав на драйвер перехвата");
        var table = new FakeFlowSource("таблица", udp: false);
        using var rec = new ConnectionRecorder(RecordingMode.NewProcesses, 0, [table, driver], new FakeProcesses());
        rec.Start();
        var r = rec.Result();
        Assert.False(r.UdpRecorded);
        Assert.Contains(r.Notes, n => n.Contains("драйвер: нет прав на драйвер перехвата"));
        Assert.Contains(r.Notes, n => n.Contains("UDP не записывается"));
    }

    [Fact]
    public void AllSourcesFail_StartThrows_WithReason()
    {
        var rec = new ConnectionRecorder(RecordingMode.NewProcesses, 0, [new FakeFlowSource("драйвер", failWith: "не открылся")], new FakeProcesses());
        var e = Assert.Throws<InvalidOperationException>(rec.Start);
        Assert.Contains("не открылся", e.Message);
    }

    [Fact]
    public void Status_CountsFlowsAndProcesses()
    {
        var procs = new FakeProcesses();
        var src = new FakeFlowSource();
        using var rec = new ConnectionRecorder(RecordingMode.NewProcesses, 0, [src], procs);
        rec.Start();
        procs.List.AddRange([P(5, 4, "a.exe"), P(6, 4, "b.exe")]);
        src.Push("udp", "203.0.113.1", 1, 5);
        src.Push("udp", "203.0.113.2", 1, 6);
        src.Push("udp", "203.0.113.3", 1, 6);
        var st = rec.Status();
        Assert.Equal((true, true, 3, 2), (st.Active, st.Running, st.Flows, st.Processes));
    }
}
