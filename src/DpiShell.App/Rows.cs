using System.Net;
using DpiShell.Core;

namespace DpiShell.App;

public sealed class GroupOption(string id, string title, bool isChecked) : Observable
{
    bool _checked = isChecked;
    public string Id { get; } = id;
    public string Title { get; } = title;
    public bool IsChecked { get => _checked; set => Set(ref _checked, value); }
}

/// <summary>Строка списка стратегий на главной: название, статистика подборов, найдена ли поиском.</summary>
public sealed record StrategyChoice(string Id, string Title, string Stats, bool Found);

/// <summary>Строка «Проверить сейчас».</summary>
public sealed record CheckRow(bool Ok, string Group, string Url, string Detail)
{
    public string Mark => Ok ? "✓" : "✗";

    public static CheckRow From(TargetVerdict v, Func<string, string> groupTitle) =>
        new(v.Ok, groupTitle(v.GroupId), v.Target.Url, v.Ok ? $"{v.BestSeconds:0.0} с" : Texts.Problem(v));
}

/// <summary>Строка таблицы подбора.</summary>
public sealed class ScoreRow(StrategyScore s) : Observable
{
    bool _best;
    public StrategyScore Score { get; } = s;
    public string Id => Score.Strategy.Id;
    public string Title => Score.Strategy.Title;
    public string Groups => $"{Score.GroupsPassed}/{Score.GroupsTotal}";
    public string Share => $"{Score.TargetShare * 100:0} %";
    public string Avg => Score.Verdicts.Any(v => v.Ok) ? $"{Score.AvgSeconds:0.00} с" : "—";
    public string Error => Score.Error is null ? "" : Score.Error.Length > 80 ? Score.Error[..80] + "…" : Score.Error;
    public string ErrorFull => Score.Error ?? "";
    public bool Usable => Score.Error is null && Score.GroupsPassed > 0;
    public bool IsBest { get => _best; set { if (Set(ref _best, value)) Raise(nameof(Mark)); } }
    public string Mark => IsBest ? "★" : "";
    /// <summary>Найдена глубоким поиском, а не из готового набора.</summary>
    public bool Found => Score.Strategy.Source == "search";
}

/// <summary>Строка сравнения DNS на «Диагностике».</summary>
public sealed record DnsRow(string Host, string System, string Doh, bool Spoofed)
{
    public string Mark => Spoofed ? "подмена" : "";

    public static DnsRow From(DnsComparison c) => new(
        c.Host,
        c.System.Length > 0 ? Ips(c.System) : "имени нет",
        c.Doh.Length > 0 ? Ips(c.Doh) : "нет ответа",
        c.Spoofed);

    static string Ips(IPAddress[] a) => string.Join(", ", a.Take(2)) + (a.Length > 2 ? " …" : "");
}

public static class Texts
{
    public static string Svc(SvcState s) => s switch
    {
        SvcState.NotInstalled => "не установлена",
        SvcState.Stopped => "остановлена",
        SvcState.StartPending => "запускается",
        SvcState.StopPending => "останавливается",
        SvcState.Running => "работает",
        _ => "неизвестно",
    };

    public static string Outcome(ProbeOutcome o) => o switch
    {
        ProbeOutcome.Timeout => "нет ответа",
        ProbeOutcome.ConnectFailed => "нет соединения",
        ProbeOutcome.TlsFailed => "защищённое соединение не устанавливается",
        ProbeOutcome.Reset => "соединение сброшено",
        ProbeOutcome.Truncated => "загрузка обрывается",
        ProbeOutcome.BadStatus => "ошибка на сайте",
        ProbeOutcome.ForeignRedirect => "заглушка провайдера",
        ProbeOutcome.Ok => "открывается",
        _ => "ошибка",
    };

    public static string Problem(TargetVerdict v) =>
        v.Probes.FirstOrDefault(p => !p.Ok) is { } p ? Outcome(p.Outcome) : "не открывается";

    /// <summary>Исключение → текст для человека.</summary>
    public static string Friendly(Exception e) => e switch
    {
        System.Net.Http.HttpRequestException => "Не удалось скачать ядро. Проверьте интернет и повторите (GitHub тоже бывает заблокирован — помогает повтор).",
        SecurityException s => s.Message,
        ConflictException c => $"Мешают другие программы обхода: {string.Join(", ", c.Conflicts.Select(x => x.Name).Distinct())}. Закройте их и повторите.",
        UnauthorizedAccessException => "Нет доступа к файлам программы. Возможно, их держит антивирус. " + e.Message,
        System.ComponentModel.Win32Exception w => "Не удалось запустить программу обхода (антивирус?): " + w.Message,
        AggregateException { InnerException: { } inner } => Friendly(inner),
        _ => e.Message,
    };
}
