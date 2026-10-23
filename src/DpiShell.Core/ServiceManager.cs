using System.Diagnostics;
using System.Text.RegularExpressions;
using DpiShell.Core.Localization;

namespace DpiShell.Core;

public enum SvcState { NotInstalled, Stopped, StartPending, StopPending, Running, Unknown }

/// <summary>Служба Windows = сам winws (sc create), без своей обёртки — так же делают бандл bol-van и Flowseal.</summary>
/// <summary>Что журналу отката нужно от службы: состояние, командная строка из реестра, удаление.</summary>
public interface IServiceOps
{
    SvcState State { get; }
    string? ImagePath { get; }
    void Remove();
}

public sealed partial class ServiceManager(AppPaths paths) : IServiceOps
{
    public const string Name = "DpiShell";

    /// <summary>Журнал изменений: создание службы записывается до команды <c>sc create</c>.</summary>
    public IChangeRecorder Recorder { get; set; } = NullRecorder.Instance;

    public string? ImagePath => QueryBinPath(Name);

    /// <summary>Проверка перед созданием/запуском службы (у Shell — хеши ядра по манифесту); исключение — служба не трогается.</summary>
    public Action? BeforeStart { get; set; }

    /// <summary>Чужие службы обхода, которые держат WinDivert и мешают нашей.</summary>
    public static readonly string[] ForeignServices = ["zapret", "winws1", "winws2", "GoodbyeDPI", "discordfix_zapret", "WinDivert14"];

    public static (int Code, string Output) Sc(params string[] args)
    {
        // sc пишет в кодировке консоли (cp866 на русской Windows) — иначе его сообщения об ошибках приходят кракозябрами.
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var oem = System.Text.Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        var psi = new ProcessStartInfo("sc.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = oem, StandardErrorEncoding = oem,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, o);
    }

    public static SvcState Query(string name)
    {
        var (code, o) = Sc("query", name);
        if (code == 1060) return SvcState.NotInstalled;
        return ParseState(o);
    }

    /// <summary>
    /// Состояние из вывода <c>sc query</c>. Подписи полей sc переводит («Состояние» / «STATE» / «ZUSTAND»), а номер и код состояния (4 RUNNING) — нет;
    /// вид строки на Windows 10 и 11 одинаков. Не узнали — <see cref="SvcState.Unknown"/>.
    /// </summary>
    public static SvcState ParseState(string o)
    {
        var m = StateRx().Match(o ?? "");
        if (!m.Success) return SvcState.Unknown;
        return m.Groups[1].Value switch
        {
            "STOPPED" => SvcState.Stopped, "START_PENDING" => SvcState.StartPending, "STOP_PENDING" => SvcState.StopPending,
            "RUNNING" => SvcState.Running, _ => SvcState.Unknown,
        };
    }

    public SvcState State => Query(Name);

    /// <summary>Командная строка службы — из реестра (ImagePath): не зависит от языка Windows и длины строки, в отличие от вывода sc qc.</summary>
    public static string? QueryBinPath(string name)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
            return key?.GetValue("ImagePath", null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    /// <summary>Тип запуска службы — «автоматически» (Start = 2 в реестре). Не узнали — true: лучше предупредить.</summary>
    public static bool IsAutoStart(string name)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
            return key?.GetValue("Start") is int start && start == 2;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return true; }
    }

    /// <summary>Поставить (или перенастроить) службу на стратегию и запустить.</summary>
    public void Apply(IReadOnlyList<string> args)
    {
        BeforeStart?.Invoke();
        var bin = $"\"{paths.Winws}\" {WinwsArgs.Join(args)}";
        if (State == SvcState.NotInstalled)
        {
            // Службы не было — запись ДО создания: прежнее «нет службы», поставим службу с нашим winws.
            var rec = Recorder.Begin(ChangeKinds.Service, L.T("core.svc.journal"),
                new System.Text.Json.Nodes.JsonObject { ["exists"] = false },
                new System.Text.Json.Nodes.JsonObject { ["exe"] = paths.Winws });
            try { Check(Sc("create", Name, "binPath=", bin, "DisplayName=", L.T("core.svc.name"), "start=", "auto"), L.T("core.svc.sc_create")); }
            catch { Recorder.Abort(rec); throw; }
            Recorder.Done(rec);
        }
        else
        {
            Stop();
            Check(Sc("config", Name, "binPath=", bin, "DisplayName=", L.T("core.svc.name"), "start=", "auto"), L.T("core.svc.sc_config"));
        }
        // Имя и описание в «Службах» — на языке окна, приславшего запрос (L.Scope помощника, API 1.13).
        Sc("description", Name, L.T("core.svc.description"));
        Sc("failure", Name, "reset=", "60", "actions=", "restart/5000/restart/5000/none/0");
        Check(Sc("start", Name), L.T("core.svc.sc_start"));
    }

    public void Stop()
    {
        if (State is SvcState.Running or SvcState.StartPending) Sc("stop", Name);
        WaitFor(Name, SvcState.Stopped, TimeSpan.FromSeconds(10));
    }

    /// <summary>«Выключить» — остановить и не стартовать с Windows.</summary>
    public void Disable()
    {
        Stop();
        if (State != SvcState.NotInstalled) Sc("config", Name, "start=", "demand");
    }

    /// <summary>«Удалить полностью»: служба и драйвер WinDivert (если им больше никто не пользуется).</summary>
    public void Remove()
    {
        Stop();
        Sc("delete", Name);
        Sc("stop", "WinDivert");
        Sc("delete", "WinDivert");
        Recorder.Close(ChangeKinds.Service, L.T("core.svc.closed"));
    }

    public static bool WaitFor(string name, SvcState want, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            var s = Query(name);
            if (s == want || want == SvcState.Stopped && s == SvcState.NotInstalled) return true;
            Thread.Sleep(250);
        }
        return false;
    }

    static void Check((int Code, string Output) r, string what)
    {
        if (r.Code != 0) throw new InvalidOperationException(L.T("core.svc.sc_failed", new { what, code = r.Code, output = r.Output.Trim() }));
    }

    [GeneratedRegex(@":\s*\d+\s+(STOPPED|START_PENDING|STOP_PENDING|RUNNING|CONTINUE_PENDING|PAUSE_PENDING|PAUSED)\b")]
    private static partial Regex StateRx();
}

public sealed record Conflict(string Kind, string Name, string Detail)
{
    /// <summary>Откуда взято (имя и «как выключить» собираются на языке окна заново); null — тексты готовые.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public ZapretInstance? Source { get; init; }
    /// <summary>Имя на языке окна: из источника, а у присланного помощником (по-русски) — переводом готовой фразы.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string NameNow => Source is { } z ? Conflicts.ConflictName(z) : L.Relocalize(Name);
    [System.Text.Json.Serialization.JsonIgnore] public string DetailNow => Source is { } z ? Dormant ? z.HowToRemove : z.HowToStop : L.Relocalize(Detail);
    /// <summary>Служба остановлена, но с автозапуском: сейчас не мешает, запустится после перезагрузки. Detail — как убрать насовсем.</summary>
    public bool Dormant { get; init; }
}

public static class Conflicts
{
    /// <summary>Чужие службы и процессы обхода DPI. Свою службу и свой winws не считаем.</summary>
    /// <summary>
    /// Чужие обходы (всё, кроме нашей службы с нашим ядром): кто это и какая стратегия — <see cref="ZapretDetector"/>.
    /// Name — для человека («zapret от Flowseal, стратегия general (ALT11)»), Detail — как выключить.
    /// </summary>
    /// <remarks>Мешает только работающее: процесс или служба не в состоянии «остановлена». Остановленная служба — не помеха.</remarks>
    public static IReadOnlyList<Conflict> Find(AppPaths paths, IReadOnlyList<Strategy>? catalog = null) =>
        [.. Scan(paths, catalog).Where(c => !c.Dormant)];

    /// <summary>Мешающие и спящие (<see cref="Conflict.Dormant"/>: остановлены, запустятся с Windows) — одним обходом.</summary>
    public static IReadOnlyList<Conflict> Scan(AppPaths paths, IReadOnlyList<Strategy>? catalog = null) =>
        From(ZapretDetector.Find(paths, catalog ?? LoadCatalog(paths)));

    /// <summary>Находки → конфликты: наше отсеяно, остановленная служба без автозапуска не показывается.</summary>
    public static IReadOnlyList<Conflict> From(IEnumerable<ZapretInstance> found) =>
        [.. found
            .Where(z => z.Owner != ZapretOwner.DpiShell && (z.Running || z.AutoStart))
            .Select(z => z.Running
                ? new Conflict(z.Kind, ConflictName(z), z.HowToStop) { Source = z }
                : new Conflict(z.Kind, ConflictName(z), z.HowToRemove) { Source = z, Dormant = true })];

    internal static string ConflictName(ZapretInstance z)
    {
        var service = z.Kind == "service";
        var owner = z.OwnerTitle;
        return (service, z.Strategy) switch
        {
            (true, { } st) => L.T("diag.conflict.name_service_strategy", new { owner, service = z.Name, strategy = st.Title }),
            (true, null) => L.T("diag.conflict.name_service", new { owner, service = z.Name }),
            (false, { } st) => L.T("diag.conflict.name_strategy", new { owner, strategy = st.Title }),
            _ => L.T("diag.conflict.name", new { owner }),
        };
    }

    static IReadOnlyList<Strategy> LoadCatalog(AppPaths paths)
    {
        try { return [.. StrategyCatalog.Load(Path.Combine(paths.Seed, "strategies.json")).Strategies, .. UserStrategies.Load(paths)]; }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException) { return []; }
    }

    /// <summary>
    /// Идёт ли интернет через VPN — тогда обход, скорее всего, не нужен. Смотрим не «есть ли VPN-адаптер»
    /// (Radmin VPN, ZeroTier и т. п. — виртуальные локальные сети, интернет через них не ходит), а через какой
    /// адаптер Windows отправит пакет наружу.
    /// </summary>
    public static IReadOnlyList<string> ActiveVpnAdapters()
    {
        var outbound = OutboundAddress();
        if (outbound is null) return [];
        return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            .Where(n => n.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(outbound)))
            .Where(n => n.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Ppp
                     || VpnRx.IsMatch(n.Description) || VpnRx.IsMatch(n.Name))
            .Select(n => n.Name).Distinct().ToList();
    }

    /// <summary>Локальный адрес, с которого ушёл бы пакет в интернет. UDP-«подключение» пакетов не шлёт — только выбирает маршрут.</summary>
    static System.Net.IPAddress? OutboundAddress()
    {
        try
        {
            using var s = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
            s.Connect(new System.Net.IPEndPoint(System.Net.IPAddress.Parse("1.1.1.1"), 53));
            return (s.LocalEndPoint as System.Net.IPEndPoint)?.Address;
        }
        catch (System.Net.Sockets.SocketException) { return null; }
    }

    static readonly Regex VpnRx = new(@"wireguard|openvpn|tap-windows|wintun|amneziawg|outline|nordlynx|proton|tailscale|zerotier|vpn", RegexOptions.IgnoreCase);
}
