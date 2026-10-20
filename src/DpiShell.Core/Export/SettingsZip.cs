using System.IO.Compression;
using System.Text.RegularExpressions;
using DpiShell.Core.Localization;

namespace DpiShell.Core.Export;

/// <summary>
/// Пакет настроек в архиве .zip (docs/settings-export.md «Архив .zip»): папка <c>dpishell-settings-&lt;дата&gt;/</c> внутри или файлы прямо в корне.
/// Архив — такой же чужой файл, как и папка: он распаковывается только во временную папку и только то, что нужно, с теми же пределами на размер файла
/// и число файлов; от «zip-бомбы» защищают пределы на распакованный размер (читается не больше предела + 1 байт на файл, не больше
/// <see cref="MaxTotalBytes"/> всего). Пути с «..», абсолютные, с диском, потоком данных и ссылки — архив не принимается целиком.
/// Дальше распакованная папка читается обычным <see cref="SettingsImport.Read"/>.
/// </summary>
public static partial class SettingsZip
{
    /// <summary>Записей в архиве (папки тоже): файлов в пакете не больше ~150, остальное — запас.</summary>
    public const int MaxEntries = 300;
    /// <summary>Размер самого файла архива.</summary>
    public const long MaxArchiveBytes = 8L * 1024 * 1024;
    /// <summary>Всего распаковано. Самый полный пакет — около 3 МБ (предельные подсети и стратегии).</summary>
    public const long MaxTotalBytes = 4L * 1024 * 1024;

    [GeneratedRegex(@"^[A-Za-z]:")]
    private static partial Regex DriveRx();

    /// <summary>Распаковать во временную папку и прочитать как обычный пакет. Временная папка удаляется в любом случае.</summary>
    public static ParsedPackage Read(string zipPath)
    {
        var pkg = new ParsedPackage { Folder = zipPath };
        var tmp = Path.Combine(Path.GetTempPath(), "dpishell-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            var problems = new List<SectionProblem>();
            var fatal = Extract(zipPath, tmp, problems);
            if (fatal is not null) { pkg.Fatal = fatal; return pkg; }
            var read = SettingsImport.Read(tmp);
            read.Folder = zipPath;
            read.Problems.InsertRange(0, problems);
            return read;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            pkg.Fatal = L.T("export.zip.unreadable", new { reason = e.Message });
            return pkg;
        }
        finally
        {
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Предел распакованного размера файла по его месту в пакете (те же, что у папки; больше — читающий отклонит файл как слишком большой).</summary>
    static int LimitFor(string rel)
    {
        if (rel.StartsWith(SettingsSections.StrategiesDir + "/", StringComparison.Ordinal)) return SettingsSections.StrategyBytes;
        return rel switch
        {
            SettingsSections.ManifestFile => SettingsSections.MaxManifestBytes,
            SettingsSections.SitesFile or SettingsSections.OptimizeFile or SettingsSections.SitesImportantFile => SettingsSections.SitesBytes,
            SettingsSections.ExcludeFile => SettingsSections.ExcludeBytes,
            SettingsSections.SubnetsFile or SettingsSections.SubnetsExcludeFile => SettingsSections.SubnetsBytes,
            SettingsSections.HostsFile => SettingsSections.HostsBytes,
            _ => SettingsSections.JsonBytes,
        };
    }

    /// <summary>Причина, по которой путь в архиве недопустим (архив не принимается), или null.</summary>
    static string? BadPath(string name)
    {
        if (name.Length == 0 || name.Length > 260 || name.Contains('\0')) return "bad";
        var n = name.Replace('\\', '/');
        if (n.StartsWith('/') || DriveRx().IsMatch(n)) return "bad";
        foreach (var seg in n.Split('/'))
            if (seg == ".." || seg.Contains(':')) return "bad";
        return null;
    }

    /// <summary>Ссылка (symlink) по атрибутам записи: unix-режим в старших 16 битах или признак reparse point в DOS-атрибутах.</summary>
    static bool IsLink(ZipArchiveEntry e)
    {
        int attr = e.ExternalAttributes;
        return ((attr >> 16) & 0xF000) == 0xA000 || (attr & 0x400) != 0;
    }

    /// <summary>Распаковать нужное в <paramref name="dest"/>. Возврат — причина отказа всему архиву (null — распаковано).</summary>
    static string? Extract(string zipPath, string dest, List<SectionProblem> problems)
    {
        var info = new FileInfo(zipPath);
        if (!info.Exists) return L.T("export.pkg.no_folder");
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return L.T("export.file.link", new { file = info.Name });
        if (info.Length > MaxArchiveBytes) return L.T("export.zip.too_big", new { max = MaxArchiveBytes / 1024 / 1024 });
        try
        {
            using var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            if (zip.Entries.Count > MaxEntries) return L.T("export.zip.too_many", new { max = MaxEntries });

            // Сначала проверка всех путей и ссылок: плохой архив не распаковывается вообще.
            var files = new List<(ZipArchiveEntry Entry, string Path)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in zip.Entries)
            {
                if (BadPath(e.FullName) is not null) return L.T("export.zip.bad_path", new { name = SettingsClean.Cut(e.FullName) });
                if (IsLink(e)) return L.T("export.zip.link", new { name = SettingsClean.Cut(e.FullName) });
                var path = e.FullName.Replace('\\', '/');
                if (path.EndsWith('/')) continue;   // папка
                if (!seen.Add(path)) return L.T("export.zip.dup", new { name = SettingsClean.Cut(path) });
                files.Add((e, path));
            }

            // Где лежит manifest.json: в корне или в единственной папке верхнего уровня.
            string prefix;
            if (files.Any(f => f.Path == SettingsSections.ManifestFile)) prefix = "";
            else
            {
                var tops = files.Where(f => f.Path.Count(c => c == '/') == 1 && f.Path.EndsWith("/" + SettingsSections.ManifestFile, StringComparison.Ordinal))
                    .Select(f => f.Path[..(f.Path.IndexOf('/') + 1)]).Distinct().ToList();
                if (tops.Count != 1) return L.T("export.pkg.no_manifest");
                prefix = tops[0];
            }

            Directory.CreateDirectory(dest);
            long total = 0;
            foreach (var (entry, path) in files)
            {
                if (!path.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var rel = path[prefix.Length..];
                var parts = rel.Split('/');
                bool nested = parts.Length == 2 && parts[0] is SettingsSections.StrategiesDir or SettingsSections.AppsDir;
                if (parts.Length != 1 && !nested) continue;   // чужие вложенные папки не читаются, как и в обычной папке
                if (parts.Any(p => p.Length == 0)) continue;
                if (!SettingsClean.IsSafeName(parts[^1]))
                {
                    if (nested) problems.Add(new(parts[0], rel, L.T("export.name.bad", new { name = SettingsClean.Cut(parts[^1]) })));
                    continue;
                }
                int limit = LimitFor(rel);
                var target = Path.Combine([dest, .. parts]);
                if (nested) Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var input = entry.Open();
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buf = new byte[16 * 1024];
                long written = 0;
                int n;
                // Не больше предела + 1 байт: дальше читающий скажет «файл слишком большой», а распаковка не уходит в бесконечность.
                while (written <= limit && (n = input.Read(buf, 0, (int)Math.Min(buf.Length, limit + 1 - written))) > 0)
                {
                    output.Write(buf, 0, n);
                    written += n;
                    total += n;
                    if (total > MaxTotalBytes) return L.T("export.zip.unpacked_big", new { max = MaxTotalBytes / 1024 / 1024 });
                }
            }
            return null;
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or ArgumentException)
        {
            return L.T("export.zip.bad");
        }
    }
}
