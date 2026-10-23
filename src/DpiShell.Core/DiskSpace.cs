using System.Text.RegularExpressions;
using DpiShell.Core.Localization;

namespace DpiShell.Core;

/// <summary>Не хватило места на диске: распознать IOException и собрать/разобрать понятное сообщение (оно доходит до окна текстом, в том числе от помощника).</summary>
public static partial class DiskSpace
{
    /// <summary>ERROR_DISK_FULL (112) и ERROR_HANDLE_DISK_FULL (39) — младшие 16 бит HResult (0x80070070, 0x80070027).</summary>
    public static bool IsDiskFull(Exception e) => e is IOException && (e.HResult & 0xFFFF) is 112 or 39;

    /// <summary>«C:» — диск, на котором лежит путь; не определить — пусто.</summary>
    public static string DiskOf(string path)
    {
        try { return Path.GetPathRoot(Path.GetFullPath(path))?.TrimEnd('\\', '/') ?? ""; }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return ""; }
    }

    /// <summary>Сообщение без размера (сколько нужно — неизвестно).</summary>
    public static string Message(string disk) => L.T("core.disk.full", new { disk });

    [GeneratedRegex(@"(?i)(?:на диске|on disk)\s+([A-Za-z]:)")]
    private static partial Regex DiskRx();
    [GeneratedRegex(@"(?i)(?:нужно около|about)\s+(\d+)")]
    private static partial Regex NeedRx();

    /// <summary>Текст ошибки — «не хватает места» (наше сообщение, любой язык): диск и, если названо, сколько МБ нужно.</summary>
    public static bool TryParse(string? text, out string disk, out long? needMb)
    {
        disk = ""; needMb = null;
        if (string.IsNullOrEmpty(text)) return false;
        var m = DiskRx().Match(text);
        if (!m.Success || !text.Contains("Not enough space", StringComparison.OrdinalIgnoreCase) && !text.Contains("Не хватает места", StringComparison.OrdinalIgnoreCase))
            return false;
        disk = m.Groups[1].Value;
        if (NeedRx().Match(text) is { Success: true } n && long.TryParse(n.Groups[1].Value, out var v)) needMb = v;
        return true;
    }
}
