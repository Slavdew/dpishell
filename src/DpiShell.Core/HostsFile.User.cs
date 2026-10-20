using DpiShell.Core.Localization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DpiShell.Core;

/// <summary>Своя строка пользователя, записанная нами: номер строки, имя, адрес, дата и источник из тега <c>dpishell-user</c>.</summary>
public sealed record HostsUserLine(int Line, string Host, string Ip, string Date, string Source);

/// <summary>Чужая строка, которую мы закомментировали: <c># DpiShell off &lt;дата&gt;: &lt;исходная строка&gt;</c>.</summary>
public sealed record HostsOffLine(int Line, string Date, string Original);

/// <summary>Чужая строка hosts (вне наших блоков) с записями «адрес имя». <see cref="Id"/> = «номер:хеш текста» — по нему строку называют помощнику.</summary>
public sealed record HostsForeignLine(int Line, string Id, string Text, IReadOnlyList<HostsEntry> Entries);

/// <summary>План правки файла: что вышло и, если файл меняется, какими байтами заменить (<see cref="OriginalBytes"/> — чтобы поймать чужую правку в промежутке).</summary>
public sealed record HostsPlan<T>(T Info, bool Changed)
{
    internal byte[] OriginalBytes { get; init; } = [];
    internal byte[] NewBytes { get; init; } = [];
}

public sealed record HostsUserChange(IReadOnlyList<HostsEntry> Added, IReadOnlyList<HostsEntry> Removed);

/// <param name="Skipped">Строки, которые не тронуты, с причиной: «номер:хеш — почему».</param>
public sealed record HostsCommentChange(IReadOnlyList<HostsOffLine> Commented, IReadOnlyList<string> Skipped);

public sealed record HostsRestoreChange(IReadOnlyList<HostsEntry> RemovedBlock, IReadOnlyList<HostsEntry> RemovedUser, int Uncommented);

/// <summary>
/// Свои строки пользователя и комментирование чужих (ТЗ 12, «Запись своих строк»). Всё, что мы пишем, узнаётся по тексту строки,
/// а не по месту в файле: блок <c># DpiShell user begin … end</c>, тег <c># dpishell-user &lt;дата&gt; &lt;источник&gt;</c> на каждой строке
/// и пометка <c># DpiShell off &lt;дата&gt;: …</c> на закомментированных. Источник пишется ASCII-словом (<c>manual</c>): файл без BOM
/// читается как Latin-1, кириллица в нём превратилась бы в «?».
/// </summary>
public sealed partial class HostsText
{
    public const string UserBeginMark = "# DpiShell user begin";
    public const string UserEndMark = "# DpiShell user end";
    public const string OffMark = "# DpiShell off";
    public const string UserTag = "dpishell-user";
    public const string ManualSource = "manual";
    const string UserNote = "# Managed by DpiShell (own lines). Every line below is tagged dpishell-user; the app can remove them.";

    [GeneratedRegex(@"#\s*dpishell-user\s+(\d{4}-\d{2}-\d{2})(?:\s+(\S+))?", RegexOptions.IgnoreCase)]
    private static partial Regex TagRx();

    [GeneratedRegex(@"^# DpiShell off (\d{4}-\d{2}-\d{2}): (.*)$")]
    private static partial Regex OffRx();

    [GeneratedRegex(@"^(\d{1,6}):([0-9a-f]{12})$")]
    private static partial Regex IdRx();

    /// <summary>Короткий отпечаток текста строки (без перевода строки): 12 hex-знаков sha256.</summary>
    public static string LineHash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];

    public static string LineId(int line, string text) => $"{line}:{LineHash(text)}";

    /// <summary>«номер:хеш» → пара; иначе false.</summary>
    public static bool TryParseId(string id, out int line, out string hash)
    {
        line = 0; hash = "";
        var m = IdRx().Match(id);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out line) || line < 1) return false;
        hash = m.Groups[2].Value;
        return true;
    }

    static bool IsUserMark(string t)
    {
        var s = t.Trim();
        return s.StartsWith(UserBeginMark, StringComparison.OrdinalIgnoreCase) || s.StartsWith(UserEndMark, StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("# Managed by DpiShell (own lines)", StringComparison.OrdinalIgnoreCase);
    }

    static bool IsUserLine(string t) => TagRx().IsMatch(t) && ParseLine(t).Any();

    bool IsUserOwned(int i) => IsUserMark(_lines[i].Text) || IsUserLine(_lines[i].Text);

    static bool IsOff(string t) => OffRx().IsMatch(t);

    /// <summary>Свои строки пользователя по порядку файла (где бы они ни лежали — тег держит их при переносе).</summary>
    public IReadOnlyList<HostsUserLine> UserLines()
    {
        var list = new List<HostsUserLine>();
        for (int i = 0; i < _lines.Count; i++)
        {
            if (!IsUserLine(_lines[i].Text)) continue;
            var m = TagRx().Match(_lines[i].Text);
            var source = m.Groups[2].Success ? m.Groups[2].Value : ManualSource;
            foreach (var e in ParseLine(_lines[i].Text)) list.Add(new HostsUserLine(i + 1, e.Host, e.Ip, m.Groups[1].Value, source));
        }
        return list;
    }

    public IReadOnlyList<HostsOffLine> OffLines()
    {
        var list = new List<HostsOffLine>();
        for (int i = 0; i < _lines.Count; i++)
            if (OffRx().Match(_lines[i].Text) is { Success: true } m) list.Add(new HostsOffLine(i + 1, m.Groups[1].Value, m.Groups[2].Value));
        return list;
    }

    /// <summary>
    /// Чужие строки с записями для аудита: вне блока белого списка, вне своих строк, не закомментированные нами.
    /// Имена без точки (localhost), маски и «имя:порт» в записи не попадают.
    /// </summary>
    public IReadOnlyList<HostsForeignLine> ForeignLines()
    {
        var own = OwnLines(out _, out _).ToHashSet();
        var list = new List<HostsForeignLine>();
        for (int i = 0; i < _lines.Count; i++)
        {
            var t = _lines[i].Text;
            if (own.Contains(i) || IsUserOwned(i) || IsOff(t)) continue;
            var entries = ParseLine(t).Where(e => IsPlainName(e.Host)).Distinct().ToList();
            if (entries.Count > 0) list.Add(new HostsForeignLine(i + 1, LineId(i + 1, t), t, entries));
        }
        return list;
    }

    static bool IsPlainName(string host) => !host.Contains(':') && !host.Contains('/') && !host.Contains('*') && HostsInput.CleanHost(host) == host;

    /// <summary>Номера наших строк: блок белого списка и свои строки (для «Вернуть как было»).</summary>
    HashSet<int> AllOwn()
    {
        var own = OwnLines(out _, out _).ToHashSet();
        for (int i = 0; i < _lines.Count; i++)
            if (IsUserOwned(i)) own.Add(i);
        return own;
    }

    /// <summary>Тот же перевод строки, что у файла; нет перевода у последней строки — дописываем, иначе наша строка прилипнет к ней.</summary>
    void FixLastEol(List<(string Text, string Eol)> result)
    {
        if (result.Count > 0 && result[^1].Eol == "") result[^1] = (result[^1].Text, Newline);
    }

    static string Day(DateTime at) => at.ToString("yyyy-MM-dd");

    /// <summary>
    /// Поставить ровно эти свои строки (пустой список — убрать их все). Прежние строки сохраняют дату и источник.
    /// Блок стоит на месте прежнего или в конце файла; строки, которые пользователь вписал между рамками сам, остаются на месте.
    /// </summary>
    public HostsPlan<HostsUserChange> PlanUserSet(IEnumerable<HostsEntry> wanted, DateTime at, string source = ManualSource)
    {
        var want = wanted.Select(e => new HostsEntry(HostsRules.Normalize(e.Host), e.Ip.Trim())).Distinct().ToList();
        var old = UserLines();
        var oldEntries = old.Select(l => new HostsEntry(l.Host, l.Ip)).Distinct().ToList();
        var added = want.Where(e => !oldEntries.Contains(e)).ToList();
        var removed = oldEntries.Where(e => !want.Contains(e)).ToList();
        var original = Encode();

        var ownIdx = Enumerable.Range(0, _lines.Count).Where(IsUserOwned).ToList();
        bool intact = want.Count == 0 ? ownIdx.Count == 0 : UserBlockIntact(ownIdx);
        if (added.Count == 0 && removed.Count == 0 && intact)
            return new HostsPlan<HostsUserChange>(new([], []), false) { OriginalBytes = original, NewBytes = original };

        var block = new List<string>();
        if (want.Count > 0)
        {
            block.Add($"{UserBeginMark} {Day(at)}");
            block.Add(UserNote);
            foreach (var e in want)
            {
                var prev = old.FirstOrDefault(l => l.Host == e.Host && l.Ip == e.Ip);
                block.Add($"{e.Ip} {e.Host}  # {UserTag} {prev?.Date ?? Day(at)} {prev?.Source ?? source}");
            }
            block.Add(UserEndMark);
        }

        var own = ownIdx.ToHashSet();
        var result = new List<(string Text, string Eol)>();
        bool placed = false;
        for (int i = 0; i < _lines.Count; i++)
        {
            if (!own.Contains(i)) { result.Add(_lines[i]); continue; }
            if (placed || block.Count == 0) continue;
            result.AddRange(block.Select(l => (l, Newline)));
            placed = true;
        }
        if (!placed && block.Count > 0)
        {
            FixLastEol(result);
            result.AddRange(block.Select(l => (l, Newline)));
        }
        return new HostsPlan<HostsUserChange>(new(added, removed), true) { OriginalBytes = original, NewBytes = Encode(result) };
    }

    /// <summary>Рамка цела: подряд идут begin, пометка, строки с тегом, end — и ничего другого.</summary>
    bool UserBlockIntact(List<int> own)
    {
        if (own.Count < 4 || own[^1] - own[0] != own.Count - 1) return false;
        var t = own.Select(i => _lines[i].Text.Trim()).ToList();
        return t[0].StartsWith(UserBeginMark, StringComparison.OrdinalIgnoreCase) && t[1] == UserNote
            && t[^1].StartsWith(UserEndMark, StringComparison.OrdinalIgnoreCase)
            && t.Skip(2).Take(t.Count - 3).All(s => TagRx().IsMatch(s));
    }

    /// <summary>
    /// Закомментировать выбранные чужие строки: <c># DpiShell off &lt;дата&gt;: &lt;строка как была&gt;</c>. Строка берётся по «номер:хеш»;
    /// не совпал хеш (файл поменялся), это наша строка или в ней нет записи — пропуск с причиной, остальные идут дальше.
    /// </summary>
    public HostsPlan<HostsCommentChange> PlanComment(IEnumerable<string> ids, DateTime at)
    {
        var original = Encode();
        var foreign = ForeignLines().ToDictionary(l => l.Line);
        var result = _lines.ToList();
        var done = new List<HostsOffLine>();
        var skipped = new List<string>();
        foreach (var id in ids.Distinct())
        {
            if (!TryParseId(id, out var line, out var hash)) { skipped.Add(L.T("core.hosts.skip.bad_id", new { id })); continue; }
            if (line > _lines.Count || LineHash(_lines[line - 1].Text) != hash) { skipped.Add(L.T("core.hosts.skip.changed", new { id })); continue; }
            if (!foreign.ContainsKey(line)) { skipped.Add(L.T("core.hosts.skip.not_foreign", new { id })); continue; }
            var text = _lines[line - 1].Text;
            result[line - 1] = ($"{OffMark} {Day(at)}: {text}", _lines[line - 1].Eol);
            done.Add(new HostsOffLine(line, Day(at), text));
        }
        return new HostsPlan<HostsCommentChange>(new(done, skipped), done.Count > 0) { OriginalBytes = original, NewBytes = Encode(result) };
    }

    /// <summary>«Вернуть закомментированные»: каждая строка <c># DpiShell off …: X</c> снова становится X, байт в байт.</summary>
    public HostsPlan<IReadOnlyList<HostsOffLine>> PlanUncomment()
    {
        var original = Encode();
        var off = OffLines();
        var result = _lines.ToList();
        foreach (var o in off) result[o.Line - 1] = (o.Original, result[o.Line - 1].Eol);
        return new HostsPlan<IReadOnlyList<HostsOffLine>>(off, off.Count > 0) { OriginalBytes = original, NewBytes = Encode(result) };
    }

    /// <summary>
    /// «Вернуть как было»: убрать блок белого списка и свои строки (рамка и все строки с тегом), раскомментировать закомментированные нами.
    /// Остальное — строки пользователя и других программ, до и после нас — остаётся теми же байтами.
    /// </summary>
    public HostsPlan<HostsRestoreChange> PlanRestoreAll()
    {
        var original = Encode();
        var blockEntries = Block().Entries;
        var userEntries = UserLines().Select(l => new HostsEntry(l.Host, l.Ip)).Distinct().ToList();
        var own = AllOwn();
        var off = OffLines().ToDictionary(o => o.Line - 1);
        var result = new List<(string Text, string Eol)>();
        for (int i = 0; i < _lines.Count; i++)
        {
            if (own.Contains(i)) continue;
            result.Add(off.TryGetValue(i, out var o) ? (o.Original, _lines[i].Eol) : _lines[i]);
        }
        bool changed = own.Count > 0 || off.Count > 0;
        return new HostsPlan<HostsRestoreChange>(new(blockEntries, userEntries, off.Count), changed) { OriginalBytes = original, NewBytes = changed ? Encode(result) : original };
    }
}
