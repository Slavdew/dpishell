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
    public string Verdict { get; }
    public string Reason { get; }
    public string Detail { get; }
    /// <summary>Технические признаки (сертификат, владелец сети, совпадение с DNS) — под «Подробнее» у строки.</summary>
    public string Technical { get; }
    public bool HasTechnical => Technical.Length > 0;
    /// <summary>ok — чинит; bad — мешает; warn — не поможет; muted — нейтрально.</summary>
    public string Tone { get; }
    public bool CanSelect { get; }
    public string? LineId => Source.LineId;

    bool _selected;
    public bool Selected { get => _selected; set { if (CanSelect && Set(ref _selected, value)) Changed?.Invoke(); } }
    public event Action? Changed;

    public TryRowVm(HostsTryRow r, bool selectable, bool selected)
    {
        Source = r;
        (Verdict, Reason, Tone) = r.Verdict switch
        {
            HostsTryVerdict.Helps => ("Поможет", "Строка чинит", "ok"),
            HostsTryVerdict.NotNeeded => ("Не нужна", "Открывается и так", "muted"),
            HostsTryVerdict.Breaks => ("Сломает", "Сайт перестанет открываться", "bad"),
            HostsTryVerdict.Useless => ("Не поможет", "Здесь нужна оптимизация, а не hosts", "warn"),
            HostsTryVerdict.Dangerous => ("Опасно", "Адрес чужой, а не этого сайта", "bad"),
            _ => ("Блокирует", "Блокировка (ничего не меняет)", "muted"),
        };
        Detail = (r.Line > 0 ? $"строка {r.Line} · " : "") + Simple(r);
        Technical = Describe(r);
        CanSelect = selectable;
        _selected = selectable && selected;
    }

    /// <summary>Одной фразой, что выяснила проверка: «Подтверждено: адрес принадлежит GitHub».</summary>
    static string Simple(HostsTryRow r) =>
        r.Live is null ? "адрес не проверялся"
        : r.Live.Ok ? (string.IsNullOrWhiteSpace(r.OwnerName) ? "подтверждено: сайт отвечает по этому адресу" : $"подтверждено: адрес принадлежит {r.OwnerName.Trim()}")
        : r.Live.Outcome == EndpointOutcome.BadCertificate ? "по адресу отвечает другой сайт"
        : "адрес не отвечает";

    /// <summary>Технические признаки: сертификат, владелец сети, как соотносится с DNS.</summary>
    static string Describe(HostsTryRow r)
    {
        var parts = new List<string>();
        parts.Add(r.Live is null ? "заглушка (0.0.0.0 и подобные) — не проверялся"
            : r.Live.Ok ? "сертификат верный"
            : r.Live.Outcome == EndpointOutcome.BadCertificate ? "сертификат не этого сайта"
            : "адрес не отвечает");
        if (!string.IsNullOrWhiteSpace(r.OwnerName)) parts.Add($"владелец сети: {r.OwnerName}");
        parts.Add(r.Match switch
        {
            DnsMatch.Same => "совпадает с DNS",
            DnsMatch.SameNet => "из той же сети, что и ответ DNS",
            DnsMatch.Differs => "отличается от DNS",
            _ => "DNS не ответил",
        });
        parts.Add(r.Dns switch { DnsState.Works => "через DNS открывается", DnsState.Fails => "через DNS не открывается", _ => "DNS не проверен" });
        return string.Join(" · ", parts);
    }
}

public sealed record UserRowVm(string Host, string Ip, string Date, bool Stale)
{
    public string Text => $"{Host} → {Ip}";
    public string Note => Stale ? $"{Date} · адрес больше не работает" : Date;
    public HostsEntry Entry => new(Host, Ip);
}

public sealed record OffRowVm(int Line, string Date, string Original)
{
    public string Text => Original.Trim();
    public string Note => $"строка {Line} · закомментирована {Date}";
}

/// <summary>
/// «Починить адреса», часть 2 (ТЗ 12): примерить строки из гайда, свои строки, проверка чужих строк hosts, «закомментировать мешающие».
/// Всё через помощника: окно ничего не пишет в hosts и не называет адресов для записи, кроме тех, что человек выбрал, —
/// помощник проверяет их заново.
/// </summary>
public sealed partial class HostsViewModel
{
    public const string WriteUserQuestion =
        "Будет изменён системный файл hosts: в конец добавится блок «DpiShell user» из {0} строк (у каждой пометка dpishell-user), другие строки не трогаем. Копия сохранена. " +
        "Защитник Windows или другой антивирус может показать предупреждение (например, HostsFileHijack) — это реакция на любую правку hosts; разрешите изменение (название кнопки зависит от версии). Продолжить?";

    public const string CommentQuestion =
        "В файле hosts строк станут комментариями: {0}. Перед каждой появится «# DpiShell off», ничего не удаляется. «Вернуть закомментированные» вернёт всё как было. " +
        "Защитник Windows или другой антивирус может показать предупреждение — разрешите изменение (название кнопки зависит от версии). Продолжить?";

    public const string RestoreQuestion =
        "Вернуть hosts как было? Уберём блоки и строки DpiShell и раскомментируем строки, которые закомментировали мы. Всё остальное в файле не меняется.";

    public const string RemoveUserQuestion = "Убрать эти свои строки из hosts? Остальные строки файла не меняются.";

    // ---------- разделы вкладки ----------

    bool _listOpen = true, _tryOpen, _userOpen, _auditOpen;
    public bool ListOpen { get => _listOpen; set => Set(ref _listOpen, value); }
    public bool TryOpen { get => _tryOpen; set => Set(ref _tryOpen, value); }
    public bool UserOpen { get => _userOpen; set => Set(ref _userOpen, value); }
    public bool AuditOpen { get => _auditOpen; set => Set(ref _auditOpen, value); }

    bool Can(string cmd) => _helper.Client?.Compat.Supports(cmd) ?? true;

    /// <summary>Помощник старее программы: новых команд у него нет. Подсказка вместо мёртвых кнопок.</summary>
    public bool ToolsUnavailable => _helper.Reader is { } c && !c.Compat.Supports("hosts_try");
    public string ToolsUnavailableText => "Для этих функций нужен новый помощник — обновите его (один запрос прав администратора).";

    // ---------- примерить строки ----------

    string _pasteText = "";
    public string PasteText { get => _pasteText; set { if (Set(ref _pasteText, value)) CommandManager.InvalidateRequerySuggested(); } }

    public ObservableCollection<TryRowVm> TryRows { get; } = [];

    string _trySummary = "";
    public string TrySummary { get => _trySummary; private set { if (Set(ref _trySummary, value)) Raise(nameof(HasTrySummary)); } }
    public bool HasTrySummary => TrySummary.Length > 0;

    bool _trying;
    public bool Trying { get => _trying; private set { if (Set(ref _trying, value)) CommandManager.InvalidateRequerySuggested(); } }

    int SelectedTry => TryRows.Count(r => r.Selected);
    public string WriteSelectedText => SelectedTry > 0 ? $"Записать выбранные ({SelectedTry})" : "Записать выбранные";

    ICommand? _tryCmd, _writeSelectedCmd, _clearTryCmd;
    public ICommand TryCmd => _tryCmd ??= RelayCommand.Async(TryAsync, () => !Busy && PasteText.Trim().Length > 0 && Can("hosts_try"));
    public ICommand WriteSelectedCmd => _writeSelectedCmd ??= RelayCommand.Async(WriteSelectedAsync, () => !Busy && SelectedTry > 0 && Can("hosts_user_set"));
    public ICommand ClearTryCmd => _clearTryCmd ??= new RelayCommand(() => { PasteText = ""; TryRows.Clear(); TrySummary = ""; Raise(nameof(WriteSelectedText)); }, () => !Busy);

    /// <summary>Вставка → пары «адрес имя» → проверка у помощника → таблица с вердиктами. Чинящие строки отмечены, остальные нет.</summary>
    public async Task TryAsync()
    {
        if (Busy) return;
        var paste = HostsPaste.Parse(PasteText);
        TryRows.Clear();
        Raise(nameof(WriteSelectedText));
        if (paste.Entries.Count == 0)
        {
            TrySummary = "Не нашла ни одной строки вида «адрес имя». Вставьте строки из гайда, например: 140.82.121.3 github.com";
            return;
        }
        if (await Need() is not { } c) return;
        Busy = true; Trying = true;
        Done = 0; Total = 1; ProgressText = "Примеряю строки…";
        var progress = new Progress<HostsCheckProgress>(p => { Done = p.Done; Total = Math.Max(1, p.Total); ProgressText = $"Проверено {p.Done} из {p.Total}"; });
        using var cts = new CancellationTokenSource();
        _cts = cts;
        try
        {
            ShowTry(await c.HostsTryAsync(paste.Entries, progress, cts.Token), paste);
        }
        catch (OperationCanceledException) { TrySummary = "Проверка остановлена."; }
        catch (HelperUnavailableException e) { _helper.MarkLost(e); TrySummary = e.Message; }
        catch (HelperException e) { TrySummary = e.Message; }
        catch (Exception e) { TrySummary = Texts.Friendly(e); }
        finally { _cts = null; Busy = false; Trying = false; ProgressText = ""; }
    }

    /// <summary>Таблица примерки по готовому отчёту (и для тестов окна).</summary>
    public void ShowTry(HostsTryReport report, HostsPasteResult? paste = null)
    {
        TryRows.Clear();
        foreach (var r in report.Rows.OrderBy(Rank).ThenBy(r => r.Host, StringComparer.Ordinal))
        {
            var row = new TryRowVm(r, selectable: r.Writable, selected: r.Writable);
            row.Changed += () => { Raise(nameof(WriteSelectedText)); CommandManager.InvalidateRequerySuggested(); };
            TryRows.Add(row);
        }
        Raise(nameof(WriteSelectedText));
        TrySummary = Summarize(report, "Проверено строк", paste);
        TryOpen = true;
        CommandManager.InvalidateRequerySuggested();
    }

    static int Rank(HostsTryRow r) => r.Verdict switch
    {
        HostsTryVerdict.Helps => 0, HostsTryVerdict.Breaks => 1, HostsTryVerdict.Dangerous => 2,
        HostsTryVerdict.Useless => 3, HostsTryVerdict.NotNeeded => 4, _ => 5,
    };

    /// <summary>«Проверено строк: 7. Поможет: 2 · сломает: 1 …» — только ненулевые.</summary>
    static string Summarize(HostsTryReport report, string head, HostsPasteResult? paste)
    {
        int Count(HostsTryVerdict v) => report.Rows.Count(r => r.Verdict == v);
        var parts = new List<string>();
        void Add(string name, params HostsTryVerdict[] vs) { if (vs.Sum(Count) is > 0 and var n) parts.Add($"{name}: {n}"); }
        // Выводы вместо шести счётчиков: что записать можно, что не нужно, а что лучше не записывать (ломает сайт или чужой адрес).
        Add("можно записать", HostsTryVerdict.Helps);
        Add("не нужны", HostsTryVerdict.NotNeeded, HostsTryVerdict.Useless, HostsTryVerdict.Blocks);
        Add("не стоит", HostsTryVerdict.Breaks, HostsTryVerdict.Dangerous);
        var text = $"{head}: {report.Rows.Count}" + (parts.Count > 0 ? ". " + string.Join(" · ", parts) : "") + ".";
        if (report.Truncated) text += $" Проверено первых {report.Rows.Count} из {report.Total}.";
        if (paste is { SkippedLines: > 0 }) text += $" Пропущено строк без адреса или имени: {paste.SkippedLines}.";
        if (paste is { Truncated: true }) text += $" Взяты первые {HostsPaste.MaxEntries} строк.";
        return text;
    }

    /// <summary>
    /// «Записать выбранные»: прежние свои строки остаются, к ним добавляются отмеченные «Поможет». Помощник проверит адреса ещё раз
    /// и скажет, какие не прошли.
    /// </summary>
    public async Task WriteSelectedAsync()
    {
        var chosen = TryRows.Where(r => r.Selected && r.Source.Writable).Select(r => r.Source.Entry).ToList();
        if (chosen.Count == 0) return;
        var all = UserRows.Select(r => r.Entry).Concat(chosen).Distinct().ToList();
        if (!Confirm(string.Format(WriteUserQuestion, all.Count))) return;
        if (await Need() is not { } c) return;
        Busy = true;
        try
        {
            var r = await c.HostsUserSetAsync(all);
            var written = r.Added.Count;
            Say(written > 0 ? $"Записано своих строк: {written}. «Вернуть как было» уберёт их." : "Новых строк не записано.");
            TrySummary = r.Rejected.Count == 0
                ? (written > 0 ? $"Записано строк: {written}." : "Эти строки уже записаны.")
                : $"Записано строк: {written}. Не прошли повторную проверку: " + string.Join("; ", r.Rejected.Select(x => $"{x.Host} — {x.Reason}")) + ".";
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
    public string UserHeader => UserRows.Count == 0 ? "Своих строк в hosts нет." : $"Записано своих строк: {UserRows.Count}.";

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
            Say(row is null ? "Готово: свои строки убраны из hosts." : $"Готово: строка для {row.Host} убрана.");
        }
        catch (HelperUnavailableException e) { _helper.MarkLost(e); Say(e.Message); }
        catch (HelperException e) { Say(e.Message); }
        finally { Busy = false; }
        await RefreshAsync();
    }

    // ---------- проверить мой hosts ----------

    public ObservableCollection<TryRowVm> AuditRows { get; } = [];
    public ObservableCollection<OffRowVm> OffRows { get; } = [];

    string _auditSummary = "";
    public string AuditSummary { get => _auditSummary; private set { if (Set(ref _auditSummary, value)) Raise(nameof(HasAuditSummary)); } }
    public bool HasAuditSummary => AuditSummary.Length > 0;

    bool _auditing;
    public bool Auditing { get => _auditing; private set { if (Set(ref _auditing, value)) CommandManager.InvalidateRequerySuggested(); } }

    int SelectedAudit => AuditRows.Count(r => r.Selected);
    public string CommentText => SelectedAudit > 0 ? $"Закомментировать выбранные ({SelectedAudit})" : "Закомментировать выбранные";
    public string UncommentText => $"Вернуть закомментированные ({OffRows.Count})";
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
        Done = 0; Total = 1; ProgressText = "Проверяю hosts…";
        var progress = new Progress<HostsCheckProgress>(p => { Done = p.Done; Total = Math.Max(1, p.Total); ProgressText = $"Проверено {p.Done} из {p.Total}"; });
        using var cts = new CancellationTokenSource();
        _cts = cts;
        try { ShowAudit(await c.HostsAuditAsync(progress, cts.Token)); }
        catch (OperationCanceledException) { AuditSummary = "Проверка остановлена."; }
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
        AuditSummary = report.Rows.Count == 0
            ? "В вашем hosts нет чужих строк с адресами сайтов."
            : (bad == 0 ? "Мешающих строк не нашла. " : $"Мешают строки: {bad}. Отметьте те, что хотите закомментировать — ничего не удаляется. ")
              + Summarize(report, "Проверено записей", null);
        AuditOpen = true;
        CommandManager.InvalidateRequerySuggested();
    }

    public async Task CommentAsync()
    {
        var ids = AuditRows.Where(r => r.Selected && r.LineId is not null).Select(r => r.LineId!).Distinct().ToList();
        if (ids.Count == 0) return;
        if (!Confirm(string.Format(CommentQuestion, ids.Count))) return;
        if (await Need() is not { } c) return;
        Busy = true;
        try
        {
            var r = await c.HostsCommentAsync(ids);
            Say(r.Commented.Count > 0
                ? $"Закомментировано строк: {r.Commented.Count}." + (r.Skipped.Count > 0 ? $" Пропущено: {r.Skipped.Count} (файл изменился — проверьте hosts заново)." : "")
                : "Ничего не закомментировано." + (r.Skipped.Count > 0 ? " " + r.Skipped[0] : ""));
            if (r.Commented.Count > 0) AuditSummary = $"Закомментировано строк: {r.Commented.Count}. Верните их кнопкой «Вернуть закомментированные».";
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
            Say(r.Changed ? $"Готово: строк возвращено — {r.Commented.Count}." : "Закомментированных строк нет.");
        }
        catch (HelperUnavailableException e) { _helper.MarkLost(e); Say(e.Message); }
        catch (HelperException e) { Say(e.Message); }
        finally { Busy = false; }
        await RefreshAsync();
    }

    // ---------- «Вернуть как было» ----------

    /// <summary>Что-то из сделанного нами есть в файле: блок проверенных адресов, свои строки или закомментированные.</summary>
    public bool HasOurs => BlockPresent || UserRows.Count > 0 || OffRows.Count > 0;

    public string OursText
    {
        get
        {
            var parts = new List<string>();
            if (BlockPresent) parts.Add($"проверенные адреса: {_status!.Block.Entries.Count}");
            if (UserRows.Count > 0) parts.Add($"свои строки: {UserRows.Count}");
            if (OffRows.Count > 0) parts.Add($"закомментированные: {OffRows.Count}");
            return parts.Count == 0 ? "" : "В hosts от DpiShell — " + string.Join(", ", parts) + ".";
        }
    }

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
            Say(r.Changed ? "Готово: hosts возвращён как был." : "Изменений DpiShell в hosts нет.");
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
            UserRows.Add(new UserRowVm(l.Host, l.Ip, FormatDate(l.Date), stale.Contains(new HostsEntry(l.Host, l.Ip))));
        OffRows.Clear();
        foreach (var o in _status?.OffLines ?? []) OffRows.Add(new OffRowVm(o.Line, FormatDate(o.Date), o.Original));
        foreach (var p in new[] { nameof(HasUserRows), nameof(UserHeader), nameof(HasOff), nameof(UncommentText), nameof(HasOurs), nameof(OursText),
                     nameof(ToolsUnavailable), nameof(ToolsUnavailableText) })
            Raise(p);
    }

    static string FormatDate(string iso) => DateTime.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? $"{d:dd.MM.yyyy}" : iso;

    /// <summary>«Адрес для X больше не работает — убрать или подобрать новый»: своих строк мы сами не снимаем, только говорим.</summary>
    IEnumerable<string> StaleWarnings() =>
        UserRows.Where(r => r.Stale).Select(r => $"Адрес для {r.Host} больше не работает — уберите строку или подберите новый адрес.");
}
