using System.Collections.ObjectModel;
using System.Windows.Input;
using DpiShell.Core;

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
/// </summary>
public sealed class HostsViewModel : Observable
{
    readonly Shell _shell;
    HostsCheckReport? _report;
    CancellationTokenSource? _cts;

    /// <summary>С чем работаем: системный hosts (по умолчанию) или копия в тестах.</summary>
    public HostsManager Manager { get; set; }
    public Action<string> Say { get; set; } = _ => { };
    public Func<string, bool> Confirm { get; set; } = _ => false;
    public event Action<string>? Notify;

    public HostsViewModel(Shell shell)
    {
        _shell = shell;
        Manager = shell.Hosts;
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

    /// <summary>Переключатель экрана. Выключение убирает наш блок (с вопросом).</summary>
    public bool Enabled
    {
        get => _shell.State.HostsFix;
        set
        {
            if (value == Enabled) return;
            if (!value && BlockPresent && !Confirm("Убрать записи DpiShell из файла hosts? Другие строки файла не меняются."))
            {
                Raise();
                return;
            }
            _shell.State.HostsFix = value;
            try { _shell.SaveState(); } catch (Exception e) { Say(Texts.Friendly(e)); }
            Raise();
            Raise(nameof(Disabled));
            RaiseStatus();
            if (!value && BlockPresent) _ = RevertAsync(ask: false);
        }
    }

    public bool Disabled => !Enabled;
    public string OffHint => "Выключено. Включите, если не качаются файлы с GitHub, не открывается веб-версия Telegram или Discord запускается несколько минут.";

    // ---------- состояние файла ----------

    HostsStatus? _status;

    public bool BlockPresent => _status?.Block.Present == true;

    /// <summary>«Записано 3 адреса 17.10.2026 в 14:30. Проверка раз в день; устаревшие убираются сами.»</summary>
    public string StatusText
    {
        get
        {
            if (_status is null) return "";
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

    /// <summary>Перечитать hosts (в фоне: файл может держать антивирус).</summary>
    public async Task RefreshAsync()
    {
        try { _status = await Task.Run(Manager.Status); }
        catch (Exception e) { Say(Texts.Friendly(e)); }
        RaiseStatus();
    }

    /// <summary>То же синхронно (тест окна строит экран по готовому состоянию).</summary>
    public void RefreshNow()
    {
        _status = Manager.Status();
        RaiseStatus();
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
        Busy = true; Checking = true;
        using var cts = new CancellationTokenSource();
        _cts = cts;
        Rows.Clear(); PreviewLines.Clear(); SetReport(null); Summary = "";
        Done = 0; Total = 1; ProgressText = "Проверяю адреса…";
        var progress = new Progress<(int Done, int Total)>(p => { Done = p.Done; Total = Math.Max(1, p.Total); ProgressText = $"Проверено {p.Done} из {p.Total}"; });
        try
        {
            var report = await Task.Run(() => Manager.CheckAsync(progress, cts.Token));
            ShowReport(report);
        }
        catch (OperationCanceledException) { Summary = "Проверка остановлена."; }
        catch (Exception e) { Summary = Texts.Friendly(e); }
        finally { _cts = null; Busy = false; Checking = false; ProgressText = ""; }
        await RefreshAsync();
    }

    /// <summary>Таблица и предпросмотр по готовому отчёту (и для тестов окна).</summary>
    public void ShowReport(HostsCheckReport report)
    {
        Rows.Clear();
        foreach (var r in report.Rows.OrderByDescending(r => r.Install).ThenBy(r => r.Host)) Rows.Add(HostsRow.From(r));
        SetReport(report);
        PreviewLines.Clear();
        if (report.NothingToFix)
        {
            Summary = "Нечего чинить: всё открывается и так.";
            return;
        }
        Summary = $"Можно записать адресов: {report.ToInstall.Count}. Остальные не нужны или не прошли проверку.";
        try
        {
            var p = Manager.Preview(report.ToInstall);
            foreach (var e in p.Added) PreviewLines.Add($"+ {e.Ip} {e.Host}");
            foreach (var e in p.Removed) PreviewLines.Add($"− {e.Ip} {e.Host}");
            if (!p.Changed) PreviewLines.Add("Эти адреса уже записаны — менять нечего.");
        }
        catch (Exception e) { PreviewLines.Add(Texts.Friendly(e)); }
    }

    void SetReport(HostsCheckReport? r)
    {
        _report = r;
        Raise(nameof(CanWrite)); Raise(nameof(ApplyText));
        CommandManager.InvalidateRequerySuggested();
    }

    public const string WriteQuestion =
        "Будет изменён системный файл hosts: добавлен блок DpiShell из {0} строк, другие строки не трогаем. Копия сохранена. " +
        "Защитник Windows может показать предупреждение HostsFileHijack — это реакция на любую правку hosts; выберите в нём «Разрешить». Продолжить?";

    public async Task ApplyAsync()
    {
        if (_report is not { NothingToFix: false } report) return;
        int lines;
        try { lines = Manager.Preview(report.ToInstall).BlockLines.Count; } catch (Exception) { lines = report.ToInstall.Count + 3; }
        if (!Confirm(string.Format(WriteQuestion, lines))) return;
        Busy = true;
        try
        {
            var r = await Task.Run(() => Manager.Apply(report));
            _shell.State.HostsCheckedAt = DateTime.Now;
            _shell.SaveState();
            Say($"Записано {report.ToInstall.Count}, проверка раз в день; устаревшие убираются сами.");
            PreviewLines.Clear();
            SetReport(null);
            Summary = "";
        }
        catch (HostsWriteException e) { Say(e.Message); Summary = e.Message; }
        catch (Exception e) { Say(Texts.Friendly(e)); }
        finally { Busy = false; }
        await RefreshAsync();
    }

    public Task RevertAsync() => RevertAsync(ask: true);

    async Task RevertAsync(bool ask)
    {
        if (ask && !Confirm("Убрать записи DpiShell из файла hosts? Другие строки файла не меняются.")) return;
        Busy = true;
        try
        {
            var r = await Task.Run(Manager.Revert);
            Say(r.Changed ? "Готово: записи DpiShell убраны из hosts." : "Записей DpiShell в hosts нет.");
        }
        catch (HostsWriteException e) { Say(e.Message); }
        catch (Exception e) { Say(Texts.Friendly(e)); }
        finally { Busy = false; }
        await RefreshAsync();
    }

    // ---------- перепроверка ----------

    /// <summary>Раз в сутки (и при старте): функция включена, а с прошлой проверки прошло больше суток — перепроверить записанное.</summary>
    public async Task RecheckIfDueAsync()
    {
        if (Busy) return;
        if (!Enabled || _shell.State.HostsCheckedAt is { } at && DateTime.Now - at < TimeSpan.FromDays(1))
        {
            if (_status is null) await RefreshAsync();   // первый раз — хотя бы состояние (для трея и предупреждений)
            return;
        }
        Busy = true;
        try
        {
            var r = await Task.Run(() => Manager.RecheckAsync());
            _shell.State.HostsCheckedAt = DateTime.Now;
            _shell.SaveState();
            foreach (var host in r.Removed.Select(x => x.Host).Distinct())
                Notify?.Invoke($"Адрес для {host} устарел, убран из hosts.");
            if (r.BlockLost) Notify?.Invoke("Записи DpiShell пропали из hosts (Защитник Windows?). Откройте «Починить адреса», чтобы записать снова.");
        }
        catch (HostsWriteException e) { Say(e.Message); }
        catch (Exception e) { Say(Texts.Friendly(e)); }
        finally { Busy = false; }
        await RefreshAsync();
    }
}
