using System.IO;
using System.Windows.Input;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.App;

/// <summary>
/// «Сайт не открывается?» (UX-аудит 7, п. 5; решение владельца к п. 15): «Проверить сайт» с оптимизацией и без неё, сломался из-за неё — «Добавить в исключения».
/// На этом ПК сайт попадает в простой список «Исключения» на вкладке «Сайты» (user-exclude.txt у помощника, протокол 1.10;
/// в командную строку он идёт как <c>--hostlist-exclude</c> во всех ролях с именами сайтов), на роутере — в его «Исключения». Строки «Напрямую»
/// продвинутого режима не трогаются.
/// </summary>
public sealed partial class MainViewModel
{
    // ---------- Главная: «Сайт не открывается?» → «Проверить сайт» ----------

    /// <summary>Оптимизация работает на этом ПК или через роутер — только тогда есть что отключать для сайта.</summary>
    bool Covered => _active || (Router.Found && Router.RouterEnabled);

    /// <summary>Ссылка «Сайт не открывается?» на карточке — пока оптимизация работает.</summary>
    public bool ShowSiteFix => Covered;

    bool _directOpen;
    /// <summary>Блок «Проверить сайт: с оптимизацией и без неё» раскрыт.</summary>
    public bool DirectOpen { get => _directOpen; set { if (Set(ref _directOpen, value)) Raise(nameof(ShowSiteAgain)); } }

    string _directSite = "";
    public string DirectSite { get => _directSite; set => Set(ref _directSite, value); }

    string _directMessage = "";
    public string DirectMessage { get => _directMessage; private set { if (Set(ref _directMessage, value)) Raise(nameof(HasDirectMessage)); } }
    public bool HasDirectMessage => _directMessage.Length > 0;

    public ICommand ToggleDirectCmd => _toggleDirectCmd ??= new RelayCommand(() => { DirectOpen = !DirectOpen; if (DirectOpen) { DirectMessage = ""; SiteOutcome = SiteCheckOutcome.None; } });
    ICommand? _toggleDirectCmd;

    /// <summary>«Добавить в исключения» после проверки: то же сохранение, что раньше делало «Открыть без оптимизации».</summary>
    public ICommand OpenDirectCmd => _openDirectCmd ??= RelayCommand.Async(AddCheckedToExcludeAsync, () => !Busy && !IsLoading);
    ICommand? _openDirectCmd;

    public ICommand SiteCheckCmd => _siteCheckCmd ??= RelayCommand.Async(SiteCheckAsync, () => !Busy && !IsLoading && SiteOutcome != SiteCheckOutcome.Checking);
    ICommand? _siteCheckCmd;

    SiteCheckOutcome _siteOutcome;
    string _siteHost = "";
    /// <summary>Итог «Проверить сайт»: строка с отметкой; «Добавить в исключения» — только когда без оптимизации открывается.</summary>
    public SiteCheckOutcome SiteOutcome
    {
        get => _siteOutcome;
        private set { if (Set(ref _siteOutcome, value)) { Raise(nameof(SiteCheckLine)); Raise(nameof(SiteCheckHealth)); Raise(nameof(HasSiteCheckLine)); Raise(nameof(ShowAddExclude)); Raise(nameof(ShowAddToList)); Raise(nameof(ShowSiteAgain)); CommandManager.InvalidateRequerySuggested(); } }
    }
    public bool HasSiteCheckLine => _siteOutcome is not (SiteCheckOutcome.None or SiteCheckOutcome.Message);
    public string SiteCheckLine => _siteOutcome switch
    {
        SiteCheckOutcome.Checking => L.T("home.sitecheck.checking", new { site = _siteHost }),
        // С оптимизацией или без — сказано прямо (аудит С-16): сайт вне списков открывается сам, под оптимизацией — с ней.
        SiteCheckOutcome.Opens => L.T(_siteExcluded ? "home.sitecheck.excluded_opens" : _siteNotInList ? "home.sitecheck.opens_plain" : _siteCovered ? "home.sitecheck.opens_with" : "home.sitecheck.opens"),
        SiteCheckOutcome.OnlyWithout => L.T("home.sitecheck.only_without"),
        SiteCheckOutcome.FailsWith => L.T("home.sitecheck.fails_with"),
        SiteCheckOutcome.Closed => L.T("home.sitecheck.closed"),
        // Итог прямо («Не открывается. … нет в списке»), тем же ключом — страница роутера (lang-src/shared-keys.txt).
        SiteCheckOutcome.NotInList => L.T("home.sitecheck.not_in_list_closed", new { site = _siteHost }),
        _ => "",
    };
    public Health SiteCheckHealth => _siteOutcome switch
    {
        SiteCheckOutcome.Opens => Health.Ok,
        SiteCheckOutcome.OnlyWithout or SiteCheckOutcome.FailsWith or SiteCheckOutcome.NotInList => Health.Warn,
        SiteCheckOutcome.Closed => Health.Error,
        _ => Health.None,
    };
    public bool ShowAddExclude => _siteOutcome is SiteCheckOutcome.OnlyWithout or SiteCheckOutcome.FailsWith;
    /// <summary>«Добавить в список оптимизации»: режим «Только из списков», сайта нет ни в одном списке и он не открывается.</summary>
    public bool ShowAddToList => _siteOutcome == SiteCheckOutcome.NotInList;

    public ICommand AddToListCmd => _addToListCmd ??= RelayCommand.Async(AddCheckedToListAsync, () => !Busy && !IsLoading);
    ICommand? _addToListCmd;

    /// <summary>«Проверить снова» у итога (аудит С-16): тот же сайт ещё раз — после добавления в список или в исключения и просто повторно.</summary>
    public bool ShowSiteAgain => DirectOpen && _siteHost.Length > 0 && _siteOutcome is not (SiteCheckOutcome.None or SiteCheckOutcome.Checking);
    public ICommand SiteAgainCmd => _siteAgainCmd ??= RelayCommand.Async(() => { DirectSite = _siteHost; return SiteCheckAsync(); },
        () => !Busy && !IsLoading && _siteHost.Length > 0 && SiteOutcome != SiteCheckOutcome.Checking);
    ICommand? _siteAgainCmd;

    /// <summary>Сайт под оптимизацией там, где она работает (для итога «Открывается с оптимизацией»); вне списков — «без неё».</summary>
    bool _siteCovered, _siteNotInList, _siteExcluded;

    /// <summary>Где сайта нет в списке при режиме «Только из списков» — туда «Добавить в список оптимизации».</summary>
    bool _listPc, _listRouter;

    /// <summary>
    /// «Проверить сайт: с оптимизацией и без неё». Через роутер — его же «Сайт не открывается» (site_check: с обходом и без).
    /// На этом ПК — та же проверка, что «Проверить сейчас» (HTTPS с DoH): с оптимизацией — сейчас; без неё — после добавления в исключения
    /// (сайт там оптимизация не трогает), поэтому «не открылось» на ПК честно говорит «с оптимизацией не открывается» и предлагает исключение.
    /// Режим «Только из списков» и сайта нет ни в одном списке там, где работает оптимизация, — не открылся он не из-за неё:
    /// итог «не в списке» и «Добавить в список оптимизации» (решение владельца). Открылся — просто «Открывается».
    /// Где работает оптимизация (ПК, роутер, оба) — как и для исключений.
    /// </summary>
    public async Task SiteCheckAsync()
    {
        if (DataStore.NormalizeDomain(DirectSite) is not { } host)
        {
            DirectMessage = L.T("home.direct.empty");
            SiteOutcome = SiteCheckOutcome.None;
            return;
        }
        _siteHost = host;
        DirectMessage = "";
        SiteOutcome = SiteCheckOutcome.Checking;
        try
        {
            // Под оптимизацией ли сайт там, где она работает: null — она там не работает или не узнать (тогда — как раньше).
            bool? pc = await PcCoversAsync(host);
            bool? router = Router.Found && Router.RouterEnabled ? await Router.CoversHostAsync(host) : null;
            _listPc = pc == false; _listRouter = router == false;
            bool notInList = (_listPc || _listRouter) && pc != true && router != true;
            _siteNotInList = notInList;
            _siteCovered = pc == true || router == true;
            _siteExcluded = false;
            if (Router.Found && Router.RouterEnabled && Router.Connected && Router.CanSiteCheck)
            {
                var r = await Router.CheckSiteQuietAsync(host);
                if (r is null) { SiteOutcome = SiteCheckOutcome.Message; DirectMessage = L.T("router.site.pending"); return; }
                bool with = r.WithBypass?.Ok == true, without = r.WithoutBypass?.Ok == true;
                SiteOutcome = with ? SiteCheckOutcome.Opens : notInList ? SiteCheckOutcome.NotInList
                    : without || r.SuggestExclude ? SiteCheckOutcome.OnlyWithout : SiteCheckOutcome.Closed;
                return;
            }
            bool ok = await ProbeSiteAsync(host);
            // Сайт в «Исключениях» ПК — оптимизация его не трогает: «открывается» здесь значит «без неё».
            _siteExcluded = _active && Hostlist.Covers(host, [.. SavedExclude, .. DraftExclude()]);
            // Без оптимизации (ничего не ведёт) — проверка и есть «без неё»: не открылось — дело не в ней.
            SiteOutcome = ok ? SiteCheckOutcome.Opens : notInList ? SiteCheckOutcome.NotInList : _active ? SiteCheckOutcome.FailsWith : SiteCheckOutcome.Closed;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            SiteOutcome = SiteCheckOutcome.Message;
            DirectMessage = L.T("home.check.failed", new { reason = Texts.Friendly(e) });
        }
    }

    /// <summary>Тот же путь, что «Проверить сейчас»: HTTPS с DoH (в тестах — подмена <see cref="ProbeGroups"/>).</summary>
    async Task<bool> ProbeSiteAsync(string host)
    {
        var group = TargetSet.UserGroup([host]);
        var verdicts = ProbeGroups is { } probe ? await probe([group]) : await Task.Run(() => _shell.CheckGroupsAsync([group]));
        return verdicts.Count > 0 && verdicts.All(v => v.Ok);
    }

    /// <summary>
    /// Оптимизирует ли ПК этот сайт — как собирает командную строку <see cref="Strategies.Build"/>: «для всех сайтов» (продвинутый режим) — да;
    /// иначе — сайт или его родитель в списках YouTube, Discord, общем, своих сайтах или строках продвинутого режима. При совместной работе
    /// на ПК остаются только свои сайты и строки. null — оптимизация на ПК не работает. IP-списки (адреса) сюда не входят.
    /// </summary>
    async Task<bool?> PcCoversAsync(string host)
    {
        if (!_active) return null;
        AdvancedInfo? adv = null;
        if (Helper.Reader is { } c && c.Compat.Supports("advanced_get"))
        {
            try { adv = await c.AdvancedGetAsync(); }
            catch (Exception e) when (e is not OperationCanceledException) { }
        }
        bool joint = Router.JointOn && !Router.JointIsFallback;
        var others = adv is { Enabled: true } ? adv.Others : AdvancedSites.OthersLists;
        if (!joint && others == AdvancedSites.OthersAll) return true;
        var rows = adv is { Enabled: true } ? adv.Rows.Where(r => r.Strategy != AdvancedSites.Direct).SelectMany(r => r.Domains) : [];
        if (Hostlist.Covers(host, rows)) return true;
        if (!joint && others == AdvancedSites.OthersDirect) return false;   // роли каталога выключены: только строки
        var lines = new List<string>(UserTargets);
        if (!joint)
        {
            lines.Add("discord.media");   // роль discord_media: --hostlist-domains=discord.media
            foreach (var f in new[] { "general.txt", RoleLists.Google, RoleLists.Discord }) lines.AddRange(ListFile(f));
        }
        return Hostlist.Covers(host, lines);
    }

    /// <summary>Встроенный список: рабочий (обновляется) или, если его не прочитать, из поставки.</summary>
    HashSet<string> ListFile(string name)
    {
        var set = Hostlist.Read(Path.Combine(_shell.Paths.Lists, name));
        return set.Count > 0 ? set : Hostlist.Read(Path.Combine(_shell.Paths.Seed, "lists", name));
    }

    /// <summary>
    /// «Добавить в список оптимизации» после проверки: на ПК — в «Свои сайты» (то же сохранение, что на «Сайтах»), на роутере — в его список
    /// «Оптимизировать»; туда, где сайта в списке не было. Затем сайт проверяется заново — строка показывает новый итог.
    /// </summary>
    public async Task AddCheckedToListAsync()
    {
        var host = _siteHost;
        if (_siteOutcome != SiteCheckOutcome.NotInList || host.Length == 0) return;
        bool pc = false, router = false;
        if (_listPc)
        {
            if (!await AddPcTargetAsync(host)) return;
            pc = true;
        }
        if (_listRouter)
        {
            if (!await Router.AddHostToGeneralAsync(host))
            {
                DirectMessage = L.T(pc ? "home.sitecheck.list_router_failed_pc" : "home.direct.router_failed", new { site = host });
                SiteOutcome = SiteCheckOutcome.Message;
                return;
            }
            router = true;
        }
        var added = L.T(pc && router ? "home.sitecheck.listed_both" : pc ? "home.sitecheck.listed_pc" : "home.sitecheck.listed_router", new { site = host });
        Message = added;
        DirectSite = host;
        await SiteCheckAsync();
        DirectSite = "";
        DirectMessage = HasDirectMessage ? added + " " + DirectMessage : added;
    }

    /// <summary>Сайт — в «Свои сайты» ПК: дописать в поле вкладки «Сайты» и сохранить тем же путём, что её «Сохранить».</summary>
    async Task<bool> AddPcTargetAsync(string host)
    {
        if (!DraftTargets().Contains(host, StringComparer.OrdinalIgnoreCase))
        {
            var text = TargetsText.TrimEnd();
            TargetsText = text.Length == 0 ? host : text + Environment.NewLine + host;
        }
        return await SaveTargetsAsync();
    }

    /// <summary>
    /// Проверенный сайт — в исключения там, где работает оптимизация. На ПК «без неё» проверяется сразу после этого:
    /// не открылось и так — дело не в оптимизации (сайт остаётся в исключениях, убрать — на «Сайтах»); открылось — так и сказано.
    /// Не проверено (роутер, сбой проверки) — «Проверьте ещё раз»; адрес остаётся в поле, чтобы «Проверить» шла по нему.
    /// </summary>
    public async Task AddCheckedToExcludeAsync()
    {
        var was = _siteOutcome;
        var host = _siteHost;
        if (was is SiteCheckOutcome.OnlyWithout or SiteCheckOutcome.FailsWith && host.Length > 0) DirectSite = host;
        await OpenDirectAsync();
        bool added = DirectSite.Length == 0 && HasDirectMessage;
        if (added) DirectSite = host;
        bool? opens = null;
        if (added && was == SiteCheckOutcome.FailsWith)
        {
            SiteOutcome = SiteCheckOutcome.Checking;
            try { opens = await ProbeSiteAsync(host); }
            catch (Exception e) when (e is not OperationCanceledException) { }   // проверить не вышло — «проверьте ещё раз»
            if (opens == false)
            {
                SiteOutcome = SiteCheckOutcome.Closed;
                DirectMessage = L.T("home.sitecheck.excluded_closed", new { site = host });
                return;
            }
        }
        if (added) DirectMessage += " " + L.T(opens == true ? "home.sitecheck.excluded_opens" : "home.sitecheck.recheck");
        if (HasDirectMessage) SiteOutcome = SiteCheckOutcome.Message;
    }

    /// <summary>Добавить сайт в исключения там, где сейчас работает оптимизация (на ПК и/или на роутере).</summary>
    public async Task OpenDirectAsync()
    {
        if (DataStore.NormalizeDomain(DirectSite) is not { } host)
        {
            DirectMessage = L.T("home.direct.empty");
            return;
        }
        bool pc = false, router = false;
        if (_active)
        {
            if (!await AddPcExcludeAsync(host)) return;
            pc = true;
        }
        if (Router.Found && Router.RouterEnabled)
        {
            if (!await Router.ExcludeHostAsync(host))
            {
                // Роутер не принял (нет входа, старая версия) — ПК уже сделан, об остальном скажет строка состояния роутера.
                DirectMessage = L.T(pc ? "home.direct.router_failed_pc" : "home.direct.router_failed", new { site = host });
                return;
            }
            router = true;
        }
        DirectSite = "";
        DirectMessage = L.T(pc && router ? "home.direct.done_both" : pc ? "home.direct.done_pc" : "home.direct.done_router", new { site = host });
    }

    async Task<bool> AddPcExcludeAsync(string host)
    {
        var lines = DraftExclude();
        if (!lines.Contains(host, StringComparer.OrdinalIgnoreCase)) lines.Add(host);
        return await SaveExcludeCoreAsync(lines);
    }

    // ---------- «Сайты» → «Исключения» (простой список исключений ПК) ----------

    string _excludeText = "";
    /// <summary>Черновик списка до «Сохранить»; сохраняет помощник (<c>user_exclude_set</c>).</summary>
    public string ExcludeText
    {
        get => _excludeText;
        set { if (Set(ref _excludeText, value)) { SetExcludeMessage(""); RaiseSitesDirty(); } }
    }

    IReadOnlyList<string> SavedExclude => Helper.Status?.UserExclude ?? [];

    /// <summary>В поле не то, что сохранено.</summary>
    public bool ExcludeDirty
    {
        get
        {
            var saved = SavedExclude.Select(DataStore.NormalizeDomain).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            return !saved.SetEquals(DraftExclude());
        }
    }

    List<string> DraftExclude() =>
        ExcludeText.Split('\n').Select(DataStore.NormalizeDomain).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    string _excludeMessage = "";
    bool _excludeError;
    /// <summary>«Сохранено…» — пока в поле то же, что сохранено (рядом с «Не сохранено» два статуса не нужны); отказ — виден всегда.</summary>
    public string ExcludeMessage => _excludeError || !ExcludeDirty ? _excludeMessage : "";

    void SetExcludeMessage(string text, bool error = false)
    {
        _excludeMessage = text; _excludeError = error;
        Raise(nameof(ExcludeMessage));
    }

    bool _excludeFromHelper;

    /// <summary>Первый ответ помощника — список в поле (дальше поле — черновик человека).</summary>
    void TakeExcludeFromHelper()
    {
        if (_excludeFromHelper || Helper.Status?.UserExclude is not { } list) return;
        _excludeFromHelper = true;
        if (list.Count > 0) ExcludeText = string.Join(Environment.NewLine, list);
    }

    /// <summary>Сохранить список из поля («Сохранить» на вкладке «Сайты» и уход с неё).</summary>
    async Task<bool> SaveExcludeAsync() => await SaveExcludeCoreAsync(DraftExclude());

    async Task<bool> SaveExcludeCoreAsync(IReadOnlyList<string> lines)
    {
        if (Helper.Reader is { } hc && !hc.Compat.Supports("user_exclude_set"))
        {
            SetExcludeMessage(L.T("sites.helper.outdated_update"), error: true);
            Message = ExcludeMessage;
            return false;
        }
        if (lines.Count > AdvancedSites.MaxExclude)
        {
            SetExcludeMessage(L.T("sites.exclude.err.max", new { max = AdvancedSites.MaxExclude }), error: true);
            Message = ExcludeMessage;
            return false;
        }
        UserTargetsResult? r = null;
        if (!await WithHelper(L.T("sites.exclude.busy.save"), async c =>
            {
                r = await c.SetUserExcludeAsync([.. lines]);
                await FetchAsync(full: false);
            }) || r is null)
            return false;
        _excludeFromHelper = true;
        ExcludeText = string.Join(Environment.NewLine, r.Saved);
        SetExcludeMessage(r.Rejected.Count == 0
            ? L.T("sites.exclude.saved", new { n = r.Saved.Count })
            : L.T("sites.exclude.saved_rejected", new { n = r.Saved.Count, rejected = string.Join(", ", r.Rejected) }));
        RaiseSitesDirty();
        return true;
    }
}
