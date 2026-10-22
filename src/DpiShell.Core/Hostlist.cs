namespace DpiShell.Core;

/// <summary>
/// Списки сайтов так, как их читает zapret (<c>--hostlist</c>): в списке сам домен или любой его родитель —
/// «example.com» в списке ловит и «www.example.com», и «a.b.example.com», но не «badexample.com»; «^example.com» — только сам домен.
/// </summary>
public static class Hostlist
{
    /// <summary>Домен или любой его родитель есть в наборе (строки набора — в нижнем регистре).</summary>
    public static bool Covers(string host, IReadOnlySet<string> list)
    {
        var d = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (list.Contains("^" + d)) return true;   // «^домен» — только сам домен, без поддоменов
        for (var s = d; s.Length > 0; s = s.IndexOf('.') is var i and >= 0 ? s[(i + 1)..] : "")
            if (list.Contains(s)) return true;
        return false;
    }

    /// <summary>То же для строк как есть (регистр и пробелы по краям не важны, «#…» и пустые — мимо).</summary>
    public static bool Covers(string host, IEnumerable<string> lines) => Covers(host, ToSet(lines));

    public static HashSet<string> ToSet(IEnumerable<string> lines) =>
        lines.Select(l => l.Trim().TrimEnd('.').ToLowerInvariant()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToHashSet();

    /// <summary>Файл списка; нет файла или его не прочитать — пусто.</summary>
    public static HashSet<string> Read(string file)
    {
        try { return File.Exists(file) ? ToSet(File.ReadLines(file)) : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }
}
