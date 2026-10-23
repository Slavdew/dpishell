namespace DpiShell.App;

/// <summary>
/// Темы кнопок «(i)»: ключи строк в DpiShell.Core/Lang/&lt;язык&gt;/info.json (текст — info.&lt;тема&gt;, заголовок — info.&lt;тема&gt;.title).
/// Абзацы — через пустую строку \n\n; первый — ответ «когда включать», в карточке он жирный.
/// Русский текст — источник правды; docs/info-texts.md — копия для чтения, тест окна сверяет её с ru/info.json.
/// </summary>
public static class InfoTexts
{
    public const string Home = "info.home";
    public const string Select = "info.select";
    public const string Sites = "info.sites";
    public const string DomainLists = "info.domain_lists";
    public const string IpLists = "info.ip_lists";
    public const string Game = "info.game";
    public const string Apps = "info.apps";
    public const string Debug = "info.debug";
    public const string Hosts = "info.hosts";
    public const string HostsTry = "info.hosts_try";
    public const string Dns = "info.dns";
    public const string Proxy = "info.proxy";
    public const string Diag = "info.diag";
    public const string RouterMode = "info.router_mode";
    public const string SiteCheck = "info.site_check";
    public const string RouterDns = "info.router_dns";
    public const string RouterWatchdog = "info.router_watchdog";
    public const string Advanced = "info.advanced";
    public const string Joint = "info.joint";
    public const string Service = "info.service";
    public const string AvExclusion = "info.av_exclusion";

    /// <summary>Все темы (для проверок).</summary>
    public static readonly IReadOnlyList<string> All =
        [Home, Select, Sites, DomainLists, IpLists, Game, Apps, Debug, Hosts, HostsTry, Dns, Proxy, Diag, RouterMode, SiteCheck, RouterDns, RouterWatchdog, Advanced, Joint, Service, AvExclusion];

    /// <summary>Текст пояснения на выбранном языке; неизвестная тема — пусто.</summary>
    public static string Get(string? topic) => topic is not null && L.Has(topic) ? L.T(topic) : "";

    /// <summary>Заголовок темы (для экранного диктора и сверки с docs/info-texts.md).</summary>
    public static string Title(string? topic) => topic is not null && L.Has(topic + ".title") ? L.T(topic + ".title") : "";
}
