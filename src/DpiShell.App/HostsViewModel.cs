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
        r.OwnerName ?? (r.Owner == OwnerState.Unknown ? "не узнать" : "—"),
        r.Live is null ? "не проверялся" : r.Live.Ok ? "✓ сертификат" : r.Live.Outcome == EndpointOutcome.BadCertificate ? "✗ чужой сертификат" : "✗ не отвечает",
        r.Live?.Ok == true,
        r.Dns switch { DnsState.Works => "✓ работает", DnsState.Fails => "✗ не работает", _ => "?" },
        r.Verdict switch
        {
            HostsVerdict.Install => "записать",
            HostsVerdict.NotNeeded => "не нужно",
            _ => "не подходит",
        },
        Why(r),
        r.Install);

    static string Why(HostsCheckRow r) => r.Verdict switch
    {
        HostsVerdict.Install => "адрес из DNS не открывается, этот — да",
        HostsVerdict.NotNeeded => "через DNS и так открывается",
        HostsVerdict.Unreachable => "адрес не отвечает" + Detail(r.Live?.Detail),
        HostsVerdict.BadCertificate => "сертификат не этого сайта" + Detail(r.Live?.Detail),
        HostsVerdict.OwnerMismatch => $"сеть принадлежит другой компании ({r.OwnerName})",
        _ => r.DnsDetail.Length > 0 ? r.DnsDetail : "не из разрешённого списка",
    };

    static string Detail(string? d) => string.IsNullOrWhiteSpace(d) ? "" : $" ({d.Trim().TrimEnd('.')})";
}

/// <summary>
/// «Починить адреса» (ТЗ v2 §3, решение 4): выключено по умолчанию; «Проверить адреса» → таблица → предпросмотр →
/// «Записать N адресов» с подтверждением → «Вернуть как было». Перепроверка раз в сутки и при старте, если функция включена.
/// Окно без прав (11-helper-spec §1): hosts читает и пишет помощник — hosts_check/preview/apply/revert/recheck;
/// состояние файла — из details. Адресов окно не передаёт: помощник пишет только то, что сам проверил.
/// </summary>
public sealed class HostsViewModel : Observable
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
        StopCmd = new RelayCommand(() => _cts?.Cancel(), () => Checking);
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
            if (!value && BlockPresent && !Confirm("Убрать записи DpiShell из файла hosts? Другие строки файла не меняются."))
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
    public string OffHint => "Выключено. Включите, если не качаются файлы с GitHub, не открывается веб-версия Telegram или Discord запускается несколько минут.";

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
            if (_status is null) return _helper.Reader is null ? "Состояние файла hosts покажет помощник DpiShell." : "";
            var n = _status.Block.Entries.Count;
            if (n == 0) return _status.Block.Present ? "Блок DpiShell в hosts пустой." : "Ничего не записано — hosts без изменений.";
            var at = _status.AppliedAt is { } a ? $" {a:dd.MM.yyyy} в {a:HH:mm}" : "";
            return $"Записано адресов: {n}{at}. Проверка раз в день; устаревшие убираются сами.";
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
                list.Add("Записи DpiShell пропали из hosts — обычно их убирает Защитник Windows. Нажмите «Проверить адреса» и запишите снова; если Защитник спросит — выберите «Разрешить».");
            if (s.Block.Damaged)
                list.Add("Блок DpiShell в hosts повреждён (его правили вручную). «Записать» или «Вернуть как было» исправит его.");
            if (s.ReadOnly)
                list.Add("Файл hosts помечен «только чтение». При записи программа снимет эту отметку и вернёт её обратно.");
            foreach (var c in s.Conflicts.GroupBy(c => c.Host).Select(g => g.First()))
                list.Add($"В hosts уже есть запись для {c.Host} от другой программы — она действует раньше нашей.");
            if (!Enabled && s.Block.Present)
                list.Add("Функция выключена, а записи DpiShell в hosts остались (от прошлой установки). Их можно убрать кнопкой «Вернуть как было».");
            return list;
        }
    }

    public bool HasWarnings => Warnings.Count > 0;

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
        RaiseEnabled();
    }

    void RaiseStatus()
    {
        foreach (var p in new[] { nameof(BlockPresent), nameof(StatusText), nameof(AppliedLines), nameof(Warnings), nameof(HasWarnings) }) Raise(p);
        CommandManager.InvalidateRequerySuggested();
    }

    // ---------- проверка → предпросмотр → запись ----------

    string _summary = "";
    /// <summary>Итог проверки: «Можно записать N» или «Нечего чинить: всё открывается и так».</summary>
    public string Summary { get => _summary; private set { if (Set(ref _summary, value)) Raise(nameof(HasSummary)); } }
    public bool HasSummary => Summary.Length > 0;

    public bool CanWrite => _report is { NothingToFix: false };
    public string ApplyText => _report is { NothingToFix: false } r ? $"Записать адресов: {r.ToInstall.Count}" : "Записать";

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
        Done = 0; Total = 1; ProgressText = "Проверяю адреса…";
        var progress = new Progress<HostsCheckProgress>(p => { Done = p.Done; Total = Math.Max(1, p.Total); ProgressText = $"Проверено {p.Done} из {p.Total}"; });
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
        catch (OperationCanceledException) { Summary = "Проверка остановлена."; }
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
        PreviewLines.Clear();
        if (report.NothingToFix)
        {
            Summary = "Нечего чинить: всё открывается и так.";
            return;
        }
        Summary = $"Можно записать адресов: {report.ToInstall.Count}. Остальные не нужны или не прошли проверку.";
        if (preview is not { } p) return;
        foreach (var e in p.Added) PreviewLines.Add($"+ {e.Ip} {e.Host}");
        foreach (var e in p.Removed) PreviewLines.Add($"− {e.Ip} {e.Host}");
        if (!p.Changed) PreviewLines.Add("Эти адреса уже записаны — менять нечего.");
    }

    void SetReport(HostsCheckReport? r)
    {
        _report = r;
        if (r is null) _preview = null;
        Raise(nameof(CanWrite)); Raise(nameof(ApplyText));
        CommandManager.InvalidateRequerySuggested();
    }

    public const string WriteQuestion =
        "Будет изменён системный файл hosts: добавлен блок DpiShell из {0} строк, другие строки не трогаем. Копия сохранена. " +
        "Защитник Windows может показать предупреждение HostsFileHijack — это реакция на любую правку hosts; выберите в нём «Разрешить». Продолжить?";

    public async Task ApplyAsync()
    {
        if (_report is not { NothingToFix: false } report) return;
        var lines = _preview?.BlockLines.Count ?? report.ToInstall.Count + 3;
        if (!Confirm(string.Format(WriteQuestion, lines))) return;
        if (await Need() is not { } c) return;
        Busy = true;
        try
        {
            await c.HostsApplyAsync();
            _draftOn = false;   // помощник включил функцию сам
            Say($"Записано {report.ToInstall.Count}, проверка раз в день; устаревшие убираются сами.");
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
        if (ask && !Confirm("Убрать записи DpiShell из файла hosts? Другие строки файла не меняются.")) return;
        if (await Need() is not { } c) { RefreshNow(); return; }
        Busy = true;
        try
        {
            var r = await c.HostsRevertAsync();
            _draftOn = false;
            Say(r.Changed ? "Готово: записи DpiShell убраны из hosts." : "Записей DpiShell в hosts нет.");
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
        if (!HelperOn || _helper.Details?.HostsCheckedAt is { } at && DateTime.Now - at < TimeSpan.FromDays(1) || _helper.Client is not { } c)
        {
            if (_status is null) await RefreshAsync();   // первый раз — хотя бы состояние (для трея и предупреждений)
            return;
        }
        Busy = true;
        try
        {
            var r = await c.HostsRecheckAsync();
            foreach (var host in r.Removed.Select(x => x.Host).Distinct())
                Notify?.Invoke($"Адрес для {host} устарел, убран из hosts.");
            if (r.BlockLost) Notify?.Invoke("Записи DpiShell пропали из hosts (Защитник Windows?). Откройте «Починить адреса», чтобы записать снова.");
        }
        catch (HelperUnavailableException e) { _helper.MarkLost(e); }
        catch (HelperException e) { Say(e.Message); }
        finally { Busy = false; }
        await RefreshAsync();
    }
}
