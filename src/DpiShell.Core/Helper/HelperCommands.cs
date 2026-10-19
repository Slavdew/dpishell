using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DpiShell.Core.Helper;

/// <summary>Что помощнику нужно знать для проверки аргументов: допустимые id стратегий и групп целей.</summary>
public interface IHelperCatalog
{
    /// <summary>Каталог поставки + найденные глубоким поиском.</summary>
    IReadOnlyCollection<string> StrategyIds { get; }
    /// <summary>Группы целей поставки + "user" (свои сайты).</summary>
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
    /// <summary>Адреса DNS-серверов: 1–4 настоящих IPv4/IPv6 без повторов (приводятся к нормальной записи).</summary>
    IpAddresses,
    /// <summary>Шаблон защищённого DNS: только https://сервер[:порт]/путь.</summary>
    DohTemplate,
    /// <summary>Название своей стратегии: одна строка до 60 знаков, без управляющих знаков.</summary>
    Title,
    /// <summary>Роли своей стратегии {роль: параметры}: известные роли и параметры из белого списка (WinwsArgs.Problem).</summary>
    Roles,
    /// <summary>Строки hosts «адрес имя» (адрес любой, в том числе заглушка 0.0.0.0): на выходе канонические строки.</summary>
    HostEntries,
    /// <summary>То же, но адрес обязан быть публичным (<see cref="HostsRules.TryParsePublic"/>) — для записи своих строк.</summary>
    PublicHostEntries,
    /// <summary>Номера строк hosts с отпечатком текста «номер:хеш» (12 hex) — как их выдал аудит.</summary>
    HostLineIds,
    /// <summary>Целое число от 0 до MaxItems.</summary>
    Int,
    /// <summary>Порты «443,27015-27030» — цифры, запятые, дефис; разбор и предел — <see cref="AppProfiles.NormalizePorts"/>.</summary>
    Ports,
    /// <summary>Номер профиля программы: p и 8 шестнадцатеричных цифр.</summary>
    ProfileId,
    /// <summary>Номер записи журнала изменений: 12 шестнадцатеричных цифр.</summary>
    ChangeId,
    /// <summary>Адрес системного прокси: хост:порт или схема=хост:порт;… (<see cref="ProxyValues.ParseServer"/>); на выходе нормальная запись.</summary>
    ProxyServer,
    /// <summary>Исключения прокси: имена, маски, &lt;local&gt; (<see cref="ProxyValues.NormalizeBypass"/>).</summary>
    ProxyBypass,
    /// <summary>Адрес автонастройки (PAC): только http(s) (<see cref="ProxyValues.NormalizePac"/>).</summary>
    ProxyPac,
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
    /// <summary>SID пользователя, подключившегося к каналу (определён сервером по токену, не из запроса). Нужен командам «для пользователя».</summary>
    public string? ClientSid { get; init; }
    public string Cmd => Spec.Name;
    public bool Bool(string name, bool fallback = false) => Args.TryGetValue(name, out var v) ? (bool)v : fallback;
    public int Int(string name) => Args.TryGetValue(name, out var v) ? (int)v : 0;
    public string? Str(string name) => Args.TryGetValue(name, out var v) ? (string)v : null;
    public IReadOnlyList<string>? List(string name) => Args.TryGetValue(name, out var v) ? (IReadOnlyList<string>)v : null;
    public IReadOnlyDictionary<string, string>? Map(string name) => Args.TryGetValue(name, out var v) ? (IReadOnlyDictionary<string, string>)v : null;
}

/// <summary>Запрос отклонён до выполнения (код <c>invalid</c>). Id — если его удалось прочитать, иначе 0.</summary>
public sealed class HelperRequestException(long id, string message) : Exception(message)
{
    public long Id { get; } = id;
}

/// <summary>
/// Белый список команд помощника (helper-protocol §3) и строгая проверка запросов. Принцип как в договоре с роутером:
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
        // Примерка строк, свои строки и комментирование чужих (ТЗ 12, API 1.4). Пути и произвольный текст не принимаются: пары «адрес имя»
        // и «номер:хеш» проходят строгий разбор; пишет помощник только то, что проверил сам (hosts_user_set — живой проверкой, hosts_comment — по последнему аудиту).
        new("hosts_try", true, true, new ArgSpec("entries", ArgKind.HostEntries, Required: true, MaxItems: HostsPaste.MaxEntries)),
        new("hosts_audit", true, true),
        new("hosts_user_set", true, true, new ArgSpec("entries", ArgKind.PublicHostEntries, Required: true, MaxItems: HostsPaste.MaxEntries)),
        new("hosts_comment", true, false, new ArgSpec("ids", ArgKind.HostLineIds, Required: true, MaxItems: HostsPaste.MaxEntries)),
        new("hosts_uncomment", true, false),
        new("hosts_restore", true, false),
        new("stop_foreign", true, false),
        new("core_repair", true, true),
        new("remove_all", true, false),
        new("clear_history", true, false),
        new("read_results", false, false),
        // Окну без прав: сведения из закрытой папки (hosts-статус, свои подсети, ядро, найденные стратегии).
        new("details", false, false),
        // Выбрать стратегию, не включая обход (запомнить у помощника).
        new("choose", true, false, new ArgSpec("strategy", ArgKind.StrategyId, Required: true)),
        // Метки TCP нужны стратегии роутера (router-api §6) — включить на этом ПК, не включая обход.
        new("tcp_timestamps_on", true, false),
        // Обновление данных по data.json (architecture.md «Обновления данных»): адреса и хеши помощник берёт сам — из своего списка источников
        // и скачанного манифеста; окно передаёт только согласие на сильно изменившийся IP-список.
        new("data_update", true, true, new ArgSpec("confirmed", ArgKind.Bool)),
        // DNS этого компьютера (architecture.md «DNS этого компьютера»): готовый вариант или свой (адреса + необязательный шаблон DoH).
        // Меняются только физические адаптеры с основным шлюзом; прежние настройки — в закрытой папке помощника.
        new("dns_status", false, false),
        new("dns_set", true, true,
            new ArgSpec("preset", ArgKind.Choice, Required: true, Choices: DnsPresets.Choices),
            new ArgSpec("ips", ArgKind.IpAddresses, MaxItems: DnsValues.MaxServers),
            new ArgSpec("doh", ArgKind.DohTemplate),
            new ArgSpec("fallback", ArgKind.Bool)),
        new("dns_restore", true, true),
        // Свои стратегии (architecture.md «Свои стратегии»): окну без прав писать в папку программы нельзя. Помощник заново проверяет роли
        // тем же белым списком, что и каталог, и сам назначает id; командной строки и путей не принимает.
        new("strategy_add", true, false,
            new ArgSpec("title", ArgKind.Title, Required: true), new ArgSpec("roles", ArgKind.Roles, Required: true),
            new ArgSpec("source", ArgKind.Choice, Required: true, Choices: ["user", "pasted"])),
        new("strategy_delete", true, false, new ArgSpec("strategy", ArgKind.StrategyId, Required: true)),
        // Проверка сайтов только для одной стратегии (вставленной): тот же Selector, один кандидат.
        new("strategy_check", true, true, new ArgSpec("strategy", ArgKind.StrategyId, Required: true), new ArgSpec("groups", ArgKind.GroupIds)),
        // Подключения программ (ТЗ 13). Запись — только чтение: «процесс → удалённый адрес:порт/протокол» на срок до 5 минут, окну отдаётся итог.
        // Профиль программы — только порты, адреса и номер варианта из готовых стратегий; командной строки и путей команды не принимают.
        new("rec_start", true, false,
            new ArgSpec("mode", ArgKind.Choice, Required: true, Choices: ["new", "process"]),
            new ArgSpec("pid", ArgKind.Int, MaxItems: 4_194_304), new ArgSpec("driver", ArgKind.Bool)),
        new("rec_status", false, false),
        new("rec_stop", true, false),
        new("app_profiles", false, false),
        new("app_profile_set", true, false,
            new ArgSpec("id", ArgKind.ProfileId), new ArgSpec("title", ArgKind.Title, Required: true),
            new ArgSpec("tcp", ArgKind.Ports), new ArgSpec("udp", ArgKind.Ports),
            new ArgSpec("addresses", ArgKind.Lines, Required: true, MaxItems: AppProfiles.MaxAddresses),
            new ArgSpec("variant", ArgKind.Int, Required: true, MaxItems: AppProfiles.MaxVariants - 1)),
        new("app_profile_remove", true, false, new ArgSpec("id", ArgKind.ProfileId, Required: true)),
        // Откат всего (API 1.6): журнал изменений программы. Помощник откатывает только то, что записал сам, и только если сейчас стоит то,
        // что ставили мы; окно передаёт лишь номер записи — ни значений, ни путей.
        new("changes_list", false, false),
        new("changes_undo_all", true, true),
        new("changes_undo", true, true, new ArgSpec("id", ArgKind.ChangeId, Required: true)),
        // Системный прокси (API 1.7). Ни путей реестра, ни имён значений команды не принимают: адрес — хост:порт, исключения — имена и маски,
        // PAC — http(s). «Для пользователя» помощник пишет в ветку пользователя ЭТОГО канала (SID клиента), «для машины» — HKLM + политику
        // ProxySettingsPerUser и по желанию WinHTTP. enabled=false — «Очистить» (прежнее запоминается), без остальных аргументов.
        new("proxy_status", false, false),
        new("proxy_user_set", true, false,
            new ArgSpec("enabled", ArgKind.Bool, Required: true), new ArgSpec("server", ArgKind.ProxyServer),
            new ArgSpec("bypass", ArgKind.ProxyBypass), new ArgSpec("pac", ArgKind.ProxyPac)),
        new("proxy_machine_set", true, false,
            new ArgSpec("enabled", ArgKind.Bool, Required: true), new ArgSpec("server", ArgKind.ProxyServer),
            new ArgSpec("bypass", ArgKind.ProxyBypass), new ArgSpec("pac", ArgKind.ProxyPac), new ArgSpec("winhttp", ArgKind.Bool)),
        new("proxy_machine_restore", true, false),
    ];

    public static IReadOnlyDictionary<string, CommandSpec> Specs { get; } = All.ToDictionary(c => c.Name);

    /// <summary>Имена команд белого списка по алфавиту — поле <c>commands</c> в ответе status (helper-protocol §6).</summary>
    public static IReadOnlyList<string> Names { get; } = [.. All.Select(c => c.Name).Order(StringComparer.Ordinal)];

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
        // dns_set: адреса и шаблон — только у своего варианта, и тогда адреса обязательны.
        if (spec.Name == "dns_set")
        {
            bool custom = (string)args["preset"] == DnsPresets.Custom;
            if (custom && !args.ContainsKey("ips")) throw new HelperRequestException(id, "dns_set: у своего варианта нужны адреса.");
            if (!custom && (args.ContainsKey("ips") || args.ContainsKey("doh")))
                throw new HelperRequestException(id, "dns_set: ips и doh — только при preset = custom.");
        }
        // rec_start: pid нужен только для режима «process» (и тогда обязателен).
        if (spec.Name == "rec_start")
        {
            bool process = (string)args["mode"] == "process";
            if (process != args.ContainsKey("pid"))
                throw new HelperRequestException(id, process ? "rec_start: для режима process нужен pid." : "rec_start: pid — только для режима process.");
        }
        // proxy_*_set: включить — адрес или PAC обязателен; выключить — без значений и без галочки WinHTTP-адресов.
        if (spec.Name is "proxy_user_set" or "proxy_machine_set")
        {
            bool on = (bool)args["enabled"];
            bool values = args.ContainsKey("server") || args.ContainsKey("pac") || args.ContainsKey("bypass");
            if (on && !args.ContainsKey("server") && !args.ContainsKey("pac"))
                throw new HelperRequestException(id, $"{spec.Name}: нужен адрес прокси или адрес автонастройки.");
            if (!on && values) throw new HelperRequestException(id, $"{spec.Name}: при enabled = false адрес, исключения и PAC не передаются.");
        }
        if (spec.Name == "app_profile_set" && !args.ContainsKey("tcp") && !args.ContainsKey("udp"))
            throw new HelperRequestException(id, "app_profile_set: нужны порты TCP или UDP.");
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

            case ArgKind.HostEntries:
            case ArgKind.PublicHostEntries:
            {
                if (v is not JsonArray arr) throw new HelperRequestException(id, $"{Where()}: нужен список строк.");
                if (arr.Count > s.MaxItems) throw new HelperRequestException(id, $"{Where()}: больше {s.MaxItems} строк.");
                var list = new List<string>(arr.Count);
                foreach (var item in arr)
                {
                    if (item is null) throw new HelperRequestException(id, $"{Where()}: пустой элемент.");
                    var line = String(id, Where(), item, MaxLineLength);
                    if (!HostsPaste.TryEntry(line, s.Kind == ArgKind.PublicHostEntries, out var e))
                        throw new HelperRequestException(id, $"{Where()}: «{Short(line)}» — не пара «адрес имя».");
                    var canon = $"{e.Ip} {e.Host}";
                    if (!list.Contains(canon)) list.Add(canon);
                }
                return list;
            }

            case ArgKind.HostLineIds:
            {
                if (v is not JsonArray arr) throw new HelperRequestException(id, $"{Where()}: нужен список строк.");
                if (arr.Count > s.MaxItems) throw new HelperRequestException(id, $"{Where()}: больше {s.MaxItems} строк.");
                var list = new List<string>(arr.Count);
                foreach (var item in arr)
                {
                    var line = item is null ? "" : String(id, Where(), item, 20);
                    if (!HostsText.TryParseId(line, out _, out _)) throw new HelperRequestException(id, $"{Where()}: «{Short(line)}» — не «номер:отпечаток».");
                    if (list.Contains(line)) throw new HelperRequestException(id, $"{Where()}: строка указана дважды.");
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

            case ArgKind.IpAddresses:
            {
                if (v is not JsonArray arr || arr.Count == 0 || arr.Count > s.MaxItems)
                    throw new HelperRequestException(id, $"{Where()}: нужно от 1 до {s.MaxItems} адресов.");
                var list = new List<string>();
                foreach (var item in arr)
                {
                    var raw = item is null ? "" : String(id, Where(), item, 45);
                    var ip = DnsValues.NormalizeIp(raw) ?? throw new HelperRequestException(id, $"{Where()}: «{Short(raw)}» — не адрес IPv4/IPv6.");
                    if (list.Contains(ip)) throw new HelperRequestException(id, $"{Where()}: адрес указан дважды.");
                    list.Add(ip);
                }
                return list;
            }

            case ArgKind.DohTemplate:
            {
                var str = String(id, Where(), v, DnsValues.MaxTemplate);
                if (!DnsValues.IsDohTemplate(str)) throw new HelperRequestException(id, $"{Where()}: нужен адрес вида https://сервер/путь.");
                return str;
            }

            case ArgKind.Title:
            {
                var str = String(id, Where(), v, StrategyExchange.MaxTitle);
                if (str.Any(char.IsControl)) throw new HelperRequestException(id, $"{Where()}: управляющие знаки в названии.");
                return str;
            }

            case ArgKind.Int:
            {
                if (v is not JsonValue iv || !iv.TryGetValue<int>(out var n) || n < 0 || n > s.MaxItems)
                    throw new HelperRequestException(id, $"{Where()}: нужно целое число от 0 до {s.MaxItems}.");
                return n;
            }

            case ArgKind.Ports:
            {
                var str = String(id, Where(), v, 200);
                return AppProfiles.NormalizePorts(str) ?? throw new HelperRequestException(id, $"{Where()}: порты записаны неверно (пример: 443,27015-27030).");
            }

            case ArgKind.ProfileId:
            {
                var str = String(id, Where(), v, 16);
                if (!AppProfiles.IsValidId(str)) throw new HelperRequestException(id, $"{Where()}: неверный номер профиля.");
                return str;
            }

            case ArgKind.ChangeId:
            {
                var str = String(id, Where(), v, 12);
                if (!ChangeIdRx().IsMatch(str)) throw new HelperRequestException(id, $"{Where()}: неверный номер записи журнала.");
                return str;
            }

            case ArgKind.ProxyServer:
            {
                var str = String(id, Where(), v, ProxyValues.MaxServerLength);
                return ProxyValues.NormalizeServer(str) ?? throw new HelperRequestException(id, $"{Where()}: нужен адрес вида хост:порт (или схема=хост:порт;…).");
            }

            case ArgKind.ProxyBypass:
            {
                var str = String(id, Where(), v, 2_000);
                var (value, ok) = ProxyValues.NormalizeBypass(str);
                if (!ok || value is null) throw new HelperRequestException(id, $"{Where()}: исключения — имена и маски через «;».");
                return value;
            }

            case ArgKind.ProxyPac:
            {
                var str = String(id, Where(), v, ProxyValues.MaxPacLength);
                var (value, ok) = ProxyValues.NormalizePac(str);
                if (!ok || value is null) throw new HelperRequestException(id, $"{Where()}: нужна ссылка http:// или https://.");
                return value;
            }

            case ArgKind.Roles:
            {
                if (v is not JsonObject ro || ro.Count == 0 || ro.Count > WinwsArgs.RoleOrder.Length)
                    throw new HelperRequestException(id, $"{Where()}: нужен объект «роль: параметры».");
                var roles = new Dictionary<string, string>();
                foreach (var (role, node) in ro)
                {
                    var args = node is null ? "" : String(id, Where(), node, WinwsArgs.MaxRoleLength);
                    roles[role] = args;
                }
                if (StrategyExchange.CheckRoles(roles) is { } problem) throw new HelperRequestException(id, $"{Where()}: {problem}.");
                return roles;
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

    [GeneratedRegex("^[0-9a-f]{12}$")]
    private static partial Regex ChangeIdRx();

    [GeneratedRegex(@"[\\""<>|\x00-\x1F\x7F]")]
    private static partial Regex ForbiddenInLineRx();
}
