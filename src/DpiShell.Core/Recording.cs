using System.Net;
using System.Net.Sockets;
using DpiShell.Core.Localization;

namespace DpiShell.Core;

// Запись подключений программы на этом ПК (ТЗ 13, docs/dev/architecture.md «Подключения программ»).
// Помощник (SYSTEM) собирает «процесс → удалённый адрес:порт/протокол» ограниченное время и лёгкими средствами:
//   · WinDivert, слой FLOW (только чтение): событие на каждый новый поток TCP и UDP, с PID и удалённым адресом. Пакеты не читаются;
//   · таблица TCP Windows раз в секунду: состояние «подключается/установлено» (для признака «нет ответа») и запись без драйвера.
// Таблица UDP (GetExtendedUdpTable) удалённых адресов не содержит — поэтому UDP без драйвера не записывается, и об этом сказано.

/// <summary>Что записывать: процессы, запущенные после «Начать» (игра запускается сейчас), или один процесс с дочерними.</summary>
public enum RecordingMode { NewProcesses, Process }

/// <summary>Одно наблюдение источника: поток к удалённому адресу от процесса.</summary>
public readonly record struct FlowObservation(string Proto, IPAddress Remote, int Port, int Pid, FlowHint Hint);

/// <summary>Что источник знает о состоянии потока. Unknown — только факт появления потока (WinDivert FLOW).</summary>
public enum FlowHint { Unknown, Connecting, Established }

/// <summary>Источник наблюдений. Start открывает его и сразу возвращается; фоновая работа до Stop. Ошибка открытия — текст, а не исключение.</summary>
public interface IFlowSource : IDisposable
{
    string Name { get; }
    bool CoversUdp { get; }
    /// <summary>Открыть источник. Null — работает; текст — не открылся (причина для человека).</summary>
    string? Start(Action<FlowObservation> onFlow);
    void Stop();
}

public sealed record ProcInfo(int Pid, int ParentPid, string Name);

/// <summary>Список процессов (Toolhelp в службе; в тестах — подмена).</summary>
public interface IProcessCatalog
{
    IReadOnlyList<ProcInfo> Snapshot();
}

/// <summary>Запись одного подключения. State: "" — неизвестно, "works" — было установлено, "noreply" — только попытки без ответа.</summary>
public sealed record RecordedFlow(string Proto, string Address, int Port, int Pid, string Process, bool Fresh, int Count, double FirstSeenSec, string State);

/// <param name="Mode">"new" — процессы, запущенные после «Начать»; "process" — выбранный процесс с дочерними.</param>
/// <param name="Running">Запись идёт (не остановлена ни человеком, ни сроком).</param>
/// <param name="UdpRecorded">UDP записан: драйвер перехвата открылся. Иначе только TCP — таблица Windows не знает удалённых адресов UDP.</param>
public sealed record RecordingResult(string Mode, bool Running, double Seconds, bool UdpRecorded, bool TimedOut, bool Truncated,
    IReadOnlyList<string> Notes, IReadOnlyList<RecordedFlow> Flows);

public sealed record RecordingStatus(bool Active, bool Running, double Seconds, int Flows, int Processes, bool UdpRecorded, IReadOnlyList<string> Notes);

/// <summary>Запись подключений. Потокобезопасна: источники зовут <see cref="Feed"/> из своих потоков, помощник читает итог.</summary>
public sealed class ConnectionRecorder : IDisposable
{
    /// <summary>Крайний срок записи: по нему она останавливается сама (решение 14: не держать перехват долго).</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(5);
    public const int MaxFlows = 4000;
    /// <summary>Сколько секунд попытка TCP без ответа должна длиться, чтобы считаться «нет ответа»: короткий SYN_SENT — обычное дело.</summary>
    public const double NoReplyAfterSec = 4;

    readonly object _lock = new();
    readonly IReadOnlyList<IFlowSource> _sources;
    readonly IProcessCatalog _procs;
    readonly Func<DateTime> _now;
    readonly Dictionary<(string, string, int, int), Acc> _flows = [];
    readonly HashSet<int> _baseline = [];
    readonly HashSet<int> _roots = [];
    readonly HashSet<int> _missed = [];   // PID, которых не было в снимке: список перечитывался ради них один раз
    readonly List<string> _notes = [];
    Dictionary<int, ProcInfo> _snap = [];
    DateTime _snapAt;
    DateTime _started, _stopped;
    bool _running, _udp, _timedOut, _truncated, _disposed;

    sealed class Acc
    {
        public string Process = "";
        public bool Fresh;
        public int Count;
        public double First;
        public double? ConnectingSince;
        public bool Established;
        public double ConnectingLast;
    }

    public RecordingMode Mode { get; }

    public ConnectionRecorder(RecordingMode mode, int rootPid, IReadOnlyList<IFlowSource> sources, IProcessCatalog procs, Func<DateTime>? now = null)
    {
        Mode = mode;
        _sources = sources;
        _procs = procs;
        _now = now ?? (() => DateTime.UtcNow);
        if (mode == RecordingMode.Process) _roots.Add(rootPid);
    }

    /// <summary>Начать. Источники открываются здесь; ни один не открылся — исключение с причиной.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_running || _disposed) throw new InvalidOperationException("Запись уже идёт.");
            _started = _now();
            RefreshSnapshot(force: true);
            if (Mode == RecordingMode.NewProcesses) foreach (var p in _snap.Keys) _baseline.Add(p);
        }
        var errors = new List<string>();
        int opened = 0;
        foreach (var s in _sources)
        {
            var err = s.Start(Feed);
            if (err is null) { opened++; if (s.CoversUdp) _udp = true; }
            else errors.Add($"{s.Name}: {err}");
        }
        lock (_lock)
        {
            _notes.AddRange(errors);
            if (opened == 0) { Dispose(); throw new InvalidOperationException("Запись не началась. " + string.Join(" ", errors)); }
            if (!_udp) _notes.Add(L.T("apps.core.note.udp_no_driver"));
            _running = true;
        }
    }

    /// <summary>Раз в секунду: срок вышел — источники останавливаются, итог остаётся до <see cref="Dispose"/>.</summary>
    public void Tick()
    {
        bool stop;
        lock (_lock)
        {
            stop = _running && _now() - _started >= MaxDuration;
            if (stop) { _timedOut = true; _running = false; _stopped = _now(); }
        }
        if (stop) StopSources();
    }

    /// <summary>«Готово»: остановить источники, итог остаётся доступным.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (!_running) return;
            _running = false;
            _stopped = _now();
        }
        StopSources();
    }

    void StopSources()
    {
        foreach (var s in _sources) { try { s.Stop(); } catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException or System.ComponentModel.Win32Exception) { } }
    }

    void RefreshSnapshot(bool force)
    {
        var now = _now();
        if (!force && (now - _snapAt).TotalMilliseconds < 800) return;
        try { _snap = _procs.Snapshot().ToDictionary(p => p.Pid); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        _snapAt = now;
    }

    /// <summary>Принадлежит ли процесс записи: для Process — корень или его потомок; для NewProcesses — не было в начале.</summary>
    bool InScope(int pid)
    {
        if (Mode == RecordingMode.NewProcesses) return !_baseline.Contains(pid);
        for (int i = 0, p = pid; i < 16 && _snap.TryGetValue(p, out var info); i++, p = info.ParentPid)
            if (_roots.Contains(p)) return true;
        return false;
    }

    /// <summary>Наблюдение источника. Чужие потоки (Process) отбрасываются; в режиме «новые» старые процессы пишутся как фон (Fresh = false).</summary>
    public void Feed(FlowObservation o)
    {
        lock (_lock)
        {
            if (!_running || _disposed) return;
            // Процесс, которого нет в снимке (только что запущен), — один раз перечитываем список; иначе раз в 2 с.
            if (!_snap.ContainsKey(o.Pid) && _missed.Add(o.Pid)) RefreshSnapshot(force: true);
            else if ((_now() - _snapAt).TotalSeconds > 2) RefreshSnapshot(force: true);
            bool fresh = InScope(o.Pid);
            if (Mode == RecordingMode.Process && !fresh) return;
            var key = (o.Proto, o.Remote.ToString(), o.Port, o.Pid);
            var t = (_now() - _started).TotalSeconds;
            if (!_flows.TryGetValue(key, out var a))
            {
                if (_flows.Count >= MaxFlows) { _truncated = true; return; }
                a = new Acc { Process = _snap.TryGetValue(o.Pid, out var pi) ? pi.Name : $"pid {o.Pid}", Fresh = fresh, First = t };
                _flows[key] = a;
            }
            a.Count++;
            if (o.Hint == FlowHint.Established) a.Established = true;
            else if (o.Hint == FlowHint.Connecting) { a.ConnectingSince ??= t; a.ConnectingLast = t; }
        }
    }

    public RecordingStatus Status()
    {
        lock (_lock)
            return new(!_disposed, _running, Elapsed(), _flows.Count, _flows.Keys.Select(k => k.Item4).Distinct().Count(), _udp, [.. _notes]);
    }

    double Elapsed() => ((_running ? _now() : _stopped == default ? _now() : _stopped) - _started).TotalSeconds;

    public RecordingResult Result()
    {
        lock (_lock)
        {
            var flows = _flows.Select(kv =>
            {
                var (proto, addr, port, pid) = kv.Key;
                var a = kv.Value;
                string state = a.Established ? "works"
                    : proto == "tcp" && a.ConnectingSince is not null && a.ConnectingLast - a.ConnectingSince.Value >= NoReplyAfterSec ? "noreply" : "";
                return new RecordedFlow(proto, addr, port, pid, a.Process, a.Fresh, a.Count, Math.Round(a.First, 1), state);
            }).OrderBy(f => f.FirstSeenSec).ToList();
            return new(Mode == RecordingMode.NewProcesses ? "new" : "process", _running, Elapsed(), _udp, _timedOut, _truncated, [.. _notes], flows);
        }
    }

    public void Dispose()
    {
        lock (_lock) { if (_disposed) return; _disposed = true; _running = false; }
        StopSources();
        foreach (var s in _sources) s.Dispose();
    }
}

/// <summary>Отсев шума: что не относится к игре. Причина — для подписи «убрано: …»; null — оставить.</summary>
public static class FlowNoise
{
    /// <summary>Системные службы, браузеры, обновления, антивирус — процессы, чьи подключения не игровые. Имена без .exe, в нижнем регистре.</summary>
    static readonly HashSet<string> NoiseProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "svchost", "lsass", "services", "wininit", "winlogon", "csrss", "dwm", "fontdrvhost", "spoolsv", "wudfhost",
        "searchhost", "searchapp", "searchindexer", "runtimebroker", "backgroundtaskhost", "taskhostw", "sihost", "dllhost", "wmiprvse",
        "msmpeng", "nissrv", "securityhealthservice", "smartscreen", "compattelrunner", "usoclient", "mousocoreworker", "tiworker",
        "trustedinstaller", "wsappx", "widgets", "yourphone", "phoneexperiencehost", "speechruntime", "ctfmon", "explorer",
        "onedrive", "ms-teams", "teams", "msedge", "msedgewebview2", "chrome", "firefox", "opera", "brave", "vivaldi", "iexplore",
        "yandex", "browser", "steamwebhelper", "epicwebhelper", "cefsharp.browsersubprocess", "dpishell", "dpishell.helper", "winws", "conhost",
    };

    /// <summary>Порты служб, которые игрой не бывают: DNS, DHCP, NTP, NetBIOS, mDNS/SSDP, LLMNR, DoT.</summary>
    static readonly HashSet<int> NoisePorts = [53, 67, 68, 123, 137, 138, 139, 1900, 5353, 5355, 853];

    public static string? Reason(RecordedFlow f, bool skipProcessNames)
    {
        if (!IPAddress.TryParse(f.Address, out var ip)) return L.T("apps.core.reason.not_address");
        if (IsLocal(ip)) return L.T("apps.core.reason.local");
        if (NoisePorts.Contains(f.Port)) return L.T("apps.core.reason.service_port");
        if (!skipProcessNames && NoiseProcesses.Contains(Path.GetFileNameWithoutExtension(f.Process))) return L.T("apps.core.reason.system_or_browser");
        return null;
    }

    public static bool IsLocal(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.Broadcast)) return true;
        ip = new IPAddress(ip.GetAddressBytes());   // без идентификатора интерфейса (fe80::1%12)
        var c = ip.AddressFamily == AddressFamily.InterNetwork ? Cidr.Parse(ip + "/32") : Cidr.Parse(ip + "/128");
        return IpsetValidator.Forbidden.Any(f => f.Net.Overlaps(c));
    }
}
