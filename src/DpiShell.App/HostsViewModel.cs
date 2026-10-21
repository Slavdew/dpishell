using System.Collections.ObjectModel;
using System.Windows.Input;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.App;

/// <summary>Строка таблицы «Починить адреса»: Домен | Адрес | Владелец | Проверка | Сейчас | Итог.</summary>
public sealed record HostsRow(string Host, string Ip, string Owner, string Live, bool LiveOk, string Dns, string Verdict, string Reason, bool Install)
{
    public static HostsRow From(HostsCheckRow r) => new(
        r.Host, r.Ip,
        r.OwnerName ?? (r.Owner == OwnerState.Unknown ? L.T("hosts.row.owner_unknown") : "—"),
        r.Live is null ? L.T("hosts.row.live.none") : r.Live.Ok ? L.T("hosts.row.live.ok") : r.Live.Outcome == EndpointOutcome.BadCertificate ? L.T("hosts.row.live.foreign") : L.T("hosts.row.live.down"),
        r.Live?.Ok == true,
        r.Dns switch { DnsState.Works => L.T("hosts.row.dns.works"), DnsState.Fails => L.T("hosts.row.dns.fails"), _ => "?" },
        r.Verdict switch
        {
            HostsVerdict.Install => L.T("hosts.row.verdict.install"),
            HostsVerdict.NotNeeded => L.T("hosts.row.verdict.not_needed"),
            _ => L.T("hosts.row.verdict.bad"),
        },
        Why(r),
        r.Install);

    public string DnsLine => L.T("hosts.col.dns", new { value = Dns });

    static string Why(HostsCheckRow r) => r.Verdict switch
    {
        HostsVerdict.Install => L.T("hosts.row.why.install"),
        HostsVerdict.NotNeeded => L.T("hosts.row.why.not_needed"),
        HostsVerdict.Unreachable => string.IsNullOrWhiteSpace(r.Live?.Detail)
            ? L.T("hosts.row.why.unreachable")
            : L.T("hosts.row.why.unreachable_detail", new { detail = L.FromRu(r.Live!.Detail!.Trim().TrimEnd('.')) }),
        HostsVerdict.BadCertificate => L.T("hosts.row.why.bad_cert"),
        HostsVerdict.OwnerMismatch => L.T("hosts.row.why.owner_mismatch", new { owner = r.OwnerName }),
        _ => r.DnsDetail.Length > 0 ? L.FromRu(r.DnsDetail) : L.T("hosts.row.why.not_allowed"),
    };
}

/// <summary>
/// «Починить адреса» (architecture.md «Починить адреса»): выключено по умолчанию; «Проверить адреса» → таблица → предпросмотр →
/// «Записать N адресов» с подтверждением → «Вернуть как было». Перепроверка раз в сутки и при старте, если функция включена.
/// Окно без прав (helper-protocol §1): hosts читает и пишет помощник — hosts_check/preview/apply/revert/recheck;
/// состояние файла — из details. Адресов окно не передаёт: помощник пишет только то, что сам проверил.
/// </summary>
public sealed partial class HostsViewModel : Observable
{
    readonly HelperLink _helper;
    HostsCheckReport? _report;
    HostsPreview? _preview;
    CancellationTokenSource? _cts;
    /// <summary>Переключатель включён в окне, но ещё ничего не записано: у помощника функция включится с первой записью.</summary>
    bool _draftOn;

    /// <summary>Помощник для действия (с установкой по требованию). Null — не вышло, причина уже сказана.</summary>
    public Func<Task<HelperClient?>> Need { get; set; }
    public Action<string> Say { get; set; } = _ => { };
    public Func<string, bool> Confirm { get; set; } = _ => false;
    public event Action<string>? Notify;

    public HostsViewModel(HelperLink helper)
    {
        _helper = helper;
        Need = () => Task.FromResult(helper.Client);
        CheckCmd = RelayCommand.Async(CheckAsync, () => Enabled && !Busy);
        StopCmd = new RelayCommand(() => _cts?.Cancel(), () => Checking || Trying || Auditing);
        ApplyCmd = RelayCommand.Async(ApplyAsync, () => Enabled && !Busy && _report is { NothingToFix: false });
        RevertCmd = RelayCommand.Async(RevertAsync, () => !Busy && BlockPresent);
    }

    public ObservableCollection<HostsRow> Rows { get; } = [];
    public ObservableCollection<string> PreviewLines { get; } = [];

    bool _busy;
    public bool Busy { get => _busy; private set { if (Set(ref _busy, value)) CommandManager.InvalidateRequerySuggested(); } }

    bool _checking;
    public bool Checking { get => _checking; private set { if (Set(ref _checking, value)) CommandManager.InvalidateRequerySuggested(); } }

    int _done, _total = 1;
    public int Done { get => _done; private set => Set(ref _done, value); }
    public int Total { get => _total; private set => Set(ref _total, value); }
    string _progressText = "";
    public string ProgressText { get => _progressText; private set => Set(ref _progressText, value); }

    // ---------- включено / выключено ----------

    /// <summary>Функция включена у помощника (записывали и не отменяли).</summary>
    bool HelperOn => _helper.Reader is not null && _helper.Status?.HostsFix == true;

    /// <summary>
    /// Переключатель экрана. Включение — черновик окна (прав не нужно, у помощника включится с первой записью);
    /// выключение убирает наш блок (с вопросом) через помощника.
    /// </summary>
    public bool Enabled
    {
        get => _draftOn || HelperOn;
        set
        {
            if (value == Enabled) return;
            if (!value && BlockPresent && !Confirm(L.T("hosts.confirm.remove_block")))
            {
                Raise();
                return;
            }
            _draftOn = value;
            RaiseEnabled();
            if (!value && (BlockPresent || HelperOn)) _ = RevertAsync(ask: false);
        }
    }

    public bool Disabled => !Enabled;
    public string OffHint => L.T("hosts.off_hint");

    void RaiseEnabled()
    {
        Raise(nameof(Enabled));
        Raise(nameof(Disabled));
        RaiseStatus();
    }

    // ---------- состояние файла ----------

    HostsStatus? _status;

    public bool BlockPresent => _status?.Block.Present == true;

    /// <summary>«Записано 3 адреса 17.10.2026 в 14:30. Проверка раз в день; устаревшие убираются сами.»</summary>
    public string StatusText
    {
        get
        {
            if (_status is null) return _helper.Reader is null ? L.T("hosts.status.no_helper") : "";
            var n = _status.Block.Entries.Count;
            if (n == 0) return _status.Block.Present ? L.T("hosts.status.block_empty") : L.T("hosts.status.nothing");
            return _status.AppliedAt is { } a
                ? L.T("hosts.status.written_at", new { n, date = L.Date(a), time = L.Time(a) })
                : L.T("hosts.status.written", new { n });
        }
    }

    public IReadOnlyList<string> AppliedLines => _status?.Block.Entries.Select(e => $"{e.Host} → {e.Ip}").ToList() ?? [];

    /// <summary>Предупреждения: снято Защитником, «только чтение», повреждённый блок, чужие записи, блок при выключенной функции.</summary>
    public IReadOnlyList<string> Warnings
    {
        get
        {
            var list = new List<string>();
            if (_status is not { } s) return list;
            if (s.BlockLost)
                list.Add(L.T("hosts.warn.lost"));
            if (s.Block.Damaged)
                list.Add(L.T("hosts.warn.damaged"));
            if (s.ReadOnly)
                list.Add(L.T("hosts.warn.read_only"));
            // Функция выключена — нашей записи нет и чужая никому не мешает: плашку не показываем.
            if (Enabled)
                foreach (var c in s.Conflicts.GroupBy(c => c.Host).Select(g => g.First()))
                    list.Add(L.T("hosts.warn.foreign", new { host = c.Host }));
            if (!Enabled && s.Block.Present)
                list.Add(L.T("hosts.warn.off_with_block"));
            list.AddRange(StaleWarnings());
            return list;
        }
    }

    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>Чужие записи в hosts на адреса, которые чинит «Починить адреса» (для панели «Этот ПК» на главной); null — состояние hosts ещё не читали.</summary>
    public IReadOnlyList<string>? ForeignHosts => _status is not { } s ? null : Enabled ? s.Conflicts.Select(c => c.Host).Distinct().ToList() : [];
    /// <summary>Заголовок свёрнутого списка замечаний: первый экран вкладки начинается с действия, а не с тревог.</summary>
    public string WarningsHeader => Warnings.Count is var n && n == 0 ? "" : L.Plural("hosts.warnings_header", n);

    /// <summary>Перечитать состояние hosts у помощника (details; файл может держать антивирус — помощник читает в фоне).</summary>
    public async Task RefreshAsync()
    {
        if (_helper.Reader is { } c)
        {
            try
            {
                _helper.Status = await c.StatusAsync();
                _helper.Details = await c.DetailsAsync();
            }
            catch (HelperUnavailableException e) { _helper.MarkLost(e); }
            catch (HelperException e) { Say(e.Message); }
        }
        RefreshNow();
    }

    /// <summary>Экран по последнему ответу помощника, без обращения к нему (и тест окна строит экран по готовому состоянию).</summary>
    public void RefreshNow()
    {
        _status = _helper.Reader is not null ? _helper.Details?.Hosts : null;
        if (HelperOn) _draftOn = false;   // включено у помощника — черновик больше не нужен
        RefreshTools();
        RaiseEnabled();
    }

    void RaiseStatus()
    {
        foreach (var p in new[] { nameof(BlockPresent), nameof(StatusText), nameof(AppliedLines), nameof(Warnings), nameof(HasWarnings), nameof(WarningsHeader) }) Raise(p);
        CommandManager.InvalidateRequerySuggested();
    }

    // ---------- проверка → предпросмотр → запись ----------

    readonly LiveText _summary = new();
    /// <summary>Итог проверки: «Подходят адресов: N» или «Нечего чинить: всё открывается и так».</summary>
    public string Summary { get => _summary.Value; private set { _summary.Set(value); Raise(); Raise(nameof(HasSummary)); } }
    void SummaryIs(Func<string> build) { _summary.Set(build); Raise(nameof(Summary)); Raise(nameof(HasSummary)); }

    /// <summary>Смена языка: таблица проверки и свои/закомментированные строки собираются заново из данных (отчёт и состояние файла).</summary>
    protected override void OnLanguageChanged()
    {
        _summary.Retranslate(); _trySummary.Retranslate(); _auditSummary.Retranslate();
        if (_report is { } report && Rows.Count > 0)
        {
            Rows.Clear();
            foreach (var r in report.Rows.OrderByDescending(r => r.Install).ThenBy(r => r.Host)) Rows.Add(HostsRow.From(r));
        }
        RefreshTools();
    }
    public bool HasSummary => Summary.Length > 0;

    public bool CanWrite => _report is { NothingToFix: false };
    /// <summary>Число на кнопке — столько же, сколько строк в «Что изменится» (добавленные и убранные), а не адресов в отчёте.</summary>
    public string ApplyText => _report is not { NothingToFix: false } r ? L.T("hosts.apply")
        : _preview is { } p ? (p.Changed ? L.T("hosts.apply_n", new { n = p.Added.Count + p.Removed.Count }) : L.T("hosts.apply"))
        : L.T("hosts.apply_n", new { n = r.ToInstall.Count });

    public ICommand CheckCmd { get; }
    public ICommand StopCmd { get; }
    public ICommand ApplyCmd { get; }
    public ICommand RevertCmd { get; }

    public async Task CheckAsync()
    {
        if (Busy) return;
        if (await Need() is not { } c) return;
        Busy = true; Checking = true;
        using var cts = new CancellationTokenSource();
        _cts = cts;
        Rows.Clear(); PreviewLines.Clear(); SetReport(null); Summary = "";
        Done = 0; Total = 1; ProgressText = L.T("hosts.progress.check");
        var progress = new Progress<HostsCheckProgress>(p => { Done = p.Done; Total = Math.Max(1, p.Total); ProgressText = L.T("hosts.progress.done", new { done = p.Done, total = p.Total }); });
        try
        {
            var report = await c.HostsCheckAsync(progress, cts.Token);
            // Предпросмотр — по той же проверке у помощника: что именно он запишет.
            HostsPreview? preview = null;
            string? previewError = null;
            if (!report.NothingToFix)
            {
                try { preview = await c.HostsPreviewAsync(); }
                catch (HelperException e) { previewError = e.Message; }
            }
            ShowReport(report, preview);
            if (previewError is not null) PreviewLines.Add(previewError);
        }
        catch (OperationCanceledException) { Summary = L.T("hosts.stopped"); }
        catch (HelperUnavailableException e) { _helper.MarkLost(e); Summary = e.Message; }
        catch (Exception e) { Summary = Texts.Friendly(e); }
        finally { _cts = null; Busy = false; Checking = false; ProgressText = ""; }
        await RefreshAsync();
    }

    /// <summary>Таблица и предпросмотр по готовому отчёту (и для тестов окна).</summary>
    public void ShowReport(HostsCheckReport report, HostsPreview? preview)
    {
        Rows.Clear();
        foreach (var r in report.Rows.OrderByDescending(r => r.Install).ThenBy(r => r.Host)) Rows.Add(HostsRow.From(r));
        SetReport(report);
        _preview = preview;
        Raise(nameof(ApplyText));
        PreviewLines.Clear();
        if (report.NothingToFix)
        {
            SummaryIs(() => L.T("hosts.summary.nothing"));
            return;
        }
        SummaryIs(() => L.T("hosts.summary.to_install", new { n = report.ToInstall.Count }));
        if (preview is not { } p) return;
        foreach (var e in p.Added) PreviewLines.Add($"+ {e.Ip} {e.Host}");
        foreach (var e in p.Removed) PreviewLines.Add($"− {e.Ip} {e.Host}");
        if (!p.Changed) PreviewLines.Add(L.T("hosts.preview.already"));
    }

    void SetReport(HostsCheckReport? r)
    {
        _report = r;
        if (r is null) _preview = null;
        Raise(nameof(CanWrite)); Raise(nameof(ApplyText));
        CommandManager.InvalidateRequerySuggested();
    }

    public async Task ApplyAsync()
    {
        if (_report is not { NothingToFix: false } report) return;
        var lines = _preview?.BlockLines.Count ?? report.ToInstall.Count + 3;
        if (!Confirm(L.T("hosts.confirm.write", new { n = lines }))) return;
        if (await Need() is not { } c) return;
        Busy = true;
        try
        {
            await c.HostsApplyAsync();
            _draftOn = false;   // помощник включил функцию сам
            Say(L.T("hosts.say.written", new { n = report.ToInstall.Count }));
            PreviewLines.Clear();
            SetReport(null);
            Summary = "";
        }
        catch (HelperUnavailableException e) { _helper.MarkLost(e); Say(e.Message); }
        catch (HelperException e) { Say(e.Message); Summary = e.Message; }
        finally { Busy = false; }
        await RefreshAsync();
    }

    public Task RevertAsync() => RevertAsync(ask: true);

    async Task RevertAsync(bool ask)
    {
        if (ask && !Confirm(L.T("hosts.confirm.remove_block"))) return;
        if (await Need() is not { } c) { RefreshNow(); return; }
        Busy = true;
        try
        {
            var r = await c.HostsRevertAsync();
            _draftOn = false;
            Say(r.Changed ? L.T("hosts.say.reverted") : L.T("hosts.say.no_entries"));
        }
        catch (HelperUnavailableException e) { _helper.MarkLost(e); Say(e.Message); }
        catch (HelperException e) { Say(e.Message); }
        finally { Busy = false; }
        await RefreshAsync();
    }

    // ---------- перепроверка ----------

    /// <summary>
    /// Раз в сутки (и при старте): функция включена у помощника, а с прошлой проверки прошло больше суток — перепроверить записанное.
    /// В фоне, без вопросов: помощника нет — перепроверять нечего (записывает только он).
    /// </summary>
    public async Task RecheckIfDueAsync()
    {
        if (Busy) return;
        // Перепроверка идёт, если стоит блок проверенных адресов или есть свои строки (о них только сообщаем, сами не снимаем).
        if (!(HelperOn || UserRows.Count > 0) || _helper.Details?.HostsCheckedAt is { } at && DateTime.Now - at < TimeSpan.FromDays(1) || _helper.Client is not { } c)
        {
            if (_status is null) await RefreshAsync();   // первый раз — хотя бы состояние (для трея и предупреждений)
            return;
        }
        Busy = true;
        try
        {
            var r = await c.HostsRecheckAsync();
            foreach (var host in r.Removed.Select(x => x.Host).Distinct())
                Notify?.Invoke(L.T("hosts.notify.stale_removed", new { host }));
            foreach (var e in r.NewStaleUser ?? [])
                Notify?.Invoke(L.T("hosts.notify.user_stale", new { host = e.Host }));
            if (r.BlockLost) Notify?.Invoke(L.T("hosts.notify.lost"));
        }
        catch (HelperUnavailableException e) { _helper.MarkLost(e); }
        catch (HelperException e) { Say(e.Message); }
        finally { Busy = false; }
        await RefreshAsync();
    }
}
