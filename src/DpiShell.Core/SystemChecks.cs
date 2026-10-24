using System.Diagnostics;
using Microsoft.Win32;
using DpiShell.Core.Localization;

namespace DpiShell.Core;

/// <summary>Находка «Диагностики»: Ok — в порядке, Warn — может мешать обходу (что делать — в тексте).</summary>
/// <param name="Go">Куда ведёт ссылка «Настроить →» («proxy» — вкладка «Прокси»); null — ссылки нет.</param>
public sealed record SystemFinding(bool Ok, string Title, string Text, string? Go = null)
{
    public const string GoProxy = "proxy";

    /// <summary>Как собрать заголовок и текст заново (после смены языка окна): данные уже в замыкании.</summary>
    public Func<(string Title, string Text)>? Render { get; init; }

    /// <summary>Какая это проверка (bfe, proxy, adguard, services, folder, hosts) — карточка «Почему не получилось» берёт только относящиеся к сорвавшемуся действию.</summary>
    public string? Key { get; init; }

    /// <summary>Находка про окружение, а не про то, что мешает включению: системный прокси (у владельца может быть нужным), папка окна, hosts.</summary>
    public bool IsContext => Key is "proxy" or "folder" or "hosts";

    /// <summary>Находка, тексты которой собираются по правилу <paramref name="render"/> (и пересобираются на другом языке).</summary>
    public static SystemFinding Of(bool ok, Func<(string Title, string Text)> render, string? go = null)
    {
        var (title, text) = render();
        return new(ok, title, text, go) { Render = render };
    }

    /// <summary>Те же данные, тексты — на текущем языке.</summary>
    public SystemFinding Refreshed() => Render is null ? this : this with { Title = Render().Title, Text = Render().Text };
}

/// <summary>
/// Проверки окружения из service.bat Flowseal (architecture.md «Диагностика») — только чтение, ничего не меняют:
/// служба BFE, системный прокси, Adguard, сетевые службы Killer / Intel Connectivity / Check Point / SmartByte,
/// путь программы с кириллицей или в OneDrive, строки YouTube в hosts вне блока DpiShell.
/// </summary>
public static class SystemChecks
{
    /// <summary>Службы, которые перехватывают трафик и ломают обход (как у Flowseal: подстрока имени или описания).</summary>
    public static IReadOnlyList<(string Pattern, string Title)> InterferingServices =>
    [
        ("Killer", L.T("diag.find.svc_killer")),
        ("Connectivity Network", "Intel Connectivity Network Service"),
        ("TracSrvWrapper", L.T("diag.find.svc_checkpoint")),
        ("EPWD", "Check Point Endpoint Security"),
        ("SmartByte", L.T("diag.find.svc_smartbyte")),
    ];

    /// <summary>Домены YouTube, чьи строки в hosts ломают его (заглушки «блокировщиков» и старые «ускорители»).</summary>
    public static readonly IReadOnlyList<string> YoutubeHosts = ["youtube.com", "youtu.be", "googlevideo.com", "ytimg.com"];

    /// <summary>Все проверки. Долго (sc, реестр, процессы) — вызывать в фоне.</summary>
    public static IReadOnlyList<SystemFinding> Run(AppPaths paths, string? hostsPath = null)
    {
        var list = new List<SystemFinding>();

        // Вывод sc не разобран (Unknown) — не «не работает»: не знаем, а не знаем — не тревожим.
        var bfe = ServiceManager.Query("BFE");
        list.Add((bfe is SvcState.Running or SvcState.Unknown
            ? SystemFinding.Of(true, () => (L.T("diag.find.bfe.title"), L.T("diag.find.ok_running")))
            : SystemFinding.Of(false, () => (L.T("diag.find.bfe.title"), L.T("diag.find.bfe.bad")))) with { Key = "bfe" });

        list.Add(ProxyFinding(ReadProxy()));

        list.Add(Process.GetProcessesByName("AdguardSvc") is { Length: > 0 } ag && Dispose(ag)
            ? SystemFinding.Of(false, () => ("Adguard", L.T("diag.find.adguard.bad"))) with { Key = "adguard" }
            : SystemFinding.Of(true, () => ("Adguard", L.T("diag.find.adguard.ok"))) with { Key = "adguard" });

        var services = MatchServices(ReadServices()).Where(s => ServiceManager.Query(s.Name) == SvcState.Running).ToList();
        list.Add((services.Count == 0
            ? SystemFinding.Of(true, () => (L.T("diag.find.services.title"), L.T("diag.find.services.ok")))
            : SystemFinding.Of(false, () => (L.T("diag.find.services.title"), L.T("diag.find.services.bad", new { list = string.Join(", ", services.Select(s => s.Title).Distinct()) })))) with { Key = "services" });

        var badPath = PathProblem(AppContext.BaseDirectory) is not null ? AppContext.BaseDirectory : PathProblem(paths.Root) is not null ? paths.Root : null;
        list.Add((badPath is null ? SystemFinding.Of(true, () => (L.T("diag.find.folder.title"), L.T("diag.find.folder.ok")))
            : SystemFinding.Of(false, () => (L.T("diag.find.folder.title"), PathProblem(badPath) ?? ""))) with { Key = "folder" });

        try
        {
            var yt = YoutubeLinesOutsideBlock(File.ReadAllBytes(hostsPath ?? HostsFileStore.SystemHostsPath));
            list.Add((yt.Count == 0
                ? SystemFinding.Of(true, () => ("hosts", L.T("diag.find.hosts.ok")))
                : SystemFinding.Of(false, () => ("hosts", L.T("diag.find.hosts.bad", new { lines = string.Join("; ", yt.Take(3)) + (yt.Count > 3 ? " …" : "") })))) with { Key = "hosts" });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return list;
    }

    static bool Dispose(Process[] ps) { foreach (var p in ps) p.Dispose(); return true; }

    public static SystemFinding ProxyFinding((bool Enabled, string? Server) proxy) => (proxy.Enabled
        ? SystemFinding.Of(false, () => (L.T("diag.find.proxy.title"), L.T("diag.find.proxy.on", new { server = proxy.Server ?? L.T("diag.find.proxy.no_server") })), SystemFinding.GoProxy)
        : SystemFinding.Of(true, () => (L.T("diag.find.proxy.title"), L.T("diag.find.proxy.off")))) with { Key = "proxy" };

    /// <summary>Действующий системный прокси: настройки пользователя или, если политика делает их общими, настройки машины. Автонастройка — как «включён».</summary>
    /// <summary>Системный прокси включён (под тестами реестр не читается — false).</summary>
    public static bool ProxyOn() => ReadProxy().Item1;

    static (bool, string?) ReadProxy()
    {
        if (SystemProxyGuard.Forbidden) return (false, null);   // под тестами настоящий реестр не читается
        try
        {
            var eff = ProxyOverview.Read(new WindowsProxyRegistry(), new WindowsWinHttpProxy()).Effective;
            return (eff.IsOn, ProxyValues.Describe(eff));
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
            return L.T("diag.find.folder.onedrive", new { path });
        if (path.Any(c => c > 127))
            return L.T("diag.find.folder.non_ascii", new { path });
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
