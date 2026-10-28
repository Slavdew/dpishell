using System.Collections.ObjectModel;
using System.Windows.Input;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.App;

/// <summary>Строка примерки или аудита: вердикт для «адрес имя» (ТЗ 12). Галочка — только у тех, с кем можно что-то сделать.</summary>
public sealed class TryRowVm : Observable
{
    public HostsTryRow Source { get; }
    public string Host => Source.Host;
    public string Ip => Source.Ip;
    // Тексты собираются из отчёта при показе — после смены языка читаются на новом.
    public string Verdict => Kinds().Verdict;
    public string Reason => Kinds().Reason;
    public string Detail => Source.Line > 0 ? L.T("hosts.try.detail", new { line = Source.Line, text = Simple(Source) }) : Simple(Source);
    /// <summary>Технические признаки (сертификат, владелец сети, совпадение с DNS) — под «Подробнее» у строки.</summary>
    public string Technical => Describe(Source);
    public bool HasTechnical => Technical.Length > 0;
    /// <summary>ok — чинит; bad — мешает; warn — не поможет; muted — нейтрально.</summary>
    public string Tone => Kinds().Tone;
    public bool CanSelect { get; }
    public string? LineId => Source.LineId;

    bool _selected;
    public bool Selected { get => _selected; set { if (CanSelect && Set(ref _selected, value)) Changed?.Invoke(); } }
    public event Action? Changed;

    (string Verdict, string Reason, string Tone) Kinds() => Source.Verdict switch
        {
            HostsTryVerdict.Helps => (L.T("hosts.try.verdict.helps"), L.T("hosts.try.reason.helps"), "ok"),
            HostsTryVerdict.NotNeeded => (L.T("hosts.try.verdict.not_needed"), L.T("hosts.try.reason.not_needed"), "muted"),
            HostsTryVerdict.Breaks => (L.T("hosts.try.verdict.breaks"), L.T("hosts.try.reason.breaks"), "bad"),
            HostsTryVerdict.Useless => (L.T("hosts.try.verdict.useless"), L.T("hosts.try.reason.useless"), "warn"),
            HostsTryVerdict.Dangerous => (L.T("hosts.try.verdict.dangerous"), L.T("hosts.try.reason.dangerous"), "bad"),
            _ => (L.T("hosts.try.verdict.blocks"), L.T("hosts.try.reason.blocks"), "muted"),
        };

    public TryRowVm(HostsTryRow r, bool selectable, bool selected)
    {
        Source = r;
        CanSelect = selectable;
        _selected = selectable && selected;
    }

    /// <summary>Одной фразой, что выяснила проверка: «Подтверждено: адрес принадлежит GitHub».</summary>
    static string Simple(HostsTryRow r) =>
        r.Live is null ? L.T("hosts.try.simple.not_checked")
        : r.Live.Ok ? (string.IsNullOrWhiteSpace(r.OwnerName) ? L.T("hosts.try.simple.confirmed") : L.T("hosts.try.simple.confirmed_owner", new { owner = r.OwnerName.Trim() }))
        : r.Live.Outcome == EndpointOutcome.BadCertificate ? L.T("hosts.try.simple.foreign")
        : L.T("hosts.try.simple.no_response");

    /// <summary>Технические признаки: сертификат, владелец сети, как соотносится с DNS.</summary>
    static string Describe(HostsTryRow r)
    {
        var parts = new List<string>();
        parts.Add(r.Live is null ? L.T("hosts.try.tech.stub")
            : r.Live.Ok ? L.T("hosts.try.tech.cert_ok")
            : r.Live.Outcome == EndpointOutcome.BadCertificate ? L.T("hosts.try.tech.cert_bad")
            : L.T("hosts.try.simple.no_response"));
        if (!string.IsNullOrWhiteSpace(r.OwnerName)) parts.Add(L.T("hosts.try.tech.owner", new { owner = r.OwnerName }));
        parts.Add(r.Match switch
        {
            DnsMatch.Same => L.T("hosts.try.tech.dns_same"),
            DnsMatch.SameNet => L.T("hosts.try.tech.dns_same_net"),
            DnsMatch.Differs => L.T("hosts.try.tech.dns_differs"),
            _ => L.T("hosts.try.tech.dns_none"),
        });
        parts.Add(r.Dns switch { DnsState.Works => L.T("hosts.try.tech.opens_dns"), DnsState.Fails => L.T("hosts.try.tech.fails_dns"), _ => L.T("hosts.try.tech.dns_unchecked") });
        return string.Join(" · ", parts);
    }
}

public sealed record UserRowVm(string Host, string Ip, string Date, bool Stale)
{
    public string Text => $"{Host} → {Ip}";
    /// <summary>Date — ISO как пришла от помощника; дата для показа собирается по языку окна.</summary>
    public string Note => Stale ? L.T("hosts.user.note_stale", new { date = HostsViewModel.FormatDate(Date) }) : HostsViewModel.FormatDate(Date);
    public HostsEntry Entry => new(Host, Ip);
}

public sealed record OffRowVm(int Line, string Date, string Original)
{
    public string Text => Original.Trim();
    public string Note => L.T("hosts.off.note", new { line = Line, date = HostsViewModel.FormatDate(Date) });
}

/// <summary>
/// «Починить адреса», часть 2 (ТЗ 12): примерить строки из гайда, свои строки, проверка чужих строк hosts, «закомментировать мешающие».
/// Всё через помощника: окно ничего не пишет в hosts и не называет адресов для записи, кроме тех, что человек выбрал, —
/// помощник проверяет их заново.
/// </summary>
public sealed partial class HostsViewModel
{
    static string RestoreQuestion => L.T("hosts.confirm.restore");

    static string RemoveUserQuestion => L.T("hosts.confirm.remove_user");

    // ---------- разделы вкладки ----------

    bool _listOpen = true, _tryOpen, _userOpen, _auditOpen;
    public bool ListOpen { get => _listOpen; set => Set(ref _listOpen, value); }
    public bool TryOpen { get => _tryOpen; set => Set(ref _tryOpen, value); }
    public bool UserOpen { get => _userOpen; set => Set(ref _userOpen, value); }
    public bool AuditOpen { get => _auditOpen; set => Set(ref _auditOpen, value); }

    bool Can(string cmd) => _helper.Client?.Compat.Supports(cmd) ?? true;

    /// <summary>Помощник старее программы: новых команд у него нет. Подсказка вместо мёртвых кнопок.</summary>
    public bool ToolsUnavailable => _helper.Reader is { } c && !c.Compat.Supports("hosts_try");
    public string ToolsUnavailableText => L.T("hosts.tools_unavailable");

    // ---------- примерить строки ----------

    string _pasteText = "";
    public string PasteText { get => _pasteText; set { if (Set(ref _pasteText, value)) CommandManager.InvalidateRequerySuggested(); } }

    public ObservableCollection<TryRowVm> TryRows { get; } = [];

    readonly LiveText _trySummary = new();
    public string TrySummary { get => _trySummary.Value; private set { _trySummary.Set(value); Raise(); Raise(nameof(HasTrySummary)); } }
    void TrySummaryIs(Func<string> build) { _trySummary.Set(build); Raise(nameof(TrySummary)); Raise(nameof(HasTrySummary)); }
    public bool HasTrySummary => TrySummary.Length > 0;

    bool _trying;
    public bool Trying { get => _trying; private set { if (Set(ref _trying, value)) CommandManager.InvalidateRequerySuggested(); } }

    int SelectedTry => TryRows.Count(r => r.Selected);
    public string WriteSelectedText => SelectedTry > 0 ? L.T("hosts.write_selected_n", new { n = SelectedTry }) : L.T("hosts.write_selected");

    ICommand? _tryCmd, _writeSelectedCmd, _clearTryCmd;
    public ICommand TryCmd => _tryCmd ??= RelayCommand.Async(TryAsync, () => !Busy && PasteText.Trim().Length > 0 && Can("hosts_try"));
    public ICommand WriteSelectedCmd => _writeSelectedCmd ??= RelayCommand.Async(WriteSelectedAsync, () => !Busy && SelectedTry > 0 && Can("hosts_user_set"));
    public ICommand ClearTryCmd => _clearTryCmd ??= new RelayCommand(() => { PasteText = ""; TryRows.Clear(); _found.Clear(); TrySummary = ""; Raise(nameof(WriteSelectedText)); Raise(nameof(CanFind)); }, () => !Busy);

    /// <summary>Вставка → пары «адрес имя» → проверка у помощника → таблица с вердиктами. Чинящие строки отмечены, остальные нет.</summary>
    public async Task TryAsync()
    {
        if (Busy) return;
        var paste = HostsPaste.Parse(PasteText);
        TryRows.Clear();
        Raise(nameof(WriteSelectedText));
        if (paste.Entries.Count == 0)
        {
            TrySummary = L.T("hosts.summary.try_empty");
            return;
        }
        if (await Need() is not { } c) return;
        Busy = true; Trying = true;
        Done = 0; Total = 1; ProgressText = L.T("hosts.progress.try");
        using var cts = new CancellationTokenSource();
        _cts = cts;
        _found.Clear();
        try
        {
            ShowTry(await TryBatchedAsync(c, paste.Entries, cts.Token), paste);
            // Заглушки (0.0.0.0, 127.*) — сразу подобрать рабочие адреса вместо них (владелец 28.10, п. 97).
            if (StubHosts is { Count: > 0 } stubs) await FindCoreAsync(c, stubs, cts.Token, paste);
        }
        catch (OperationCanceledException) { TrySummary = L.T("hosts.stopped"); }
        catch (HelperUnavailableException e) { _helper.MarkLost(e); TrySummary = e.Message; }
        catch (HelperException e) { TrySummary = e.Message; }
        catch (Exception e) { TrySummary = Texts.Friendly(e); }
        finally { _cts = null; Busy = false; Trying = false; ProgressText = ""; }
    }

    /// <summary>
    /// Примерка у помощника партиями по <see cref="HostsPaste.MaxBatch"/> (столько принимает одна команда): длинный список идёт сам, раунд за раундом,
    /// прогресс — общий «проверено N из M», отчёт — один на все партии.
    /// </summary>
    async Task<HostsTryReport> TryBatchedAsync(HelperClient c, IReadOnlyList<HostsEntry> entries, CancellationToken ct)
    {
        var rows = new List<HostsTryRow>();
        int total = entries.Count;
        Done = 0; Total = Math.Max(1, total);
        for (int start = 0; start < total; start += HostsPaste.MaxBatch)
        {
            int offset = start;
            var progress = new Progress<HostsCheckProgress>(p =>
            {
                Done = offset + p.Done;
                ProgressText = L.T("hosts.progress.done", new { done = offset + p.Done, total });
            });
            rows.AddRange((await c.HostsTryAsync([.. entries.Skip(start).Take(HostsPaste.MaxBatch)], progress, ct)).Rows);
        }
        return new HostsTryReport(rows, 0, false, total, DateTime.Now);
    }

    /// <summary>Таблица примерки по готовому отчёту (и для тестов окна).</summary>
    public void ShowTry(HostsTryReport report, HostsPasteResult? paste = null)
    {
        TryRows.Clear();
        foreach (var r in report.Rows.OrderBy(Rank).ThenBy(r => r.Host, StringComparer.Ordinal))
        {
            // Подобранный адрес записать можно и при «Не нужна»: он рабочий (сертификат сайта), им заменяют заглушку. Помощник проверит его ещё раз.
            bool writable = r.Writable || _found.Contains(r.Entry) && r.Live?.Ok == true;
            var row = new TryRowVm(r, selectable: writable, selected: writable);
            row.Changed += () => { Raise(nameof(WriteSelectedText)); CommandManager.InvalidateRequerySuggested(); };
            TryRows.Add(row);
        }
        Raise(nameof(WriteSelectedText)); Raise(nameof(CanFind));
        TrySummaryIs(() => Summarize(report, entries: false, paste));
        TryOpen = true;
        CommandManager.InvalidateRequerySuggested();
    }

    // ---------- «Подобрать адрес» (п. 97): адрес из гайда не помог — ищем рабочий через DNS ----------

    /// <summary>
    /// Поиск рабочих адресов (в тестах — подмена): опрос нескольких DNS, каждый адрес — живая проверка TLS с сертификатом (<see cref="HostsFinder"/>),
    /// до первого рабочего. Ищет окно без прав: запись — только через помощника, и найденное он примеряет заново.
    /// </summary>
    public Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<HostsEntry>>> FindAddresses { get; set; } = (hosts, ct) => Task.Run(async () =>
    {
        // В фоне: сотни сайтов × несколько DNS — тысячи продолжений, на потоке окна они бы его тормозили (жалоба 29.10, 500 строк).
        var r = await new HostsFinder(DnsSources.Default(), new TlsEndpointProbe()).RunAsync(hosts, all: false, ct: ct);
        return (IReadOnlyList<HostsEntry>)[.. r.Domains.Where(d => d.Best is not null).Select(d => new HostsEntry(d.Host, d.Best!.Ip.ToString()))];
    }, ct);

    /// <summary>Адреса, подобранные через DNS в этой примерке: их можно записать, даже если сайт и так открывается.</summary>
    readonly HashSet<HostsEntry> _found = [];

    static bool IsStub(HostsTryRow r) => System.Net.IPAddress.TryParse(r.Ip, out var ip) && !HostsRules.IsPublicAddress(ip);

    /// <summary>Есть ли у сайта уже подобранный или чинящий адрес — тогда искать снова незачем.</summary>
    bool HasWorking(string host) => TryRows.Any(x => x.Host == host && x.CanSelect);

    /// <summary>Сайты из вставки с заглушкой вместо адреса — для них подбор идёт сам.</summary>
    IReadOnlyList<string> StubHosts => TryRows.Select(r => r.Source).Where(r => r.Line == 0 && IsStub(r))
        .Select(r => r.Host).Where(h => !HasWorking(h)).Distinct().ToList();

    /// <summary>Сайты, которым подбор может помочь: заглушка вместо адреса или через DNS не открываются, а строка из гайда не чинит.</summary>
    IReadOnlyList<string> FindHosts => TryRows.Select(r => r.Source)
        .Where(r => r.Line == 0 && (IsStub(r) || r.Verdict is not (HostsTryVerdict.Helps or HostsTryVerdict.NotNeeded) && r.Dns != DnsState.Works))
        .Select(r => r.Host).Where(h => !HasWorking(h)).Distinct().ToList();
    public bool CanFind => FindHosts.Count > 0;

    ICommand? _findCmd;
    public ICommand FindCmd => _findCmd ??= RelayCommand.Async(FindAsync, () => !Busy && CanFind && Can("hosts_try"));

    public async Task FindAsync()
    {
        var hosts = FindHosts;
        if (Busy || hosts.Count == 0) return;
        if (await Need() is not { } c) return;
        Busy = true; Trying = true;
        using var cts = new CancellationTokenSource();
        _cts = cts;
        try { await FindCoreAsync(c, hosts, cts.Token); }
        catch (OperationCanceledException) { TrySummary = L.T("hosts.stopped"); }
        catch (HelperUnavailableException e) { _helper.MarkLost(e); TrySummary = e.Message; }
        catch (HelperException e) { TrySummary = e.Message; }
        catch (Exception e) { TrySummary = Texts.Friendly(e); }
        finally { _cts = null; Busy = false; Trying = false; ProgressText = ""; }
    }

    /// <summary>
    /// Подобрать адреса для сайтов и примерить найденное у помощника. Рабочий адрес встаёт вместо заглушки (строка заглушки уходит из таблицы),
    /// остальные строки остаются. Ошибки — наверх, вызывающему.
    /// </summary>
    async Task FindCoreAsync(HelperClient c, IReadOnlyList<string> hosts, CancellationToken ct, HostsPasteResult? paste = null)
    {
        Done = 0; Total = 1; ProgressText = L.T("hosts.progress.find", new { n = hosts.Count });
        var have = TryRows.Select(r => r.Source.Entry).ToHashSet();
        var found = (await FindAddresses(hosts, ct)).Where(e => !have.Contains(e)).ToList();
        var rows = TryRows.Select(r => r.Source).ToList();
        if (found.Count > 0)
        {
            var tried = (await TryBatchedAsync(c, found, ct)).Rows;
            foreach (var t in tried.Where(t => t.Live?.Ok == true)) _found.Add(t.Entry);
            var replaced = tried.Where(t => t.Live?.Ok == true).Select(t => t.Host).ToHashSet();
            rows.RemoveAll(r => r.Line == 0 && IsStub(r) && replaced.Contains(r.Host));
            rows.AddRange(tried);
        }
        var report = new HostsTryReport(rows, 0, false, rows.Count, DateTime.Now);
        ShowTry(report);
        var foundN = found.Count;
        var missing = hosts.Where(h => found.All(e => e.Host != h)).ToList();
        TrySummaryIs(() => Summarize(report, entries: false, paste) + " " + (foundN > 0 ? L.T("hosts.find.found", new { n = foundN }) : L.T("hosts.find.none"))
            + (missing.Count > 0 && foundN > 0 ? " " + L.T("hosts.find.missing", new { list = string.Join(", ", missing) }) : ""));
    }

    static int Rank(HostsTryRow r) => r.Verdict switch
    {
        HostsTryVerdict.Helps => 0, HostsTryVerdict.Breaks => 1, HostsTryVerdict.Dangerous => 2,
        HostsTryVerdict.Useless => 3, HostsTryVerdict.NotNeeded => 4, _ => 5,
    };

    /// <summary>«Проверено строк: 7. Поможет: 2 · сломает: 1 …» — только ненулевые.</summary>
    static string Summarize(HostsTryReport report, bool entries, HostsPasteResult? paste)
    {
        int Count(HostsTryVerdict v) => report.Rows.Count(r => r.Verdict == v);
        var parts = new List<string>();
        void Add(string key, params HostsTryVerdict[] vs) { if (vs.Sum(Count) is > 0 and var n) parts.Add(L.T(key, new { n })); }
        // Выводы вместо шести счётчиков: что записать можно, что не нужно, а что лучше не записывать (ломает сайт или чужой адрес).
        Add("hosts.summary.part.can_write", HostsTryVerdict.Helps);
        Add("hosts.summary.part.not_needed", HostsTryVerdict.NotNeeded, HostsTryVerdict.Useless, HostsTryVerdict.Blocks);
        Add("hosts.summary.part.avoid", HostsTryVerdict.Breaks, HostsTryVerdict.Dangerous);
        var count = report.Rows.Count;
        var text = parts.Count > 0
            ? L.T(entries ? "hosts.summary.entries_parts" : "hosts.summary.lines_parts", new { n = count, parts = string.Join(" · ", parts) })
            : L.T(entries ? "hosts.summary.entries" : "hosts.summary.lines", new { n = count });
        if (report.Truncated) text += L.T("hosts.summary.truncated", new { shown = report.Rows.Count, total = report.Total });
        if (paste is { SkippedLines: > 0 }) text += L.T("hosts.summary.skipped", new { n = paste.SkippedLines });
        if (paste is { Truncated: true }) text += L.T("hosts.summary.first_n", new { n = HostsPaste.MaxEntries });
        return text;
    }

    /// <summary>
    /// «Записать выбранные»: прежние свои строки остаются, к ним добавляются отмеченные «Поможет». Помощник проверит адреса ещё раз
    /// и скажет, какие не прошли.
    /// </summary>
    public async Task WriteSelectedAsync()
    {
        var chosen = TryRows.Where(r => r.Selected && r.CanSelect).Select(r => r.Source.Entry).ToList();
        if (chosen.Count == 0) return;
        var all = UserRows.Select(r => r.Entry).Concat(chosen).Distinct().ToList();
        // Своих строк помощник принимает не больше предела вставки — иначе он отказал бы всему списку целиком.
        if (all.Count > HostsPaste.MaxEntries)
        {
            TrySummary = L.T("hosts.try.too_many", new { max = HostsPaste.MaxEntries, have = UserRows.Count, n = chosen.Count });
            return;
        }
        if (!Confirm(L.T("hosts.confirm.write_user", new { n = all.Count }))) return;
        if (await Need() is not { } c) return;
        Busy = true;
        try
        {
            var r = await c.HostsUserSetAsync(all);
            var written = r.Added.Count;
            Say(written > 0 ? L.T("hosts.say.user_written", new { n = written }) : L.T("hosts.say.user_none"));
            TrySummary = r.Rejected.Count == 0
                ? (written > 0 ? L.T("hosts.try.written", new { n = written }) : L.T("hosts.try.already"))
                : L.T("hosts.try.written_rejected", new { n = written, list = string.Join("; ", r.Rejected.Select(x => L.T("hosts.rejected_item", new { host = x.Host, reason = L.FromRu(x.Reason) }))) });
            if (written > 0) { TryRows.Clear(); Raise(nameof(WriteSelectedText)); UserOpen = true; }
        }
        catch (HelperUnavailableException e) { _helper.MarkLost(e); Say(e.Message); }
        catch (HelperException e) { Say(e.Message); TrySummary = e.Message; }
        finally { Busy = false; }
        await RefreshAsync();
    }

    // ---------- мои строки ----------

    public ObservableCollection<UserRowVm> UserRows { get; } = [];
    public bool HasUserRows => UserRows.Count > 0;
    public string UserHeader => UserRows.Count == 0 ? L.T("hosts.user.header_none") : L.T("hosts.user.header", new { n = UserRows.Count });

    ICommand? _removeUserCmd, _removeAllUserCmd;
    public ICommand RemoveUserCmd => _removeUserCmd ??= new RelayCommand(p => _ = RemoveUserAsync(p as UserRowVm), p => !Busy && p is UserRowVm && Can("hosts_user_set"));
    public ICommand RemoveAllUserCmd => _removeAllUserCmd ??= RelayCommand.Async(() => RemoveUserAsync(null), () => !Busy && HasUserRows && Can("hosts_user_set"));

    /// <summary>Убрать одну свою строку (или все, если null) — остальные остаются. Чужие строки и блок белого списка не трогаются.</summary>
    public async Task RemoveUserAsync(UserRowVm? row)
    {
        if (UserRows.Count == 0 || row is not null && !UserRows.Contains(row)) return;
        if (!Confirm(RemoveUserQuestion)) return;
        if (await Need() is not { } c) return;
        var rest = row is null ? [] : UserRows.Where(r => r != row).Select(r => r.Entry).ToList();
        Busy = true;
        try
        {
            await c.HostsUserSetAsync(rest);
            Say(row is null ? L.T("hosts.say.user_removed_all") : L.T("hosts.say.user_removed_one", new { host = row.Host }));
        }
        catch (HelperUnavailableException e) { _helper.MarkLost(e); Say(e.Message); }
        catch (HelperException e) { Say(e.Message); }
        finally { Busy = false; }
        await RefreshAsync();
    }

    // ---------- проверить мой hosts ----------

    public ObservableCollection<TryRowVm> AuditRows { get; } = [];
    public ObservableCollection<OffRowVm> OffRows { get; } = [];

    readonly LiveText _auditSummary = new();
    public string AuditSummary { get => _auditSummary.Value; private set { _auditSummary.Set(value); Raise(); Raise(nameof(HasAuditSummary)); } }
    void AuditSummaryIs(Func<string> build) { _auditSummary.Set(build); Raise(nameof(AuditSummary)); Raise(nameof(HasAuditSummary)); }
    public bool HasAuditSummary => AuditSummary.Length > 0;

    bool _auditing;
    public bool Auditing { get => _auditing; private set { if (Set(ref _auditing, value)) CommandManager.InvalidateRequerySuggested(); } }

    int SelectedAudit => AuditRows.Count(r => r.Selected);
    public string CommentText => SelectedAudit > 0 ? L.T("hosts.audit.comment_n", new { n = SelectedAudit }) : L.T("hosts.audit.comment");
    public string UncommentText => L.T("hosts.audit.uncomment_n", new { n = OffRows.Count });
    public bool HasOff => OffRows.Count > 0;
    public bool HasProblems => AuditRows.Any(r => r.CanSelect);

    ICommand? _auditCmd, _commentCmd, _uncommentCmd, _selectProblemsCmd;
    public ICommand AuditCmd => _auditCmd ??= RelayCommand.Async(AuditAsync, () => !Busy && Can("hosts_audit"));
    public ICommand CommentCmd => _commentCmd ??= RelayCommand.Async(CommentAsync, () => !Busy && SelectedAudit > 0 && Can("hosts_comment"));
    public ICommand UncommentCmd => _uncommentCmd ??= RelayCommand.Async(UncommentAsync, () => !Busy && HasOff && Can("hosts_uncomment"));
    public ICommand SelectProblemsCmd => _selectProblemsCmd ??= new RelayCommand(() => { foreach (var r in AuditRows) r.Selected = r.CanSelect; }, () => HasProblems);

    public async Task AuditAsync()
    {
        if (Busy) return;
        if (await Need() is not { } c) return;
        Busy = true; Auditing = true;
        AuditRows.Clear();
        Raise(nameof(CommentText)); Raise(nameof(HasProblems));
        AuditSummary = "";
        Done = 0; Total = 1; ProgressText = L.T("hosts.progress.audit");
        var progress = new Progress<HostsCheckProgress>(p => { Done = p.Done; Total = Math.Max(1, p.Total); ProgressText = L.T("hosts.progress.done", new { done = p.Done, total = p.Total }); });
        using var cts = new CancellationTokenSource();
        _cts = cts;
        try { ShowAudit(await c.HostsAuditAsync(progress, cts.Token)); }
        catch (OperationCanceledException) { AuditSummary = L.T("hosts.stopped"); }
        catch (HelperUnavailableException e) { _helper.MarkLost(e); AuditSummary = e.Message; }
        catch (HelperException e) { AuditSummary = e.Message; }
        catch (Exception e) { AuditSummary = Texts.Friendly(e); }
        finally { _cts = null; Busy = false; Auditing = false; ProgressText = ""; }
    }

    /// <summary>Таблица аудита по готовому отчёту (и для тестов окна). Мешающие («Сломает», «Опасно») можно отметить — по умолчанию не отмечено ничего.</summary>
    public void ShowAudit(HostsTryReport report)
    {
        AuditRows.Clear();
        foreach (var r in report.Rows.OrderBy(Rank).ThenBy(r => r.Line))
        {
            var row = new TryRowVm(r, selectable: r.Problem && r.LineId is not null, selected: false);
            row.Changed += () => { Raise(nameof(CommentText)); CommandManager.InvalidateRequerySuggested(); };
            AuditRows.Add(row);
        }
        Raise(nameof(CommentText)); Raise(nameof(HasProblems));
        int bad = report.Rows.Count(r => r.Problem);
        AuditSummaryIs(() => report.Rows.Count == 0
            ? L.T("hosts.audit.summary_empty")
            : bad == 0
                ? L.T("hosts.audit.summary_ok", new { summary = Summarize(report, entries: true, null) })
                : L.T("hosts.audit.summary_bad", new { n = bad, summary = Summarize(report, entries: true, null) }));
        AuditOpen = true;
        CommandManager.InvalidateRequerySuggested();
    }

    public async Task CommentAsync()
    {
        var ids = AuditRows.Where(r => r.Selected && r.LineId is not null).Select(r => r.LineId!).Distinct().ToList();
        if (ids.Count == 0) return;
        if (!Confirm(L.T("hosts.confirm.comment", new { n = ids.Count }))) return;
        if (await Need() is not { } c) return;
        Busy = true;
        try
        {
            var r = await c.HostsCommentAsync(ids);
            Say(r.Commented.Count > 0
                ? (r.Skipped.Count > 0
                    ? L.T("hosts.say.commented_skipped", new { n = r.Commented.Count, skipped = r.Skipped.Count })
                    : L.T("hosts.say.commented", new { n = r.Commented.Count }))
                : r.Skipped.Count > 0
                    ? L.T("hosts.say.comment_none_reason", new { reason = L.FromRu(r.Skipped[0]) })
                    : L.T("hosts.say.comment_none"));
            if (r.Commented.Count > 0) AuditSummary = L.T("hosts.audit.commented", new { n = r.Commented.Count });
            foreach (var row in AuditRows.Where(x => ids.Contains(x.LineId ?? "")).ToList()) AuditRows.Remove(row);
            Raise(nameof(CommentText)); Raise(nameof(HasProblems));
        }
        catch (HelperUnavailableException e) { _helper.MarkLost(e); Say(e.Message); }
        catch (HelperException e) { Say(e.Message); AuditSummary = e.Message; }
        finally { Busy = false; }
        await RefreshAsync();
    }

    public async Task UncommentAsync()
    {
        if (OffRows.Count == 0) return;
        if (await Need() is not { } c) return;
        Busy = true;
        try
        {
            var r = await c.HostsUncommentAsync();
            Say(r.Changed ? L.T("hosts.say.uncommented", new { n = r.Commented.Count }) : L.T("hosts.say.uncomment_none"));
        }
        catch (HelperUnavailableException e) { _helper.MarkLost(e); Say(e.Message); }
        catch (HelperException e) { Say(e.Message); }
        finally { Busy = false; }
        await RefreshAsync();
    }

    // ---------- «Вернуть как было» ----------

    /// <summary>Что-то из сделанного нами есть в файле: блок проверенных адресов, свои строки или закомментированные.</summary>
    public bool HasOurs => BlockPresent || UserRows.Count > 0 || OffRows.Count > 0;

    public string OursText => OursParts is { Length: > 0 } p ? L.T("hosts.ours", new { parts = p }) : "";

    /// <summary>«проверенные адреса: 2, свои строки: 1» — что от DpiShell лежит в hosts; пусто — ничего.</summary>
    string OursParts
    {
        get
        {
            var parts = new List<string>();
            if (BlockPresent) parts.Add(L.T("hosts.ours.verified", new { n = _status!.Block.Entries.Count }));
            if (UserRows.Count > 0) parts.Add(L.T("hosts.ours.user", new { n = UserRows.Count }));
            if (OffRows.Count > 0) parts.Add(L.T("hosts.ours.off", new { n = OffRows.Count }));
            return string.Join(", ", parts);
        }
    }

    /// <summary>Строка состояния в заголовке блока «Починить адреса» на вкладке «Сеть».</summary>
    public string NetStatus => OursParts is { Length: > 0 } p ? L.T("net.hosts.some", new { parts = p }) : L.T("net.hosts.none");

    ICommand? _restoreAllCmd;
    public ICommand RestoreAllCmd => _restoreAllCmd ??= RelayCommand.Async(RestoreAllAsync, () => !Busy && HasOurs && Can("hosts_restore"));

    public async Task RestoreAllAsync()
    {
        if (!Confirm(RestoreQuestion)) return;
        if (await Need() is not { } c) { RefreshNow(); return; }
        Busy = true;
        try
        {
            var r = await c.HostsRestoreAsync();
            _draftOn = false;
            Say(r.Changed ? L.T("hosts.say.restored") : L.T("hosts.say.restore_none"));
        }
        catch (HelperUnavailableException e) { _helper.MarkLost(e); Say(e.Message); }
        catch (HelperException e) { Say(e.Message); }
        finally { Busy = false; }
        await RefreshAsync();
    }

    // ---------- по состоянию файла ----------

    /// <summary>Свои строки и закомментированные — из состояния hosts у помощника.</summary>
    void RefreshTools()
    {
        var stale = (_status?.UserStale ?? []).ToHashSet();
        UserRows.Clear();
        foreach (var l in _status?.UserLines ?? [])
            UserRows.Add(new UserRowVm(l.Host, l.Ip, l.Date, stale.Contains(new HostsEntry(l.Host, l.Ip))));
        OffRows.Clear();
        foreach (var o in _status?.OffLines ?? []) OffRows.Add(new OffRowVm(o.Line, o.Date, o.Original));
        foreach (var p in new[] { nameof(HasUserRows), nameof(UserHeader), nameof(HasOff), nameof(UncommentText), nameof(HasOurs), nameof(OursText), nameof(NetStatus),
                     nameof(ToolsUnavailable), nameof(ToolsUnavailableText) })
            Raise(p);
    }

    internal static string FormatDate(string iso) => DateTime.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? L.Date(d) : iso;

    /// <summary>«Адрес для X больше не работает — убрать или подобрать новый»: своих строк мы сами не снимаем, только говорим.</summary>
    IEnumerable<string> StaleWarnings() =>
        UserRows.Where(r => r.Stale).Select(r => L.T("hosts.warn.stale", new { host = r.Host }));
}
