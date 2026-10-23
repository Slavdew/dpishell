using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;
using DpiShell.Core.Localization;

namespace DpiShell.Core;

public enum ZapretOwner { DpiShell, Flowseal, OtherZapret, GoodbyeDpi }

/// <summary>Найденный запущенный обход: служба или процесс, чей он и какая стратегия (если узнали).</summary>
public sealed record ZapretInstance(
    string Kind, string Name, ZapretOwner Owner, string? ExePath, string? CommandLine, Strategy? Strategy, string? State)
{
    public string OwnerTitle => Owner switch
    {
        ZapretOwner.DpiShell => "DpiShell",
        ZapretOwner.Flowseal => L.T("diag.conflict.owner_flowseal"),
        ZapretOwner.GoodbyeDpi => "GoodbyeDPI",
        _ => L.T("diag.conflict.owner_other"),
    };

    /// <summary>Как это выключить — для предупреждения.</summary>
    public string HowToStop => (Owner, Kind) switch
    {
        (ZapretOwner.Flowseal, "service") => L.T("diag.conflict.stop_flowseal_service"),
        (ZapretOwner.Flowseal, _) => L.T("diag.conflict.stop_flowseal_window"),
        (_, "service") => L.T("diag.conflict.stop_service", new { name = Name }),
        _ => L.T("diag.conflict.stop_process"),
    };
}

/// <summary>
/// Кто сейчас перехватывает трафик: наша служба, Flowseal, другой zapret, GoodbyeDPI.
/// Стратегия узнаётся сравнением командной строки winws с каталогом (общая и YouTube-части, фейки — по имени файла).
/// </summary>
public static class ZapretDetector
{
    static readonly string[] Services = [ServiceManager.Name, "zapret", "winws1", "winws2", "GoodbyeDPI", "discordfix_zapret"];

    public static IReadOnlyList<ZapretInstance> Find(AppPaths paths, IReadOnlyList<Strategy> catalog)
    {
        var r = new List<ZapretInstance>();
        var serviceExes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in Services)
        {
            var state = ServiceManager.Query(name);
            if (state == SvcState.NotInstalled) continue;
            var bin = ServiceManager.QueryBinPath(name);
            var (exe, args) = SplitCommandLine(bin);
            if (exe is not null) serviceExes.Add(exe);
            r.Add(new("service", name, OwnerOf(paths, exe, name), exe, bin, Match(args, catalog), state.ToString()));
        }

        foreach (var (pid, exe, cmd) in Processes("winws.exe", "goodbyedpi.exe"))
        {
            // Процесс, запущенный службой, уже учтён строкой службы.
            if (exe is not null && serviceExes.Contains(exe) && IsServiceChild(pid)) continue;
            var (_, args) = SplitCommandLine(cmd);
            r.Add(new("process", $"PID {pid}", OwnerOf(paths, exe, null), exe, cmd, Match(args, catalog), null));
        }
        return r;
    }

    static ZapretOwner OwnerOf(AppPaths paths, string? exe, string? service)
    {
        if (exe is not null && exe.Equals(paths.Winws, StringComparison.OrdinalIgnoreCase)) return ZapretOwner.DpiShell;
        if (service is "GoodbyeDPI" || exe?.EndsWith("goodbyedpi.exe", StringComparison.OrdinalIgnoreCase) == true) return ZapretOwner.GoodbyeDpi;
        if (exe is not null && LooksLikeFlowseal(exe)) return ZapretOwner.Flowseal;
        return ZapretOwner.OtherZapret;
    }

    /// <summary>Сборка Flowseal: winws.exe лежит в bin\, рядом с bin — service.bat и general*.bat.</summary>
    public static bool LooksLikeFlowseal(string exe)
    {
        try
        {
            var bin = Path.GetDirectoryName(exe);
            var root = bin is null ? null : Path.GetDirectoryName(bin);
            if (root is null) return false;
            return File.Exists(Path.Combine(root, "service.bat")) && Directory.GetFiles(root, "general*.bat").Length > 0
                   || root.Contains("zapret-discord-youtube", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    // ---------- стратегия по командной строке ----------

    /// <summary>Какая стратегия каталога совпадает по общей (general) и YouTube (google) частям. null — не узнали.</summary>
    public static Strategy? Match(IReadOnlyList<string> args, IReadOnlyList<Strategy> catalog)
    {
        var roles = RolesOf(args);
        if (!roles.ContainsKey("general") && !roles.ContainsKey("google")) return null;
        return catalog.FirstOrDefault(s =>
            new[] { "general", "google" }.All(role =>
                roles.TryGetValue(role, out var got) == s.Roles.ContainsKey(role)
                && (got is null || Normalize(s.Roles[role]) == got)));
    }

    /// <summary>Профили (между --new) → роль по фильтру → нормализованные параметры десинхронизации.</summary>
    public static Dictionary<string, string> RolesOf(IReadOnlyList<string> args)
    {
        var result = new Dictionary<string, string>();
        var profile = new List<string>();
        void Flush()
        {
            if (profile.Count == 0) return;
            var role = RoleOf(profile);
            if (role is not null && !result.ContainsKey(role))
                result[role] = NormalizeTokens(profile.Where(t => !IsFilter(t)));
            profile.Clear();
        }
        foreach (var a in args)
        {
            if (a == "--new") Flush(); else profile.Add(a);
        }
        Flush();
        return result;
    }

    static bool IsFilter(string t) => t.StartsWith("--filter-") || t.StartsWith("--hostlist") || t.StartsWith("--ipset") || t.StartsWith("--wf-");

    static string? RoleOf(List<string> p)
    {
        string? Val(string name) => p.FirstOrDefault(t => t.StartsWith(name + "="))?.Split('=', 2)[1];
        var tcp = Val("--filter-tcp");
        bool ipset = p.Any(t => t.StartsWith("--ipset="));
        bool google = p.Any(t => t.StartsWith("--hostlist=") && t.Contains("google", StringComparison.OrdinalIgnoreCase));
        if (tcp == "80,443" && !ipset) return "general";
        if (tcp == "443" && google) return "google";
        return null;
    }

    /// <summary>Пути фейков → имя файла, кавычки и порядок одинаковых параметров не важны для сравнения.</summary>
    public static string Normalize(string desync) => NormalizeTokens(desync.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>По токенам: путь в значении может содержать пробел (папка «Dpi Shell»), резать строку нельзя.</summary>
    static string NormalizeTokens(IEnumerable<string> tokens) =>
        string.Join(' ', tokens
            .Select(t =>
            {
                t = t.Replace("\"", "");
                var i = t.IndexOf('=');
                if (i < 0) return t;
                var v = t[(i + 1)..];
                if (v.Contains('/') || v.Contains('\\')) v = Path.GetFileName(v.Replace('\\', '/'));
                return t[..(i + 1)] + v;
            }));

    // ---------- командные строки ----------

    /// <summary>«"C:\x\winws.exe" --a=1 --b="c d"» → exe и аргументы (правила разбора Windows).</summary>
    public static (string? Exe, IReadOnlyList<string> Args) SplitCommandLine(string? cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return (null, []);
        var parts = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool quoted = false, any = false;
        foreach (var ch in cmd)
        {
            if (ch == '"') { quoted = !quoted; any = true; continue; }
            if (char.IsWhiteSpace(ch) && !quoted) { if (any || cur.Length > 0) parts.Add(cur.ToString()); cur.Clear(); any = false; continue; }
            cur.Append(ch);
        }
        if (any || cur.Length > 0) parts.Add(cur.ToString());
        return parts.Count == 0 ? (null, []) : (parts[0], parts.Skip(1).ToList());
    }

    internal static IEnumerable<(int Pid, string? Exe, string? Cmd)> Processes(params string[] names)
    {
        var list = new List<(int, string?, string?)>();
        try
        {
            var where = string.Join(" OR ", names.Select(n => $"Name='{n}'"));
            using var s = new ManagementObjectSearcher($"SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE {where}");
            foreach (var o in s.Get())
                using (o)
                    list.Add((Convert.ToInt32(o["ProcessId"]), o["ExecutablePath"] as string, o["CommandLine"] as string));
        }
        catch (Exception e) when (e is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // Без WMI — хотя бы пути, без командной строки (стратегию не узнаем).
            foreach (var n in names)
                foreach (var (pid, path) in WinwsProcess.FindRunning(Path.GetFileNameWithoutExtension(n)))
                    list.Add((pid, path, null));
        }
        return list;
    }

    static bool IsServiceChild(int pid)
    {
        try
        {
            using var s = new ManagementObjectSearcher($"SELECT ParentProcessId FROM Win32_Process WHERE ProcessId={pid}");
            foreach (var o in s.Get())
                using (o)
                {
                    var parent = Convert.ToInt32(o["ParentProcessId"]);
                    using var p = Process.GetProcessById(parent);
                    return p.ProcessName.Equals("services", StringComparison.OrdinalIgnoreCase);
                }
        }
        catch (Exception) { }
        return false;
    }
}
