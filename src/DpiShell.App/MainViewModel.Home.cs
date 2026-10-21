using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using DpiShell.Core;

namespace DpiShell.App;

/// <summary>Отметка группы в итоге «Проверить сейчас» на главной: «✓ YouTube».</summary>
public sealed record CheckGroupMark(string Title, bool Ok, bool Browser = false)
{
    /// <summary>⚠ — открывается программой, но не браузером (DNS системы ведёт не туда).</summary>
    public string Mark => !Ok ? "✗" : Browser ? "⚠" : "✓";
}

/// <summary>Место в окне, куда прокрутить по ссылке (<see cref="MainViewModel.ShowRequested"/>).</summary>
public enum Spot { Strategies, PcDns }

/// <summary>
/// Главная по варианту A макета (prototypes/combined, снимки A-*, A2-*): карточка статуса с круглым значком, одна большая кнопка ПК,
/// строки «Этот ПК» и «Роутер» со стратегиями и значками состояния; под ними — плитки сайтов или панель подробностей ПК / роутера;
/// внизу — «Проверяется само». Отдельной карточки помех нет: её пункты — в панелях (данные — из тех же моделей, новых проверок нет).
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Показать место на странице: окно прокручивает к нему (и раскрывает список DNS). Ссылка не должна «ничего не делать» (UX-аудит 2, п. 2, 4).</summary>
    public event Action<Spot>? ShowRequested;

    void InitHome()
    {
        CheckRows.CollectionChanged += (_, _) => RebuildCheckGroups();
        Updates.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(UpdatesViewModel.DataAvailable)) RaiseIssues(); };
        Router.PropertyChanged += OnRouterHomeChanged;
        Router.JointChanged += RaiseIssues;
        Hosts.PropertyChanged += (_, e) => { if (e.PropertyName is "" or null or nameof(HostsViewModel.HasWarnings) or nameof(HostsViewModel.Enabled)) RaiseIssues(); };
    }

    void OnRouterHomeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is { Length: > 0 } p && !RouterViewModel.HomeProps.Contains(p)) return;
        RaiseIssues();
    }

    // ---------- выключатель ----------

    /// <summary>Обход делает роутер, на ПК выключен: кнопка ПК — «Включить и на ПК», без выделения цветом (действие не нужно).</summary>
    public bool PcSecondary => !IsRunning && Router.Found && Router.RouterEnabled;
    public bool PcPrimary => !PcSecondary;
    /// <summary>Цветом кнопка выделена, только когда нужно действие: оптимизация выключена и роутер её не ведёт.</summary>
    public bool PcAccent => !IsRunning && !PcSecondary;
    public bool PcPlain => !PcAccent;

    // ---------- карточка статуса ----------

    bool _partial;

    /// <summary>Круглый значок: on — галочка, router — роутер, off — питание, partial — «!», error — крестик, busy — стрелка по кругу.</summary>
    public string HeroState => Kind switch
    {
        StatusKind.Busy => "busy",
        StatusKind.Error => "error",
        _ when _partial => "partial",
        StatusKind.On => "on",
        _ when Router.Found && Router.RouterEnabled => "router",
        _ => "off",
    };

    /// <summary>Под заголовком — итог проверки («Открывается 3 из 3 · в 10:02»); проверки нет, идёт действие или ошибка — пояснение состояния.</summary>
    public string HeroDetail
    {
        get
        {
            if (Kind is StatusKind.Busy or StatusKind.Error) return StatusDetail;
            if (Checking) return L.T("home.check.running");
            if (CheckGroups.Count > 0 && CheckSummary.Length > 0) return CheckedAt.Length > 0 ? CheckSummary + " · " + CheckedAt : CheckSummary;
            return StatusDetail;
        }
    }

    // ---------- строки «Этот ПК» и «Роутер» ----------

    public string PcStateText =>
        Selecting ? L.T("home.pc.state.selecting")
        : IsRunning ? (Router.JointOn && Router.Found && !Router.JointIsFallback ? L.T("home.pc.state.on_joint")
            // Совместная работа откатилась (роутер пропал и т. п.) — почему ПК работает сам, тем же местом.
            : Router.JointIsFallback && Router.HasJointStatus ? L.T("home.pc.state.on_status", new { status = Router.JointStatus })
            : L.T("home.pc.state.on"))
        : PcSecondary ? L.T("home.pc.state.off_router")
        : L.T("home.pc.state.off");

    /// <summary>Стратегия ПК — выделяемым текстом; копируется строкой dpishell:1:… (как «Скопировать стратегию» в списке).</summary>
    public string PcStrategyText => CurrentStrategyId is { } id ? StrategyTitle(id) : L.T("home.strategy.none_short");
    public ICommand CopyPcStrategyCmd => _copyPcStrategyCmd ??= new RelayCommand(() => { if (CurrentStrategyId is { } id) CopyStrategyCmd.Execute(id); }, () => CurrentStrategyId is not null);
    ICommand? _copyPcStrategyCmd;

    /// <summary>Строка роутера — только когда DpiShell на роутере найден (и «Показывать роутер» не снят: тогда Found = false).</summary>
    public bool ShowRouterRow => Router.Found;
    public string RouterStateText => Router.HomeStateText;
    public string RouterStrategyText => Router.CurrentStrategyTitle;
    public bool ShowRouterStrategy => Router.Found && Router.Connected;
    public bool ShowRouterToggle => Router.Connected && !Router.CanReEnable && Router.ShowRouterActions;
    public bool ShowRouterReEnable => Router.CanReEnable;
    public bool ShowRouterCheck => Router.Connected && Router.RouterEnabled;
    public bool ShowRouterSelect => Router.Connected && Router.ShowRouterActions;
    public bool ShowRouterLogin => Router.NeedLogin;

    public ICommand GoRouterCmd => _goRouterCmd ??= new RelayCommand(() => Page = Pages.Router);
    ICommand? _goRouterCmd;
    public ICommand GoHostsCmd => _goHostsCmd ??= new RelayCommand(() => OpenNet(NetBlock.Hosts));
    ICommand? _goHostsCmd;

    // ---------- значки состояния и панели ----------

    /// <summary>Пункты «Этот ПК»: сайты, которые не открылись; DNS провайдера; похожие программы; VPN; чужие записи hosts; обновление списков.</summary>
    public IReadOnlyList<HealthItem> PcItems => _pcItems ??= BuildPcItems();
    IReadOnlyList<HealthItem>? _pcItems;

    IReadOnlyList<HealthItem> BuildPcItems()
    {
        {
            var list = new List<HealthItem>();
            if (Router.BothOn)
                list.Add(new(Health.Error, "", L.T("home.warn.both_on"), L.T("home.stop_pc"), Router.StopPcCmd));
            if (CoreProblemText is { } core)
                list.Add(new(Health.Error, L.T("home.pc.core.label"), core, L.T("home.pc.core.fix"), GoDiagCmd));
            // Сайты — те, что ведёт этот ПК (при совместной работе — свои сайты; через роутер — в панели роутера).
            if (IsRunning)
            {
                var mine = PcGroups();
                if (Checking) list.Add(new(Health.None, L.T("home.pc.sites.label"), L.T("home.check.running")));
                else if (mine.Count == 0 && _checkVerdicts is null) list.Add(new(Health.None, L.T("home.pc.sites.label"), L.T("home.pc.sites.not_checked"), L.T("home.check_now"), CheckNowCmd));
                else
                {
                    var bad = mine.Where(g => !g.Ok).ToList();
                    foreach (var g in bad)
                        list.Add(new(Health.Error, g.Title, L.T("home.pc.site.closed"), L.T("router.select"), GoSelectCmd, Short: L.T("home.pc.site.short", new { group = g.Title })));
                    if (bad.Count == 0 && mine.Count > 0)
                        list.Add(new(Health.Ok, L.T("home.pc.sites.label"), L.T("home.pc.sites.ok", new { ok = mine.Count, total = mine.Count })));
                }
            }
            // DNS: браузер не откроет из-за DNS — кнопка защищённого DNS; запись hosts — на «Починить адреса».
            var browser = CheckRows.Where(r => r.HasBrowser).ToList();
            var dnsGroups = string.Join(", ", browser.Where(r => r.Browser is BrowserPath.DnsReason or BrowserPath.WindowsOnlyReason).Select(r => r.Group).Distinct());
            if (dnsGroups.Length > 0)
                list.Add(new(Health.Warn, L.T("web.diag.dns_label"), L.T("home.pc.dns.browser", new { groups = dnsGroups }), RecommendedDnsText, FixDnsCmd, Shield: true));
            else if (DnsSpoofed)
                list.Add(new(Health.Warn, L.T("web.diag.dns_label"), L.T("home.pc.dns.spoofed"), RecommendedDnsText, FixDnsCmd, Shield: true));
            else if (_dnsList is not null)
                list.Add(new(Health.Ok, L.T("home.pc.dns.label"), L.T("home.pc.dns.clean")));
            var hostsGroups = string.Join(", ", browser.Where(r => r.Browser == BrowserPath.HostsReason).Select(r => r.Group).Distinct());
            if (hostsGroups.Length > 0)
                list.Add(new(Health.Warn, L.T("home.pc.hosts.label"), L.T("home.pc.hosts.browser", new { groups = hostsGroups }), L.T("home.pc.open_hosts"), GoHostsCmd));
            list.Add(HasConflicts
                ? new(Health.Error, L.T("router.diag.conflicts"), L.T("home.pc.conflicts", new { names = ConflictNames }), L.T("home.stop"), StopConflictsCmd, Shield: true,
                    Short: L.T("home.conflict.warn", new { names = ConflictNames }))
                : new(Health.Ok, L.T("router.diag.conflicts"), L.T("home.pc.none")));
            list.Add(HasVpn ? new(Health.Warn, L.T("home.pc.vpn.label"), L.T("home.pc.vpn.on", new { names = _vpnNames }), L.T("home.vpn.dismiss"), DismissVpnCmd)
                : _vpnNames.Length > 0 ? new(Health.Ok, L.T("home.pc.vpn.label"), L.T("home.pc.vpn.known", new { names = _vpnNames }))
                : new(Health.Ok, L.T("home.pc.vpn.label"), L.T("home.pc.vpn.off")));
            if (Hosts.ForeignHosts is { } foreign)
                list.Add(foreign.Count > 0
                    ? new(Health.Warn, L.T("home.pc.foreign.label"), string.Join(", ", foreign), L.T("home.pc.open_hosts"), GoHostsCmd)
                    : new(Health.Ok, L.T("home.pc.foreign.label"), L.T("home.pc.none")));
            if (Updates.DataAvailable)
                list.Add(new(Health.Warn, L.T("home.pc.lists.label"), Updates.AvailableText, L.T("home.pc.lists.go"), GoAboutCmd));
            return list;
        }
    }

    /// <summary>Пункты «Роутер»: сайты через роутер, ускорение сети, IPv6, DNS роутера, сторож (тексты — ключами web.diag.*).</summary>
    public IReadOnlyList<HealthItem> RouterItems => _routerItems ??= Router.HomeItems(RouterFailingGroups());
    IReadOnlyList<HealthItem>? _routerItems;

    /// <summary>Группы проверки, которые ведёт этот ПК (по ним — пункты «Сайты» панели ПК).</summary>
    List<CheckGroupMark> PcGroups() => GroupsOf(router: false);

    List<string> RouterFailingGroups() => Checking ? [] : GroupsOf(router: true).Where(g => !g.Ok).Select(g => g.Title).ToList();

    List<CheckGroupMark> GroupsOf(bool router)
    {
        if (_checkVerdicts is not { } v) return [];
        return v.Where(x => !Names.Control(x.GroupId)).GroupBy(x => x.GroupId)
            .Where(g => Router.LeadsGroup(g.Key, IsRunning) == router && (router || IsRunning))
            .Select(g => new CheckGroupMark(Names.Title(g.Key), g.All(x => x.Ok || !x.Target.Required) && g.Any(x => x.Ok), g.Any(x => x.BrowserFails)))
            .ToList();
    }

    public Health PcHealth => HealthItem.Worst(PcItems);
    public string PcHealthTip => HealthItem.Tip(PcItems);
    public Health RouterHealth => HealthItem.Worst(RouterItems);
    public string RouterHealthTip => HealthItem.Tip(RouterItems);
    public bool ShowRouterHealth => Router.Found && RouterItems.Count > 0;

    HomePanel _panel;
    /// <summary>Что на месте плиток: плитки сайтов или панель «Этот ПК: …» / «Роутер: …». Повторный щелчок по значку возвращает плитки.</summary>
    public HomePanel Panel
    {
        get => _panel;
        set
        {
            if (!Set(ref _panel, value)) return;
            if (value == HomePanel.Router && Router.Connected && !Router.DiagLoaded && !Router.Busy) _ = Router.RefreshDiagAsync();
            RaisePanel();
        }
    }
    public bool ShowTiles => _panel == HomePanel.None;
    public bool ShowPanel => _panel != HomePanel.None;
    public bool PcPanelOpen => _panel == HomePanel.Pc;
    public bool RouterPanelOpen => _panel == HomePanel.Router;
    public IReadOnlyList<HealthItem> PanelItems => _panel == HomePanel.Router ? RouterItems : _panel == HomePanel.Pc ? PcItems : [];
    public string PanelTitle => _panel switch
    {
        HomePanel.Pc => L.T("home.panel.pc", new { state = Lower(HealthItem.Tip(PcItems)) }),
        HomePanel.Router => L.T("home.panel.router", new { state = Lower(HealthItem.Tip(RouterItems)) }),
        _ => "",
    };

    static string Lower(string s) => s.Length == 0 ? s : char.ToLower(s[0], L.Culture) + s[1..];

    public ICommand TogglePcPanelCmd => _togglePcPanelCmd ??= new RelayCommand(() => Panel = Panel == HomePanel.Pc ? HomePanel.None : HomePanel.Pc);
    ICommand? _togglePcPanelCmd;
    public ICommand ToggleRouterPanelCmd => _toggleRouterPanelCmd ??= new RelayCommand(() => Panel = Panel == HomePanel.Router ? HomePanel.None : HomePanel.Router);
    ICommand? _toggleRouterPanelCmd;
    public ICommand ClosePanelCmd => _closePanelCmd ??= new RelayCommand(() => Panel = HomePanel.None);
    ICommand? _closePanelCmd;

    void RaisePanel()
    {
        foreach (var p in new[] { nameof(ShowTiles), nameof(ShowCheckDetails), nameof(ShowPanel), nameof(PcPanelOpen), nameof(RouterPanelOpen), nameof(PanelItems), nameof(PanelTitle) })
            Raise(p);
    }

    // ---------- плитки сайтов ----------

    DateTime? _checkedTime;

    /// <summary>
    /// Плитки по группам проверки (кроме контрольной): цветная полоса, «Открывается» / «В браузере не откроется» / «Не открывается»,
    /// ниже — время и кто ведёт («через роутер» / «этот ПК»). До проверки — серые «не проверялось».
    /// </summary>
    public IReadOnlyList<SiteTile> Tiles => _tiles ??= BuildTiles();
    IReadOnlyList<SiteTile>? _tiles;

    IReadOnlyList<SiteTile> BuildTiles()
    {
        {
            var tiles = new List<SiteTile>();
            if (_checkVerdicts is { } v && !Checking)
            {
                foreach (var g in v.Where(x => !Names.Control(x.GroupId)).GroupBy(x => x.GroupId))
                {
                    var items = g.ToList();
                    bool ok = items.All(x => x.Ok || !x.Target.Required) && items.Any(x => x.Ok);
                    var browser = items.FirstOrDefault(x => x.BrowserFails);
                    int opened = items.Count(x => x.Ok);
                    var secs = items.Where(x => x.Ok).Select(x => x.BestSeconds).DefaultIfEmpty(0).Average();
                    var time = opened > 0 ? L.T(items.Count > 1 && g.Key == "user" ? "home.tile.avg" : "home.tile.time", new { sec = secs.ToString("0.0", L.Culture) }) : "";
                    var who = Who(g.Key);
                    string Join(params string[] parts) => string.Join(" · ", parts.Where(p => p.Length > 0));
                    if (!ok)
                        tiles.Add(new(Names.Title(g.Key), Health.Error,
                            items.Count > 1 && g.Key == "user" && opened > 0 ? L.T("home.tile.some", new { ok = opened, total = items.Count }) : L.T("home.tile.closed"), Join(who)));
                    else if (browser is not null)
                        tiles.Add(new(Names.Title(g.Key), Health.Warn, L.T("home.tile.browser"), Join(Texts.BrowserReason(browser.Browser!.Reason), who)));
                    else
                        tiles.Add(new(Names.Title(g.Key), Health.Ok,
                            items.Count > 1 && g.Key == "user" ? L.T("home.tile.some", new { ok = opened, total = items.Count }) : L.T("home.tile.opens"), Join(time, who)));
                }
                return tiles;
            }
            foreach (var g in CheckedGroups().Where(g => !g.Control))
                tiles.Add(new(Names.Title(g.Id), Health.None, L.T(Checking ? "home.check.running" : "home.tile.not_checked"), ""));
            return tiles;
        }
    }

    /// <summary>Кто ведёт группу: через роутер / этот ПК; оптимизации нет — «без оптимизации». Без роутера при работающем ПК — не пишется.</summary>
    string Who(string groupId)
    {
        if (Router.LeadsGroup(groupId, IsRunning)) return L.T("home.tile.via_router");
        if (IsRunning) return Router.Found ? L.T("home.tile.via_pc") : "";
        return L.T("home.tile.no_optimization");
    }

    /// <summary>Четвёртая плитка: «Проверено в 10:02» и «Подробнее» (строки по адресам).</summary>
    public string TilesMeta => Checking ? L.T("home.check.running") : _checkedTime is { } t ? L.T("home.tile.checked_at", new { time = L.Time(t) }) : L.T("home.tile.never");

    bool _checkDetailsOpen;
    /// <summary>«Подробнее» у плиток: адреса последней проверки и ⚠ про браузер.</summary>
    public bool CheckDetailsOpen { get => _checkDetailsOpen; set { if (Set(ref _checkDetailsOpen, value)) Raise(nameof(ShowCheckDetails)); } }
    public ICommand ToggleCheckDetailsCmd => _toggleCheckDetailsCmd ??= new RelayCommand(() => CheckDetailsOpen = !CheckDetailsOpen);
    ICommand? _toggleCheckDetailsCmd;
    public bool HasCheckDetails => CheckRows.Count > 0;
    /// <summary>Подробности проверки видны: раскрыты «Подробнее», на месте панели — плитки и есть что показать.</summary>
    public bool ShowCheckDetails => _checkDetailsOpen && ShowTiles && HasCheckDetails;

    // ---------- «Проверяется само» ----------

    /// <summary>Что программа проверяет сама и как часто — из настроек, которые есть (сторож ПК, расписание и сторож роутера, hosts, обновления).</summary>
    public IReadOnlyList<AutoRow> AutoRows => _autoRows ??= BuildAutoRows();
    IReadOnlyList<AutoRow>? _autoRows;

    IReadOnlyList<AutoRow> BuildAutoRows()
    {
        {
            var rows = new List<AutoRow>
            {
                new(L.T("home.auto.sites"), L.T(IsRunning ? "home.auto.sites_on" : "home.auto.sites_off", new { minutes = (int)WatchdogPolicy.Interval.TotalMinutes })),
                new(L.T("home.auto.failed"), L.T("home.auto.failed_value")),
            };
            if (Router.Found && Router.Connected)
            {
                var parts = new List<string>();
                if (Router.WatchdogAvailable) parts.Add(L.T(Router.WatchdogEnabled ? "home.auto.router_wd_on" : "home.auto.router_wd_off"));
                if (Router.ScheduleEnabled && RouterViewModel.ScheduleOptions.FirstOrDefault(o => o.Hours == Router.ScheduleHours) is { } o)
                    parts.Add(L.T("home.auto.router_schedule", new { every = o.Title }));
                if (parts.Count > 0) rows.Add(new(L.T("home.auto.router"), string.Join("; ", parts)));
            }
            rows.Add(new(L.T("home.auto.hosts"), L.T(Hosts.Enabled ? "home.auto.hosts_on" : "home.auto.hosts_off")));
            rows.Add(new(L.T("home.auto.updates"), L.T("home.auto.updates_value")));
            return rows;
        }
    }

    // ---------- стратегии ----------

    bool _showStrategies;
    /// <summary>Список стратегий на главной раскрыт («Сменить стратегию»). По умолчанию свёрнут: менять руками нужно редко.</summary>
    public bool ShowStrategies
    {
        get => _showStrategies;
        set
        {
            if (!Set(ref _showStrategies, value)) return;
            Raise(nameof(StrategiesLinkText));
            if (value) ShowRequested?.Invoke(Spot.Strategies);
        }
    }
    public string StrategiesLinkText => L.T(ShowStrategies ? "home.strategies.hide" : "home.strategies.show");
    public ICommand ToggleStrategiesCmd => _toggleStrategiesCmd ??= new RelayCommand(() => ShowStrategies = !ShowStrategies);
    ICommand? _toggleStrategiesCmd;

    // ---------- итог «Проверить сейчас» ----------

    /// <summary>По группе: открылось всё — ✓. Строится из строк проверки (адреса — в «Подробнее»).</summary>
    public ObservableCollection<CheckGroupMark> CheckGroups { get; } = [];
    public bool HasCheck => CheckRows.Count > 0 || Checking;

    readonly LiveText _checkedAt = new();
    /// <summary>«в 14:05» — когда проверяли (время хранится, текст собирается при показе).</summary>
    public string CheckedAt { get => _checkedAt.Value; private set { _checkedAt.Set(value); Raise(); Raise(nameof(HeroDetail)); } }
    void CheckedAtIs(Func<string> build) { _checkedAt.Set(build); Raise(nameof(CheckedAt)); Raise(nameof(HeroDetail)); }

    void RebuildCheckGroups()
    {
        CheckGroups.Clear();
        // Как в подборе: группа открывается, если открылись её обязательные адреса (необязательный провал — только в «Подробнее»).
        foreach (var g in CheckRows.GroupBy(r => r.Group)) CheckGroups.Add(new(g.Key, g.All(r => r.Ok || !r.Alert), g.Any(r => r.HasBrowser)));
        Raise(nameof(HasCheck)); Raise(nameof(HasCheckDetails));
        Raise(nameof(BrowserWarning)); Raise(nameof(HasBrowserWarning)); Raise(nameof(ShowBrowserDnsFix));
        RaiseIssues();
    }

    /// <summary>«⚠ В браузере не откроется: YouTube — DNS провайдера подменяет адрес.» Пусто — браузер открывает то же, что программа.</summary>
    public string BrowserWarning
    {
        get
        {
            var bad = CheckRows.Where(r => r.HasBrowser).ToList();
            if (bad.Count == 0) return "";
            // Явная подмена и hosts — «в браузере не откроется»; просто «через DNS Windows не открылось» — про программы, берущие адрес у Windows.
            var parts = bad.GroupBy(r => r.Browser).Select(g =>
            {
                var groups = string.Join(", ", g.Select(r => r.Group).Distinct());
                // Причина — константа ядра: по её значению выбираем целую фразу (не склейку).
                return L.T(g.Key == BrowserPath.WindowsOnlyReason ? "home.browser.windows_only" : g.Key == BrowserPath.HostsReason ? "home.browser.hosts" : "home.browser.dns",
                    new { groups });
            });
            return "⚠ " + string.Join(" ", parts);
        }
    }
    public bool HasBrowserWarning => CheckRows.Any(r => r.HasBrowser);
    /// <summary>Кнопка «Включить защищённый DNS» — когда виноват DNS (подмена или просто не открылось через DNS Windows), не hosts.</summary>
    public bool ShowBrowserDnsFix => CheckRows.Any(r => r.Browser is BrowserPath.DnsReason or BrowserPath.WindowsOnlyReason);

    readonly Dictionary<string, object> _homeShown = [];

    /// <summary>Главная пересобирается из данных: значки, панели, плитки, строки ПК и роутера, «Проверяется само».</summary>
    /// <remarks>
    /// Пересобирается часто (каждое обновление роутера и ПК, ход подбора на роутере), поэтому окну сообщается только то, что изменилось:
    /// списки сравниваются по значению (записи), строки и флаги — с прежними. Иначе частые события держат окно занятым перерисовкой.
    /// </remarks>
    void RaiseIssues()
    {
        bool lists = false;
        void List<T>(ref IReadOnlyList<T>? cache, Func<IReadOnlyList<T>> build, string name)
        {
            var fresh = build();
            if (cache is not null && cache.SequenceEqual(fresh)) return;
            cache = fresh;
            lists = true;
            Raise(name);
        }
        List(ref _pcItems, BuildPcItems, nameof(PcItems));
        List(ref _routerItems, () => Router.HomeItems(RouterFailingGroups()), nameof(RouterItems));
        List(ref _tiles, BuildTiles, nameof(Tiles));
        List(ref _autoRows, BuildAutoRows, nameof(AutoRows));
        if (lists) Raise(nameof(PanelItems));
        foreach (var (name, value) in new (string, Func<object>)[]
                 {
                     (nameof(PcHealth), () => PcHealth), (nameof(PcHealthTip), () => PcHealthTip), (nameof(RouterHealth), () => RouterHealth),
                     (nameof(RouterHealthTip), () => RouterHealthTip), (nameof(ShowRouterHealth), () => ShowRouterHealth), (nameof(PanelTitle), () => PanelTitle),
                     (nameof(TilesMeta), () => TilesMeta), (nameof(HasCheckDetails), () => HasCheckDetails), (nameof(ShowCheckDetails), () => ShowCheckDetails),
                     (nameof(HeroState), () => HeroState), (nameof(HeroDetail), () => HeroDetail), (nameof(PcStateText), () => PcStateText),
                     (nameof(PcStrategyText), () => PcStrategyText), (nameof(ShowRouterRow), () => ShowRouterRow), (nameof(RouterStateText), () => RouterStateText),
                     (nameof(RouterStrategyText), () => RouterStrategyText), (nameof(ShowRouterStrategy), () => ShowRouterStrategy), (nameof(ShowRouterToggle), () => ShowRouterToggle),
                     (nameof(ShowRouterReEnable), () => ShowRouterReEnable), (nameof(ShowRouterCheck), () => ShowRouterCheck), (nameof(ShowRouterSelect), () => ShowRouterSelect),
                     (nameof(ShowRouterLogin), () => ShowRouterLogin), (nameof(PcAccent), () => PcAccent), (nameof(PcPlain), () => PcPlain), (nameof(ShowSiteFix), () => ShowSiteFix),
                 })
        {
            var v = value();
            if (_homeShown.TryGetValue(name, out var old) && Equals(old, v)) continue;
            _homeShown[name] = v;
            Raise(name);
        }
        // Роутер пропал или снят «Показывать роутер» — его панель закрывается сама.
        if (_panel == HomePanel.Router && !Router.Found) Panel = HomePanel.None;
    }
}
