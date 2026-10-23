using System.Diagnostics;
using System.Windows.Input;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.App;

/// <summary>Вид карточки «Почему не получилось» на Главной.</summary>
public enum GuidedKind { NoInternet, Portal, Clock, Uac, NetChanged, Finding, Driver, DiskFull, Unknown }

/// <summary>Состояние карточки: данные, из которых текст собирается при показе (язык окна меняется на лету).</summary>
public sealed record GuidedState(GuidedKind Kind, TimeSpan Skew = default, string? Url = null, SystemFinding? Finding = null, string? Error = null, Func<Task>? Retry = null, string? Disk = null, long? NeedMb = null);

/// <summary>
/// «Окно само ведёт» (docs/dev/architecture.md, «Общие ошибки»): когда «Включить», «Подобрать» или «Проверить сайт» не удались, вместо общей ошибки
/// на месте действия — карточка «Почему: … · [одна кнопка]». Причины: нет интернета, вход в сеть, сбитое время, отказ в правах, смена сети,
/// находка «Диагностики» (те же <see cref="SystemChecks"/>, без дублирования); неизвестная — «Сохранить отчёт» и «Написать о проблеме».
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Разбор причин включён (в App — да; в тестах — по умолчанию нет: сеть и реестр настоящие не трогаются).</summary>
    public bool GuidedEnabled { get; set; }

    /// <summary>Сеть: вход в сеть, интернет, время. Null — настоящая (контрольные сайты из targets.json).</summary>
    public Func<CancellationToken, Task<NetVerdict>>? NetDiagnose { get; set; }

    /// <summary>Находки окружения для карточки (те же, что в «Диагностике»). Null — <see cref="SystemChecks.Run"/>.</summary>
    public Func<IReadOnlyList<SystemFinding>>? SystemFindings { get; set; }

    /// <summary>Открыть ссылку или ms-settings (в тестах — подмена, браузер не запускается).</summary>
    public Action<string>? LinkOpener { get; set; }

    /// <summary>Стратегия по сетям (файл в папке окна; в тестах — память).</summary>
    public NetworkStrategies Networks { get; set; } = new();
    public INetworkIdentity? NetworkIdentity { get; set; }

    /// <summary>TCP-проба для строки Telegram в «Диагностике». Null — строки нет.</summary>
    public TelegramCheck.Tcp? TelegramTcp { get; set; }

    GuidedState? _guided;
    Func<Task>? _retry;
    string? _networkId;

    public GuidedState? Guided => _guided;
    public bool ShowGuided => _guided is not null;
    public string GuidedTitle => L.T("home.guided.title");

    public string GuidedText => _guided switch
    {
        null => "",
        { Kind: GuidedKind.NoInternet } => L.T("home.guided.no_internet"),
        { Kind: GuidedKind.Portal } => L.T("home.guided.portal"),
        { Kind: GuidedKind.Clock } g => L.T("home.guided.clock", new { diff = SkewText(g.Skew) }),
        { Kind: GuidedKind.Uac } => L.T("home.guided.uac"),
        { Kind: GuidedKind.NetChanged } => L.T("home.guided.net_changed"),
        { Kind: GuidedKind.Finding, Finding: { } f } => L.T("home.guided.finding", new { title = f.Refreshed().Title, text = f.Refreshed().Text }),
        { Kind: GuidedKind.Driver } => L.T("home.guided.driver"),
        { Kind: GuidedKind.DiskFull, NeedMb: { } n } g => L.T("home.guided.disk_need", new { disk = g.Disk, need = n }),
        { Kind: GuidedKind.DiskFull } g => L.T("home.guided.disk", new { disk = g.Disk }),
        { Kind: GuidedKind.Unknown } g => L.T("home.guided.unknown", new { error = g.Error ?? "" }),
        _ => "",
    };

    /// <summary>Надпись единственной кнопки исправления (пусто — кнопки нет).</summary>
    public string GuidedActionText => _guided switch
    {
        { Kind: GuidedKind.NoInternet or GuidedKind.Uac } => L.T("home.guided.retry"),
        { Kind: GuidedKind.Portal } => L.T("home.guided.portal_open"),
        { Kind: GuidedKind.Clock } => L.T("home.guided.clock_sync"),
        { Kind: GuidedKind.NetChanged } => L.T("home.guided.select"),
        { Kind: GuidedKind.Finding, Finding.Go: SystemFinding.GoProxy } => L.T("home.guided.open_proxy"),
        { Kind: GuidedKind.Finding } => L.T("home.guided.open_diag"),
        { Kind: GuidedKind.Driver } => L.T("home.guided.driver_open"),
        { Kind: GuidedKind.DiskFull } => L.T("home.guided.disk_open"),
        _ => "",
    };

    public bool GuidedHasAction => GuidedActionText.Length > 0 && !(_guided is { Kind: GuidedKind.NoInternet or GuidedKind.Uac, Retry: null });
    /// <summary>Причина неизвестна: «Сохранить отчёт» и «Написать о проблеме» вместо кнопки исправления.</summary>
    /// <summary>Драйвер не запустился из-за защиты Windows: рядом с кнопкой «(i)» — что это и чем рискует выключение.</summary>
    public bool GuidedIsDriver => _guided is { Kind: GuidedKind.Driver };
    public bool GuidedIsUnknown => _guided is { Kind: GuidedKind.Unknown };

    public ICommand GuidedActionCmd => _guidedActionCmd ??= RelayCommand.Async(GuidedActionAsync, () => _guided is not null && !Busy);
    ICommand? _guidedActionCmd;
    public ICommand ReportIssueCmd => _reportIssueCmd ??= new RelayCommand(() => OpenAny(IssueUrl()));
    ICommand? _reportIssueCmd;

    /// <summary>Ссылка «Написать о проблеме»: версии и текст ошибки в заготовке обращения; личное вычищено так же, как в отчёте (<see cref="ErrorReport"/>).</summary>
    public string IssueUrl()
    {
        var e = LastError ?? new ErrorInfo(_guided?.Error ?? "", null, DateTime.Now, null);
        var body = ErrorReport.Build(e, VersionLines(), PrivateNames());
        if (body.Length > 3500) body = body[..3500];
        return RepoUrl + "/issues/new?title=" + Uri.EscapeDataString(L.T("home.guided.issue_title")) + "&body=" + Uri.EscapeDataString(body);
    }

    void OpenAny(string target)
    {
        if (LinkOpener is { } open) open(target); else OpenLink(target);
    }

    static string SkewText(TimeSpan skew)
    {
        var total = (long)Math.Round(skew.Duration().TotalMinutes);
        long d = total / 1440, h = total % 1440 / 60, m = total % 60;
        var parts = new List<string>();
        if (d > 0) parts.Add(L.T("home.guided.unit_d", new { n = d }));
        if (h > 0) parts.Add(L.T("home.guided.unit_h", new { n = h }));
        if (m > 0 && d == 0) parts.Add(L.T("home.guided.unit_m", new { n = m }));
        return string.Join(" ", parts.Take(2));
    }

    void ShowGuidedCard(GuidedState g)
    {
        _guided = g;
        RaiseGuided();
    }

    void ClearGuided()
    {
        if (_guided is null) return;
        _guided = null;
        RaiseGuided();
    }

    void RaiseGuided()
    {
        foreach (var p in new[] { nameof(Guided), nameof(ShowGuided), nameof(GuidedTitle), nameof(GuidedText), nameof(GuidedActionText), nameof(GuidedHasAction), nameof(GuidedIsUnknown), nameof(GuidedIsDriver) }) Raise(p);
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Действие, которое сейчас делает человек: если помощник не нужен — отказ в правах покажет карточку с «Повторить» именно на нём.</summary>
    async Task WithRetry(Func<Task> action, Func<Task> body)
    {
        var prev = _retry;
        _retry = action;
        try { await body(); }
        finally { _retry = prev; }
    }

    public async Task GuidedActionAsync()
    {
        if (_guided is not { } g) return;
        switch (g.Kind)
        {
            case GuidedKind.NoInternet or GuidedKind.Uac:
                ClearGuided();
                if (g.Retry is { } r) await r();
                break;
            case GuidedKind.Portal:
                OpenAny(g.Url ?? NetDiagnosis.PortalProbeUrl);
                break;
            case GuidedKind.Clock:
                await SyncTimeAsync();
                break;
            case GuidedKind.NetChanged:
                ClearGuided();
                Page = Pages.Select;
                await StartSelectAsync();
                break;
            case GuidedKind.Driver:
                OpenWithFallback(CoreIsolation, SecurityHome);
                break;
            case GuidedKind.DiskFull:
                OpenWithFallback(StorageSettings, SettingsHome);
                break;
            case GuidedKind.Finding:
                if (g.Finding?.Go == SystemFinding.GoProxy) GoProxyCmd.Execute(null); else Page = Pages.Diag;
                break;
        }
    }

    const string TimeSettings = "ms-settings:dateandtime";
    /// <summary>«Безопасность Windows» → «Безопасность устройства» → «Изоляция ядра» (Windows 10 1803+ и 11); запасной — главная «Безопасности Windows».</summary>
    public const string CoreIsolation = "windowsdefender://coreisolation";
    public const string SecurityHome = "windowsdefender://";
    /// <summary>«Параметры» → «Память» (Windows 10 1703+ и 11); запасной — главная «Параметров».</summary>
    public const string StorageSettings = "ms-settings:storagesense";
    public const string SettingsHome = "ms-settings:";

    /// <summary>Открыть адрес; система не нашла обработчик (старая сборка) — запасной. В тестах открывается подмена, один раз, без запасного.</summary>
    void OpenWithFallback(string target, string fallback)
    {
        if (LinkOpener is { } open) { open(target); return; }
        try { UiGuard.CheckOpen(target); Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { OpenLink(fallback); }
        catch (InvalidOperationException) { OpenLink(fallback); }
    }


    /// <summary>«Синхронизировать время»: помощник делает w32tm /resync; не вышло (или помощник старый) — «Параметры → Время».</summary>
    async Task SyncTimeAsync()
    {
        HelperClient? c = null;
        await WithRetry(SyncTimeAsync, async () => c = await NeedHelperAsync());
        if (c is null) return;
        TimeSyncResult? res = null;
        if (c.Compat.Supports("time_sync"))
        {
            try { res = await c.TimeSyncAsync(); }
            catch (Exception e) when (e is HelperException or HelperUnavailableException) { }
        }
        if (res is { Ok: true })
        {
            ClearGuided();
            Message = L.T("home.guided.clock_done");
            return;
        }
        Message = L.T("home.guided.clock_settings");
        OpenAny(TimeSettings);
    }

    // ---------- разбор причин ----------

    IReadOnlyList<string> ControlUrls() =>
        [.. _shell.DefaultTargets.Groups.Where(g => g.Control).SelectMany(g => g.Targets).Select(t => t.Url).Take(4)];

    async Task<NetVerdict> NetVerdictAsync()
    {
        try
        {
            if (NetDiagnose is { } f) return await f(CancellationToken.None);
            return await NetDiagnosis.RunAsync(ControlUrls(), new HttpNetFetcher());
        }
        catch (Exception e) when (e is not OperationCanceledException) { return new(NetCause.Ok); }
    }

    GuidedState? FromVerdict(NetVerdict v) => v.Cause switch
    {
        NetCause.NoInternet => new(GuidedKind.NoInternet, Retry: _retry),
        NetCause.CaptivePortal => new(GuidedKind.Portal, Url: v.PortalUrl),
        NetCause.ClockSkew => new(GuidedKind.Clock, Skew: v.Skew),
        _ => null,
    };

    /// <summary>Только сеть (вход, интернет, время): для «Проверить сайт» и «Проверить сейчас». Показал карточку — true.</summary>
    async Task<bool> DiagnoseNetworkAsync()
    {
        if (!GuidedEnabled) return false;
        if (FromVerdict(await NetVerdictAsync()) is not { } g) return false;
        ShowGuidedCard(g);
        return true;
    }

    /// <summary>
    /// Действие сорвалось с ошибкой: сеть, затем находки «Диагностики» (первая из мешающих), иначе — неизвестная причина с отчётом.
    /// <paramref name="error"/> — текст ошибки для обращения.
    /// </summary>
    async Task DiagnoseFailureAsync(string error)
    {
        if (!GuidedEnabled || _guided is not null) return;
        // Причина видна прямо в тексте ошибки (код драйвера, «не хватает места») — сеть и находки не спрашиваем.
        if (FromErrorText(error) is { } known) { ShowGuidedCard(known); CauseFound(); return; }
        if (await DiagnoseNetworkAsync()) { CauseFound(); return; }
        var findings = await Task.Run(() => SystemFindings is { } f ? f() : SystemChecks.Run(_shell.Paths));
        if (findings.FirstOrDefault(x => !x.Ok) is { } bad) { ShowGuidedCard(new(GuidedKind.Finding, Finding: bad)); CauseFound(); }
        else ShowGuidedCard(new(GuidedKind.Unknown, Error: error));
    }

    /// <summary>Причина по тексту ошибки: драйвер перехвата заблокирован защитой Windows; не хватает места на диске. Иначе null.</summary>
    internal static GuidedState? FromErrorText(string? error)
    {
        if (WinwsFailure.IsDriverBlocked(error)) return new(GuidedKind.Driver, Error: error);
        if (DiskSpace.TryParse(error, out var disk, out var need)) return new(GuidedKind.DiskFull, Error: error, Disk: disk, NeedMb: need);
        return null;
    }

    /// <summary>Причина найдена — общая «Ошибка» в карточке статуса и тост не нужны: причина и кнопка уже на месте.</summary>
    void CauseFound()
    {
        _error = null;
        HideToast();
        Message = "";
        UpdateStatus();
    }

    /// <summary>Перед подбором: без интернета (или за входом в сеть) подбор не начинается. true — можно начинать.</summary>
    async Task<bool> NetworkAllowsSelectAsync()
    {
        ClearGuided();
        if (!GuidedEnabled) return true;
        if (FromVerdict(await NetVerdictAsync()) is not { } g) return true;
        ShowGuidedCard(g with { Retry = StartSelectAsync });
        return false;
    }

    // ---------- сеть сменилась ----------

    /// <summary>Сеть сменилась (событие системы, App; в тестах — вызовом). Известная сеть — её стратегия; новая и сайты не открылись — «Подобрать для этой сети?».</summary>
    public async Task NetworkChangedAsync()
    {
        if (!GuidedEnabled || NetworkIdentity is not { } identity) return;
        var id = await Task.Run(identity.CurrentId);
        if (id is null || id == _networkId) return;
        bool first = _networkId is null;
        _networkId = id;
        if (first || Busy || Selecting || IsLoading) return;
        // Обход ведёт роутер (или совместная работа) — стратегию ПК не трогаем.
        if (Router.Found && Router.RouterEnabled) return;
        if (Networks.StrategyFor(id) is { } known && (Helper.Status?.Strategies.Any(s => s.Id == known) == true || _shell.Catalog.Find(known) is not null))
        {
            if (!IsRunning) return;
            ClearGuided();
            if (CurrentStrategyId != known && await ApplyAsync(known)) Message = L.T("home.guided.net_known", new { name = StrategyTitle(known) });
            return;
        }
        if (!IsRunning) return;
        await CheckNowCoreAsync();
        if (FailingCheckGroups() is not null && _guided is null) ShowGuidedCard(new(GuidedKind.NetChanged));
    }

    /// <summary>Итог проверки: всё открылось на включённой стратегии — запомнить её для этой сети.</summary>
    async Task RememberNetworkStrategyAsync()
    {
        if (!GuidedEnabled || NetworkIdentity is not { } identity) return;
        if (!IsRunning || Router.Found && Router.RouterEnabled || CurrentStrategyId is not { } id || FailingCheckGroups() is not null || CheckGroups.Count == 0) return;
        var net = _networkId ?? await Task.Run(identity.CurrentId);
        if (net is null) return;
        _networkId ??= net;
        Networks.Remember(net, id);
        if (_guided is { Kind: GuidedKind.NetChanged }) ClearGuided();
    }

    // ---------- Telegram (строка «Диагностики») ----------

    SystemFinding TelegramFinding(TelegramState s) => s switch
    {
        TelegramState.Blocked => SystemFinding.Of(false, () => (L.T("diag.find.telegram.title"), L.T("diag.find.telegram.blocked"))),
        TelegramState.Open => SystemFinding.Of(true, () => (L.T("diag.find.telegram.title"), L.T("diag.find.telegram.open"))),
        _ => SystemFinding.Of(true, () => (L.T("diag.find.telegram.title"), L.T("diag.find.telegram.unknown"))),
    };
}
