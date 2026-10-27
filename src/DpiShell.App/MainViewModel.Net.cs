using System.Windows.Input;

namespace DpiShell.App;

/// <summary>
/// Вкладка «Сеть»: блоки DNS → «Прокси» → «Починить адреса», каждый сворачивается (по умолчанию свёрнуты), в заголовке свёрнутого —
/// одна строка состояния. Строки собираются из данных при показе (docs/localization.md). Ссылки со всего окна ведут сюда через <see cref="OpenNet"/>.
/// Здесь же меню слева (свёрнуто до значков) и «Показывать роутер».
/// </summary>
public sealed partial class MainViewModel
{
    bool _dnsBlockOpen, _proxyBlockOpen, _hostsBlockOpen;
    public bool DnsBlockOpen { get => _dnsBlockOpen; set { if (Set(ref _dnsBlockOpen, value)) RaiseNetOpen(); } }
    public bool ProxyBlockOpen { get => _proxyBlockOpen; set { if (Set(ref _proxyBlockOpen, value)) RaiseNetOpen(); } }
    public bool HostsBlockOpen { get => _hostsBlockOpen; set { if (Set(ref _hostsBlockOpen, value)) RaiseNetOpen(); } }

    void RaiseNetOpen() { Raise(nameof(NetAllOpen)); Raise(nameof(NetToggleAllText)); }

    /// <summary>Все три блока раскрыты — «Раскрыть все» становится «Свернуть все».</summary>
    public bool NetAllOpen => DnsBlockOpen && ProxyBlockOpen && HostsBlockOpen;
    public string NetToggleAllText => L.T(NetAllOpen ? "net.collapse_all" : "net.expand_all");
    ICommand? _netToggleAllCmd;
    public ICommand NetToggleAllCmd => _netToggleAllCmd ??= new RelayCommand(() =>
    {
        var open = !NetAllOpen;
        DnsBlockOpen = ProxyBlockOpen = HostsBlockOpen = open;
    });

    /// <summary>Перейти на «Сеть» с раскрытым блоком (ссылки «Открыть →», «Другой DNS», переходы из диагностики).</summary>
    public void OpenNet(NetBlock block)
    {
        switch (block)
        {
            case NetBlock.Dns: DnsBlockOpen = true; break;
            case NetBlock.Proxy: ProxyBlockOpen = true; break;
            case NetBlock.Hosts: HostsBlockOpen = true; break;
        }
        Page = Pages.Net;
    }

    /// <summary>Зайти на «Сеть»: перечитать состояние всех трёх блоков — оно видно и в свёрнутых заголовках.</summary>
    async Task RefreshNetTabAsync()
    {
        var hosts = Hosts.RefreshAsync();
        var proxy = Proxy.RefreshAsync();
        if (DnsRows.Count == 0) _ = CheckDnsAsync();
        await Task.WhenAll(hosts, proxy);
    }

    // ---------- строки состояния в заголовках ----------

    /// <summary>«Google, защищённый · Ethernet · подмены нет».</summary>
    public string NetDnsStatus
    {
        get
        {
            var parts = new List<string>();
            if (PcDnsNow() is { Length: > 0 } now) parts.Add(now);
            if (_dnsList is { Length: > 0 } list)
            {
                if (DnsSpoofed) parts.Add(L.T("net.dns.spoofed"));
                else if (!list.All(c => c.Doh.Length == 0)) parts.Add(L.T("net.dns.clean"));
            }
            return parts.Count > 0 ? string.Join(" · ", parts) : L.T("net.status.unknown");
        }
    }
    public string NetProxyStatus => Proxy.NetStatus;
    public string NetHostsStatus => Hosts.NetStatus;

    void InitNet()
    {
        Proxy.PropertyChanged += (_, _) => Raise(nameof(NetProxyStatus));
        Proxy.Items.CollectionChanged += (_, _) => Raise(nameof(NetProxyStatus));
        Hosts.PropertyChanged += (_, _) => Raise(nameof(NetHostsStatus));
        PcDns.PropertyChanged += (_, _) => Raise(nameof(NetDnsStatus));
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DnsSummary) or nameof(DnsSpoofed) or nameof(DnsChecking)) Raise(nameof(NetDnsStatus));
        };
    }

    // ---------- меню слева ----------

    /// <summary>Меню свёрнуто до значков; запоминается в настройках окна (window.json). По умолчанию раскрыто.</summary>
    public bool NavCollapsed
    {
        get => Settings.NavCollapsed;
        set
        {
            if (Settings.NavCollapsed == value) return;
            Settings.NavCollapsed = value;
            SaveSettings();
            Raise(); Raise(nameof(NavToggleTip));
        }
    }
    ICommand? _toggleNavCmd;
    public ICommand ToggleNavCmd => _toggleNavCmd ??= new RelayCommand(() => NavCollapsed = !NavCollapsed);
    /// <summary>Подсказка и имя кнопки «Свернуть меню» / «Развернуть меню».</summary>
    public string NavToggleTip => L.T(NavCollapsed ? "common.nav.expand" : "common.nav.collapse");

    // ---------- «Показывать инструменты для опытных» ----------

    /// <summary>Свой фильтр WinDivert в профилях («Игры и программы»). Выключено по умолчанию; значение держит и окно профилей.</summary>
    public bool ExpertTools
    {
        get => Settings.ExpertTools;
        set
        {
            if (Settings.ExpertTools == value) return;
            Settings.ExpertTools = value;
            Apps.ExpertTools = value;
            SaveSettings();
            Raise();
        }
    }

    // ---------- «Показывать роутер» ----------

    /// <summary>
    /// «Показывать роутер» (Настройки), по умолчанию включено. Снято — окно такое же, как при ненайденном роутере: пункта «Роутер» нет,
    /// поиск и обращения к роутеру не идут (одна точка: <see cref="RouterViewModel.Enabled"/>).
    /// </summary>
    public bool ShowRouter
    {
        get => Settings.ShowRouter;
        set
        {
            if (Settings.ShowRouter == value) return;
            Settings.ShowRouter = value;
            SaveSettings();
            Router.ShowRouterChanged();
            if (!value && Page == Pages.Router) Page = Pages.Home;
            Raise();
        }
    }
}
