using System.Text.Json;
using System.Text.Json.Serialization;
using DpiShell.Core.Helper;
using DpiShell.Core.Localization;

namespace DpiShell.Core.Export;

/// <summary>Как сливать загруженное со своим: «добавить к своим» или «заменить».</summary>
public enum ImportMode { Add, Replace }

/// <summary>
/// Разделы выгрузки настроек и их предельные размеры. Формат — docs/settings-export.md (общий с роутером OpenWrt: без привязки к путям Windows).
/// Выгружается только то, что человек задал сам; прокси, пароль роутера, история, статистика, результаты подборов, адреса этого ПК,
/// журналы и токены не входят ни в один раздел.
/// </summary>
public static class SettingsSections
{
    public const string Sites = "sites", Exclude = "exclude", Strategies = "strategies", Advanced = "advanced",
        Apps = "apps", Subnets = "subnets", Hosts = "hosts", Dns = "dns";

    /// <summary>Порядок разделов в окне и в manifest.json.</summary>
    public static readonly IReadOnlyList<string> All = [Sites, Exclude, Strategies, Advanced, Apps, Subnets, Hosts, Dns];

    public static bool IsKnown(string? id) => id is not null && All.Contains(id);

    /// <summary>
    /// Имена разделов, которые бывают только в чужих пакетах (роутер, docs/settings-export.md «Расширения»): <c>optimize</c> — на ПК это «Свои сайты»,
    /// <c>mode</c> — режим «все сайты / только из списков» роутера, на ПК такого нет. Окно выгрузки их не знает; в manifest.json при чтении они допустимы.
    /// </summary>
    public const string Optimize = "optimize", Mode = "mode";

    /// <summary>Раздел, который может значиться в manifest.json пакета: свои и «чужие» (<see cref="Optimize"/>, <see cref="Mode"/>).</summary>
    public static bool IsPackageSection(string? id) => IsKnown(id) || id is Optimize or Mode;

    public const string OptimizeFile = "optimize.txt", SitesImportantFile = "sites-important.txt", ModeFile = "mode.json";

    /// <summary>Порядок применения: стратегии раньше строк «сайты → стратегия», профили — после стратегий (варианты параметров), hosts и DNS — в конце.</summary>
    public static readonly IReadOnlyList<string> ApplyOrder = [Sites, Exclude, Subnets, Strategies, Apps, Advanced, Hosts, Dns];

    public const int FormatVersion = 1;

    // Имена файлов и папок.
    public const string ManifestFile = "manifest.json", SitesFile = "sites.txt", ExcludeFile = "exclude.txt", AdvancedFile = "advanced.json",
        SubnetsFile = "subnets.txt", SubnetsExcludeFile = "subnets-exclude.txt", HostsFile = "hosts.txt", DnsFile = "dns.json",
        StrategiesDir = "strategies", AppsDir = "apps";

    // Пределы (по ним файлы чужих людей отсекаются до разбора).
    public const int MaxManifestBytes = 16 * 1024;
    public const int MaxSites = HelperCommands.MaxUserTargets, MaxExclude = AdvancedSites.MaxExclude, MaxSubnets = IpsetValidator.MaxUserLines,
        MaxHosts = HostsPaste.MaxEntries, MaxStrategies = UserStrategies.MaxPersonal, MaxApps = AppProfiles.MaxProfiles;
    public const int SitesBytes = 100 * 1024, ExcludeBytes = 50 * 1024, SubnetsBytes = 512 * 1024, HostsBytes = 64 * 1024,
        JsonBytes = 64 * 1024, StrategyBytes = 16 * 1024;
    /// <summary>Строк в файле сверх допустимого числа записей — на комментарии и пустые.</summary>
    public const int CommentLines = 200;
    public const int MaxProvider = 60, MaxNote = 500;
    /// <summary>Запрос settings_import в знаках: подсети до 10 000 строк и остальное с запасом.</summary>
    public const int MaxRequestChars = 3 * 1024 * 1024;

    public static int MaxItems(string section) => section switch
    {
        Sites => MaxSites, Exclude => MaxExclude, Strategies => MaxStrategies, Advanced => AdvancedSites.MaxRows, Apps => MaxApps,
        Subnets => MaxSubnets, Hosts => MaxHosts, _ => 1,
    };

    /// <summary>Раздел по умолчанию без галочки: подмена адресов в hosts — путь к фишингу, окно показывает строки и просит отметить сознательно. Чужой DNS-сервер — тоже.</summary>
    public static bool IsRisky(string section, DnsChoice? dns = null) =>
        section == Hosts || section == Dns && dns?.Choice == DnsPresets.Custom;

    public static string Title(string section) => section switch
    {
        Sites => L.T("export.section.sites"),
        Exclude => L.T("export.section.exclude"),
        Strategies => L.T("export.section.strategies"),
        Advanced => L.T("export.section.advanced"),
        Apps => L.T("export.section.apps"),
        Subnets => L.T("export.section.subnets"),
        Hosts => L.T("export.section.hosts"),
        Dns => L.T("export.section.dns"),
        Optimize => L.T("export.section.optimize"),
        Mode => L.T("export.section.mode"),
        _ => section,
    };

    public static readonly JsonSerializerOptions Json = new(HelperProtocol.Json)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
    };
}

/// <summary>Выбранный вариант DNS: id готового или «custom» со своими адресами (и шаблоном DoH).</summary>
public sealed class DnsChoice
{
    public string Choice { get; set; } = "";
    public List<string>? Ips { get; set; }
    public string? Doh { get; set; }

    public bool Same(DnsChoice? o) =>
        o is not null && Choice == o.Choice && (Ips ?? []).SequenceEqual(o.Ips ?? []) && (Doh ?? "") == (o.Doh ?? "");

    public string Describe() => Choice == DnsPresets.Custom ? string.Join(", ", Ips ?? []) : DnsPresets.Find(Choice)?.Title ?? Choice;
}

/// <summary>
/// Свои данные этого ПК, как их видит помощник (то, что окно без прав прочитать не может), плюс сведения о ПК, нужные для проверки чужого файла:
/// какие файлы есть в папке фейков, какие стратегии известны и какие варианты параметров есть у профилей. Адресов этого ПК, прокси и истории здесь нет.
/// </summary>
public sealed class SettingsState
{
    public List<string> Sites { get; set; } = [];
    /// <summary>Группы своих сайтов (API 1.18): домен → имя группы; сайты «Прочее» не перечислены. null — групп нет.</summary>
    public Dictionary<string, string>? SiteGroups { get; set; }
    public List<string> Exclude { get; set; } = [];
    /// <summary>Все известные стратегии (готовые и свои): готовые нужны для сверки «такая уже есть», выгружаются только свои и найденные.</summary>
    public List<Strategy> Strategies { get; set; } = [];
    public AdvancedConfig Advanced { get; set; } = new();
    public List<AppProfile> Apps { get; set; } = [];
    public List<string> Subnets { get; set; } = [];
    public List<string> SubnetsExclude { get; set; } = [];
    /// <summary>Свои строки hosts в виде «адрес имя».</summary>
    public List<string> Hosts { get; set; } = [];
    public DnsChoice? Dns { get; set; }
    // Сведения о ПК для проверки чужого файла.
    public List<string> FakeFiles { get; set; } = [];
    public List<ProfileVariant> Variants { get; set; } = [];

    /// <summary>Выгружаются свои и найденные поиском; готовые из каталога и смешанные (собираются программой) — нет.</summary>
    public static bool IsExportable(Strategy s) => s.Source == "search" || UserStrategies.IsPersonal(s.Source);

    public IEnumerable<Strategy> Exportable => Strategies.Where(IsExportable);
}

// ---------- проверенное содержимое пакета (оно же уходит помощнику) ----------

public sealed class ImportStrategy
{
    /// <summary>Имя файла без «.txt»: на него ссылаются строки «своих настроек для сайтов».</summary>
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    public Dictionary<string, string> Roles { get; set; } = [];
}

public sealed class ImportAdvancedRow
{
    public List<string> Domains { get; set; } = [];
    /// <summary>auto, direct или id стратегии из каталога; пусто — стратегия по <see cref="StrategyFile"/>.</summary>
    public string? Strategy { get; set; }
    public string? StrategyFile { get; set; }
}

public sealed class ImportAdvanced
{
    public bool Enabled { get; set; }
    public string Others { get; set; } = AdvancedSites.OthersLists;
    public List<ImportAdvancedRow> Rows { get; set; } = [];
}

public sealed class ImportApp
{
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    public string Tcp { get; set; } = "";
    public string Udp { get; set; } = "";
    public List<string> Addresses { get; set; } = [];
    public string TcpDesync { get; set; } = "";
    public string UdpDesync { get; set; } = "";
}

/// <summary>Содержимое пакета после проверки. Раздел null — в пакете его нет или он пропущен.</summary>
public sealed class ImportData
{
    public List<string>? Sites { get; set; }
    /// <summary>Группы своих сайтов (необязательно): домен → имя. В файле — строки «# group: Имя» перед сайтами группы в sites.txt; для остальных читателей это комментарии.</summary>
    public Dictionary<string, string>? SiteGroups { get; set; }
    public List<string>? Exclude { get; set; }
    public List<string>? Subnets { get; set; }
    public List<string>? SubnetsExclude { get; set; }
    public List<ImportStrategy>? Strategies { get; set; }
    public ImportAdvanced? Advanced { get; set; }
    public List<ImportApp>? Apps { get; set; }
    public List<string>? Hosts { get; set; }
    public DnsChoice? Dns { get; set; }

    public bool Has(string section) => section switch
    {
        SettingsSections.Sites => Sites is not null,
        SettingsSections.Exclude => Exclude is not null,
        SettingsSections.Subnets => Subnets is not null || SubnetsExclude is not null,
        SettingsSections.Strategies => Strategies is not null,
        SettingsSections.Apps => Apps is not null,
        SettingsSections.Advanced => Advanced is not null,
        SettingsSections.Hosts => Hosts is not null,
        SettingsSections.Dns => Dns is not null,
        _ => false,
    };

    public int Count(string section) => section switch
    {
        SettingsSections.Sites => Sites?.Count ?? 0,
        SettingsSections.Exclude => Exclude?.Count ?? 0,
        SettingsSections.Subnets => (Subnets?.Count ?? 0) + (SubnetsExclude?.Count ?? 0),
        SettingsSections.Strategies => Strategies?.Count ?? 0,
        SettingsSections.Apps => Apps?.Count ?? 0,
        SettingsSections.Advanced => Advanced?.Rows.Count ?? 0,
        SettingsSections.Hosts => Hosts?.Count ?? 0,
        SettingsSections.Dns => Dns is null ? 0 : 1,
        _ => 0,
    };

    /// <summary>Только выбранные разделы.</summary>
    public ImportData Only(IReadOnlyCollection<string> sections) => new()
    {
        Sites = sections.Contains(SettingsSections.Sites) ? Sites : null,
        SiteGroups = sections.Contains(SettingsSections.Sites) ? SiteGroups : null,
        Exclude = sections.Contains(SettingsSections.Exclude) ? Exclude : null,
        Subnets = sections.Contains(SettingsSections.Subnets) ? Subnets : null,
        SubnetsExclude = sections.Contains(SettingsSections.Subnets) ? SubnetsExclude : null,
        Strategies = sections.Contains(SettingsSections.Strategies) ? Strategies : null,
        Advanced = sections.Contains(SettingsSections.Advanced) ? Advanced : null,
        Apps = sections.Contains(SettingsSections.Apps) ? Apps : null,
        Hosts = sections.Contains(SettingsSections.Hosts) ? Hosts : null,
        Dns = sections.Contains(SettingsSections.Dns) ? Dns : null,
    };

    public string Canon() => JsonSerializer.Serialize(this, SettingsSections.Json);
}

/// <summary>Манифест пакета. <c>sections</c> — какие разделы выгружены; <c>provider</c> и <c>note</c> необязательны.</summary>
public sealed class SettingsManifest
{
    public int Format { get; set; }
    public string App { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public string Created { get; set; } = "";
    public List<string> Sections { get; set; } = [];
    public string? Provider { get; set; }
    public string? Note { get; set; }
}

/// <summary>Что дописывает выгружающий в manifest.json (необязательное).</summary>
public sealed record ExportMeta(string? Provider = null, string? Note = null, DateTime? Created = null, string? AppVersion = null);

/// <summary>Раздел в окне выгрузки: число записей («Свои сайты: 12»), признак риска и сами записи (показать, что уйдёт).</summary>
public sealed record SectionPreview(string Id, string Title, int Count, bool Risky, IReadOnlyList<string> Items);

/// <summary>Проблема, найденная при чтении или разборе: раздел, файл, причина одной фразой.</summary>
public sealed record SectionProblem(string Section, string? File, string Reason)
{
    public override string ToString() => File is null ? Reason : $"{File}: {Reason}";
}
