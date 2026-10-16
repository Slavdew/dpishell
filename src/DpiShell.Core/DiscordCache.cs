using System.Diagnostics;

namespace DpiShell.Core;

/// <summary>Что нашли или удалили: папки кэша, их размер, что не удалось (файл занят).</summary>
public sealed record DiscordCacheInfo(IReadOnlyList<string> Folders, long Bytes, IReadOnlyList<string> Failed);

/// <summary>
/// «Очистить кэш Discord» (09-features-spec, решение 5): после смены стратегии Discord держит в кэше неудачные ответы
/// и долго «подключается». Удаляются только Cache, Code Cache и GPUCache в %APPDATA%\discord (и PTB/Canary) —
/// вход, настройки и сообщения не трогаются. Discord должен быть закрыт: открытые им файлы не удалятся.
/// </summary>
public static class DiscordCache
{
    public static readonly IReadOnlyList<string> Clients = ["discord", "discordptb", "discordcanary"];
    public static readonly IReadOnlyList<string> CacheFolders = ["Cache", "Code Cache", "GPUCache"];
    /// <summary>Имена процессов клиентов (без .exe).</summary>
    public static readonly IReadOnlyList<string> Processes = ["Discord", "DiscordPTB", "DiscordCanary"];

    static string DefaultAppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>Существующие папки кэша. <paramref name="appData"/> — для тестов (по умолчанию %APPDATA%).</summary>
    public static DiscordCacheInfo Find(string? appData = null)
    {
        var root = appData ?? DefaultAppData;
        var folders = Clients.SelectMany(c => CacheFolders.Select(f => Path.Combine(root, c, f))).Where(Directory.Exists).ToList();
        return new(folders, folders.Sum(Size), []);
    }

    /// <summary>Удалить содержимое папок кэша (сами папки тоже). Занятые файлы пропускаются и попадают в Failed.</summary>
    public static DiscordCacheInfo Clear(string? appData = null)
    {
        var found = Find(appData);
        var failed = new List<string>();
        long freed = 0;
        foreach (var dir in found.Folders)
        {
            foreach (var f in SafeFiles(dir))
            {
                long len = 0;
                try { len = new FileInfo(f).Length; File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); freed += len; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failed.Add(f); }
            }
            // Пустые подпапки — снизу вверх; занятые остаются.
            foreach (var d in SafeDirs(dir).OrderByDescending(d => d.Length).Append(dir))
                try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return new(found.Folders, freed, failed);
    }

    /// <summary>Запущен ли Discord (любой клиент).</summary>
    public static bool IsRunning() => Processes.Any(n =>
    {
        var list = Process.GetProcessesByName(n);
        foreach (var p in list) p.Dispose();
        return list.Length > 0;
    });

    /// <summary>Закрыть Discord (все его процессы). Возвращает, сколько процессов завершено.</summary>
    public static int Close()
    {
        int n = 0;
        foreach (var name in Processes)
            foreach (var p in Process.GetProcessesByName(name))
                using (p)
                {
                    try { p.Kill(entireProcessTree: true); p.WaitForExit(5000); n++; }
                    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                }
        return n;
    }

    static long Size(string dir) => SafeFiles(dir).Sum(f => { try { return new FileInfo(f).Length; } catch (IOException) { return 0L; } });

    static IEnumerable<string> SafeFiles(string dir)
    {
        try { return Directory.GetFiles(dir, "*", SearchOption.AllDirectories); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    static IEnumerable<string> SafeDirs(string dir)
    {
        try { return Directory.GetDirectories(dir, "*", SearchOption.AllDirectories); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }
}
