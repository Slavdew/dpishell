using System.Text.RegularExpressions;

namespace DpiShell.Core;

/// <summary>
/// Запуск прямо из архива: двойной щелчок по exe внутри zip в Проводнике распаковывает только его в %TEMP%\Temp*_*.zip\ — папки data рядом нет.
/// Только определение; сообщение показывает окно при старте (без кнопок и скачиваний).
/// </summary>
public static partial class ArchiveRun
{
    /// <summary>Рядом с exe нет папки data.</summary>
    public static bool DataMissing(string baseDir) => !Directory.Exists(Path.Combine(baseDir, "data"));

    /// <summary>Путь вида %TEMP%\Temp1_DpiShell.zip\ — Проводник открыл архив как папку.</summary>
    public static bool LooksLikeArchive(string path) => ArchiveRx().IsMatch(path ?? "");

    [GeneratedRegex(@"[\\/]Temp[^\\/]*\.zip[\\/]", RegexOptions.IgnoreCase)]
    private static partial Regex ArchiveRx();
}
