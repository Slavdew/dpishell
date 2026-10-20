using DpiShell.Core.Localization;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;

namespace DpiShell.Core;

// Настоящие источники записи (Windows). Работают только в помощнике; на этой сборке живой проверки нет — тесты идут на подмене
// (IFlowSource) и на разборе таблицы TCP на самой машине. Список «что проверить вживую» — в docs/windows/README.md.

/// <summary>Таблица TCP Windows раз в секунду: IPv4 и IPv6 с PID. Без драйвера. Состояние «подключается» (SYN_SENT) и «установлено».</summary>
public sealed class TcpTableSource : IFlowSource
{
    public string Name => L.T("core.rec.src.tcp");
    public bool CoversUdp => false;

    CancellationTokenSource? _cts;
    Task? _loop;
    readonly TimeSpan _period;

    public TcpTableSource(TimeSpan? period = null) => _period = period ?? TimeSpan.FromSeconds(1);

    public string? Start(Action<FlowObservation> onFlow)
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { foreach (var o in Read()) onFlow(o); }
                catch (Win32Exception) { }
                try { await Task.Delay(_period, ct); } catch (OperationCanceledException) { break; }
            }
        }, ct);
        return null;
    }

    public void Stop() => _cts?.Cancel();
    public void Dispose() { Stop(); _cts?.Dispose(); }

    const int AfInet = 2, AfInet6 = 23, TcpTableOwnerPidAll = 5;

    /// <summary>Одна выборка таблицы: удалённый адрес не пустой, состояние не «слушает».</summary>
    public static IReadOnlyList<FlowObservation> Read()
    {
        var list = new List<FlowObservation>();
        foreach (var af in new[] { AfInet, AfInet6 })
        {
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, TcpTableOwnerPidAll, 0);   // узнать размер
            for (int attempt = 0; attempt < 3 && size > 0; attempt++)
            {
                var buf = Marshal.AllocHGlobal(size);
                try
                {
                    int rc = GetExtendedTcpTable(buf, ref size, false, af, TcpTableOwnerPidAll, 0);
                    if (rc == 122 /* ERROR_INSUFFICIENT_BUFFER: таблица выросла */) { size += 4096; continue; }
                    if (rc != 0) break;
                    int n = Marshal.ReadInt32(buf);
                    int rowSize = af == AfInet ? 24 : 56;
                    for (int i = 0; i < n; i++)
                        if (Row(buf + 4 + i * rowSize, af == AfInet6) is { } o) list.Add(o);
                    break;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        return list;
    }

    static FlowObservation? Row(IntPtr p, bool v6)
    {
        uint state, pid; int port; byte[] remote;
        if (!v6)
        {
            state = (uint)Marshal.ReadInt32(p);
            remote = BitConverter.GetBytes(Marshal.ReadInt32(p, 12));   // уже в сетевом порядке байт
            port = NetPort(Marshal.ReadInt32(p, 16));
            pid = (uint)Marshal.ReadInt32(p, 20);
        }
        else
        {
            remote = new byte[16];
            Marshal.Copy(p + 24, remote, 0, 16);
            port = NetPort(Marshal.ReadInt32(p, 44));
            state = (uint)Marshal.ReadInt32(p, 48);
            pid = (uint)Marshal.ReadInt32(p, 52);
        }
        return Make(state, remote, port, (int)pid);
    }

    /// <summary>Разбор одной строки таблицы (вынесен для проверки без сети).</summary>
    public static FlowObservation? Make(uint state, byte[] remote, int port, int pid)
    {
        if (state is 2 /* LISTEN */ or 12 /* DELETE_TCB */ || port == 0) return null;
        var ip = new IPAddress(remote);
        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return null;
        var hint = state switch { 3 => FlowHint.Connecting, 5 => FlowHint.Established, _ => FlowHint.Unknown };
        return new FlowObservation("tcp", ip, port, pid, hint);
    }

    static int NetPort(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern int GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool sort, int af, int tableClass, uint reserved);
}

/// <summary>
/// WinDivert, слой FLOW, только чтение (SNIFF | RECV_ONLY): события «поток появился» с PID и удалённым адресом для TCP и UDP.
/// Содержимое пакетов не читается и не меняется — драйвер тот же, что у winws, но нагрузки на трафик нет. DLL берётся из папки ядра
/// по полному пути (после проверки хешей ядра), а не по имени.
/// </summary>
public sealed class WinDivertFlowSource : IFlowSource
{
    public string Name => L.T("core.rec.src.windivert");
    public bool CoversUdp => true;

    readonly string _dll;
    IntPtr _lib, _handle;
    Thread? _thread;
    volatile bool _stop;
    Delegates? _f;

    public WinDivertFlowSource(string dllPath) => _dll = dllPath;

    sealed record Delegates(Open Open, Recv Recv, Shutdown Shutdown, Close Close);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr Open([MarshalAs(UnmanagedType.LPStr)] string filter, int layer, short priority, ulong flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate bool Recv(IntPtr h, IntPtr packet, uint packetLen, IntPtr recvLen, IntPtr addr);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate bool Shutdown(IntPtr h, int how);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate bool Close(IntPtr h);

    const int LayerFlow = 2;
    const ulong FlagSniff = 1, FlagRecvOnly = 4;
    const int AddrSize = 80;

    public string? Start(Action<FlowObservation> onFlow)
    {
        if (!File.Exists(_dll)) return L.T("core.rec.no_dll");
        try
        {
            _lib = NativeLibrary.Load(_dll);
            _f = new Delegates(
                Fn<Open>("WinDivertOpen"), Fn<Recv>("WinDivertRecv"), Fn<Shutdown>("WinDivertShutdown"), Fn<Close>("WinDivertClose"));
        }
        catch (Exception e) when (e is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            return L.T("core.rec.driver_not_loaded");
        }
        // Фильтр «true»: события отбираем здесь. Так нечему не скомпилироваться в фильтре на другой версии драйвера.
        _handle = _f.Open("true", LayerFlow, 0, FlagSniff | FlagRecvOnly);
        if (_handle == new IntPtr(-1) || _handle == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            _handle = IntPtr.Zero;
            return err == 5 ? L.T("core.rec.driver_denied") : err == 577 ? L.T("core.rec.driver_blocked") : L.T("core.rec.driver_failed", new { code = err });
        }
        _thread = new Thread(() => Loop(onFlow)) { IsBackground = true, Name = "flow-record" };
        _thread.Start();
        return null;
    }

    T Fn<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_lib, name));

    void Loop(Action<FlowObservation> onFlow)
    {
        var addr = Marshal.AllocHGlobal(AddrSize);
        try
        {
            while (!_stop)
            {
                if (!_f!.Recv(_handle, IntPtr.Zero, 0, IntPtr.Zero, addr)) break;   // закрыли ручку — выходим
                if (Parse(addr) is { } o) onFlow(o);
            }
        }
        finally { Marshal.FreeHGlobal(addr); }
    }

    /// <summary>Разбор WINDIVERT_ADDRESS слоя FLOW. Только событие «установлен», без loopback, TCP и UDP.</summary>
    public static FlowObservation? Parse(IntPtr a)
    {
        int ev = Marshal.ReadByte(a, 9);
        byte flags = Marshal.ReadByte(a, 10);
        bool loopback = (flags & 0x04) != 0, v6 = (flags & 0x10) != 0;
        if (ev != 0 /* FLOW_ESTABLISHED */ || loopback) return null;
        int pid = Marshal.ReadInt32(a, 32);
        int port = (ushort)Marshal.ReadInt16(a, 70);
        int proto = Marshal.ReadByte(a, 72);
        if (proto is not (6 or 17) || port == 0) return null;
        var words = new uint[4];
        for (int i = 0; i < 4; i++) words[i] = (uint)Marshal.ReadInt32(a, 52 + i * 4);
        var ip = FlowAddress(words, v6);
        return new FlowObservation(proto == 6 ? "tcp" : "udp", ip, port, pid, FlowHint.Unknown);
    }

    /// <summary>Адрес из четырёх слов WinDivert (порядок хоста): IPv4 — слово 0; IPv6 — слова идут от младшего к старшему.</summary>
    public static IPAddress FlowAddress(uint[] w, bool v6)
    {
        if (!v6) return new IPAddress([(byte)(w[0] >> 24), (byte)(w[0] >> 16), (byte)(w[0] >> 8), (byte)w[0]]);
        var b = new byte[16];
        for (int i = 0; i < 4; i++)
        {
            uint x = w[3 - i];
            b[i * 4] = (byte)(x >> 24); b[i * 4 + 1] = (byte)(x >> 16); b[i * 4 + 2] = (byte)(x >> 8); b[i * 4 + 3] = (byte)x;
        }
        return new IPAddress(b);
    }

    public void Stop()
    {
        _stop = true;
        if (_handle != IntPtr.Zero && _f is not null)
        {
            var h = _handle;
            _handle = IntPtr.Zero;
            try { _f.Shutdown(h, 1 /* RECV */); } catch (SEHException) { }
            _thread?.Join(2000);
            try { _f.Close(h); } catch (SEHException) { }
        }
    }

    public void Dispose()
    {
        Stop();
        if (_lib != IntPtr.Zero) { NativeLibrary.Free(_lib); _lib = IntPtr.Zero; }
    }
}

/// <summary>Список процессов и родителей (Toolhelp32): без прав администратора доступен, без WMI.</summary>
public sealed class ToolhelpProcessCatalog : IProcessCatalog
{
    public IReadOnlyList<ProcInfo> Snapshot()
    {
        var list = new List<ProcInfo>();
        var snap = CreateToolhelp32Snapshot(2 /* PROCESS */, 0);
        if (snap == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var e = new Entry { Size = (uint)Marshal.SizeOf<Entry>() };
            for (bool ok = Process32First(snap, ref e); ok; ok = Process32Next(snap, ref e))
                list.Add(new((int)e.Pid, (int)e.ParentPid, e.Exe));
        }
        finally { CloseHandle(snap); }
        return list;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct Entry
    {
        public uint Size, Usage, Pid;
        public UIntPtr HeapId;
        public uint ModuleId, Threads, ParentPid;
        public int PriClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Exe;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")] static extern bool Process32First(IntPtr h, ref Entry e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")] static extern bool Process32Next(IntPtr h, ref Entry e);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
}
