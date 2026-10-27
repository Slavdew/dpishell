using System.IO;
using System.Text.Json;
using System.Windows.Input;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.App;

/// <summary>
/// Последний отчёт подбора ПК (UX-аудит 8, §4 «Подбор»): хранится рядом с window.json до запуска нового подбора,
/// чтобы таблица на «Подборе» и «Итоги подбора» не пустели после перезапуска окна. Файл другой версии или битый — молча не показывается.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Версия формата last-select.json: при смене формата старый файл просто не читается.</summary>
    public const int LastSelectVersion = 1;

    sealed record LastSelectDoc(int Version, DateTime At, SelectionReport? Report);

    /// <summary>%LocalAppData%\DpiShell\last-select.json (в тестах — рядом с временным window.json).</summary>
    public string LastSelectFile => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Settings.File))!, "last-select.json");

    DateTime? _lastSelectAt;
    /// <summary>Когда закончился подбор, чей отчёт сейчас на «Подборе»; null — отчёта нет (не было или идёт новый).</summary>
    DateTime? LastSelectAt
    {
        get => _lastSelectAt;
        set
        {
            if (_lastSelectAt == value) return;
            _lastSelectAt = value;
            Raise(nameof(HasLastReport)); Raise(nameof(LastReportTip));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool HasLastReport => _lastSelectAt is not null;

    /// <summary>Подсказка «Итогов подбора»: когда был подбор; отчёта нет — почему кнопка неактивна.</summary>
    public string LastReportTip => _lastSelectAt is { } at
        ? L.T("home.report.tip", new { date = L.DayMonth(at), time = L.Time(at) })
        : L.T("home.report.none");

    /// <summary>«Итоги подбора»: «Подбор» (ПК) с раскрытой карточкой итога.</summary>
    public ICommand ShowLastReportCmd => _showLastReportCmd ??= new RelayCommand(ShowLastReport, () => HasLastReport);
    ICommand? _showLastReportCmd;

    void ShowLastReport()
    {
        if (!HasLastReport) return;
        if (Router.Connected) Router.SelectOnPc = true;
        ResultExpanded = true;
        Page = Pages.Select;
    }

    /// <summary>Свежий отчёт — на диск; прочитанный с диска — итог свёрнут в строку «Последний подбор: 27.10, 14:05 · лучшая …» («Подробнее» раскрывает).</summary>
    void SaveLastOrSummarize(SelectionReport r, DateTime? restoredAt)
    {
        if (restoredAt is not { } at) { SaveLastReport(r, DateTime.Now); return; }
        LastSelectAt = at;
        var when = new { date = L.DayMonth(at), time = L.Time(at) };
        ResultSummary = r.Best is { } best
            ? L.T("select.summary.last", new { when.date, when.time, name = best.Strategy.Title })
            : L.T("select.summary.last_none", when);
    }

    void SaveLastReport(SelectionReport r, DateTime at)
    {
        LastSelectAt = at;
        try
        {
            var file = LastSelectFile;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new LastSelectDoc(LastSelectVersion, at, r), HelperProtocol.Json));
            File.Move(tmp, file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException) { }
    }

    /// <summary>Новый подбор начался или история очищена: прежний отчёт больше не показывается и с диска убирается.</summary>
    void ForgetLastReport()
    {
        LastSelectAt = null;
        try { File.Delete(LastSelectFile); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Прочитать отчёт; null — нет файла, другая версия или файл битый.</summary>
    public static (SelectionReport Report, DateTime At)? ReadLastReport(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var doc = JsonSerializer.Deserialize<LastSelectDoc>(File.ReadAllText(file), HelperProtocol.Json);
            if (doc is not { Version: LastSelectVersion, Report: { Scores: { Count: > 0 } scores } report }) return null;
            // Поля, без которых строку таблицы не собрать, — файл чужой или обрезанный.
            if (report.Baseline is null || report.TestedGroups is null || scores.Any(s => s?.Strategy?.Id is null || s.Verdicts is null)) return null;
            return (report, doc.At);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException) { return null; }
    }

    /// <summary>При запуске окна: показать сохранённый отчёт без автоприменения и без перечитывания статистики.</summary>
    void RestoreLastReport()
    {
        if (ReadLastReport(LastSelectFile) is not { } saved) return;
        try { ShowReportCore(saved.Report, saved.At); }
        catch (Exception e) when (e is NullReferenceException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            // Записан другой сборкой с неполными данными — не показывать вовсе, а не половину.
            ResetResults();
            SelectProgressText = "";
            LastSelectAt = null;
        }
    }
}
