using System.Security.Authentication;
using System.Text.Json;
using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Router;

namespace DpiShell.Tests;

/// <summary>«Подробнее» у автоматических проверок: строки по адресам для таблиц подбора, «Проверить сейчас» и роутера.</summary>
public sealed class VerdictDetailTests
{
    static readonly GroupNames Names = new([
        new("youtube", "YouTube", []),
        new("control", "Обычные сайты", [], Control: true),
    ]);

    static TargetVerdict V(string group, string url, bool required, params (SslProtocols Tls, ProbeOutcome Outcome, string Detail, double S)[] probes)
    {
        var t = new Target(url, Required: required);
        var list = probes.Select(p => new ProbeResult(t, p.Tls, p.Outcome, p.S, 0, p.Detail)).ToList();
        bool ok = list.All(p => p.Ok);
        return new(group, t, ok, ok ? list.Max(p => p.Seconds) : 0, list);
    }

    [Fact]
    public void CheckRow_Ok_SameDetailForBothTls_Merged()
    {
        var r = CheckRow.From(V("youtube", "https://youtube.com/", true,
            (SslProtocols.Tls13, ProbeOutcome.Ok, "301 → www.youtube.com/ → 200, 512 КБ", 0.4),
            (SslProtocols.Tls12, ProbeOutcome.Ok, "301 → www.youtube.com/ → 200, 512 КБ", 0.6)), Names);
        Assert.True(r.Ok);
        Assert.Equal("YouTube", r.Group);
        Assert.Equal("TLS 1.3 и TLS 1.2: 301 → www.youtube.com/ → 200, 512 КБ", r.Detail);
        Assert.Equal($"{0.6:0.0} с", r.Time);   // дробная часть — как в системе
        Assert.False(r.HasNote);
        Assert.False(r.Alert);
    }

    [Fact]
    public void CheckRow_Failed_OutcomeInWords_PerTls_AndMarks()
    {
        var bad = V("youtube", "https://www.youtube.com/", true,
            (SslProtocols.Tls13, ProbeOutcome.Truncated, "200, получено 16384 из 500000", 6.2),
            (SslProtocols.Tls12, ProbeOutcome.Ok, "HTTP 200, 488 КБ", 0.5));
        var r = CheckRow.From(bad, Names);
        Assert.False(r.Ok);
        Assert.Equal("TLS 1.3: загрузка обрывается, 200, получено 16384 из 500000 · TLS 1.2: HTTP 200, 488 КБ", r.Detail);
        Assert.Equal("обязательный", r.Note);
        Assert.True(r.Alert);
        Assert.Equal($"{6.2:0.0} с", r.Time);

        var optional = CheckRow.From(V("youtube", "https://yt3.ggpht.com/", false, (SslProtocols.Tls13, ProbeOutcome.Reset, "", 0.1)), Names);
        Assert.Equal(("необязательный", false), (optional.Note, optional.Alert));
        Assert.Equal("TLS 1.3: соединение сброшено", optional.Detail);

        var control = CheckRow.From(V("control", "https://vk.com/", true, (SslProtocols.Tls13, ProbeOutcome.Blocked, "451: недоступно по требованию закона", 0.2)), Names);
        Assert.Equal(("Обычные сайты", "ломает обычный сайт", true), (control.Group, control.Note, control.Alert));
        Assert.Contains("блокировка (451)", control.Detail);
    }

    [Fact]
    public void ScoreRow_Details_AllTargets_AlertsFirst_MissedVisibleInRow()
    {
        var ok = V("youtube", "https://i.ytimg.com/x.jpg", true, (SslProtocols.Tls13, ProbeOutcome.Ok, "HTTP 200, 40 КБ", 0.3));
        var optional = V("youtube", "https://yt3.ggpht.com/", false, (SslProtocols.Tls13, ProbeOutcome.Timeout, "таймаут", 9));
        var bad = V("youtube", "https://www.youtube.com/", true, (SslProtocols.Tls13, ProbeOutcome.Reset, "", 0.1));
        var row = new ScoreRow(new StrategyScore(new Strategy("a", "A", "test", []), [ok, optional, bad], 0, 1), Names);

        Assert.True(row.HasDetails);
        Assert.Equal(["https://www.youtube.com/", "https://i.ytimg.com/x.jpg", "https://yt3.ggpht.com/"], row.Details.Select(d => d.Url));
        Assert.Equal("не открылось: YouTube", row.Error);   // без раскрытия
        Assert.False(row.Expanded);
        row.Expanded = true;
        Assert.True(row.Expanded);

        // Ошибка запуска важнее; адресов нет — и раскрывать нечего.
        var crashed = new ScoreRow(new StrategyScore(new Strategy("b", "B", "test", []), [], 0, 1, "winws завершился"));
        Assert.Equal("winws завершился", crashed.Error);
        Assert.False(crashed.HasDetails);
    }

    static RouterSelectProgress Parse(string rows)
    {
        var json = (JsonSerializerOptions)typeof(RouterClient).GetField("Json", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        return JsonSerializer.Deserialize<RouterSelectProgress>(
            $$"""{ "running": false, "index": 1, "total": 1, "message": "", "rows": {{rows}}, "finished_at": null, "best": null }""", json)!;
    }

    [Fact]
    public void RouterRow_Verdicts_Optional()
    {
        var p = Parse("""
            [ { "id": "alt", "group_share": 1.0, "target_share": 0.5, "avg_s": 0.7, "error": null,
                "verdicts": [ { "group": "youtube", "url": "https://www.youtube.com/", "ok": true, "detail": "HTTP 200, 512 КБ", "seconds": 0.42 },
                              { "group": "control", "url": "https://vk.com/", "ok": false, "detail": "соединение сброшено" } ] },
              { "id": "old", "group_share": 1.0, "target_share": 1.0, "avg_s": 0.7, "error": null } ]
            """);
        var withDetails = new RouterScoreRow(p.Rows![0], "ALT", Names);
        Assert.True(withDetails.HasDetails);
        Assert.Equal(["https://vk.com/", "https://www.youtube.com/"], withDetails.Details.Select(d => d.Url));   // сломанный обычный сайт — первым
        Assert.Equal(("ломает обычный сайт", true, ""), (withDetails.Details[0].Note, withDetails.Details[0].Alert, withDetails.Details[0].Time));
        Assert.Equal(("YouTube", $"{0.4:0.0} с", "HTTP 200, 512 КБ"), (withDetails.Details[1].Group, withDetails.Details[1].Time, withDetails.Details[1].Detail));

        // Роутер без разбивки (нынешний договор) — только подсказка.
        var old = new RouterScoreRow(p.Rows[1], "old", Names);
        Assert.False(old.HasDetails);
        Assert.True(old.NoDetails);
        Assert.Contains("после обновления роутера", old.NoDetailsHint);

        // Кандидат глубокого поиска: разбивки нет по договору — ни стрелки, ни подсказки.
        var cand = new RouterScoreRow(p.Rows[1] with { Candidate = true }, "d-1", Names);
        Assert.False(cand.CanExpand);
        Assert.True(old.CanExpand && withDetails.CanExpand);

        // Опрос хода: та же строка с новым списком — не пересоздаём (раскрытие не сбрасывается).
        var again = Parse("""[ { "id": "alt", "group_share": 1.0, "target_share": 0.5, "avg_s": 0.7, "error": null, "verdicts": [ {"group":"youtube","url":"u","ok":true}, {"group":"youtube","url":"v","ok":true} ] } ]""");
        Assert.True(withDetails.Same(again.Rows![0]));
        Assert.False(withDetails.Same(again.Rows[0] with { AvgS = 0.9 }));
    }

    // ---------- список стратегий: подпись по группам и адреса последнего подбора ----------

    [Fact]
    public void StrategyChoice_Describe_ByGroups_UserShare_ControlAsWarning()
    {
        var names = new GroupNames([new("youtube", "YouTube", []), new("discord", "Discord", []), new("control", "Обычные сайты", [], Control: true)]);
        var st = new StrategyStats.Stat(0.9, 5, 3, 0.8, 7, 9, 0.5, 1,
            [new("youtube", 5, 5), new("discord", 5, 4)],
            new("2026-10-17_10-00-00.json", [
                new("youtube", "https://www.youtube.com/", true, true, false, 0.4, [new("Tls13", "Ok", "301 → /?gl=RU → 200, 512 КБ", 0.4), new("Tls12", "Ok", "301 → /?gl=RU → 200, 512 КБ", 0.5)]),
                new("user", "свой адрес №2", false, true, false, 6, [new("Tls13", "Truncated", "200, получено 16384 из 90000", 6)]),
            ]));
        var (stats, warning) = StrategyChoice.Describe(st, names);
        Assert.Equal("YouTube 5/5 · Discord 4/5 · свои сайты 7 из 9", stats);
        Assert.Equal("ломала обычные сайты: 1 из 5 подборов", warning);

        var details = StrategyChoice.LastDetails(st, names);
        Assert.Equal(["свой адрес №2", "https://www.youtube.com/"], details.Select(d => d.Url));   // провал своего адреса — первым
        Assert.Equal(("Свои адреса", "обязательный"), (details[0].Group, details[0].Note));
        Assert.Equal("TLS 1.3: загрузка обрывается, 200, получено 16384 из 90000", details[0].Detail);
        Assert.Equal("TLS 1.3 и TLS 1.2: 301 → /?gl=RU → 200, 512 КБ", details[1].Detail);

        var row = new StrategyChoice("a", "A", stats, false, warning, details);
        Assert.True(row.HasWarning && row.HasDetails && !row.Expanded);

        // Старая статистика (без групп) — общие числа, как раньше; раскрывать нечего.
        var old = new StrategyStats.Stat(0.8, 5, 4);
        Assert.Equal(("открыла всё в 4 из 5 подборов (80 %)", ""), StrategyChoice.Describe(old, names));
        Assert.Empty(StrategyChoice.LastDetails(old, names));
    }
}
