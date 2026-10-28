using DpiShell.Core.Localization;
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
    /// <summary>Начало нагрузки пакета: hex строчными, до 16 байт (API 1.22).</summary>
    PayloadHex,
    /// <summary>Свой фильтр WinDivert профиля (API 1.22): одна строка до <see cref="CustomFilter.MaxLength"/> знаков без управляющих; разбор по белому списку и обязательные условия — <see cref="CustomFilter.Problem"/> (в <see cref="AppProfiles.Problem"/>).</summary>
    WdFilter,
    /// <summary>Параметры десинхронизации профиля одной строкой до <see cref="WinwsArgs.MaxRoleLength"/> знаков; белый список — в <see cref="AppProfiles.Problem"/>.</summary>
    Desync,
    /// <summary>Номер профиля программы: p и 8 шестнадцатеричных цифр.</summary>
    ProfileId,
    /// <summary>Выбор стратегии профиля (API 1.22): «default», «own» или id стратегии из каталога и своих.</summary>
    ProfileStrategy,
    /// <summary>Номер записи журнала изменений: 12 шестнадцатеричных цифр.</summary>
    ChangeId,
    /// <summary>Адрес системного прокси: хост:порт или схема=хост:порт;… (<see cref="ProxyValues.ParseServer"/>); на выходе нормальная запись.</summary>
    ProxyServer,
    /// <summary>Исключения прокси: имена, маски, &lt;local&gt; (<see cref="ProxyValues.NormalizeBypass"/>).</summary>
    ProxyBypass,
    /// <summary>Адрес автонастройки (PAC): только http(s) (<see cref="ProxyValues.NormalizePac"/>).</summary>
    ProxyPac,
    /// <summary>Строки продвинутого режима [{domains: [домен…], strategy}]: домены — строгий шаблон (<see cref="AdvancedSites.IsDomain"/>), стратегия — auto, direct или id из каталога. Других полей нет.</summary>
    AdvancedRows,
    /// <summary>Свои сайты по группам (API 1.18) [{name, sites: [строка…]}]: имя до <see cref="SiteGroups.MaxName"/> знаков без управляющих (пусто — «Прочее»), сайты — как <see cref="Lines"/>, всего не больше MaxItems. Других полей нет.</summary>
    SiteGroups,
    /// <summary>Сайты вида example.com — строгий шаблон (<see cref="AdvancedSites.IsDomain"/>), без повторов; на выходе список строк.</summary>
    Domains,
    /// <summary>План загрузки настроек (JSON-строка <see cref="Export.ImportRequest"/>): разобран строго, каждая запись проверена теми же правилами, что и при чтении чужих файлов, без правок — подделка отклоняется целиком.</summary>
    SettingsPlan,
    /// <summary>Часть архива ядра в base64 (API 1.19, core_offline_part): только байты, до <see cref="HelperCommands.MaxOfflineChunkBytes"/>; на выходе byte[].</summary>
    Base64Chunk,
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
    /// <summary>Язык окна для ответа на этот запрос (API 1.13, поле <c>lang</c>); null — не прислан, помощник отвечает на своём.</summary>
    public string? Lang { get; init; }
    /// <summary>SID пользователя, подключившегося к каналу (определён сервером по токену, не из запроса). Нужен командам «для пользователя».</summary>
    public string? ClientSid { get; init; }
    public string Cmd => Spec.Name;
    public bool Bool(string name, bool fallback = false) => Args.TryGetValue(name, out var v) ? (bool)v : fallback;
    public int Int(string name) => Args.TryGetValue(name, out var v) ? (int)v : 0;
    public string? Str(string name) => Args.TryGetValue(name, out var v) ? (string)v : null;
    public byte[]? Bytes(string name) => Args.TryGetValue(name, out var v) ? (byte[])v : null;
    public IReadOnlyList<string>? List(string name) => Args.TryGetValue(name, out var v) ? (IReadOnlyList<string>)v : null;
    public IReadOnlyDictionary<string, string>? Map(string name) => Args.TryGetValue(name, out var v) ? (IReadOnlyDictionary<string, string>)v : null;
    public IReadOnlyList<SiteGroup>? Groups(string name) => Args.TryGetValue(name, out var v) ? (IReadOnlyList<SiteGroup>)v : null;
    public IReadOnlyList<AdvancedRowInput>? Rows(string name) => Args.TryGetValue(name, out var v) ? (IReadOnlyList<AdvancedRowInput>)v : null;
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
    /// <summary>Часть офлайн-архива ядра (core_offline_part) — до 1 МБ байтов: строка канала ограничена, архив идёт частями.</summary>
    public const int MaxOfflineChunkBytes = 1024 * 1024;

    /// <summary>Откуда подбор берёт адреса сайтов (API 1.12, <see cref="CheckDns"/>); не указан — защищённый DNS.</summary>
    static readonly ArgSpec DnsArg = new("dns", ArgKind.Choice, Choices: [CheckDns.Secure, CheckDns.System]);
    /// <summary>API 1.21: подбор только своих сайтов — winws кандидатов без ролей YouTube, Discord, IP-списков (их ведёт роутер).</summary>
    static readonly ArgSpec OwnOnlyArg = new("ownOnly", ArgKind.Bool);

    static readonly CommandSpec[] All =
    [
        new("status", false, false),
        new("enable", true, false, new ArgSpec("strategy", ArgKind.StrategyId, Required: true)),
        new("disable", true, false),
        new("set_run_mode", true, false, new ArgSpec("service", ArgKind.Bool, Required: true)),
        new("select", true, true, new ArgSpec("quick", ArgKind.Bool), new ArgSpec("groups", ArgKind.GroupIds), DnsArg, OwnOnlyArg),
        new("search", true, true, new ArgSpec("base", ArgKind.StrategyId), new ArgSpec("groups", ArgKind.GroupIds), DnsArg, OwnOnlyArg),
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
        // Свои сайты по группам (API 1.18): список целиком, как user_targets_set, плюс имена групп; сайты без группы — name "".
        new("user_groups_set", true, false, new ArgSpec("groups", ArgKind.SiteGroups, Required: true, MaxItems: MaxUserTargets)),
        // Сайты, которые открываются без оптимизации (API 1.10): список целиком, только домены (тот же разбор, что у «Своих сайтов»);
        // в командную строку они попадают как --hostlist-exclude файла user-exclude.txt, который собирает сам помощник.
        new("user_exclude_set", true, false, new ArgSpec("lines", ArgKind.Lines, Required: true, MaxItems: AdvancedSites.MaxExclude)),
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
        // Хвост журнала помощника (API 1.13): журнал лежит в закрытой папке, окну для «Сохранить отчёт» его отдаёт помощник — последние строки, без аргументов.
        new("log_tail", false, false),
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
        new("strategy_check", true, true, new ArgSpec("strategy", ArgKind.StrategyId, Required: true), new ArgSpec("groups", ArgKind.GroupIds), DnsArg, OwnOnlyArg),
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
            new ArgSpec("addresses", ArgKind.Lines, MaxItems: AppProfiles.MaxAddresses),
            new ArgSpec("variant", ArgKind.Int, Required: true, MaxItems: AppProfiles.MaxVariants - 1),
            // API 1.22: сигнатура пакета и свои параметры (из импорта). Окну не доверяем: поля проверяет AppProfiles.Problem, здесь — только форма.
            new ArgSpec("tcpLen", ArgKind.Int, MaxItems: AppProfiles.MaxPayloadLength), new ArgSpec("tcpHex", ArgKind.PayloadHex),
            new ArgSpec("udpLen", ArgKind.Int, MaxItems: AppProfiles.MaxPayloadLength), new ArgSpec("udpHex", ArgKind.PayloadHex),
            new ArgSpec("tcpDesync", ArgKind.Desync), new ArgSpec("udpDesync", ArgKind.Desync),
            new ArgSpec("tcpFilter", ArgKind.WdFilter), new ArgSpec("udpFilter", ArgKind.WdFilter)),
        new("app_profile_remove", true, false, new ArgSpec("id", ArgKind.ProfileId, Required: true)),
        // API 1.22: профиль включается сам по себе; выбор стратегии — «default», «own» или id из каталога (есть ли у неё игровая часть и свои ли параметры — проверяет Shell).
        new("app_profile_options", true, false, new ArgSpec("id", ArgKind.ProfileId, Required: true), new ArgSpec("enabled", ArgKind.Bool),
            new ArgSpec("strategy", ArgKind.ProfileStrategy)),
        new("app_profiles_separate", true, false, new ArgSpec("on", ArgKind.Bool, Required: true)),
        // Откат всего (API 1.6): журнал изменений программы. Помощник откатывает только то, что записал сам, и только если сейчас стоит то,
        // что ставили мы; окно передаёт лишь номер записи — ни значений, ни путей.
        new("changes_list", false, false),
        new("changes_undo_all", true, true),
        new("changes_undo", true, true, new ArgSpec("id", ArgKind.ChangeId, Required: true)),
        // API 1.15: действие вместе с записями, сделанными внутри него (загрузка настроек и её hosts, DNS, профили), — остальной журнал не трогается.
        new("changes_undo_group", true, true, new ArgSpec("id", ArgKind.ChangeId, Required: true)),
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
        // Продвинутый режим «сайты → стратегия» (API 1.8): сайты и стратегия на строку — ни параметров winws, ни путей, ни списков. Домены проходят строгий шаблон,
        // стратегия — auto, direct или id из каталога и найденных; помощник сам собирает профили белым списком и сам пишет итоги подбора (resolved от окна не принимается).
        new("advanced_get", false, false),
        new("advanced_set", true, false,
            new ArgSpec("enabled", ArgKind.Bool, Required: true),
            new ArgSpec("others", ArgKind.Choice, Required: true, Choices: [AdvancedSites.OthersDirect, AdvancedSites.OthersLists, AdvancedSites.OthersAll]),
            new ArgSpec("rows", ArgKind.AdvancedRows, Required: true, MaxItems: AdvancedSites.MaxRows)),
        // Совместная работа с роутером (API 1.9): joint_get — что ПК готов взять на себя (свои сайты, строки, имена приманок) и действует ли связка; joint_set —
        // включить/выключить и продлить связку. Ни путей, ни параметров winws: только флаг и список сайтов ПК, которые ведёт роутер (строгий шаблон имени).
        new("joint_get", false, false),
        new("joint_set", true, false,
            new ArgSpec("active", ArgKind.Bool, Required: true),
            new ArgSpec("owned", ArgKind.Domains, MaxItems: JointRules.MaxDomains)),
        // Выгрузка и загрузка своих настроек папкой файлов (API 1.11, docs/settings-export.md). settings_export_read — только чтение: свои данные из закрытой папки
        // для окна без прав. settings_import — готовый, уже проверенный окном план; помощник проверяет его ещё раз (окну не доверяет), сам сверяет со своим состоянием,
        // пишет в журнал изменений («Отменить всё» вернёт прежнее) и применяет разделы теми же проверками, что и обычные команды (домены, белый список winws, публичные адреса hosts).
        new("settings_export_read", false, false),
        new("settings_import", true, true, new ArgSpec("plan", ArgKind.SettingsPlan, Required: true)),
        // Исключение Защитника для папки ядра (API 1.16): аргументов нет вовсе — путь помощник берёт свой (%ProgramData%\DpiShell), любой присланный — отказ.
        // Иначе пользователь без прав исключил бы через помощника что угодно. Только WMI, только по нажатию; снимается только своё (журнал, before.present).
        new("av_exclusion_status", false, false),
        new("av_exclusion_add", true, false),
        // Синхронизация часов (API 1.20, карточка «Сбилось время»): аргументов нет — w32tm /resync, больше ничего.
        new("time_sync", true, false),
        new("av_exclusion_remove", true, false),
        // Офлайн-архив ядра (API 1.19, «Указать архив ядра…»): окно без прав передаёт байты файла частями, путь не передаётся вовсе.
        // Помощник собирает их в своей закрытой папке (core.offline) и ставит ядро, только если sha256 архива совпал с core.json — тем же путём, что скачанное.
        new("core_offline_part", true, false, new ArgSpec("data", ArgKind.Base64Chunk, Required: true), new ArgSpec("first", ArgKind.Bool)),
        new("core_offline_install", true, true),
    ];

    public static IReadOnlyDictionary<string, CommandSpec> Specs { get; } = All.ToDictionary(c => c.Name);

    /// <summary>Имена команд белого списка по алфавиту — поле <c>commands</c> в ответе status (helper-protocol §6).</summary>
    public static IReadOnlyList<string> Names { get; } = [.. All.Select(c => c.Name).Order(StringComparer.Ordinal)];

    /// <summary>
    /// Действия на весь компьютер (API 1.14, helper-protocol §5): правка и дополнение hosts, смена DNS, прокси машины, исключение Защитника (1.16). Принимаются только от окна
    /// той же сборки, что помощник (<see cref="HelperClientImage"/>). Возврат как было (hosts_revert, hosts_restore, hosts_uncomment, dns_restore,
    /// changes_undo*) сюда не входит: он только снимает наше.
    /// </summary>
    public static IReadOnlySet<string> MachineCommands { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "hosts_apply", "hosts_user_set", "hosts_comment", "dns_set", "proxy_machine_set", "proxy_machine_restore",
        // Исключение Защитника — на весь компьютер (API 1.16); снятие своего (av_exclusion_remove) — возврат как было, не ограничено.
        "av_exclusion_add",
    };

    /// <summary>Запрос меняет настройки всего компьютера: команда из <see cref="MachineCommands"/> или загрузка настроек с разделом hosts или DNS.</summary>
    public static bool IsMachine(HelperCall c)
    {
        if (MachineCommands.Contains(c.Cmd)) return true;
        if (c.Cmd != "settings_import") return false;
        // План уже прошёл строгую проверку; не читается — считаем машинным (осторожнее).
        var plan = Export.ImportRequest.TryParse(c.Str("plan"), out _);
        return plan is null || plan.Sections.Any(s => s is Export.SettingsSections.Hosts or Export.SettingsSections.Dns);
    }

    static readonly HashSet<string> TopLevel = ["id", "cmd", "args", "target", "lang"];

    [GeneratedRegex("^[A-Za-z]{2,3}(-[A-Za-z0-9]{1,8}){0,3}$")]
    private static partial Regex LangRx();

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
        { throw new HelperRequestException(0, L.T("core.req.not_json")); }
        if (node is not JsonObject o) throw new HelperRequestException(0, L.T("core.req.not_object"));
        // Отказы — на языке окна (поле lang, API 1.13), как и остальные ответы: окно показывает их в «Подробнее» (аудит-2, П-18).
        using var scope = L.Scope(o["lang"] is JsonValue lpeek && lpeek.TryGetValue<string>(out var lcode) && LangRx().IsMatch(lcode) ? lcode : null);

        long id = 0;
        if (o["id"] is JsonValue idv && idv.TryGetValue<long>(out var parsed) && parsed > 0) id = parsed;
        else throw new HelperRequestException(0, L.T("core.req.no_id"));

        foreach (var (k, _) in o)
            if (!TopLevel.Contains(k)) throw new HelperRequestException(id, L.T("core.req.extra_field", new { name = Short(k) }));

        // lang (API 1.13): код языка окна для ответа; неузнанный помощником код ничего не меняет.
        string? lang = null;
        if (o["lang"] is { } lv)
        {
            if (lv is not JsonValue lval || !lval.TryGetValue<string>(out lang) || !LangRx().IsMatch(lang) || lang.Length > 16)
                throw new HelperRequestException(id, L.T("core.req.lang"));
        }

        if (o["cmd"] is not JsonValue cv || !cv.TryGetValue<string>(out var cmd))
            throw new HelperRequestException(id, L.T("core.req.no_cmd"));
        if (!Specs.TryGetValue(cmd, out var spec))
            throw new HelperRequestException(id, L.T("core.req.unknown_cmd", new { name = Short(cmd) }));

        if (spec.Name == "cancel")
        {
            if (o.ContainsKey("args")) throw new HelperRequestException(id, L.T("core.req.cancel_args"));
            if (o["target"] is not JsonValue tv || !tv.TryGetValue<long>(out var target) || target <= 0)
                throw new HelperRequestException(id, L.T("core.req.cancel_target"));
            return new HelperCall(id, spec, new Dictionary<string, object>(), target) { Lang = lang };
        }
        if (o.ContainsKey("target")) throw new HelperRequestException(id, L.T("core.req.extra_field", new { name = "target" }));

        var args = new Dictionary<string, object>();
        var raw = o["args"];
        if (raw is not null and not JsonObject) throw new HelperRequestException(id, L.T("core.req.args_object"));
        var a = raw as JsonObject ?? [];
        foreach (var (k, _) in a)
            if (!spec.Args.Any(s => s.Name == k)) throw new HelperRequestException(id, L.T("core.req.arg_extra", new { cmd, name = Short(k) }));
        foreach (var s in spec.Args)
        {
            if (!a.TryGetPropertyValue(s.Name, out var v) || v is null)
            {
                if (s.Required) throw new HelperRequestException(id, L.T("core.req.arg_required", new { cmd, name = s.Name }));
                continue;
            }
            args[s.Name] = Check(id, cmd, s, v, catalog);
        }

        // Связка полей ipset_update: содержимое — только при from = content и тогда обязательно.
        if (spec.Name == "ipset_update")
        {
            bool fromContent = (string)args["from"] == "content";
            if (fromContent != args.ContainsKey("content"))
                throw new HelperRequestException(id, fromContent ? L.T("core.req.ipset_no_content") : L.T("core.req.ipset_content_only"));
        }
        // dns_set: адреса и шаблон — только у своего варианта, и тогда адреса обязательны.
        if (spec.Name == "dns_set")
        {
            bool custom = (string)args["preset"] == DnsPresets.Custom;
            if (custom && !args.ContainsKey("ips")) throw new HelperRequestException(id, L.T("core.req.dns_need_ips"));
            if (!custom && (args.ContainsKey("ips") || args.ContainsKey("doh")))
                throw new HelperRequestException(id, L.T("core.req.dns_custom_only"));
        }
        // rec_start: pid нужен только для режима «process» (и тогда обязателен).
        if (spec.Name == "rec_start")
        {
            bool process = (string)args["mode"] == "process";
            if (process != args.ContainsKey("pid"))
                throw new HelperRequestException(id, process ? L.T("core.req.rec_need_pid") : L.T("core.req.rec_pid_only"));
        }
        // proxy_*_set: включить — адрес или PAC обязателен; выключить — без значений и без галочки WinHTTP-адресов.
        if (spec.Name is "proxy_user_set" or "proxy_machine_set")
        {
            bool on = (bool)args["enabled"];
            bool values = args.ContainsKey("server") || args.ContainsKey("pac") || args.ContainsKey("bypass");
            if (on && !args.ContainsKey("server") && !args.ContainsKey("pac"))
                throw new HelperRequestException(id, L.T("core.req.proxy_need", new { cmd = spec.Name }));
            if (!on && values) throw new HelperRequestException(id, L.T("core.req.proxy_off_values", new { cmd = spec.Name }));
        }
        if (spec.Name == "app_profile_set" && !args.ContainsKey("tcp") && !args.ContainsKey("udp"))
            throw new HelperRequestException(id, L.T("core.req.ports_need"));
        return new HelperCall(id, spec, args) { Lang = lang };
    }

    static object Check(long id, string cmd, ArgSpec s, JsonNode v, IHelperCatalog catalog)
    {
        string Where() => $"{cmd}.{s.Name}";
        switch (s.Kind)
        {
            case ArgKind.Bool:
                if (v is JsonValue bv && bv.GetValueKind() is JsonValueKind.True or JsonValueKind.False) return bv.GetValue<bool>();
                throw new HelperRequestException(id, L.T("core.req.bool", new { where = Where() }));

            case ArgKind.ProfileStrategy:
            {
                var str = String(id, Where(), v, 64);
                if (str is not (AppProfiles.Default or AppProfiles.Own) && (!IdRx().IsMatch(str) || !catalog.StrategyIds.Contains(str)))
                    throw new HelperRequestException(id, L.T("core.req.no_strategy", new { where = Where() }));
                return str;
            }

            case ArgKind.StrategyId:
            {
                var str = String(id, Where(), v, 64);
                if (!IdRx().IsMatch(str) || !catalog.StrategyIds.Contains(str))
                    throw new HelperRequestException(id, L.T("core.req.no_strategy", new { where = Where() }));
                return str;
            }

            case ArgKind.GroupIds:
            {
                if (v is not JsonArray arr || arr.Count > MaxGroups) throw new HelperRequestException(id, L.T("core.req.groups_list", new { where = Where() }));
                var list = new List<string>();
                foreach (var item in arr)
                {
                    var g = item is null ? "" : String(id, Where(), item, 64);
                    if (!IdRx().IsMatch(g) || !catalog.GroupIds.Contains(g))
                        throw new HelperRequestException(id, L.T("core.req.no_group", new { where = Where() }));
                    if (list.Contains(g)) throw new HelperRequestException(id, L.T("core.req.group_twice", new { where = Where() }));
                    list.Add(g);
                }
                return list;
            }

            case ArgKind.Choice:
            {
                var str = String(id, Where(), v, 32);
                if (!s.Choices!.Contains(str)) throw new HelperRequestException(id, L.T("core.req.choices", new { where = Where(), choices = string.Join(", ", s.Choices!) }));
                return str;
            }

            case ArgKind.Lines:
            {
                if (v is not JsonArray arr) throw new HelperRequestException(id, L.T("core.req.string_list", new { where = Where() }));
                if (arr.Count > s.MaxItems) throw new HelperRequestException(id, L.T("core.req.too_many_lines", new { where = Where(), max = s.MaxItems }));
                var list = new List<string>(arr.Count);
                foreach (var item in arr)
                {
                    if (item is null) throw new HelperRequestException(id, L.T("core.req.empty_item", new { where = Where() }));
                    var line = String(id, Where(), item, MaxLineLength);
                    // Домены и подсети не содержат ни обратного слеша, ни кавычек, ни управляющих символов.
                    if (ForbiddenInLineRx().IsMatch(line))
                        throw new HelperRequestException(id, L.T("core.req.path_like", new { where = Where() }));
                    list.Add(line);
                }
                return list;
            }

            case ArgKind.HostEntries:
            case ArgKind.PublicHostEntries:
            {
                if (v is not JsonArray arr) throw new HelperRequestException(id, L.T("core.req.string_list", new { where = Where() }));
                if (arr.Count > s.MaxItems) throw new HelperRequestException(id, L.T("core.req.too_many_lines", new { where = Where(), max = s.MaxItems }));
                var list = new List<string>(arr.Count);
                foreach (var item in arr)
                {
                    if (item is null) throw new HelperRequestException(id, L.T("core.req.empty_item", new { where = Where() }));
                    var line = String(id, Where(), item, MaxLineLength);
                    if (!HostsPaste.TryEntry(line, s.Kind == ArgKind.PublicHostEntries, out var e))
                        throw new HelperRequestException(id, L.T("core.req.hosts_pair", new { where = Where(), value = Short(line) }));
                    var canon = $"{e.Ip} {e.Host}";
                    if (!list.Contains(canon)) list.Add(canon);
                }
                return list;
            }

            case ArgKind.HostLineIds:
            {
                if (v is not JsonArray arr) throw new HelperRequestException(id, L.T("core.req.string_list", new { where = Where() }));
                if (arr.Count > s.MaxItems) throw new HelperRequestException(id, L.T("core.req.too_many_lines", new { where = Where(), max = s.MaxItems }));
                var list = new List<string>(arr.Count);
                foreach (var item in arr)
                {
                    var line = item is null ? "" : String(id, Where(), item, 20);
                    if (!HostsText.TryParseId(line, out _, out _)) throw new HelperRequestException(id, L.T("core.req.hosts_id", new { where = Where(), value = Short(line) }));
                    if (list.Contains(line)) throw new HelperRequestException(id, L.T("core.req.line_twice", new { where = Where() }));
                    list.Add(line);
                }
                return list;
            }

            case ArgKind.Content:
            {
                var str = String(id, Where(), v, MaxContentChars);
                if (str.Contains('\0')) throw new HelperRequestException(id, L.T("core.req.bad_chars", new { where = Where() }));
                return str;
            }

            case ArgKind.IpAddresses:
            {
                if (v is not JsonArray arr || arr.Count == 0 || arr.Count > s.MaxItems)
                    throw new HelperRequestException(id, L.T("core.req.ips_count", new { where = Where(), max = s.MaxItems }));
                var list = new List<string>();
                foreach (var item in arr)
                {
                    var raw = item is null ? "" : String(id, Where(), item, 45);
                    var ip = DnsValues.NormalizeIp(raw) ?? throw new HelperRequestException(id, L.T("core.req.not_ip", new { where = Where(), value = Short(raw) }));
                    if (list.Contains(ip)) throw new HelperRequestException(id, L.T("core.req.ip_twice", new { where = Where() }));
                    list.Add(ip);
                }
                return list;
            }

            case ArgKind.DohTemplate:
            {
                var str = String(id, Where(), v, DnsValues.MaxTemplate);
                if (!DnsValues.IsDohTemplate(str)) throw new HelperRequestException(id, L.T("core.req.doh", new { where = Where() }));
                return str;
            }

            case ArgKind.Title:
            {
                var str = String(id, Where(), v, StrategyExchange.MaxTitle);
                if (str.Any(char.IsControl)) throw new HelperRequestException(id, L.T("core.req.control_chars", new { where = Where() }));
                return str;
            }

            case ArgKind.Int:
            {
                if (v is not JsonValue iv || !iv.TryGetValue<int>(out var n) || n < 0 || n > s.MaxItems)
                    throw new HelperRequestException(id, L.T("core.req.int_range", new { where = Where(), max = s.MaxItems }));
                return n;
            }

            case ArgKind.Ports:
            {
                var str = String(id, Where(), v, 200);
                return AppProfiles.NormalizePorts(str) ?? throw new HelperRequestException(id, L.T("core.req.ports", new { where = Where() }));
            }

            case ArgKind.PayloadHex:
            {
                var str = String(id, Where(), v, AppProfiles.MaxSignatureBytes * 2);
                if (AppProfiles.SignatureProblem(0, str, true) is not null || str.Length == 0)
                    throw new HelperRequestException(id, L.T("core.req.payload_hex", new { where = Where() }));
                return str;
            }

            case ArgKind.WdFilter:
            {
                var str = String(id, Where(), v, CustomFilter.MaxLength);
                if (str.Any(char.IsControl)) throw new HelperRequestException(id, L.T("core.req.control_chars", new { where = Where() }));
                return str;
            }

            case ArgKind.Desync:
            {
                var str = String(id, Where(), v, WinwsArgs.MaxRoleLength);
                if (str.Any(char.IsControl)) throw new HelperRequestException(id, L.T("core.req.control_chars", new { where = Where() }));
                return str;
            }

            case ArgKind.ProfileId:
            {
                var str = String(id, Where(), v, 16);
                if (!AppProfiles.IsValidId(str)) throw new HelperRequestException(id, L.T("core.req.profile_id", new { where = Where() }));
                return str;
            }

            case ArgKind.ChangeId:
            {
                var str = String(id, Where(), v, 12);
                if (!ChangeIdRx().IsMatch(str)) throw new HelperRequestException(id, L.T("core.req.change_id", new { where = Where() }));
                return str;
            }

            case ArgKind.ProxyServer:
            {
                var str = String(id, Where(), v, ProxyValues.MaxServerLength);
                return ProxyValues.NormalizeServer(str) ?? throw new HelperRequestException(id, L.T("core.req.proxy_server", new { where = Where() }));
            }

            case ArgKind.ProxyBypass:
            {
                var str = String(id, Where(), v, 2_000);
                var (value, ok) = ProxyValues.NormalizeBypass(str);
                if (!ok || value is null) throw new HelperRequestException(id, L.T("core.req.proxy_bypass", new { where = Where() }));
                return value;
            }

            case ArgKind.ProxyPac:
            {
                var str = String(id, Where(), v, ProxyValues.MaxPacLength);
                var (value, ok) = ProxyValues.NormalizePac(str);
                if (!ok || value is null) throw new HelperRequestException(id, L.T("core.req.pac", new { where = Where() }));
                return value;
            }

            case ArgKind.SiteGroups:
            {
                if (v is not JsonArray arr || arr.Count > SiteGroups.MaxGroups + 1) throw new HelperRequestException(id, L.T("core.req.string_list", new { where = Where() }));
                var groups = new List<SiteGroup>(arr.Count);
                int total = 0;
                for (int i = 0; i < arr.Count; i++)
                {
                    if (arr[i] is not JsonObject row) throw new HelperRequestException(id, L.T("core.req.adv_row", new { where = $"{Where()}[{i}]", shape = "{name, sites}" }));
                    foreach (var (k, _) in row)
                        if (k is not ("name" or "sites")) throw new HelperRequestException(id, L.T("core.req.arg_extra", new { cmd = $"{Where()}[{i}]", name = Short(k) }));
                    var name = row["name"] is null ? "" : row["name"] is JsonValue nv && nv.TryGetValue<string>(out var nameText) && nameText.Length <= SiteGroups.MaxName
                        ? nameText : throw new HelperRequestException(id, L.T("core.req.control_chars", new { where = $"{Where()}[{i}].name" }));
                    if (name.Any(char.IsControl)) throw new HelperRequestException(id, L.T("core.req.control_chars", new { where = $"{Where()}[{i}].name" }));
                    if (row["sites"] is not JsonArray ds) throw new HelperRequestException(id, L.T("core.req.string_list", new { where = $"{Where()}[{i}].sites" }));
                    var sites = new List<string>(ds.Count);
                    foreach (var item in ds)
                    {
                        if (item is null) throw new HelperRequestException(id, L.T("core.req.empty_item", new { where = $"{Where()}[{i}].sites" }));
                        var line = String(id, $"{Where()}[{i}].sites", item, MaxLineLength);
                        if (ForbiddenInLineRx().IsMatch(line)) throw new HelperRequestException(id, L.T("core.req.path_like", new { where = $"{Where()}[{i}].sites" }));
                        sites.Add(line);
                    }
                    total += sites.Count;
                    if (total > s.MaxItems) throw new HelperRequestException(id, L.T("core.req.too_many_lines", new { where = Where(), max = s.MaxItems }));
                    groups.Add(new SiteGroup(name, sites));
                }
                return groups;
            }

            case ArgKind.AdvancedRows:
            {
                if (v is not JsonArray arr) throw new HelperRequestException(id, L.T("core.req.string_list", new { where = Where() }));
                if (arr.Count > s.MaxItems) throw new HelperRequestException(id, L.T("core.req.too_many_lines", new { where = Where(), max = s.MaxItems }));
                var rows = new List<AdvancedRowInput>(arr.Count);
                for (int i = 0; i < arr.Count; i++)
                {
                    if (arr[i] is not JsonObject row) throw new HelperRequestException(id, L.T("core.req.adv_row", new { where = $"{Where()}[{i}]", shape = "{domains, strategy}" }));
                    foreach (var (k, _) in row)
                        if (k is not ("domains" or "strategy")) throw new HelperRequestException(id, L.T("core.req.arg_extra", new { cmd = $"{Where()}[{i}]", name = Short(k) }));
                    if (row["domains"] is not JsonArray ds || ds.Count is 0 or > AdvancedSites.MaxDomains)
                        throw new HelperRequestException(id, L.T("core.req.adv_domains_count", new { where = $"{Where()}[{i}].domains", max = AdvancedSites.MaxDomains }));
                    var domains = new List<string>(ds.Count);
                    foreach (var item in ds)
                    {
                        var d = item is null ? "" : String(id, Where(), item, 253);
                        if (!AdvancedSites.IsDomain(d)) throw new HelperRequestException(id, L.T("core.req.not_site", new { where = $"{Where()}[{i}].domains", value = Short(d) }));
                        if (!domains.Contains(d)) domains.Add(d);
                    }
                    var strategy = row["strategy"] is { } sn ? String(id, Where(), sn, 64) : AdvancedSites.Auto;
                    if (strategy is not (AdvancedSites.Auto or AdvancedSites.Direct) && (!IdRx().IsMatch(strategy) || !catalog.StrategyIds.Contains(strategy)))
                        throw new HelperRequestException(id, L.T("core.req.adv_strategy", new { where = $"{Where()}[{i}].strategy" }));
                    rows.Add(new(domains, strategy));
                }
                return rows;
            }

            case ArgKind.Domains:
            {
                if (v is not JsonArray arr) throw new HelperRequestException(id, L.T("core.req.site_list", new { where = Where() }));
                if (arr.Count > s.MaxItems) throw new HelperRequestException(id, L.T("core.req.too_many_sites", new { where = Where(), max = s.MaxItems }));
                var list = new List<string>(arr.Count);
                foreach (var item in arr)
                {
                    var d = item is null ? "" : String(id, Where(), item, 253);
                    if (!AdvancedSites.IsDomain(d)) throw new HelperRequestException(id, L.T("core.req.not_site", new { where = Where(), value = Short(d) }));
                    if (!list.Contains(d)) list.Add(d);
                }
                return list;
            }

            case ArgKind.Base64Chunk:
            {
                var str = String(id, Where(), v, (MaxOfflineChunkBytes + 2) / 3 * 4);
                byte[] bytes;
                try { bytes = Convert.FromBase64String(str); }
                catch (FormatException) { throw new HelperRequestException(id, L.T("core.req.base64", new { where = Where() })); }
                if (bytes.Length > MaxOfflineChunkBytes) throw new HelperRequestException(id, L.T("core.req.string_len", new { where = Where() }));
                return bytes;
            }

            case ArgKind.SettingsPlan:
            {
                var str = String(id, Where(), v, Export.SettingsSections.MaxRequestChars);
                if (Export.ImportRequest.TryParse(str, out var why) is not { } req) throw new HelperRequestException(id, $"{Where()}: {why}");
                if (req.Verify(null) is { } bad) throw new HelperRequestException(id, $"{Where()}: {bad}");
                return str;
            }

            case ArgKind.Roles:
            {
                if (v is not JsonObject ro || ro.Count == 0 || ro.Count > WinwsArgs.RoleOrder.Length)
                    throw new HelperRequestException(id, L.T("core.req.roles_object", new { where = Where() }));
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
        throw new HelperRequestException(id, L.T("core.req.unknown_kind", new { where = Where() }));
    }

    static string String(long id, string where, JsonNode v, int max)
    {
        if (v is not JsonValue sv || sv.GetValueKind() != JsonValueKind.String) throw new HelperRequestException(id, L.T("core.req.string", new { where }));
        var s = sv.GetValue<string>();
        if (s.Length == 0 || s.Length > max) throw new HelperRequestException(id, L.T("core.req.string_len", new { where }));
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
