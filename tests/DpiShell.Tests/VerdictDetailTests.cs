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
        Assert.Equal("важный", r.Note);
        Assert.True(r.Alert && r.Required);
        Assert.Equal($"{6.2:0.0} с", r.Time);

        var optional = CheckRow.From(V("youtube", "https://yt3.ggpht.com/", false, (SslProtocols.Tls13, ProbeOutcome.Reset, "", 0.1)), Names);
        Assert.Equal(("", false), (optional.Note, optional.Alert));   // метка только у важных
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
    public void StrategyChoice_Describe_OnlyNewestRun()
    {
        var names = new GroupNames([new("youtube", "YouTube", []), new("discord", "Discord", []), new("control", "Обычные сайты", [], Control: true)]);
        var last = new StrategyStats.LastRun("2026-10-17_14-30-00.json", [
            new("youtube", "https://www.youtube.com/", true, true, false, 0.4, [new("Tls13", "Ok", "301 → /?gl=RU → 200, 512 КБ", 0.4), new("Tls12", "Ok", "301 → /?gl=RU → 200, 512 КБ", 0.5)]),
            new("discord", "https://discord.com/", false, true, false, 6, [new("Tls13", "Reset", "", 6)]),
            new("user", "свой адрес №1", true, true, false, 0.3, []),
            new("user", "свой адрес №2", false, true, false, 6, [new("Tls13", "Truncated", "200, получено 16384 из 90000", 6)]),
            new("control", "https://vk.com/", true, true, true, 0.2, []),
        ]);
        // История (5 прогонов, ломала обычные сайты в одном из старых) не видна: только последний прогон.
        var st = new StrategyStats.Stat(0.9, 5, 3, 0.8, 1, 2, 0.5, 1, [new("youtube", 5, 5), new("discord", 5, 4)], last,
            new("2026-10-17_14-30-00.json", 1, 2, false), 0);
        var (stats, warning) = StrategyChoice.Describe(st, names);
        Assert.Equal("YouTube ✓ · Discord ✗ · свои 1 из 2 · проверено 17.10 в 14:30", stats);
        Assert.Equal("", warning);

        // Ломает обычные сайты в последнем прогоне — предупреждение.
        Assert.Equal("ломает обычные сайты", StrategyChoice.Describe(st with { Newest = st.Newest! with { ControlBroken = true } }, names).Warning);

        var details = StrategyChoice.LastDetails(st, names);
        Assert.Equal(["https://discord.com/", "свой адрес №2"], details.Take(2).Select(d => d.Url));   // провалы обязательных — первыми
        Assert.Equal("TLS 1.3: загрузка обрывается, 200, получено 16384 из 90000", details[1].Detail);

        var row = new StrategyChoice("a", "A", stats, false, warning, details);
        Assert.True(!row.HasWarning && row.HasDetails);

        // Самый свежий прогон — старого формата (без адресов): только итог, без адресов прошлого.
        var oldNewest = st with { Newest = new("2026-10-18_09-05-00.json", 1, 2, false) };
        Assert.Equal(("открыла 1 из 2 групп · проверено 18.10 в 09:05", ""), StrategyChoice.Describe(oldNewest, names));
        Assert.Equal(("открыла всё · проверено 18.10 в 09:05", ""),
            StrategyChoice.Describe(new StrategyStats.Stat(0.8, 5, 4, Newest: new("2026-10-18_09-05-00.json", 2, 2, false)), names));
        // Старый помощник (без Newest и адресов) — «ещё не проверялась» вместо истории.
        Assert.Equal(("ещё не проверялась", ""), StrategyChoice.Describe(new StrategyStats.Stat(0.8, 5, 4), names));
        Assert.Empty(StrategyChoice.LastDetails(new StrategyStats.Stat(0.8, 5, 4), names));
    }

    [Fact]
    public void StrategyParams_RolesAsInCatalog_CommandLineForThisPc()
    {
        using var t = new TempDir();
        var p = new AppPaths(t.Sub("root"), Repo.Data);
        var s = new Strategy("alt", "general (ALT)", "catalog", new()
        {
            ["general"] = "--dpi-desync=fake --dpi-desync-fake-tls={fake}/tls_clienthello_www_google_com.bin",
            ["google"] = "--dpi-desync=fake",
            ["quic"] = "--dpi-desync=fake --dpi-desync-repeats=6",
        });
        var text = StrategyParams.Full(s, p, BuildOptions.Default);
        var lines = text.Split('\n');
        // Роли — в порядке командной строки, плейсхолдер как в каталоге.
        Assert.Equal(["Параметры по ролям:", "quic: --dpi-desync=fake --dpi-desync-repeats=6", "google: --dpi-desync=fake",
            "general: --dpi-desync=fake --dpi-desync-fake-tls={fake}/tls_clienthello_www_google_com.bin", ""], lines[..5]);
        Assert.StartsWith("Командная строка на этом ПК (IP-список: выкл, игровой режим: выкл; пути — от " + p.Root, lines[5]);
        // Командная строка — та же, что у WinwsArgs.Build, пути — от папки программы.
        Assert.Equal("winws.exe " + WinwsArgs.Join(WinwsArgs.Build(s, p)).Replace(p.Root + Path.DirectorySeparatorChar, ""), lines[6]);
        Assert.Contains("--dpi-desync-fake-tls=" + Path.Combine("fake", "tls_clienthello_www_google_com.bin"), lines[6]);
        Assert.DoesNotContain(p.Root, lines[6]);
        Assert.Contains("IP-список: только из списков, игровой режим: вкл", StrategyParams.Full(s, p, new(IpsetMode.List, GameMode.TcpUdp)));

        // Пункт меню стратегии: параметры есть — в подсказке и «Скопировать параметры».
        var row = new StrategyChoice("alt", "general (ALT)", "ещё не проверялась", false, parameters: text);
        Assert.True(row.HasParams);
        Assert.False(new StrategyChoice("x", "X", "", false).HasParams);
    }

    [Fact]
    public void StrategyChoice_Regression_AfterStreak_NotForOldRecords()
    {
        var yt = new TargetGroup("youtube", "YouTube", [new("https://www.youtube.com/", 20480)]);
        var dc = new TargetGroup("discord", "Discord", [new("https://discord.com/", 20480)]);
        var names = new GroupNames([yt, dc]);
        StrategyScore Run(string id, params string[] failed)
        {
            var verdicts = new[] { yt, dc }.SelectMany(g => g.Targets.Select(t =>
                new TargetVerdict(g.Id, t, !failed.Contains(t.Url), 0.3, [new ProbeResult(t, SslProtocols.Tls13, failed.Contains(t.Url) ? ProbeOutcome.Reset : ProbeOutcome.Ok, 0.3, 0, "")]))).ToList();
            return DpiShell.Core.Selector.Score(new Strategy(id, "general (" + id + ")", "test", new() { ["general"] = "--dpi-desync=fake" }), [yt, dc], verdicts);
        }
        var report = new SelectionReport([], [yt, dc], [Run("alt", "https://discord.com/"), Run("alt2"), Run("old", "https://discord.com/")], false);
        var before = new Dictionary<string, StrategyStats.Stat>
        {
            ["alt"] = new(1, 5, 5, Streak: 5),
            ["alt2"] = new(1, 5, 5, Streak: 7),   // прошла и сейчас — подсказки нет
            ["old"] = new(1, 9, 9),               // старые записи без групп: серии нет
        };
        Assert.Equal(L.Plural("select.regression", 5, new { name = "general (alt)", failed = L.T("select.failed.not_opening", new { groups = "Discord" }) }),
            StrategyChoice.Regression(before, report, "alt2", names));
        Assert.Contains("последние 3 раза", StrategyChoice.Regression(new Dictionary<string, StrategyStats.Stat> { ["alt"] = new(1, 3, 3, Streak: 3) }, report, null, names));
        Assert.Equal("", StrategyChoice.Regression(new Dictionary<string, StrategyStats.Stat> { ["alt"] = new(1, 2, 2, Streak: 2) }, report, null, names));
        Assert.Equal("", StrategyChoice.Regression(before.Where(kv => kv.Key != "alt").ToDictionary(), report, null, names));
        Assert.Equal("", StrategyChoice.Regression(null, report, null, names));
    }
}
