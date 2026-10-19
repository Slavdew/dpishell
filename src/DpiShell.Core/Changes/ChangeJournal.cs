using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DpiShell.Core;

/// <summary>Где записан вид изменения (поле <c>kind</c> журнала). Новые виды добавляются сюда и регистрируются в <see cref="ChangeUndoEngine"/>.</summary>
public static class ChangeKinds
{
    public const string Service = "service";
    public const string TcpTimestamps = "tcp_timestamps";
    public const string Dns = "dns";
    public const string HostsBlock = "hosts_block";
    public const string HostsUser = "hosts_user";
    public const string HostsComment = "hosts_comment";
    public const string Core = "core";
    public const string AppProfile = "app_profile";
    // Журнал окна (%LocalAppData%): права администратора не нужны.
    public const string Autostart = "autostart";
    public const string RouterDns = "router_dns";
    public const string RouterEnable = "router_enable";
}

public enum ChangeState
{
    /// <summary>Записано, откатить можно.</summary>
    Active,
    /// <summary>Действие не состоялось (отказ, откат внутри самой функции) — откатывать нечего.</summary>
    Aborted,
    /// <summary>Откачено или признано уже откаченным.</summary>
    Undone,
    /// <summary>Откат сделала сама функция («Вернуть как было») — в списке не нужна.</summary>
    Closed,
}

/// <param name="Completed">Действие дописано до конца; false — запись есть, а подтверждения нет (оборвалось на середине): откат проверит, что стоит сейчас.</param>
public sealed record ChangeEntry(string Id, DateTime At, string Kind, string Title, JsonNode? Before, JsonNode? After, ChangeState State, bool Completed,
    string? Result = null, string? Note = null, int Order = 0);

/// <summary>
/// Куда программа пишет, что она меняет в системе. Запись делается ДО действия (<see cref="Begin"/>), затем подтверждается
/// (<see cref="Done"/>) или отменяется (<see cref="Abort"/>). Реализация по умолчанию — <see cref="NullRecorder"/>.
/// </summary>
public interface IChangeRecorder
{
    /// <summary>Что собираемся менять: прежнее значение (снимок) и то, что поставим. Возвращает id записи.</summary>
    string Begin(string kind, string title, JsonNode? before, JsonNode? after);
    /// <summary>Действие сделано; <paramref name="after"/> — уточнение того, что фактически стоит (null — как в Begin).</summary>
    void Done(string id, JsonNode? after = null);
    /// <summary>Действие не состоялось или откатилось внутри функции.</summary>
    void Abort(string id);
    /// <summary>Поменять «что поставили» у уже записанного (то же изменение правится повторно).</summary>
    void Update(string id, JsonNode? after);
    /// <summary>Функция сама вернула прежнее («Вернуть как было») — записи этого вида больше не нужны.</summary>
    void Close(string kind, string why, Func<ChangeEntry, bool>? filter = null);
    /// <summary>Незакрытые записи этого вида (для связки повторных правок).</summary>
    IReadOnlyList<ChangeEntry> Active(string kind);
}

public sealed class NullRecorder : IChangeRecorder
{
    public static readonly NullRecorder Instance = new();
    public string Begin(string kind, string title, JsonNode? before, JsonNode? after) => "";
    public void Done(string id, JsonNode? after = null) { }
    public void Abort(string id) { }
    public void Update(string id, JsonNode? after) { }
    public void Close(string kind, string why, Func<ChangeEntry, bool>? filter = null) { }
    public IReadOnlyList<ChangeEntry> Active(string kind) => [];
}

/// <summary>
/// Журнал изменений (<c>changes.jsonl</c>): одна строка JSON — одно событие, файл только дописывается. Недописанная последняя строка
/// (питание, обрыв) при чтении пропускается, при следующей записи файл сначала получает перевод строки. Запись сбрасывается на диск
/// до возврата из <see cref="Begin"/>. Ошибки записи не мешают самому действию (журнал — страховка, а не условие), но попадают в <see cref="Failed"/>.
/// </summary>
public sealed class ChangeJournal(string file) : IChangeRecorder
{
    /// <summary>После стольких строк журнал переписывается без законченных записей.</summary>
    public const int CompactAfterLines = 2000;

    readonly object _gate = new();
    public string File { get; } = file;
    /// <summary>Часы (UTC); тесты подставляют свои.</summary>
    public Func<DateTime> Now { get; init; } = () => DateTime.UtcNow;
    /// <summary>Последняя ошибка записи (null — всё писалось).</summary>
    public string? Failed { get; private set; }

    static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string Begin(string kind, string title, JsonNode? before, JsonNode? after)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        Append(new JsonObject
        {
            ["op"] = "do", ["id"] = id, ["at"] = Now().ToString("O"), ["kind"] = kind, ["title"] = title,
            ["before"] = before?.DeepClone(), ["after"] = after?.DeepClone(),
        });
        return id;
    }

    public void Done(string id, JsonNode? after = null)
    {
        if (id.Length == 0) return;
        Append(new JsonObject { ["op"] = "done", ["id"] = id, ["after"] = after?.DeepClone() });
    }

    public void Abort(string id)
    {
        if (id.Length > 0) Append(new JsonObject { ["op"] = "abort", ["id"] = id });
    }

    public void Update(string id, JsonNode? after)
    {
        if (id.Length > 0) Append(new JsonObject { ["op"] = "update", ["id"] = id, ["after"] = after?.DeepClone() });
    }

    /// <summary>Итог отката одной записи: restored / already / skipped.</summary>
    public void MarkUndone(string id, string result, string? note) =>
        Append(new JsonObject { ["op"] = "undone", ["id"] = id, ["at"] = Now().ToString("O"), ["result"] = result, ["note"] = note });

    public void Close(string kind, string why, Func<ChangeEntry, bool>? filter = null)
    {
        foreach (var e in Entries().Where(e => e.State == ChangeState.Active && e.Kind == kind && (filter?.Invoke(e) ?? true)))
            Append(new JsonObject { ["op"] = "closed", ["id"] = e.Id, ["why"] = why });
    }

    public IReadOnlyList<ChangeEntry> Active(string kind) => [.. Entries().Where(e => e.State == ChangeState.Active && e.Kind == kind)];

    /// <summary>Незавершённые записи (откатываются), по порядку записи.</summary>
    public IReadOnlyList<ChangeEntry> Active() => [.. Entries().Where(e => e.State == ChangeState.Active)];

    /// <summary>Все записи по порядку; битые и недописанные строки пропускаются.</summary>
    public IReadOnlyList<ChangeEntry> Entries()
    {
        lock (_gate) return Fold(ReadLines());
    }

    List<JsonObject> ReadLines()
    {
        var list = new List<JsonObject>();
        if (!System.IO.File.Exists(File)) return list;
        byte[] bytes;
        try
        {
            using var fs = new FileStream(File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            bytes = ms.ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Failed = e.Message; return list; }
        foreach (var raw in Encoding.UTF8.GetString(bytes).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            try { if (JsonNode.Parse(line) is JsonObject o && o["op"] is not null && o["id"] is not null) list.Add(o); }
            catch (JsonException) { /* оборванная строка */ }
        }
        return list;
    }

    static string? S(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static List<ChangeEntry> Fold(List<JsonObject> lines)
    {
        var map = new Dictionary<string, ChangeEntry>();
        var order = new List<string>();
        foreach (var o in lines)
        {
            var id = S(o, "id")!;
            switch (S(o, "op"))
            {
                case "do":
                    if (map.ContainsKey(id) || !DateTime.TryParse(S(o, "at"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var at)) break;
                    map[id] = new ChangeEntry(id, at.ToUniversalTime(), S(o, "kind") ?? "", S(o, "title") ?? "", o["before"]?.DeepClone(), o["after"]?.DeepClone(),
                        ChangeState.Active, false, Order: order.Count);
                    order.Add(id);
                    break;
                case "done" when map.TryGetValue(id, out var d):
                    map[id] = d with { Completed = true, After = o["after"]?.DeepClone() ?? d.After };
                    break;
                case "update" when map.TryGetValue(id, out var u):
                    map[id] = u with { After = o["after"]?.DeepClone() ?? u.After };
                    break;
                case "abort" when map.TryGetValue(id, out var a) && a.State == ChangeState.Active:
                    map[id] = a with { State = ChangeState.Aborted };
                    break;
                case "closed" when map.TryGetValue(id, out var c) && c.State == ChangeState.Active:
                    map[id] = c with { State = ChangeState.Closed, Note = S(o, "why") };
                    break;
                case "undone" when map.TryGetValue(id, out var x):
                    map[id] = x with { State = ChangeState.Undone, Result = S(o, "result"), Note = S(o, "note") };
                    break;
            }
        }
        return [.. order.Select(i => map[i])];
    }

    void Append(JsonObject line)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(File))!);
                using (var fs = new FileStream(File, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
                {
                    fs.Seek(0, SeekOrigin.End);
                    var text = line.ToJsonString(Json) + "\n";
                    var bytes = Encoding.UTF8.GetBytes(text);
                    // Прошлая запись оборвалась на середине строки — не склеиваем новую с обрывком.
                    if (fs.Length > 0)
                    {
                        fs.Seek(-1, SeekOrigin.End);
                        if (fs.ReadByte() != '\n') fs.WriteByte((byte)'\n');
                    }
                    fs.Write(bytes);
                    fs.Flush(true);
                }
                Failed = null;
                if (S(line, "op") != "do") MaybeCompact();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Failed = e.Message; }
        }
    }

    int _appends;

    /// <summary>Законченные записи (откачены, отменены, закрыты) уходят из файла: остаются только те, что ещё можно откатить.</summary>
    void MaybeCompact()
    {
        if (++_appends % 200 != 0) return;
        var lines = ReadLines();
        if (lines.Count < CompactAfterLines) return;
        var keep = Fold(lines).Where(e => e.State == ChangeState.Active).Select(e => e.Id).ToHashSet();
        var text = string.Concat(lines.Where(l => keep.Contains(S(l, "id")!)).Select(l => l.ToJsonString(Json) + "\n"));
        var tmp = File + ".tmp";
        System.IO.File.WriteAllText(tmp, text);
        System.IO.File.Move(tmp, File, overwrite: true);
    }
}
