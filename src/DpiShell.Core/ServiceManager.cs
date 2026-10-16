using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DpiShell.Core;

public enum SvcState { NotInstalled, Stopped, StartPending, StopPending, Running, Unknown }

/// <summary>Служба Windows = сам winws (sc create), без своей обёртки — так же делают бандл bol-van и Flowseal.</summary>
public sealed partial class ServiceManager(AppPaths paths)
{
    public const string Name = "DpiShell";

    /// <summary>Чужие службы обхода, которые держат WinDivert и мешают нашей.</summary>
    public static readonly string[] ForeignServices = ["zapret", "winws1", "winws2", "GoodbyeDPI", "discordfix_zapret", "WinDivert14"];

    public static (int Code, string Output) Sc(params string[] args)
    {
        var psi = new ProcessStartInfo("sc.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
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
        var m = StateRx().Match(o);
        if (!m.Success) return SvcState.Unknown;
        return m.Groups[1].Value switch
        {
            "1" => SvcState.Stopped, "2" => SvcState.StartPending, "3" => SvcState.StopPending, "4" => SvcState.Running, _ => SvcState.Unknown,
        };
    }

    public SvcState State => Query(Name);

    /// <summary>Командная строка службы из её настроек (sc qc). Буфер с запасом: у служб zapret строка длинная.</summary>
    public static string? QueryBinPath(string name)
    {
        var (code, o) = Sc("qc", name, "16384");
        if (code != 0) return null;
        var m = BinPathRx().Match(o);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    /// <summary>Поставить (или перенастроить) службу на стратегию и запустить.</summary>
    public void Apply(IReadOnlyList<string> args)
    {
        var bin = $"\"{paths.Winws}\" {WinwsArgs.Join(args)}";
        if (State == SvcState.NotInstalled)
            Check(Sc("create", Name, "binPath=", bin, "DisplayName=", "DpiShell DPI bypass", "start=", "auto"), "создать службу");
        else
        {
            Stop();
            Check(Sc("config", Name, "binPath=", bin, "start=", "auto"), "перенастроить службу");
        }
        Sc("description", Name, "Обход DPI (zapret winws), управляется приложением DpiShell");
        Sc("failure", Name, "reset=", "60", "actions=", "restart/5000/restart/5000/none/0");
        Check(Sc("start", Name), "запустить службу");
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
        if (r.Code != 0) throw new InvalidOperationException($"Не удалось {what}: код {r.Code}. {r.Output.Trim()}");
    }

    [GeneratedRegex(@"BINARY_PATH_NAME\s*:\s*(.+)")]
    private static partial Regex BinPathRx();

    [GeneratedRegex(@"STATE\s*:\s*(\d)")]
    private static partial Regex StateRx();
}

public sealed record Conflict(string Kind, string Name, string Detail);

public static class Conflicts
{
    /// <summary>Чужие службы и процессы обхода DPI. Свою службу и свой winws не считаем.</summary>
    /// <summary>
    /// Чужие обходы (всё, кроме нашей службы с нашим ядром): кто это и какая стратегия — <see cref="ZapretDetector"/>.
    /// Name — для человека («zapret от Flowseal, стратегия general (ALT11)»), Detail — как выключить.
    /// </summary>
    public static IReadOnlyList<Conflict> Find(AppPaths paths, IReadOnlyList<Strategy>? catalog = null)
    {
        catalog ??= LoadCatalog(paths);
        return ZapretDetector.Find(paths, catalog)
            .Where(z => z.Owner != ZapretOwner.DpiShell)
            .Select(z => new Conflict(z.Kind,
                z.OwnerTitle + (z.Kind == "service" ? $" (служба {z.Name})" : "") + (z.Strategy is { } st ? $", стратегия {st.Title}" : ""),
                z.HowToStop))
            .ToList();
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
