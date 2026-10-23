using System.Globalization;

namespace DpiShell.Core;

/// <summary>
/// Ядро ждёт окна (ТЗ av-exclusion, п. 10; helper-protocol §2, API 1.17). Порядок первого запуска: установка помощника → вопрос об исключении
/// Защитника (добавляет уже помощник) → только потом скачивание ядра. Поэтому установка помощника, если ядра ещё нет, кладёт в закрытую
/// папку метку <c>core.wait</c> со временем, и служба при старте ядро сама не качает. Метку снимает первое же скачивание ядра по команде окна
/// (<c>core_repair</c>, <c>core_offline_install</c>). Окна нет — служба ждёт не дольше <see cref="Limit"/> и качает сама; после перезагрузки
/// истёкшая метка не держит. Так служба без окна не останется без ядра, а старое окно с новым помощником получает ядро тем же путём.
/// </summary>
public static class CoreWait
{
    public static readonly TimeSpan Limit = TimeSpan.FromMinutes(15);

    public static string FilePath(AppPaths p) => Path.Combine(p.Root, "core.wait");

    public static bool Exists(AppPaths p) => File.Exists(FilePath(p));

    /// <summary>Поставить метку (установка помощника, ядра ещё нет).</summary>
    public static void Begin(AppPaths p, DateTime utcNow)
    {
        Directory.CreateDirectory(p.Root);
        File.WriteAllText(FilePath(p), utcNow.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
    }

    /// <summary>Сколько ещё ждать окна; метки нет, она истекла или не читается — null (качать сразу).</summary>
    public static TimeSpan? Remaining(AppPaths p, DateTime utcNow)
    {
        try
        {
            if (!File.Exists(FilePath(p))) return null;
            if (!DateTime.TryParse(File.ReadAllText(FilePath(p)).Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)) return null;
            var left = at.ToUniversalTime() + Limit - utcNow.ToUniversalTime();
            if (left <= TimeSpan.Zero) return null;
            // Часы перевели назад — дольше предела всё равно не ждём.
            return left > Limit ? Limit : left;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public static void Clear(AppPaths p)
    {
        try { if (File.Exists(FilePath(p))) File.Delete(FilePath(p)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
