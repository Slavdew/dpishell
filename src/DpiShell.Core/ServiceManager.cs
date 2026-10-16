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

    [GeneratedRegex(@"STATE\s*:\s*(\d)")]
    private static partial Regex StateRx();
}

public sealed record Conflict(string Kind, string Name, string Detail);

public static class Conflicts
{
    /// <summary>Чужие службы и процессы обхода DPI. Свою службу и свой winws не считаем.</summary>
    public static IReadOnlyList<Conflict> Find(AppPaths paths)
    {
        var r = new List<Conflict>();
        foreach (var s in ServiceManager.ForeignServices)
        {
            var st = ServiceManager.Query(s);
            if (st is not SvcState.NotInstalled) r.Add(new("service", s, st.ToString()));
        }
        foreach (var (pid, path) in WinwsProcess.FindRunning())
            if (path is null || !path.Equals(paths.Winws, StringComparison.OrdinalIgnoreCase))
                r.Add(new("process", "winws.exe", $"PID {pid} {path}"));
        foreach (var (pid, path) in WinwsProcess.FindRunning("goodbyedpi"))
            r.Add(new("process", "goodbyedpi.exe", $"PID {pid} {path}"));
        return r;
    }

    /// <summary>Есть ли поднятый VPN/туннельный адаптер — тогда обход, скорее всего, не нужен.</summary>
    public static IReadOnlyList<string> ActiveVpnAdapters() =>
        System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            .Where(n => n.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Ppp
                     || VpnRx.IsMatch(n.Description) || VpnRx.IsMatch(n.Name))
            .Select(n => n.Description).ToList();

    static readonly Regex VpnRx = new(@"wireguard|openvpn|tap-windows|wintun|amneziawg|outline|nordlynx|proton|tailscale|zerotier|vpn", RegexOptions.IgnoreCase);
}
