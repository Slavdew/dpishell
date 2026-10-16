using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DpiShell.Core.Helper;

/// <summary>Что помощнику нужно знать для проверки аргументов: допустимые id стратегий и групп целей.</summary>
public interface IHelperCatalog
{
    /// <summary>Каталог поставки + найденные глубоким поиском.</summary>
    IReadOnlyCollection<string> StrategyIds { get; }
    /// <summary>Группы целей поставки + "user" (свои адреса).</summary>
    IReadOnlyCollection<string> GroupIds { get; }
}

public enum ArgKind
{
    Bool,
    /// <summary>Id стратегии: только из каталога и найденных.</summary>
    StrategyId,
    /// <summary>Массив id групп целей (без повторов).</summary>
    GroupIds,
    /// <summary>Строка из фиксированного набора.</summary>
    Choice,
    /// <summary>Массив коротких строк (домены, подсети). Пути и кавычки запрещены.</summary>
    Lines,
    /// <summary>Содержимое IP-списка — одна большая строка; дальше её прогоняет валидатор.</summary>
    Content,
}

public sealed record ArgSpec(string Name, ArgKind Kind, bool Required = false, string[]? Choices = null, int MaxItems = 0);

/// <param name="Exclusive">
/// Меняет систему — выполняется только одна такая команда за раз, остальные получают <c>busy</c>.
/// Без флага — только чтение (status, read_results, hosts_preview), их можно звать во время подбора.
/// </param>
/// <param name="Long">Долгая: шлёт ход и принимает отмену.</param>
public sealed record CommandSpec(string Name, bool Exclusive, bool Long, params ArgSpec[] Args);

/// <summary>Проверенный запрос. Аргументы уже приведены к типам (bool, string, IReadOnlyList&lt;string&gt;).</summary>
public sealed record HelperCall(long Id, CommandSpec Spec, IReadOnlyDictionary<string, object> Args, long? Target = null)
{
    public string Cmd => Spec.Name;
    public bool Bool(string name, bool fallback = false) => Args.TryGetValue(name, out var v) ? (bool)v : fallback;
    public string? Str(string name) => Args.TryGetValue(name, out var v) ? (string)v : null;
    public IReadOnlyList<string>? List(string name) => Args.TryGetValue(name, out var v) ? (IReadOnlyList<string>)v : null;
}

/// <summary>Запрос отклонён до выполнения (код <c>invalid</c>). Id — если его удалось прочитать, иначе 0.</summary>
public sealed class HelperRequestException(long id, string message) : Exception(message)
{
    public long Id { get; } = id;
}

/// <summary>
/// Белый список команд помощника (11-helper-spec §3) и строгая проверка запросов. Принцип как в договоре с роутером:
/// никаких путей, командных строк и произвольных файлов — только id из каталога, флаги, фиксированные значения
/// и данные (домены, подсети, содержимое IP-списка), которые дальше проходят свои валидаторы.
/// </summary>
public static partial class HelperCommands
{
    public const int MaxUserTargets = 1_000;
    public const int MaxLineLength = 300;
    public const int MaxGroups = 32;
    /// <summary>Содержимое IP-списка в символах: предел валидатора (4 МБ) — больше и он не примет.</summary>
    public const int MaxContentChars = (int)IpsetValidator.MaxBytes;

    static readonly CommandSpec[] All =
    [
        new("status", false, false),
        new("enable", true, false, new ArgSpec("strategy", ArgKind.StrategyId, Required: true)),
        new("disable", true, false),
        new("set_run_mode", true, false, new ArgSpec("service", ArgKind.Bool, Required: true)),
        new("select", true, true, new ArgSpec("quick", ArgKind.Bool), new ArgSpec("groups", ArgKind.GroupIds)),
        new("search", true, true, new ArgSpec("base", ArgKind.StrategyId), new ArgSpec("groups", ArgKind.GroupIds)),
        new("cancel", false, false),
        new("ipset_mode", true, false, new ArgSpec("mode", ArgKind.Choice, Required: true, Choices: ["off", "list"])),
        new("all_addresses_start", true, false),
        new("all_addresses_stop", true, false),
        new("game_mode", true, true, new ArgSpec("mode", ArgKind.Choice, Required: true, Choices: ["off", "all"])),
        new("ipset_update", true, true,
            new ArgSpec("from", ArgKind.Choice, Required: true, Choices: ["manifest", "content"]),
            new ArgSpec("content", ArgKind.Content), new ArgSpec("confirmed", ArgKind.Bool)),
        new("user_ipset_set", true, false,
            new ArgSpec("lines", ArgKind.Lines, Required: true, MaxItems: IpsetValidator.MaxUserLines),
            new ArgSpec("exclude", ArgKind.Bool), new ArgSpec("confirmed", ArgKind.Bool)),
        new("user_targets_set", true, false, new ArgSpec("lines", ArgKind.Lines, Required: true, MaxItems: MaxUserTargets)),
        new("hosts_check", true, true),
        new("hosts_preview", false, false),
        new("hosts_apply", true, false),
        new("hosts_revert", true, false),
        new("hosts_recheck", true, true),
        new("stop_foreign", true, false),
        new("core_repair", true, true),
        new("remove_all", true, false),
        new("clear_history", true, false),
        new("read_results", false, false),
    ];

    public static IReadOnlyDictionary<string, CommandSpec> Specs { get; } = All.ToDictionary(c => c.Name);

    static readonly HashSet<string> TopLevel = ["id", "cmd", "args", "target"];

    /// <summary>Разобрать и проверить строку запроса. Любое отступление — <see cref="HelperRequestException"/>.</summary>
    public static HelperCall Parse(string line, IHelperCatalog catalog)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(line, documentOptions: new JsonDocumentOptions { MaxDepth = 8, AllowDuplicateProperties = false });
            if (node is JsonObject probe) _ = probe.Count;   // объект разбирается лениво — повтор ключа всплыл бы позже
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        { throw new HelperRequestException(0, "Запрос — не JSON или с повтором полей."); }
        if (node is not JsonObject o) throw new HelperRequestException(0, "Запрос должен быть JSON-объектом.");

        long id = 0;
        if (o["id"] is JsonValue idv && idv.TryGetValue<long>(out var parsed) && parsed > 0) id = parsed;
        else throw new HelperRequestException(0, "Нет поля id (целое число больше нуля).");

        foreach (var (k, _) in o)
            if (!TopLevel.Contains(k)) throw new HelperRequestException(id, $"Лишнее поле «{Short(k)}».");

        if (o["cmd"] is not JsonValue cv || !cv.TryGetValue<string>(out var cmd))
            throw new HelperRequestException(id, "Нет поля cmd.");
        if (!Specs.TryGetValue(cmd, out var spec))
            throw new HelperRequestException(id, $"Неизвестная команда «{Short(cmd)}».");

        if (spec.Name == "cancel")
        {
            if (o.ContainsKey("args")) throw new HelperRequestException(id, "У cancel нет аргументов — только target.");
            if (o["target"] is not JsonValue tv || !tv.TryGetValue<long>(out var target) || target <= 0)
                throw new HelperRequestException(id, "cancel: нужен target — id отменяемого запроса.");
            return new HelperCall(id, spec, new Dictionary<string, object>(), target);
        }
        if (o.ContainsKey("target")) throw new HelperRequestException(id, "Лишнее поле «target».");

        var args = new Dictionary<string, object>();
        var raw = o["args"];
        if (raw is not null and not JsonObject) throw new HelperRequestException(id, "args должен быть объектом.");
        var a = raw as JsonObject ?? [];
        foreach (var (k, _) in a)
            if (!spec.Args.Any(s => s.Name == k)) throw new HelperRequestException(id, $"{cmd}: лишнее поле «{Short(k)}».");
        foreach (var s in spec.Args)
        {
            if (!a.TryGetPropertyValue(s.Name, out var v) || v is null)
            {
                if (s.Required) throw new HelperRequestException(id, $"{cmd}: нет обязательного поля «{s.Name}».");
                continue;
            }
            args[s.Name] = Check(id, cmd, s, v, catalog);
        }

        // Связка полей ipset_update: содержимое — только при from = content и тогда обязательно.
        if (spec.Name == "ipset_update")
        {
            bool fromContent = (string)args["from"] == "content";
            if (fromContent != args.ContainsKey("content"))
                throw new HelperRequestException(id, fromContent ? "ipset_update: нет содержимого списка." : "ipset_update: content — только при from = content.");
        }
        return new HelperCall(id, spec, args);
    }

    static object Check(long id, string cmd, ArgSpec s, JsonNode v, IHelperCatalog catalog)
    {
        string Where() => $"{cmd}.{s.Name}";
        switch (s.Kind)
        {
            case ArgKind.Bool:
                if (v is JsonValue bv && bv.GetValueKind() is JsonValueKind.True or JsonValueKind.False) return bv.GetValue<bool>();
                throw new HelperRequestException(id, $"{Where()}: нужно true или false.");

            case ArgKind.StrategyId:
            {
                var str = String(id, Where(), v, 64);
                if (!IdRx().IsMatch(str) || !catalog.StrategyIds.Contains(str))
                    throw new HelperRequestException(id, $"{Where()}: нет такой стратегии в каталоге.");
                return str;
            }

            case ArgKind.GroupIds:
            {
                if (v is not JsonArray arr || arr.Count > MaxGroups) throw new HelperRequestException(id, $"{Where()}: нужен список групп.");
                var list = new List<string>();
                foreach (var item in arr)
                {
                    var g = item is null ? "" : String(id, Where(), item, 64);
                    if (!IdRx().IsMatch(g) || !catalog.GroupIds.Contains(g))
                        throw new HelperRequestException(id, $"{Where()}: нет такой группы целей.");
                    if (list.Contains(g)) throw new HelperRequestException(id, $"{Where()}: группа указана дважды.");
                    list.Add(g);
                }
                return list;
            }

            case ArgKind.Choice:
            {
                var str = String(id, Where(), v, 32);
                if (!s.Choices!.Contains(str)) throw new HelperRequestException(id, $"{Where()}: допустимо {string.Join(", ", s.Choices!)}.");
                return str;
            }

            case ArgKind.Lines:
            {
                if (v is not JsonArray arr) throw new HelperRequestException(id, $"{Where()}: нужен список строк.");
                if (arr.Count > s.MaxItems) throw new HelperRequestException(id, $"{Where()}: больше {s.MaxItems} строк.");
                var list = new List<string>(arr.Count);
                foreach (var item in arr)
                {
                    if (item is null) throw new HelperRequestException(id, $"{Where()}: пустой элемент.");
                    var line = String(id, Where(), item, MaxLineLength);
                    // Домены и подсети не содержат ни обратного слеша, ни кавычек, ни управляющих символов.
                    if (ForbiddenInLineRx().IsMatch(line))
                        throw new HelperRequestException(id, $"{Where()}: строка похожа на путь или командную строку.");
                    list.Add(line);
                }
                return list;
            }

            case ArgKind.Content:
            {
                var str = String(id, Where(), v, MaxContentChars);
                if (str.Contains('\0')) throw new HelperRequestException(id, $"{Where()}: недопустимые символы.");
                return str;
            }
        }
        throw new HelperRequestException(id, $"{Where()}: неизвестный тип аргумента.");
    }

    static string String(long id, string where, JsonNode v, int max)
    {
        if (v is not JsonValue sv || sv.GetValueKind() != JsonValueKind.String) throw new HelperRequestException(id, $"{where}: нужна строка.");
        var s = sv.GetValue<string>();
        if (s.Length == 0 || s.Length > max) throw new HelperRequestException(id, $"{where}: пустая или слишком длинная строка.");
        return s;
    }

    static string Short(string s) => s.Length > 40 ? s[..40] + "…" : s;

    /// <summary>Id стратегий и групп: латиница, цифры, '-', '_', '.'. Пути, пробелы и кавычки не пройдут.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.\-]{0,63}$")]
    private static partial Regex IdRx();

    [GeneratedRegex(@"[\\""<>|\x00-\x1F\x7F]")]
    private static partial Regex ForbiddenInLineRx();
}
