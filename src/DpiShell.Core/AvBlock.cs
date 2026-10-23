using System.ComponentModel;
using DpiShell.Core.Localization;

namespace DpiShell.Core;

/// <summary>Обнаружение Защитника (MSFT_MpThreatDetection + имя из MSFT_MpThreat): угроза, затронутые файлы, когда найдена.</summary>
public sealed record DefenderThreat(string Name, IReadOnlyList<string> Resources, DateTime? DetectedUtc);

/// <summary>
/// Антивирус оборвал действие (ТЗ av-exclusion, дополнение 2): файл удалён или заблокирован во время работы. Общий вид сообщения —
/// «&lt;что&gt; удалил или заблокировал Защитник: &lt;угроза&gt; — &lt;файл&gt;», если журнал Защитника это подтверждает; иначе — «возможно, антивирус»,
/// без утверждений. Кнопка исправления — карточка ядра на Главной («Добавить в исключения и восстановить» / «Восстановить»).
/// </summary>
public static class AvBlock
{
    /// <summary>ERROR_VIRUS_INFECTED: «Операция не завершена: файл содержит вирус».</summary>
    public const int VirusInfected = 225;
    /// <summary>ERROR_VIRUS_DELETED: «…файл содержит вирус и удалён».</summary>
    public const int VirusDeleted = 226;

    static int Code(Exception e) => e switch
    {
        Win32Exception w => w.NativeErrorCode,
        _ => e.HResult & 0xFFFF,
    };

    /// <summary>Windows прямо говорит, что файл заблокирован антивирусом (225/226).</summary>
    public static bool IsVirusError(Exception e) => e is Win32Exception or IOException && Code(e) is VirusInfected or VirusDeleted;

    /// <summary>
    /// Файл, который только что был, пропал или закрыт: коды антивируса, «не найден», «нет доступа». Вызывающий решает, что файл был на месте
    /// (иначе «не найден» — обычная ошибка).
    /// </summary>
    public static bool IsVanished(Exception e) =>
        IsVirusError(e) || e is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException
        || e is Win32Exception { NativeErrorCode: 2 or 3 or 5 };

    /// <summary>Свежее обнаружение Защитника, задевшее этот путь (или папку внутри него); не узнать (нет прав, WMI, другой антивирус) — null.</summary>
    public static DefenderThreat? FindThreat(IDefenderSystem? defender, string path, DateTime sinceUtc)
    {
        if (defender is null) return null;
        IReadOnlyList<DefenderThreat> all;
        try { all = defender.Threats(); }
        catch (Exception e) when (AvExclusions.IsWmiError(e) || e is InvalidOperationException) { return null; }
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return all.Where(t => t.DetectedUtc is not { } at || at >= sinceUtc.AddMinutes(-2))
            .OrderByDescending(t => t.DetectedUtc ?? DateTime.MinValue)
            .FirstOrDefault(t => t.Resources.Any(r => Touches(r, full)));
    }

    /// <summary>Ресурс обнаружения («file:_C:\…», «containerfile:_C:\…\x.zip», иногда «…->(Zip)…») касается пути: тот же файл или файл внутри папки.</summary>
    public static bool Touches(string resource, string fullPath)
    {
        var r = resource;
        int colon = r.IndexOf(":_", StringComparison.Ordinal);
        if (colon >= 0) r = r[(colon + 2)..];
        int arrow = r.IndexOf("->", StringComparison.Ordinal);
        if (arrow >= 0) r = r[..arrow];
        r = Path.TrimEndingDirectorySeparator(r.Trim());
        return r.Equals(fullPath, StringComparison.OrdinalIgnoreCase)
            || r.StartsWith(fullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Действие оборвал антивирус (<see cref="AvBlock"/>). <see cref="Threat"/> — имя угрозы, если Защитник подтвердил.</summary>
public sealed class AvBlockedException : Exception
{
    /// <summary>Что пропало — словами («Архив ядра», «winws.exe»).</summary>
    public string What { get; }
    public string FilePath { get; }
    public string? Threat { get; }

    public AvBlockedException(string what, string path, Exception? inner = null, string? threat = null)
        : base(Text(what, path, threat), inner)
    {
        What = what; FilePath = path; Threat = threat;
    }

    static string Text(string what, string path, string? threat) => threat is { Length: > 0 }
        ? L.T("core.av.blocked_by", new { what, threat, file = path })
        : L.T("core.av.blocked_maybe", new { what, file = path });

    /// <summary>Свериться с журналом Защитника: подтвердил — то же с именем угрозы; нет — как есть («возможно, антивирус»).</summary>
    public AvBlockedException Confirm(IDefenderSystem? defender, DateTime sinceUtc) =>
        Threat is null && AvBlock.FindThreat(defender, FilePath, sinceUtc) is { } t ? new(What, FilePath, InnerException, t.Name) : this;
}
