using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace DpiShell.Core;

/// <summary>Строка hosts вне нашего блока, которая касается домена из белого списка: она сработает раньше нашей (ТЗ v2 §3 п. 6).</summary>
public sealed record HostsConflict(int Line, string Host, string Ip, string Text);

/// <summary>Наш блок в файле: даты/версия из строки begin, записи, повреждён ли (begin без end, лишний end, несколько блоков).</summary>
public sealed record HostsBlockInfo(bool Present, bool Damaged, string? Header, IReadOnlyList<HostsEntry> Entries);

/// <summary>Что изменится в hosts. <see cref="Changed"/> = false — файл не трогаем вовсе.</summary>
public sealed record HostsPreview(
    bool Changed,
    IReadOnlyList<HostsEntry> Added,
    IReadOnlyList<HostsEntry> Removed,
    IReadOnlyList<string> BlockLines,
    IReadOnlyList<HostsConflict> Conflicts,
    HostsBlockInfo Before)
{
    internal byte[] OriginalBytes { get; init; } = [];
    internal byte[] NewBytes { get; init; } = [];
}

/// <summary>
/// Текст hosts как последовательность строк с их собственными переводами строк. Всё вне нашего блока при сборке
/// возвращается теми же байтами: файл без BOM читается как Latin-1 (байт = символ), UTF-8/UTF-16 с BOM — своей кодировкой.
/// </summary>
public sealed class HostsText
{
    public const string BeginMark = "# DpiShell begin";
    public const string EndMark = "# DpiShell end";
    public const int FormatVersion = 1;
    const string Note = "# Managed by DpiShell (Fix addresses). Lines inside this block are rewritten automatically.";

    readonly Encoding _enc;
    readonly byte[] _preamble;
    readonly List<(string Text, string Eol)> _lines;

    /// <summary>Перевод строки файла: первый встреченный; в пустом/однострочном файле — CRLF, как принято в Windows.</summary>
    public string Newline { get; }

    HostsText(Encoding enc, byte[] preamble, List<(string, string)> lines, string newline)
    {
        _enc = enc; _preamble = preamble; _lines = lines; Newline = newline;
    }

    public static HostsText Decode(byte[] bytes)
    {
        Encoding enc; int skip;
        // UTF-8 (с BOM и без) тоже идёт через Latin-1: наш блок — ASCII, а чужие байты переживают круг без потерь даже битыми.
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) { enc = Encoding.Latin1; skip = 3; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) { enc = new UnicodeEncoding(false, false); skip = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) { enc = new UnicodeEncoding(true, false); skip = 2; }
        else { enc = Encoding.Latin1; skip = 0; }
        var text = enc.GetString(bytes, skip, bytes.Length - skip);

        var lines = new List<(string, string)>();
        string? newline = null;
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n' && text[i] != '\r') continue;
            var eol = text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? "\r\n" : text[i].ToString();
            lines.Add((text[start..i], eol));
            newline ??= eol;
            i += eol.Length - 1;
            start = i + 1;
        }
        if (start < text.Length) lines.Add((text[start..], ""));
        return new HostsText(enc, bytes[..skip], lines, newline ?? "\r\n");
    }

    public byte[] Encode() => Encode(_lines);

    byte[] Encode(IEnumerable<(string Text, string Eol)> lines)
    {
        var sb = new StringBuilder();
        foreach (var (t, e) in lines) sb.Append(t).Append(e);
        return [.. _preamble, .. _enc.GetBytes(sb.ToString())];
    }

    static bool IsBegin(string line) => line.Trim().StartsWith(BeginMark, StringComparison.OrdinalIgnoreCase);
    static bool IsEnd(string line) => line.Trim().StartsWith(EndMark, StringComparison.OrdinalIgnoreCase);

    /// <summary>Разбор строки hosts: «адрес имя [имя…] [# комментарий]». Комментарии и мусор — пусто.</summary>
    public static IEnumerable<HostsEntry> ParseLine(string line)
    {
        var hash = line.IndexOf('#');
        var body = hash >= 0 ? line[..hash] : line;
        var parts = body.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !IPAddress.TryParse(parts[0], out _)) yield break;
        foreach (var h in parts.Skip(1)) yield return new HostsEntry(HostsRules.Normalize(h), parts[0]);
    }

    /// <summary>
    /// Индексы строк нашего блока. begin…end — блок целиком. begin без end — блок повреждён: наши только begin и идущие
    /// за ним подряд записи доменов белого списка (чужое после обрыва не трогаем). Одинокий end — тоже наш.
    /// </summary>
    List<int> OwnLines(out bool damaged, out string? header)
    {
        var own = new List<int>();
        damaged = false; header = null;
        int blocks = 0;
        for (int i = 0; i < _lines.Count; i++)
        {
            var t = _lines[i].Text;
            if (IsEnd(t)) { own.Add(i); damaged = true; continue; }   // end без begin
            if (!IsBegin(t)) continue;
            blocks++;
            header ??= t.Trim();
            int end = -1;
            for (int j = i + 1; j < _lines.Count && !IsBegin(_lines[j].Text); j++)
                if (IsEnd(_lines[j].Text)) { end = j; break; }
            if (end >= 0)
            {
                for (int j = i; j <= end; j++) own.Add(j);
                i = end;
                continue;
            }
            damaged = true;
            own.Add(i);
            int k = i + 1;
            for (; k < _lines.Count; k++)
            {
                var s = _lines[k].Text.Trim();
                var entries = ParseLine(s).ToList();
                bool ours = s.StartsWith("# Managed by DpiShell", StringComparison.OrdinalIgnoreCase)
                    || entries.Count > 0 && entries.All(e => HostsRules.IsAllowedHost(e.Host)) && !s.Contains('#');
                if (!ours) break;
                own.Add(k);
            }
            i = k - 1;
        }
        if (blocks > 1) damaged = true;
        return own;
    }

    public HostsBlockInfo Block()
    {
        var own = OwnLines(out var damaged, out var header);
        var entries = own.SelectMany(i => ParseLine(_lines[i].Text)).Distinct().ToList();
        return new HostsBlockInfo(own.Count > 0, damaged, header, entries);
    }

    /// <summary>Строки вне нашего блока для доменов белого списка (у них приоритет: Windows берёт первую подходящую строку).</summary>
    public IReadOnlyList<HostsConflict> Conflicts()
    {
        var own = OwnLines(out _, out _).ToHashSet();
        var list = new List<HostsConflict>();
        for (int i = 0; i < _lines.Count; i++)
        {
            if (own.Contains(i)) continue;
            foreach (var e in ParseLine(_lines[i].Text))
                if (HostsRules.IsAllowedHost(e.Host)) list.Add(new HostsConflict(i + 1, e.Host, e.Ip, _lines[i].Text.Trim()));
        }
        return list;
    }

    public static IReadOnlyList<string> BlockLines(IEnumerable<HostsEntry> entries, DateTime at) =>
    [
        $"{BeginMark} v{FormatVersion} {at:yyyy-MM-dd}",
        Note,
        .. entries.Select(e => $"{e.Ip} {e.Host}"),
        EndMark,
    ];

    /// <summary>
    /// Предпросмотр: убрать все наши строки и, если записи есть, поставить блок на место первого (или в конец файла).
    /// Те же записи в целом блоке — без изменений (не переписываем hosts ради новой даты: каждая запись — повод для Защитника).
    /// </summary>
    public HostsPreview Plan(IEnumerable<HostsEntry> entries, DateTime at)
    {
        var want = entries.Select(e => new HostsEntry(HostsRules.Normalize(e.Host), e.Ip.Trim())).Distinct().ToList();
        var before = Block();
        var own = OwnLines(out _, out _);
        var block = want.Count > 0 ? BlockLines(want, at) : [];
        var conflicts = Conflicts();
        var original = Encode();

        var oldSet = before.Entries.ToHashSet();
        var added = want.Where(e => !oldSet.Contains(e)).ToList();
        var removed = before.Entries.Where(e => !want.Contains(e)).ToList();
        bool same = added.Count == 0 && removed.Count == 0 && !before.Damaged &&
                    (want.Count > 0 ? before.Present && LooksIntact(own) : !before.Present);
        if (same)
            return new HostsPreview(false, [], [], block, conflicts, before) { OriginalBytes = original, NewBytes = original };

        var ownSet = own.ToHashSet();
        var result = new List<(string Text, string Eol)>();
        bool placed = false;
        for (int i = 0; i < _lines.Count; i++)
        {
            if (!ownSet.Contains(i)) { result.Add(_lines[i]); continue; }
            if (placed || block.Count == 0) continue;
            result.AddRange(block.Select(l => (l, Newline)));
            placed = true;
        }
        if (!placed && block.Count > 0)
        {
            // Последняя чужая строка без перевода — дописываем перевод, иначе блок прилипнет к ней.
            if (result.Count > 0 && result[^1].Eol == "") result[^1] = (result[^1].Text, Newline);
            result.AddRange(block.Select(l => (l, Newline)));
        }
        return new HostsPreview(true, added, removed, block, conflicts, before) { OriginalBytes = original, NewBytes = Encode(result) };
    }

    /// <summary>Блок без посторонних строк внутри (только наши заголовок, пометка и записи) — переписывать незачем.</summary>
    bool LooksIntact(List<int> own) => own.All(i =>
    {
        var s = _lines[i].Text.Trim();
        return IsBegin(s) || IsEnd(s) || s == Note || ParseLine(s).Any() && !s.Contains('#');
    });
}

/// <summary>Ошибка записи hosts: текст — для пользователя; файл не изменён.</summary>
public sealed class HostsWriteException(string message, string? backup, Exception? inner = null) : Exception(message, inner)
{
    public string? Backup { get; } = backup;
}

public sealed record HostsWriteResult(bool Changed, IReadOnlyList<HostsEntry> Added, IReadOnlyList<HostsEntry> Removed, string? Backup);

/// <summary>
/// Файл hosts на диске: чтение, атомарная запись (временный файл рядом + File.Replace), резервные копии в нашей папке,
/// снятие и возврат «только чтение», сброс кэша DNS. Путь — параметр: тесты работают только с копией во временной папке.
/// </summary>
public sealed class HostsFileStore(string hostsPath, string backupDir, Action? afterWrite = null)
{
    public const int KeepBackups = 10;
    public const string OriginalBackup = "hosts.original.bak";

    public string HostsPath { get; } = hostsPath;
    public string BackupDir { get; } = backupDir;

    /// <summary>%SystemRoot%\System32\drivers\etc\hosts.</summary>
    public static string SystemHostsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    public byte[] ReadBytes() => File.Exists(HostsPath) ? File.ReadAllBytes(HostsPath) : [];

    public HostsText Read() => HostsText.Decode(ReadBytes());

    public HostsPreview Preview(IEnumerable<HostsEntry> entries, DateTime? at = null) => Read().Plan(entries, at ?? DateTime.Now);

    /// <summary>Поставить ровно эти записи в наш блок (пустой список — убрать блок). Чужие строки не меняются.</summary>
    public HostsWriteResult Apply(IEnumerable<HostsEntry> entries, DateTime? at = null)
    {
        var list = entries.ToList();
        for (int attempt = 0; ; attempt++)
        {
            var plan = Preview(list, at);
            if (!plan.Changed) return new(false, [], [], null);
            try
            {
                var backup = Commit(plan.OriginalBytes, plan.NewBytes);
                return new(true, plan.Added, plan.Removed, backup);
            }
            catch (ConcurrentEditException) when (attempt == 0) { }   // файл поменяли между чтением и записью — план заново
            catch (ConcurrentEditException) { throw Busy(); }
        }
    }

    static HostsWriteException Busy() =>
        new("Не удалось записать hosts: файл одновременно меняет другая программа. Изменения не внесены, повторите позже.", null);

    /// <summary>«Вернуть как было»: удалить только наш блок.</summary>
    public HostsWriteResult RemoveBlock() => Apply([]);

    public IReadOnlyList<string> Backups() => Directory.Exists(BackupDir)
        ? [.. Dated(),
           .. Directory.GetFiles(BackupDir, OriginalBackup)]
        : [];

    /// <summary>Различия «сейчас → копия» построчно (для предпросмотра полного восстановления по явной просьбе).</summary>
    public (IReadOnlyList<string> Added, IReadOnlyList<string> Removed) DiffWithBackup(string backup)
    {
        var now = HostsText.Decode(ReadBytes()).Encode();
        var cur = Lines(now); var old = Lines(File.ReadAllBytes(backup));
        return (old.Except(cur).ToList(), cur.Except(old).ToList());
        static List<string> Lines(byte[] b) => Encoding.Latin1.GetString(b).Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
    }

    /// <summary>Восстановить файл целиком из копии — только по явной команде пользователя.</summary>
    public string? RestoreBackup(string backup)
    {
        var full = Path.GetFullPath(backup);
        if (!full.StartsWith(Path.GetFullPath(BackupDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Копия должна лежать в папке копий DpiShell.", nameof(backup));
        try { return Commit(ReadBytes(), File.ReadAllBytes(full)); }
        catch (ConcurrentEditException) { throw Busy(); }
    }

    sealed class ConcurrentEditException : Exception;

    /// <summary>Запись: копия → проверка, что файл не меняли → временный файл рядом → File.Replace → проверка чтением.</summary>
    string? Commit(byte[] expectedOld, byte[] newBytes)
    {
        string? backup = null;
        var dir = Path.GetDirectoryName(Path.GetFullPath(HostsPath))!;
        var tmp = Path.Combine(dir, Path.GetFileName(HostsPath) + ".dpishell.tmp");
        bool exists = File.Exists(HostsPath);
        FileAttributes? attrs = null;
        try
        {
            if (exists) backup = MakeBackup();
            if (!ReadBytes().AsSpan().SequenceEqual(expectedOld)) throw new ConcurrentEditException();

            if (exists)
            {
                attrs = File.GetAttributes(HostsPath);
                if (attrs.Value.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(HostsPath, attrs.Value & ~FileAttributes.ReadOnly);
            }
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(newBytes);
                fs.Flush(true);
            }
            if (exists) File.Replace(tmp, HostsPath, null, ignoreMetadataErrors: true);
            else File.Move(tmp, HostsPath);
        }
        catch (ConcurrentEditException) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new HostsWriteException(Explain(e, backup), backup, e);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            try { if (attrs is { } a && a.HasFlag(FileAttributes.ReadOnly) && File.Exists(HostsPath)) File.SetAttributes(HostsPath, File.GetAttributes(HostsPath) | FileAttributes.ReadOnly); } catch { }
        }

        // Защитник может отменить правку сразу (HostsFileHijack) — проверяем, что на диске то, что записали.
        if (!ReadBytes().AsSpan().SequenceEqual(newBytes))
            throw new HostsWriteException("Запись в hosts отменена — скорее всего, Защитником Windows (HostsFileHijack) или другой защитой. "
                + "Если Защитник показал предупреждение, выберите в нём «Разрешить» и повторите." + BackupNote(backup), backup);
        try { afterWrite?.Invoke(); } catch { }
        return backup;
    }

    static string BackupNote(string? backup) => backup is null ? "" : $" Копия файла: {backup}";

    static string Explain(Exception e, string? backup)
    {
        const int SharingViolation = 32, LockViolation = 33;
        var code = e.HResult & 0xFFFF;
        string why = e switch
        {
            // 1175–1177 — ReplaceFile не смог убрать/подменить файл: его держит другая программа.
            IOException when code is SharingViolation or LockViolation or 1175 or 1176 or 1177 =>
                "файл hosts занят другой программой (антивирус, блокировщик рекламы). Повторите через минуту",
            UnauthorizedAccessException when !IsElevated() => "нужны права администратора",
            UnauthorizedAccessException =>
                "запись запрещена — файл защищён Защитником Windows, другим антивирусом или политикой организации. " +
                "Исключения в антивирус DpiShell не добавляет",
            _ => e.Message.TrimEnd('.'),
        };
        return $"Не удалось записать hosts: {why}. Изменения не внесены." + BackupNote(backup);
    }

    static bool IsElevated()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    /// <summary>Копия перед правкой: hosts.original.bak — один раз, навсегда; hosts.yyyyMMdd-HHmmss.bak — последние 10.</summary>
    string MakeBackup()
    {
        Directory.CreateDirectory(BackupDir);
        var original = Path.Combine(BackupDir, OriginalBackup);
        if (!File.Exists(original)) File.Copy(HostsPath, original);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var file = Path.Combine(BackupDir, $"hosts.{stamp}.bak");
        for (int n = 2; File.Exists(file); n++) file = Path.Combine(BackupDir, $"hosts.{stamp}-{n:D3}.bak");
        File.Copy(HostsPath, file);
        foreach (var old in Dated().Skip(KeepBackups))
            try { File.Delete(old); } catch { }
        return file;
    }

    /// <summary>Копии с датой, новые первыми (имя без .bak: «…-120000» раньше «…-120000-002»).</summary>
    IEnumerable<string> Dated() => Directory.GetFiles(BackupDir, "hosts.2*.bak")
        .OrderByDescending(f => Path.GetFileNameWithoutExtension(f), StringComparer.Ordinal);

    public static string Sha256(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));
}

/// <summary>Сброс кэша DNS Windows (то же, что ipconfig /flushdns).</summary>
public static class DnsCache
{
    [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DnsFlushResolverCache();

    public static bool Flush()
    {
        try { return DnsFlushResolverCache(); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }
}
