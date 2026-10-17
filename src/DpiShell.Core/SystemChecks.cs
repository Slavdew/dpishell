using System.Diagnostics;
using Microsoft.Win32;

namespace DpiShell.Core;

/// <summary>Находка «Диагностики»: Ok — в порядке, Warn — может мешать обходу (что делать — в тексте).</summary>
public sealed record SystemFinding(bool Ok, string Title, string Text);

/// <summary>
/// Проверки окружения из service.bat Flowseal (architecture.md «Диагностика») — только чтение, ничего не меняют:
/// служба BFE, системный прокси, Adguard, сетевые службы Killer / Intel Connectivity / Check Point / SmartByte,
/// путь программы с кириллицей или в OneDrive, строки YouTube в hosts вне блока DpiShell.
/// </summary>
public static class SystemChecks
{
    /// <summary>Службы, которые перехватывают трафик и ломают обход (как у Flowseal: подстрока имени или описания).</summary>
    public static readonly IReadOnlyList<(string Pattern, string Title)> InterferingServices =
    [
        ("Killer", "Killer Network (сетевые утилиты ноутбуков Dell/MSI)"),
        ("Connectivity Network", "Intel Connectivity Network Service"),
        ("TracSrvWrapper", "Check Point (VPN/защита)"),
        ("EPWD", "Check Point Endpoint Security"),
        ("SmartByte", "SmartByte (ускоритель сети Dell)"),
    ];

    /// <summary>Домены YouTube, чьи строки в hosts ломают его (заглушки «блокировщиков» и старые «ускорители»).</summary>
    public static readonly IReadOnlyList<string> YoutubeHosts = ["youtube.com", "youtu.be", "googlevideo.com", "ytimg.com"];

    /// <summary>Все проверки. Долго (sc, реестр, процессы) — вызывать в фоне.</summary>
    public static IReadOnlyList<SystemFinding> Run(AppPaths paths, string? hostsPath = null)
    {
        var list = new List<SystemFinding>();

        var bfe = ServiceManager.Query("BFE");
        list.Add(bfe == SvcState.Running
            ? new(true, "Служба BFE", "работает")
            : new(false, "Служба BFE", "не работает — без неё обход не запустится. Включите службу «Служба базовой фильтрации» (BFE) или проверьте антивирус."));

        list.Add(ProxyFinding(ReadProxy()));

        list.Add(Process.GetProcessesByName("AdguardSvc") is { Length: > 0 } ag && Dispose(ag)
            ? new(false, "Adguard", "запущен — он тоже перехватывает трафик. Если сайты не открываются, выключите в нём фильтрацию HTTPS или закройте его.")
            : new(true, "Adguard", "не запущен"));

        var services = MatchServices(ReadServices()).Where(s => ServiceManager.Query(s.Name) == SvcState.Running).ToList();
        list.Add(services.Count == 0
            ? new(true, "Сетевые службы", "мешающих не найдено")
            : new(false, "Сетевые службы", $"работают: {string.Join(", ", services.Select(s => s.Title).Distinct())}. Они перехватывают трафик — если обход не работает, отключите их."));

        var path = PathProblem(AppContext.BaseDirectory) ?? PathProblem(paths.Root);
        list.Add(path is null ? new(true, "Папка программы", "в порядке") : new(false, "Папка программы", path));

        try
        {
            var yt = YoutubeLinesOutsideBlock(File.ReadAllBytes(hostsPath ?? HostsFileStore.SystemHostsPath));
            list.Add(yt.Count == 0
                ? new(true, "hosts", "лишних строк для YouTube нет")
                : new(false, "hosts", $"есть строки для YouTube от другой программы: {string.Join("; ", yt.Take(3))}{(yt.Count > 3 ? " …" : "")}. Из-за них YouTube может не открываться — уберите их из файла hosts."));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return list;
    }

    static bool Dispose(Process[] ps) { foreach (var p in ps) p.Dispose(); return true; }

    public static SystemFinding ProxyFinding((bool Enabled, string? Server) proxy) => proxy.Enabled
        ? new(false, "Системный прокси", $"включён ({proxy.Server ?? "адрес не указан"}). Через прокси обход может не работать — если он не нужен, выключите: Параметры → Сеть и Интернет → Прокси.")
        : new(true, "Системный прокси", "не используется");

    static (bool, string?) ReadProxy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            return (key?.GetValue("ProxyEnable") is int v && v != 0, key?.GetValue("ProxyServer") as string);
        }
        catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException) { return (false, null); }
    }

    /// <summary>Службы из реестра: имя ключа и отображаемое имя; отключённые (Start = 4) пропускаются.</summary>
    static IEnumerable<(string Name, string Display)> ReadServices()
    {
        var list = new List<(string, string)>();
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (root is null) return list;
            foreach (var name in root.GetSubKeyNames())
            {
                try
                {
                    using var k = root.OpenSubKey(name);
                    if (k is null || k.GetValue("Start") is int s && s == 4) continue;
                    list.Add((name, k.GetValue("DisplayName") as string ?? ""));
                }
                catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
        return list;
    }

    /// <summary>Какие из служб — мешающие (подстрока имени или отображаемого имени, без учёта регистра).</summary>
    public static IReadOnlyList<(string Name, string Title)> MatchServices(IEnumerable<(string Name, string Display)> services) =>
        services.SelectMany(s => InterferingServices
                .Where(p => s.Name.Contains(p.Pattern, StringComparison.OrdinalIgnoreCase) || s.Display.Contains(p.Pattern, StringComparison.OrdinalIgnoreCase))
                .Select(p => (s.Name, p.Title)))
            .ToList();

    /// <summary>Кириллица (и вообще не-ASCII) или OneDrive в пути: у ядра (cygwin) с такими путями бывают сбои. Null — в порядке.</summary>
    public static string? PathProblem(string path)
    {
        if (path.Contains("OneDrive", StringComparison.OrdinalIgnoreCase))
            return $"лежит в OneDrive ({path}) — синхронизация может блокировать файлы ядра. Перенесите программу, например, в C:\\Program Files.";
        if (path.Any(c => c > 127))
            return $"в пути есть русские или другие не латинские буквы ({path}) — ядро может не запуститься. Перенесите программу в папку с латинским именем.";
        return null;
    }

    /// <summary>Строки hosts для доменов YouTube вне блока DpiShell (наш блок YouTube не содержит никогда).</summary>
    public static IReadOnlyList<string> YoutubeLinesOutsideBlock(byte[] hostsBytes)
    {
        using var reader = new StreamReader(new MemoryStream(hostsBytes), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var lines = reader.ReadToEnd().Replace("\r\n", "\n").Split('\n', '\r');
        var found = new List<string>();
        bool inBlock = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith(HostsText.BeginMark, StringComparison.OrdinalIgnoreCase)) { inBlock = true; continue; }
            if (line.StartsWith(HostsText.EndMark, StringComparison.OrdinalIgnoreCase)) { inBlock = false; continue; }
            if (inBlock) continue;
            if (HostsText.ParseLine(line).Any(e => YoutubeHosts.Any(y => e.Host == y || e.Host.EndsWith("." + y, StringComparison.Ordinal))))
                found.Add(line);
        }
        return found;
    }
}
