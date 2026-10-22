using System.IO;
using System.Net.Http;
using System.Windows.Input;
using DpiShell.Core;
using DpiShell.Core.Router;

namespace DpiShell.App;

/// <summary>
/// Роутер на главной (вариант A макета): строка состояния, выбор стратегии, значок и панель «Роутер: …».
/// Новых проверок нет — только status, его предупреждения и diag, которые окно уже читает.
/// </summary>
public sealed partial class RouterViewModel
{
    /// <summary>Название текущей стратегии роутера — на кнопке выбора на главной.</summary>
    public string CurrentStrategyTitle => StrategyTitle(CurrentStrategyId);

    /// <summary>Параметры текущей стратегии можно скопировать (роутер отдал роли).</summary>
    public bool CanCopyCurrentParams => Strategies.FirstOrDefault(s => s.Id == CurrentStrategyId) is { HasParams: true };

    /// <summary>«Скопировать параметры» текущей стратегии роутера (то же, что пункт меню стратегий).</summary>
    public ICommand CopyCurrentParamsCmd => _copyCurrentParamsCmd ??= new RelayCommand(() =>
    {
        if (Strategies.FirstOrDefault(s => s.Id == CurrentStrategyId) is { HasParams: true } s) CopyParams(s);
        else Say(L.T("home.router.no_params"));
    });
    ICommand? _copyCurrentParamsCmd;

    /// <summary>Включить сторожа с главной (то же, что галочка «Сторож» на вкладке).</summary>
    public ICommand EnableWatchdogCmd => _enableWatchdogCmd ??= new RelayCommand(() => WatchdogEnabled = true, () => CanAct);
    ICommand? _enableWatchdogCmd;

    /// <summary>Совместная работа: есть ли у ПК что вести (свои сайты не роутера, профили программ). null — не узнать.</summary>
    public async Task<bool?> JointHasWorkAsync() => _joint is null ? null : await _joint.HasWorkAsync();

    /// <summary>«Работать вместе» из вопроса при «Включить»: включает совместную работу (и с ней оптимизацию на ПК). true — связка действует.</summary>
    public async Task<bool> StartJointAsync()
    {
        if (_joint is null) return false;
        await SetJointAsync(true);
        return JointOn;
    }

    /// <summary>Роутер умеет «Сайт не открывается» (site_check).</summary>
    public bool CanSiteCheck => Can("site_check");

    /// <summary>
    /// «Проверить сайт» с главной: тот же site_check, что на «Сайтах» (с обходом и без), но итог — вызывающему, поля вкладки «Сайты» не трогаются.
    /// null — роутер занят или не ответил вовремя.
    /// </summary>
    public async Task<RouterSiteCheckResult?> CheckSiteQuietAsync(string url)
    {
        RouterSiteCheckResult? res = null;
        await Guard(L.T("router.site.busy"), async () =>
        {
            await Call(c => c.SiteCheckAsync(url));
            var deadline = DateTime.UtcNow + CheckTimeout;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(PollInterval);
                var r = await Call(c => c.SiteCheckResultAsync());
                if (!r.Running) { res = r; break; }
            }
        }, changes: false);
        Say("");
        return res;
    }

    /// <summary>
    /// Оптимизирует ли роутер этот сайт: режим «Все сайты» — да; «Только из списков» — если сайт (или его родитель) есть в одном из списков
    /// роутера (general, google, discord, свой user-general, строки продвинутого режима). null — не узнать (нет входа, роутер до r9 без встроенных списков).
    /// Режим — как в build_opts роутера: включённый продвинутый режим со своими «остальными» главнее <c>mode</c>.
    /// </summary>
    public async Task<bool?> CoversHostAsync(string host)
    {
        if (!Connected || _status is not { } st) return null;
        try
        {
            var mode = st.Mode ?? "all";
            var rows = new List<string>();
            if (st.Advanced is { Enabled: true } && Can("advanced_get"))
            {
                var a = await Call(c => c.AdvancedGetAsync());
                if (a.Enabled)
                {
                    if (a.Others is "all" or "lists") mode = a.Others;
                    else if (a.Others == "direct") mode = "direct";
                    rows.AddRange(a.Rows.Where(r => r.Strategy != "direct").SelectMany(r => r.Domains));
                }
            }
            if (mode == "all") return true;
            if (Hostlist.Covers(host, rows)) return true;
            if (mode == "direct") return false;   // роли каталога выключены: только строки
            if (!Can("lists_get")) return null;
            var lines = new List<string> { "discord.media" };   // роль discord_media: --hostlist-domains=discord.media
            foreach (var name in new[] { "user-general", "general", "google", "discord" })
                lines.AddRange((await Call(c => c.ListsGetAsync(name))).Lines);
            return Hostlist.Covers(host, lines);
        }
        catch (Exception e) when (e is RouterException or IOException or HttpRequestException or TaskCanceledException) { return null; }
    }

    /// <summary>«Добавить в список оптимизации» с главной: сайт — в свой список роутера «Оптимизировать» (user-general), как поле на «Сайтах».</summary>
    public async Task<bool> AddHostToGeneralAsync(string host)
    {
        if (!Can("lists_set")) return false;
        var wasDirty = SitesDirty;
        if (!await Guard(L.T("router.general.busy"), async () =>
            {
                var lines = _listsLoaded
                    ? ListGeneralText.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList()
                    : (await Call(c => c.ListsGetAsync("user-general"))).Lines.ToList();
                if (!lines.Contains(host, StringComparer.OrdinalIgnoreCase)) lines.Add(host);
                await Call(c => c.ListsSetAsync("user-general", lines));
                if (_listsLoaded) ListGeneralText = string.Join(Environment.NewLine, lines);
            }))
            return false;
        if (!wasDirty) MarkSitesSaved();
        return true;
    }

    /// <summary>Строка «Роутер» на главной — только состояние; подробности — в <see cref="HomeStateTip"/>.</summary>
    public string HomeStateText =>
        Selecting || WdRunning ? L.T("home.router.state.selecting")
        : NeedLogin ? L.T("home.router.state.login")
        : RouterEnabled ? L.T("home.router.state.on")
        : L.T("home.router.state.off");

    /// <summary>Подсказка к строке «Роутер»: «работает · YouTube и Discord · проверено в 10:01».</summary>
    public string HomeStateTip
    {
        get
        {
            var parts = new List<string>();
            if (Selecting || WdRunning) parts.Add(L.T("home.router.state.selecting"));
            else if (NeedLogin) parts.Add(L.T("home.router.state.login"));
            else if (RouterEnabled)
            {
                parts.Add(L.T("home.router.state.on"));
                if (JointOn && !JointIsFallback) parts.Add(L.T("home.router.state.joint"));
                var at = _status?.LastCheck?.At ?? _detection?.Status?.CheckedAt;
                if (Checking) parts.Add(L.T("router.home.checking"));
                else if (at is not null) parts.Add(L.T("router.home.checked", new { time = RouterTexts.Time(at) }));
            }
            else parts.Add(L.T("home.router.state.off"));
            if (!Updating && _status?.Update is { Available: true, Version: { } uv }) parts.Add(L.T("router.home.update_available", new { version = uv }));
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Группы, которые ведёт роутер (для подписи плиток): при совместной работе — всё, кроме своих сайтов ПК.</summary>
    public bool LeadsGroup(string groupId, bool pcRunning) =>
        Found && RouterEnabled && (!pcRunning || JointOn && !JointIsFallback && groupId != "user");

    /// <summary>
    /// Пункты панели «Роутер: …»: сайты через роутер, ускорение сети, IPv6, DNS роутера, сторож, похожие программы и прочие предупреждения.
    /// <paramref name="pcFailing"/> — группы, которые ведёт роутер и которые не открылись в проверке с этого ПК.
    /// </summary>
    public IReadOnlyList<HealthItem> HomeItems(IReadOnlyList<string> pcFailing)
    {
        var list = new List<HealthItem>();
        if (!Found) return list;
        if (NeedLogin)
            list.Add(new(Health.Warn, L.T("home.r.login.label"), L.T("home.r.login.value"), L.T("router.login"), LoginCmd));
        if (HasOff)
            list.Add(new(Health.Error, L.T("home.r.off.label"), OffText, CanReEnable ? ReEnableText : null, CanReEnable ? ReEnableCmd : null));

        // Сайты через роутер: проверка с этого ПК (то, что видит человек) и последняя проверка самого роутера.
        var bad = new List<string>(pcFailing);
        var last = _status?.LastCheck;
        if (RouterEnabled && last is { Ok: false })
            foreach (var g in (last.Groups ?? []).Where(g => !g.Ok).Select(g => GroupTitle(g.Id)))
                if (!bad.Contains(g)) bad.Add(g);
        var when = last?.At is { } at ? L.T("router.home.checked", new { time = RouterTexts.Time(at) }) : "";
        if (RouterEnabled)
        {
            foreach (var g in bad)
                list.Add(new(Health.Error, g, when.Length > 0 ? L.T("home.r.site.closed_at", new { when }) : L.T("home.r.site.closed"),
                    L.T("home.find_on_router"), GoSelectCmd, Short: L.T("home.r.site.short", new { group = g })));
            if (bad.Count == 0 && last is { Ok: true })
            {
                var groups = last.Groups ?? [];
                list.Add(new(Health.Ok, L.T("home.r.sites.label"), groups.Count > 0
                    ? L.T("home.r.sites.ok", new { ok = groups.Count, total = groups.Count, when })
                    : L.T("home.r.sites.ok_plain", new { when })));
            }
            else if (bad.Count == 0 && Connected)
                list.Add(new(Health.None, L.T("home.r.sites.label"), L.T("home.r.sites.not_checked"), L.T("router.check"), CheckCmd));
        }

        var warnings = _status?.Warnings ?? [];
        bool Warned(string code) => warnings.Any(w => w.Code == code);

        // Ускорение сети: из diag (если окно его читало), иначе по предупреждению роутера.
        if (_diag is { } d)
        {
            if (FlowOffloadFixable) list.Add(new(Health.Warn, L.T("router.diag.flow"), L.T("web.diag.flow_on"), L.T("home.r.fix"), FixWarningCmd, "flow_offload"));
            else list.Add(new(Health.Ok, L.T("router.diag.flow"), d.FlowOffload ? L.T("router.diag.flow_on")
                : d.Flowoffload is "software" or "hardware" ? L.T("web.diag.flow_handed") : L.T("web.diag.flow_off")));
            if (Ipv6Fixable) list.Add(new(Health.Warn, L.T("home.r.ipv6.label"), L.T("web.diag.ipv6_bad"), L.T("home.r.fix"), FixWarningCmd, "ipv6"));
            else list.Add(new(Health.Ok, L.T("home.r.ipv6.label"), d.Ipv6Wan ? L.T("web.diag.ipv6_on") : L.T("web.diag.ipv6_none")));
            if (d.Dns is { Spoofed: true } || Warned("dns_spoofed"))
                list.Add(new(Health.Warn, L.T("web.diag.dns_label"), L.T("router.diag.dns_spoofed"), L.T("router.dns.enable"), DnsEnableCmd));
            else if (d.Dns is not null) list.Add(new(Health.Ok, L.T("home.r.dns.label"), L.T("web.diag.dns_clean")));
            if (Conflicts.Count == 0) list.Add(new(Health.Ok, L.T("router.diag.conflicts"), L.T("web.diag.conflicts_none")));
            foreach (var c in Conflicts)
                list.Add(new(Health.Error, L.T("router.diag.conflicts"), L.T("web.diag.conflict_row", new { name = c.Name }),
                    c.CanStop ? L.T("router.diag.stop") : null, c.CanStop ? StopConflictCmd : null, c.Name));
        }
        else
        {
            if (Warned("flow_offload")) list.Add(new(Health.Warn, L.T("router.diag.flow"), L.T("web.diag.flow_on"), L.T("home.r.fix"), FixWarningCmd, "flow_offload"));
            if (Warned("ipv6")) list.Add(new(Health.Warn, L.T("home.r.ipv6.label"), L.T("web.diag.ipv6_bad"), L.T("home.r.fix"), FixWarningCmd, "ipv6"));
            if (Warned("dns_spoofed")) list.Add(new(Health.Warn, L.T("web.diag.dns_label"), L.T("router.diag.dns_spoofed"), L.T("router.dns.enable"), DnsEnableCmd));
        }
        // Остальные предупреждения роутера — строкой как есть (переведены по ключу или коду).
        foreach (var w in WarningRows.Where(r => r.Code is not ("flow_offload" or "ipv6" or "dns_spoofed") && !(r.Code == "conflict" && _diag is not null)))
            list.Add(new(Health.Warn, "", w.Text, w.HasFix ? w.Fix : null, w.HasFix ? FixWarningCmd : null, w.Code));
        if (TimestampsNeedAction)
            list.Add(new(Health.Warn, "", TimestampsNeed, L.T("router.enable"), EnableTimestampsCmd, Shield: true));
        // Обновление — пунктом «!», а не хвостом строки: ставится на вкладке «Информация и обновления», где видны шаги.
        if (!Updating && _status?.Update is { Available: true, Version: { } uv })
            list.Add(new(Health.Warn, L.T("home.r.update.label"), L.T("router.update.available", new { version = uv }), L.T("home.r.update.go"), GoUpdatesCmd));
        if (WatchdogAvailable)
            list.Add(_watchdog ? new(Health.Ok, L.T("home.r.watchdog.label"), L.T("home.r.watchdog.on"))
                : new(Health.Warn, L.T("home.r.watchdog.label"), L.T("home.r.watchdog.off"), L.T("router.enable"), EnableWatchdogCmd));
        return list;
    }

    /// <summary>Свойства, от которых зависит роутер на главной: окно пересобирает строку и панель, когда они меняются.</summary>
    internal static readonly HashSet<string> HomeProps =
    [
        nameof(Found), nameof(Enabled), nameof(Connected), nameof(NeedLogin), nameof(RouterEnabled), nameof(Status), nameof(Busy), nameof(Checking), nameof(Selecting),
        nameof(HasOff), nameof(OffText), nameof(CanReEnable), nameof(DiagLoaded), nameof(FlowOffloadText), nameof(Ipv6Text), nameof(DnsText),
        nameof(WatchdogEnabled), nameof(WatchdogAvailable), nameof(ScheduleEnabled), nameof(ScheduleHours), nameof(JointOn), nameof(JointShown),
        nameof(TimestampsNeedAction), nameof(HomeDetail), nameof(Updating), nameof(WdRunning),
    ];
}
